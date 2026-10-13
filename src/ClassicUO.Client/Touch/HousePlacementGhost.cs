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
    /// Zombie Survival's build cursor (build 52, servuo Custom/Zombie/BuildLines.cs) comes with 0x7A5B too: what
    /// the piece can do and how it looks laid east-west and north-south. Then a tap still puts the one ghost
    /// where you tap (Place builds it), but a drag lays a line: from where the finger went down along the
    /// drag's longer way, a ghost on every tile (red where the server's 0x7A5A answer says that one can't go),
    /// and lifting the finger builds them all with one 0x7A58. Slide back to the first tile, or lift over a
    /// window, to lay nothing. Turn (0x7A5C) swaps the piece for its next way round.
    /// </summary>
    internal static class HousePlacementGhost
    {
        public const ushort Query = 0x7A55;
        public const ushort Reply = 0x7A56;
        public const ushort AddonPieces = 0x7A57;
        public const ushort LineLay = 0x7A58; // servuo BuildLines.Lay
        public const ushort LineAsk = 0x7A59; // BuildLines.Ask
        public const ushort LineAnswer = 0x7A5A; // BuildLines.Answer
        public const ushort BuildInfo = 0x7A5B; // BuildLines.Info
        public const ushort BuildTurn = 0x7A5C; // BuildLines.Turn

        private const byte FlagLines = 1, FlagTurns = 4; // BuildLines.FlagLines / FlagTurns

        // a build cursor's abilities and its piece laid east-west / north-south (0x7A5B)
        private static uint _buildCursor;
        private static byte _buildFlags, _lineMax;
        private static List<(ushort Graphic, short X, short Y, short Z, ushort Hue)> _alongX, _alongY;

        // the line a finger is dragging: where it went down and where it is now (the ground or floor there)
        private static GameObject _lineStart, _lineEnd;
        private static bool _lineStartPending; // the finger just went down: its first spot starts the line
        private static bool _lineArmed; // the finger has travelled far enough for a drag (a tap that rolls a little lays no line)
        private static (int SX, int SY, int SZ, int EX, int EY, int EZ) _lineKey;
        private static ushort _lineSeq;
        private static uint _lineSentAt;
        private static bool _lineDirty, _lineAnswered;
        private static byte _lineStatus;
        private static readonly List<(short X, short Y, short Z, byte Problem)> _lineAnswer = new List<(short, short, short, byte)>();
        private static readonly List<Multi> _linePreview = new List<Multi>();
        private static bool _linePreviewAlongY;
        private static int _lineShown = -1; // a cheap signature of what the line's ghost shows now

        // a furniture deed's cursor: its id, its pieces (graphic, x/y/z offset, hue) and their ghost in the world
        private static uint _addonCursor;
        private static List<(ushort Graphic, short X, short Y, short Z, ushort Hue)> _pieces;
        private static readonly List<Multi> _preview = new List<Multi>();
        private static int _previewX = -1, _previewY, _previewZ, _previewHue = -1;

        /// <summary>UO's plain red: the house can't go there.</summary>
        private const ushort BadHue = 0x0021;

        private const uint SendEveryMs = 120, ResendAfterMs = 600; // (the server answers at most every 100 ms)
        private const uint LineResendMs = 250; // a line question the server dropped as too soon (network jitter) is asked again quickly
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
            _lineAnswered = false; // a line being dragged is asked about again for this cursor
            _lineDirty = true;
            _lineSeq++; // (an answer still on its way was for the old cursor)
        }

        /// <summary>PacketHandlers (0xBF 0x7A5B): a build cursor's abilities, and its piece laid both ways.</summary>
        public static void OnBuildInfo(uint cursor, byte flags, byte max, List<(ushort, short, short, short, ushort)> alongX, List<(ushort, short, short, short, ushort)> alongY)
        {
            _buildCursor = cursor;
            _buildFlags = flags;
            _lineMax = max;
            _alongX = alongX.Count > 0 ? alongX : null;
            _alongY = alongY.Count > 0 ? alongY : null;
            _lineAnswered = false;
            _lineDirty = true;
            _lineSeq++; // (an answer still on its way was for the old cursor)
            ClearLinePreview(); // the piece may look different now
        }

        /// <summary>PacketHandlers (0xBF 0x7A5A): what lifting the finger would build, piece by piece.</summary>
        public static void OnLineAnswer(ushort seq, byte status, List<(short, short, short, byte)> pieces)
        {
            if (seq != _lineSeq)
            {
                return;
            }

            _lineStatus = status;
            _lineAnswer.Clear();
            _lineAnswer.AddRange(pieces);
            _lineAnswered = true;
        }

        /// <summary>This cursor is a build cursor whose piece lays lines (the server said so for this very cursor).</summary>
        private static bool LineCapable(World world) =>
            AddonActive(world) && _buildCursor == world.TargetManager.TargetCursorId && (_buildFlags & FlagLines) != 0 && _alongX != null && _alongY != null;

        /// <summary>This build cursor's piece has another way round (the Turn button).</summary>
        public static bool TurnCapable(World world) =>
            AddonActive(world) && _buildCursor == world.TargetManager.TargetCursorId && (_buildFlags & FlagTurns) != 0;

        /// <summary>The cursor up is a build cursor the server described (pieces may stand on a walkway's floor).</summary>
        private static bool BuildCursor(World world) =>
            _buildCursor != 0 && world?.TargetManager != null && world.TargetManager.IsTargeting && _buildCursor == world.TargetManager.TargetCursorId;

        /// <summary>A placed walkway or other fixed floor item on the ground: a build piece's ghost can stand on it.</summary>
        private static bool FloorItem(GameObject o) => o is Item it && it.OnGround && !it.IsMulti && IsSurface(o);

        /// <summary>
        /// How far a finger must travel before it lays a line: half a tile on screen at the current zoom, at least the tap
        /// slop (a tap near a tile's edge with a little roll must not build two).
        /// </summary>
        private static int LineSlop()
        {
            float zoom = Client.Game.Scene is GameScene scene ? scene.Camera.Zoom : 1f;

            return Math.Max(12, (int)(22f / Math.Max(0.25f, zoom)));
        }

        /// <summary>A finger is laying a line now: it went down on one tile and is over another.</summary>
        private static bool LineActive(World world) =>
            TouchInput.GhostFingerDown && _lineStart != null && _lineEnd != null && (_lineStart.X != _lineEnd.X || _lineStart.Y != _lineEnd.Y) && LineCapable(world);

        /// <summary>TouchInput: a finger went down on the world while placing (it drags the ghost, or lays a line).</summary>
        public static void FingerDown()
        {
            _lineStart = _lineEnd = null;
            _lineStartPending = true;
            _lineArmed = false;
            _lineAnswered = false;
        }

        /// <summary>
        /// TouchInput: the dragging finger lifted (<paramref name="overWorld" />: over the world, not a window). A line
        /// is built from where it went down to where it lifted; the ghost waits on the next tile along.
        /// </summary>
        public static void FingerUp(World world, bool overWorld, bool quiet = false)
        {
            bool line = world != null && _lineStart != null && _lineEnd != null && (_lineStart.X != _lineEnd.X || _lineStart.Y != _lineEnd.Y) && LineCapable(world);

            if (line)
            {
                if (overWorld)
                {
                    Lay(world);
                }
                else if (!quiet) // (a pinch or the system took the finger: nothing to explain)
                {
                    GameActions.Print(world, "No line laid (lifted off the ground).", 0x3B2);
                }
            }

            _lineStart = _lineEnd = null;
            _lineStartPending = false;
            _lineArmed = false;
            _lineAnswered = false;
            _lineKey = default;
            ClearLinePreview();
        }

        /// <summary>The bar's Turn: the server swaps the cursor for the piece's next way round (the ghost follows).</summary>
        public static void Turn(World world)
        {
            if (!TurnCapable(world) || NetClient.Socket == null || !NetClient.Socket.IsConnected)
            {
                return;
            }

            var writer = new StackDataWriter(12);
            writer.WriteUInt8(0xBF);
            writer.WriteZero(2);
            writer.WriteUInt16BE(BuildTurn);
            writer.WriteUInt32BE(world.TargetManager.TargetCursorId);
            writer.Seek(1, SeekOrigin.Begin);
            writer.WriteUInt16BE((ushort)writer.BytesWritten);
            NetClient.Socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        /// <summary>The line's tiles as the server lays them (BuildLines.Tiles): along the longer way, at most the cap.</summary>
        private static (int N, bool AlongY, int Step, int Dist) LineShape()
        {
            int dx = _lineEnd.X - _lineStart.X, dy = _lineEnd.Y - _lineStart.Y;
            bool alongY = Math.Abs(dy) > Math.Abs(dx);
            int dist = Math.Abs(alongY ? dy : dx);

            return (Math.Min(dist + 1, Math.Max(1, (int)_lineMax)), alongY, Math.Sign(alongY ? dy : dx), dist);
        }

        /// <summary>The line's ghost: its piece on every tile (as the server answered: red where it can't go).</summary>
        private static void DrawLine(World world)
        {
            (int n, bool alongY, int step, int dist) = LineShape();
            int sz = TopOf(_lineStart), ez = TopOf(_lineEnd);
            var key = (_lineStart.X, _lineStart.Y, sz, _lineEnd.X, _lineEnd.Y, ez);

            if (key != _lineKey)
            {
                _lineKey = key;
                _lineDirty = true;
                _lineAnswered = false;
            }

            List<(ushort Graphic, short X, short Y, short Z, ushort Hue)> comps = alongY ? _alongY : _alongX;
            int count = n * comps.Count;

            if (_linePreview.Count != count || _linePreviewAlongY != alongY || _linePreview.Count > 0 && _linePreview[0].IsDestroyed)
            {
                ClearLinePreview();

                for (int i = 0; i < n; i++)
                {
                    foreach (var c in comps)
                    {
                        Multi m = Multi.Create(world, c.Graphic);
                        m.IsHousePreview = true; // see-through, and never what a finger picks
                        _linePreview.Add(m);
                    }
                }

                _linePreviewAlongY = alongY;
            }

            bool answered = _lineAnswered && _lineStatus == 0 && _lineAnswer.Count == n;
            int shown = HashCode.Combine(key, answered, _lineSeq, n);

            if (shown == _lineShown)
            {
                return;
            }

            _lineShown = shown;

            for (int i = 0; i < n; i++)
            {
                int x = _lineStart.X + (alongY ? 0 : step * i), y = _lineStart.Y + (alongY ? step * i : 0);
                int z;
                bool bad = false;

                if (answered && _lineAnswer[i].X == x && _lineAnswer[i].Y == y)
                {
                    z = _lineAnswer[i].Z;
                    bad = _lineAnswer[i].Problem != 0;
                }
                else
                {
                    // until the server answers: the floor near the line's height there (it runs evenly end to end)
                    int refZ = dist == 0 ? sz : sz + (ez - sz) * i / dist;
                    GameObject floor = FloorAt(world, x, y, refZ, true);
                    z = floor != null ? TopOf(floor) : refZ;
                }

                for (int j = 0; j < comps.Count; j++)
                {
                    var c = comps[j];
                    Multi m = _linePreview[i * comps.Count + j];
                    m.Hue = bad ? BadHue : c.Hue;
                    m.SetInWorldTile((ushort)(x + c.X), (ushort)(y + c.Y), (sbyte)Math.Clamp(z + c.Z, sbyte.MinValue, sbyte.MaxValue));
                }
            }
        }

        private static void ClearLinePreview()
        {
            foreach (Multi m in _linePreview)
            {
                m.Destroy();
            }

            _linePreview.Clear();
            _lineShown = -1;
        }

        /// <summary>What lifting now would build (0x7A59): the cursor, where the drag began and where it is.</summary>
        private static void AskLine(World world)
        {
            _lineSeq++;
            _lineDirty = false;
            _lineSentAt = Time.Ticks;
            SendLine(LineAsk, world.TargetManager.TargetCursorId, true);
        }

        /// <summary>The lift (0x7A58): build the line. The ghost then waits on the next tile along it.</summary>
        private static void Lay(World world)
        {
            var (n, alongY, step, _) = LineShape();
            int nx = _lineStart.X + (alongY ? 0 : step * n), ny = _lineStart.Y + (alongY ? step * n : 0);
            int lastZ = _lineAnswered && _lineAnswer.Count == n ? _lineAnswer[n - 1].Z : TopOf(_lineEnd);

            SendLine(LineLay, world.TargetManager.TargetCursorId, false);

            GameObject next = FloorAt(world, nx, ny, lastZ, true);

            if (next != null)
            {
                Moved(next);
            }
        }

        private static void SendLine(ushort sub, uint cursor, bool withSeq)
        {
            if (NetClient.Socket == null || !NetClient.Socket.IsConnected || _lineStart == null || _lineEnd == null)
            {
                return;
            }

            var writer = new StackDataWriter(24);
            writer.WriteUInt8(0xBF);
            writer.WriteZero(2);
            writer.WriteUInt16BE(sub);

            if (withSeq)
            {
                writer.WriteUInt16BE(_lineSeq);
            }

            writer.WriteUInt32BE(cursor);
            writer.WriteUInt16BE(_lineStart.X);
            writer.WriteUInt16BE(_lineStart.Y);
            writer.WriteInt16BE((short)TopOf(_lineStart));
            writer.WriteUInt16BE(_lineEnd.X);
            writer.WriteUInt16BE(_lineEnd.Y);
            writer.WriteInt16BE((short)TopOf(_lineEnd));
            writer.Seek(1, SeekOrigin.Begin);
            writer.WriteUInt16BE((ushort)writer.BytesWritten);
            NetClient.Socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        /// <summary>The bar's words while a line is dragged.</summary>
        private static string LineText(World world)
        {
            var (n, _, _, _) = LineShape();

            if (!_lineAnswered)
            {
                return $"Lift to build {n} - checking...";
            }

            if (_lineStatus == 2)
            {
                return "This piece goes one at a time: tap a spot, then Place";
            }

            if (_lineStatus != 0 || _lineAnswer.Count != n)
            {
                return $"Lift to build {n}";
            }

            int good = 0;
            var why = new Dictionary<byte, int>();

            foreach (var p in _lineAnswer)
            {
                if (p.Problem == 0)
                {
                    good++;
                }
                else
                {
                    why[p.Problem] = why.TryGetValue(p.Problem, out int k) ? k + 1 : 1;
                }
            }

            if (good == n)
            {
                return n >= _lineMax ? $"Lift to build all {n} (the most one line lays)" : $"Lift to build all {n}";
            }

            byte top = 0;
            int most = 0;

            foreach (var kv in why)
            {
                if (kv.Value > most)
                {
                    (top, most) = (kv.Key, kv.Value);
                }
            }

            return good == 0 ? $"None of the {n} can go: {LineReason(top)}" : $"Lift to build {good} of {n} - {n - good} can't: {LineReason(top)}";
        }

        /// <summary>The server's reason for one piece of a line (servuo PlaceProblem; BuildLines.Short).</summary>
        private static string LineReason(byte problem)
        {
            switch (problem)
            {
                case 1: return "something in the way";
                case 2: return "too far";
                case 3: return "out of sight";
                case 4: return "on water";
                case 5: return "on a house";
                case 6: return "in someone's camp";
                case 7: return "uneven ground";
                case 8: return "out of kits";
                case 9: return "over your piece limit";
                default: return "can't build there";
            }
        }

        /// <summary>The ground under a mobile, corpse or effect (they would carry the ghost off), else the thing itself.</summary>
        private static GameObject Standable(GameObject o, bool floorItems = false)
        {
            return o == null || o is Land || o is Static || o is Multi || floorItems && FloorItem(o) ? o : LandAt(o.World, o.X, o.Y) ?? o;
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
                ClearLinePreview();

                // pieces only ever follow their cursor: once it is gone they are stale (a restarted server
                // hands out the same cursor ids again)
                if (world?.TargetManager == null || !world.TargetManager.IsTargeting || world.TargetManager.TargetCursorId != _addonCursor)
                {
                    _pieces = null;
                }

                if (world?.TargetManager == null || !world.TargetManager.IsTargeting || world.TargetManager.TargetCursorId != _buildCursor)
                {
                    _buildCursor = 0; // (as the pieces: stale once its cursor is gone)
                    _alongX = _alongY = null;
                }

                return;
            }

            bool build = BuildCursor(world); // (a build piece may stand on a walkway's floor; a deed's ghost as before)
            GameObject finger = under != null && TouchInput.GhostFingerDown ? Standable(FloorAt(world, under.X, under.Y, world.Player.Z, build) ?? under, build) : null;
            bool lines = LineCapable(world);

            // a build piece that lays lines: the finger's first spot starts the line, then - once the finger has really
            // moved - it follows the finger
            if (lines && TouchInput.GhostFingerDown && finger != null)
            {
                if (_lineStartPending || _lineStart == null || _lineStart.IsDestroyed)
                {
                    _lineStart = finger;
                    _lineStartPending = false;
                }

                if (!_lineArmed && TouchInput.GhostFingerTravel >= LineSlop())
                {
                    _lineArmed = true;
                }

                _lineEnd = _lineArmed ? finger : _lineStart;
            }

            if (LineActive(world))
            {
                ClearPreview(); // the single ghost gives way to the line's
                DrawLine(world);

                return;
            }

            ClearLinePreview();

            // only a finger dragging on the world moves it (not the tap that chose a ladder's facing); for a line
            // piece, the spot the finger went down on
            GameObject pick = lines && TouchInput.GhostFingerDown && _lineStart != null && !_lineStart.IsDestroyed ? _lineStart
                : finger != null ? finger
                : _anchor != null && !_anchor.IsDestroyed ? _anchor
                : FloorAt(world, world.Player.X + 1, world.Player.Y + 2, world.Player.Z, build);

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

            if (_preview.Count != _pieces.Count || _preview.Count > 0 && _preview[0].IsDestroyed) // (a map change destroys them)
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

        /// <summary>TouchInput.Unload: nothing carried over to the next login.</summary>
        public static void Reset()
        {
            ClearPreview();
            _pieces = null;
            _addonCursor = 0;
            ClearLinePreview();
            _buildCursor = 0;
            _alongX = _alongY = null;
            _lineStart = _lineEnd = null;
            _lineStartPending = false;
            _lineAnswered = false;
            _lineKey = default;
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
        private static GameObject FloorAt(World world, int x, int y, int level, bool floorItems = false)
        {
            GameObject best = null;
            int bestTop = int.MinValue;

            for (GameObject o = world.Map.GetTile(x, y); o != null; o = o.TNext)
            {
                bool floor = o is Land || (o is Static || o is Multi m && !m.IsHousePreview) && IsSurface(o) || floorItems && FloorItem(o);

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
            if (!HouseActive(world))
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

            // a mobile, corpse or effect under the finger would carry the ghost off (or vanish): the ground under it (a
            // build piece may stand on a walkway's floor)
            if (!(anchor is Land || anchor is Static || anchor is Multi || FloorItem(anchor) && BuildCursor(anchor.World)))
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

            bool line = LineActive(world);

            if (line)
            {
                if (_lineDirty && Time.Ticks - _lineSentAt >= SendEveryMs || !_lineAnswered && Time.Ticks - _lineSentAt >= LineResendMs)
                {
                    AskLine(world);
                }
            }
            else if (_anchor != null && (_dirty && Time.Ticks - _sentAt >= SendEveryMs || !_answered && Time.Ticks - _sentAt >= ResendAfterMs))
            {
                Ask();
            }

            bool addon = AddonActive(world);
            bool lines = LineCapable(world);
            string text = line ? LineText(world)
                : _anchor == null ? lines ? "Tap where it goes, or drag to lay a line" : addon ? "Drag on the floor to move it" : "Drag on the ground to move the house"
                : lines && _answered && _result == 0 ? "Clear - tap Place, or drag a line"
                : Reason(_result, addon);

            _bar.Show(text, line ? _lineAnswered && _lineStatus == 0 && _lineAnswer.TrueForAll(p => p.Problem == 0) : _answered && _result == 0, TurnCapable(world));
        }

        public static void Place(World world)
        {
            if (!Active(world))
            {
                return;
            }

            if (_anchor == null)
            {
                GameActions.Print(world, AddonActive(world) ? "Drag it onto the floor first." : "Drag the house onto the ground first.", 0x0021);

                return;
            }

            // only a known "no" for this very spot stops it (the server checks the placement anyway)
            if (_answered && Cannot(_result))
            {
                GameActions.Print(world, Reason(_result, AddonActive(world)), 0x0021);

                return;
            }

            if (_anchor is Item floorItem && BuildCursor(world))
            {
                world.TargetManager.Target(floorItem.Serial); // a walkway's floor: the piece goes on top of it

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
                case 3 when addon: return "Too far away: bring it closer";
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

            if (o is Static || o is Multi || FloorItem(o) && BuildCursor(o.World)) // (the server reads a floor item's spot like a static's)
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

    /// <summary>The bar under the house ghost: what the server says about the spot, Place and Cancel (and Turn for a build piece).</summary>
    internal sealed class HousePlacementBar : Gump
    {
        private const int ID_PLACE = 1, ID_CANCEL = 2, ID_TURN = 3;
        private const int W = 420, H = 62;

        private readonly Label _status;
        private readonly NiceButton _place, _cancel, _turn;
        private string _shown;
        private bool _shownGood;
        private bool? _shownTurn;

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
            Add(_place = new NiceButton(W / 2 - 150, H - 30, 130, 26, ButtonAction.Activate, "Place", hue: 0x0044) { ButtonParameter = ID_PLACE, IsSelectable = false });
            Add(_cancel = new NiceButton(W / 2 + 20, H - 30, 130, 26, ButtonAction.Activate, "Cancel", hue: 0x0021) { ButtonParameter = ID_CANCEL, IsSelectable = false });
            Add(_turn = new NiceButton(W / 2 - 55, H - 30, 110, 26, ButtonAction.Activate, "Turn", hue: 0x0035) { ButtonParameter = ID_TURN, IsSelectable = false, IsVisible = false });
        }

        public void Show(string text, bool good, bool turn = false)
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

            if (turn != _shownTurn)
            {
                // a piece with another way round: Place, Turn, Cancel across the bar
                _shownTurn = turn;
                _turn.IsVisible = turn;
                _place.X = turn ? 12 : W / 2 - 150;
                _cancel.X = turn ? W - 12 - 130 : W / 2 + 20;
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

                case ID_TURN:
                    HousePlacementGhost.Turn(World);

                    break;
            }
        }
    }
}
