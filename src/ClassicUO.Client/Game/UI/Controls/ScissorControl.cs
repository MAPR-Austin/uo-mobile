// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;

namespace ClassicUO.Game.UI.Controls
{
    internal class ScissorControl : Control
    {
        public ScissorControl(bool enabled, int x, int y, int width, int height) : this(enabled)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public ScissorControl(bool enabled)
        {
            CanMove = false;
            AcceptMouseInput = false;
            AcceptKeyboardInput = false;
            Alpha = 1.0f;
            WantUpdateSize = false;
            DoScissor = enabled;
        }

        public bool DoScissor;

        // whether each open begin really pushed a clip (ClipBegin refuses an empty or off-screen area;
        // its end must not pop then - an empty scissor stack threw and closed the game)
        private static readonly System.Collections.Generic.Stack<bool> _pushed = new System.Collections.Generic.Stack<bool>();

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            bool clipIt(Renderer.UltimaBatcher2D batcher)
            {
                if (DoScissor)
                {
                    _pushed.Push(batcher.ClipBegin(x, y, Width, Height));
                }
                else if (_pushed.Count == 0 || _pushed.Pop())
                {
                    batcher.ClipEnd();
                }
                return true;
            }

            renderLists.AddGumpWithAtlas(clipIt);
            renderLists.AddGumpNoAtlas(clipIt);

            return true;
        }
    }
}