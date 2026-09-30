using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClassicUO.Game;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Loads the art of the windows players open soon after logging in, a little per frame, so opening
    /// them doesn't stall. The magery spellbook built its images the first time it opened: ~430 ms in
    /// that one frame on a desktop (more on a phone); with its 76 small images loaded ahead it opens in
    /// ~70 ms (the book background itself still loads then). Starts 4 s after entering the world (after
    /// login's own loading; re-armed if the player leaves the world before it's done) and loads images on
    /// drawn frames only (GameController skips the frame limiter's idle ticks and the background, where
    /// iOS forbids GPU work), at least one per frame and more while a ~6 ms budget lasts. Logs its totals
    /// when done (the phone's log is in Documents). Loaded images stay cached for the rest of the session,
    /// so this runs once per launch.
    /// </summary>
    internal static class GumpWarmup
    {
        private const double BudgetMs = 6;
        private const uint StartDelayMs = 4000;

        private static readonly Queue<uint> _pending = new Queue<uint>();
        private static bool _queued;
        private static uint _startAt;
        private static bool _armed;
        private static double _totalMs, _worstMs;
        private static uint _worstGraphic;
        private static int _loaded, _frames;

        public static void Update(World world)
        {
            if (_queued && _pending.Count == 0)
            {
                return;
            }

            if (world == null || !world.InGame)
            {
                _armed = false; // wait again after the next login

                return;
            }

            if (!_queued)
            {
                _queued = true;
                QueueMageryBook();
            }

            if (!_armed)
            {
                _armed = true;
                _startAt = Time.Ticks + StartDelayMs;

                return;
            }

            if (Time.Ticks < _startAt)
            {
                return;
            }

            long frameStart = Stopwatch.GetTimestamp();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000);
            _frames++;

            do
            {
                long one = Stopwatch.GetTimestamp();
                uint graphic = _pending.Dequeue();
                Client.Game.UO.Gumps.GetGump(graphic);
                double ms = (Stopwatch.GetTimestamp() - one) * 1000.0 / Stopwatch.Frequency;
                _totalMs += ms;
                if (ms > _worstMs)
                {
                    _worstMs = ms;
                    _worstGraphic = graphic;
                }

                _loaded++;
            }
            while (_pending.Count > 0 && Stopwatch.GetTimestamp() - frameStart < budget);

            if (_pending.Count == 0)
            {
                Log.Info($"GumpWarmup: {_loaded} images over {_frames} frames, {_totalMs:0} ms in all, worst {_worstMs:0.0} ms (0x{_worstGraphic:X4})");
            }
        }

        // SpellbookGump (magery): minimized book, page corners, circle buttons, divider, 64 icons. Not the
        // book itself (0x08AC): at ~47 ms it's one image that costs a visible hitch in any frame, and it
        // is better paid when the player taps the book than while walking.
        private static void QueueMageryBook()
        {
            foreach (uint graphic in new uint[] { 0x08BA, 0x08BB, 0x08BC, 0x0835 })
            {
                _pending.Enqueue(graphic);
            }

            for (uint graphic = 0x08B1; graphic <= 0x08B8; graphic++)
            {
                _pending.Enqueue(graphic);
            }

            for (uint graphic = 0x08C0; graphic < 0x08C0 + 64; graphic++)
            {
                _pending.Enqueue(graphic);
            }
        }
    }
}
