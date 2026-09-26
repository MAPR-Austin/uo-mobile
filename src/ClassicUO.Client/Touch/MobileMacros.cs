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
    ///   settarget nearest|next    choose the last target without a cursor
    ///   useitem GRAPHIC [HUE]     double-click the first matching item in the backpack
    ///   attack last|nearest
    ///   if COND / elseif COND / else / endif
    ///   loop [N]  ...  endloop    N times, or until the macro is stopped
    ///   stop
    ///
    /// Conditions: hp|mana|stam|targethp  &lt;|&gt;|&lt;=|&gt;=|= N   (percent),
    ///             poisoned, hidden, war, targeting, dead, targetalive,
    ///             targetrange &lt;= N (tiles), count GRAPHIC &gt;= N (items in pack);
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
            MobileMacroSet set = File.Exists(FilePath) ? ConfigurationResolver.Load(FilePath, MobileMacroJsonContext.Default.MobileMacroSet) : null;

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
            _loops.Clear();
        }

        public static void Stop()
        {
            _program = null;
            _name = null;
            _loops.Clear();
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
                    MobileActions.Run(world, (s.Args.Length > 0 ? s.Args[0].ToLowerInvariant() : "nearest") == "next" ? "target_next" : "target_nearest");

                    break;

                case "attack":
                    MobileActions.Run(world, (s.Args.Length > 0 ? s.Args[0].ToLowerInvariant() : "last") == "nearest" ? "attack_nearest" : "attack_last");

                    break;

                case "useitem":
                    Item item = FindInPack(world, Graphic(s.Args, 0), s.Args.Length > 1 ? Graphic(s.Args, 1) : (ushort)0xFFFF);

                    if (item != null)
                    {
                        GameActions.DoubleClick(world, item.Serial);
                    }
                    else
                    {
                        GameActions.Print(world, $"Macro: no {arg} in your pack.", 0x21);
                    }

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
                    if (tm.LastTargetInfo.IsEntity && SerialHelper.IsValid(tm.LastTargetInfo.Serial))
                    {
                        tm.Target(tm.LastTargetInfo.Serial);
                    }
                    else
                    {
                        tm.TargetLast();
                    }

                    break;

                case "nearest":
                case "next":
                    uint serial = kind == "nearest"
                        ? world.FindNearest(ScanTypeObject.Hostile)
                        : world.FindNext(ScanTypeObject.Hostile, tm.SelectedTarget, false);

                    if (SerialHelper.IsValid(serial))
                    {
                        tm.SelectedTarget = serial;
                        tm.Target(serial);
                    }

                    break;

                case "item":
                    Item item = FindInPack(world, Graphic(args, 1), 0xFFFF);

                    if (item != null)
                    {
                        tm.Target(item.Serial);
                    }

                    break;
            }
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
                case "poisoned": case "hidden": case "war": case "targeting": case "dead": case "targetalive":
                    return null;

                case "hp": case "mana": case "stam": case "targethp": case "targetrange":
                    return args.Length >= i + 3 && Array.IndexOf(Comparators, args[i + 1]) >= 0 && int.TryParse(args[i + 2], out _)
                        ? null
                        : $"'{args[i]}' needs a comparison, e.g. '{args[i]} < 50'";

                case "count":
                    return args.Length >= i + 4 && Array.IndexOf(Comparators, args[i + 2]) >= 0 && int.TryParse(args[i + 3], out _)
                        ? null
                        : "'count' needs a graphic and comparison, e.g. 'count 0x0E21 >= 5'";

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
                case "hidden": result = p.IsHidden; break;
                case "war": result = p.InWarMode; break;
                case "targeting": result = tm.IsTargeting; break;
                case "dead": result = p.IsDead; break;
                case "targetalive": result = target != null && !target.IsDead && !target.IsDestroyed; break;
                case "hp": result = Compare(Percent(p.Hits, p.HitsMax), args[i + 1], args[i + 2]); break;
                case "mana": result = Compare(Percent(p.Mana, p.ManaMax), args[i + 1], args[i + 2]); break;
                case "stam": result = Compare(Percent(p.Stamina, p.StaminaMax), args[i + 1], args[i + 2]); break;
                case "targethp": result = target != null && Compare(Percent(target.Hits, target.HitsMax), args[i + 1], args[i + 2]); break;
                case "targetrange": result = target != null && Compare(target.Distance, args[i + 1], args[i + 2]); break;
                case "count": result = Compare(CountInPack(world, Graphic(args, i + 1)), args[i + 2], args[i + 3]); break;
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

        private static int Int(string[] args, int index, int fallback) =>
            index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static Item Backpack(World world) => world.Player?.FindItemByLayer(Layer.Backpack);

        private static Item FindInPack(World world, ushort graphic, ushort hue) => Backpack(world)?.FindItem(graphic, hue);

        private static int CountInPack(World world, ushort graphic)
        {
            Item pack = Backpack(world);

            return pack == null ? 0 : Count(pack, graphic);
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
