// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    /// Band-limited upsampling of UO's 22050 Hz, 16-bit mono sound effects to the output rate
    /// (48 kHz on phones). FAudio resamples with linear interpolation; for 22 kHz material its
    /// images land at 11-22 kHz and a phone speaker plays them as hiss ("static"). A windowed-sinc
    /// filter removes them. Polyphase: 48000/22050 = 320/147, so 320 phases of 32 taps, built
    /// once. Each effect is resampled once when first loaded (UOSound instances are cached).
    /// </summary>
    public static class Resampler
    {
        private const int TAPS = 32;         // 16 input samples on each side
        private const int HALF = TAPS / 2;
        private const double CUTOFF = 0.95;  // of the input's Nyquist frequency

        private static float[] _table;
        private static int _up, _down;

        public static byte[] Pcm16Mono(byte[] input, int inRate, int outRate)
        {
            int g = Gcd(inRate, outRate);
            int up = outRate / g, down = inRate / g;
            float[] table = Table(up, down);

            int inCount = input.Length / 2;
            long outCount = (long)inCount * up / down;
            byte[] output = new byte[outCount * 2];

            for (long n = 0; n < outCount; n++)
            {
                long position = n * down; // in 1/up input samples
                int i = (int)(position / up);
                int row = (int)(position % up) * TAPS;
                double acc = 0;

                for (int k = 0; k < TAPS; k++)
                {
                    int j = i + k - HALF + 1;

                    if ((uint)j < (uint)inCount)
                    {
                        acc += table[row + k] * (short)(input[2 * j] | (input[2 * j + 1] << 8));
                    }
                }

                int s = Math.Clamp((int)Math.Round(acc), short.MinValue, short.MaxValue);
                output[2 * n] = (byte)s;
                output[2 * n + 1] = (byte)(s >> 8);
            }

            return output;
        }

        private static float[] Table(int up, int down)
        {
            if (_table != null && _up == up && _down == down)
            {
                return _table;
            }

            var t = new float[up * TAPS];

            for (int p = 0; p < up; p++)
            {
                double frac = p / (double)up; // the output sample sits this far past input sample i
                double sum = 0;

                for (int k = 0; k < TAPS; k++)
                {
                    double x = (k - HALF + 1) - frac; // input sample j's distance from the output point
                    double a = Math.PI * CUTOFF * x;
                    double h = CUTOFF * (x == 0 ? 1 : Math.Sin(a) / a) * Blackman(x / HALF);
                    t[p * TAPS + k] = (float)h;
                    sum += h;
                }

                for (int k = 0; k < TAPS; k++)
                {
                    t[p * TAPS + k] = (float)(t[p * TAPS + k] / sum); // unity gain at DC for every phase
                }
            }

            _up = up;
            _down = down;

            return _table = t;
        }

        private static double Blackman(double u) =>
            Math.Abs(u) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);

        private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }
}
