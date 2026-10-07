// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
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
    /// Furniture deeds work the same way (addon mode, build 48): with the deed's cursor the server sends
    /// 0x7A57 - the cursor's id and the pieces (servuo AddonPreview.cs) - and the ghost is those pieces on
    /// the floor at your level under the finger; the same question is answered for the deed.
    /// </summary>
    internal static class HousePlacementGhost
    {
        public const ushort Query = 0x7A55;
        public const ushort Reply = 0x7A56;
        public const ushort AddonPieces = 0x7A57;

        // a furniture deed's cursor: its id, its pieces (graphic, x/y/z offset, hue) and their ghost in the world
        private static uint _addonCursor;
        private static List<(ushort Graphic, short X, short Y, short Z, ushort Hue)> _pieces;
        private static readonly List<Multi> _preview = new List<Multi>();
        private static int _previewX = -1, _previewY, _previewZ, _previewHue = -1;

        /// <summary>UO's plain red: the house can't go there.</summary>
        private const ushort BadHue = 0x0021;

        private const uint SendEveryMs = 120, ResendAfterMs = 600; // (the server answers at most every 100 ms)
        private const byte Unknown = 255;

        private static GameObject _anchor; // what the ghost stands on: what Place targets
        private static ushort _graphic, _x, _y;
        private static short _z;
        private static bool _dirty;
        private static ushort _seq;
        private static byte _result = Unknown; // the last answer: kept (and shown) while a newer spot is asked about
        private static bool _answered; // _result is the answer for the spot the ghost is on now
        private static uint _sentAt;
        private static ushort _model; // the cursor's house: a new one means new answers
        private static HousePlacementBar _bar;

        /// <summary>
        /// The server says the house can't go there (a reason the bar names). 6 ("not placing a house")
        /// and unknown codes are no opinion: a boat's cursor looks the same and its ghost stays plain.
        /// </summary>
        private static bool Cannot(byte result) => result >= 1 && result <= 5 || result == 7 || result >= 8 && result <= 10;

        public static bool Active(World world) => HouseActive(world) || AddonActive(world);

        private static bool HouseActive(World world)
        {
            return TouchInput.Enabled && world != null && world.InGame && Client.Game.Scene is GameScene &&
                   world.TargetManager.IsTargeting && world.TargetManager.TargetingState == CursorTarget.MultiPlacement &&
                   world.CustomHouseManager == null && world.TargetManager.MultiTargetInfo != null;
        }

        /// <summary>A furniture deed's cursor is up and its pieces came with it.</summary>
        private static bool AddonActive(World world)
        {
            return TouchInput.Enabled && world != null && world.InGame && Client.Game.Scene is GameScene && _pieces != null &&
                   world.TargetManager.IsTargeting && world.TargetManager.TargetingState != CursorTarget.MultiPlacement &&
                   world.TargetManager.TargetCursorId == _addonCursor;
        }

        /// <summary>PacketHandlers (0xBF 0x7A57): a furniture deed's cursor and its pieces.</summary>
        public static void OnAddonPieces(uint cursor, List<(ushort, short, short, short, ushort)> pieces)
        {
            ClearPreview();
            _addonCursor = cursor;
            _pieces = pieces.Count > 0 ? pieces : null;
            _result = Unknown;
            _answered = false;
            _dirty = _anchor != null;
        }

        /// <summary>
        /// GameScene, every frame: the deed's ghost on the floor at your level under the finger (or where it was
        /// left, at first a couple of steps in front of you), red where the server says it won't fit.
        /// </summary>
        public static void UpdateAddon(World world, GameObject under)
        {
            if (!AddonActive(world))
            {
                ClearPreview();

                return;
            }

            // only a finger dragging on the world moves it (not the tap that chose a ladder's facing)
            GameObject pick = under != null && TouchInput.GhostFingerDown ? FloorAt(world, under.X, under.Y, world.Player.Z) ?? under
                : _anchor != null && !_anchor.IsDestroyed ? _anchor
                : FloorAt(world, world.Player.X + 1, world.Player.Y + 2, world.Player.Z);

            if (pick == null)
            {
                return;
            }

            if (!ReferenceEquals(pick, _anchor))
            {
                Moved(pick);
            }

            if (_anchor == null)
            {
                return;
            }

            int top = TopOf(_anchor);
            int hue = Cannot(_result) ? BadHue : -2;

            if (_preview.Count != _pieces.Count)
            {
                ClearPreview();

                foreach (var piece in _pieces)
                {
                    Multi m = Multi.Create(world, piece.Graphic);
                    m.IsHousePreview = true; // see-through, and never what a finger picks
                    _preview.Add(m);
                }
            }

            if (_anchor.X == _previewX && _anchor.Y == _previewY && top == _previewZ && hue == _previewHue)
            {
                return;
            }

            (_previewX, _previewY, _previewZ, _previewHue) = (_anchor.X, _anchor.Y, top, hue);

            for (int i = 0; i < _pieces.Count; i++)
            {
                var piece = _pieces[i];
                _preview[i].Hue = hue == BadHue ? BadHue : piece.Hue;
                _preview[i].SetInWorldTile((ushort)(_anchor.X + piece.X), (ushort)(_anchor.Y + piece.Y), (sbyte)(top + piece.Z));
            }
        }

        private static void ClearPreview()
        {
            foreach (Multi m in _preview)
            {
                m.Destroy();
            }

            _preview.Clear();
            _previewX = -1;
            _previewHue = -1;
        }

        /// <summary>
        /// Where furniture would stand at that tile: the highest floor (or the ground) at about your level - the
        /// floor you're on, not the one above or the ground under a house.
        /// </summary>
        private static GameObject FloorAt(World world, int x, int y, int level)
        {
            GameObject best = null;
            int bestTop = int.MinValue;

            for (GameObject o = world.Map.GetTile(x, y); o != null; o = o.TNext)
            {
                bool floor = o is Land || (o is Static || o is Multi m && !m.IsHousePreview) && IsSurface(o);

                if (!floor)
                {
                    continue;
                }

                int top = TopOf(o);

                if (top <= level + 10 && top > bestTop)
                {
                    best = o;
                    bestTop = top;
                }
            }

            return best;
        }

        private static bool IsSurface(GameObject o)
        {
            return o.Graphic < Client.Game.UO.FileManager.TileData.StaticData.Length && Client.Game.UO.FileManager.TileData.StaticData[o.Graphic].IsSurface;
        }

        /// <summary>The height a piece standing on <paramref name="o" /> stands at (a bridge counts half its height, as the server does).</summary>
        private static int TopOf(GameObject o)
        {
            if (o is Land)
            {
                return o.Z;
            }

            if (o.Graphic < Client.Game.UO.FileManager.TileData.StaticData.Length)
            {
                var data = Client.Game.UO.FileManager.TileData.StaticData[o.Graphic];

                return o.Z + (data.IsBridge ? data.Height / 2 : data.Height);
            }

            return o.Z;
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
            // cursor's spot sits at the house's offset from its centre - but within reach of the cursor
            // (a keep's or castle's offset would put it out of range)
            MultiTargetInfo info = world.TargetManager.MultiTargetInfo;
            int dx = Math.Clamp(4 + (short)info.XOff, -9, 9), dy = Math.Clamp(4 + (short)info.YOff, -9, 9);

            return LandAt(world, world.Player.X + dx, world.Player.Y + dy);
        }

        /// <summary>GameScene put the ghost on <paramref name="anchor" />: ask the server about the new spot.</summary>
        public static void Moved(GameObject anchor)
        {
            if (anchor == null || !TouchInput.Enabled)
            {
                return;
            }

            // a mobile, corpse or effect under the finger would carry the ghost off (or vanish): the ground under it
            if (!(anchor is Land || anchor is Static || anchor is Multi))
            {
                anchor = LandAt(anchor.World, anchor.X, anchor.Y) ?? anchor;
            }

            (ushort graphic, ushort x, ushort y, short z) = TargetOf(anchor);

            if (ReferenceEquals(anchor, _anchor) && graphic == _graphic && x == _x && y == _y && z == _z)
            {
                return;
            }

            _anchor = anchor;
            (_graphic, _x, _y, _z) = (graphic, x, y, z);
            _dirty = true;
            _answered = false;
        }

        /// <summary>The ghost's hue: red when the server says the house can't go there.</summary>
        public static ushort Hue(ushort normal)
        {
            return Cannot(_result) && TouchInput.Enabled ? BadHue : normal;
        }

        /// <summary>PacketHandlers (0xBF 0x7A56): the server's answer.</summary>
        public static void OnReply(ushort seq, byte result)
        {
            if (seq == _seq)
            {
                _result = result;
                _answered = true;
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
                    _answered = false;
                    _dirty = false;
                    _model = 0;
                }

                return;
            }

            // the server swapped the cursor for another house: the answers so far were for the old one
            ushort model = HouseActive(world) ? world.TargetManager.MultiTargetInfo.Model : (ushort)0;

            if (model != _model)
            {
                _model = model;
                _result = Unknown;
                _answered = false;
                _dirty = _anchor != null;
            }

            if (_bar == null || _bar.IsDisposed)
            {
                UIManager.Add(_bar = new HousePlacementBar(world));
            }

            if (_anchor != null && (_dirty && Time.Ticks - _sentAt >= SendEveryMs || !_answered && Time.Ticks - _sentAt >= ResendAfterMs))
            {
                Ask();
            }

            bool addon = AddonActive(world);
            _bar.Show(_anchor == null ? addon ? "Drag on the floor to move it" : "Drag on the ground to move the house" : Reason(_result, addon), _answered && _result == 0);
        }

        public static void Place(World world)
        {
            if (!Active(world))
            {
                return;
            }

            if (_anchor == null)
            {
                GameActions.Print(world, "Drag the house onto the ground first.", 0x0021);

                return;
            }

            // only a known "no" for this very spot stops it (the server checks the placement anyway)
            if (_answered && Cannot(_result))
            {
                GameActions.Print(world, Reason(_result), 0x0021);

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

        private static string Reason(byte result, bool addon = false)
        {
            switch (result)
            {
                case Unknown: return "Checking...";
                case 0: return "This spot is clear - tap Place";
                case 1 when addon: return "Doesn't fit: a wall, furniture, a person or a low ceiling is in the way";
                case 1: return "Can't build here: something is in the way or the ground isn't right";
                case 8: return "Only inside a house you own";
                case 9: return "Too close to a door";
                case 10: return "It hangs on a wall: move it against one";
                case 2: return "Houses can't be built in this area";
                case 3: return "Too far away: bring the house closer";
                case 7: return "Out of sight: move it where you can see it";
                case 4: return "Castles and keeps can't go here";
                case 5: return "No building here right now";
                default: return "Drag to choose the spot, then tap Place"; // a boat, or a server without the check
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
            writer.WriteInt16BE(TargetZ(_graphic, _z));
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

        /// <summary>The z a target on this static carries (TargetManager.Target adds a surface's height on CV_7090+).</summary>
        private static short TargetZ(ushort graphic, short z)
        {
            if (graphic != 0 && graphic < Client.Game.UO.FileManager.TileData.StaticData.Length &&
                Client.Game.UO.Version >= Utility.ClientVersion.CV_7090 && Client.Game.UO.FileManager.TileData.StaticData[graphic].IsSurface)
            {
                return (short)(z + Client.Game.UO.FileManager.TileData.StaticData[graphic].Height);
            }

            return z;
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
