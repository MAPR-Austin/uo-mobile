// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Draws the touch HUD: joystick, the active layout's buttons, and (in edit mode) the
    /// "+" button and edit tint. Purely visual - it never takes mouse input; hit-testing
    /// belongs to <see cref="TouchInput"/>, which sees fingers before the UI does.
    /// </summary>
    internal sealed class TouchHudGump : Gump
    {
        private readonly List<Label> _labels = new List<Label>();
        private Label _layoutName;
        private int _builtRevision = -1;
        private int _builtW, _builtH;
        private bool _builtSuppressed;
        private bool _builtWar;

        private static bool InWar => Client.Game.UO.World?.Player?.InWarMode ?? false;

        // ---- target panel (top-left): name in notoriety colour, HP bar, distance ----
        private Label _targetLabel;
        private int _targetHpPercent = -1;
        private static readonly Color PanelFill = new Color(10, 10, 16, 190);
        private static readonly Color HpBack = new Color(60, 10, 10, 230);
        private static readonly Color HpFront = new Color(40, 200, 60, 255);
        private static readonly Color HpPoison = new Color(60, 200, 200, 255);
        private const int PANEL_W = 190, PANEL_H = 36;

        /// <summary>Where the target panel is (UI units); empty when there is no target. Tapping it pulls the health bar.</summary>
        public static Rectangle TargetPanel { get; private set; }

        private void UpdateTargetPanel()
        {
            Mobile target = TouchInput.Suppressed ? null : MobileActions.CurrentTarget(World);

            if (target == null)
            {
                TargetPanel = Rectangle.Empty;

                if (_targetLabel != null)
                {
                    _targetLabel.IsVisible = false;
                }

                return;
            }

            // The server only sends another mobile's hits after a status request (the health bar
            // gump normally asks); ask once whenever the panel's target changes.
            if (target.Serial != _statusRequestedFor)
            {
                _statusRequestedFor = target.Serial;
                GameActions.RequestMobileStatus(World, target.Serial);
            }

            Rectangle safe = TouchInput.Safe;
            TargetPanel = new Rectangle(safe.X + 6, safe.Y + 6, PANEL_W, PANEL_H);

            string text = $"{target.Name}  ({target.Distance})";
            ushort hue = Notoriety.GetHue(target.NotorietyFlag);

            if (_targetLabel == null || _targetLabel.IsDisposed)
            {
                _targetLabel = new Label(text, true, hue, PANEL_W - 8, 1, FontStyle.BlackBorder);
                Add(_targetLabel);
            }

            if (_targetLabel.Text != text)
            {
                _targetLabel.Text = text;
            }

            _targetLabel.Hue = hue;
            _targetLabel.X = TargetPanel.X + 4;
            _targetLabel.Y = TargetPanel.Y + 2;
            _targetLabel.IsVisible = true;
            _targetHpPercent = target.HitsMax > 0 ? Math.Clamp(target.Hits * 100 / target.HitsMax, 0, 100) : 0;
            _targetPoisoned = target.IsPoisoned;
        }

        private bool _targetPoisoned;
        private uint _statusRequestedFor;

        private TouchHudGump(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = false;
            CanCloseWithRightClick = false;
            CanCloseWithEsc = false;
            LayerOrder = UILayer.Over;
            X = 0;
            Y = 0;
        }

        public static void Ensure(World world)
        {
            TouchHudGump hud = UIManager.GetGump<TouchHudGump>();

            if (hud == null || hud.IsDisposed)
            {
                UIManager.Add(new TouchHudGump(world));
            }
        }

        public override void Update()
        {
            base.Update();

            // Keep the world viewport filling the screen (window resizes, rotation, DPI changes).
            if (Client.Game.Scene is GameScene scene
                && (scene.Camera.Bounds.Width != TouchInput.ScreenW || scene.Camera.Bounds.Height != TouchInput.ScreenH))
            {
                WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

                if (viewport != null)
                {
                    viewport.ResizeGameWindow(new Point(Client.Game.Window.ClientBounds.Width, Client.Game.Window.ClientBounds.Height));
                    viewport.SetGameWindowPosition(new Point(-5, -5));
                }
            }

            UpdateTargetPanel();

            if (_builtRevision != TouchInput.Revision || _builtW != TouchInput.ScreenW || _builtH != TouchInput.ScreenH || _builtSuppressed != TouchInput.Suppressed || _builtWar != InWar)
            {
                Rebuild();
            }
        }

        private void Rebuild()
        {
            _builtRevision = TouchInput.Revision;
            _builtSuppressed = TouchInput.Suppressed;
            _builtWar = InWar;
            _builtW = TouchInput.ScreenW;
            _builtH = TouchInput.ScreenH;
            Width = _builtW;
            Height = _builtH;

            foreach (Label l in _labels)
            {
                l.Dispose();
            }

            _targetLabel?.Dispose(); // re-created on the next update
            _targetLabel = null;

            _labels.Clear();
            _layoutName?.Dispose();

            ActionLayout layout = TouchInput.Current;

            if (layout == null || _builtSuppressed)
            {
                return;
            }

            foreach (ActionButtonDef b in layout.Buttons)
            {
                Point c = TouchInput.ButtonCenter(b);
                int r = TouchInput.ButtonRadius(b);

                // The war/peace toggle names what a tap will do: "Peace" while at war.
                string text = b.Action == "war_peace" && _builtWar ? "Peace" : b.DisplayLabel;
                Label label = new Label(text, true, 0x0481, r * 2 - 4, 1, FontStyle.BlackBorder, TEXT_ALIGN_TYPE.TS_CENTER);
                label.X = c.X - r + 2;
                label.Y = c.Y - label.Height / 2;
                _labels.Add(label);
                Add(label);
            }

            string title = TouchInput.EditMode ? $"EDITING: {layout.Name}" : layout.Name;
            _layoutName = new Label(title, true, TouchInput.EditMode ? (ushort)0x0035 : (ushort)0x0481, 0, 1, FontStyle.BlackBorder);
            _layoutName.X = (int)(0.45f * _builtW) - _layoutName.Width / 2;
            _layoutName.Y = 4;
            Add(_layoutName);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            ActionLayout layout = TouchInput.Current;

            if (layout == null || TouchInput.Suppressed)
            {
                return false;
            }

            Vector3 hue = ShaderHueTranslator.GetHueVector(0);
            bool edit = TouchInput.EditMode;

            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    // target panel
                    Rectangle tp = TargetPanel;

                    if (!tp.IsEmpty)
                    {
                        Texture2D white = SolidColorTextureCache.GetTexture(Color.White);
                        batcher.Draw(SolidColorTextureCache.GetTexture(PanelFill), tp, hue, layerDepth);
                        Rectangle bar = new Rectangle(tp.X + 4, tp.Bottom - 10, tp.Width - 8, 6);
                        batcher.Draw(SolidColorTextureCache.GetTexture(HpBack), bar, hue, layerDepth);
                        bar.Width = bar.Width * Math.Max(0, _targetHpPercent) / 100;
                        batcher.Draw(SolidColorTextureCache.GetTexture(_targetPoisoned ? HpPoison : HpFront), bar, hue, layerDepth);
                    }

                    // joystick
                    Point jc = TouchInput.JoystickCenter;
                    int jr = TouchInput.JoystickRadius;
                    DrawCircle(batcher, jc, jr, edit ? EditFill : JoyBase, hue, layerDepth);
                    DrawCircle(batcher, jc, jr, JoyRing, hue, layerDepth, ring: true);

                    Vector2 off = TouchInput.JoystickOffset;
                    Point knob = new Point(jc.X + (int)(off.X * jr), jc.Y + (int)(off.Y * jr));
                    DrawCircle(batcher, knob, Math.Max(12, jr * 2 / 5), TouchInput.JoystickActive ? KnobActive : Knob, hue, layerDepth);

                    // buttons
                    for (int i = 0; i < layout.Buttons.Count; i++)
                    {
                        ActionButtonDef b = layout.Buttons[i];
                        Point c = TouchInput.ButtonCenter(b);
                        int r = TouchInput.ButtonRadius(b);
                        bool pressed = TouchInput.PressedButton == i;

                        Color fill = pressed ? ButtonPressed : edit ? EditFill : b.Action == "war_peace" && InWar ? WarFill : ButtonFill;
                        DrawCircle(batcher, c, r, fill, hue, layerDepth);
                        DrawCircle(batcher, c, r, edit ? EditRing : ButtonRing, hue, layerDepth, ring: true);
                    }

                    if (edit)
                    {
                        Point ac = TouchInput.AddButtonCenter;
                        int ar = TouchInput.AddButtonRadius;
                        DrawCircle(batcher, ac, ar, AddFill, hue, layerDepth);
                        DrawCircle(batcher, ac, ar, EditRing, hue, layerDepth, ring: true);
                        Texture2D plus = SolidColorTextureCache.GetTexture(Color.White);
                        batcher.Draw(plus, new Rectangle(ac.X - ar / 2, ac.Y - 2, ar, 4), hue, layerDepth);
                        batcher.Draw(plus, new Rectangle(ac.X - 2, ac.Y - ar / 2, 4, ar), hue, layerDepth);
                    }

                    return true;
                }
            );

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        // ---------- circle textures ----------

        private static readonly Color ButtonFill = new Color(20, 20, 28, 150);
        private static readonly Color ButtonPressed = new Color(200, 160, 60, 200);
        private static readonly Color WarFill = new Color(170, 20, 20, 210);
        private static readonly Color ButtonRing = new Color(220, 200, 150, 200);
        private static readonly Color JoyBase = new Color(20, 20, 28, 90);
        private static readonly Color JoyRing = new Color(220, 200, 150, 140);
        private static readonly Color Knob = new Color(200, 190, 160, 150);
        private static readonly Color KnobActive = new Color(240, 210, 120, 220);
        private static readonly Color EditFill = new Color(40, 90, 160, 150);
        private static readonly Color EditRing = new Color(120, 190, 255, 230);
        private static readonly Color AddFill = new Color(30, 120, 60, 200);

        private const int TEX = 128;
        private static readonly Dictionary<(Color, bool), Texture2D> _circles = new Dictionary<(Color, bool), Texture2D>();

        private static void DrawCircle(UltimaBatcher2D batcher, Point center, int radius, Color color, Vector3 hue, float depth, bool ring = false)
        {
            batcher.Draw(GetCircle(color, ring), new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2), hue, depth);
        }

        private static Texture2D GetCircle(Color color, bool ring)
        {
            if (_circles.TryGetValue((color, ring), out Texture2D tex) && !tex.IsDisposed)
            {
                return tex;
            }

            Color[] data = new Color[TEX * TEX];
            float c = (TEX - 1) / 2f;
            float outer = TEX / 2f - 1f;
            float inner = outer - 5f;

            for (int y = 0; y < TEX; y++)
            {
                for (int x = 0; x < TEX; x++)
                {
                    float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    // 1px anti-aliased edges
                    float a = MathHelper.Clamp(outer - d + 0.5f, 0f, 1f);

                    if (ring)
                    {
                        a *= MathHelper.Clamp(d - inner + 0.5f, 0f, 1f);
                    }

                    float alpha = a * color.A / 255f;
                    // premultiplied alpha
                    data[y * TEX + x] = new Color((byte)(color.R * alpha), (byte)(color.G * alpha), (byte)(color.B * alpha), (byte)(255 * alpha));
                }
            }

            tex = new Texture2D(Client.Game.GraphicsDevice, TEX, TEX, false, SurfaceFormat.Color);
            tex.SetData(data);
            _circles[(color, ring)] = tex;

            return tex;
        }
    }
}
