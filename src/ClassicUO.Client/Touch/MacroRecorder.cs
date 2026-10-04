// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Razor's macro recorder for the phone: tap Record, play, tap Record again, and what you did is
    /// a new phone macro ("Recorded 1", ...) opened in the macro editor to rename or tweak. It
    /// writes the macro language's own steps - HUD buttons as their actions ("cast EnergyBolt",
    /// "skill Hiding", "action spell_lt:Explosion", "call Heal Up"), spellbook casts, skills, items
    /// used from your pack or hands ("useitem bandages"), target taps ("target self", "target last",
    /// "target item 0x0F7A", "target ground front", "target nearby forge"), speech, attacks and
    /// server menu buttons - with "waitfortarget" before a target, "waitforgump" before a menu
    /// button and a "wait" for pauses over a second. What isn't the player's own doing is left out:
    /// a running macro or trigger, a spell's automatic Last Target, auto loot, the pack primer.
    /// </summary>
    internal static class MacroRecorder
    {
        private const int MaxLines = 200;
        private const uint PauseMs = 1200;

        private enum Kind { Step, Target, Menu }

        private static readonly List<string> _lines = new List<string>();
        private static uint _lastAt;
        private static bool _full;

        /// <summary>Above 0 while the client acts on its own (macros, triggers, automatic targets): not recorded.</summary>
        public static int Quiet;

        public static bool Recording { get; private set; }

        /// <summary>The "record" action: start, or stop and save.</summary>
        public static void Toggle(World world)
        {
            if (!Recording)
            {
                _lines.Clear();
                _lastAt = 0;
                _full = false;
                Recording = true;
                GameActions.Print(world, "Recording a macro: do what it should do, then tap Record again.", 0x22);

                return;
            }

            Recording = false;

            if (_lines.Count == 0)
            {
                GameActions.Print(world, "Nothing was recorded.", 0x35);

                return;
            }

            MobileMacroRunner.EnsureLoaded();
            MobileMacroSet set = MobileMacroRunner.Macros;
            int n = 1;

            while (set.Find($"Recorded {n}") != null)
            {
                n++;
            }

            MobileMacro macro = new MobileMacro { Name = $"Recorded {n}" };
            macro.Lines.Add($"// Recorded {DateTime.Now:MMM d HH:mm}. Rename it, change waits or targets, add if/loop around it.");
            macro.Lines.AddRange(_lines);
            set.Macros.Add(macro);
            set.Save();
            _lines.Clear();

            GameActions.Print(world, $"Saved as '{macro.Name}' ({macro.Lines.Count - 1} steps).", 0x44);
            MacroEditorGump.Open(world, macro.Name);
        }

        private static void Add(string line, Kind kind)
        {
            if (!Recording || Quiet > 0 || line == null)
            {
                return;
            }

            if (_lines.Count >= MaxLines)
            {
                if (!_full)
                {
                    _full = true;
                    GameActions.Print(Client.Game.UO.World, $"The recording is full ({MaxLines} steps): tap Record to save it.", 0x21);
                }

                return;
            }

            uint now = Time.Ticks;
            string last = _lines.Count > 0 ? _lines[^1] : null;

            if (kind == Kind.Target)
            {
                if (last == null || !last.StartsWith("waitfortarget"))
                {
                    _lines.Add("waitfortarget 3000");
                }
            }
            else if (kind == Kind.Menu)
            {
                _lines.Add("waitforgump 3000");
            }
            else if (_lastAt != 0 && now - _lastAt >= PauseMs)
            {
                _lines.Add($"wait {Math.Min(60000, (now - _lastAt + 50) / 100 * 100)}");
            }

            _lines.Add(line);
            _lastAt = now;
            GameActions.Print(Client.Game.UO.World, "REC " + line, 0x3B2);
        }

        // ---------- what the player did ----------

        /// <summary>MobileActions.Run: a HUD button (its own targets and casts are then quiet).</summary>
        public static void OnAction(string action, string arg)
        {
            if (!Recording || Quiet > 0)
            {
                return;
            }

            switch (action)
            {
                case "record": case "stop_macro": case "layout_next": case "edit_layout": case "macro_editor": case "uo_macros":
                case "chat": case "triggers": case "autoloot": case "dress_save": case "all_names": case "healthbar_target": case "open":
                    return; // controls for the phone, not steps
                case "spell": Add("cast " + arg, Kind.Step); return;
                case "skill": Add("skill " + arg, Kind.Step); return;
                case "say": Add("say " + arg, Kind.Step); return;
                case "mmacro": Add("call " + arg, Kind.Step); return;
                case "attack_last": Add("attack last", Kind.Step); return;
                case "useonce": Add("useonce " + (arg ?? ""), Kind.Step); return;
                case "dress": Add("dress " + (arg ?? "1"), Kind.Step); return;
                case "undress": Add("undress " + (arg ?? "1"), Kind.Step); return;
                case "target_self": Add("target self", Kind.Target); return;
                case "last_target": Add("target last", Kind.Target); return;
                default: Add("action " + (arg == null ? action : action + ":" + arg), Kind.Step); return;
            }
        }

        /// <summary>GameActions.CastSpell: a spell from the spellbook or a hotkey.</summary>
        public static void OnCast(int index)
        {
            if (!Recording || Quiet > 0)
            {
                return;
            }

            MacroSubType spell = MacroSubType.Clumsy + (index - 1);

            if (index >= 1 && index <= 64)
            {
                Add("cast " + spell, Kind.Step);
            }
        }

        /// <summary>GameActions.UseSkill.</summary>
        public static void OnSkill(int index)
        {
            if (!Recording || Quiet > 0)
            {
                return;
            }

            string name = Client.Game.UO.World?.Player?.Skills is Skill[] skills && index >= 0 && index < skills.Length ? skills[index]?.Name : null;
            string compact = name?.Replace(" ", "").Replace("'", "");

            if (compact != null && Enum.TryParse(compact, true, out MacroSubType _))
            {
                Add("skill " + compact, Kind.Step);
            }
        }

        /// <summary>GameActions.DoubleClick: an item in your pack or hands becomes "useitem".</summary>
        public static void OnDoubleClick(World world, uint serial)
        {
            if (!Recording || Quiet > 0 || !SerialHelper.IsItem(serial))
            {
                return;
            }

            Item item = world.Items.Get(serial);

            if (item == null)
            {
                return;
            }

            if (item.RootContainer == world.Player.Serial)
            {
                Add("useitem " + ItemName(item.Graphic), Kind.Step);
            }
            else
            {
                Add($"// used {item.Name ?? item.ItemData.Name} (not in your pack: not recorded)", Kind.Step);
            }
        }

        /// <summary>TargetManager.Target(serial): a tap on a creature, a player or an item.</summary>
        public static void OnTarget(World world, uint serial)
        {
            if (!Recording || Quiet > 0 || !IsPlainCursor(world))
            {
                return;
            }

            if (serial == world.Player.Serial)
            {
                Add("target self", Kind.Target);
            }
            else if (SerialHelper.IsMobile(serial))
            {
                Add("target last", Kind.Target);
            }
            else if (world.Items.Get(serial) is Item item)
            {
                Add(item.RootContainer == world.Player.Serial
                    ? $"target item 0x{item.Graphic:X4}"
                    : $"target nearby {Word(item.ItemData.Name)} 2", Kind.Target);
            }
        }

        /// <summary>TargetManager.Target(graphic, x, y, z): the ground or a static (a tree, water, an anvil...).</summary>
        public static void OnTargetGround(World world, ushort graphic, int x, int y)
        {
            if (!Recording || Quiet > 0 || !IsPlainCursor(world))
            {
                return;
            }

            PlayerMobile p = world.Player;
            int dx = x - p.X, dy = y - p.Y;

            if (graphic != 0)
            {
                Add($"target nearby {Word(Client.Game.UO.FileManager.TileData.StaticData[graphic].Name)} {Math.Max(2, Math.Max(Math.Abs(dx), Math.Abs(dy)))}", Kind.Target);
            }
            else if (dx == 0 && dy == 0)
            {
                Add("target ground here", Kind.Target);
            }
            else if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
            {
                Add("target ground front", Kind.Target);
            }
            else
            {
                Add($"// targeted the ground {Math.Max(Math.Abs(dx), Math.Abs(dy))} tiles away (not recorded)", Kind.Step);
            }
        }

        /// <summary>GameActions.Say: speech from the chat line.</summary>
        public static void OnSay(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Add("say " + text.Trim(), Kind.Step);
            }
        }

        /// <summary>GameActions.Attack: a double tap on a foe in war mode.</summary>
        public static void OnAttack() => Add("attack last", Kind.Step);

        /// <summary>GameActions.ReplyGump: a button on a server menu (closing it is left out).</summary>
        public static void OnMenuButton(int button)
        {
            if (button != 0)
            {
                Add("gumpbutton " + button, Kind.Menu);
            }
        }

        private static bool IsPlainCursor(World world) =>
            world.TargetManager.TargetingState is CursorTarget.Object or CursorTarget.Position;

        /// <summary>A group name when the graphic has one of its own ("bandages"), else its hex.</summary>
        private static string ItemName(ushort graphic)
        {
            foreach ((string name, string _, ushort[] graphics) in ItemGroups.Named)
            {
                if (graphics.Length == 1 && graphics[0] == graphic)
                {
                    return name;
                }
            }

            return $"0x{graphic:X4}";
        }

        /// <summary>The last word of a tile's name ("a forge" -> "forge"), for "target nearby".</summary>
        private static string Word(string name)
        {
            string[] words = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return words.Length == 0 ? "?" : words[^1];
        }
    }
}
