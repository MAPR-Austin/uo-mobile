// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    /// <summary>What a Zombie Survival map marker is (servuo Custom/Zombie/PhoneMarkers.cs MarkerKind).</summary>
    internal enum SurvivalMarkerKind : byte
    {
        Town = 1,
        Peddler = 2,
        Camp = 3,
        FriendCamp = 4,

        /// <summary>
        /// Build 56 (servuo RescueMissions): a stranded survivor's signal ("SOS: ..."), or, state 0x40, your escort under
        /// way ("Escort: ..., N min"). Sent only to an app that asks with <see cref="ZombieMarkers"/>' flag 4; an older
        /// app gets the signals as friends' camps.
        /// </summary>
        Rescue = 5,

        /// <summary>
        /// Build 57 (servuo Dungeons, DungeonMarkers): a dungeon's door, named by the dungeon; state: 0 its dead stand,
        /// 1 quiet (the crypt bare, the pits stilled), 2 laid to rest (the crypt's master dead). Sent only to an app that asks
        /// with <see cref="ZombieMarkers"/>' flag 8; an older app gets the doors as friends' camps named "Dungeon: ...".
        /// </summary>
        Dungeon = 6
    }

    /// <summary>One marker: where (X, Y), a town's outline (OX, OY, W, H), its state and name.</summary>
    internal readonly struct SurvivalMarker
    {
        public SurvivalMarker(SurvivalMarkerKind kind, byte state, byte facet, short x, short y, short ox, short oy, ushort w, ushort h, string name)
        {
            Kind = kind;
            State = state;
            Facet = facet;
            X = x;
            Y = y;
            OX = ox;
            OY = oy;
            W = w;
            H = h;
            Name = name ?? "";
        }

        public readonly SurvivalMarkerKind Kind;
        public readonly byte State;
        public readonly byte Facet;
        public readonly short X, Y, OX, OY;
        public readonly ushort W, H;
        public readonly string Name;

        /// <summary>A town: 0 overrun, 1 cleared, 2 held.</summary>
        public int TownState => State;

        public bool Fleeing => Kind == SurvivalMarkerKind.Peddler && (State & 1) != 0;

        public bool FireOut => (Kind == SurvivalMarkerKind.Camp || Kind == SurvivalMarkerKind.FriendCamp) && (State & 1) != 0;

        public bool UnderAttack => (Kind == SurvivalMarkerKind.Camp || Kind == SurvivalMarkerKind.FriendCamp) && (State & 0x40) != 0;

        /// <summary>A rescue marker: your escort under way (else a survivor's SOS signal).</summary>
        public bool Escort => Kind == SurvivalMarkerKind.Rescue && (State & 0x40) != 0;

        /// <summary>A dungeon's door: 0 its dead stand, 1 quiet, 2 laid to rest (servuo Dungeon.MarkerState).</summary>
        public int DungeonState => Kind == SurvivalMarkerKind.Dungeon ? State : 0;
    }

    /// <summary>
    /// Zombie Survival's map markers and alerts (iPhone build 53). The server (servuo Custom/Zombie/PhoneMarkers.cs and
    /// PhoneAlerts.cs - keep both ends' numbers in step) sends 0xBF 0x7A61 by itself 2 s after login: every town with its
    /// state (overrun, cleared, held) and outline, the wandering peddlers where they are, your camp and your friends'.
    /// That tells the app this shard has markers and alerts, so it says hello (0x7A60 flag 1: "I show alert banners") and
    /// from then on asks for fresh markers (0x7A60 flag 2) when a map opens and every 30 s while one is open (the phone's
    /// <see cref="SurvivalMapGump" />, or ClassicUO's world map). Alerts come as 0x7A62 (<see cref="AlertBanner" />).
    /// A server without it never sends 0x7A61, so the app never sends anything (an old server would only trace an
    /// unknown sub-command). Everything here is forgotten on logout (GameScene.Unload), so another shard's markers never
    /// show.
    /// </summary>
    internal static class ZombieMarkers
    {
        public const ushort RequestSub = 0x7A60; // servuo ZombieMarkers.RequestSub
        public const ushort MarkersSub = 0x7A61; // ZombieMarkers.MarkersSub
        public const ushort AlertSub = 0x7A62; // ZombieAlerts.AlertSub

        // 4 (build 56): "I draw rescue markers (kind 5)" - servuo RescueMissions.FlagRescueMarkers; 8 (build 57): "I draw dungeon
        // doors (kind 6)" - servuo DungeonMarkers.FlagDungeonMarkers. Sent with every hello and request, so the server's
        // answers carry kinds 5 and 6 (it remembers them per connection)
        private const byte FlagAlerts = 1, FlagSend = 2, FlagRescue = 4, FlagDungeon = 8;

        /// <summary>The kinds this app draws, said with every hello and request.</summary>
        private const byte FlagKinds = FlagRescue | FlagDungeon;

        /// <summary>After the hello, one fresh request: the login push came before the server knew this app draws kinds 5 and 6.</summary>
        private const uint RescueRefreshMs = 2500;

        private static uint _helloAt;
        private static bool _rescueRefreshed;

        /// <summary>How often an open map asks for fresh markers (peddlers walk).</summary>
        public const uint RefreshMs = 30000;

        public static readonly List<SurvivalMarker> Markers = new List<SurvivalMarker>();

        /// <summary>This login's shard sent markers: it has markers and alerts.</summary>
        public static bool Supported { get; private set; }

        /// <summary>Goes up whenever the markers change (maps redraw their labels).</summary>
        public static int Version { get; private set; }

        /// <summary>When the markers last came (Time.Ticks).</summary>
        public static uint ReceivedAt { get; private set; }

        private static bool _helloSent;
        private static uint _lastRequest;
        private static bool _mapWasOpen;

        /// <summary>A spot an alert was about (a horde's town, the camp under attack): the map rings it for a while.</summary>
        internal readonly struct AlertSpot
        {
            public AlertSpot(byte kind, short x, short y, byte facet, uint at)
            {
                Kind = kind;
                X = x;
                Y = y;
                Facet = facet;
                At = at;
            }

            public readonly byte Kind;
            public readonly short X, Y;
            public readonly byte Facet;
            public readonly uint At;
        }

        /// <summary>How long a map rings an alert's spot.</summary>
        public const uint AlertSpotMs = 180000;

        public static readonly List<AlertSpot> AlertSpots = new List<AlertSpot>();

        /// <summary>PacketHandlers (0xBF 0x7A61): the markers.</summary>
        public static void Read(World world, ref StackDataReader p)
        {
            byte version = p.ReadUInt8();

            if (version != 1)
            {
                Log.Warn($"[UOMobile] markers version {version} not understood");

                return;
            }

            int count = p.ReadUInt16BE();
            List<SurvivalMarker> list = new List<SurvivalMarker>(count);

            for (int i = 0; i < count && p.Remaining >= 16; i++) // (16 bytes before the name)
            {
                SurvivalMarkerKind kind = (SurvivalMarkerKind)p.ReadUInt8();
                byte state = p.ReadUInt8();
                byte facet = p.ReadUInt8();
                short x = (short)p.ReadUInt16BE();
                short y = (short)p.ReadUInt16BE();
                short ox = (short)p.ReadUInt16BE();
                short oy = (short)p.ReadUInt16BE();
                ushort w = p.ReadUInt16BE();
                ushort h = p.ReadUInt16BE();
                int nameLength = p.ReadUInt8();
                string name = nameLength > 0 ? p.ReadUnicodeBE(nameLength) : "";

                if (kind >= SurvivalMarkerKind.Town && kind <= SurvivalMarkerKind.Dungeon)
                {
                    list.Add(new SurvivalMarker(kind, state, facet, x, y, ox, oy, w, h, name)); // (a kind from a newer server: skipped)
                }
            }

            Markers.Clear();
            Markers.AddRange(list);
            Supported = true;
            Version++;
            ReceivedAt = Time.Ticks;

            if (!_helloSent)
            {
                SayHello(world); // the banners may come now
            }

            AlertBanner.Ensure(world); // (its Update keeps an open map's markers fresh)
        }

        /// <summary>An alert about a spot (AlertBanner): the map rings it for a few minutes.</summary>
        public static void NoteAlert(byte kind, short x, short y, byte facet)
        {
            if (x == 0 && y == 0)
            {
                return;
            }

            AlertSpots.RemoveAll(s => Time.Ticks - s.At > AlertSpotMs || s.X == x && s.Y == y);
            AlertSpots.Add(new AlertSpot(kind, x, y, facet, Time.Ticks));
            Version++;
        }

        /// <summary>Fresh markers now (the server answers at most every 2 s); nothing on a shard without them.</summary>
        public static void Request(World world)
        {
            if (!Supported)
            {
                return;
            }

            _lastRequest = Time.Ticks;
            Send(world, FlagSend | FlagKinds);
        }

        /// <summary>"I show alert banners, and I draw rescue markers and dungeon doors" (an old server reads only the flags it knows).</summary>
        private static void SayHello(World world)
        {
            _helloSent = Send(world, FlagAlerts | FlagKinds);

            if (_helloSent)
            {
                _helloAt = Time.Ticks;
            }
        }

        private static bool MapOpen => UIManager.GetGump<SurvivalMapGump>() != null || UIManager.GetGump<WorldMapGump>() != null;

        /// <summary>Every frame (AlertBanner, SurvivalMapGump): a map that just opened, or has been open 30 s, asks again.</summary>
        public static void Update(World world)
        {
            if (Supported && !_helloSent)
            {
                SayHello(world); // (the first try came too early: once the player is there)
            }

            // the login push (before the hello) had any SOS or dungeon door as a friend's camp: ask once more, past the server's 2 s
            if (Supported && _helloSent && !_rescueRefreshed && Time.Ticks - _helloAt >= RescueRefreshMs)
            {
                _rescueRefreshed = true;
                Request(world);
            }

            bool open = MapOpen;

            if (Supported && open && (!_mapWasOpen || Time.Ticks - _lastRequest >= RefreshMs))
            {
                Request(world);
            }

            _mapWasOpen = open;
        }

        private static bool Send(World world, byte flags)
        {
            if (world == null || !world.InGame || world.Player == null || NetClient.Socket == null || !NetClient.Socket.IsConnected)
            {
                return false; // (an in-game sub-command before the player exists would get the client dropped)
            }

            var writer = new StackDataWriter(8);
            writer.WriteUInt8(0xBF);
            writer.WriteZero(2);
            writer.WriteUInt16BE(RequestSub);
            writer.WriteUInt8(flags);
            writer.Seek(1, SeekOrigin.Begin);
            writer.WriteUInt16BE((ushort)writer.BytesWritten);
            NetClient.Socket.Send(writer.BufferWritten);
            writer.Dispose();

            return true;
        }

        /// <summary>GameScene.Unload: nothing carries over to the next login (or another shard).</summary>
        public static void Reset()
        {
            Markers.Clear();
            AlertSpots.Clear();
            Supported = false;
            _helloSent = false;
            _helloAt = 0;
            _rescueRefreshed = false;
            _lastRequest = 0;
            _mapWasOpen = false;
            Version++;
            AlertBanner.Reset();
            SurvivalMapGump.Release();
        }
    }
}
