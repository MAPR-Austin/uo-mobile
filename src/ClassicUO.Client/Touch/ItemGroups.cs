// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Names for the items players count and use, so a phone macro can say "useitem bandages" or
    /// "if count blackpearl < 10" instead of hex graphics; counters and agents share them. Before
    /// AOS the server sends no item names (no property lists), so items are told apart by graphic:
    /// lesser and greater potions of a kind look the same and count together. Graphics are UO's
    /// standard ones; a name may stand for several (all reagents, all ore piles). A macro can still
    /// give graphics in hex, and mix both: "useitem heal,0x0F0C".
    /// </summary>
    internal static class ItemGroups
    {
        private static readonly ushort[] Reagents = { 0x0F7A, 0x0F7B, 0x0F84, 0x0F85, 0x0F86, 0x0F88, 0x0F8C, 0x0F8D };

        private static readonly Dictionary<string, ushort[]> _groups = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The names, one per group (no aliases), for the editor's help and pickers.</summary>
        public static readonly List<(string Name, string Label, ushort[] Graphics)> Named = new List<(string, string, ushort[])>();

        static ItemGroups()
        {
            Add("bandages", "Bandages", new ushort[] { 0x0E21 }, "bandage", "bandies");

            // potions (bottles by colour)
            Add("heal", "Heal potions", new ushort[] { 0x0F0C }, "healpotion", "yellow");
            Add("cure", "Cure potions", new ushort[] { 0x0F07 }, "curepotion", "orange");
            Add("refresh", "Refresh potions", new ushort[] { 0x0F0B }, "refreshpotion", "red", "stam");
            Add("strength", "Strength potions", new ushort[] { 0x0F09 }, "strengthpotion", "white", "str");
            Add("agility", "Agility potions", new ushort[] { 0x0F08 }, "agilitypotion", "blue", "agi");
            Add("explosion", "Explosion potions", new ushort[] { 0x0F0D }, "explosionpotion", "purple", "explode");
            Add("poisonpotion", "Poison potions", new ushort[] { 0x0F0A }, "green");
            Add("nightsight", "Night Sight potions", new ushort[] { 0x0F06 }, "nightsightpotion", "black");
            Add("emptybottle", "Empty bottles", new ushort[] { 0x0F0E }, "bottle", "bottles");

            // reagents
            Add("blackpearl", "Black Pearl", new ushort[] { 0x0F7A }, "bp", "pearl");
            Add("bloodmoss", "Blood Moss", new ushort[] { 0x0F7B }, "bm", "moss");
            Add("garlic", "Garlic", new ushort[] { 0x0F84 }, "ga");
            Add("ginseng", "Ginseng", new ushort[] { 0x0F85 }, "gs", "gin");
            Add("mandrake", "Mandrake Root", new ushort[] { 0x0F86 }, "mandrakeroot", "mr");
            Add("nightshade", "Nightshade", new ushort[] { 0x0F88 }, "ns");
            Add("ash", "Sulfurous Ash", new ushort[] { 0x0F8C }, "sulfurousash", "sa");
            Add("silk", "Spiders' Silk", new ushort[] { 0x0F8D }, "spiderssilk", "ss");
            Add("regs", "All reagents", Reagents, "reagents");

            // ammunition, money
            Add("arrows", "Arrows", new ushort[] { 0x0F3F }, "arrow");
            Add("bolts", "Crossbow bolts", new ushort[] { 0x1BFB }, "bolt");
            Add("gold", "Gold", new ushort[] { 0x0EED }, "coins");

            // gathering and crafting
            Add("ore", "Ore", new ushort[] { 0x19B7, 0x19B8, 0x19B9, 0x19BA });
            Add("ingots", "Ingots", new ushort[] { 0x1BF2 }, "ingot");
            Add("logs", "Logs", new ushort[] { 0x1BDD, 0x1BE0 }, "log", "wood");
            Add("boards", "Boards", new ushort[] { 0x1BD7 }, "board");
            Add("cloth", "Cloth", new ushort[] { 0x1766, 0x175D }, "bolt of cloth");
            Add("leather", "Leather", new ushort[] { 0x1081 });
            Add("hides", "Hides", new ushort[] { 0x1079 }, "hide");
            Add("fish", "Fish", new ushort[] { 0x09CC, 0x09CD, 0x09CE, 0x09CF });
            Add("pickaxe", "Pickaxes and shovels", new ushort[] { 0x0E86, 0x0E85, 0x0F39, 0x0F3A }, "shovel");
            Add("hatchet", "Hatchets and axes", new ushort[] { 0x0F43, 0x0F44, 0x0F47, 0x0F48, 0x0F49, 0x0F4A, 0x13FA, 0x13FB }, "axe");
            Add("fishingpole", "Fishing poles", new ushort[] { 0x0DBF, 0x0DC0 }, "pole");
            Add("tinkertools", "Tinker's tools", new ushort[] { 0x1EB8, 0x1EB9 });
            Add("smithhammer", "Smith's hammers", new ushort[] { 0x13E3, 0x13E4, 0x0FB4, 0x0FB5 }, "hammer", "tongs");
            Add("sewingkit", "Sewing kits", new ushort[] { 0x0F9D });
            Add("mortar", "Mortars and pestles", new ushort[] { 0x0E9B }, "mortarpestle");
            Add("scissors", "Scissors", new ushort[] { 0x0F9E, 0x0F9F });
            Add("lockpicks", "Lockpicks", new ushort[] { 0x14FB, 0x14FC, 0x14FD, 0x14FE }, "lockpick");

            // useful odds and ends
            Add("pouch", "Pouches (trapped or not)", new ushort[] { 0x0E79 });
            Add("runebook", "Runebooks", new ushort[] { 0x22C5 });
            Add("rune", "Recall runes", new ushort[] { 0x1F14, 0x1F15, 0x1F16, 0x1F17 }, "runes");
            Add("spellbook", "Spellbooks", new ushort[] { 0x0EFA });
        }

        private static void Add(string name, string label, ushort[] graphics, params string[] aliases)
        {
            _groups[name] = graphics;
            Named.Add((name, label, graphics));

            foreach (string a in aliases)
            {
                _groups[a.Replace(" ", "")] = graphics;
            }
        }

        public static bool TryGet(string name, out ushort[] graphics)
        {
            return _groups.TryGetValue(name, out graphics);
        }

        /// <summary>The group a graphic belongs to (the first named one), for labels; null if none.</summary>
        public static string NameOf(ushort graphic)
        {
            foreach ((string name, string _, ushort[] graphics) in Named)
            {
                if (graphics.Length == 1 && graphics[0] == graphic)
                {
                    return name;
                }
            }

            return Named.FirstOrDefault(n => Array.IndexOf(n.Graphics, graphic) >= 0).Name;
        }
    }
}
