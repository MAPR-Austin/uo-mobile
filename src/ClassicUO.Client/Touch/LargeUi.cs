// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Build 55 (owner: "menus in game are still hard to read and navigate"): the phone's large menus.
    /// <list type="bullet">
    /// <item>Size: server menus and the stock windows people read (shop lists, skills, spellbook, status, journal,
    /// paperdoll, old-style menus, text prompts) open zoomed to a readable size, fitted to the screen in either
    /// orientation (<see cref="AutoScale"/>). A pinch still sets a window's own zoom, which then wins.</item>
    /// <item>Fit: every zoomed window is kept on the screen; one taller than the screen can be dragged up and down
    /// to reach its bottom (<see cref="KeepOnScreen"/>).</item>
    /// <item>Taps: on a server menu, a tap on a button's label, on the plate a button sits on, or just beside a small
    /// button presses that button (<see cref="SnapTarget"/>): UO's 30 x 22 buttons are a fraction of a fingertip.</item>
    /// <item>Close: a server menu that can be closed but has no close button (reply 0) gets a big X
    /// (<see cref="Adapt"/>); before, only a long press (a right click) closed it.</item>
    /// </list>
    /// Off with UOM_LARGE_UI=0 (desktop comparison); old servers are unaffected beyond the zoom.
    /// </summary>
    internal static class LargeUi
    {
        /// <summary>A server menu's zoom: its text at ~16 pt on a 6" phone, a 400-wide menu within half a sideways screen.</summary>
        public const float SERVER = 1.3f;

        /// <summary>Margin kept free round a fitted window (UI units).</summary>
        private const int MARGIN = 6;

        /// <summary>The least a window is shrunk to fit the screen's height; taller than that, it scrolls (is dragged).</summary>
        private const float MIN_FIT_HEIGHT = 0.8f;

        /// <summary>How far (logical units) round a small server button a tap still counts as on it.</summary>
        private const int PAD = 10;

        /// <summary>How far (logical units) right of a button its label may start.</summary>
        private const int LABEL_GAP = 48;

        /// <summary>
        /// The tallest text that still counts as a button's label (two lines), and the tallest plate that counts as a
        /// row's: a block of description beside a button, or a big panel, is not that button.
        /// </summary>
        private const int MAX_LABEL_H = 44, MAX_PLATE_H = 100;

        public static bool On => TouchInput.Enabled && !_off;

        private static readonly bool _off = Environment.GetEnvironmentVariable("UOM_LARGE_UI") == "0";

        /// <summary>A plain server gump (not one of the client's own kinds built from a server packet).</summary>
        public static bool IsServerMenu(Gump g) => g != null && g.ServerSerial != 0 && g.GetType() == typeof(Gump);

        /// <summary>The zoom a kind of window opens at on a phone; 0 = not auto-sized (it keeps 1, as before).</summary>
        private static float Target(Gump g)
        {
            switch (g)
            {
                case ShopGump:
                case MenuGump:
                case GrayMenuGump:
                case TextEntryDialogGump:
                case QuestionGump:
                    return SERVER;
                case StandardSkillsGump:
                case SkillGumpAdvanced:
                    return 1.35f;
                case SpellbookGump:
                    return 1.3f;
                case StatusGumpBase:
                    return 1.3f;
                case JournalGump:
                case TradingGump:
                    return 1.2f;
                case PaperDollGump:
                    return 1.15f;
            }

            // (not UO's options: its drop-down lists open at unzoomed spots, so upright it still widens the UI instead)

            return IsServerMenu(g) ? SERVER : 0f;
        }

        /// <summary>The window is sized by this mode (its scale isn't simply 1 when nothing was pinched).</summary>
        public static bool Sizes(Gump g) => On && g != null && Target(g) > 0f;

        /// <summary>
        /// Build 56: the least a server menu is shrunk to fit the screen's width. <see cref="GumpScale.MIN"/> (0.6) let a
        /// menu wider than ~780 run off an upright screen with its X; at 0.4 one up to ~1170 wide fits.
        /// </summary>
        public const float MENU_MIN = 0.4f;

        /// <summary>
        /// Build 56: a server menu's zoom, kept per kind of menu (its gump id) while the screen keeps its size and the
        /// menu stays up. A paged menu is rebuilt by the server on Prev / Next, often a little taller or shorter: refitted
        /// every page it visibly changed size. Now a page that needs less room keeps the zoom; one that needs more
        /// shrinks it once. A menu of that kind gone for <see cref="MENU_ZOOM_FORGET_MS"/> (closed, not paged) is
        /// fitted afresh when it opens again.
        /// </summary>
        private struct MenuZoom
        {
            public int ScreenW, ScreenH;
            public float Scale;
            public uint Seen;
        }

        private const uint MENU_ZOOM_FORGET_MS = 3000;

        private static readonly System.Collections.Generic.Dictionary<uint, MenuZoom> _menuZoom = new System.Collections.Generic.Dictionary<uint, MenuZoom>();

        /// <summary>Logout: the next login (or shard) fits its menus afresh.</summary>
        public static void Reset() => _menuZoom.Clear();

        /// <summary>
        /// The window's zoom when nothing was pinched: its kind's target, made smaller to fit the screen's width, and
        /// its height down to <see cref="MIN_FIT_HEIGHT"/> (below that it is dragged instead). 1 when off. A server menu
        /// keeps one zoom across its pages (<see cref="MenuZoom"/>).
        /// </summary>
        public static float AutoScale(Gump g)
        {
            float target = On && g != null ? Target(g) : 0f;

            if (target <= 0f)
            {
                return 1f;
            }

            float s = Fit(g, target);

            if (!IsServerMenu(g) || g.IsDisposed || g.Width <= 1 || g.Height <= 1)
            {
                return s;
            }

            int sw = TouchInput.ScreenW, sh = TouchInput.ScreenH;
            uint now = Time.Ticks;

            if (_menuZoom.TryGetValue(g.ServerSerial, out MenuZoom z) && z.ScreenW == sw && z.ScreenH == sh && z.Scale <= s &&
                now - z.Seen < MENU_ZOOM_FORGET_MS)
            {
                s = z.Scale; // (a page that would fit larger keeps the menu's size)
            }

            // first page, a turned screen, a bigger page, or a menu opened again; and "still up" (asked every frame it's drawn)
            _menuZoom[g.ServerSerial] = new MenuZoom { ScreenW = sw, ScreenH = sh, Scale = s, Seen = now };

            return s;
        }

        /// <summary>
        /// A zoom someone pinched, kept within the screen the same way when this mode sizes the window (a menu
        /// pinched to 1.3 on a sideways phone must not run off an upright one).
        /// </summary>
        public static float FitScale(Gump g, float pinched) => Sizes(g) ? Math.Min(pinched, Fit(g, GumpScale.MAX)) : pinched;

        private static float Fit(Gump g, float target)
        {
            int w = Math.Max(1, g.Width), h = Math.Max(1, g.Height);
            float sw = TouchInput.ScreenW - 2 * MARGIN, sh = TouchInput.ScreenH - 2 * MARGIN;
            float min = IsServerMenu(g) ? MENU_MIN : GumpScale.MIN; // (build 56: a wide server menu fits upright too)

            if (sw <= 0 || sh <= 0)
            {
                return Math.Clamp(target, GumpScale.MIN, GumpScale.MAX);
            }

            float s = Math.Min(target, sw / w);

            if (h * s > sh)
            {
                s = Math.Min(s, Math.Max(sh / h, Math.Min(MIN_FIT_HEIGHT, s)));
            }

            return Math.Clamp(s, min, GumpScale.MAX);
        }

        // ------------------------------------------------------------------ keeping windows on the screen

        /// <summary>
        /// Every frame: a zoomed window is pulled back inside the screen by its drawn size (a menu opened at the
        /// server's spot would otherwise run off the edge once zoomed). Taller or wider than the screen: it may
        /// sit anywhere that keeps the screen covered, so a drag scrolls it. Not while a window is dragged or (build 56)
        /// pinched: a pinch moves the window with the fingers, and clamping it each frame made it jump about under them
        /// (the pinch's end pulls it back on screen).
        /// </summary>
        public static void KeepOnScreen()
        {
            if (!On || UIManager.IsDragging || TouchInput.PinchingWindow || !(Client.Game.Scene is Game.Scenes.GameScene) || LoginLayout.Active)
            {
                return;
            }

            int sw = TouchInput.ScreenW, sh = TouchInput.ScreenH;

            if (sw <= 0 || sh <= 0)
            {
                return;
            }

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || !g.IsVisible || !GumpScale.Scalable(g) || g is Game.UI.Gumps.WorldViewportGump || g.Width <= 1)
                {
                    continue;
                }

                float s = GumpScale.Of(g);

                if (s == 1f && !Sizes(g))
                {
                    continue; // (as before: only a rotation pulls those back)
                }

                Fit(g, s, sw, sh);
            }
        }

        public static void Fit(Gump g, float s, int sw, int sh)
        {
            int w = (int)(g.Width * s), h = (int)(g.Height * s);
            int x = w <= sw ? Math.Clamp(g.X, 0, sw - w) : Math.Clamp(g.X, sw - w, 0);
            int y = h <= sh ? Math.Clamp(g.Y, 0, sh - h) : Math.Clamp(g.Y, sh - h, 0);

            if (x != g.X || y != g.Y)
            {
                g.X = x;
                g.Y = y;
            }
        }

        // ------------------------------------------------------------------ a close button for every server menu

        /// <summary>
        /// A server menu just built from its packet (and each time it is rebuilt): a big X at its top right when it
        /// can be closed but has no close button of its own. The X sends reply 0, as a right click does.
        /// </summary>
        public static void Adapt(Gump g)
        {
            if (!On || g == null || !g.CanCloseWithRightClick || !IsServerMenu(g))
            {
                return;
            }

            int right = 0;

            foreach (Control c in g.Children)
            {
                if (c is Button b && b.ButtonAction == ButtonAction.Activate && b.ButtonID == 0)
                {
                    return; // it has one
                }

                if (c.IsVisible)
                {
                    right = Math.Max(right, c.X + c.Width);
                }
            }

            g.Add(new CloseX(right + 2, 0));
        }

        /// <summary>The big X a server menu without a close button gets (outside its top-right corner).</summary>
        internal sealed class CloseX : PlateButton
        {
            public const int SIZE = 40;

            public CloseX(int x, int y)
                : base(x, y, SIZE, SIZE, "", 0)
            {
                Fill = new Color(96, 30, 30);
                Add(new GumpPic((SIZE - 30) / 2, (SIZE - 22) / 2, 0x0FB1, 0) { AcceptMouseInput = false }); // UO's own X (4017)
            }

            public override void OnButtonClick(int buttonID)
            {
                if (RootParent is Gump g && !g.IsDisposed)
                {
                    g.OnButtonClick(0); // reply 0 and close, as a right click
                }
            }
        }

        // ------------------------------------------------------------------ bigger taps on server menus

        /// <summary>
        /// Where a press at <paramref name="physical"/> (UI units) should really land: the centre of the server
        /// menu's button it means, or null to leave it alone. Only when the finger is on a server menu and not on
        /// anything that takes input itself. In order: just beside a small button or check box (within
        /// <see cref="PAD"/>); on a label that starts just right of a button on the same line (UO's "button, then
        /// its text" rows); on a plate (a tiled image or panel smaller than the menu) holding exactly one button.
        /// </summary>
        public static Point? SnapTarget(Point physical)
        {
            if (!On)
            {
                return null;
            }

            Gump g = UIManager.GumpAtPhysical(physical, out Point p);
            Point? centre = SnapLogical(g, p);

            if (!centre.HasValue)
            {
                return null;
            }

            float s = GumpScale.Of(g);
            Point c = centre.Value;

            return s == 1f ? c : new Point(g.X + (int)MathF.Round((c.X - g.X) * s), g.Y + (int)MathF.Round((c.Y - g.Y) * s));
        }

        /// <summary>
        /// <see cref="SnapTarget"/> in the menu's own (unzoomed) layout: <paramref name="p"/> is a point there, and so is
        /// the answer. Build 56: the lift asks this with the press's own mapping, so a menu the server rebuilt (or that was
        /// refitted) under the finger is read the way the finger's press will be.
        /// </summary>
        public static Point? SnapLogical(Gump g, Point p)
        {
            if (!On || !IsServerMenu(g) || g.IsDisposed || !g.IsVisible)
            {
                return null;
            }

            Control hit = null;
            g.HitTest(p, ref hit);

            if (hit == null || TakesInput(hit))
            {
                return null;
            }

            Control best = null;
            int bestD = int.MaxValue;

            // 1. just beside a button or box
            foreach (Control c in g.Children)
            {
                if (!Candidate(g, c))
                {
                    continue;
                }

                Rectangle r = Rect(g, c);
                Rectangle pad = r;
                pad.Inflate(PAD, PAD);

                if (pad.Contains(p))
                {
                    int dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right)), dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
                    int d = dx * dx + dy * dy;

                    if (d < bestD)
                    {
                        best = c;
                        bestD = d;
                    }
                }
            }

            // 2. a button's label
            if (best == null)
            {
                foreach (Control t in g.Children)
                {
                    // (build 56: not HTML with a link - a tap on the link opens it, it doesn't press the button beside it)
                    if (!OnPage(g, t) || !(t is Label || t is HtmlControl html && !html.HasLinks || t is CroppedText) || t.Height > MAX_LABEL_H ||
                        !Rect(g, t).Contains(p))
                    {
                        continue;
                    }

                    Rectangle tr = Rect(g, t);

                    foreach (Control c in g.Children)
                    {
                        if (!Candidate(g, c))
                        {
                            continue;
                        }

                        Rectangle r = Rect(g, c);
                        int gap = tr.Left - r.Right;
                        int cy = r.Center.Y;

                        if (gap >= -4 && gap <= LABEL_GAP && (cy >= tr.Top - 6 && cy <= tr.Bottom + 6 || tr.Center.Y >= r.Top && tr.Center.Y <= r.Bottom) &&
                            gap < bestD)
                        {
                            best = c;
                            bestD = gap;
                        }
                    }
                }
            }

            // 3. the plate a button sits on
            if (best == null)
            {
                Rectangle plate = Rectangle.Empty;
                long area = (long)g.Width * g.Height * 6 / 10;

                foreach (Control c in g.Children)
                {
                    if (!OnPage(g, c) || !(c is GumpPicTiled || c is ResizePic || c is AlphaBlendControl || c is CheckerTrans || c is ColorBox))
                    {
                        continue;
                    }

                    Rectangle r = Rect(g, c);

                    if (r.Contains(p) && (long)r.Width * r.Height < area && r.Height <= MAX_PLATE_H && (plate.IsEmpty || r.Width * r.Height < plate.Width * plate.Height))
                    {
                        plate = r;
                    }
                }

                if (!plate.IsEmpty)
                {
                    int n = 0;

                    foreach (Control c in g.Children)
                    {
                        if (Candidate(g, c) && plate.Contains(Rect(g, c).Center))
                        {
                            best = c;
                            n++;
                        }
                    }

                    if (n != 1)
                    {
                        best = null;
                    }
                }
            }

            if (best == null)
            {
                return null;
            }

            return Rect(g, best).Center;
        }

        private static bool OnPage(Gump g, Control c) => c.IsVisible && !c.IsDisposed && (c.Page == 0 || c.Page == g.ActivePage);

        private static bool Candidate(Gump g, Control c) =>
            OnPage(g, c) && c.IsEnabled && (c is Button || c is Checkbox || c is StbTextBox box && box.IsEditable) && c.Width > 0 && c.Height > 0;

        private static Rectangle Rect(Gump g, Control c) => new Rectangle(g.X + c.X, g.Y + c.Y, c.Width, c.Height);

        /// <summary>Something the finger works directly: a button, box, text field, scroll bar or scrolling text.</summary>
        private static bool TakesInput(Control c)
        {
            for (Control x = c; x != null; x = x.Parent)
            {
                if (x is Button || x is Checkbox || x is StbTextBox || x is ScrollBarBase || x is ScrollArea || x is HitBox ||
                    x is HtmlControl html && (html.HasScrollbar || html.HasLinks)) // (build 56: a link is the finger's own)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
