// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Player macros for the phone HUD, in the spirit of UOAssist / Razor macros with
    /// EasyUO-style conditions and loops. A macro is a list of text lines, one step each:
    ///
    ///   action ID                 any MobileActions id (attack_nearest, spell_lt:EnergyBolt, open:Backpack, ...)
    ///   cast SPELL                start casting (cursor comes up when the spell is ready)
    ///   skill SKILL               use a skill
    ///   say TEXT
    ///   wait MS
    ///   waitfortarget [MS]        wait until a target cursor is up (default 3000 ms)
    ///   target self|last|nearest|next|item GRAPHIC      answer the target cursor
    ///   target ground [front|here]   the ground ahead of you (or under you): mining
    ///   target nearby NAME [RANGE]   the closest thing whose name has NAME, within RANGE tiles
    ///                                (default 2): a tree, water, a forge, an anvil...
    ///   settarget nearest|next    choose the last target without a cursor
    ///   useitem GRAPHIC[,GRAPHIC...] [HUE]   double-click the first match in the pack or in hand
    ///   attack last|nearest
    ///   waitforgump [MS]          wait until a server menu is open (default 3000 ms)
    ///   gumpbutton ID             press button ID on the newest server menu (21 = craft Make Last, 0 = close)
    ///   print TEXT                a message only you see
    ///   clearjournal              forget what the journal said so far (for "if journal")
    ///   if COND / elseif COND / else / endif
    ///   loop [N]  ...  endloop    N times, or until the macro is stopped
    ///   stop
    ///
    /// Conditions: hp|mana|stam|targethp|weight  &lt;|&gt;|&lt;=|&gt;=|= N   (percent),
    ///             poisoned, hidden, war, targeting, dead, targetalive, gump (a server menu is open),
    ///             targetrange &lt;= N (tiles), count GRAPHIC[,GRAPHIC...] &gt;= N (items in pack),
    ///             journal TEXT (a message containing TEXT arrived since the macro started),
    ///             gumptext TEXT (the macro's server menu shows TEXT, e.g. a craft menu notice);
    ///             prefix "not " to negate.
    /// Graphics are hex (0x0E21) or decimal.
    /// </summary>
    internal sealed class MobileMacro
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("lines")] public List<string> Lines { get; set; } = new List<string>();
    }

    internal sealed class MobileMacroSet
    {
        [JsonPropertyName("macros")] public List<MobileMacro> Macros { get; set; } = new List<MobileMacro>();

        private static string FilePath =>
            Path.Combine(ProfileManager.ProfilePath ?? CUOEnviroment.ExecutablePath, "mobile_macros.json");

        public MobileMacro Find(string name) => Macros.Find(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        public static MobileMacroSet Load()
        {
            MobileMacroSet set = File.Exists(FilePath) ? ConfigurationResolver.Load(FilePath, MobileMacroJsonContext.Default.MobileMacroSet, escapeBackslashes: false) : null;

            return set ?? CreateDefault();
        }

        public void Save() => ConfigurationResolver.Save(this, FilePath, MobileMacroJsonContext.Default.MobileMacroSet);

        /// <summary>Starter macros that double as examples of the language.</summary>
        public static MobileMacroSet CreateDefault() => new MobileMacroSet
        {
            Macros =
            {
                new MobileMacro { Name = "Dump E-Bolt", Lines = { "if not targetalive", "  settarget nearest", "endif", "cast EnergyBolt", "waitfortarget 3000", "target last" } },
                new MobileMacro { Name = "Heal Up", Lines = { "if poisoned", "  cast Cure", "  waitfortarget", "  target self", "elseif hp < 70", "  cast GreaterHeal", "  waitfortarget", "  target self", "else", "  say I'm fine", "endif" } },
                new MobileMacro { Name = "Bandage Loop", Lines = { "loop", "  if hp < 90", "    useitem 0x0E21", "    waitfortarget 1500", "    target self", "    wait 5500", "  else", "    wait 1000", "  endif", "endloop" } },
                new MobileMacro { Name = "Hide", Lines = { "if not hidden", "  skill Hiding", "endif" } },
            }
        };
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(MobileMacroSet))]
    sealed partial class MobileMacroJsonContext : JsonSerializerContext { }

    /// <summary>Parses and runs one <see cref="MobileMacro"/> at a time, a few steps per frame.</summary>
    internal static class MobileMacroRunner
    {
        private const int STEPS_PER_UPDATE = 64;

        private sealed class Step
        {
            public string Op;
            public string[] Args;
            public string Raw;
            public int Line;
            public int Jump = -1; // if/elseif -> next branch marker; loop -> after endloop; endloop -> its loop
            public int End = -1;  // elseif/else -> the chain's endif
        }

        private static List<Step> _program;
        private static string _name;
        private static int _pc;
        private static uint _waitUntil;
        private static uint _targetDeadline;
        private static uint _gumpDeadline;
        private static DateTime _journalSince;
        private static readonly HashSet<Gump> _openBefore = new HashSet<Gump>(); // server menus open when the macro started
        private static readonly Stack<(int Loop, int Remaining)> _loops = new Stack<(int, int)>();

        public static MobileMacroSet Macros { get; private set; }

        public static bool IsRunning => _program != null;
        public static string RunningName => _name;

        public static void EnsureLoaded()
        {
            Macros ??= MobileMacroSet.Load();
        }

        public static void Unload()
        {
            Stop();
            Macros = null;
        }

        public static void Run(World world, string name)
        {
            EnsureLoaded();
            MobileMacro macro = Macros.Find(name);

            if (macro == null)
            {
                GameActions.Print(world, $"No phone macro named '{name}'.");

                return;
            }

            // Tapping the running macro's button again stops it (for loops).
            if (IsRunning && string.Equals(_name, macro.Name, StringComparison.OrdinalIgnoreCase))
            {
                Stop();
                GameActions.Print(world, $"Macro '{macro.Name}' stopped.", 0x35);

                return;
            }

            string error = Compile(macro.Lines, out List<Step> program);

            if (error != null)
            {
                GameActions.Print(world, $"Macro '{macro.Name}': {error}", 0x21);

                return;
            }

            _program = program;
            _name = macro.Name;
            _pc = 0;
            _waitUntil = 0;
            _targetDeadline = 0;
            _gumpDeadline = 0;
            _journalSince = DateTime.Now;
            _loops.Clear();
            _openBefore.Clear();

            foreach (Gump g in UIManager.Gumps)
            {
                if (IsServerMenu(g))
                {
                    _openBefore.Add(g);
                }
            }
        }

        public static void Stop()
        {
            _program = null;
            _name = null;
            _loops.Clear();
            _openBefore.Clear();
        }

        /// <summary>Checks a macro without running it. Returns null when it compiles.</summary>
        public static string Validate(List<string> lines) => Compile(lines, out _);

        // ---------- compile ----------

        private static string Compile(List<string> lines, out List<Step> program)
        {
            program = new List<Step>();
            var open = new Stack<Step>();

            for (int i = 0; i < lines.Count; i++)
            {
                string raw = (lines[i] ?? "").Trim();

                if (raw.Length == 0 || raw.StartsWith("//") || raw.StartsWith("#"))
                {
                    continue;
                }

                string[] parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                Step s = new Step { Op = parts[0].ToLowerInvariant(), Args = parts[1..], Raw = raw, Line = i + 1 };
                int index = program.Count;
                program.Add(s);

                switch (s.Op)
                {
                    case "if":
                    case "loop":
                        open.Push(s);

                        break;

                    case "elseif":
                    case "else":
                        if (open.Count == 0 || (open.Peek().Op != "if" && open.Peek().Op != "elseif"))
                        {
                            return $"line {s.Line}: '{s.Op}' without 'if'";
                        }

                        open.Pop().Jump = index;
                        open.Push(s);

                        break;

                    case "endif":
                        if (open.Count == 0 || open.Peek().Op is not ("if" or "elseif" or "else"))
                        {
                            return $"line {s.Line}: 'endif' without 'if'";
                        }

                        open.Pop().Jump = index;

                        break;

                    case "endloop":
                        if (open.Count == 0 || open.Peek().Op != "loop")
                        {
                            return $"line {s.Line}: 'endloop' without 'loop'";
                        }

                        Step loop = open.Pop();
                        loop.Jump = index + 1;
                        s.Jump = program.IndexOf(loop);

                        break;

                    case "action": case "cast": case "skill": case "say": case "wait": case "waitfortarget":
                    case "target": case "settarget": case "useitem": case "attack": case "stop":
                    case "waitforgump": case "gumpbutton": case "print": case "clearjournal":
                        break;

                    default:
                        return $"line {s.Line}: unknown step '{s.Op}'";
                }

                if (s.Op is "if" or "elseif")
                {
                    string err = CheckCondition(s.Args);

                    if (err != null)
                    {
                        return $"line {s.Line}: {err}";
                    }
                }
            }

            if (open.Count > 0)
            {
                return $"line {open.Peek().Line}: '{open.Peek().Op}' is never closed";
            }

            // Falling into an elseif/else means the branch above it finished: jump to the chain's endif.
            for (int i = 0; i < program.Count; i++)
            {
                if (program[i].Op is "elseif" or "else")
                {
                    program[i].End = FindEndIf(program, i);
                }
            }

            return null;
        }

        private static int FindEndIf(List<Step> program, int from)
        {
            int j = from;

            while (j >= 0 && j < program.Count && program[j].Op != "endif")
            {
                j = program[j].Op is "elseif" or "else" ? program[j].Jump : j + 1;
            }

            return j;
        }

        // ---------- run ----------

        public static void Update(World world)
        {
            if (_program == null)
            {
                return;
            }

            if (world == null || !world.InGame || world.Player == null)
            {
                Stop();

                return;
            }

            for (int budget = 0; budget < STEPS_PER_UPDATE; budget++)
            {
                if (Time.Ticks < _waitUntil)
                {
                    return;
                }

                if (_targetDeadline != 0)
                {
                    if (world.TargetManager.IsTargeting || Time.Ticks >= _targetDeadline)
                    {
                        _targetDeadline = 0;
                    }
                    else
                    {
                        return;
                    }
                }

                if (_gumpDeadline != 0)
                {
                    if (NewestServerGump() != null || Time.Ticks >= _gumpDeadline)
                    {
                        _gumpDeadline = 0;
                    }
                    else
                    {
                        return;
                    }
                }

                if (_pc >= _program.Count)
                {
                    Stop();

                    return;
                }

                Step s = _program[_pc];

                try
                {
                    if (!Execute(world, s))
                    {
                        return; // yield (e.g. end of a loop iteration)
                    }
                }
                catch (Exception e)
                {
                    GameActions.Print(world, $"Macro '{_name}' line {s.Line}: {e.Message}", 0x21);
                    Stop();

                    return;
                }

                if (_program == null)
                {
                    return;
                }
            }
        }

        /// <summary>Executes the step at _pc and advances. Returns false to yield until the next frame.</summary>
        private static bool Execute(World world, Step s)
        {
            string arg = s.Args.Length > 0 ? string.Join(" ", s.Args) : "";

            switch (s.Op)
            {
                case "action":
                    MobileActions.Run(world, arg);

                    break;

                case "cast":
                    MobileActions.Run(world, "spell:" + arg);

                    break;

                case "skill":
                    MobileActions.Run(world, "skill:" + arg);

                    break;

                case "say":
                    GameActions.Say(arg);

                    break;

                case "wait":
                    _waitUntil = Time.Ticks + (uint)Math.Max(0, Int(s.Args, 0, 500));
                    _pc++;

                    return true;

                case "waitfortarget":
                    if (!world.TargetManager.IsTargeting)
                    {
                        _targetDeadline = Time.Ticks + (uint)Math.Max(1, Int(s.Args, 0, 3000));
                    }

                    break;

                case "target":
                    AnswerTarget(world, s.Args);

                    break;

                case "settarget":
                    {
                        // settarget [closest|nearest|next] [colour] [human|monster]: "settarget next red human"
                        string how = s.Args.Length > 0 ? s.Args[0].ToLowerInvariant() : "closest";
                        bool next = how == "next";
                        int from = how is "next" or "closest" or "nearest" ? 1 : 0;
                        string spec = s.Args.Length > from ? string.Join(",", s.Args, from, s.Args.Length - from) : "enemy";
                        SmartTargeting.Select(world, spec, next);
                    }

                    break;

                case "attack":
                    MobileActions.Run(world, (s.Args.Length > 0 ? s.Args[0].ToLowerInvariant() : "last") == "nearest" ? "attack_nearest" : "attack_last");

                    break;

                case "useitem":
                    Item item = FindUsable(world, Graphics(s.Args, 0), s.Args.Length > 1 ? Graphic(s.Args, 1) : (ushort)0xFFFF);

                    if (item == null)
                    {
                        // out of potions, bandages, ore or the tool broke: a loop can't go on without it
                        GameActions.Print(world, $"Macro '{_name}' stopped: no {arg} in your pack or hands.", 0x21);
                        Stop();

                        return false;
                    }

                    GameActions.DoubleClick(world, item.Serial);

                    break;

                case "waitforgump":
                    if (NewestServerGump() == null)
                    {
                        _gumpDeadline = Time.Ticks + (uint)Math.Max(1, Int(s.Args, 0, 3000));
                    }

                    break;

                case "gumpbutton":
                    Gump menu = NewestServerGump();

                    if (menu != null)
                    {
                        menu.OnButtonClick(Int(s.Args, 0, 0)); // replies with the menu's checkboxes and text, then closes it
                    }
                    else
                    {
                        GameActions.Print(world, "Macro: no menu is open.", 0x21);
                    }

                    break;

                case "print":
                    GameActions.Print(world, arg, 0x35);

                    break;

                case "clearjournal":
                    _journalSince = DateTime.Now;

                    break;

                case "stop":
                    Stop();

                    return false;

                case "if":
                    // Walk the chain to the first true branch; land on endif when none is true.
                    int pc = _pc;
                    Step branch = s;

                    while (true)
                    {
                        if (Condition(world, branch.Args))
                        {
                            _pc = pc + 1;

                            break;
                        }

                        pc = branch.Jump;
                        Step next = _program[pc];

                        if (next.Op == "elseif")
                        {
                            branch = next;

                            continue;
                        }

                        _pc = next.Op == "else" ? pc + 1 : pc; // else: enter it; endif: no-op next

                        break;
                    }

                    return true;

                case "elseif":
                case "else":
                    // reached by finishing the branch above
                    _pc = s.End;

                    return true;

                case "endif":
                    break;

                case "loop":
                    int count = Int(s.Args, 0, 0);
                    _loops.Push((_pc, count <= 0 ? -1 : count));

                    break;

                case "endloop":
                    if (_loops.Count > 0)
                    {
                        (int loopPc, int remaining) = _loops.Pop();

                        if (remaining == -1 || remaining > 1)
                        {
                            _loops.Push((loopPc, remaining == -1 ? -1 : remaining - 1));
                            _pc = loopPc + 1;

                            return false; // one iteration per frame at most
                        }
                    }

                    break;
            }

            _pc++;

            return true;
        }

        private static void AnswerTarget(World world, string[] args)
        {
            TargetManager tm = world.TargetManager;

            if (!tm.IsTargeting)
            {
                return;
            }

            string kind = args.Length > 0 ? args[0].ToLowerInvariant() : "last";

            switch (kind)
            {
                case "self":
                    tm.Target(world.Player.Serial);

                    break;

                case "last":
                    SmartTargeting.TargetLast(world); // last harmful or beneficial for this cursor, range-checked

                    break;

                case "nearest":
                case "closest":
                case "next":
                    {
                        // target closest|next [colour] [human|monster]: "target closest red"
                        string spec = args.Length > 1 ? string.Join(",", args, 1, args.Length - 1) : "enemy";
                        Mobile pick = SmartTargeting.Select(world, spec, kind == "next");

                        if (pick != null)
                        {
                            tm.Target(pick.Serial);
                        }
                    }

                    break;

                case "item":
                    Item item = FindInPack(world, Graphic(args, 1), 0xFFFF);

                    if (item != null)
                    {
                        tm.Target(item.Serial);
                    }

                    break;

                case "ground":
                    TargetGround(world, args.Length > 1 && args[1].Equals("here", StringComparison.OrdinalIgnoreCase));

                    break;

                case "nearby":
                    if (args.Length > 1)
                    {
                        TargetNearby(world, args[1], Int(args, 2, 2));
                    }

                    break;
            }
        }

        private static readonly (int X, int Y)[] Ahead = { (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1) };

        /// <summary>The land tile ahead of the player (or under them): a mountainside or a cave floor to mine.</summary>
        private static void TargetGround(World world, bool here)
        {
            if (world.TargetManager.TargetingState == CursorTarget.Object)
            {
                GameActions.Print(world, "Macro: this cursor wants an object, not the ground.", 0x21);
                world.TargetManager.CancelTarget();

                return;
            }

            PlayerMobile p = world.Player;
            (int dx, int dy) = here ? (0, 0) : Ahead[(int)(p.Direction & Direction.Mask)];
            int x = p.X + dx, y = p.Y + dy;

            for (GameObject o = world.Map.GetTile(x, y, false); o != null; o = o.TNext)
            {
                if (o is Land land)
                {
                    world.TargetManager.Target(0, (ushort)x, (ushort)y, land.Z, land.TileData.IsWet);

                    return;
                }
            }

            GameActions.Print(world, "Macro: no ground there.", 0x21);
            world.TargetManager.CancelTarget();
        }

        /// <summary>
        /// The closest item, static or land tile within RANGE (at most 12) whose tiledata name
        /// contains NAME ("tree", "water", "forge", "anvil", ...). A tile named exactly NAME wins
        /// over one that only contains it (real water over a water trough), then items and statics
        /// over land, then the closest.
        /// </summary>
        private static void TargetNearby(World world, string name, int range)
        {
            range = Math.Clamp(range, 0, 12);
            PlayerMobile p = world.Player;
            GameObject best = null;
            int bestDistance = int.MaxValue, bestRank = int.MaxValue;

            for (int dy = -range; dy <= range; dy++)
            {
                for (int dx = -range; dx <= range; dx++)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                    for (GameObject o = world.Map.GetTile(p.X + dx, p.Y + dy, false); o != null; o = o.TNext)
                    {
                        string tileName;
                        int rank;

                        switch (o)
                        {
                            case Item it when it.OnGround && !it.IsMulti: tileName = it.ItemData.Name; rank = 1; break;
                            case Static st: tileName = st.Name; rank = 1; break;
                            case Land land: tileName = land.TileData.Name; rank = 2; break;
                            default: continue;
                        }

                        if (tileName == null || tileName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        if (tileName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            rank = 0;
                        }

                        if (rank < bestRank || (rank == bestRank && distance < bestDistance))
                        {
                            best = o;
                            bestDistance = distance;
                            bestRank = rank;
                        }
                    }
                }
            }

            TargetManager tm = world.TargetManager;

            switch (best)
            {
                case Item it: tm.Target(it.Serial); break;
                case Land land: tm.Target(0, (ushort)land.X, (ushort)land.Y, land.Z, land.TileData.IsWet); break;
                case Static st: tm.Target(st.Graphic, (ushort)st.X, (ushort)st.Y, st.Z); break;
                default:
                    GameActions.Print(world, $"Macro: no {name} within {range} tiles.", 0x21);
                    tm.CancelTarget();

                    break;
            }
        }

        private static bool IsServerMenu(Gump g) => !g.IsDisposed && g.GetType() == typeof(Gump) && g.LocalSerial != 0 && g.ServerSerial != 0;

        /// <summary>
        /// The server menu (gump) on top that this macro's run brought up: a craft menu, a vendor
        /// list... Menus already open when the macro started (a house sign, a bulletin board) are
        /// not its menus, so a craft loop never presses buttons on them.
        /// </summary>
        private static Gump NewestServerGump()
        {
            foreach (Gump g in UIManager.Gumps)
            {
                if (IsServerMenu(g) && !_openBefore.Contains(g))
                {
                    return g;
                }
            }

            return null;
        }

        /// <summary>The macro's server menu shows TEXT (its labels and HTML: a craft notice, a price...).</summary>
        private static bool MenuSays(string text)
        {
            Gump menu = NewestServerGump();

            if (menu == null)
            {
                return false;
            }

            foreach (Game.UI.Controls.HtmlControl html in menu.FindControls<Game.UI.Controls.HtmlControl>())
            {
                if (html.Text != null && html.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            foreach (Game.UI.Controls.Label label in menu.FindControls<Game.UI.Controls.Label>())
            {
                if (label.Text != null && label.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool JournalSays(string text)
        {
            var entries = JournalManager.Entries;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                JournalEntry e = entries[i];

                if (e == null || e.Time < _journalSince)
                {
                    break;
                }

                if (e.Text != null && e.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // ---------- conditions ----------

        private static readonly string[] Comparators = { "<=", ">=", "<", ">", "=" };

        private static string CheckCondition(string[] args)
        {
            if (args.Length == 0)
            {
                return "missing condition";
            }

            int i = args[0].Equals("not", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            if (i >= args.Length)
            {
                return "missing condition after 'not'";
            }

            switch (args[i].ToLowerInvariant())
            {
                case "poisoned": case "hidden": case "war": case "targeting": case "dead": case "targetalive": case "gump": case "bandaging":
                    return null;

                case "journal":
                    return args.Length > i + 1 ? null : "'journal' needs the text to look for, e.g. 'journal no metal here'";

                case "gumptext":
                    return args.Length > i + 1 ? null : "'gumptext' needs the text to look for, e.g. 'gumptext haven't made'";

                case "hp": case "mana": case "stam": case "targethp": case "targetrange": case "weight":
                    return args.Length >= i + 3 && Array.IndexOf(Comparators, args[i + 1]) >= 0 && int.TryParse(args[i + 2], out _)
                        ? null
                        : $"'{args[i]}' needs a comparison, e.g. '{args[i]} < 50'";

                case "count":
                    return args.Length >= i + 4 && Array.IndexOf(Comparators, args[i + 2]) >= 0 && int.TryParse(args[i + 3], out _)
                        ? null
                        : "'count' needs an item and comparison, e.g. 'count bandages >= 5' or 'count 0x0E21 >= 5'";

                default:
                    return $"unknown condition '{args[i]}'";
            }
        }

        private static bool Condition(World world, string[] args)
        {
            bool negate = args[0].Equals("not", StringComparison.OrdinalIgnoreCase);
            int i = negate ? 1 : 0;
            PlayerMobile p = world.Player;
            TargetManager tm = world.TargetManager;
            world.Mobiles.TryGetValue(tm.LastTargetInfo.Serial, out Mobile target);
            bool result;

            switch (args[i].ToLowerInvariant())
            {
                case "poisoned": result = p.IsPoisoned; break;
                case "bandaging": result = PlayerTimers.Bandaging; break;
                case "hidden": result = p.IsHidden; break;
                case "war": result = p.InWarMode; break;
                case "targeting": result = tm.IsTargeting; break;
                case "dead": result = p.IsDead; break;
                case "targetalive": result = target != null && !target.IsDead && !target.IsDestroyed; break;
                case "gump": result = NewestServerGump() != null; break;
                case "journal": result = JournalSays(string.Join(" ", args, i + 1, args.Length - i - 1)); break;
                case "gumptext": result = MenuSays(string.Join(" ", args, i + 1, args.Length - i - 1)); break;
                case "weight": result = Compare(Percent(p.Weight, p.WeightMax), args[i + 1], args[i + 2]); break;
                case "hp": result = Compare(Percent(p.Hits, p.HitsMax), args[i + 1], args[i + 2]); break;
                case "mana": result = Compare(Percent(p.Mana, p.ManaMax), args[i + 1], args[i + 2]); break;
                case "stam": result = Compare(Percent(p.Stamina, p.StaminaMax), args[i + 1], args[i + 2]); break;
                case "targethp": result = target != null && Compare(Percent(target.Hits, target.HitsMax), args[i + 1], args[i + 2]); break;
                case "targetrange": result = target != null && Compare(target.Distance, args[i + 1], args[i + 2]); break;
                case "count": result = Compare(CountInPack(world, Graphics(args, i + 1)), args[i + 2], args[i + 3]); break;
                default: result = false; break;
            }

            return negate ? !result : result;
        }

        private static int Percent(int value, int max) => max <= 0 ? 0 : value * 100 / max;

        private static bool Compare(int value, string op, string rhs)
        {
            int n = int.Parse(rhs, CultureInfo.InvariantCulture);

            return op switch
            {
                "<" => value < n,
                ">" => value > n,
                "<=" => value <= n,
                ">=" => value >= n,
                _ => value == n
            };
        }

        // ---------- items ----------

        private static ushort Graphic(string[] args, int index)
        {
            if (index >= args.Length)
            {
                throw new FormatException("missing item graphic");
            }

            string s = args[index];

            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ushort.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// One graphic or several separated by commas, each a hex graphic or an item group's name
        /// (Touch/ItemGroups): "0x0E86,0x0E85", "bandages", "regs", "heal,cure".
        /// </summary>
        private static ushort[] Graphics(string[] args, int index)
        {
            if (index >= args.Length)
            {
                throw new FormatException("missing item graphic");
            }

            string[] parts = args[index].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var list = new List<ushort>(parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                if (ItemGroups.TryGet(parts[i], out ushort[] group))
                {
                    list.AddRange(group);
                }
                else
                {
                    list.Add(Graphic(parts, i));
                }
            }

            return list.ToArray();
        }

        private static int Int(string[] args, int index, int fallback) =>
            index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static Item Backpack(World world) => world.Player?.FindItemByLayer(Layer.Backpack);

        private static Item FindInPack(World world, ushort graphic, ushort hue) => Backpack(world)?.FindItem(graphic, hue);

        /// <summary>The first of the graphics found in the pack (any depth), else held in a hand: a tool or a weapon.</summary>
        private static Item FindUsable(World world, ushort[] graphics, ushort hue)
        {
            foreach (ushort g in graphics)
            {
                Item found = FindInPack(world, g, hue);

                if (found != null)
                {
                    return found;
                }
            }

            foreach (Layer hand in new[] { Layer.OneHanded, Layer.TwoHanded })
            {
                Item held = world.Player.FindItemByLayer(hand);

                if (held != null && Array.IndexOf(graphics, held.Graphic) >= 0 && (hue == 0xFFFF || held.Hue == hue))
                {
                    return held;
                }
            }

            return null;
        }

        private static int CountInPack(World world, ushort[] graphics)
        {
            Item pack = Backpack(world);
            int total = 0;

            if (pack != null)
            {
                foreach (ushort g in graphics)
                {
                    total += Count(pack, g);
                }
            }

            return total;
        }

        private static int Count(Item container, ushort graphic)
        {
            int total = 0;

            for (LinkedObject i = container.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (it.Graphic == graphic)
                {
                    total += Math.Max((int)it.Amount, 1);
                }

                if (!it.IsEmpty)
                {
                    total += Count(it, graphic);
                }
            }

            return total;
        }
    }
}
