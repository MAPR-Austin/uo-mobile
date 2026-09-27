// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.IO.Audio
{
    public class UOSound : Sound
    {
        private const int MAX_RESAMPLE_SECONDS = 30;

        private readonly byte[] _waveBuffer;

        public UOSound(string name, int index, byte[] buffer) : base(name, index)
        {
            Delay = (uint) ((buffer.Length - 32) / 88.2f); // from the 22 kHz length

            // Phones: hand FAudio 48 kHz so it doesn't upsample with linear interpolation (hiss).
            // Not the few minutes-long sounds (heartbeat loops, up to 480 s): resampling those
            // would stall the frame and cost ~90 MB; they play at 22 kHz as before.
            if (OutputRate > 0 && OutputRate != Frequency && buffer.Length <= MAX_RESAMPLE_SECONDS * Frequency * 2)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _waveBuffer = Resampler.Pcm16Mono(buffer, Frequency, OutputRate);
                Frequency = OutputRate;

                if (watch.ElapsedMilliseconds > 8)
                {
                    ClassicUO.Utility.Logging.Log.Info($"[UOMobile] sound {index} ({buffer.Length / 44100.0:0.0} s) resampled in {watch.ElapsedMilliseconds} ms");
                }
            }
            else
            {
                _waveBuffer = buffer;
            }
        }

        public bool CalculateByDistance { get; set; }
        public int X, Y;

        protected override void OnBufferNeeded(object sender, EventArgs e)
        {
            // not needed.
            //if (World.InGame && X >= 0 && Y >= 0 && CalculateByDistance)
            //{
            //    int distX = Math.Abs(X - World.Player.X);
            //    int distY = Math.Abs(Y - World.Player.Y);
            //    int distance = Math.Max(distX, distY);

            //    float volume = ProfileManager.CurrentProfile.SoundVolume / Constants.SOUND_DELTA;
            //    float distanceFactor = 0.0f;

            //    if (distance >= 1)
            //    {
            //        float volumeByDist = volume / (World.ClientViewRange + 1);
            //        distanceFactor = volumeByDist * distance;
            //    }

            //    if (distance > World.ClientViewRange)
            //    {
            //        Stop();
            //        Dispose();
            //        return;
            //    }

            //    if (ProfileManager.CurrentProfile == null || !ProfileManager.CurrentProfile.EnableSound || !Client.Game.IsActive && !ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            //        volume = 0;

            //    if (Client.Game.IsActive)
            //    {
            //        if (!ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            //            volume = ProfileManager.CurrentProfile.SoundVolume / Constants.SOUND_DELTA;
            //    }
            //    else if (!ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            //        volume = 0;

            //    VolumeFactor = distanceFactor;
            //    Volume = volume;
            //}
        }

        protected override ArraySegment<byte> GetBuffer()
        {
            return _waveBuffer;
        }
    }
}
