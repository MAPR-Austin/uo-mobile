// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Razor-style agents for the phone.
    /// <list type="bullet">
    /// <item>Use Once: each press uses the next item of a kind not used yet this session - a trapped
    /// pouch by default (UOR's way out of a paralyze: the trap's damage breaks it). Action
    /// "useonce[:GROUP]", macro "useonce [group]".</item>
    /// <item>Dress sets: "dress_save:NAME" remembers what you wear; "dress:NAME" puts it back on
    /// (whatever is in the way goes to the pack first), "undress:NAME" takes it off into the pack -
    /// one item at a time at the server's pace. Saved per character (dress_sets.json beside the
    /// phone macros). Macro "dress NAME" / "undress NAME".</item>
    /// <item>Restock: with your bank box (or a chest) open, "restock" tops your pack up to set amounts
    /// from it - bandages 50, each reagent 50, heal, cure and refresh potions 5 unless the action
    /// says otherwise: "restock:bandages=100,regs=30,arrows=200". A stack in the pack grows rather
    /// than a new pile landing beside it.</item>
    /// <item>Organize: "organize[:LIST]" moves every item of LIST (default gold) from your pack into
    /// the container you opened last - the bank box, a chest, a bag in the pack: "organize:gold,ore".</item>
    /// </list>
    /// </summary>
    internal static class Agents
    {
        private const uint StepMs = 700; // the server's item lift / equip pace

        private static readonly Layer[] Worn =
        {
            Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt, Layer.Helmet, Layer.Gloves,
            Layer.Ring, Layer.Talisman, Layer.Necklace, Layer.Waist, Layer.Torso, Layer.Bracelet, Layer.Tunic,
            Layer.Earrings, Layer.Arms, Layer.Cloak, Layer.Robe, Layer.Skirt, Layer.Legs
        };

        private static readonly HashSet<uint> _usedOnce = new HashSet<uint>();
        private static readonly Queue<Action> _steps = new Queue<Action>();
        private static uint _nextStepAt;
        private static Dictionary<string, List<uint>> _sets;
        private static string _setsFile;

        public static bool Busy => _steps.Count > 0;

        /// <summary>A step for the queue (auto loot's lifts): run in turn, at the server's pace.</summary>
        public static void Enqueue(Action step) => _steps.Enqueue(step);

        // ---------- use once ----------

        public static void UseOnce(World world, string group)
        {
            group = string.IsNullOrWhiteSpace(group) ? "pouch" : group.Trim();

            if (!ItemGroups.TryGet(group, out ushort[] graphics))
            {
                GameActions.Print(world, $"Use once: no item kind called '{group}'.", 0x21);

                return;
            }

            Item pack = world.Player?.FindItemByLayer(Layer.Backpack);
            Item found = pack == null ? null : FindUnused(pack, graphics);

            if (found == null)
            {
                GameActions.Print(world, $"No unused {group} left in your pack.", 0x21);

                return;
            }

            _usedOnce.Add(found.Serial);
            GameActions.DoubleClick(world, found.Serial);
        }

        private static Item FindUnused(Item container, ushort[] graphics)
        {
            for (LinkedObject o = container.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;

                if (Array.IndexOf(graphics, it.Graphic) >= 0 && !_usedOnce.Contains(it.Serial))
                {
                    return it;
                }
            }

            for (LinkedObject o = container.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;

                if (!it.IsEmpty)
                {
                    Item inner = FindUnused(it, graphics);

                    if (inner != null)
                    {
                        return inner;
                    }
                }
            }

            return null;
        }

        // ---------- dress sets ----------

        public static void SaveDress(World world, string name)
        {
            name = Name(name);
            List<uint> serials = new List<uint>();

            foreach (Layer layer in Worn)
            {
                Item it = world.Player?.FindItemByLayer(layer);

                if (it != null)
                {
                    serials.Add(it.Serial);
                }
            }

            Sets()[name] = serials;
            SaveSets();
            GameActions.Print(world, $"Dress set '{name}' saved: {serials.Count} items.", 0x44);
        }

        public static void Dress(World world, string name)
        {
            name = Name(name);

            if (!Sets().TryGetValue(name, out List<uint> serials) || serials.Count == 0)
            {
                GameActions.Print(world, $"No dress set '{name}': save one first.", 0x21);

                return;
            }

            Item pack = world.Player?.FindItemByLayer(Layer.Backpack);
            int queued = 0, missing = 0;

            foreach (uint serial in serials)
            {
                Item it = world.Items.Get(serial);

                if (it == null || pack == null)
                {
                    missing++; // gone, or in a bag the client hasn't seen opened

                    continue;
                }

                if (it.Container == world.Player.Serial)
                {
                    continue; // already worn
                }

                uint s = serial;

                // whatever is in the way goes to the pack first
                foreach (Layer layer in InTheWay(it))
                {
                    Layer l = layer;
                    Item worn = world.Player.FindItemByLayer(l);

                    // a shield stays for a one-handed weapon; only a two-handed weapon in that hand moves
                    if (l == Layer.TwoHanded && (Layer)it.ItemData.Layer == Layer.OneHanded && worn != null && !worn.ItemData.IsWeapon)
                    {
                        continue;
                    }

                    if (worn != null && worn.Serial != s && !serials.Contains(worn.Serial))
                    {
                        _steps.Enqueue(() =>
                        {
                            Item w = world.Player?.FindItemByLayer(l);

                            if (w != null && w.Serial != s && GameActions.PickUp(world, w.Serial, 0, 0, 1))
                            {
                                GameActions.DropItem(w.Serial, 0xFFFF, 0xFFFF, 0, pack.Serial);
                            }
                        });
                    }
                }

                _steps.Enqueue(() =>
                {
                    if (GameActions.PickUp(world, s, 0, 0, 1))
                    {
                        GameActions.Equip(world);
                    }
                });

                queued++;
            }

            string note = missing > 0 ? $" ({missing} not found - in a bag that hasn't been opened?)" : "";
            GameActions.Print(world, queued == 0 ? $"Nothing of '{name}' to put on{note}." : $"Dressing '{name}'...{note}", 0x3B2);
        }

        /// <summary>
        /// The slots to empty before putting <paramref name="it" /> on: its own; and for the hands, a
        /// two-handed weapon needs both free, a one-handed weapon pushes out a two-handed one (a shield
        /// stays), a shield pushes out a two-handed weapon.
        /// </summary>
        private static Layer[] InTheWay(Item it)
        {
            Layer own = (Layer)it.ItemData.Layer;
            bool weapon = it.ItemData.IsWeapon;

            if (own == Layer.TwoHanded && weapon)
            {
                return new[] { Layer.OneHanded, Layer.TwoHanded };
            }

            if (own == Layer.OneHanded)
            {
                return new[] { Layer.OneHanded, Layer.TwoHanded }; // TwoHanded only if a weapon there (checked below)
            }

            return new[] { own };
        }

        public static void Undress(World world, string name)
        {
            name = Name(name);
            Item pack = world.Player?.FindItemByLayer(Layer.Backpack);

            if (!Sets().TryGetValue(name, out List<uint> serials) || pack == null)
            {
                GameActions.Print(world, $"No dress set '{name}'.", 0x21);

                return;
            }

            foreach (uint serial in serials)
            {
                Item it = world.Items.Get(serial);

                if (it != null && it.Container == world.Player.Serial)
                {
                    uint s = serial;
                    _steps.Enqueue(() =>
                    {
                        if (GameActions.PickUp(world, s, 0, 0, 1))
                        {
                            GameActions.DropItem(s, 0xFFFF, 0xFFFF, 0, pack.Serial);
                        }
                    });
                }
            }
        }

        // ---------- restock and organize ----------

        public const string DefaultRestock = "bandages=50,regs=50,heal=5,cure=5,refresh=5";
        public const string DefaultOrganize = "gold";

        public static void Restock(World world, string list)
        {
            list = string.IsNullOrWhiteSpace(list) ? DefaultRestock : list;
            Item pack = world.Player?.FindItemByLayer(Layer.Backpack);
            Item source = OpenedContainer(world, pack, false);

            if (pack == null || source == null)
            {
                GameActions.Print(world, "Restock: open your bank box or a chest first.", 0x21);

                return;
            }

            int stacks = 0;

            foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=');

                if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), out int want) || want < 0 || MobileMacroRunner.ItemListError(kv[0]) != null)
                {
                    GameActions.Print(world, $"Restock: '{part.Trim()}' should look like bandages=50.", 0x21);

                    return;
                }

                foreach (ushort graphic in MobileMacroRunner.ItemList(kv[0]))
                {
                    int need = want - Count(pack, graphic);

                    for (Item stack = Find(source, graphic, null); need > 0 && stack != null; stack = Find(source, graphic, stack))
                    {
                        int take = Math.Min(need, Math.Max(1, (int)stack.Amount));
                        need -= take;
                        Move(world, stack.Serial, take, pack, graphic);
                        stacks++;
                    }
                }
            }

            GameActions.Print(world, stacks == 0 ? "Restock: your pack is already stocked (or the container has none)." : $"Restocking {stacks} stacks...", 0x3B2);
        }

        public static void Organize(World world, string list)
        {
            list = string.IsNullOrWhiteSpace(list) ? DefaultOrganize : list;
            string bad = MobileMacroRunner.ItemListError(list);
            Item pack = world.Player?.FindItemByLayer(Layer.Backpack);
            Item dest = OpenedContainer(world, pack, true);

            if (bad != null)
            {
                GameActions.Print(world, $"Organize: {bad}", 0x21);

                return;
            }

            if (pack == null || dest == null)
            {
                GameActions.Print(world, "Organize: open the container to fill first (your bank box, a chest or a bag).", 0x21);

                return;
            }

            ushort[] graphics = MobileMacroRunner.ItemList(list);
            List<Item> moving = new List<Item>();

            for (LinkedObject o = pack.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;

                if (it.Serial != dest.Serial && Array.IndexOf(graphics, it.Graphic) >= 0)
                {
                    moving.Add(it);
                }
            }

            foreach (Item it in moving)
            {
                Move(world, it.Serial, it.Amount, dest, it.Graphic);
            }

            GameActions.Print(world, moving.Count == 0 ? $"Organize: no {list.Replace(",", ", ")} in your pack." : $"Moving {moving.Count} items...", 0x3B2);
        }

        /// <summary>The container window opened last, other than the backpack (one in the pack only when <paramref name="inPack"/>).</summary>
        private static Item OpenedContainer(World world, Item pack, bool inPack)
        {
            foreach (Game.UI.Gumps.Gump g in Game.Managers.UIManager.Gumps)
            {
                if (g is Game.UI.Gumps.ContainerGump cg && !cg.IsDisposed && world.Items.Get(cg.LocalSerial) is Item c && pack != null && c.Serial != pack.Serial &&
                    (inPack || !Inside(world, c, pack.Serial)))
                {
                    return c;
                }
            }

            return null;
        }

        private static bool Inside(World world, Item it, uint container)
        {
            for (uint s = it.Container; SerialHelper.IsValid(s);)
            {
                if (s == container)
                {
                    return true;
                }

                Item parent = world.Items.Get(s);

                if (parent == null)
                {
                    return false;
                }

                s = parent.Container;
            }

            return false;
        }

        private static int Count(Item container, ushort graphic)
        {
            int n = 0;

            for (LinkedObject o = container.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;
                n += it.Graphic == graphic ? Math.Max(1, (int)it.Amount) : 0;

                if (!it.IsEmpty)
                {
                    n += Count(it, graphic);
                }
            }

            return n;
        }

        /// <summary>The next stack of <paramref name="graphic"/> in <paramref name="container"/> (top level first, then bags) after <paramref name="after"/>.</summary>
        private static Item Find(Item container, ushort graphic, Item after)
        {
            bool passed = after == null;

            foreach (Item it in Stacks(container, graphic))
            {
                if (passed)
                {
                    return it;
                }

                passed = it == after;
            }

            return null;
        }

        private static IEnumerable<Item> Stacks(Item container, ushort graphic)
        {
            for (LinkedObject o = container.Items; o != null; o = o.Next)
            {
                if (((Item)o).Graphic == graphic)
                {
                    yield return (Item)o;
                }
            }

            for (LinkedObject o = container.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;

                if (!it.IsEmpty && it.Graphic != graphic)
                {
                    foreach (Item inner in Stacks(it, graphic))
                    {
                        yield return inner;
                    }
                }
            }
        }

        /// <summary>Queues a lift of <paramref name="amount"/> and a drop onto a like stack in <paramref name="into"/>, else into it.</summary>
        private static void Move(World world, uint serial, int amount, Item into, ushort graphic)
        {
            uint intoSerial = into.Serial;

            _steps.Enqueue(() =>
            {
                Item target = world.Items.Get(intoSerial);

                if (target == null || world.Items.Get(serial) == null || !GameActions.PickUp(world, serial, 0, 0, amount))
                {
                    return;
                }

                Item stack = null;

                for (LinkedObject o = target.Items; o != null && stack == null; o = o.Next)
                {
                    Item it = (Item)o;
                    stack = it.Graphic == graphic && it.Serial != serial && it.ItemData.IsStackable ? it : null;
                }

                if (stack != null)
                {
                    GameActions.DropItem(serial, stack.X, stack.Y, 0, stack.Serial); // onto the stack: they merge
                }
                else
                {
                    GameActions.DropItem(serial, 0xFFFF, 0xFFFF, 0, intoSerial);
                }
            });
        }

        /// <summary>Every frame (TouchInput.Update, in the world): the next queued equip step.</summary>
        public static void Update(World world)
        {
            if (_steps.Count == 0 || Time.Ticks < _nextStepAt)
            {
                return;
            }

            if (world?.Player == null || world.Player.IsDead)
            {
                _steps.Clear();

                return;
            }

            if (Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                return; // the player is carrying something: wait
            }

            _nextStepAt = Time.Ticks + StepMs;
            _steps.Dequeue()();
        }

        /// <summary>A new login: pouches count afresh, sets come from the new character's file.</summary>
        public static void Reset()
        {
            _usedOnce.Clear();
            _steps.Clear();
            _sets = null;
        }

        private static string Name(string name) => string.IsNullOrWhiteSpace(name) ? "1" : name.Trim();

        private static Dictionary<string, List<uint>> Sets()
        {
            string file = Path.Combine(ProfileManager.ProfilePath ?? CUOEnviroment.ExecutablePath, "dress_sets.json");

            if (_sets != null && file == _setsFile)
            {
                return _sets;
            }

            _setsFile = file;
            _sets = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(file))
                {
                    var loaded = ConfigurationResolver.Load(file, AgentsJsonContext.Default.DictionaryStringListUInt32, escapeBackslashes: false);

                    if (loaded != null)
                    {
                        foreach (var kv in loaded)
                        {
                            _sets[kv.Key] = kv.Value ?? new List<uint>();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"dress sets: {e.Message}");
            }

            return _sets;
        }

        private static void SaveSets()
        {
            try
            {
                ConfigurationResolver.Save(_sets, _setsFile, AgentsJsonContext.Default.DictionaryStringListUInt32); // write, then swap in
            }
            catch (Exception e)
            {
                Log.Warn($"dress sets: {e.Message}");
            }
        }
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, List<uint>>))]
    internal sealed partial class AgentsJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
