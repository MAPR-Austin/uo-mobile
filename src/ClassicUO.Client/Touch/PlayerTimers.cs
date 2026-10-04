// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Timers a phone player wants in sight (Razor's and UOAssist's): the bandage being applied,
    /// counted down with the server's own pre-AOS times (on yourself 9.4 + 0.6 x (120 - Dex) / 10 s;
    /// on someone else 3 s at 100+ Dex, 4 s at 40+, else 5 s, 5 s more to resurrect - servuo
    /// Bandage.GetDelay) from "You begin applying the bandages" until a message that ends it. Shown
    /// in a small strip under the counter bar (or at the top left) while one runs; the macro
    /// condition "bandaging" reads it.
    /// </summary>
    internal static class PlayerTimers
    {
        // the healer's messages that end a bandage (servuo Bandage.cs: finished, failed, interrupted, cures, resurrections)
        private static readonly uint[] BandageEnds =
        {
            500969, 500968, 500962, 500963, 501042, 1010395, 500965, 1049658, 1049659, 503256, 500966,
            1010058, 1010060, 1010062, 1010063, 500955, 500970
        };

        private const uint BandageBegins = 500956;
        private const uint Grace = 1500; // past the expected time, in case the end message was missed

        private static uint _bandageStart, _bandageEnd;
        private static uint _lastTargetAt;
        private static uint _lastTargetSerial;
        private static TimersGump _gump;

        public static bool Bandaging => _bandageEnd != 0 && Time.Ticks < _bandageEnd + Grace;

        /// <summary>TargetManager: a target went out (bandaging yourself or someone else decides the time).</summary>
        public static void OnTargeted(uint serial)
        {
            _lastTargetSerial = serial;
            _lastTargetAt = Time.Ticks;
        }

        /// <summary>PacketHandlers: a cliloc message for the player.</summary>
        public static void OnCliloc(World world, uint cliloc)
        {
            if (world?.Player == null)
            {
                return;
            }

            if (cliloc == BandageBegins)
            {
                // a bandage cursor answered just now with someone else: their time; otherwise (self, or the bandage-self command) yours
                bool other = _lastTargetSerial != 0 && _lastTargetSerial != world.Player.Serial && Time.Ticks - _lastTargetAt < 3000;
                int dex = world.Player.Dexterity;
                double seconds = other ? (dex >= 100 ? 3.0 : dex >= 40 ? 4.0 : 5.0) : 9.4 + 0.6 * ((120 - dex) / 10.0);

                _bandageStart = Time.Ticks;
                _bandageEnd = Time.Ticks + (uint)(seconds * 1000);
            }
            else if (_bandageEnd != 0 && Array.IndexOf(BandageEnds, cliloc) >= 0)
            {
                _bandageEnd = 0;
            }
        }

        /// <summary>Every frame (TouchInput.Update, in the world).</summary>
        public static void Update(World world)
        {
            string text = null;

            if (Bandaging)
            {
                int left = (int)Math.Ceiling(Math.Max(0, (int)(_bandageEnd - Time.Ticks)) / 1000.0);
                text = left > 0 ? $"Bandage {left}s" : "Bandage...";
            }
            else
            {
                _bandageEnd = 0;
            }

            if (text == null)
            {
                if (_gump != null)
                {
                    _gump.Dispose();
                    _gump = null;
                }

                return;
            }

            if (_gump == null || _gump.IsDisposed)
            {
                UIManager.Add(_gump = new TimersGump(world));
            }

            _gump.Show(text);
        }
    }

    /// <summary>The strip of running timers, under the counter bar.</summary>
    internal sealed class TimersGump : Gump
    {
        private readonly Label _label;
        private readonly AlphaBlendControl _back;

        public TimersGump(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = false;
            CanCloseWithRightClick = false;
            CanCloseWithEsc = false;

            Add(_back = new AlphaBlendControl(0.6f) { Width = 120, Height = 20 });
            Add(_label = new Label("", true, 0x0035, 0, 1) { X = 6, Y = 2 });
        }

        public void Show(string text)
        {
            if (_label.Text != text)
            {
                _label.Text = text;
                _back.Width = Width = _label.Width + 12;
                Height = 20;
            }

            CounterBarGump bar = UIManager.GetGump<CounterBarGump>();

            if (bar != null && bar.IsVisible)
            {
                X = bar.X;
                Y = bar.Y + bar.Height + 2;
            }
            else
            {
                Rectangle safe = TouchInput.Safe;
                X = safe.X + 6;
                Y = safe.Y + (int)(safe.Height * 0.2f);
            }

            Y = Math.Min(Y, TouchInput.ScreenH - Height - 4); // a strip moved low stays on the screen
        }
    }
}
