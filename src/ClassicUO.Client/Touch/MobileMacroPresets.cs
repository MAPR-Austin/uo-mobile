// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Ready-made phone macros, by category, for the macro editor's "Presets" tab: tapping one adds
    /// a copy the player can run, edit or put on the bar. Tuned to this shard's pre-AOS rules
    /// (spell recovery 0.75 s, mining 1.6 s, chopping up to three 1.6 s swings, fishing ~8 s, craft
    /// menu "Make Last" is button 21). A leading "//" line is the usage note shown when it's added.
    /// </summary>
    internal static class MobileMacroPresets
    {
        // item graphics
        private const string Pickaxes = "0x0E86,0x0E85,0x0F39,0x0F3A";
        private const string Axes = "0x0F43,0x0F44,0x0F47,0x0F48,0x0F49,0x0F4A,0x0F4B,0x0F4C,0x0F45,0x0F46,0x13FA,0x13FB,0x1442,0x1443";
        private const string FishingPoles = "0x0DBF,0x0DC0";
        private const string Ore = "0x19B7,0x19B8,0x19B9,0x19BA";
        private const string HealPotion = "0x0F0C", CurePotion = "0x0F07", RefreshPotion = "0x0F0B";
        private const string StrengthPotion = "0x0F09", AgilityPotion = "0x0F08", ExplosionPotion = "0x0F0D";

        public static readonly (string Category, (string Name, string[] Lines)[] Macros)[] Library =
        {
            ("Combat", new[]
            {
                ("Explo + EB", new[] { "// The classic dump: Explosion, then Energy Bolt as it lands. Set a target first.", "if not targetalive", "  settarget nearest", "endif", "cast Explosion", "waitfortarget 3000", "target last", "wait 800", "cast EnergyBolt", "waitfortarget 3000", "target last" }),
                ("Para Dump", new[] { "// Paralyze, then Explosion and Energy Bolt while they're stuck.", "cast Paralyze", "waitfortarget 3000", "target last", "wait 800", "cast Explosion", "waitfortarget 3000", "target last", "wait 800", "cast EnergyBolt", "waitfortarget 3000", "target last" }),
                ("Poison + EB", new[] { "// Poison stops their Greater Heal; follow with Energy Bolt.", "cast Poison", "waitfortarget 3000", "target last", "wait 800", "cast EnergyBolt", "waitfortarget 3000", "target last" }),
                ("Flamestrike", new[] { "cast FlameStrike", "waitfortarget 3000", "target last" }),
                ("Interrupt", new[] { "// A fast Magic Arrow to break their spell (watch for In Vas Mani).", "cast MagicArrow", "waitfortarget 2000", "target last" }),
                ("Strip Reflect", new[] { "// Two Fireballs and a Harm empty a basic Magic Reflection before you dump.", "cast Fireball", "waitfortarget 3000", "target last", "wait 800", "cast Fireball", "waitfortarget 3000", "target last", "wait 800", "cast Harm", "waitfortarget 2000", "target last" }),
                ("Precast EB", new[] { "// Holds an Energy Bolt ready: tap a target (or Last Target) when they commit.", "cast EnergyBolt" }),
                ("Explo Potion", new[] { "// Lights a purple potion, holds it, throws it at your target before it blows (3.75 s fuse).", "useitem " + ExplosionPotion, "waitfortarget 1500", "wait 1600", "target last" }),
                ("Stun Punch", new[] { "// UOR wrestling stun (Wrestling + Anatomy 80, empty hands): readies it and attacks.", "action stun", "attack last" }),
                ("Disarm", new[] { "// UOR wrestling disarm (Wrestling + Arms Lore 80, empty hands).", "action disarm", "attack last" }),
                ("Arm/Disarm", new[] { "// Puts your weapon away (to drink or cast) or takes it back out.", "action arm_disarm" }),
            }),
            ("Heal", new[]
            {
                ("Heal Up", new[] { "// Cure first if poisoned, then Greater Heal.", "if poisoned", "  cast Cure", "  waitfortarget 2000", "  target self", "elseif hp < 80", "  cast GreaterHeal", "  waitfortarget 2500", "  target self", "endif" }),
                ("Pot Up", new[] { "// Cure potion if poisoned, else a heal potion when hurt; refresh when tired.", "if poisoned", "  useitem " + CurePotion, "elseif hp < 70", "  useitem " + HealPotion, "endif", "if stam < 60", "  wait 600", "  useitem " + RefreshPotion, "endif" }),
                ("Buff Pots", new[] { "useitem " + StrengthPotion, "wait 600", "useitem " + AgilityPotion }),
                ("Bandage Self", new[] { "useitem 0x0E21", "waitfortarget 1500", "target self" }),
                ("Bandage Loop", new[] { "// Keeps a bandage going on you (about 11 s each at 100 Dex). Tap again to stop.", "loop", "  if hp < 95", "    useitem 0x0E21", "    waitfortarget 1500", "    target self", "    wait 10800", "  else", "    wait 1000", "  endif", "endloop" }),
                ("Reflect", new[] { "cast MagicReflection" }),
                ("Reactive Armor", new[] { "cast ReactiveArmor" }),
                ("Bless Self", new[] { "cast Bless", "waitfortarget 2000", "target self" }),
            }),
            ("Gather", new[]
            {
                ("Mine", new[] { "// Face a mountainside or stand in a cave with a pickaxe or shovel. Stops when the spot runs dry or you're heavy.", "clearjournal", "loop", "  if weight >= 95", "    print Too heavy: smelt or bank your ore.", "    stop", "  endif", "  if journal no metal here", "    print This spot is mined out: move and run it again.", "    stop", "  endif", "  if journal can't mine", "    print Face a mountainside or cave floor.", "    stop", "  endif", "  useitem " + Pickaxes, "  waitfortarget 2000", "  target ground front", "  wait 2000", "endloop" }),
                ("Mine Here", new[] { "// Mines the cave floor you stand on.", "clearjournal", "loop", "  if weight >= 95", "    print Too heavy: smelt or bank your ore.", "    stop", "  endif", "  if journal no metal here", "    print This spot is mined out: move and run it again.", "    stop", "  endif", "  useitem " + Pickaxes, "  waitfortarget 2000", "  target ground here", "  wait 2000", "endloop" }),
                ("Chop Wood", new[] { "// Stand next to a tree with an axe or hatchet.", "clearjournal", "loop", "  if weight >= 95", "    print Too heavy: drop off your logs.", "    stop", "  endif", "  if journal not enough wood", "    print This tree is done: move to another.", "    stop", "  endif", "  useitem " + Axes, "  waitfortarget 2000", "  target nearby tree 2", "  wait 5000", "endloop" }),
                ("Fish", new[] { "// Stand within 4 tiles of water with a fishing pole.", "clearjournal", "loop", "  if weight >= 95", "    print Too heavy: drop off your fish.", "    stop", "  endif", "  if journal seem to be biting", "    print The fish moved on: try another spot.", "    stop", "  endif", "  useitem " + FishingPoles, "  waitfortarget 2000", "  target nearby water 4", "  wait 9000", "endloop" }),
                ("Smelt Ore", new[] { "// Stand next to a forge; smelts every pile of ore you carry.", "clearjournal", "loop", "  if count " + Ore + " < 1", "    print All ore smelted.", "    stop", "  endif", "  if journal not enough metal-bearing", "    print A pile is too small to smelt: combine it with another, then run this again.", "    stop", "  endif", "  if journal no idea how to smelt", "    print You can't smelt that ore yet: set it aside, then run this again.", "    stop", "  endif", "  useitem " + Ore, "  waitfortarget 2000", "  target nearby forge 2", "  wait 1200", "endloop" }),
            }),
            ("Craft", new[]
            {
                Craft("Smith", "0x0FBB,0x0FBC,0x13E3,0x13E4,0x0FB4,0x0FB5", "0x1BF2", "ingots"),
                Craft("Tailor", "0x0F9D,0x0F9E", null, null),
                Craft("Carpenter", "0x1028,0x1029,0x1034,0x1035,0x102C,0x102D,0x1030,0x1031,0x1032,0x1033,0x10E4,0x10E5,0x10E6,0x10E7", "0x1BDD,0x1BD7", "wood"),
                Craft("Tinker", "0x1EB8,0x1EBC", null, null),
                Craft("Bowyer", "0x1022,0x1023", "0x1BDD,0x1BD7", "wood"),
                Craft("Alchemy", "0x0E9B", "0x0F0E", "empty bottles"),
                Craft("Scribe", "0x0FBF,0x0FC0", "0x0EF3", "blank scrolls"),
                Craft("Cook", "0x097F,0x1043,0x103E", null, null),
            }),
            ("Train", new[]
            {
                ("Hiding", new[] { "loop", "  skill Hiding", "  wait 10500", "endloop" }),
                ("Stealth", new[] { "// Stealth needs Hiding 80. Hides, then sneaks.", "loop", "  if not hidden", "    skill Hiding", "  else", "    skill Stealth", "  endif", "  wait 10500", "endloop" }),
                ("Meditation", new[] { "loop", "  if mana < 100", "    skill Meditation", "  endif", "  wait 10500", "endloop" }),
                ("Magery", new[] { "// Greater Heal on yourself, meditating when low on mana.", "loop", "  if mana < 20", "    skill Meditation", "    wait 10500", "  else", "    cast GreaterHeal", "    waitfortarget 2500", "    target self", "    wait 1000", "  endif", "endloop" }),
                ("Detect Hidden", new[] { "loop", "  skill DetectingHidden", "  waitfortarget 2000", "  target self", "  wait 10500", "endloop" }),
                ("Spirit Speak", new[] { "loop", "  skill SpiritSpeak", "  wait 10500", "endloop" }),
                ("Anatomy", new[] { "// Uses your last target.", "loop", "  skill Anatomy", "  waitfortarget 2000", "  target last", "  wait 1500", "endloop" }),
                ("Eval Int", new[] { "// Uses your last target.", "loop", "  skill EvaluatingIntelligence", "  waitfortarget 2000", "  target last", "  wait 1500", "endloop" }),
            }),
            ("Home/Town", new[]
            {
                ("Lock Down", new[] { "say I wish to lock this down" }),
                ("Release", new[] { "say I wish to release this" }),
                ("Secure", new[] { "say I wish to secure this" }),
                ("Ban", new[] { "say I ban thee" }),
                ("Eject", new[] { "say Remove thyself" }),
                ("Bank", new[] { "say bank" }),
                ("Balance", new[] { "say balance" }),
                ("Vendor Buy", new[] { "say vendor buy" }),
                ("Vendor Sell", new[] { "say vendor sell" }),
                ("Guards", new[] { "say guards" }),
                ("Stable", new[] { "say stable" }),
                ("Claim Pets", new[] { "say claim" }),
            }),
            ("Pets", new[]
            {
                ("All Kill", new[] { "say all kill", "waitfortarget 1500", "target last" }),
                ("All Follow", new[] { "say all follow me" }),
                ("All Guard", new[] { "say all guard me" }),
                ("All Come", new[] { "say all come" }),
                ("All Stay", new[] { "say all stay" }),
                ("All Stop", new[] { "say all stop" }),
            }),
        };

        /// <summary>Opens the tool's craft menu when needed and keeps pressing Make Last (button 21).</summary>
        private static (string, string[]) Craft(string name, string tools, string resource, string resourceName)
        {
            var lines = new List<string> { $"// Make the item once from the {name.ToLowerInvariant()} menu first; this repeats Make Last. Tap again to stop.", "loop" };

            if (resource != null)
            {
                lines.AddRange(new[] { $"  if count {resource} < 1", $"    print Out of {resourceName}.", "    stop", "  endif" });
            }

            lines.AddRange(new[]
            {
                "  if not gump",
                "    useitem " + tools,
                "    waitforgump 3000",
                "  endif",
                "  if not gump",
                "    print The craft menu didn't open: check your tools.",
                "    stop",
                "  endif",
                "  gumpbutton 21",
                "  waitforgump 8000",
                "  wait 300",
                "  if gumptext haven't made",
                "    print Make the item once from the menu first.",
                "    stop",
                "  elseif gumptext required skills",
                "    print Your skill is too low for that item.",
                "    stop",
                "  elseif gumptext not have",
                "    print Out of materials.",
                "    stop",
                "  elseif gumptext don't have",
                "    print Out of materials.",
                "    stop",
                "  elseif gumptext must be near",
                "    print Stand next to the station this craft needs.",
                "    stop",
                "  endif",
                "endloop"
            });

            return (name + " Last", lines.ToArray());
        }
    }
}
