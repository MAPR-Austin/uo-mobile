// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// What a phone player gets out of the box (once per character; everything stays changeable in
    /// UO's options and on the windows themselves): doors open as you walk into them, and a counter
    /// strip near the top left shows bandages, the main potions and the eight reagents - red under 5,
    /// a double tap uses one; it is locked (a drag moves the strip; a double tap on its frame unlocks
    /// it to add counters by dropping items on it). A long press hides the strip; a "Counters" button
    /// (action "counters[:on|off]") shows it again. No explosion potion: a stray double tap would arm
    /// one in the pack.
    /// </summary>
    internal static class PhoneDefaults
    {
        /// <summary>Raise when a new default should reach characters set up before it.</summary>
        private const int Version = 1;

        private static readonly string[] Counters =
        {
            "bandages", "heal", "cure", "refresh", "strength", "agility",
            "blackpearl", "bloodmoss", "garlic", "ginseng", "mandrake", "nightshade", "ash", "silk"
        };

        private const int Cell = 36, Columns = 7;

        /// <summary>TouchInput.Update, in the world: set the character up once.</summary>
        public static void Apply(World world)
        {
            Profile p = ProfileManager.CurrentProfile;

            // in the game scene only: the world is "in game" a moment earlier, still in the login scene
            // (its UI units, and before the saved windows are restored)
            if (p == null || p.TouchDefaultsVersion >= Version || world?.Player == null || !(Client.Game.Scene is Game.Scenes.GameScene))
            {
                return;
            }

            p.TouchDefaultsVersion = Version;

            p.AutoOpenDoors = true;
            p.CounterBarEnabled = true;
            p.CounterBarHighlightOnAmount = true;
            p.CounterBarHighlightAmount = 5;
            p.CounterBarCellSize = Cell;

            if (UIManager.GetGump<CounterBarGump>() != null)
            {
                return; // the player already has one of their own
            }

            UIManager.Add(CreateStrip(world));
        }

        /// <summary>The phone's counter strip: 14 counters, 7 across, near the top left, locked.</summary>
        public static CounterBarGump CreateStrip(World world)
        {
            Rectangle safe = TouchInput.Safe;
            int rows = (Counters.Length + Columns - 1) / Columns;
            CounterBarGump bar = new CounterBarGump(world, safe.X + 6, safe.Y + (int)(safe.Height * 0.2f), Cell);

            foreach (string name in Counters)
            {
                if (ItemGroups.TryGet(name, out ushort[] graphics))
                {
                    bar.AddCounter(graphics[0], null); // any hue: potions and reagents come in one colour
                }
            }

            bar.SizeTo(Columns, rows);
            bar.ReadOnly = true; // a drag moves the strip instead of pulling a counter off it

            return bar;
        }

        /// <summary>TouchInput.CheckLongPress: a long press on the strip hides it. True when it did.</summary>
        public static bool HideStripOnLongPress(Game.UI.Controls.Control over)
        {
            CounterBarGump bar = over?.RootParent as CounterBarGump ?? over as CounterBarGump;
            Profile p = ProfileManager.CurrentProfile;

            if (bar == null || p == null || !bar.ReadOnly)
            {
                return false; // unlocked for editing: a long press opens the counter's menu as before
            }

            p.TouchCountersHidden = true;
            bar.IsVisible = false;
            GameActions.Print(Client.Game.UO.World, "Counter strip hidden. Edit a button and pick 'Counters' to bring it back.", 0x3B2);

            return true;
        }

        private static (int Revision, bool Portrait, Rectangle Safe, int W, int H) _stripChecked = (-1, false, Rectangle.Empty, 0, 0);

        /// <summary>
        /// Every frame, in the world: a hidden strip stays hidden (it comes back with the saved windows
        /// at login); and when the phone turns or the layout changes, a strip lying under the HUD's top
        /// row (War, Chat, ...) moves down just below it.
        /// </summary>
        public static void KeepStripHidden()
        {
            Profile p = ProfileManager.CurrentProfile;

            if (p == null || !(UIManager.GetGump<CounterBarGump>() is CounterBarGump bar))
            {
                return;
            }

            if (p.TouchCountersHidden)
            {
                bar.IsVisible = false;

                return;
            }

            // the safe area too: iOS can report new insets a frame after the turn
            var key = (TouchInput.Revision, TouchInput.IsPortrait, TouchInput.Safe, TouchInput.ScreenW, TouchInput.ScreenH);

            if (_stripChecked.Equals(key))
            {
                return;
            }

            _stripChecked = key;
            ActionLayout layout = TouchInput.Current;

            if (layout == null)
            {
                return;
            }

            int rowBottom = -1;

            foreach (ActionButtonDef b in layout.Buttons)
            {
                if (b.Y >= 0.16f)
                {
                    continue; // the top row only
                }

                Point c = TouchInput.ButtonCenter(b);
                int r = TouchInput.ButtonRadius(b);

                // the button and the strip overlap across
                if (c.X + r > bar.X && c.X - r < bar.X + bar.Width)
                {
                    rowBottom = Math.Max(rowBottom, c.Y + r);
                }
            }

            if (rowBottom >= 0 && bar.Y < rowBottom + 4 && bar.Y + bar.Height > rowBottom - 2 * TouchInput.ScreenMin / 10)
            {
                bar.Y = Math.Max(0, Math.Min(rowBottom + 6, TouchInput.ScreenH - bar.Height - 4));
            }
        }

        /// <summary>The "counters" action: show or hide the strip ("on" / "off", or switch).</summary>
        public static void ToggleStrip(World world, string arg)
        {
            Profile p = ProfileManager.CurrentProfile;

            if (p == null)
            {
                return;
            }

            CounterBarGump bar = UIManager.GetGump<CounterBarGump>();
            bool visible = bar != null && bar.IsVisible && !p.TouchCountersHidden;
            bool show = string.IsNullOrEmpty(arg) ? !visible : arg.Equals("on", System.StringComparison.OrdinalIgnoreCase);

            p.TouchCountersHidden = !show;

            if (show)
            {
                p.CounterBarEnabled = true;

                if (bar == null)
                {
                    UIManager.Add(bar = CreateStrip(world));
                }

                bar.IsEnabled = true;
                bar.IsVisible = true;
            }
            else if (bar != null)
            {
                bar.IsVisible = false;
            }

            GameActions.Print(world, show ? "Counter strip shown." : "Counter strip hidden.", 0x3B2);
        }
    }
}
