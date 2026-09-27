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

        private enum Owner { Joystick, Button, Pointer, EditDrag }

        private sealed class Finger
        {
            public long Id;
            public Owner Owner;
            public int Button = -1;
            public Point Start, Pos;
            public uint DownTime;
            public bool Moved, LongPressFired;
            public bool Joystick; // EditDrag of the joystick itself
        }

        private enum PointerEventType { Down, Move, Up, RightClick }

        private readonly struct PointerEvent
        {
            public PointerEvent(PointerEventType type, Point pos, long frame)
            {
                Type = type;
                Pos = pos;
                ReadyFrame = frame;
            }

            public readonly PointerEventType Type;
            public readonly Point Pos;
            public readonly long ReadyFrame;
        }

        private static readonly Dictionary<long, Finger> _fingers = new Dictionary<long, Finger>();
        private static readonly Queue<PointerEvent> _pointerQueue = new Queue<PointerEvent>();
        private static long _pointerFinger = long.MinValue;
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
            JoystickActive = false;
            PressedButton = -1;
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
                Rectangle full = new Rectangle(0, 0, ScreenW, ScreenH);

                if (!HasVirtualKeyboard) // phones/tablets only
                {
                    return full;
                }

                IntPtr window = Client.Game.Window.Handle;

                if (!SDL3.SDL.SDL_GetWindowSize(window, out int ww, out int wh) || ww <= 0 || wh <= 0 ||
                    !SDL3.SDL.SDL_GetWindowSafeArea(window, out SDL3.SDL.SDL_Rect r) || r.w <= 0 || r.h <= 0)
                {
                    return full;
                }

                float fx = ScreenW / (float)ww, fy = ScreenH / (float)wh;

                return new Rectangle((int)(r.x * fx), (int)(r.y * fy), (int)(r.w * fx), (int)(r.h * fy));
            }
        }

        private static Point FromLayout(float x, float y)
        {
            Rectangle safe = Safe;

            return new Point(safe.X + (int)(x * safe.Width), safe.Y + (int)(y * safe.Height));
        }

        public static Point ButtonCenter(ActionButtonDef b) => FromLayout(b.X, b.Y);

        public static int ButtonRadius(ActionButtonDef b) => Math.Max(14, (int)(b.Size * ScreenMin * 0.5f));

        public static Point JoystickCenter => Current == null ? Point.Zero : FromLayout(Current.JoystickX, Current.JoystickY);

        public static int JoystickRadius => Current == null ? 0 : Math.Max(30, (int)(Current.JoystickSize * ScreenMin * 0.5f));

        /// <summary>In edit mode an extra "+" button sits at the top centre.</summary>
        public static Point AddButtonCenter => FromLayout(0.5f, 0.07f);

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

        /// <summary>While an editor window is open the HUD is hidden and every finger is a pointer.</summary>
        public static bool Suppressed => UIManager.GetGump<MacroEditorGump>() != null || UIManager.GetGump<ButtonEditGump>() != null;

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

                    if (!JoystickActive && InCircle(pos, JoystickCenter, (int)(JoystickRadius * 1.25f)))
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

            // Only one finger can be the pointer; extra fingers are ignored.
            if (_pointerFinger != long.MinValue)
            {
                return true;
            }

            f.Owner = Owner.Pointer;
            _fingers[id] = f;
            _pointerFinger = id;
            SetPointer(pos);
            _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Down, pos, _frame + POINTER_FRAME_DELAY));

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
                    _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Move, pos, _frame));

                    break;
            }

            return true;
        }

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
                        _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Up, pos, _frame));
                    }

                    _pointerFinger = long.MinValue;

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

            if (Client.Game.UO.World != null && Client.Game.UO.World.InGame)
            {
                EnsureLoaded();
                TouchHudGump.Ensure(Client.Game.UO.World);
            }
            else if (Layouts != null)
            {
                Unload();
            }

            DrainPointerQueue();
            SyncKeyboard();
            CheckLongPress();
            Walk();
            MobileMacroRunner.Update(Client.Game.UO.World);
        }

        private static void DrainPointerQueue()
        {
            while (_pointerQueue.Count > 0 && _pointerQueue.Peek().ReadyFrame <= _frame)
            {
                PointerEvent e = _pointerQueue.Dequeue();
                SetPointer(e.Pos);

                switch (e.Type)
                {
                    case PointerEventType.Down:
                        Client.Game.DispatchMouseDown(MouseButtonType.Left);
                        _keyboardWanted = UIManager.MouseOverControl is Game.UI.Controls.StbTextBox;

                        break;

                    case PointerEventType.Move:
                        Client.Game.DispatchMouseMotion();

                        break;

                    case PointerEventType.Up:
                        Client.Game.DispatchMouseUp(MouseButtonType.Left);

                        break;

                    case PointerEventType.RightClick:
                        Client.Game.DispatchMouseDown(MouseButtonType.Right);
                        Client.Game.DispatchMouseUp(MouseButtonType.Right);

                        break;
                }
            }
        }

        private static void SyncKeyboard()
        {
            if (!HasVirtualKeyboard)
            {
                return;
            }

            bool active = Microsoft.Xna.Framework.Input.TextInputEXT.IsTextInputActive();

            if (_keyboardWanted && !active)
            {
                // Tell iOS where the text box is, so SDL slides the view up above the keyboard.
                if (UIManager.KeyboardFocusControl is Control box && !box.IsDisposed)
                {
                    IntPtr window = Client.Game.Window.Handle;

                    if (SDL3.SDL.SDL_GetWindowSize(window, out int ww, out int wh) && ww > 0 && wh > 0)
                    {
                        float fx = ww / (float)ScreenW, fy = wh / (float)ScreenH;
                        var area = new SDL3.SDL.SDL_Rect
                        {
                            x = (int)(box.ScreenCoordinateX * fx),
                            y = (int)(box.ScreenCoordinateY * fy),
                            w = Math.Max(1, (int)(box.Width * fx)),
                            h = Math.Max(1, (int)(box.Height * fy))
                        };
                        SDL3.SDL.SDL_SetTextInputArea(window, ref area, 0);
                    }
                }

                Microsoft.Xna.Framework.Input.TextInputEXT.StartTextInput();
            }
            else if (!_keyboardWanted && active)
            {
                Microsoft.Xna.Framework.Input.TextInputEXT.StopTextInput();
            }
        }

        private static void CheckLongPress()
        {
            if (_pointerFinger == long.MinValue || !_fingers.TryGetValue(_pointerFinger, out Finger f))
            {
                return;
            }

            if (f.Moved || f.LongPressFired || Time.Ticks - f.DownTime < LONG_PRESS_MS)
            {
                return;
            }

            // Long press over a gump = right click (UO's "close this window").
            // Over the world it does nothing, so a slow tap still works as a click.
            if (UIManager.MouseOverControl != null)
            {
                f.LongPressFired = true;
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.Up, f.Pos, _frame));
                _pointerQueue.Enqueue(new PointerEvent(PointerEventType.RightClick, f.Pos, _frame));
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

            if (f.Joystick)
            {
                layout.JoystickX = nx;
                layout.JoystickY = ny;
            }
            else if (f.Button >= 0 && f.Button < layout.Buttons.Count)
            {
                layout.Buttons[f.Button].X = nx;
                layout.Buttons[f.Button].Y = ny;
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

            layout.Buttons.Add(new ActionButtonDef { Label = "New", Action = "target_nearest", X = 0.5f, Y = 0.5f, Size = 0.13f });
            Revision++;
            ButtonEditGump.Open(Client.Game.UO.World, layout.Buttons.Count - 1);
        }
    }
}
