using System;
using Android.Util;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace UOMobile.AndroidHello
{
    /// <summary>Colours, a square per finger, one tone, and a log line a second for the smoke test.</summary>
    internal sealed class HelloGame : Game
    {
        private readonly GraphicsDeviceManager _gdm;
        private SpriteBatch _batch;
        private Texture2D _pixel;
        private SoundEffect _tone;
        private int _frames;
        private double _logAt;
        private bool _toned;

        public HelloGame()
        {
            _gdm = new GraphicsDeviceManager(this)
            {
                IsFullScreen = true,
                SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight | DisplayOrientation.Portrait
            };
        }

        protected override void LoadContent()
        {
            _batch = new SpriteBatch(GraphicsDevice);
            _pixel = new Texture2D(GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });

            // 0.4 s of 440 Hz, 16-bit mono at 48 kHz (the rate the game's audio uses), faded out
            const int rate = 48000;
            int n = rate * 2 / 5;
            byte[] pcm = new byte[n * 2];

            for (int i = 0; i < n; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000 * Math.Min(1.0, (n - i) / 2000.0));
                pcm[2 * i] = (byte)s;
                pcm[2 * i + 1] = (byte)(s >> 8);
            }

            try
            {
                _tone = new SoundEffect(pcm, rate, AudioChannels.Mono);
            }
            catch (Exception e)
            {
                Log.Warn(MainActivity.Tag, "audio: no device (" + e.GetType().Name + ": " + e.Message + ")");
            }

            PresentationParameters pp = GraphicsDevice.PresentationParameters;
            Log.Info(MainActivity.Tag, $"loaded: backbuffer {pp.BackBufferWidth}x{pp.BackBufferHeight}, adapter '{GraphicsAdapter.DefaultAdapter.Description}'");
        }

        protected override void Update(GameTime gameTime)
        {
            if (!_toned)
            {
                _toned = true;
                Log.Info(MainActivity.Tag, "tone played: " + (_tone?.Play() ?? false));
            }

            foreach (TouchLocation t in TouchPanel.GetState())
            {
                if (t.State == TouchLocationState.Pressed)
                {
                    Log.Info(MainActivity.Tag, $"touch {t.Id} at {t.Position.X:F0},{t.Position.Y:F0}");
                }
            }

            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            _frames++;
            double s = gameTime.TotalGameTime.TotalSeconds;
            GraphicsDevice.Clear(new Color((float)(0.5 + 0.5 * Math.Sin(s)), (float)(0.5 + 0.5 * Math.Sin(s + 2)), (float)(0.5 + 0.5 * Math.Sin(s + 4))));

            _batch.Begin();
            _batch.Draw(_pixel, new Rectangle(24, 24, 96, 96), Color.Black);

            foreach (TouchLocation t in TouchPanel.GetState())
            {
                _batch.Draw(_pixel, new Rectangle((int)t.Position.X - 60, (int)t.Position.Y - 60, 120, 120), Color.White);
            }

            _batch.End();

            if (s >= _logAt)
            {
                _logAt = s + 1;
                Log.Info(MainActivity.Tag, $"frames {_frames} at {s:F1}s");
            }

            base.Draw(gameTime);
        }
    }
}
