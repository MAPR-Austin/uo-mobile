// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Razor's auto-loot for the phone: walk over a corpse and what you want from it goes to your
    /// pack - gold and reagents unless you say otherwise. Off until switched on (action "autoloot";
    /// "autoloot:on|off"; "autoloot:gold,regs,arrows" sets the list and switches it on), and kept per
    /// character. Corpses within 2 tiles, the nearest first, each once: it is opened without showing
    /// its window, and the wanted items (top level, not inside bags) are lifted at the server's pace
    /// through the agents' queue.
    /// Never a corpse looting would make you a criminal or that isn't yours to take: blue, green,
    /// yellow or a party member's at death (the death packet tells the colour); a corpse that died out
    /// of sight is taken only if it isn't a human's (a monster can't be blue; a person might be); and
    /// never once the server warns, on opening it, that looting it is a criminal act or not yours (a
    /// monster someone else killed: the first two minutes are theirs).
    /// It waits while you are hidden (a lift reveals you), in war mode (each lift holds your next
    /// potion or bandage for half a second), and for 2 s after your own taps.
    /// </summary>
    internal static class AutoLoot
    {
        private const int Range = 2;
        private const uint CheckMs = 400, ScanAfterMs = 700, ContentsWaitMs = 2500, SwallowMs = 3000, PlayerGraceMs = 2000;

        // Corpse.CheckLoot / CanLoot: "did not earn the right", "may not loot", "will be a criminal act" (monster / player)
        private static readonly uint[] Warnings = { 1005035, 1010049, 1005036, 1005038 };
        private static bool _warned;
        public const string DefaultList = "gold,regs";

        private static readonly Dictionary<uint, (NotorietyFlag Noto, bool Party)> _deaths = new Dictionary<uint, (NotorietyFlag, bool)>();
        private static readonly HashSet<uint> _done = new HashSet<uint>(); // corpses looted or passed over
        private static uint _opening, _openedAt, _swallowUntil, _nextCheck;
        private static ushort[] _wanted;
        private static string _wantedFrom;

        public static bool On => ProfileManager.CurrentProfile?.TouchAutoLoot == true;

        /// <summary>The "autoloot" action: switch, "on" / "off", or a list of items to take (and on).</summary>
        public static void Set(World world, string arg)
        {
            Profile p = ProfileManager.CurrentProfile;

            if (p == null)
            {
                return;
            }

            arg = arg?.Trim();

            if (string.IsNullOrEmpty(arg))
            {
                p.TouchAutoLoot = !p.TouchAutoLoot;
            }
            else if (arg.Equals("on", StringComparison.OrdinalIgnoreCase) || arg.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                p.TouchAutoLoot = arg.Equals("on", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                string bad = MobileMacroRunner.ItemListError(arg);

                if (bad != null)
                {
                    GameActions.Print(world, $"Auto loot: {bad}", 0x21);

                    return;
                }

                p.TouchAutoLootItems = arg;
                p.TouchAutoLoot = true;
            }

            _done.Clear(); // corpses passed over while it was off get their turn

            GameActions.Print(world, p.TouchAutoLoot
                ? $"Auto loot on: {List().Replace(",", ", ")} from corpses within {Range} tiles (never blue, green or party corpses)."
                : "Auto loot off.", p.TouchAutoLoot ? (ushort)0x44 : (ushort)0x35);
        }

        private static string List()
        {
            string list = ProfileManager.CurrentProfile?.TouchAutoLootItems;

            return string.IsNullOrWhiteSpace(list) ? DefaultList : list;
        }

        /// <summary>PacketHandlers.DisplayDeath: the colour the dead had, for its corpse.</summary>
        public static void OnDeath(World world, Mobile owner, uint corpseSerial)
        {
            if (owner == null || !SerialHelper.IsValid(corpseSerial))
            {
                return;
            }

            if (_deaths.Count > 2000)
            {
                _deaths.Clear();
            }

            _deaths[corpseSerial] = (owner.NotorietyFlag, world.Party.Contains(owner.Serial));
        }

        /// <summary>PacketHandlers: a cliloc message; a warning while a corpse opens leaves it alone.</summary>
        public static void OnCliloc(uint cliloc)
        {
            if (_opening != 0 && Array.IndexOf(Warnings, cliloc) >= 0)
            {
                _warned = true;
            }
        }

        /// <summary>PacketHandlers.OpenContainer: true to skip the window (a corpse this opened).</summary>
        public static bool SwallowOpen(uint serial)
        {
            if (serial != _opening || _swallowUntil == 0 || Time.Ticks > _swallowUntil)
            {
                return false;
            }

            _swallowUntil = 0;

            return true;
        }

        /// <summary>Every frame (TouchInput.Update, in the world).</summary>
        public static void Update(World world)
        {
            if (!On || Time.Ticks < _nextCheck)
            {
                return;
            }

            _nextCheck = Time.Ticks + CheckMs;
            PlayerMobile p = world?.Player;

            if (p == null || p.IsDead)
            {
                return;
            }

            if (_opening != 0)
            {
                Item opened = world.Items.Get(_opening);

                // contents can take longer than 0.7 s on a slow link
                if (Time.Ticks - _openedAt < ScanAfterMs || (opened != null && opened.Items == null && !_warned && Time.Ticks - _openedAt < ContentsWaitMs))
                {
                    return;
                }

                _opening = 0;

                if (_warned)
                {
                    Log.Trace($"[UOMobile] auto loot: corpse {opened?.Serial:X8} left alone (the server warned)");
                }
                else if (opened != null)
                {
                    Take(world, opened);
                }

                _warned = false;

                return;
            }

            if (p.IsHidden || p.InWarMode || Time.Ticks - MobileMacroRunner.PlayerActedAt < PlayerGraceMs ||
                Agents.Busy || Client.Game.UO.GameCursor.ItemHold.Enabled || world.TargetManager.IsTargeting || (p.WeightMax > 0 && p.Weight >= p.WeightMax))
            {
                return;
            }

            Item corpse = null;
            int best = int.MaxValue;

            foreach (Item it in world.Items.Values)
            {
                if (it.IsCorpse && it.OnGround && !it.IsDestroyed && it.Distance <= Range && it.Distance < best && !_done.Contains(it.Serial))
                {
                    corpse = it;
                    best = it.Distance;
                }
            }

            if (corpse == null)
            {
                return;
            }

            if (_done.Count > 2000)
            {
                _done.Clear();
            }

            _done.Add(corpse.Serial);
            bool lootable = Lootable(corpse);
            Log.Trace($"[UOMobile] auto loot: corpse {corpse.Serial:X8} body 0x{corpse.Amount:X4} {(lootable ? "opened" : "passed over")}");

            if (!lootable)
            {
                return;
            }

            _opening = corpse.Serial;
            _openedAt = Time.Ticks;
            _warned = false;
            _swallowUntil = Time.Ticks + SwallowMs;

            uint lastObject = world.LastObject;
            GameActions.DoubleClick(world, corpse.Serial);
            world.LastObject = lastObject; // Last Object stays what the player last used
        }

        private static bool Lootable(Item corpse)
        {
            if (_deaths.TryGetValue(corpse.Serial, out var death))
            {
                return !death.Party && death.Noto is not (NotorietyFlag.Innocent or NotorietyFlag.Ally or NotorietyFlag.Invulnerable);
            }

            return !IsHumanBody(corpse.Amount); // died out of sight: a corpse's amount is the body it was
        }

        private static bool IsHumanBody(ushort body) =>
            body is 0x0190 or 0x0191 or 0x0192 or 0x0193 or 0x025D or 0x025E or 0x025F or 0x0260 or 0x029A or 0x029B or 0x02B6 or 0x02B7;

        private static void Take(World world, Item corpse)
        {
            Item pack = world.Player.FindItemByLayer(Layer.Backpack);

            if (pack == null)
            {
                return;
            }

            if (_wanted == null || _wantedFrom != List())
            {
                _wantedFrom = List();
                _wanted = MobileMacroRunner.ItemList(_wantedFrom);
            }

            uint corpseSerial = corpse.Serial;
            uint packSerial = pack.Serial;

            for (LinkedObject o = corpse.Items; o != null; o = o.Next)
            {
                Item it = (Item)o;

                if (Array.IndexOf(_wanted, it.Graphic) < 0)
                {
                    continue;
                }

                uint s = it.Serial;

                Agents.EnqueueLoot(() =>
                {
                    Item now = world.Items.Get(s);

                    // still in the corpse, the corpse still in reach, and nothing to give away (a lift reveals; war: a fight)
                    if (now != null && now.Container == corpseSerial && world.Items.Get(corpseSerial) is Item c && c.Distance <= Range &&
                        world.Player is PlayerMobile pm && !pm.IsHidden && !pm.InWarMode &&
                        GameActions.PickUp(world, s, 0, 0, now.Amount))
                    {
                        GameActions.DropItem(s, 0xFFFF, 0xFFFF, 0, packSerial);
                    }
                });
            }
        }

        /// <summary>A new login: nothing remembered from the last character.</summary>
        public static void Reset()
        {
            _deaths.Clear();
            _done.Clear();
            _opening = _swallowUntil = 0;
            _warned = false;
            _wanted = null;
        }
    }
}
