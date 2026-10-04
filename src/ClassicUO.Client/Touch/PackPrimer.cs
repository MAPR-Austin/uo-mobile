// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Touch
{
    /// <summary>
    /// The client only learns what is in a container once it has been opened, so the counter strip
    /// (and macros counting reagents or bandages) read 0 until the player opens their backpack.
    /// Once per login on the phone: open the backpack without showing it - the server sends its
    /// contents, PacketHandlers.OpenContainer skips the window - as Razor and UOAssist do.
    /// </summary>
    internal static class PackPrimer
    {
        private const uint SwallowWithinMs = 5000;

        private static uint _primedFor; // the player serial this login was primed for
        private static uint _packSerial;
        private static uint _swallowUntil;

        /// <summary>TouchInput.Update, in the world.</summary>
        public static void Update(World world)
        {
            if (world?.Player == null || _primedFor == world.Player.Serial)
            {
                return;
            }

            Item pack = world.Player.FindItemByLayer(Layer.Backpack);

            if (pack == null)
            {
                return;
            }

            _primedFor = world.Player.Serial;
            _packSerial = pack.Serial;
            _swallowUntil = Time.Ticks + SwallowWithinMs;
            GameActions.DoubleClick(world, pack.Serial);
        }

        /// <summary>PacketHandlers.OpenContainer: true to skip the window (the backpack this primer opened).</summary>
        public static bool SwallowOpen(uint serial)
        {
            if (serial != _packSerial || _swallowUntil == 0 || Time.Ticks > _swallowUntil)
            {
                return false;
            }

            _swallowUntil = 0; // only the one: a backpack the player opens shows

            return true;
        }

        /// <summary>A new login primes again (TouchInput.Unload).</summary>
        public static void Reset()
        {
            _primedFor = 0;
            _swallowUntil = 0;
        }
    }
}
