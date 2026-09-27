// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Touch
{
    /// <summary>
    /// The screen shown while <see cref="GameFiles"/> checks, adopts or downloads the game files,
    /// before anything is loaded from them (GameController runs it in place of UO.Load). It draws
    /// with the renderer's embedded font only; no UO art exists yet. One button: "Download" to
    /// agree to a large download, "Retry" after a failure.
    /// </summary>
    internal sealed class DownloadScreen
    {
        private static readonly Color Back = new Color(14, 12, 18);
        private static readonly Color BarBack = new Color(52, 48, 58);
        private static readonly Color BarFill = new Color(196, 160, 72);
        private static readonly Color ButtonFill = new Color(120, 28, 28);
        private static readonly Color ButtonEdge = new Color(214, 190, 130);

        private readonly GameFiles _files;
        private Rectangle _button; // canvas units; empty when no button is shown
        private float _uiToCanvas = 1f;

        private uint _rateTick;
        private long _rateBytes;
        private double _rate; // bytes per second, smoothed

        public DownloadScreen(GameFiles files)
        {
            _files = files;
            _files.Start();
        }

        public bool Done => _files.State == GameFiles.Phase.Done;

        public string UoPathOverride => _files.UoPathOverride;

        public void Update()
        {
            uint now = Time.Ticks;

            if (_rateTick == 0)
            {
                _rateTick = now;
                _rateBytes = _files.DoneBytes;
            }
            else if (now - _rateTick >= 1000)
            {
                long done = _files.DoneBytes;
                double r = Math.Max(0, done - _rateBytes) * 1000.0 / (now - _rateTick);
                _rate = _rate <= 0 ? r : _rate * 0.7 + r * 0.3;
                _rateBytes = done;
                _rateTick = now;
            }
        }

        /// <summary>A tap, in UI units (as the touch layer reports them).</summary>
        public void OnTap(Point ui)
        {
            var p = new Point((int)(ui.X * _uiToCanvas), (int)(ui.Y * _uiToCanvas));

            if (_button.IsEmpty || !_button.Contains(p))
            {
                return;
            }

            switch (_files.State)
            {
                case GameFiles.Phase.AskToDownload:
                    _files.Consent();

                    break;

                case GameFiles.Phase.Failed:
                    _rate = 0;
                    _rateTick = 0;
                    _files.Retry();

                    break;
            }
        }

        /// <summary>Draws straight to the backbuffer; <paramref name="dpiScale"/> maps UI units to its pixels.</summary>
        public void Draw(UltimaBatcher2D batcher, int backbufferW, int backbufferH, float dpiScale)
        {
            // A canvas about 320 units on its short side, whatever the screen.
            float k = Math.Max(0.5f, Math.Min(backbufferW, backbufferH) / 320f);
            _uiToCanvas = dpiScale / k;
            int w = (int)(backbufferW / k), h = (int)(backbufferH / k);
            Vector3 hue = ShaderHueTranslator.GetHueVector(0);
            Vector3 dim = ShaderHueTranslator.GetHueVector(0, false, 0.7f);
            GameFiles.Phase phase = _files.State;

            batcher.Begin(null, Matrix.CreateScale(k, k, 1f));
            batcher.Draw(SolidColorTextureCache.GetTexture(Back), new Rectangle(0, 0, w, h), hue, 0f);

            int y = (int)(h * 0.40f);
            y = DrawCentered(batcher, Fonts.Bold, _files.Status, w, y, hue) + 6;
            y = DrawCentered(batcher, Fonts.Regular, _files.Detail, w, y, dim) + 10;

            if (phase == GameFiles.Phase.Downloading || phase == GameFiles.Phase.Adopting)
            {
                long total = Math.Max(1, _files.TotalBytes), done = Math.Min(_files.DoneBytes, total);
                var bar = new Rectangle((int)(w * 0.15f), Math.Max(y, (int)(h * 0.58f)), (int)(w * 0.7f), 8);
                batcher.Draw(SolidColorTextureCache.GetTexture(BarBack), bar, hue, 0f);

                if (phase == GameFiles.Phase.Downloading)
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(BarFill), new Rectangle(bar.X, bar.Y, (int)(bar.Width * (done / (double)total)), bar.Height), hue, 0f);

                    string line = $"{GameFiles.Size(done)} of {GameFiles.Size(total)}";

                    if (_rate > 1024)
                    {
                        double seconds = (total - done) / _rate;
                        line += $"  -  {GameFiles.Size((long)_rate)}/s  -  about {Eta(seconds)} left";
                    }

                    DrawCentered(batcher, Fonts.Regular, line, w, bar.Bottom + 8, dim);
                }
            }

            _button = Rectangle.Empty;
            string label = phase == GameFiles.Phase.AskToDownload ? "Download"
                         : phase == GameFiles.Phase.Failed ? "Retry"
                         : null;

            if (label != null)
            {
                _button = new Rectangle(w / 2 - 80, (int)(h * 0.74f), 160, 34);
                batcher.Draw(SolidColorTextureCache.GetTexture(ButtonEdge), _button, hue, 0f);
                batcher.Draw(SolidColorTextureCache.GetTexture(ButtonFill), new Rectangle(_button.X + 2, _button.Y + 2, _button.Width - 4, _button.Height - 4), hue, 0f);
                Vector2 size = Fonts.Bold.MeasureString(label);
                batcher.DrawString(Fonts.Bold, label, new Vector2(_button.Center.X - size.X / 2, _button.Center.Y - size.Y / 2), hue, 0f);
            }

            batcher.End();

            // Title, larger.
            batcher.Begin(null, Matrix.CreateScale(k * 1.8f, k * 1.8f, 1f));
            DrawCentered(batcher, Fonts.Bold, "Graveyard Battles", (int)(w / 1.8f), (int)(h * 0.16f / 1.8f), hue);
            batcher.End();
        }

        private static string Eta(double seconds) =>
            seconds >= 5400 ? $"{seconds / 3600:0.0} hours" :
            seconds >= 90 ? $"{Math.Ceiling(seconds / 60)} min" :
            $"{Math.Max(1, (int)seconds)} s";

        /// <summary>Word-wrapped, centred text; returns the y below it.</summary>
        private static int DrawCentered(UltimaBatcher2D batcher, SpriteFont font, string text, int width, int y, Vector3 hue)
        {
            if (string.IsNullOrEmpty(text))
            {
                return y;
            }

            foreach (string line in Wrap(font, text, width * 0.9f))
            {
                Vector2 size = font.MeasureString(line);
                batcher.DrawString(font, line, new Vector2((width - size.X) / 2f, y), hue, 0f);
                y += (int)size.Y + 2;
            }

            return y;
        }

        private static IEnumerable<string> Wrap(SpriteFont font, string text, float maxWidth)
        {
            string line = "";

            foreach (string word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;

                if (line.Length > 0 && font.MeasureString(candidate).X > maxWidth)
                {
                    yield return line;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}
