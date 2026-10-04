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
    /// Razor-style targeting for the phone (owner: notoriety targeting allowed, reds on blues too).
    /// <list type="bullet">
    /// <item>Pick by colour: the closest or next mobile matching a filter - enemy (gray, criminal,
    /// red, orange), red, gray, blue, orange, green, any - optionally "human" or "monster" only;
    /// never yourself, your party, your pets, the dead or the invulnerable. Actions
    /// "target_closest:red,human" and "target_next:enemy"; macro "settarget closest blue".</item>
    /// <item>Smart last target: separate last harmful and last beneficial targets, chosen by the
    /// kind of cursor the server opened (a heal goes to whom you last healed, an attack spell to
    /// whom you last attacked).</item>
    /// <item>Target queue: a spell button that targets (E-Bolt > Last, Heal Self) answers its own spell's
    /// cursor when it comes up, up to 4 s later; a fizzle or another action cancels it.</item>
    /// <item>Range check: a mobile out of spell range (12 tiles) isn't sent - the cursor stays up
    /// instead of the spell being lost on "That is too far away".</item>
    /// </list>
    /// </summary>
    internal static class SmartTargeting
    {
        private const int SpellRange = 12, ViewRange = 18;
        private const uint QueueMs = 4000; // (an 8th-circle spell takes 2.5 s to cast)

        private static uint _lastHarmful, _lastBeneficial;
        private static uint _cycledSerial; // the last mobile "next" picked
        private enum Queued { None, Last, Self }
        private static Queued _queued;
        private static uint _queuedAt;

        // ---------- picking by colour ----------

        /// <summary>The closest (or, with <paramref name="next" />, the next after the current one) mobile matching <paramref name="spec" />; null if none.</summary>
        public static Mobile Pick(World world, string spec, bool next)
        {
            (Func<NotorietyFlag, bool> colour, int kind) = Parse(spec);
            List<Mobile> found = new List<Mobile>();

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (m == null || m.IsDestroyed || m == world.Player || m.IsDead || m.Distance > ViewRange ||
                    m.NotorietyFlag == NotorietyFlag.Invulnerable || m.IsRenamable || world.Party.Contains(m.Serial) ||
                    !colour(m.NotorietyFlag) || kind == 1 && !m.IsHuman || kind == 2 && m.IsHuman)
                {
                    continue;
                }

                found.Add(m);
            }

            if (found.Count == 0)
            {
                return null;
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            Mobile pick = found[0];

            if (next)
            {
                int i = found.FindIndex(m => m.Serial == _cycledSerial);
                pick = found[(i + 1) % found.Count];
            }

            _cycledSerial = pick.Serial;

            return pick;
        }

        /// <summary>"enemy" / "red" / "gray" / "blue" / "orange" / "green" / "any", plus "human" or "monster", comma or space separated.</summary>
        private static (Func<NotorietyFlag, bool> colour, int kind) Parse(string spec)
        {
            Func<NotorietyFlag, bool> colour = Enemy;
            int kind = 0;

            foreach (string word in (spec ?? "").ToLowerInvariant().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "enemy": case "hostile": colour = Enemy; break;
                    case "red": case "murderer": colour = n => n == NotorietyFlag.Murderer; break;
                    case "gray": case "grey": case "criminal": colour = n => n == NotorietyFlag.Gray || n == NotorietyFlag.Criminal; break;
                    case "blue": case "innocent": colour = n => n == NotorietyFlag.Innocent; break;
                    case "orange": colour = n => n == NotorietyFlag.Enemy; break;
                    case "green": case "ally": case "friend": colour = n => n == NotorietyFlag.Ally; break;
                    case "any": colour = n => true; break;
                    case "human": case "humanoid": case "player": kind = 1; break;
                    case "monster": case "creature": case "npc": kind = 2; break;
                }
            }

            return (colour, kind);
        }

        private static bool Enemy(NotorietyFlag n) =>
            n == NotorietyFlag.Gray || n == NotorietyFlag.Criminal || n == NotorietyFlag.Murderer || n == NotorietyFlag.Enemy;

        /// <summary>Pick and make it the current target (the "Target: name" overhead, the target panel).</summary>
        public static Mobile Select(World world, string spec, bool next)
        {
            Mobile m = Pick(world, spec, next);

            if (m == null)
            {
                GameActions.Print(world, $"No {(string.IsNullOrWhiteSpace(spec) ? "enemy" : spec.Replace(',', ' '))} in sight.", 0x3B2);

                return null;
            }

            MobileActions.SetCurrentTarget(world, m);

            return m;
        }

        // ---------- smart last target ----------

        /// <summary>
        /// Friendly to you, so a beneficial target: your party, your guild and allies (green), your
        /// pets, and blues - unless you are red yourself, when blues are prey.
        /// </summary>
        private static bool Friendly(World world, Mobile m)
        {
            return m.NotorietyFlag == NotorietyFlag.Ally || world.Party.Contains(m.Serial) || m.IsRenamable ||
                   m.NotorietyFlag == NotorietyFlag.Innocent && world.Player.NotorietyFlag != NotorietyFlag.Murderer;
        }

        /// <summary>
        /// MobileActions.SetCurrentTarget: picking a mobile (Closest Red, Set Target, Next...) makes it the
        /// last target of its kind - harmful for a foe, beneficial for a friend.
        /// </summary>
        public static void Remember(World world, Mobile m)
        {
            if (m == null || world?.Player == null || m == world.Player)
            {
                return;
            }

            if (Friendly(world, m))
            {
                _lastBeneficial = m.Serial;
            }
            else
            {
                _lastHarmful = m.Serial;
            }
        }

        /// <summary>TargetManager.Target: a mobile went to a harmful or beneficial cursor.</summary>
        public static void OnTargeted(World world, uint serial, TargetType type)
        {
            if (world?.Player == null || serial == world.Player.Serial || !SerialHelper.IsMobile(serial))
            {
                return;
            }

            if (type == TargetType.Harmful)
            {
                _lastHarmful = serial;
            }
            else if (type == TargetType.Beneficial)
            {
                _lastBeneficial = serial;
            }
        }

        /// <summary>A remembered mobile still worth targeting: present and alive (a ghost would just eat the spell).</summary>
        private static Mobile Alive(World world, uint serial)
        {
            Mobile m = serial == 0 ? null : world.Mobiles.Get(serial);

            return m != null && !m.IsDestroyed && !m.IsDead ? m : null;
        }

        /// <summary>Last Target, smart: by the open cursor's kind. Without a cursor up it does nothing (as UO's own).</summary>
        public static void TargetLast(World world)
        {
            TargetManager tm = world.TargetManager;

            if (!tm.IsTargeting)
            {
                return;
            }

            uint serial;

            if (tm.TargetingType == TargetType.Beneficial)
            {
                // the last friend helped; else the current target if it's a friend; else yourself
                Mobile last = Alive(world, tm.LastTargetInfo.IsEntity ? tm.LastTargetInfo.Serial : 0);
                serial = Alive(world, _lastBeneficial)?.Serial ?? (last != null && Friendly(world, last) ? last.Serial : world.Player.Serial);
            }
            else if (tm.TargetingType == TargetType.Harmful)
            {
                serial = Alive(world, _lastHarmful)?.Serial ?? (tm.LastTargetInfo.IsEntity ? tm.LastTargetInfo.Serial : 0);
            }
            else
            {
                serial = tm.LastTargetInfo.IsEntity ? tm.LastTargetInfo.Serial : 0;
            }

            if (serial == 0)
            {
                tm.TargetLast();

                return;
            }

            if (tm.TargetingType != TargetType.Neutral && SerialHelper.IsMobile(serial) && serial != world.Player.Serial)
            {
                Mobile m = Alive(world, serial);

                if (m == null || m.Distance > SpellRange)
                {
                    // keep the cursor (and the spell): out of range it would be lost
                    GameActions.Print(world, m == null ? "Your last target is gone." : $"{m.Name} is out of range ({m.Distance} tiles).", 0x21);

                    return;
                }
            }

            tm.Target(serial);
        }

        public static void TargetSelf(World world)
        {
            if (world.TargetManager.IsTargeting)
            {
                world.TargetManager.Target(world.Player.Serial);
            }
        }

        /// <summary>
        /// spell_lt / spell_self: the spell just cast will open a cursor; answer that one when it comes
        /// (a harmful or beneficial cursor only - never a crafting, bandage or placement one - and
        /// yourself never on a harmful one). A fizzle, another action or 4 s end the wait.
        /// </summary>
        public static void QueueLast() => Queue(Queued.Last);

        public static void QueueSelf() => Queue(Queued.Self);

        /// <summary>Another action ran, or the spell fizzled: whatever was waiting isn't wanted any more.</summary>
        public static void ClearQueue() => _queued = Queued.None;

        /// <summary>A spell_lt / spell_self target is waiting for its cursor (triggers hold off).</summary>
        public static bool Pending => _queued != Queued.None;

        private static void Queue(Queued what)
        {
            _queued = what;
            _queuedAt = Time.Ticks;
        }

        /// <summary>PacketHandlers (via PlayerTimers): the spell fizzled or was disturbed - its cursor won't come.</summary>
        public static void OnCliloc(uint cliloc)
        {
            if (cliloc == 502632 || cliloc == 500641) // The spell fizzles. / Your concentration is disturbed...
            {
                _queued = Queued.None;
            }
        }

        /// <summary>A new login: nothing remembered from the last character.</summary>
        public static void Reset()
        {
            _lastHarmful = _lastBeneficial = _cycledSerial = 0;
            _queued = Queued.None;
        }

        /// <summary>Every frame: a queued Last Target / Self fires when the spell's cursor comes up, or lapses.</summary>
        public static void Update(World world)
        {
            if (_queued == Queued.None)
            {
                return;
            }

            if (Time.Ticks - _queuedAt > QueueMs || world?.Player == null)
            {
                _queued = Queued.None;

                return;
            }

            TargetManager tm = world.TargetManager;

            if (!tm.IsTargeting || tm.TargetingType == TargetType.Neutral)
            {
                return; // not the spell's cursor (yet)
            }

            Queued what = _queued;
            _queued = Queued.None;

            if (what == Queued.Self)
            {
                if (tm.TargetingType == TargetType.Beneficial)
                {
                    tm.Target(world.Player.Serial);
                }
            }
            else
            {
                TargetLast(world);
            }
        }
    }
}
