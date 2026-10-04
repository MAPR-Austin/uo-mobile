// SPDX-License-Identifier: BSD-2-Clause

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
    /// it to add counters by dropping items on it, a long press on one sets it up). No explosion
    /// potion: a stray double tap would arm one in the pack.
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
            UIManager.Add(bar);
        }
    }
}
