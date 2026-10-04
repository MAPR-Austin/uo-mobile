// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>UO Mobile: the phone's default counter strip is filled in code (Touch/PhoneDefaults).</summary>
    internal partial class CounterBarGump
    {
        /// <summary>A counter for this graphic (hue null: any hue).</summary>
        internal void AddCounter(ushort graphic, ushort? hue)
        {
            _dataBox.Add(new CounterItem(this, graphic, hue, 0));
            SetupLayout();
        }

        /// <summary>Size the bar to so many cells across and down.</summary>
        internal void SizeTo(int columns, int rows)
        {
            ResizeWindow(new Point(columns * _rectSize + BoderSize * 2, rows * _rectSize + BoderSize * 2));
            SetupLayout();
        }
    }
}
