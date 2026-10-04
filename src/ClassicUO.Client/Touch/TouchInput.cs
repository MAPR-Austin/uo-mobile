// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Touch front-end for phones. Every finger is claimed by one owner when it lands:
    ///  - the virtual joystick (walk / run, 8 directions),
    ///  - an on-screen action button (runs a <see cref="MobileActions"/> id on release),
    ///  - or the "pointer": the finger drives the client's mouse, so a tap is a left click,
    ///    a double tap is a double click, and a drag is a drag (pull a health bar off a
    ///    creature, move items, move gumps). A long press over a gump is a right click (close).
    /// Pointer presses are delayed two frames so the scene has re-picked the object under
    /// the finger before the click lands (the scene picks what is under Mouse.Position
    /// once per frame).
    ///
    /// On desktop the mouse can drive the joystick and buttons too (finger id -1), which is
    /// how the layer is exercised without a touchscreen.
    ///
    /// Coordinates: fingers arrive normalised (0..1) to the window. "UI space" is the space
    /// Mouse.Position and gumps use.
    /// </summary>
    internal static class TouchInput
    {
        public const long MouseFingerId = -1;

        private const float JOY_DEADZONE = 0.22f;
        private const float JOY_RUN = 0.62f;
        private const uint LONG_PRESS_MS = 550;
        private const int TAP_SLOP = 12;
        private const int POINTER_FRAME_DELAY = 2; // drawn frames
        // A press on a window waits this long (unless the finger moves or lifts first) so a second
        // finger can turn it into a window pinch before the game ever sees it. Taps are unaffected:
        // lifting releases the press and its release together.
        private const uint WINDOW_PRESS_HOLD_MS = 130;

        // Pinch: two world fingers zoom the camera. Ignored: the finger left over when a pinch ends.
        private enum Owner { Joystick, Button, Pointer, EditDrag, Pinch, Ignored }

        // Camera.Zoom > 1 zooms out. Practical limits for a phone screen (the camera allows 0.5-2.5);
        // build 38: 15% more range each way (was 0.6-1.6).
        private const float PINCH_ZOOM_MIN = 0.52f;
        private const float PINCH_ZOOM_MAX = 1.84f;
        private static long _pinchA = long.MinValue, _pinchB = long.MinValue;
        private static float _pinchStartDistance, _pinchStartZoom;

        private sealed class Finger
        {
            public long Id;
            public Owner Owner;
            public int Button = -1;
            public Point Start, Pos;
            public uint DownTime;
            public bool Moved, LongPressFired;
            public bool Joystick; // EditDrag of the joystick itself
            public bool OnWorld; // Pointer: landed on the world, not on a window
            public Game.UI.Gumps.Gump OnGump; // Pointer: the window it landed on (a pinch there zooms that window)
            public uint HoldUntil; // Pointer on a window: its events wait until this tick (0 = not held)
            public bool PinchLeftover; // Ignored: still down after its pinch partner lifted
            public Game.UI.Gumps.Gump PinchGump; // PinchLeftover: the window that pinch was zooming (null = world)
        }

        /// <summary>
        /// A pointer finger that pressed a zoomed window: the game sees it in the window's own
        /// unscaled layout (see <see cref="GumpScale"/>), fixed at touch-down so a scroll or press
        /// inside the window stays consistent even if the window moves.
        /// </summary>
        private sealed class PointerMap
        {
            public Game.UI.Gumps.Gump Gump;
            public Point Origin, DownPhysical, DownLogical;
            public float Scale;
        }

        private static readonly Dictionary<long, PointerMap> _maps = new Dictionary<long, PointerMap>();
        private static readonly List<long> _mapsToDrop = new List<long>();

        /// <summary>The window the game's pointer position is currently mapped onto (null: raw position).</summary>
        public static Game.UI.Gumps.Gump PointerGump { get; private set; }

        private static Point? _physicalPointer;

        /// <summary>Where the finger really is (held items, tooltips and the target cursor draw here).</summary>
        public static Point CursorPosition => Enabled && _physicalPointer.HasValue ? _physicalPointer.Value : Mouse.Position;

        private enum PointerEventType { Down, Move, Up, RightClick }

        private readonly struct PointerEvent
        {
            public PointerEvent(PointerEventType type, Point pos, long frame, long finger)
            {
                Type = type;
                Pos = pos;
                ReadyFrame = frame;
                Finger = finger;
            }

            public readonly PointerEventType Type;
            public readonly Point Pos;
            public readonly long ReadyFrame;
            public readonly long Finger;
        }

        private static readonly Dictionary<long, Finger> _fingers = new Dictionary<long, Finger>();
        private static readonly Queue<PointerEvent> _pointerQueue = new Queue<PointerEvent>();
        private static long _pointerFinger = long.MinValue;
        // The finger whose press has reached the game and not yet been released.
        private static long _downFinger = long.MinValue;
        // Drawn frames, not Update ticks: the scene re-picks the object under the pointer when it draws.
        private static long _frame => (long)Client.Game.DrawCount;

        public static bool Enabled { get; set; }

        /// <summary>Platforms where starting text input pops an on-screen keyboard.</summary>
        public static bool HasVirtualKeyboard => OperatingSystem.IsIOS() || OperatingSystem.IsAndroid();

        // The keyboard is up only after a tap on a text box or the Chat button, and goes away on
        // the next tap anywhere else (UIManager always gives the chat line keyboard focus, so
        // focus alone can't decide it).
        private static bool _keyboardWanted;

        /// <summary>Focus the speech line and raise the keyboard (the "chat" button action).</summary>
        public static void OpenChat()
        {
            if (UIManager.SystemChat != null && !UIManager.SystemChat.IsDisposed)
            {
                UIManager.KeyboardFocusControl = UIManager.SystemChat.TextBoxControl;
            }

            _keyboardWanted = true;
        }
        public static bool EditMode { get; private set; }
        public static ActionLayoutSet Layouts { get; private set; }

        public static bool JoystickActive { get; private set; }
        public static Vector2 JoystickOffset { get; private set; } // -1..1, knob position relative to base
        public static int PressedButton { get; private set; } = -1;

        /// <summary>Layout geometry changed (buttons added/removed/moved, layout switched); the HUD rebuilds its labels.</summary>
        public static int Revision { get; private set; }

        public static ActionLayout Current => Layouts?.Current;

        public static int ScreenW => (int)(Client.Game.GraphicManager.PreferredBackBufferWidth / Client.Game.DpiScale);
        public static int ScreenH => (int)(Client.Game.GraphicManager.PreferredBackBufferHeight / Client.Game.DpiScale);
        public static int ScreenMin => Math.Min(ScreenW, ScreenH);

        public static void EnsureLoaded()
        {
            if (Layouts == null)
            {
                Layouts = ActionLayoutSet.Load();
                Revision++;
            }
        }

        public static void Unload()
        {
            MobileMacroRunner.Unload();
            Layouts = null;
            EditMode = false;
            _fingers.Clear();
            _pointerQueue.Clear();
            _pointerFinger = long.MinValue;
            _downFinger = long.MinValue;
            _pinchA = _pinchB = long.MinValue;
            _pinchGump = null;
            _ghostFinger = long.MinValue;
            PackPrimer.Reset();
            Agents.Reset();
            AutoLoot.Reset();
            SmartTargeting.Reset();
            _maps.Clear();
            PointerGump = null;
            _physicalPointer = null;
            GumpScale.Unload();
            JoystickActive = false;
            PressedButton = -1;
        }

        /// <summary>
        /// After a rotation the screen is a different shape: pull every window that now hangs off an
        /// edge back inside, so nothing ends up out of reach of a finger.
        /// </summary>
        public static void KeepGumpsOnScreen()
        {
            if (!Enabled)
            {
                return;
            }

            int w = ScreenW, h = ScreenH;

            if (w <= 0 || h <= 0 || LoginLayout.Active) // the login screens are placed by LoginLayout
            {
                return;
            }

            foreach (Game.UI.Gumps.Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || g is TouchHudGump || g is Game.UI.Gumps.WorldViewportGump)
                {
                    continue;
                }

                KeepOnScreen(g, w, h);
            }
        }

        /// <summary>Pull a window back inside the screen, by its drawn (zoomed) size.</summary>
        private static void KeepOnScreen(Game.UI.Gumps.Gump g, int w, int h)
        {
            float scale = GumpScale.Of(g);
            int x = Math.Max(0, Math.Min(g.X, w - (int)(g.Width * scale)));
            int y = Math.Max(0, Math.Min(g.Y, h - (int)(g.Height * scale)));

            if (x != g.X || y != g.Y)
            {
                g.X = x;
                g.Y = y;
            }
        }

        /// <summary>Screen width in UI units in portrait (the short side; see GameController.FitUiToPhone).</summary>
        private const int PORTRAIT_UI_WIDTH = 480;

        /// <summary>
        /// Portrait, in game, with a window open that is wider than the portrait screen (the macro and
        /// button editors, UO's options): the UI should be fitted wider until it closes.
        /// </summary>
        public static bool WantsWideUi()
        {
            if (!Enabled || !IsPortrait || !(Client.Game.Scene is Game.Scenes.GameScene))
            {
                return false;
            }

            foreach (Game.UI.Gumps.Gump g in UIManager.Gumps)
            {
                if (!g.IsDisposed && g.IsVisible && g.Width > PORTRAIT_UI_WIDTH &&
                    !(g is TouchHudGump) && !(g is Game.UI.Gumps.WorldViewportGump))
                {
                    return true;
                }
            }

            return false;
        }

        public static void NextLayout()
        {
            Layouts?.Next();
            Layouts?.Save();
            Revision++;
            GameActions.Print(Client.Game.UO.World, $"Layout: {Current?.Name}", 0x35);
        }

        public static void ToggleEditMode()
        {
            EditMode = !EditMode;

            if (!EditMode)
            {
                Layouts?.Save();
            }

            Revision++;
            GameActions.Print(Client.Game.UO.World, EditMode ? "Edit mode: drag buttons to move them, tap one to change it, tap + to add. Tap Edit again when done." : "Layout saved.", 0x35);
        }

        public static void MarkChanged()
        {
            Revision++;
        }

        // ---------- geometry ----------

        /// <summary>
        /// The part of the screen not covered by the notch / Dynamic Island / home indicator, in UI
        /// units. Layout positions (0..1) are relative to this rectangle. Whole screen on desktop.
        /// </summary>
        public static Rectangle Safe
        {
            get
            {
                (float x, float y, float w, float h) = SafeFractions();

                return new Rectangle((int)(x * ScreenW), (int)(y * ScreenH), (int)(w * ScreenW), (int)(h * ScreenH));
            }
        }

        /// <summary>
        /// The safe area as shares (0..1) of the window: SDL's on phones, the whole window on desktop -
        /// where UOM_SAFE_INSETS=iphone fakes an iPhone 15's notch and home indicator for testing.
        /// </summary>
        public static (float X, float Y, float W, float H) SafeFractions()
        {
            IntPtr window = Client.Game.Window.Handle;

            if (!SDL3.SDL.SDL_GetWindowSize(window, out int ww, out int wh) || ww <= 0 || wh <= 0)
            {
                return (0f, 0f, 1f, 1f);
            }

            if (!HasVirtualKeyboard) // phones/tablets only
            {
                if (!FakeIphoneInsets)
                {
                    return (0f, 0f, 1f, 1f);
                }

                // iPhone 15, in points: portrait 393x852 with 59 above and 34 below; landscape 59 each side and 21 below
                return wh > ww ? (0f, 59f / 852, 1f, (852f - 59 - 34) / 852) : (59f / 852, 0f, (852f - 118) / 852, (393f - 21) / 393);
            }

            if (!SDL3.SDL.SDL_GetWindowSafeArea(window, out SDL3.SDL.SDL_Rect r) || r.w <= 0 || r.h <= 0)
            {
                return (0f, 0f, 1f, 1f);
            }

            return (r.x / (float)ww, r.y / (float)wh, r.w / (float)ww, r.h / (float)wh);
        }

        private static readonly bool FakeIphoneInsets = Environment.GetEnvironmentVariable("UOM_SAFE_INSETS") == "iphone";

        public static Point FromLayout(float x, float y)
        {
            Rectangle safe = Safe;

            return new Point(safe.X + (int)(x * safe.Width), safe.Y + (int)(y * safe.Height));
        }

        public static bool IsPortrait => ScreenH > ScreenW;

        /// <summary>Long side / short side of the screen.</summary>
        private static float Aspect => Math.Max(ScreenW, ScreenH) / (float)Math.Max(1, ScreenMin);

        private static (float X, float Y) Place(float x, float y, float? px, float? py)
        {
            if (!IsPortrait)
            {
                return (x, y);
            }

            if (px.HasValue && py.HasValue)
            {
                return (px.Value, py.Value);
            }

            return ActionLayout.Portrait(x, y, Aspect);
        }

        public static Point ButtonCenter(ActionButtonDef b)
        {
            (float x, float y) = Place(b.X, b.Y, b.PX, b.PY);

            return FromLayout(x, y);
        }

        public static int ButtonRadius(ActionButtonDef b) => Math.Max(14, (int)(b.Size * ScreenMin * 0.5f));

        public static Point JoystickCenter
        {
            get
            {
                if (Current == null)
                {
                    return Point.Zero;
                }

                (float x, float y) = Place(Current.JoystickX, Current.JoystickY, Current.PJoystickX, Current.PJoystickY);

                return FromLayout(x, y);
            }
        }

        public static int JoystickRadius => Current == null ? 0 : Math.Max(30, (int)(Current.JoystickSize * ScreenMin * 0.5f));

        /// <summary>In edit mode an extra "+" button sits at the top centre.</summary>
        public static Point AddButtonCenter => IsPortrait ? FromLayout(0.5f, 0.13f) : FromLayout(0.5f, 0.07f);

        public static int AddButtonRadius => Math.Max(14, (int)(0.05f * ScreenMin));

        private static bool InCircle(Point p, Point c, int r) => (p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) <= r * r;

        private static int ButtonAt(Point p)
        {
            ActionLayout layout = Current;

            if (layout == null)
            {
                return -1;
            }

            // topmost = last drawn
            for (int i = layout.Buttons.Count - 1; i >= 0; i--)
            {
                ActionButtonDef b = layout.Buttons[i];

                if (InCircle(p, ButtonCenter(b), ButtonRadius(b)))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool InGame => Client.Game.UO.World != null && Client.Game.UO.World.InGame && Current != null;

        /// <summary>
        /// A server window's button, check box or text field under the finger (the house placement
        /// warning's OKAY sat under the joystick). The client's own windows - health bars, journal,
        /// paperdoll, the speech line - don't count: a walk must not turn into a click on them.
        /// </summary>
        private static bool WindowControlAt(Point pos)
        {
            Control c = UIManager.ControlAtPhysical(pos);

            return (c is Button || c is Checkbox || c is StbTextBox box && box.IsEditable) &&
                   c.RootParent is Game.UI.Gumps.Gump g && g.ServerSerial != 0;
        }

        /// <summary>While an editor window is open, or a house is being placed, the HUD is hidden and every finger is a pointer.</summary>
        public static bool Suppressed => UIManager.GetGump<MacroEditorGump>() != null || UIManager.GetGump<ButtonEditGump>() != null ||
                                         UIManager.GetGump<HousePlacementBar>() != null;

        public static Point ToUi(float nx, float ny) => new Point((int)(nx * ScreenW), (int)(ny * ScreenH));

        // ---------- input entry points ----------

        /// <summary>Returns true when the touch layer consumed the press (HUD hit, or a finger in pointer mode).</summary>
        public static bool OnDown(long id, Point pos, bool isMouse = false)
        {
            if (!Enabled)
            {
                return false;
            }

            Finger f = new Finger { Id = id, Start = pos, Pos = pos, DownTime = Time.Ticks };

            if (InGame && !Suppressed)
            {
                int button = ButtonAt(pos);

                if (!EditMode && button < 0 && TouchHudGump.TargetPanel.Contains(pos))
                {
                    f.Owner = Owner.Button;
                    f.Button = -3; // the target panel
                    _fingers[id] = f;

                    return true;
                }

                if (EditMode)
                {
                    if (InCircle(pos, AddButtonCenter, AddButtonRadius))
                    {
                        f.Owner = Owner.Button;
                        f.Button = -2; // the "+" button
                        _fingers[id] = f;

                        return true;
                    }

                    if (button >= 0)
                    {
                        f.Owner = Owner.EditDrag;
                        f.Button = button;
                        _fingers[id] = f;
                        PressedButton = button;

                        return true;
                    }

                    if (InCircle(pos, JoystickCenter, JoystickRadius))
                    {
                        f.Owner = Owner.EditDrag;
                        f.Joystick = true;
                        _fingers[id] = f;

                        return true;
                    }
                }
                else
                {
                    if (button >= 0)
                    {
                        f.Owner = Owner.Button;
                        f.Button = button;
                        _fingers[id] = f;
                        PressedButton = button;

                        return true;
                    }

                    // (a window's button or box under the joystick's ring takes the tap: e.g. OKAY on the
                    // house placement warning; the rest of a window there still steers)
                    if (!JoystickActive && InCircle(pos, JoystickCenter, (int)(JoystickRadius * 1.25f)) && !WindowControlAt(pos))
                    {
                        f.Owner = Owner.Joystick;
                        _fingers[id] = f;
                        JoystickActive = true;
                        UpdateJoystick(pos);

                        return true;
                    }
                }
            }

            if (isMouse)
            {
                return false; // a real mouse keeps its normal path
            }

            // One finger is the pointer; a second one turns a world press into a pinch-zoom. A finger
            // that lands during a pinch never clicks; after one pinch finger lifts, a new finger
            // resumes the pinch with the one still down.
            if (_pinchA != long.MinValue)
            {
                f.Owner = Owner.Ignored;
                _fingers[id] = f;

                return true;
            }

            Finger leftover = PinchLeftover();

            if (leftover != null || _pointerFinger != long.MinValue)
            {
                bool pinched = leftover != null ? CanPinch() && BeginPinch(leftover, f, leftover.PinchGump) : TryStartPinch(f);

                if (!pinched)
                {
                    f.Owner = Owner.Ignored;
                    _fingers[id] = f;
                }

                return true;
            }

            f.Owner = Owner.Pointer;

            _maps.Remove(id); // iOS reuses touch ids; a lifted finger's map must not carry over

            if (Client.Game.Scene is Game.Scenes.GameScene)
            {
                f.OnGump = UIManager.GumpAtPhysical(pos, out Point local);
                f.OnWorld = f.OnGump == null && UIManager.IsWorldAt(pos);

                if (f.OnGump != null && GumpScale.Scalable(f.OnGump))
                {
                    f.HoldUntil = Time.Ticks + WINDOW_PRESS_HOLD_MS;
                }
                float scale = GumpScale.Of(f.OnGump);

                if (scale != 1f)
                {
                    _maps[id] = new PointerMap
                    {
                        Gump = f.OnGump, Scale = scale, Origin = new Point(f.OnGump.X, f.OnGump.Y),
                        DownPhysical = pos, DownLogical = local
                    };
                }
            }

            _fingers[id] = f;
            _pointerFinger = id;
            SetPointer(MapPointer(pos, id));
            _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Down, pos, _frame + POINTER_FRAME_DELAY, id));

            return true;
        }

        public static bool OnMove(long id, Point pos)
        {
            if (!Enabled || !_fingers.TryGetValue(id, out Finger f))
            {
                return false;
            }

            f.Pos = pos;

            if (Math.Abs(pos.X - f.Start.X) > TAP_SLOP || Math.Abs(pos.Y - f.Start.Y) > TAP_SLOP)
            {
                f.Moved = true;
                f.HoldUntil = 0; // a drag: let the press through now
            }

            switch (f.Owner)
            {
                case Owner.Joystick:
                    UpdateJoystick(pos);

                    break;

                case Owner.EditDrag when f.Moved:
                    MoveEdited(f, pos);

                    break;

                case Owner.Pointer:
                    _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Move, pos, _frame, id));

                    break;

                case Owner.Pinch:
                    UpdatePinch();

                    break;
            }

            return true;
        }

        // ---------- pinch zoom ----------

        private static readonly Point OffScreen = new Point(-10000, -10000);

        /// <summary>Not while editing, over a menu, dragging a window, or holding an item.</summary>
        private static bool CanPinch() =>
            !EditMode && (!Suppressed || UIManager.GetGump<HousePlacementBar>() != null) && Client.Game.Scene is Game.Scenes.GameScene &&
            !UIManager.IsDragging && !Client.Game.UO.GameCursor.ItemHold.Enabled;

        private static Finger PinchLeftover()
        {
            foreach (Finger f in _fingers.Values)
            {
                if (f.Owner == Owner.Ignored && f.PinchLeftover)
                {
                    return f;
                }
            }

            return null;
        }

        private static bool TryStartPinch(Finger second)
        {
            if (!CanPinch() || !_fingers.TryGetValue(_pointerFinger, out Finger first))
            {
                return false;
            }

            // A press on the world zooms the world; a press on a window zooms that window.
            Game.UI.Gumps.Gump gump = null;

            if (!first.OnWorld)
            {
                gump = first.OnGump;

                // Only while its press is still held back: once the game has it, a control may be
                // pressed (buttons, scroll arrows, sliders, skill rows) and can't be un-pressed.
                if (gump == null || gump.IsDisposed || !GumpScale.Scalable(gump) || _downFinger == first.Id || !IsHeld(first.Id))
                {
                    return false;
                }
            }

            // Undo the first finger's click: drop whatever of it hasn't reached the game yet (other
            // fingers' events stay - dropping a queued release would leave the button held).
            int queued = _pointerQueue.Count;

            for (int i = 0; i < queued; i++)
            {
                PointerEvent e = _pointerQueue.Dequeue();

                if (e.Finger != first.Id)
                {
                    _pointerQueue.Enqueue(e);
                }
            }

            if (_downFinger == first.Id)
            {
                // World: release off-screen, where the release picks nothing. (A window press is
                // never taken over once it reached the game: its control may already be pressed,
                // scrolling or dragging - see the check above.)
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Move, OffScreen, _frame, first.Id));
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Up, OffScreen, _frame + POINTER_FRAME_DELAY, first.Id));
            }
            else
            {
                _parkPointerAt = _frame + 1; // the pointer was placed under the finger on touch-down
            }

            _pointerFinger = long.MinValue;

            return BeginPinch(first, second, gump);
        }

        private static Game.UI.Gumps.Gump _pinchGump;
        private static float _pinchStartScale;
        private static Vector2 _pinchContent; // window: the point under the fingers' midpoint, in the window's own units

        private static bool BeginPinch(Finger a, Finger b, Game.UI.Gumps.Gump gump)
        {
            if (gump != null && gump.IsDisposed)
            {
                return false;
            }

            a.Owner = Owner.Pinch;
            b.Owner = Owner.Pinch;
            a.PinchLeftover = false;
            _fingers[a.Id] = a;
            _fingers[b.Id] = b;
            _pinchA = a.Id;
            _pinchB = b.Id;
            _pinchStartDistance = Math.Max(1f, Distance(a.Pos, b.Pos));
            _pinchGump = gump;

            if (gump != null)
            {
                _pinchStartScale = GumpScale.Of(gump);
                Vector2 mid = Mid(a.Pos, b.Pos);
                _pinchContent = new Vector2((mid.X - gump.X) / _pinchStartScale, (mid.Y - gump.Y) / _pinchStartScale);
            }
            else
            {
                // A profile zoom outside the pinch range (desktop allows 0.5-2.5) would jump on the first move.
                _pinchStartZoom = MathHelper.Clamp(((Game.Scenes.GameScene)Client.Game.Scene).Camera.Zoom, PINCH_ZOOM_MIN, PINCH_ZOOM_MAX);
            }

            return true;
        }

        private static void UpdatePinch()
        {
            if (!_fingers.TryGetValue(_pinchA, out Finger a) || !_fingers.TryGetValue(_pinchB, out Finger b) ||
                !(Client.Game.Scene is Game.Scenes.GameScene scene))
            {
                return;
            }

            float spread = Math.Max(1f, Distance(a.Pos, b.Pos)) / _pinchStartDistance;

            if (_pinchGump != null)
            {
                if (_pinchGump.IsDisposed)
                {
                    return;
                }

                // Fingers apart = bigger, up to what fits on the screen. The point that was under
                // the fingers stays under them, so moving both fingers also moves the window.
                float fit = Math.Min(ScreenW / (float)Math.Max(1, _pinchGump.Width), ScreenH / (float)Math.Max(1, _pinchGump.Height));
                GumpScale.Set(_pinchGump, Math.Min(_pinchStartScale * spread, Math.Max(1f, fit)));
                float scale = GumpScale.Of(_pinchGump);
                Vector2 mid = Mid(a.Pos, b.Pos);
                _pinchGump.X = (int)MathF.Round(mid.X - _pinchContent.X * scale);
                _pinchGump.Y = (int)MathF.Round(mid.Y - _pinchContent.Y * scale);

                return;
            }

            // Fingers apart = zoom in (a smaller Camera.Zoom).
            scene.Camera.Zoom = MathHelper.Clamp(_pinchStartZoom / spread, PINCH_ZOOM_MIN, PINCH_ZOOM_MAX);
        }

        private static void EndPinch(long liftedId)
        {
            if (liftedId != _pinchA && liftedId != _pinchB)
            {
                return;
            }

            long other = liftedId == _pinchA ? _pinchB : _pinchA;

            if (_fingers.TryGetValue(other, out Finger f))
            {
                f.Owner = Owner.Ignored; // does nothing until it lifts, or a new finger re-pinches with it
                f.PinchLeftover = true;
                f.PinchGump = _pinchGump;
            }

            if (_pinchGump != null && !_pinchGump.IsDisposed)
            {
                KeepOnScreen(_pinchGump, ScreenW, ScreenH);
                GumpScale.Save();
            }

            _pinchGump = null;
            _pinchA = _pinchB = long.MinValue;
        }

        private static Vector2 Mid(Point a, Point b) => new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

        private static float Distance(Point a, Point b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        public static bool OnUp(long id, Point pos)
        {
            if (!Enabled || !_fingers.TryGetValue(id, out Finger f))
            {
                return false;
            }

            _fingers.Remove(id);
            f.Pos = pos;

            switch (f.Owner)
            {
                case Owner.Joystick:
                    JoystickActive = false;
                    JoystickOffset = Vector2.Zero;

                    break;

                case Owner.Button:
                    PressedButton = -1;

                    if (f.Button == -2)
                    {
                        AddButton();
                    }
                    else if (f.Button == -3)
                    {
                        if (TouchHudGump.TargetPanel.Contains(pos))
                        {
                            MobileActions.Run(Client.Game.UO.World, "healthbar_target");
                        }
                    }
                    else if (Current != null && f.Button < Current.Buttons.Count && ButtonAt(pos) == f.Button)
                    {
                        MobileActions.Run(Client.Game.UO.World, Current.Buttons[f.Button].Action);
                    }

                    break;

                case Owner.EditDrag:
                    PressedButton = -1;

                    if (!f.Moved)
                    {
                        if (f.Joystick)
                        {
                            // tapping the joystick in edit mode cycles its size
                            Current.JoystickSize = Current.JoystickSize >= 0.40f ? 0.24f : Current.JoystickSize + 0.05f;
                            Revision++;
                        }
                        else if (f.Button >= 0 && Current != null && f.Button < Current.Buttons.Count)
                        {
                            string action = Current.Buttons[f.Button].Action;

                            // The Edit and Layout buttons keep working in edit mode, so you can
                            // leave it, or switch to another layout to edit that one too.
                            if (action is "edit_layout" or "layout_next")
                            {
                                MobileActions.Run(Client.Game.UO.World, action);
                            }
                            else
                            {
                                ButtonEditGump.Open(Client.Game.UO.World, f.Button);
                            }
                        }
                    }

                    break;

                case Owner.Pointer:
                    if (!f.LongPressFired)
                    {
                        _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Up, pos, _frame, id));
                    }

                    _pointerFinger = long.MinValue;

                    break;

                case Owner.Pinch:
                    EndPinch(id);

                    break;
            }

            return true;
        }

        public static bool OwnsFinger(long id) => _fingers.ContainsKey(id);

        // ---------- per frame ----------

        public static void Update()
        {
            if (!Enabled)
            {
                return;
            }

            LoginLayout.Update();

            if (Client.Game.UO.World != null && Client.Game.UO.World.InGame)
            {
                EnsureLoaded();
                TouchHudGump.Ensure(Client.Game.UO.World);
                PhoneDefaults.Apply(Client.Game.UO.World);
                PlayerTimers.Update(Client.Game.UO.World);
                PackPrimer.Update(Client.Game.UO.World);
                Agents.Update(Client.Game.UO.World);
                AutoLoot.Update(Client.Game.UO.World);
                SmartTargeting.Update(Client.Game.UO.World);
            }
            else if (Layouts != null)
            {
                Unload();
            }

            DrainPointerQueue();
            DropFinishedMaps();
            ParkPointer();
            SyncKeyboard();
            CheckLongPress();
            Walk();
            MobileMacroRunner.Update(Client.Game.UO.World);
            StealthCounter.Update(Client.Game.UO.World);
            ScreenKeyboard.Update();
            HousePlacementGhost.Update(Client.Game.UO.World);
            LoginLayout.Update(); // again: a window a tap opened just now is placed before it is drawn
        }

        private static void DrainPointerQueue()
        {
            while (_pointerQueue.Count > 0 && _pointerQueue.Peek().ReadyFrame <= _frame && !IsHeld(_pointerQueue.Peek().Finger))
            {
                PointerEvent e = _pointerQueue.Dequeue();
                SetPointer(MapPointer(e.Pos, e.Finger));

                switch (e.Type)
                {
                    case PointerEventType.Down:
                        // placing a house: a finger on the world drags its ghost, it doesn't place it (HousePlacementGhost)
                        if (HousePlacementGhost.Active(Client.Game.UO.World) && UIManager.IsWorldAt(e.Pos))
                        {
                            _ghostFinger = e.Finger;
                            Client.Game.DispatchMouseMotion();
                            _downFinger = e.Finger;

                            break;
                        }

                        if (e.Finger == _ghostFinger)
                        {
                            _ghostFinger = long.MinValue; // (a reused touch id: this press is a normal one)
                        }

                        Client.Game.DispatchMouseDown(MouseButtonType.Left);
                        _downFinger = e.Finger;
                        _keyboardWanted = UIManager.MouseOverControl is Game.UI.Controls.StbTextBox;

                        break;

                    case PointerEventType.Move:
                        Client.Game.DispatchMouseMotion();

                        break;

                    case PointerEventType.Up:
                        if (e.Finger == _ghostFinger)
                        {
                            _ghostFinger = long.MinValue; // the ghost stays where the finger left it
                        }
                        else
                        {
                            Client.Game.DispatchMouseUp(MouseButtonType.Left);
                        }

                        _downFinger = long.MinValue;
                        _parkPointerAt = _frame + 2;

                        break;

                    case PointerEventType.RightClick:
                        Client.Game.DispatchMouseDown(MouseButtonType.Right);
                        Client.Game.DispatchMouseUp(MouseButtonType.Right);

                        break;
                }
            }
        }

        private static long _parkPointerAt;
        private static long _ghostFinger = long.MinValue;

        /// <summary>
        /// A lifted finger leaves no pointer behind: once the tap has been handled, move the virtual
        /// pointer off-screen so hover highlights and tooltips don't stick to the last tap.
        /// </summary>
        private static void ParkPointer()
        {
            if (_parkPointerAt == 0 || _frame < _parkPointerAt || _pointerFinger != long.MinValue || _pointerQueue.Count > 0)
            {
                return;
            }

            _parkPointerAt = 0;

            PointerGump = null;
            _physicalPointer = null;

            if (Mouse.TouchPosition.HasValue)
            {
                Mouse.TouchPosition = OffScreen;
                Mouse.Update();
            }
        }

        /// <summary>
        /// Where the game should see a finger. Over a zoomed window: in that window's own layout.
        /// Carrying an item: whichever window is under the finger now (so a drop from a zoomed
        /// backpack onto the world or another window lands under the finger). Moving a zoomed
        /// window: 1:1 with the finger (the drag is measured in the window's own units).
        /// </summary>
        private static Point MapPointer(Point pos, long finger)
        {
            PointerGump = null;
            _physicalPointer = pos == OffScreen ? (Point?)null : pos;

            if (pos == OffScreen || !(Client.Game.Scene is Game.Scenes.GameScene))
            {
                return pos;
            }

            if (Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                Game.UI.Gumps.Gump under = UIManager.GumpAtPhysical(pos, out Point local);

                if (under != null && GumpScale.Of(under) != 1f)
                {
                    PointerGump = under;

                    return local;
                }

                return pos;
            }

            if (!_maps.TryGetValue(finger, out PointerMap m) || m.Gump.IsDisposed)
            {
                return pos;
            }

            if (UIManager.IsDragging && UIManager.DraggingControl != null && UIManager.DraggingControl != m.Gump)
            {
                // Dragging something out of the zoomed window (a split-stack menu, a spell or skill
                // icon): from here on the finger is on the screen, not in the window.
                _maps.Remove(finger);

                return pos;
            }

            PointerGump = m.Gump;

            if (UIManager.IsDragging && UIManager.DraggingControl == m.Gump)
            {
                return m.DownLogical + (pos - m.DownPhysical);
            }

            return new Point(m.Origin.X + (int)MathF.Round((pos.X - m.Origin.X) / m.Scale),
                             m.Origin.Y + (int)MathF.Round((pos.Y - m.Origin.Y) / m.Scale));
        }

        private static bool IsHeld(long finger) =>
            _fingers.TryGetValue(finger, out Finger f) && f.HoldUntil != 0 && Time.Ticks < f.HoldUntil;

        /// <summary>A lifted finger's mapping goes once none of its events are left to dispatch.</summary>
        private static void DropFinishedMaps()
        {
            if (_maps.Count == 0)
            {
                return;
            }

            _mapsToDrop.Clear();

            foreach (long id in _maps.Keys)
            {
                if (_fingers.ContainsKey(id))
                {
                    continue;
                }

                bool queued = false;

                foreach (PointerEvent e in _pointerQueue)
                {
                    queued |= e.Finger == id;
                }

                if (!queued)
                {
                    _mapsToDrop.Add(id);
                }
            }

            foreach (long id in _mapsToDrop)
            {
                _maps.Remove(id);
            }
        }

        private static Control _keyboardFor;

        private static void SyncKeyboard()
        {
            Control box = UIManager.KeyboardFocusControl is Control c && !c.IsDisposed ? c : null;

            if (!HasVirtualKeyboard)
            {
                ScreenKeyboard.FakeFor(_keyboardWanted, box); // desktop test of the speech line's keyboard

                return;
            }

            IntPtr window = Client.Game.Window.Handle;
            bool active = SDL3.SDL.SDL_TextInputActive(window);

            // Focus moved to another box (account -> password) while the keyboard is up:
            // restart it so the keyboard type/capitalisation match the new box.
            if (_keyboardWanted && active && box != _keyboardFor)
            {
                SDL3.SDL.SDL_StopTextInput(window);
                active = false;
            }

            if (_keyboardWanted && !active)
            {
                if (box != null)
                {
                    // Tell iOS where the text box is, so SDL slides the view up above the keyboard -
                    // except for the speech line, where the view stays put and ScreenKeyboard raises
                    // the line and the camera instead (an area at the top of the screen: no slide).
                    SetInputArea(window, box, ScreenKeyboard.KeepsViewFor(box));
                }

                ScreenKeyboard.Starting(box);
                StartKeyboard(window, box);
                _keyboardFor = box;
            }
            else if (box != null && ScreenKeyboard.NeedsFallback(active))
            {
                SetInputArea(window, box, false); // no keyboard report came: let SDL slide the view after all
            }
            else if (!_keyboardWanted && active)
            {
                SDL3.SDL.SDL_StopTextInput(window);
                _keyboardFor = null;
            }
        }

        private static void SetInputArea(IntPtr window, Control box, bool keepView)
        {
            if (!SDL3.SDL.SDL_GetWindowSize(window, out int ww, out int wh) || ww <= 0 || wh <= 0)
            {
                return;
            }

            float fx = ww / (float)ScreenW, fy = wh / (float)ScreenH;
            var area = keepView
                ? new SDL3.SDL.SDL_Rect { x = 0, y = 0, w = 1, h = 1 }
                : new SDL3.SDL.SDL_Rect
                {
                    x = (int)(box.ScreenCoordinateX * fx),
                    y = (int)(box.ScreenCoordinateY * fy),
                    w = Math.Max(1, (int)(box.Width * fx)),
                    h = Math.Max(1, (int)(box.Height * fy))
                };
            SDL3.SDL.SDL_SetTextInputArea(window, ref area, 0);
        }

        /// <summary>
        /// SDL3's default keyboard capitalises sentences and autocorrects, which mangles account
        /// names and passwords (first login fails with "incorrect password"). Pick a keyboard per box.
        /// </summary>
        private static void StartKeyboard(IntPtr window, Control box)
        {
            string kind = box?.GetType().Name ?? "";
            bool password = kind == "PasswordStbTextBox";
            bool account = !password && box?.RootParent is Game.UI.Gumps.Login.LoginGump;

            uint props = SDL3.SDL.SDL_CreateProperties();
            SDL3.SDL.SDL_SetNumberProperty(props, SDL3.SDL.SDL_PROP_TEXTINPUT_TYPE_NUMBER,
                (long)(password ? SDL3.SDL.SDL_TextInputType.SDL_TEXTINPUT_TYPE_TEXT_PASSWORD_HIDDEN
                    : account ? SDL3.SDL.SDL_TextInputType.SDL_TEXTINPUT_TYPE_TEXT_USERNAME
                    : SDL3.SDL.SDL_TextInputType.SDL_TEXTINPUT_TYPE_TEXT));
            SDL3.SDL.SDL_SetNumberProperty(props, SDL3.SDL.SDL_PROP_TEXTINPUT_CAPITALIZATION_NUMBER,
                (long)(password || account ? SDL3.SDL.SDL_Capitalization.SDL_CAPITALIZE_NONE : SDL3.SDL.SDL_Capitalization.SDL_CAPITALIZE_SENTENCES));
            SDL3.SDL.SDL_SetBooleanProperty(props, SDL3.SDL.SDL_PROP_TEXTINPUT_AUTOCORRECT_BOOLEAN, false);
            SDL3.SDL.SDL_StartTextInputWithProperties(window, props);
            SDL3.SDL.SDL_DestroyProperties(props);
        }

        private static void CheckLongPress()
        {
            if (_pointerFinger == long.MinValue || !_fingers.TryGetValue(_pointerFinger, out Finger f))
            {
                return;
            }

            if (f.Moved || f.LongPressFired || Time.Ticks - f.DownTime < LONG_PRESS_MS || f.Id == _ghostFinger)
            {
                return; // (a finger resting on a house ghost is looking, not right-clicking)
            }

            // Long press over a gump = right click (UO's "close this window").
            // Over the world it does nothing, so a slow tap still works as a click.
            if (UIManager.MouseOverControl != null)
            {
                f.LongPressFired = true;
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Up, f.Pos, _frame, f.Id));
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.RightClick, f.Pos, _frame, f.Id));
            }
        }

        private static void SetPointer(Point uiPos)
        {
            Mouse.TouchPosition = uiPos;
            Mouse.Update();
        }

        private static void UpdateJoystick(Point pos)
        {
            Point c = JoystickCenter;
            float r = JoystickRadius;
            Vector2 v = new Vector2((pos.X - c.X) / r, (pos.Y - c.Y) / r);

            if (v.LengthSquared() > 1f)
            {
                v.Normalize();
            }

            JoystickOffset = v;
        }

        private static void Walk()
        {
            if (!JoystickActive || Client.Game.UO.World == null || !Client.Game.UO.World.InGame || Client.Game.UO.World.Player == null)
            {
                return;
            }

            float mag = JoystickOffset.Length();

            if (mag < JOY_DEADZONE)
            {
                return;
            }

            if (Client.Game.UO.World.Player.Pathfinder.AutoWalking)
            {
                Client.Game.UO.World.Player.Pathfinder.StopAutoWalk();
            }

            // Same screen-offset -> UO direction mapping the client uses for right-mouse walking.
            Direction facing = (Direction)GameCursor.GetMouseDirection(0, 0, (int)(JoystickOffset.X * 1000), (int)(JoystickOffset.Y * 1000), 1);

            if (facing == Direction.North)
            {
                facing = (Direction)8;
            }

            Client.Game.UO.World.Player.Walk(facing - 1, mag >= JOY_RUN);
        }

        // ---------- edit mode ----------

        private static void MoveEdited(Finger f, Point pos)
        {
            ActionLayout layout = Current;

            if (layout == null)
            {
                return;
            }

            Rectangle safe = Safe;
            float nx = MathHelper.Clamp((pos.X - safe.X) / (float)Math.Max(1, safe.Width), 0.02f, 0.98f);
            float ny = MathHelper.Clamp((pos.Y - safe.Y) / (float)Math.Max(1, safe.Height), 0.02f, 0.98f);

            // Edits apply to the orientation being edited; the other keeps its own positions.
            bool portrait = IsPortrait;

            if (f.Joystick)
            {
                if (portrait)
                {
                    layout.PJoystickX = nx;
                    layout.PJoystickY = ny;
                }
                else
                {
                    layout.JoystickX = nx;
                    layout.JoystickY = ny;
                }
            }
            else if (f.Button >= 0 && f.Button < layout.Buttons.Count)
            {
                ActionButtonDef b = layout.Buttons[f.Button];

                if (portrait)
                {
                    b.PX = nx;
                    b.PY = ny;
                }
                else
                {
                    b.X = nx;
                    b.Y = ny;
                }
            }

            Revision++;
        }

        private static void AddButton()
        {
            ActionLayout layout = Current;

            if (layout == null)
            {
                return;
            }

            layout.Buttons.Add(new ActionButtonDef { Label = "New", Action = "target_nearest", X = 0.5f, Y = 0.5f, PX = 0.5f, PY = 0.5f, Size = 0.13f });
            Revision++;
            ButtonEditGump.Open(Client.Game.UO.World, layout.Buttons.Count - 1);
        }
    }
}
