// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;

namespace ClassicUO.Touch
{
    /// <summary>
    /// The stealth step counter over your own head. While you move stealthily the UO Mobile server
    /// labels your character "Stealth: N" after each step (servuo Scripts/Custom/UOMobile/StealthRules.cs;
    /// keep the text the same in both). A plain client stacks each label and logs it in the journal;
    /// here the newest one replaces the last, stays up for as long as you stay hidden, and stays out of
    /// the journal.
    /// </summary>
    internal static class StealthCounter
    {
        private const string Prefix = "Stealth: ";
        private const long ShowFor = 30 * 60 * 1000; // taken down when you're seen, not by time

        private static TextObject _shown;

        /// <summary>MessageManager.HandleMessage: true when the message was the counter (shown here instead).</summary>
        public static bool TryShow(World world, Entity parent, string text, ushort hue, MessageType type, byte font, bool unicode)
        {
            if (type != MessageType.Label || parent == null || world?.Player == null || parent.Serial != world.Player.Serial ||
                text == null || !text.StartsWith(Prefix) || !int.TryParse(text.Substring(Prefix.Length), out _))
            {
                return false;
            }

            Hide();

            TextObject msg = world.MessageManager.CreateMessage(text, hue, font, unicode, type, TextType.OBJECT);
            msg.Time = Time.Ticks + ShowFor;
            parent.AddMessage(msg);
            _shown = msg;

            return true;
        }

        public static void Update(World world)
        {
            if (_shown != null && (_shown.IsDestroyed || world == null || !world.InGame || world.Player == null || !world.Player.IsHidden))
            {
                Hide();
            }
        }

        private static void Hide()
        {
            TextObject old = _shown;
            _shown = null;

            // gone already if five newer messages pushed it off the stack
            if (old == null || old.IsDestroyed)
            {
                return;
            }

            GameObject owner = old.Owner;

            if (owner?.TextContainer != null)
            {
                owner.TextContainer.Remove(old);
                owner.TextContainer.Size--;
            }

            // (not UpdateScreenPosition to close the gap: on the player that also auto-opens doors; the
            // next counter or step restacks the text)
            old.Destroy();
        }
    }
}
