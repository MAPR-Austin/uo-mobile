// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Build 54 (owner: "macro menus are hard to read"): a NiceButton on a solid plate, its text in the touch windows'
    /// bigger font - so on a phone each step, tab and choice of the macro and button editors reads as a button and
    /// stands out from the dark window behind it.
    /// </summary>
    internal sealed class PlateButton : NiceButton
    {
        /// <summary>The editors' text: unicode font 0, a size up from font 1 (ClassicUO's default).</summary>
        public const byte FONT = 0;

        public static readonly Color Normal = new Color(44, 52, 68);
        public static readonly Color Selected = new Color(110, 84, 24);

        public PlateButton(int x, int y, int w, int h, string text, int id = 0, ushort hue = 0xFFFF, TEXT_ALIGN_TYPE align = TEXT_ALIGN_TYPE.TS_CENTER, bool selected = false)
            : base(x, y, w, h, ButtonAction.Activate, align == TEXT_ALIGN_TYPE.TS_LEFT ? " " + text : text, 0, align, hue, true, FONT)
        {
            ButtonParameter = id;
            IsSelectable = false;
            Fill = selected ? Selected : Normal;
        }

        public Color Fill { get; set; }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            Rectangle plate = new Rectangle(x, y + 1, Width, Height - 2);
            Color fill = Fill;

            renderLists.AddGumpNoAtlas
            (
                batcher =>
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(fill), plate, ShaderHueTranslator.GetHueVector(0, false, 1f), layerDepth);

                    return true;
                }
            );

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }

    /// <summary>Build 54: a dark plate under the editors' text fields (white text on the old parchment was hard to read).</summary>
    internal sealed class InputPlate : Control
    {
        private static readonly Color Fill = new Color(58, 68, 90);

        public InputPlate()
        {
            AcceptMouseInput = false;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            Rectangle plate = new Rectangle(x, y, Width, Height);

            renderLists.AddGumpNoAtlas
            (
                batcher =>
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(Fill), plate, ShaderHueTranslator.GetHueVector(0, false, 1f), layerDepth);

                    return true;
                }
            );

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }
}
