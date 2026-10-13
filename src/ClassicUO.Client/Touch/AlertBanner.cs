// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Zombie Survival's alerts (iPhone build 53): a banner across the top of the screen with a sound - "The dead are bashing
    /// your camp's wooden palisade, east of the fire!", "A horde of 24 dead marches on Britain from the north-east!", a town
    /// fallen or held, a tripwire. The server (servuo Custom/Zombie/PhoneAlerts.cs) sends 0xBF 0x7A62: u8 kind, u16 hue,
    /// u16 sound (0: the kind's own), i16 x, i16 y, u8 facet (the spot it is about; 0,0 none), u16 length, the text
    /// (big-endian UTF-16) - only to an app that said hello (<see cref="ZombieMarkers" />), and the journal gets the line
    /// too. The banner takes no touches (it is drawn, not hit: fingers reach whatever is under it) and sits just below the
    /// HUD's top row (and, in portrait, the target panel and counter strip), clear of the buttons, in both orientations.
    /// One at a time, ~5.5 s each (3.5 s while more wait; at most 6 wait). Alerts reach a player who is logged in: a closed
    /// app is told nothing (true push notifications would need APNs and a push server).
    /// </summary>
    internal sealed class AlertBanner : Gump
    {
        private readonly struct Alert
        {
            public Alert(byte kind, ushort hue, ushort sound, string text)
            {
                Kind = kind;
                Hue = hue;
                Sound = sound;
                Text = text;
            }

            public readonly byte Kind;
            public readonly ushort Hue;
            public readonly ushort Sound;
            public readonly string Text;
        }

        private const int MaxQueued = 6;
        private const uint ShowMs = 5500, ShortShowMs = 3500, FadeInMs = 180, FadeOutMs = 350;
        private const int Pad = 8, Accent = 5, MaxWidth = 540, MinWidth = 280;

        private static readonly Queue<Alert> _queue = new Queue<Alert>();

        private bool _showing;
        private Alert _current;
        private uint _shownAt, _showFor;
        private RenderedText _text;
        private int _textWidth = -1;

        // where the banner went, and what that depended on: placed again only when something on screen moved, so it can't
        // flip between two spots from frame to frame
        private Rectangle _placed;
        private (int W, int H, Rectangle Safe, int Revision, bool Suppressed, Rectangle Panel, Rectangle Strip, string Text) _placedFor;

        private AlertBanner(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = false; // drawn, never hit: fingers go to what is under it
            CanCloseWithRightClick = false;
            CanCloseWithEsc = false;
            LayerOrder = UILayer.Over;
            X = 0;
            Y = 0;
            Width = 1; // (its size doesn't bound what it draws; small, so nothing measures it as a wide window)
            Height = 1;
        }

        public static void Ensure(World world)
        {
            AlertBanner banner = UIManager.GetGump<AlertBanner>();

            if (banner == null || banner.IsDisposed)
            {
                UIManager.Add(new AlertBanner(world));
            }
        }

        /// <summary>PacketHandlers (0xBF 0x7A62): an alert.</summary>
        public static void Read(World world, ref StackDataReader p)
        {
            byte kind = p.ReadUInt8();
            ushort hue = p.ReadUInt16BE();
            ushort sound = p.ReadUInt16BE();
            short x = (short)p.ReadUInt16BE();
            short y = (short)p.ReadUInt16BE();
            byte facet = p.ReadUInt8();
            int length = p.ReadUInt16BE();
            string text = length > 0 ? p.ReadUnicodeBE(Math.Min(length, 400)) : "";

            Show(world, kind, hue, sound, text);
            ZombieMarkers.NoteAlert(kind, x, y, facet);
        }

        /// <summary>Queue a banner (the oldest waiting one drops if too many wait).</summary>
        public static void Show(World world, byte kind, ushort hue, ushort sound, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            while (_queue.Count >= MaxQueued)
            {
                _queue.Dequeue();
            }

            _queue.Enqueue(new Alert(kind, hue, sound, text.Trim()));
            Ensure(world);
        }

        /// <summary>Logout (ZombieMarkers.Reset): nothing waits for the next login.</summary>
        public static void Reset()
        {
            _queue.Clear();
        }

        /// <summary>The server's sound for a kind when it sends none (servuo ZombieAlerts.Sound).</summary>
        private static ushort DefaultSound(byte kind)
        {
            switch (kind)
            {
                case 1: return 0x1D7; // camp attack: a zombie's moan
                case 2: return 0x2E7; // horde: war drums
                case 3: return 0x2E8; // town fallen
                case 4: return 0x5B5; // town held
                case 5: return 0x5B6; // town cleared
                case 6: return 0x5B3; // tripwire
                default: return 0x5B6;
            }
        }

        /// <summary>The banner's accent colour by kind (red for danger, green held, yellow cleared, orange tripwire).</summary>
        private static Color AccentColor(byte kind)
        {
            switch (kind)
            {
                case 1: return new Color(220, 40, 40, 255);
                case 2: return new Color(200, 30, 30, 255);
                case 3: return new Color(170, 30, 30, 255);
                case 4: return new Color(50, 180, 70, 255);
                case 5: return new Color(225, 195, 50, 255);
                case 6: return new Color(240, 130, 30, 255);
                default: return new Color(110, 160, 230, 255);
            }
        }

        private static readonly Color Fill = new Color(10, 10, 15, 215); // (premultiplied: the batcher blends that way)

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            ZombieMarkers.Update(World);

            uint now = Time.Ticks;

            if (_showing && now - _shownAt >= _showFor)
            {
                _showing = false;
            }

            if (!_showing && _queue.Count > 0 && World.InGame)
            {
                _current = _queue.Dequeue();
                _showing = true;
                _shownAt = now;
                _showFor = _queue.Count > 0 ? ShortShowMs : ShowMs;
                _textWidth = -1; // laid out on the next draw
                Client.Game.Audio?.PlaySound(_current.Sound != 0 ? _current.Sound : DefaultSound(_current.Kind));
            }
            else if (_showing && _queue.Count > 0 && _showFor > ShortShowMs && now - _shownAt < ShortShowMs)
            {
                _showFor = ShortShowMs; // more are waiting: this one gives way sooner
            }

            if (_showing && UIManager.Gumps.First?.Value != this)
            {
                UIManager.MakeTopMostGump(this); // over everything while it shows, the map too (never hit, so nothing loses a touch)
            }
        }

        /// <summary>
        /// Where the banner goes (UI units) for a banner about <paramref name="height" /> tall: just below the HUD's top row
        /// of buttons, in the widest stretch of that band no HUD button, the target panel or the counter strip reaches into
        /// (landscape: usually between the strip and the buttons on the right); where no stretch is wide enough (portrait,
        /// with the strip under the row), below whatever is in the way.
        /// </summary>
        private static Rectangle Place(int height)
        {
            Rectangle safe = TouchInput.Safe;
            int screenH = TouchInput.ScreenH;
            int top = safe.Y + 6;
            List<Rectangle> obstacles = new List<Rectangle>();
            ActionLayout layout = TouchInput.Enabled && !TouchInput.Suppressed ? TouchInput.Current : null;

            if (layout != null)
            {
                foreach (ActionButtonDef b in layout.Buttons)
                {
                    if (TouchInput.SteppedAside(b))
                    {
                        continue;
                    }

                    Point c = TouchInput.ButtonCenter(b);
                    int r = TouchInput.ButtonRadius(b);

                    // the top row as this orientation has it (as PhoneDefaults finds it for the counter strip): below it
                    float topY = TouchInput.IsPortrait ? layout.PortraitOf(b, TouchInput.Aspect, TouchInput.Revision).Y * TouchInput.Aspect : b.Y;

                    if (topY < 0.16f)
                    {
                        top = Math.Max(top, c.Y + r + 6);
                    }

                    obstacles.Add(new Rectangle(c.X - r, c.Y - r, 2 * r, 2 * r));
                }

                if (!TouchHudGump.TargetPanel.IsEmpty)
                {
                    obstacles.Add(TouchHudGump.TargetPanel);
                }

                if (UIManager.GetGump<CounterBarGump>() is CounterBarGump strip && strip.IsVisible && !strip.IsDisposed)
                {
                    float s = GumpScale.Of(strip);
                    obstacles.Add(new Rectangle(strip.X, strip.Y, (int)(strip.Width * s), (int)(strip.Height * s)));
                }
            }

            int widest = Math.Min(MaxWidth, safe.Width - 16);

            for (int attempt = 0; attempt < 4 && top + height < screenH * 0.6f; attempt++)
            {
                // the free stretches across the band [top, top + height + 6)
                List<(int L, int R)> free = new List<(int, int)> { (safe.X + 8, safe.Right - 8) };
                int lowest = top;

                foreach (Rectangle o in obstacles)
                {
                    if (o.Bottom <= top || o.Top >= top + height + 6)
                    {
                        continue;
                    }

                    lowest = Math.Max(lowest, o.Bottom + 6);
                    List<(int, int)> next = new List<(int, int)>();

                    foreach ((int l, int r) in free)
                    {
                        if (o.Right + 6 <= l || o.Left - 6 >= r)
                        {
                            next.Add((l, r));

                            continue;
                        }

                        if (o.Left - 6 > l)
                        {
                            next.Add((l, o.Left - 6));
                        }

                        if (o.Right + 6 < r)
                        {
                            next.Add((o.Right + 6, r));
                        }
                    }

                    free = next;
                }

                (int bl, int br) = (0, -1);

                foreach ((int l, int r) in free)
                {
                    if (r - l > br - bl)
                    {
                        (bl, br) = (l, r);
                    }
                }

                if (br - bl >= MinWidth)
                {
                    int w = Math.Min(widest, br - bl);

                    return new Rectangle(bl + (br - bl - w) / 2, top, w, height);
                }

                top = lowest; // nothing wide enough here: below what's in the way
            }

            return new Rectangle(safe.X + (safe.Width - widest) / 2, Math.Min(top, screenH - height - 8), widest, height);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (!_showing || IsDisposed || !(Client.Game.Scene is GameScene))
            {
                return false;
            }

            CounterBarGump strip = UIManager.GetGump<CounterBarGump>();
            var key = (TouchInput.ScreenW, TouchInput.ScreenH, TouchInput.Safe, TouchInput.Revision, TouchInput.Suppressed, TouchHudGump.TargetPanel,
                       strip != null && strip.IsVisible ? new Rectangle(strip.X, strip.Y, strip.Width, strip.Height) : Rectangle.Empty, _current.Text);

            if (_text == null || _text.IsDestroyed || _textWidth < 0 || !key.Equals(_placedFor))
            {
                // placed for a two-line banner and laid out for the width it got; placed once more if the text came out taller
                Rectangle r0 = Place(44);
                Layout(r0.Width);

                if (_text.Height + 2 * Pad > 44)
                {
                    Rectangle r1 = Place(_text.Height + 2 * Pad);

                    if (r1.Width != r0.Width)
                    {
                        Layout(r1.Width);
                    }

                    r0 = r1;
                }

                r0.Height = _text.Height + 2 * Pad;
                _placed = r0;
                _placedFor = key;
            }

            Rectangle r = _placed;
            uint t = Time.Ticks - _shownAt;
            long left = (long)_showFor - t; // (a frame drawn after its time, before Update ends it: faded out)
            float alpha = t < FadeInMs ? t / (float)FadeInMs : left < FadeOutMs ? Math.Clamp(left / (float)FadeOutMs, 0f, 1f) : 1f;
            int slide = t < FadeInMs ? (int)((1f - t / (float)FadeInMs) * 10) : 0;
            r.Y -= slide;

            float layerDepth = layerDepthRef;
            Vector3 hue = ShaderHueTranslator.GetHueVector(0, false, alpha);
            Color accent = AccentColor(_current.Kind);

            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(Fill), r, hue, layerDepth);
                    batcher.Draw(SolidColorTextureCache.GetTexture(accent), new Rectangle(r.X, r.Y, Accent, r.Height), hue, layerDepth);
                    batcher.Draw(SolidColorTextureCache.GetTexture(accent), new Rectangle(r.X, r.Bottom - 2, r.Width, 2), hue, layerDepth);

                    return true;
                }
            );

            int textX = r.X + Accent + (r.Width - Accent - _text.Width) / 2;
            renderLists.AddGumpNoAtlas(_text, textX, r.Y + Pad, layerDepth, alpha);

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        /// <summary>The text laid out for a banner <paramref name="width" /> wide.</summary>
        private void Layout(int width)
        {
            int maxTextWidth = width - 2 * Pad - Accent;

            if (_text == null || _text.IsDestroyed || _textWidth != maxTextWidth || _text.Text != _current.Text)
            {
                _text?.Destroy();
                _text = RenderedText.Create(_current.Text, 0x0481, 1, true, FontStyle.BlackBorder, TEXT_ALIGN_TYPE.TS_CENTER, maxTextWidth);
                _textWidth = maxTextWidth;
            }
        }

        public override void Dispose()
        {
            _text?.Destroy();
            _text = null;
            base.Dispose();
        }
    }
}
