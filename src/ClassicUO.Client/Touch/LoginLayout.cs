// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.Gumps.CharCreation;
using ClassicUO.Game.UI.Gumps.Login;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// The login, server and character screens on a phone, in landscape and portrait. Each is one
    /// 640x480 picture drawn from (0,0). Here the UI is scaled so the part of the picture a screen
    /// needs fits inside the safe area (clear of the notch, Dynamic Island and home indicator) with a
    /// margin, and every login window (UO's framed backdrop too) is moved to centre it there; the rest
    /// of the screen keeps the client's dark background. Landscape fits the whole picture. Portrait fits just the screen's middle (its
    /// sides are empty art), so the text and buttons come out bigger; on the account screen Quit and
    /// Credits, which sit out on the picture's sides, move below the chest. Music and art are UO's own.
    /// Runs on the phone screen-fit path (iOS; UOM_PHONE_FIT=1 with -touch on desktop).
    /// </summary>
    internal static class LoginLayout
    {
        /// <summary>Space kept between the picture and the safe area's edge, as a share of the short side.</summary>
        private const float Margin = 0.04f;

        private static readonly Rectangle WholePicture = new Rectangle(0, 0, 640, 480);

        // where each login window and moved control stood in the picture (they're moved from there)
        private static readonly Dictionary<Control, Point> _home = new Dictionary<Control, Point>();
        private static readonly Dictionary<Control, int> _homeHeight = new Dictionary<Control, int>();
        private static string _fitted;

        public static bool Active => GameController.PhoneFit && TouchInput.Enabled && Client.Game.Scene is LoginScene;

        /// <summary>GameController.FitUiToPhone: the UI scale that fits the current screen's picture inside the safe area.</summary>
        public static float FitScale(int backbufferW, int backbufferH, float display)
        {
            (float sx, float sy, float sw, float sh) = TouchInput.SafeFractions();
            Rectangle c = Content(Current, backbufferH > backbufferW);
            float margin = Margin * Math.Min(backbufferW, backbufferH);
            float w = sw * backbufferW - 2 * margin, h = sh * backbufferH - 2 * margin;

            return Math.Min(w / c.Width, h / c.Height) / display;
        }

        /// <summary>Every frame (TouchInput.Update): refit when the screen, safe area or login step changes; place the windows.</summary>
        public static void Update()
        {
            if (!Active)
            {
                if (_home.Count > 0 || _homeHeight.Count > 0)
                {
                    _home.Clear();
                    _homeHeight.Clear();
                }

                _fitted = null;

                return;
            }

            Gump current = Current;
            bool portrait = TouchInput.IsPortrait;
            (float sx, float sy, float sw, float sh) = TouchInput.SafeFractions();
            string key = $"{Client.Game.GraphicManager.PreferredBackBufferWidth}x{Client.Game.GraphicManager.PreferredBackBufferHeight} {sx:F3},{sy:F3},{sw:F3},{sh:F3} {current?.GetType().Name}";

            if (key != _fitted)
            {
                _fitted = key;
                Client.Game.FillScreenOnPhone(); // the UI scale for this screen's picture
                portrait = TouchInput.IsPortrait;
                Log.Info($"[UOMobile] login layout {key} {(portrait ? "portrait" : "landscape")}, UI {TouchInput.ScreenW}x{TouchInput.ScreenH}, safe {TouchInput.Safe}");
            }

            Rectangle content = Content(current, portrait);
            Rectangle safe = TouchInput.Safe;
            int offX = safe.X + (safe.Width - content.Width) / 2 - content.X;
            int offY = safe.Y + (safe.Height - content.Height) / 2 - content.Y;

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed)
                {
                    continue;
                }

                // (UO's framed backdrop too: it is a 640x480 frame, so it stays behind the picture)
                Point home = Home(g);
                Place(g, home.X + offX, home.Y + offY);

                if (g is LoginGump)
                {
                    AccountScreen(g, portrait);
                }
            }

            // forget windows that closed
            if (_home.Count > 64)
            {
                List<Control> gone = new List<Control>();

                foreach (Control c in _home.Keys)
                {
                    if (c.IsDisposed)
                    {
                        gone.Add(c);
                    }
                }

                gone.ForEach(c =>
                {
                    _home.Remove(c);
                    _homeHeight.Remove(c);
                });
            }
        }

        private static Gump Current => (Client.Game.Scene as LoginScene)?.CurrentGump;

        /// <summary>The part of the 640x480 picture a screen needs (portrait leaves out its empty sides).</summary>
        private static Rectangle Content(Gump g, bool portrait)
        {
            if (!portrait)
            {
                return WholePicture;
            }

            switch (g)
            {
                case LoginGump _: return new Rectangle(110, 0, 440, 575); // the chest, and Quit / Credits moved below it
                case ServerSelectionGump _: return new Rectangle(140, 50, 500, 430); // the list, sort row and arrows
                case CharacterSelectionGump _: return new Rectangle(150, 60, 490, 420); // the list, New / Delete and arrows
                case LoadingGump _: return new Rectangle(130, 120, 390, 240); // the message box
                default: return WholePicture; // character creation and the rest use the whole picture
            }
        }

        /// <summary>The account screen in portrait: Quit and Credits (out on the picture's sides) below the chest, the links inside it.</summary>
        private static void AccountScreen(Gump g, bool portrait)
        {
            // taps reach only controls inside the window: in portrait it reaches down to Quit and Credits
            if (!_homeHeight.TryGetValue(g, out int height))
            {
                _homeHeight[g] = height = g.Height;
            }

            g.Height = portrait ? Math.Max(height, 575) : height;

            // the credits cover the picture, not what was moved below it
            bool covered = UIManager.GetGump<CreditsGump>() != null;

            foreach (Control c in g.Children)
            {
                Point home = Home(c);
                Point at = home;

                if (portrait)
                {
                    if (c is Button b && b.ButtonID == 1) // Quit
                    {
                        at = new Point(160, 492);
                        c.IsVisible = !covered;
                    }
                    else if (c is Button b2 && b2.ButtonID == 2) // Credits
                    {
                        at = new Point(370, 508);
                        c.IsVisible = !covered;
                    }
                    else if (c is HtmlControl && home.X > 500) // Website / Join Discord
                    {
                        at = new Point(470, home.Y);
                    }
                }
                else if (c is Button b3 && (b3.ButtonID == 1 || b3.ButtonID == 2) && !c.IsVisible)
                {
                    c.IsVisible = true; // back in the picture
                }

                if (c.X != at.X || c.Y != at.Y)
                {
                    c.X = at.X;
                    c.Y = at.Y;
                }
            }
        }

        private static Point Home(Control c)
        {
            if (!_home.TryGetValue(c, out Point p))
            {
                _home[c] = p = new Point(c.X, c.Y);
            }

            return p;
        }

        private static void Place(Control c, int x, int y)
        {
            if (c.X != x || c.Y != y)
            {
                c.X = x;
                c.Y = y;
            }
        }
    }
}
