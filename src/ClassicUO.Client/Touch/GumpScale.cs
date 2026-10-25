// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Per-window zoom on phones: each kind of window (backpack, paperdoll, skills, a server gump...)
    /// keeps its own scale, set by pinching on it, independent of the world zoom.
    /// Drawing: <see cref="Game.Managers.UIManager.Draw"/> draws a scaled window in its own pass,
    /// scaled around its top-left (X, Y). Input: a finger on the window is mapped back into the
    /// window's unscaled layout (<see cref="ToLogical"/>), so ClassicUO's hit tests, clicks, drags
    /// and drops work unchanged.
    /// </summary>
    internal static class GumpScale
    {
        public const float MIN = 0.6f;
        public const float MAX = 2.0f;

        private static Dictionary<string, float> _scales;
        private static string _profilePath;
        private static string _loadedFrom; // the character's file; null before login (nothing is saved then)
        private static bool _dirty;
        private static readonly ConditionalWeakTable<Gump, string> _keys = new ConditionalWeakTable<Gump, string>();

        /// <summary>
        /// The window's scale; 1 when touch is off or the window can't be scaled. Not pinched: the phone's large
        /// menus pick it (build 55, <see cref="LargeUi.AutoScale"/>; 1 for kinds they leave alone).
        /// </summary>
        public static float Of(Gump g)
        {
            // In game only: the login screens' pointer is never mapped.
            if (!TouchInput.Enabled || g == null || !(Client.Game.Scene is Game.Scenes.GameScene) || !Scalable(g))
            {
                return 1f;
            }

            EnsureLoaded();

            return _scales.TryGetValue(Key(g), out float s) ? LargeUi.FitScale(g, s) : LargeUi.AutoScale(g);
        }

        public static void Set(Gump g, float scale)
        {
            if (g == null || !Scalable(g))
            {
                return;
            }

            EnsureLoaded();
            scale = Math.Clamp(scale, Math.Min(MIN, LargeUi.AutoScale(g)), MAX); // (build 56: a wide menu fitted below MIN can be pinched from there)

            if (Math.Abs(scale - LargeUi.AutoScale(g)) < 0.03f || scale < MIN && LargeUi.IsServerMenu(g))
            {
                // snap back to the window's own size (1, or the large menus' size): drawn as if never pinched. Build 57: a
                // server menu pinched below MIN (only a wide one fitted below it upright can be) isn't kept either - sideways
                // that tiny zoom stayed (FitScale only shrinks), so the menu opened unreadably small (build 56 audit)
                _scales.Remove(Key(g));
            }
            else
            {
                _scales[Key(g)] = scale;
            }

            _dirty = true;
        }

        /// <summary>
        /// Not: the HUD and the world view (they are the screen), momentary menus, world-anchored
        /// labels (name plates, the quest arrow: they reposition every frame), and health bars
        /// (they snap into groups by their unscaled size).
        /// </summary>
        public static bool Scalable(Gump g) =>
            !(g is TouchHudGump || g is WorldViewportGump || g is PopupMenuGump ||
              g is NameOverheadGump || g is QuestArrowGump || g is BaseHealthBarGump ||
              g is SurvivalMapGump || g is AlertBanner); // (the map fills the screen; the banner is drawn, never touched)

        /// <summary>A physical point (UI units) in the window's own unscaled layout.</summary>
        public static Point ToLogical(Gump g, Point p, float scale) =>
            scale == 1f ? p : new Point(g.X + (int)MathF.Round((p.X - g.X) / scale), g.Y + (int)MathF.Round((p.Y - g.Y) / scale));

        /// <summary>Remembered per kind of window: server gumps by their type id, containers by their art.</summary>
        private static string Key(Gump g) =>
            _keys.GetValue(g, x => x.ServerSerial != 0 && x.GetType() == typeof(Gump) ? "server:" + x.ServerSerial
                : x is ContainerGump c ? "ContainerGump:" + c.Graphic
                : x.GetType().Name);


        /// <summary>
        /// Per character. Windows are drawn (and ask for their scale) on the login screen too, before
        /// there is a profile, so follow the profile path rather than loading once.
        /// </summary>
        private static void EnsureLoaded()
        {
            string profile = ProfileManager.ProfilePath;

            if (_scales != null && profile == _profilePath)
            {
                return; // (runs for every window every frame: no allocation on this path)
            }

            Save(); // the previous character's changes
            _profilePath = profile;
            string path = profile == null ? null : Path.Combine(profile, "mobile_gumpscale.json");
            _loadedFrom = path;
            _scales = null;

            try
            {
                if (path != null && File.Exists(path))
                {
                    _scales = ConfigurationResolver.Load(path, MobileJsonContext.Default.DictionaryStringSingle, escapeBackslashes: false);
                }
            }
            catch (Exception e)
            {
                // This runs inside the UI draw; an unreadable file must not break every frame.
                Utility.Logging.Log.Error($"[UOMobile] window zoom file unreadable: {e.Message}");
            }

            _scales ??= new Dictionary<string, float>();

            _dirty = false;
            bool migrated = false;

            if (!_scales.ContainsKey(LARGE_UI_MARK))
            {
                // Build 55: server menus open at the large menus' size; a zoom pinched before (relative to the old
                // small size) is forgotten once, so nobody stays on a smaller menu than everyone else.
                foreach (string key in new List<string>(_scales.Keys))
                {
                    if (key.StartsWith("server:", StringComparison.Ordinal))
                    {
                        _scales.Remove(key);
                    }
                }

                _scales[LARGE_UI_MARK] = 1f;
                migrated = true;
            }

            foreach (string key in new List<string>(_scales.Keys))
            {
                float v = _scales[key];

                if (key.StartsWith("server:", StringComparison.Ordinal) && float.IsFinite(v) && v < MIN)
                {
                    _scales.Remove(key); // build 57: a server menu's zoom below MIN kept by build 56 (see Set): fitted afresh
                    _dirty = true;

                    continue;
                }

                _scales[key] = float.IsFinite(v) ? Math.Clamp(v, LargeUi.MENU_MIN, MAX) : 1f; // a hand-edited 0 would divide by zero
            }

            _dirty |= migrated; // (saved at the next save point)
        }

        /// <summary>Marks a zoom file written since build 55's large menus (never a window's key).</summary>
        private const string LARGE_UI_MARK = "_build55";

        public static void Save()
        {
            if (_dirty && _scales != null && _loadedFrom != null)
            {
                ConfigurationResolver.Save(_scales, _loadedFrom, MobileJsonContext.Default.DictionaryStringSingle);
                _dirty = false;
            }
        }

        /// <summary>Leaving the world: save, and read the next character's file when it's needed.</summary>
        public static void Unload()
        {
            Save();
            _scales = null;
            _profilePath = null;
            _loadedFrom = null;
        }
    }
}
