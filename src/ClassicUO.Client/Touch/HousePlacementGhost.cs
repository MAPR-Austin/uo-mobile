// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Network;
using Microsoft.Xna.Framework;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Placing a house on the phone. On desktop the house's ghost follows the mouse and a click
    /// places it; on a phone the first tap placed it, so it couldn't be seen first. Here, while a
    /// house placement cursor is up, a finger on the world drags the ghost instead (it starts a few
    /// steps down the screen from you; the HUD steps aside meanwhile), a bar offers Place and Cancel,
    /// and the server says whether the spot
    /// would take the house: the ghost turns red where it can't go, with the reason on the bar.
    /// The server's check is servuo Scripts/Custom/UOMobile/PlacementCheck.cs: 0xBF sub-command
    /// 0x7A55 asks (sequence, the target the tap would send), 0x7A56 answers (sequence, result) -
    /// keep both ends' numbers in step. A server without it never answers: the ghost stays its own
    /// colour, "Checking..." stays up, and Place still works (the server checks it then).
    /// </summary>
    internal static class HousePlacementGhost
    {
        public const ushort Query = 0x7A55;
        public const ushort Reply = 0x7A56;

        /// <summary>UO's plain red: the house can't go there.</summary>
        private const ushort BadHue = 0x0021;

        private const uint SendEveryMs = 120, ResendAfterMs = 1500;
        private const byte Unknown = 255;

        private static GameObject _anchor; // what the ghost stands on: what Place targets
        private static ushort _graphic, _x, _y;
        private static short _z;
        private static bool _dirty;
        private static ushort _seq;
        private static byte _result = Unknown;
        private static uint _sentAt;
        private static HousePlacementBar _bar;

        public static bool Active(World world)
        {
            return TouchInput.Enabled && world != null && world.InGame && Client.Game.Scene is GameScene &&
                   world.TargetManager.IsTargeting && world.TargetManager.TargetingState == CursorTarget.MultiPlacement &&
                   world.CustomHouseManager == null && world.TargetManager.MultiTargetInfo != null;
        }

        /// <summary>
        /// GameScene: what the ghost should stand on this frame when no finger is on the world - where
        /// it was left, or at first a spot a few steps in front of you.
        /// </summary>
        public static GameObject RestingAnchor(World world)
        {
            if (!Active(world))
            {
                return null;
            }

            if (_anchor != null && !_anchor.IsDestroyed)
            {
                return _anchor;
            }

            // the house a few steps straight down the screen from you (south-east), all of it in view: the
            // cursor's spot sits at the house's offset from its centre
            MultiTargetInfo info = world.TargetManager.MultiTargetInfo;

            return LandAt(world, world.Player.X + 4 + (short)info.XOff, world.Player.Y + 4 + (short)info.YOff);
        }

        /// <summary>GameScene put the ghost on <paramref name="anchor" />: ask the server about the new spot.</summary>
        public static void Moved(GameObject anchor)
        {
            if (anchor == null || !TouchInput.Enabled)
            {
                return;
            }

            (ushort graphic, ushort x, ushort y, short z) = TargetOf(anchor);

            if (ReferenceEquals(anchor, _anchor) && graphic == _graphic && x == _x && y == _y && z == _z)
            {
                return;
            }

            _anchor = anchor;
            (_graphic, _x, _y, _z) = (graphic, x, y, z);
            _dirty = true;
            _result = Unknown;
        }

        /// <summary>The ghost's hue: red when the server says the house can't go there.</summary>
        public static ushort Hue(ushort normal)
        {
            return _result != Unknown && _result != 0 && TouchInput.Enabled ? BadHue : normal;
        }

        /// <summary>PacketHandlers (0xBF 0x7A56): the server's answer.</summary>
        public static void OnReply(ushort seq, byte result)
        {
            if (seq == _seq)
            {
                _result = result;
            }
        }

        /// <summary>Every frame (TouchInput.Update): the bar, and the question for the latest spot.</summary>
        public static void Update(World world)
        {
            if (!Active(world))
            {
                if (_bar != null || _anchor != null)
                {
                    _bar?.Dispose();
                    _bar = null;
                    _anchor = null;
                    _result = Unknown;
                    _dirty = false;
                }

                return;
            }

            if (_bar == null || _bar.IsDisposed)
            {
                UIManager.Add(_bar = new HousePlacementBar(world));
            }

            if (_anchor != null && (_dirty && Time.Ticks - _sentAt >= SendEveryMs || _result == Unknown && Time.Ticks - _sentAt >= ResendAfterMs))
            {
                Ask();
            }

            _bar.Show(_anchor == null ? "Drag on the ground to move the house" : Reason(_result), _result == 0);
        }

        public static void Place(World world)
        {
            if (!Active(world) || _anchor == null)
            {
                return;
            }

            if (_result != 0 && _result != Unknown)
            {
                GameActions.Print(world, "The house can't go there: " + Reason(_result).ToLowerInvariant(), 0x0021);

                return;
            }

            (ushort graphic, ushort x, ushort y, short z) = TargetOf(_anchor);
            world.TargetManager.Target(graphic, x, y, z, _anchor is Land land && land.TileData.IsWet);
        }

        public static void Cancel(World world)
        {
            if (Active(world))
            {
                world.TargetManager.CancelTarget();
            }
        }

        private static string Reason(byte result)
        {
            switch (result)
            {
                case Unknown: return "Checking...";
                case 0: return "This spot is clear - tap Place";
                case 1: return "Can't build here: something is in the way or the ground isn't right";
                case 2: return "Houses can't be built in this area";
                case 3: return "Too far away: bring the house closer";
                case 7: return "Out of sight: move it where you can see it";
                case 4: return "Castles and keeps can't go here";
                case 5: return "No building here right now";
                default: return "Can't build here";
            }
        }

        private static void Ask()
        {
            _seq++;
            _dirty = false;
            _sentAt = Time.Ticks;

            if (NetClient.Socket == null || !NetClient.Socket.IsConnected)
            {
                return;
            }

            var writer = new StackDataWriter(16);
            writer.WriteUInt8(0xBF);
            writer.WriteZero(2);
            writer.WriteUInt16BE(Query);
            writer.WriteUInt16BE(_seq);
            writer.WriteUInt16BE(_graphic);
            writer.WriteUInt16BE(_x);
            writer.WriteUInt16BE(_y);
            writer.WriteInt16BE(_z);
            writer.Seek(1, SeekOrigin.Begin);
            writer.WriteUInt16BE((ushort)writer.BytesWritten);
            NetClient.Socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        /// <summary>What a tap on <paramref name="o" /> sends while placing (GameSceneInputHandler): ground or a static; anything else, the ground under it.</summary>
        private static (ushort graphic, ushort x, ushort y, short z) TargetOf(GameObject o)
        {
            if (o is Land land)
            {
                return (0, land.X, land.Y, land.Z);
            }

            if (o is Static || o is Multi)
            {
                return (o.Graphic, o.X, o.Y, o.Z);
            }

            Land under = LandAt(o.World, o.X, o.Y) as Land;

            return under != null ? ((ushort)0, under.X, under.Y, (short)under.Z) : ((ushort)0, o.X, o.Y, (short)o.Z);
        }

        private static GameObject LandAt(World world, int x, int y)
        {
            for (GameObject o = world.Map.GetTile(x, y); o != null; o = o.TNext)
            {
                if (o is Land)
                {
                    return o;
                }
            }

            return null;
        }

    }

    /// <summary>The bar under the house ghost: what the server says about the spot, Place and Cancel.</summary>
    internal sealed class HousePlacementBar : Gump
    {
        private const int ID_PLACE = 1, ID_CANCEL = 2;
        private const int W = 420, H = 62;

        private readonly Label _status;
        private string _shown;
        private bool _shownGood;

        public HousePlacementBar(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = true;
            CanCloseWithRightClick = false;
            CanCloseWithEsc = false;

            Width = W;
            Height = H;

            Add(new AlphaBlendControl(0.8f) { Width = W, Height = H });
            Add(_status = new Label("", true, 0x0481, W - 20, 1) { X = 10, Y = 6 });
            Add(new NiceButton(W / 2 - 150, H - 30, 130, 26, ButtonAction.Activate, "Place", hue: 0x0044) { ButtonParameter = ID_PLACE, IsSelectable = false });
            Add(new NiceButton(W / 2 + 20, H - 30, 130, 26, ButtonAction.Activate, "Cancel", hue: 0x0021) { ButtonParameter = ID_CANCEL, IsSelectable = false });
        }

        public void Show(string text, bool good)
        {
            Rectangle safe = TouchInput.Safe;
            X = safe.X + (safe.Width - W) / 2;
            Y = safe.Bottom - H - 6;

            if (text != _shown || good != _shownGood)
            {
                _shown = text;
                _shownGood = good;
                _status.Text = text;
                _status.Hue = good ? (ushort)0x0044 : (ushort)0x0481;
            }
        }

        public override void OnButtonClick(int buttonID)
        {
            switch (buttonID)
            {
                case ID_PLACE:
                    HousePlacementGhost.Place(World);

                    break;

                case ID_CANCEL:
                    HousePlacementGhost.Cancel(World);

                    break;
            }
        }
    }
}
