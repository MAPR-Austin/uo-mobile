// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using ClassicUO.Configuration;

namespace ClassicUO.Touch
{
    /// <summary>
    /// One on-screen button. Positions are the button centre, normalised to the safe area (0..1).
    /// Landscape (X/Y) is the layout; portrait follows it (<see cref="ActionLayout.PortraitOf"/>)
    /// unless the button was dragged while in portrait, which pins it there (PX/PY, PortraitPinned).
    /// </summary>
    internal sealed class ActionButtonDef
    {
        /// <summary>Button text. "|" is a line break: ClassicUO's JSON loader doubles backslashes, so "\n" would not survive a save.</summary>
        [JsonPropertyName("label")] public string Label { get; set; } = "";

        /// <summary>Label as drawn (also repairs layouts saved with a mangled "\n").</summary>
        [JsonIgnore]
        public string DisplayLabel => (Label ?? "").Replace("\\n", "\n").Replace('|', '\n');
        /// <summary>An action id understood by <see cref="MobileActions"/> ("attack_nearest", "spell_lt:EnergyBolt", "macro:My Macro", ...).</summary>
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("px")] public float? PX { get; set; }
        [JsonPropertyName("py")] public float? PY { get; set; }
        /// <summary>Placed by hand in portrait: PX/PY hold; otherwise portrait follows landscape.</summary>
        [JsonPropertyName("ppin")] public bool PortraitPinned { get; set; }
        /// <summary>Diameter as a fraction of the screen's shorter side.</summary>
        [JsonPropertyName("size")] public float Size { get; set; } = 0.14f;
    }

    internal sealed class ActionLayout
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("joy_x")] public float JoystickX { get; set; } = 0.14f;
        [JsonPropertyName("joy_y")] public float JoystickY { get; set; } = 0.74f;
        [JsonPropertyName("joy_px")] public float? PJoystickX { get; set; }
        [JsonPropertyName("joy_py")] public float? PJoystickY { get; set; }
        [JsonPropertyName("joy_ppin")] public bool JoystickPortraitPinned { get; set; }
        [JsonPropertyName("joy_size")] public float JoystickSize { get; set; } = 0.34f;
        [JsonPropertyName("buttons")] public List<ActionButtonDef> Buttons { get; set; } = new List<ActionButtonDef>();

        // the portrait spots worked out from landscape, for one screen shape and layout revision
        private Dictionary<ActionButtonDef, (float X, float Y)> _derived;
        private float _derivedAspect;
        private int _derivedRevision = -1;

        /// <summary>
        /// Where <paramref name="b"/> goes in portrait. A spot pinned by dragging it in portrait
        /// stays; otherwise the landscape layout is refitted to the tall screen: the top row stays a
        /// row across the top, the cluster on the right keeps its shape in the lower right (squeezed
        /// only as much as the narrower screen needs), and buttons on the left go above the
        /// joystick. <paramref name="aspect"/> is long side / short side.
        /// </summary>
        public (float X, float Y) PortraitOf(ActionButtonDef b, float aspect, int revision)
        {
            if (b.PortraitPinned && b.PX.HasValue && b.PY.HasValue)
            {
                return (b.PX.Value, b.PY.Value);
            }

            if (_derived == null || _derivedRevision != revision || Math.Abs(_derivedAspect - aspect) > 0.001f)
            {
                Derive(aspect, revision);
            }

            return _derived.TryGetValue(b, out (float X, float Y) p) ? p : Portrait(b.X, b.Y, aspect);
        }

        /// <summary>The joystick in portrait: pinned by hand, else bottom-left (bottom-right for a right-handed landscape joystick).</summary>
        public (float X, float Y) PortraitJoystick =>
            JoystickPortraitPinned && PJoystickX.HasValue && PJoystickY.HasValue
                ? (PJoystickX.Value, PJoystickY.Value)
                : (JoystickX < 0.5f ? 0.24f : 0.76f, 0.86f);

        private void Derive(float aspect, int revision)
        {
            _derived = new Dictionary<ActionButtonDef, (float X, float Y)>();
            _derivedAspect = aspect;
            _derivedRevision = revision;

            List<ActionButtonDef> top = new List<ActionButtonDef>(), right = new List<ActionButtonDef>(), left = new List<ActionButtonDef>();

            foreach (ActionButtonDef b in Buttons)
            {
                if (b.PortraitPinned && b.PX.HasValue && b.PY.HasValue)
                {
                    continue;
                }

                (b.Y < 0.16f ? top : b.X >= 0.5f ? right : left).Add(b);
            }

            bool joyLeft = JoystickX < 0.5f;

            // the top row: across the top, kept to its side, squeezed to fit the width
            Fit(top, aspect, 0.04f, 0.96f, float.NaN, float.NaN, null);
            // the action cluster: the lower part of the screen, beside the joystick
            Fit(right, aspect, joyLeft ? 0.40f : 0.03f, joyLeft ? 0.97f : 0.60f, 0.50f, 0.97f, joyLeft);
            // the other side: above the joystick
            Fit(left, aspect, joyLeft ? 0.03f : 0.55f, joyLeft ? 0.45f : 0.97f, 0.40f, 0.74f, !joyLeft);
        }

        /// <summary>
        /// Places a group of buttons in a box of the portrait screen, keeping their arrangement in
        /// physical units (short-side lengths): squeezed across and down only as much as needed,
        /// anchored at the box's right (or left) side and its bottom - or each button keeps its
        /// distance from the top (bottom NaN). anchorRight null: the side the group was on.
        /// </summary>
        private void Fit(List<ActionButtonDef> group, float aspect, float boxLeft, float boxRight, float boxTop, float bottom, bool? anchorRight)
        {
            if (group.Count == 0)
            {
                return;
            }

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;

            foreach (ActionButtonDef b in group)
            {
                float ux = b.X * aspect, r = b.Size * 0.5f;
                minX = Math.Min(minX, ux - r);
                maxX = Math.Max(maxX, ux + r);
                minY = Math.Min(minY, b.Y - r);
                maxY = Math.Max(maxY, b.Y + r);
            }

            bool toRight = anchorRight ?? (minX + maxX) * 0.5f >= aspect * 0.5f;
            float sx = Math.Min(1f, (boxRight - boxLeft) / Math.Max(0.01f, maxX - minX));
            float sy = float.IsNaN(bottom) ? 1f : Math.Min(1f, (bottom - boxTop) * aspect / Math.Max(0.01f, maxY - minY));

            foreach (ActionButtonDef b in group)
            {
                float ux = b.X * aspect;
                float px = toRight ? boxRight - (maxX - ux) * sx : boxLeft + (ux - minX) * sx;
                float py = float.IsNaN(bottom) ? b.Y / aspect : (bottom * aspect - (maxY - b.Y) * sy) / aspect;
                _derived[b] = (Math.Clamp(px, 0.04f, 0.96f), Math.Clamp(py, 0.02f, 0.98f));
            }
        }

        /// <summary>A free landscape spot for a new button: the right-hand side, clear of the others.</summary>
        public (float X, float Y) FreeSpot(float size, float aspect)
        {
            for (float y = 0.24f; y <= 0.93f; y += 0.12f)
            {
                for (float x = 0.93f; x >= 0.45f; x -= 0.07f)
                {
                    bool clear = true;

                    foreach (ActionButtonDef b in Buttons)
                    {
                        float dx = (b.X - x) * aspect, dy = b.Y - y, need = (b.Size + size) * 0.5f + 0.01f;

                        if (dx * dx + dy * dy < need * need)
                        {
                            clear = false;

                            break;
                        }
                    }

                    if (clear)
                    {
                        return (x, y);
                    }
                }
            }

            return (0.5f, 0.3f);
        }

        /// <summary>
        /// Portrait position for a landscape one that has none: keep the same physical distance from
        /// the nearest corner, staying on the same half of the screen; the middle band stays in the
        /// middle. <paramref name="aspect"/> is long side / short side of the screen.
        /// </summary>
        public static (float X, float Y) Portrait(float x, float y, float aspect)
        {
            float px = Math.Abs(x - 0.5f) < 0.15f ? x
                : x > 0.5f ? Math.Max(0.55f, 1f - (1f - x) * aspect)
                : Math.Min(0.45f, x * aspect);
            float py = y > 0.5f ? 1f - (1f - y) / aspect : y / aspect;

            return (Math.Clamp(px, 0.08f, 0.92f), Math.Clamp(py, 0.03f, 0.97f));
        }
    }

    internal sealed class ActionLayoutSet
    {
        [JsonPropertyName("active")] public int Active { get; set; }
        /// <summary>2: portrait spots are pinned only when placed by hand in portrait.</summary>
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("layouts")] public List<ActionLayout> Layouts { get; set; } = new List<ActionLayout>();

        [JsonIgnore]
        public ActionLayout Current =>
            Layouts.Count == 0 ? null : Layouts[((Active % Layouts.Count) + Layouts.Count) % Layouts.Count];

        public void Next()
        {
            if (Layouts.Count > 0)
            {
                Active = (Active + 1) % Layouts.Count;
            }
        }

        private static string FilePath =>
            Path.Combine(ProfileManager.ProfilePath ?? CUOEnviroment.ExecutablePath, "mobile_layouts.json");

        public static ActionLayoutSet Load()
        {
            ActionLayoutSet set = null;

            if (File.Exists(FilePath))
            {
                set = ConfigurationResolver.Load(FilePath, MobileJsonContext.Default.ActionLayoutSet, escapeBackslashes: false);
            }

            // Valid JSON can still hold nulls ("layouts": [null], "buttons": null); drop them
            // rather than crash on every entry to the world.
            if (set?.Layouts != null)
            {
                set.Layouts.RemoveAll(l => l == null);

                foreach (ActionLayout l in set.Layouts)
                {
                    l.Buttons ??= new List<ActionButtonDef>();
                    l.Buttons.RemoveAll(b => b == null);
                }
            }

            if (set?.Layouts == null || set.Layouts.Count == 0)
            {
                set = CreateDefault();
            }
            else if (set.Version < 2)
            {
                PinHandPlacedPortraitSpots(set);
            }

            return set;
        }

        /// <summary>
        /// Layouts saved before version 2 kept a portrait spot for every button: the default layout's,
        /// the screen centre for a button added with "+", or wherever the player dragged it in
        /// portrait. Only that last kind is pinned; the others now follow landscape.
        /// </summary>
        private static void PinHandPlacedPortraitSpots(ActionLayoutSet set)
        {
            ActionLayoutSet defaults = CreateDefault();

            static bool Near(float a, float b) => Math.Abs(a - b) < 0.006f;

            for (int i = 0; i < set.Layouts.Count; i++)
            {
                ActionLayout layout = set.Layouts[i];
                ActionLayout d = defaults.Layouts.Find(x => x.Name == layout.Name) ?? defaults.Layouts[i % defaults.Layouts.Count];

                layout.JoystickPortraitPinned = layout.PJoystickX.HasValue && layout.PJoystickY.HasValue &&
                                                !(Near(layout.PJoystickX.Value, 0.24f) && Near(layout.PJoystickY.Value, 0.86f));

                List<ActionButtonDef> unused = new List<ActionButtonDef>(d.Buttons);

                foreach (ActionButtonDef b in layout.Buttons)
                {
                    ActionButtonDef match = unused.Find(x => x.Action == b.Action);

                    if (match != null)
                    {
                        unused.Remove(match);
                    }

                    bool hasSpot = b.PX.HasValue && b.PY.HasValue;
                    bool isDefault = hasSpot && match != null && Near(b.PX.Value, match.PX ?? -1f) && Near(b.PY.Value, match.PY ?? -1f);
                    bool isNew = hasSpot && Near(b.PX.Value, 0.5f) && Near(b.PY.Value, 0.5f);
                    b.PortraitPinned = hasSpot && !isDefault && !isNew;
                }
            }

            set.Version = 2;
        }

        public void Save()
        {
            ConfigurationResolver.Save(this, FilePath, MobileJsonContext.Default.ActionLayoutSet);
        }

        /// <summary>A button with landscape (x, y) and portrait (px, py) positions.</summary>
        private static ActionButtonDef B(string label, string action, float x, float y, float px, float py, float size = 0.14f) =>
            new ActionButtonDef { Label = label.Replace('\n', '|'), Action = action, X = x, Y = y, PX = px, PY = py, Size = size };

        private static ActionLayout L(string name) => new ActionLayout
        {
            Name = name,
            // portrait: bottom-left, under the world view
            PJoystickX = 0.24f,
            PJoystickY = 0.86f
        };

        /// <summary>
        /// Three starter layouts. Every layout carries the same system row (war/peace, chat, cancel
        /// target, edit, layout toggle) so the player can never lose the way back. Landscape puts the
        /// action cluster bottom-right; portrait stacks it under the world view on the right, with
        /// the joystick on the left.
        /// </summary>
        public static ActionLayoutSet CreateDefault()
        {
            List<ActionButtonDef> SystemRow() => new List<ActionButtonDef>
            {
                B("Layout", "layout_next", 0.95f, 0.07f, 0.90f, 0.035f, 0.10f),
                B("Edit", "edit_layout", 0.87f, 0.07f, 0.70f, 0.035f, 0.10f),
                B("Cancel", "cancel_target", 0.79f, 0.07f, 0.50f, 0.035f, 0.10f),
                B("Chat", "chat", 0.71f, 0.07f, 0.30f, 0.035f, 0.10f),
                B("War", "war_peace", 0.63f, 0.07f, 0.10f, 0.035f, 0.10f),
            };

            ActionLayout combat = L("Combat");
            combat.Buttons.AddRange(SystemRow());
            combat.Buttons.AddRange(new[]
            {
                B("Attack\nNearest", "attack_nearest", 0.90f, 0.80f, 0.82f, 0.90f, 0.20f),
                B("Next\nTarget", "target_next", 0.76f, 0.88f, 0.58f, 0.94f),
                B("Last\nTarget", "last_target", 0.78f, 0.70f, 0.82f, 0.77f),
                B("Self", "target_self", 0.90f, 0.58f, 0.58f, 0.72f),
                B("Attack\nLast", "attack_last", 0.64f, 0.88f, 0.58f, 0.83f),
                B("Health\nBar", "healthbar_target", 0.95f, 0.40f, 0.93f, 0.60f, 0.11f),
                B("Bandage", "bandage_self", 0.84f, 0.44f, 0.70f, 0.62f, 0.11f),
                B("Set\nTarget", "set_target", 0.65f, 0.70f, 0.82f, 0.67f, 0.12f),
            });

            ActionLayout mage = L("Mage");
            mage.Buttons.AddRange(SystemRow());
            mage.Buttons.AddRange(new[]
            {
                B("E-Bolt", "spell_lt:EnergyBolt", 0.90f, 0.82f, 0.82f, 0.90f, 0.16f),
                B("Explo", "spell_lt:Explosion", 0.76f, 0.88f, 0.58f, 0.94f),
                B("Flame", "spell_lt:FlameStrike", 0.78f, 0.70f, 0.58f, 0.83f),
                B("Magic\nArrow", "spell_lt:MagicArrow", 0.64f, 0.88f, 0.82f, 0.78f),
                B("Para", "spell_lt:Paralyze", 0.90f, 0.62f, 0.58f, 0.72f),
                B("Heal\nSelf", "spell_self:GreaterHeal", 0.95f, 0.44f, 0.93f, 0.66f, 0.11f),
                B("Cure\nSelf", "spell_self:Cure", 0.84f, 0.46f, 0.74f, 0.66f, 0.11f),
                B("Next\nTarget", "target_next", 0.66f, 0.72f, 0.93f, 0.59f, 0.11f),
                B("Last\nTarget", "last_target", 0.95f, 0.26f, 0.74f, 0.59f, 0.10f),
                B("Set\nTarget", "set_target", 0.66f, 0.55f, 0.55f, 0.62f, 0.11f),
            });

            ActionLayout utility = L("Utility");
            utility.Buttons.AddRange(SystemRow());
            utility.Buttons.AddRange(new[]
            {
                B("Pack", "open:Backpack", 0.90f, 0.82f, 0.82f, 0.90f, 0.14f),
                B("Paper\ndoll", "open:Paperdoll", 0.77f, 0.86f, 0.58f, 0.94f),
                B("Skills", "open:Skills", 0.90f, 0.66f, 0.82f, 0.77f),
                B("Journal", "open:Journal", 0.77f, 0.70f, 0.58f, 0.83f),
                B("Names", "all_names", 0.64f, 0.88f, 0.58f, 0.72f),
                B("Macros", "macro_editor", 0.95f, 0.46f, 0.93f, 0.66f, 0.11f),
                B("Status", "open:Status", 0.84f, 0.50f, 0.74f, 0.66f, 0.11f),
            });

            return new ActionLayoutSet { Active = 0, Version = 2, Layouts = { combat, mage, utility } };
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ActionLayoutSet))]
    [JsonSerializable(typeof(Dictionary<string, float>))] // GumpScale
    sealed partial class MobileJsonContext : JsonSerializerContext { }
}
