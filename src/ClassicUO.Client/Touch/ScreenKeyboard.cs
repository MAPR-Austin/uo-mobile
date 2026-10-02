// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Globalization;
using ClassicUO.Utility.Logging;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Typing on the speech line with the phone's keyboard. SDL slides the whole view up until the
    /// text box clears the keyboard, which pushed the character and what people say off the top of
    /// the screen. For the speech line the view now stays put: the line rises to sit just above the
    /// keyboard and the camera eases up so the character is in the middle of what the keyboard leaves
    /// in sight - both moving with the keyboard's own slide, from its height and timing as iOS reports
    /// them (ios/Program.cs calls <see cref="OnFrame" /> on every keyboard frame change). Other text
    /// boxes keep SDL's slide, as does the speech line if no host reports the keyboard.
    /// Desktop test: UOM_FAKE_KEYBOARD=0.5 (with -touch) fakes a keyboard covering half the screen
    /// whenever the speech line has it.
    /// </summary>
    public static class ScreenKeyboard
    {
        private static readonly float Fake = float.TryParse(Environment.GetEnvironmentVariable("UOM_FAKE_KEYBOARD"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? Math.Clamp(f, 0f, 0.9f) : 0f;

        // the keyboard's cover (share of the screen height), animated from _from to _to like the keyboard
        private static float _from, _to, _now;
        private static uint _start;
        private static double _durationMs;

        // the keyboard is (or was last) up for the speech line: only then does the world make room
        private static bool _forChat;
        private static bool _fakeUp;

        // no report of the keyboard arrived for the speech line: it goes back to SDL's slide
        private const uint ReportWaitMs = 700;
        private static uint _startedAt;
        private static bool _reported, _fellBack;
        private static int _logged;

        /// <summary>The host reports the keyboard's frame (set by iOS at startup): the speech line can then skip SDL's slide.</summary>
        public static bool HostReports { get; set; }

        /// <summary>
        /// The host's report of a keyboard frame change (iOS: UIKeyboardWillChangeFrame, at the start of
        /// the keyboard's slide): the share of the screen height it will cover, and how long it takes.
        /// </summary>
        public static void OnFrame(float covered, double seconds)
        {
            if (covered > 0f)
            {
                _reported = true;
            }

            if (_logged < 6) // the phone's log shows the reports arrive
            {
                _logged++;
                Log.Info($"[UOMobile] keyboard covers {covered:F3} of the screen, slide {seconds:F2} s");
            }

            _from = _now;
            _to = Math.Clamp(covered, 0f, 0.9f);
            _start = Time.Ticks;
            _durationMs = Math.Clamp(seconds, 0.0, 1.0) * 1000.0;
        }

        internal static bool IsSpeechLine(Control box)
        {
            return box != null && UIManager.SystemChat != null && !UIManager.SystemChat.IsDisposed && box == UIManager.SystemChat.TextBoxControl;
        }

        /// <summary>TouchInput is starting the keyboard for box: the view stays put for the speech line when the host reports the keyboard.</summary>
        internal static bool KeepsViewFor(Control box)
        {
            return (HostReports || Fake > 0) && !_fellBack && IsSpeechLine(box);
        }

        internal static void Starting(Control box)
        {
            _forChat = KeepsViewFor(box);
            _startedAt = Time.Ticks;
            _reported = Fake > 0; // (the fake keyboard reports itself)
        }

        /// <summary>
        /// The keyboard is up for the speech line but no report of it arrived: the line would sit under the
        /// keyboard. True once (TouchInput then gives SDL the line's real area, so SDL slides the view as
        /// before); the speech line keeps SDL's slide for the rest of the session.
        /// </summary>
        internal static bool NeedsFallback(bool keyboardActive)
        {
            if (!_forChat || _reported || _fellBack || !keyboardActive || Time.Ticks - _startedAt < ReportWaitMs)
            {
                return false;
            }

            _fellBack = true;
            _forChat = false;
            Log.Warn("[UOMobile] no keyboard report from the host: the speech line falls back to SDL's slide");

            return true;
        }

        /// <summary>Desktop (no on-screen keyboard): with UOM_FAKE_KEYBOARD, a pretend keyboard for the speech line.</summary>
        internal static void FakeFor(bool wanted, Control box)
        {
            if (Fake <= 0)
            {
                return;
            }

            if (wanted && !_fakeUp && IsSpeechLine(box))
            {
                _fakeUp = true;
                _forChat = true;
                OnFrame(Fake, 0.25);
            }
            else if (!wanted && _fakeUp)
            {
                _fakeUp = false;
                OnFrame(0f, 0.25);
            }
        }

        /// <summary>Every frame (TouchInput.Update): follow the keyboard's slide; raise the speech line and the camera with it.</summary>
        internal static void Update()
        {
            float t = _durationMs <= 0 ? 1f : (float)Math.Clamp((Time.Ticks - _start) / _durationMs, 0.0, 1.0);
            float eased = 1f - (1f - t) * (1f - t) * (1f - t); // ease out, close to the keyboard's own curve
            _now = _from + (_to - _from) * eased;

            if (!(Client.Game.Scene is GameScene scene) || UIManager.SystemChat == null || UIManager.SystemChat.IsDisposed)
            {
                return;
            }

            int lift = _forChat ? (int)Math.Round(_now * TouchInput.ScreenH) : 0;

            // the speech line (and the recent lines above it) just above the keyboard
            int chatHeight = scene.Camera.Bounds.Height - lift;
            SystemChatControl chat = UIManager.SystemChat;

            if (chat.Height != chatHeight && chatHeight > 0)
            {
                chat.Height = chatHeight;
                chat.Resize();
            }

            // the character in the middle of what's left in sight
            scene.Camera.ScreenShift = new Vector2(0f, -lift * 0.5f);
        }
    }
}
