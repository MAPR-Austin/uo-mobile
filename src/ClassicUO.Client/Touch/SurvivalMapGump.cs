// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Touch
{
    /// <summary>
    /// The phone's map (the "map" action; iPhone build 53). ClassicUO's world map isn't offered on the phone: it builds a
    /// full-size 7168x4096 image (~120 MB) off the main thread, and pans only with Alt or the middle button. This one is
    /// made for a finger: it fills the screen (the HUD steps aside), a drag pans, - and + (or a double tap) zoom, You
    /// centres on you again and follows, Close (or the Map button again) closes it. Its picture of the facet is built once
    /// per session at two tiles a texel (Felucca: 3584x2048, ~29 MB), the reading on a worker thread (CPU only: its own
    /// file handles, as the world map's) and the texture made on a drawn frame on the main thread.
    /// On Zombie Survival it shows the server's markers (<see cref="ZombieMarkers" />): every town coloured by its state
    /// (held green, cleared yellow, overrun red), the wandering peddlers where they are, your camp and your friends',
    /// rings where alerts were about; tap one to name it, with its distance and way from you. Markers are asked for as the
    /// map opens and every 30 s while it is open. Elsewhere it is a plain map with you on it.
    /// </summary>
    internal sealed class SurvivalMapGump : Gump
    {
        private const int TilesPerTexel = 2;
        private const int BarH = 46;
        private const int ID_OUT = 1, ID_IN = 2, ID_ME = 3, ID_CLOSE = 4;

        // the facet's picture: shared by every map opened this session
        private static Texture2D _texture;
        private static int _textureMap = -1, _texW, _texH;
        private static Task<uint[]> _building;
        private static int _buildingMap = -1, _buildW, _buildH;
        private static bool _buildFailed;

        private static readonly float[] Zooms = { 0f, 0.25f, 0.5f, 1f, 2f, 3f }; // UI units a tile; 0: the whole facet
        private static int _zoomIndex = 2;

        private float _cx, _cy; // the middle of the view, in tiles
        private bool _follow = true;
        private bool _dragging, _dragMoved;
        private Point _dragFrom;
        private float _dragCx, _dragCy;
        private int _selected = -1; // a marker's index, -2 you, -1 none
        private int _selectedVersion;
        private (SurvivalMarkerKind Kind, string Name) _selectedKey; // found again after the markers are refreshed
        private string _info = "";

        private int _builtW, _builtH;
        private Rectangle _builtSafe;
        private readonly List<Control> _bar = new List<Control>();
        private readonly Dictionary<(string, ushort), RenderedText> _labels = new Dictionary<(string, ushort), RenderedText>();
        private RenderedText _infoText, _statusText;
        private string _infoShown, _statusShown;

        // what one frame draws (filled in AddToRenderLists, drawn in the same frame)
        private readonly List<(RenderedText Text, int X, int Y)> _texts = new List<(RenderedText, int, int)>();
        private readonly List<(Point P, Color C, int R, bool Ring)> _dots = new List<(Point, Color, int, bool)>();
        private readonly List<(Rectangle R, Color C, Color Edge)> _areas = new List<(Rectangle, Color, Color)>();
        private readonly List<Rectangle> _buttonRects = new List<Rectangle>();

        private SurvivalMapGump(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = true;
            CanCloseWithRightClick = false; // (a long press is a right click on the phone: a resting finger mustn't close the map)
            CanCloseWithEsc = true;
            LayerOrder = UILayer.Over;

            if (world.Player != null)
            {
                _cx = world.Player.X;
                _cy = world.Player.Y;
            }

            _buildFailed = false; // (a picture that failed is tried again)
            Layout(); // (its Update asks for fresh markers: ZombieMarkers.Update sees a map open)
        }

        /// <summary>The "map" action: open it, or close it if it is open.</summary>
        public static void Toggle(World world)
        {
            SurvivalMapGump open = UIManager.GetGump<SurvivalMapGump>();

            if (open != null)
            {
                open.Dispose();

                return;
            }

            if (world?.Player == null)
            {
                return;
            }

            UIManager.Add(new SurvivalMapGump(world));
        }

        // ------------------------------------------------------------------ layout

        private Rectangle View
        {
            get
            {
                Rectangle s = TouchInput.Safe;

                return new Rectangle(s.X + 6, s.Y + 6, s.Width - 12, s.Height - 12 - BarH);
            }
        }

        private void Layout()
        {
            _builtW = TouchInput.ScreenW;
            _builtH = TouchInput.ScreenH;
            _builtSafe = TouchInput.Safe;
            X = 0;
            Y = 0;
            Width = _builtW;
            Height = _builtH;

            foreach (Control c in _bar)
            {
                c.Dispose();
            }

            _bar.Clear();

            Rectangle s = _builtSafe;
            int y = s.Bottom - BarH + 3;
            int right = s.Right - 6;
            (int Id, string Text, int W)[] buttons = { (ID_CLOSE, "Close", 84), (ID_ME, "You", 64), (ID_IN, "+", 56), (ID_OUT, "-", 56) };

            foreach ((int id, string text, int w) in buttons)
            {
                right -= w;
                NiceButton b = new NiceButton(right, y, w - 6, BarH - 6, ButtonAction.Activate, text, 0, TEXT_ALIGN_TYPE.TS_CENTER, 0x0481)
                {
                    ButtonParameter = id,
                    IsSelectable = false
                };
                _bar.Add(b);
                Add(b);
            }
        }

        public override void OnButtonClick(int buttonID)
        {
            switch (buttonID)
            {
                case ID_OUT:
                    Zoom(-1, View.Center);

                    break;

                case ID_IN:
                    Zoom(1, View.Center);

                    break;

                case ID_ME:
                    _follow = true;

                    break;

                case ID_CLOSE:
                    Dispose();

                    break;
            }
        }

        private float Scale
        {
            get
            {
                if (Zooms[_zoomIndex] > 0f)
                {
                    return Zooms[_zoomIndex];
                }

                Rectangle v = View;
                (int w, int h) = MapSize();

                return Math.Min(v.Width / (float)Math.Max(1, w), v.Height / (float)Math.Max(1, h));
            }
        }

        /// <summary>One step in or out, keeping the tile under <paramref name="at" /> where it is.</summary>
        private void Zoom(int step, Point at)
        {
            int next = Math.Clamp(_zoomIndex + step, 0, Zooms.Length - 1);

            if (next == _zoomIndex)
            {
                return;
            }

            Rectangle v = View;
            float s0 = Scale;
            float tx = _cx + (at.X - v.Center.X) / s0, ty = _cy + (at.Y - v.Center.Y) / s0;
            _zoomIndex = next;
            float s1 = Scale;
            _cx = tx - (at.X - v.Center.X) / s1;
            _cy = ty - (at.Y - v.Center.Y) / s1;
            _follow &= at == v.Center; // (- and + keep following you; a double tap somewhere looks there)
            ClampCentre();
        }

        private (int W, int H) MapSize()
        {
            int map = World.MapIndex;

            if (map < 0 || map >= Client.Game.UO.FileManager.Maps.MapsDefaultSize.GetLength(0))
            {
                return (7168, 4096);
            }

            return (Client.Game.UO.FileManager.Maps.MapsDefaultSize[map, 0], Client.Game.UO.FileManager.Maps.MapsDefaultSize[map, 1]);
        }

        /// <summary>The view stays on the facet: centred on it where the facet is smaller than the view (the whole-facet zoom).</summary>
        private void ClampCentre()
        {
            (int w, int h) = MapSize();
            Rectangle v = View;
            float s = Scale;
            float halfW = v.Width / 2f / s, halfH = v.Height / 2f / s;
            _cx = w <= 2 * halfW ? w / 2f : Math.Clamp(_cx, halfW, w - halfW);
            _cy = h <= 2 * halfH ? h / 2f : Math.Clamp(_cy, halfH, h - halfH);
        }

        // ------------------------------------------------------------------ input

        protected override void OnMouseDown(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left && View.Contains(x, y))
            {
                _dragging = true;
                _dragMoved = false;
                _dragFrom = Mouse.Position;
                _dragCx = _cx;
                _dragCy = _cy;
            }

            base.OnMouseDown(x, y, button);
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left && _dragging)
            {
                _dragging = false;

                if (!_dragMoved)
                {
                    Select(new Point(x, y));
                }
            }

            base.OnMouseUp(x, y, button);
        }

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left && View.Contains(x, y))
            {
                Zoom(1, new Point(x, y));

                return true;
            }

            return base.OnMouseDoubleClick(x, y, button);
        }

        private void Pan()
        {
            if (!_dragging)
            {
                return;
            }

            if (!Mouse.LButtonPressed)
            {
                _dragging = false;

                return;
            }

            Point d = Mouse.Position - _dragFrom;

            if (!_dragMoved && Math.Abs(d.X) + Math.Abs(d.Y) < 10)
            {
                return; // (a tap with a little roll)
            }

            _dragMoved = true;
            _follow = false;
            float s = Scale;
            _cx = _dragCx - d.X / s;
            _cy = _dragCy - d.Y / s;
            ClampCentre();
        }

        /// <summary>A tap: the nearest marker within a finger's width is named, with its distance and way from you.</summary>
        private void Select(Point p)
        {
            int best = -1;
            int bestD = 26 * 26;
            Rectangle v = View;
            float s = Scale;
            List<SurvivalMarker> markers = ZombieMarkers.Markers;

            for (int i = 0; i < markers.Count; i++)
            {
                SurvivalMarker k = markers[i];

                if (k.Facet != World.MapIndex)
                {
                    continue;
                }

                Point q = Spot(k.X, k.Y, v, s);
                int d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);

                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            if (World.Player != null)
            {
                Point me = Spot(World.Player.X, World.Player.Y, v, s);
                int d = (me.X - p.X) * (me.X - p.X) + (me.Y - p.Y) * (me.Y - p.Y);

                if (d < bestD)
                {
                    best = -2;
                }
            }

            _selected = best;
            _selectedVersion = ZombieMarkers.Version;
            _selectedKey = best >= 0 ? (markers[best].Kind, markers[best].Name) : default;
        }

        private Point ToScreen(float tx, float ty, Rectangle v, float s) =>
            new Point(v.Center.X + (int)MathF.Round((tx - _cx) * s), v.Center.Y + (int)MathF.Round((ty - _cy) * s));

        /// <summary>Where a marker on tile (x, y) goes: the middle of the tile.</summary>
        private Point Spot(int x, int y, Rectangle v, float s) => ToScreen(x + 0.5f, y + 0.5f, v, s);

        // ------------------------------------------------------------------ update

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            if (!World.InGame || World.Player == null)
            {
                Dispose();

                return;
            }

            if (_builtW != TouchInput.ScreenW || _builtH != TouchInput.ScreenH || _builtSafe != TouchInput.Safe)
            {
                Layout(); // turned
            }

            ZombieMarkers.Update(World);
            Pan();

            if (_follow)
            {
                _cx = World.Player.X;
                _cy = World.Player.Y;
            }

            ClampCentre();
            StartBuilding();

            if (_selected >= 0 && _selectedVersion != ZombieMarkers.Version)
            {
                // the list changed under it (a refresh): the same marker again, if it is still there
                _selected = ZombieMarkers.Markers.FindIndex(k => k.Kind == _selectedKey.Kind && k.Name == _selectedKey.Name);
                _selectedVersion = ZombieMarkers.Version;
            }

            _info = Describe();
        }

        private string Describe()
        {
            if (_selected == -2)
            {
                return $"You: {World.Player.X}, {World.Player.Y}";
            }

            if (_selected < 0 || _selected >= ZombieMarkers.Markers.Count)
            {
                return ZombieMarkers.Supported ? "Tap a marker to name it. Drag to look around." : "Drag to look around.";
            }

            SurvivalMarker k = ZombieMarkers.Markers[_selected];
            string where = Away(k.X, k.Y);

            switch (k.Kind)
            {
                case SurvivalMarkerKind.Town:
                    return k.TownState == 2 ? $"{k.Name}: held - merchants open ({where})"
                        : k.TownState == 1 ? $"{k.Name}: cleared - hold it and its merchants return ({where})"
                        : $"{k.Name}: overrun by the dead ({where})";

                case SurvivalMarkerKind.Peddler:
                    return $"{k.Name}: {where}{(k.Fleeing ? " - running from the dead!" : "")}";

                default:
                    return $"{k.Name}: {(k.UnderAttack ? "UNDER ATTACK, " : "")}{(k.FireOut ? "its fire is out" : "fire lit")} ({where})";
            }
        }

        private string Away(int x, int y)
        {
            int dx = x - World.Player.X, dy = y - World.Player.Y;
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));

            if (dist <= 2)
            {
                return "here";
            }

            double ang = Math.Atan2(-dy, dx) * 180.0 / Math.PI; // UO: north is -y
            int octant = (int)Math.Round(((ang % 360) + 360) % 360 / 45.0) % 8;
            string[] names = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };

            return $"{dist} tiles {names[octant]}";
        }

        // ------------------------------------------------------------------ the facet's picture

        private void StartBuilding()
        {
            int map = World.MapIndex;

            if (_textureMap == map && _texture != null && !_texture.IsDisposed || _buildingMap == map && _building != null || _buildFailed && _buildingMap == map)
            {
                return;
            }

            MapLoader maps = Client.Game.UO.FileManager.Maps;

            if (map < 0 || map >= maps.BlockData.Length || maps.BlockData[map] == null)
            {
                return;
            }

            IndexMap[] blocks = maps.BlockData[map];
            int bw = maps.MapBlocksSize[map, 0], bh = maps.MapBlocksSize[map, 1];
            int w = maps.MapsDefaultSize[map, 0], h = maps.MapsDefaultSize[map, 1];
            World world = World;
            _buildingMap = map;
            _buildFailed = false;
            _buildW = (w + TilesPerTexel - 1) / TilesPerTexel;
            _buildH = (h + TilesPerTexel - 1) / TilesPerTexel;
            int tw = _buildW, th = _buildH;
            _building = Task.Run(() => Build(world, blocks, bw, bh, tw, th));
        }

        /// <summary>The main thread, a drawn frame: the finished picture becomes a texture.</summary>
        private static void TakeTexture()
        {
            if (_building == null || !_building.IsCompleted)
            {
                return;
            }

            Task<uint[]> done = _building;
            _building = null;

            if (done.IsFaulted || done.Result == null)
            {
                _buildFailed = true;
                Log.Error($"[UOMobile] map picture failed: {done.Exception?.GetBaseException().Message}");

                return;
            }

            try
            {
                _texture?.Dispose();
                _texture = new Texture2D(Client.Game.GraphicsDevice, _buildW, _buildH, false, SurfaceFormat.Color);
                _texture.SetData(done.Result);
                _texW = _buildW;
                _texH = _buildH;
                _textureMap = _buildingMap;
            }
            catch (Exception e)
            {
                // (no picture: the map still shows the markers; tried again when it next opens)
                _texture?.Dispose();
                _texture = null;
                _textureMap = -1;
                _buildFailed = true;
                Log.Error($"[UOMobile] map texture failed: {e.Message}");
            }
        }

        /// <summary>
        /// Logout (ZombieMarkers.Reset, the main thread, in the foreground): the picture's ~29 MB go; a build still running is
        /// forgotten (its result is dropped when it ends).
        /// </summary>
        public static void Release()
        {
            _texture?.Dispose();
            _texture = null;
            _textureMap = -1;
            _texW = _texH = 0;
            _building = null;
            _buildingMap = -1;
            _buildFailed = false;
        }

        /// <summary>
        /// The worker: every block's ground and its highest statics (the radar colours, as the world map draws them), two by
        /// two tiles to a texel, then a little relief (lit from the north-west). CPU only.
        /// </summary>
        private static unsafe uint[] Build(World world, IndexMap[] blocks, int bw, int bh, int tw, int th)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            const int S = TilesPerTexel;
            uint[] px = new uint[tw * th];
            sbyte[] zs = new sbyte[tw * th];
            HuesLoader hues = Client.Game.UO.FileManager.Hues;
            Dictionary<string, UOFile> files = new Dictionary<string, UOFile>();
            StaticsBlock[] statics = new StaticsBlock[64];
            uint* color = stackalloc uint[64];
            sbyte* top = stackalloc sbyte[64];

            UOFile Open(string path)
            {
                if (!files.TryGetValue(path, out UOFile f))
                {
                    files[path] = f = new UOFile(path);
                }

                return f;
            }

            try
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int by = 0; by < bh; by++)
                    {
                        int index = bx * bh + by;

                        if (index >= blocks.Length || !blocks[index].IsValid() || blocks[index].MapFile == null)
                        {
                            continue;
                        }

                        IndexMap im = blocks[index];
                        UOFile mapFile = Open(im.MapFile.FilePath);
                        mapFile.Seek((long)im.MapAddress, SeekOrigin.Begin);
                        MapBlock block = mapFile.Read<MapBlock>();

                        for (int i = 0; i < 64; i++)
                        {
                            color[i] = HuesHelper.Color16To32((ushort)(0x8000 | hues.GetRadarColorData(block.Cells[i].TileID & 0x3FFF)));
                            top[i] = block.Cells[i].Z;
                        }

                        if (im.StaticCount > 0 && im.StaticFile != null && im.StaticCount < 4096)
                        {
                            if (statics.Length < im.StaticCount)
                            {
                                statics = new StaticsBlock[im.StaticCount];
                            }

                            Span<StaticsBlock> span = statics.AsSpan(0, (int)im.StaticCount);
                            UOFile staticFile = Open(im.StaticFile.FilePath);
                            staticFile.Seek((long)im.StaticAddress, SeekOrigin.Begin);
                            staticFile.Read(MemoryMarshal.AsBytes(span));

                            foreach (ref StaticsBlock sb in span)
                            {
                                if (sb.X >= 8 || sb.Y >= 8 || sb.Color == 0 || sb.Color == 0xFFFF || !GameObject.CanBeDrawn(world, sb.Color))
                                {
                                    continue;
                                }

                                int i = sb.Y * 8 + sb.X;

                                if (sb.Z >= top[i])
                                {
                                    ushort c = sb.Hue != 0 ? hues.GetColor16(16384, sb.Hue) : hues.GetRadarColorData(sb.Color + 0x4000);
                                    color[i] = HuesHelper.Color16To32((ushort)(0x8000 | c));
                                    top[i] = sb.Z;
                                }
                            }
                        }

                        for (int ty = 0; ty < 8; ty += S)
                        {
                            int py = (by * 8 + ty) / S;

                            if (py >= th)
                            {
                                break;
                            }

                            for (int tx = 0; tx < 8; tx += S)
                            {
                                int pxi = (bx * 8 + tx) / S;

                                if (pxi >= tw)
                                {
                                    break;
                                }

                                int r = 0, g = 0, b = 0, z = sbyte.MinValue;

                                for (int dy = 0; dy < S; dy++)
                                {
                                    for (int dx = 0; dx < S; dx++)
                                    {
                                        int i = (ty + dy) * 8 + tx + dx;
                                        uint c = color[i];
                                        r += (int)(c & 0xFF);
                                        g += (int)((c >> 8) & 0xFF);
                                        b += (int)((c >> 16) & 0xFF);
                                        z = Math.Max(z, top[i]);
                                    }
                                }

                                const int N = S * S;
                                px[py * tw + pxi] = (uint)(r / N | (g / N) << 8 | (b / N) << 16) | 0xFF000000;
                                zs[py * tw + pxi] = (sbyte)z;
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (UOFile f in files.Values)
                {
                    f.Dispose();
                }
            }

            // relief, lit from the north-west (as the world map shades its picture)
            for (int y = th - 1; y >= 1; y--)
            {
                for (int x = tw - 1; x >= 1; x--)
                {
                    int dz = zs[y * tw + x] - zs[(y - 1) * tw + x - 1];

                    if (dz == 0)
                    {
                        continue;
                    }

                    float f = dz > 0 ? 1.18f : 0.82f;
                    uint c = px[y * tw + x];
                    uint r = (uint)Math.Min(255, (int)((c & 0xFF) * f));
                    uint g = (uint)Math.Min(255, (int)(((c >> 8) & 0xFF) * f));
                    uint b = (uint)Math.Min(255, (int)(((c >> 16) & 0xFF) * f));
                    px[y * tw + x] = r | g << 8 | b << 16 | 0xFF000000;
                }
            }

            Log.Info($"[UOMobile] map picture {tw}x{th} in {watch.ElapsedMilliseconds} ms");

            return px;
        }

        // ------------------------------------------------------------------ drawing

        // (translucent colours are premultiplied: the batcher blends that way)
        private static readonly Color Back = new Color(6, 6, 9, 240);
        private static readonly Color BarFill = new Color(19, 19, 26, 235);
        private static readonly Color ButtonFill = new Color(54, 54, 72, 230);
        private static readonly Color LegendFill = new Color(0, 0, 0, 170);
        private static readonly Color Held = new Color(60, 200, 80, 255), Cleared = new Color(235, 205, 60, 255), Overrun = new Color(215, 45, 45, 255);
        private static readonly Color HeldArea = new Color(14, 47, 19, 60), ClearedArea = new Color(55, 48, 14, 60), OverrunArea = new Color(46, 10, 10, 55);
        private static readonly Color PeddlerColor = new Color(255, 140, 20, 255), Fleeing = new Color(255, 60, 30, 255);
        private static readonly Color CampColor = new Color(255, 220, 90, 255), FriendCampColor = new Color(120, 200, 255, 255);
        private static readonly Color Ring = new Color(0, 0, 0, 220), White = new Color(255, 255, 255, 255), AlertRing = new Color(255, 40, 40, 255);

        private static ushort TownHue(int state) => state == 2 ? (ushort)0x0044 : state == 1 ? (ushort)0x0035 : (ushort)0x0022;

        private static Color TownColor(int state) => state == 2 ? Held : state == 1 ? Cleared : Overrun;

        private RenderedText Label(string text, ushort hue)
        {
            if (!_labels.TryGetValue((text, hue), out RenderedText t) || t.IsDestroyed)
            {
                _labels[(text, hue)] = t = RenderedText.Create(text, hue, 1, true, FontStyle.BlackBorder);
            }

            return t;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (IsDisposed || World.Player == null)
            {
                return false;
            }

            TakeTexture(); // (a drawn frame: GPU work is allowed)

            float layerDepth = layerDepthRef;
            Rectangle v = View;
            Rectangle s = TouchInput.Safe;
            float scale = Scale;
            Vector3 hue = ShaderHueTranslator.GetHueVector(0);
            int map = World.MapIndex;
            Texture2D tex = _textureMap == map && _texture != null && !_texture.IsDisposed ? _texture : null;
            (int mapW, int mapH) = MapSize();
            uint now = Time.Ticks;
            bool blink = now / 400 % 2 == 0;
            List<(RenderedText Text, int X, int Y)> texts = _texts;
            texts.Clear();

            // what's visible, in whole texels so the picture and the markers line up
            float halfW = v.Width / 2f / scale, halfH = v.Height / 2f / scale;
            int t0x = Math.Max(0, (int)MathF.Floor((_cx - halfW) / TilesPerTexel)), t0y = Math.Max(0, (int)MathF.Floor((_cy - halfH) / TilesPerTexel));
            int t1x = Math.Min(_texW > 0 ? _texW : mapW / TilesPerTexel, (int)MathF.Ceiling((_cx + halfW) / TilesPerTexel) + 1);
            int t1y = Math.Min(_texH > 0 ? _texH : mapH / TilesPerTexel, (int)MathF.Ceiling((_cy + halfH) / TilesPerTexel) + 1);
            Point d0 = ToScreen(t0x * TilesPerTexel, t0y * TilesPerTexel, v, scale), d1 = ToScreen(t1x * TilesPerTexel, t1y * TilesPerTexel, v, scale);
            Rectangle dest = new Rectangle(d0.X, d0.Y, d1.X - d0.X, d1.Y - d0.Y);
            Rectangle src = new Rectangle(t0x, t0y, t1x - t0x, t1y - t0y);

            // markers on screen
            List<SurvivalMarker> markers = ZombieMarkers.Markers;
            List<(Point P, Color C, int R, bool Ring)> dots = _dots;
            List<(Rectangle R, Color C, Color Edge)> areas = _areas;
            dots.Clear();
            areas.Clear();

            for (int i = 0; i < markers.Count; i++)
            {
                SurvivalMarker k = markers[i];

                if (k.Facet != map)
                {
                    continue;
                }

                Point p = Spot(k.X, k.Y, v, scale);
                bool selected = i == _selected;

                switch (k.Kind)
                {
                    case SurvivalMarkerKind.Town:
                    {
                        Point a = ToScreen(k.OX, k.OY, v, scale), b = ToScreen(k.OX + k.W, k.OY + k.H, v, scale);

                        if (b.X - a.X >= 14)
                        {
                            Color area = k.TownState == 2 ? HeldArea : k.TownState == 1 ? ClearedArea : OverrunArea;
                            areas.Add((new Rectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y), area, TownColor(k.TownState)));
                        }

                        dots.Add((p, TownColor(k.TownState), selected ? 8 : 6, true));
                        RenderedText t = Label(k.Name, TownHue(k.TownState));
                        texts.Add((t, p.X - t.Width / 2, p.Y + 8));

                        break;
                    }

                    case SurvivalMarkerKind.Peddler:
                    {
                        dots.Add((p, k.Fleeing && blink ? Fleeing : PeddlerColor, selected ? 7 : 5, true));

                        if (scale >= 0.5f || selected)
                        {
                            RenderedText t = Label(k.Fleeing ? "Peddler (fleeing)" : "Peddler", 0x002B);
                            texts.Add((t, p.X - t.Width / 2, p.Y - 8 - t.Height));
                        }

                        break;
                    }

                    default: // camps
                    {
                        bool mine = k.Kind == SurvivalMarkerKind.Camp;

                        if (k.UnderAttack && blink)
                        {
                            dots.Add((p, AlertRing, 13, false));
                        }

                        dots.Add((p, mine ? CampColor : FriendCampColor, selected ? 8 : 6, true));
                        RenderedText t = Label(k.Name, mine ? (ushort)0x0035 : (ushort)0x0059);
                        texts.Add((t, p.X - t.Width / 2, p.Y + 9));

                        break;
                    }
                }
            }

            foreach (ZombieMarkers.AlertSpot a in ZombieMarkers.AlertSpots)
            {
                if (a.Facet == map && now - a.At < ZombieMarkers.AlertSpotMs && blink)
                {
                    // red for danger (a camp attacked, a horde, a town fallen, a tripwire); a town held or cleared rings its colour
                    dots.Add((Spot(a.X, a.Y, v, scale), a.Kind == 4 ? Held : a.Kind == 5 ? Cleared : AlertRing, 16, false));
                }
            }

            Point me = Spot(World.Player.X, World.Player.Y, v, scale);
            dots.Add((me, White, _selected == -2 ? 6 : 5, true));
            RenderedText you = Label("You", 0x0481);
            texts.Add((you, me.X - you.Width / 2, me.Y - 8 - you.Height));

            // status (top right) and the selection or a hint (the bar)
            string status = tex == null ? (_buildFailed ? "The map picture couldn't be made." : "Drawing the map...")
                : !ZombieMarkers.Supported ? "" : $"Markers {Math.Max(0, (int)((now - ZombieMarkers.ReceivedAt) / 1000))} s old";

            if (_statusText == null || _statusShown != status)
            {
                _statusText?.Destroy();
                _statusText = RenderedText.Create(status, 0x0481, 1, true, FontStyle.BlackBorder);
                _statusShown = status;
            }

            int infoWidth = Math.Max(80, s.Width - 12 - 260 - 12);

            if (_infoText == null || _infoShown != _info || _infoText.MaxWidth != infoWidth)
            {
                _infoText?.Destroy();
                _infoText = RenderedText.Create(_info, 0x0481, 1, true, FontStyle.BlackBorder, TEXT_ALIGN_TYPE.TS_LEFT, infoWidth);
                _infoShown = _info;
            }

            // the legend (top left)
            List<(Color C, RenderedText T)> legend = new List<(Color, RenderedText)>
            {
                (Held, Label("Held", 0x0481)), (Cleared, Label("Cleared", 0x0481)), (Overrun, Label("Overrun", 0x0481)),
                (PeddlerColor, Label("Peddler", 0x0481)), (CampColor, Label("Your camp", 0x0481))
            };
            int legendW = 8;

            foreach ((Color _, RenderedText t) in legend)
            {
                legendW += 14 + t.Width + 10;
            }

            bool showLegend = ZombieMarkers.Supported;
            Rectangle legendBox = new Rectangle(v.X + 4, v.Y + 4, Math.Min(legendW, v.Width - 8), 22);
            Rectangle bar = new Rectangle(s.X, s.Bottom - BarH, s.Width, BarH);
            List<Rectangle> buttons = _buttonRects;
            buttons.Clear();

            foreach (Control c in _bar)
            {
                buttons.Add(new Rectangle(c.X, c.Y, c.Width, c.Height));
            }

            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(Back), new Rectangle(0, 0, TouchInput.ScreenW, TouchInput.ScreenH), hue, layerDepth);

                    if (batcher.ClipBegin(v.X, v.Y, v.Width, v.Height))
                    {
                        if (tex != null && src.Width > 0 && src.Height > 0)
                        {
                            batcher.Draw(tex, dest, src, hue, layerDepth);
                        }

                        foreach ((Rectangle r, Color c, Color edge) in areas)
                        {
                            batcher.Draw(SolidColorTextureCache.GetTexture(c), r, hue, layerDepth);
                            batcher.DrawRectangle(SolidColorTextureCache.GetTexture(edge), r.X, r.Y, r.Width, r.Height, hue, layerDepth);
                        }

                        foreach ((Point p, Color c, int r, bool ring) in dots)
                        {
                            if (ring)
                            {
                                DrawCircle(batcher, p, r + 2, Ring, false, hue, layerDepth);
                                DrawCircle(batcher, p, r, c, false, hue, layerDepth);
                            }
                            else
                            {
                                DrawCircle(batcher, p, r, c, true, hue, layerDepth);
                            }
                        }

                        if (showLegend)
                        {
                            batcher.Draw(SolidColorTextureCache.GetTexture(LegendFill), legendBox, hue, layerDepth);
                            int lx = legendBox.X + 8;

                            foreach ((Color c, RenderedText t) in legend)
                            {
                                DrawCircle(batcher, new Point(lx + 5, legendBox.Y + 11), 5, c, false, hue, layerDepth);
                                lx += 14 + t.Width + 10;
                            }
                        }

                        batcher.ClipEnd();
                    }

                    batcher.Draw(SolidColorTextureCache.GetTexture(BarFill), bar, hue, layerDepth);

                    foreach (Rectangle r in buttons)
                    {
                        batcher.Draw(SolidColorTextureCache.GetTexture(ButtonFill), r, hue, layerDepth);
                        batcher.DrawRectangle(SolidColorTextureCache.GetTexture(new Color(150, 140, 110, 255)), r.X, r.Y, r.Width, r.Height, hue, layerDepth);
                    }

                    return true;
                }
            );

            // the labels, inside the view only
            foreach ((RenderedText t, int tx, int ty) in texts)
            {
                if (tx >= v.X && ty >= v.Y && tx + t.Width <= v.Right && ty + t.Height <= v.Bottom &&
                    !(showLegend && legendBox.Intersects(new Rectangle(tx, ty, t.Width, t.Height))))
                {
                    renderLists.AddGumpNoAtlas(t, tx, ty, layerDepth);
                }
            }

            if (showLegend)
            {
                int lx = legendBox.X + 8;

                foreach ((Color _, RenderedText t) in legend)
                {
                    if (lx + 14 + t.Width <= legendBox.Right)
                    {
                        renderLists.AddGumpNoAtlas(t, lx + 14, legendBox.Y + (22 - t.Height) / 2, layerDepth);
                    }

                    lx += 14 + t.Width + 10;
                }
            }

            if (!string.IsNullOrEmpty(status))
            {
                // top right; under the legend where the legend reaches that far (portrait)
                int sx = v.Right - _statusText.Width - 6;
                int sy = showLegend && legendBox.Right + 8 > sx ? legendBox.Bottom + 4 : v.Y + 6;
                renderLists.AddGumpNoAtlas(_statusText, sx, sy, layerDepth);
            }

            renderLists.AddGumpNoAtlas(_infoText, bar.X + 10, bar.Y + (BarH - _infoText.Height) / 2, layerDepth);

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        public override void Dispose()
        {
            foreach (RenderedText t in _labels.Values)
            {
                t.Destroy();
            }

            _labels.Clear();
            _infoText?.Destroy();
            _statusText?.Destroy();
            _infoText = _statusText = null;
            base.Dispose();
        }

        // ---------- circles ----------

        private const int TEX = 32;
        private static readonly Dictionary<(Color, bool), Texture2D> _circles = new Dictionary<(Color, bool), Texture2D>();

        private static void DrawCircle(UltimaBatcher2D batcher, Point c, int r, Color color, bool ring, Vector3 hue, float depth)
        {
            batcher.Draw(GetCircle(color, ring), new Rectangle(c.X - r, c.Y - r, r * 2, r * 2), hue, depth);
        }

        private static Texture2D GetCircle(Color color, bool ring)
        {
            if (_circles.TryGetValue((color, ring), out Texture2D tex) && !tex.IsDisposed)
            {
                return tex;
            }

            Color[] data = new Color[TEX * TEX];
            float mid = (TEX - 1) / 2f, outer = TEX / 2f - 1f, inner = outer - 4f;

            for (int y = 0; y < TEX; y++)
            {
                for (int x = 0; x < TEX; x++)
                {
                    float d = MathF.Sqrt((x - mid) * (x - mid) + (y - mid) * (y - mid));
                    float a = Math.Clamp(outer - d + 0.5f, 0f, 1f);

                    if (ring)
                    {
                        a *= Math.Clamp(d - inner + 0.5f, 0f, 1f);
                    }

                    float alpha = a * color.A / 255f;
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
