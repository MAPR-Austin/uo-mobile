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
            int queued = 0;

            foreach (uint serial in serials)
            {
                Item it = world.Items.Get(serial);

                if (it == null || it.Container == world.Player.Serial || pack == null)
                {
                    continue; // gone, or already worn
                }

                Layer layer = (Layer)it.ItemData.Layer;
                uint s = serial;

                // whatever is in that slot goes to the pack first
                _steps.Enqueue(() =>
                {
                    Item worn = world.Player?.FindItemByLayer(layer);

                    if (worn != null && worn.Serial != s && GameActions.PickUp(world, worn.Serial, 0, 0, 1))
                    {
                        GameActions.DropItem(worn.Serial, 0xFFFF, 0xFFFF, 0, pack.Serial);
                    }
                });

                _steps.Enqueue(() =>
                {
                    if (GameActions.PickUp(world, s, 0, 0, 1))
                    {
                        GameActions.Equip(world);
                    }
                });

                queued++;
            }

            GameActions.Print(world, queued == 0 ? $"Already wearing '{name}'." : $"Dressing '{name}'...", 0x3B2);
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
                    var loaded = JsonSerializer.Deserialize(File.ReadAllText(file), AgentsJsonContext.Default.DictionaryStringListUInt32);

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
                File.WriteAllText(_setsFile, JsonSerializer.Serialize(_sets, AgentsJsonContext.Default.DictionaryStringListUInt32));
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
