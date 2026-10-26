#!/usr/bin/env bash
# iOS pre-ship static checks (see ios/AUDIT.md). Fast; runs locally (Git Bash / macOS) and as
# the first CI step. Each check encodes a failure that already cost a TestFlight round trip.
set -uo pipefail

cd "$(dirname "$0")/.."
fail=0
ok()   { printf '  ok   %s\n' "$1"; }
bad()  { printf '  FAIL %s\n' "$1"; fail=1; }
need() { if grep -q -- "$2" "$1"; then ok "$3"; else bad "$3  ($1 lacks: $2)"; fi; }
none() { # none <description> <grep -rnE args...>: fails if anything matches, or if grep itself errors
  local desc="$1"; shift
  local hits rc
  hits="$(grep -rnE "$@" 2>&1)"; rc=$?
  if [ $rc -eq 2 ]; then bad "$desc (the check itself failed: $hits)"; return; fi
  if [ -z "$hits" ]; then ok "$desc"; else bad "$desc"; printf '%s\n' "$hits" | sed 's/^/         /' | head -20; fi
}

echo "== Info.plist / packaging"
P=ios/Info.plist
need $P NSCameraUsageDescription            "camera purpose string (ITMS-90683, build 21)"
need $P NSMicrophoneUsageDescription        "microphone purpose string"
need $P NSBluetoothAlwaysUsageDescription   "Bluetooth purpose string (ITMS-90683)"
need $P ITSAppUsesNonExemptEncryption       "export-compliance flag"
need $P NSLocalNetworkUsageDescription      "local network purpose string"
[ -f ios/Assets.xcassets/AppIcon.appiconset/icon-1024.png ] && ok "1024px app icon present" || bad "app icon missing"

echo "== Native linking"
C=ios/ClassicUO.iOS.csproj
need $C "<FnaLinkMode Condition=\"'\$(FnaLinkMode)' == ''\">mtouch</FnaLinkMode>" "fnalibs linked via MtouchExtraArgs (NativeReference was ignored, build 17)"
for fw in AudioToolbox AVFoundation CoreAudio CoreBluetooth CoreGraphics CoreHaptics CoreMedia CoreMotion CoreVideo Foundation GameController Metal QuartzCore UIKit UniformTypeIdentifiers; do
  grep -q -- "-framework $fw" $C || bad "framework $fw missing from MtouchExtraArgs (build 18)"
done
ok "SDL3 framework list checked"
need ios/build-ios.sh "aligned(8))) name##Default" "F3DAudio alignment patch (build 19)"
need ios/build-ios.sh 'nm -gU "$exe" > "$syms"' "symbol check without nm|grep -q under pipefail"

echo "== DllImport resolvers (one per assembly; FNA owns FNA.dll - builds 14-16, 22)"
none "ios/Program.cs never registers a resolver on the FNA/SDL assembly" \
     "SetDllImportResolver\(typeof\(SDL\)" ios/Program.cs
grep -A8 "internal static void Init()" src/ClassicUO.Client/DllMap.cs | grep -q "OperatingSystem.IsIOS()" \
  && ok "ClassicUO DllMap.Init skips iOS" || bad "ClassicUO DllMap.Init must return early on iOS"

echo "== APIs that throw PlatformNotSupportedException on iOS"
none "Console colour/title/key APIs only in the guarded Logger (build 25)" \
     "Console\.(ForegroundColor|BackgroundColor|Title|ReadKey|KeyAvailable|CancelKeyPress|WindowWidth|BufferWidth|CursorVisible)" \
     src --include=*.cs --exclude=Logger.cs
grep -q "ConsoleColorsSupported" src/ClassicUO.Utility/Logging/Logger.cs && ok "Logger colour guard present" || bad "Logger colour guard missing"
none "Process.Start only in PlatformHelper (which has an OpenUrlOverride for iOS)" \
     "Process\.Start\(" src --include=*.cs --exclude=PlatformHelper.cs --exclude-dir=ClassicUO.Bootstrap
none "no Reflection.Emit / DynamicMethod / Expression.Compile" \
     "System\.Reflection\.Emit|new DynamicMethod|\.Compile\(\)" src --include=*.cs
need ios/Program.cs '"-no_server_ping"' "server-list ICMP ping disabled on iOS"

echo "== Paths / lifecycle"
grep -A12 "private static void RealMain" ios/Program.cs | grep -q "SetCurrentDirectory(_documents)" \
  && ok "RealMain chdirs back to Documents after SDL startup (build 28)" || bad "RealMain must chdir to Documents"
need ios/Program.cs "FindUoData" "UO data found in any Documents subfolder (build 29)"
need src/ClassicUO.Client/GameController.cs "SDL_EVENT_DID_ENTER_BACKGROUND" "save/stop drawing on background"
need src/ClassicUO.Client/Configuration/ConfigurationResolver.cs "catch (JsonException" "corrupt JSON does not crash every launch"

echo "== iOS-only substitutes (must also be exercisable on desktop)"
grep -A14 "static ZLib()" src/ClassicUO.Utility/ZLib.cs | grep -q "new DotNetZLib()" \
  && ok "iOS zlib = System.IO.Compression (ZLibManaged failed 'CRC mismatch', build 29)" || bad "iOS zlib must be DotNetZLib"
grep -A14 "static ZLib()" src/ClassicUO.Utility/ZLib.cs | grep -q "UOM_DOTNET_ZLIB" \
  && ok "zlib substitute can be forced on desktop (UOM_DOTNET_ZLIB=1)" || bad "zlib substitute needs a desktop test flag"

echo "== Orientation / screen fit"
# Info.plist (the iPhone array, not ~ipad) is the real gate: FNA overrides the SDL_ORIENTATIONS hint
# at startup with its own list, which must include every orientation the plist allows.
p1=$(sed -n '/<key>UISupportedInterfaceOrientations<\/key>/,/<\/array>/p' ios/Info.plist | grep -c "UIInterfaceOrientationPortrait<")
p2=$(grep -A3 "SDL_HINT_ORIENTATIONS," external/FNA/src/FNAPlatform/SDL3_FNAPlatform.cs | grep -c "Portrait")
if [ "$p1" -eq 0 ] || [ "$p2" -gt 0 ]; then ok "portrait in Info.plist is allowed by FNA's SDL_ORIENTATIONS"; else bad "Info.plist allows portrait but FNA's SDL_ORIENTATIONS does not"; fi
# Build 46: portrait spots are derived from landscape; build 50: and pinned where portrait first shows them
# (each orientation keeps its own). The build-31 failure was derived spots landing off-screen / on the
# joystick, so every portrait spot must go through ActionLayout.PortraitOf, which keeps the cluster clear
# of the joystick and on screen.
need src/ClassicUO.Client/Touch/TouchInput.cs "Current.PortraitOf(b, Aspect, Revision)" "HUD buttons take their portrait spot from ActionLayout.PortraitOf"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "Math.Clamp(px, 0.04f, 0.96f), Math.Clamp(py, 0.02f, 0.98f)" "derived portrait spots are kept on screen"
need src/ClassicUO.Client/Touch/TouchHudGump.cs "layout.PinPortrait(TouchInput.Aspect, TouchInput.Revision)" "portrait spots are pinned where portrait first shows them (each orientation keeps its own)"
need src/ClassicUO.Client/GameController.cs "UOM_PHONE_FIT" "phone screen-fit path can be forced on desktop (UOM_PHONE_FIT=1)"
need src/ClassicUO.Client/GameController.cs "FollowPhoneRotation();" "backbuffer follows rotation (FNA does not update PreferredBackBuffer*)"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "PinHandPlacedPortraitSpots(set)" "old layouts migrate: only portrait spots placed by hand stay pinned (build 46)"
need src/ClassicUO.Client/Game/Managers/UIManager.cs "HitTestGumps(position, TouchInput.PointerGump" "hit testing maps zoomed windows like the pointer does"
need src/ClassicUO.Client/GameController.cs "GumpScale.Save();" "window zoom saved on suspend"
# Build 47: the HUD draws over every window, so buttons step aside for shop and trade windows (their
# Accept/Clear sat under Set Target and Cure Self), both when drawn and when hit.
need src/ClassicUO.Client/Touch/TouchInput.cs "InCircle(p, ButtonCenter(b), ButtonRadius(b)) && !SteppedAside(b)" "HUD buttons over a shop/trade window take no taps"
need src/ClassicUO.Client/Touch/TouchHudGump.cs "if (TouchInput.SteppedAside(b))" "HUD buttons over a shop/trade window are not drawn"
need src/ClassicUO.Client/Touch/TouchHudGump.cs "TouchInput.FindTradeWindows();" "the HUD finds shop/trade windows every frame"

echo "== Houses"
# Build 47: a dyed custom house: tiles take the house hue, and a hue change recolors in place (a
# reload dropped every custom wall and floor until the design was sent again).
[ "$(grep -c 'item.Hue, // UO Mobile: a dyed custom house keeps its color' src/ClassicUO.Client/Network/PacketHandlers.cs)" = 3 ] \
  && ok "custom house tiles take the house's hue (all 3 plane modes)" || bad "every custom house tile must be added with item.Hue"
need src/ClassicUO.Client/Network/PacketHandlers.cs "item.WantUpdateMulti = moved || (item.Hue != hue && !recolorHouse);" "a house's hue change recolors in place instead of reloading"
# Build 48: furniture deeds get the house's placement ghost: the server sends 0xBF 0x7A57 (cursor id +
# pieces, servuo AddonPreview.cs) and answers 0x7A55 for the deed; keep both ends' numbers in step.
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort AddonPieces = 0x7A57;" "furniture deed pieces sub-command is 0x7A57 (servuo AddonPreview.Pieces)"
need src/ClassicUO.Client/Network/PacketHandlers.cs "case Touch.HousePlacementGhost.AddonPieces:" "0xBF 0x7A57 reaches the placement ghost"
need src/ClassicUO.Client/Game/Scenes/GameScene.cs "Touch.HousePlacementGhost.UpdateAddon(_world, SelectedObject.Object as GameObject);" "the furniture ghost is drawn every frame"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "world.TargetManager.TargetCursorId == _addonCursor" "the furniture ghost only follows the deed's own cursor"
# Build 52: Zombie Survival's build cursor lays lines and turns (servuo Custom/Zombie/BuildLines.cs): 0x7A5B describes the
# cursor, 0x7A59 asks about a dragged line and 0x7A5A answers piece by piece, 0x7A58 lays it, 0x7A5C turns the piece.
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort LineLay = 0x7A58;" "line lay sub-command is 0x7A58 (servuo BuildLines.Lay)"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort LineAsk = 0x7A59;" "line question sub-command is 0x7A59 (BuildLines.Ask)"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort LineAnswer = 0x7A5A;" "line answer sub-command is 0x7A5A (BuildLines.Answer)"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort BuildInfo = 0x7A5B;" "build cursor info sub-command is 0x7A5B (BuildLines.Info)"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "public const ushort BuildTurn = 0x7A5C;" "turn sub-command is 0x7A5C (BuildLines.Turn)"
need src/ClassicUO.Client/Network/PacketHandlers.cs "case Touch.HousePlacementGhost.BuildInfo:" "0xBF 0x7A5B reaches the placement ghost"
need src/ClassicUO.Client/Network/PacketHandlers.cs "case Touch.HousePlacementGhost.LineAnswer:" "0xBF 0x7A5A reaches the placement ghost"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "_buildCursor == world.TargetManager.TargetCursorId" "lines and Turn only for the cursor the server described (a deed's or an old server's cursor drags the ghost)"
need src/ClassicUO.Client/Touch/TouchInput.cs "HousePlacementGhost.FingerUp(Client.Game.UO.World, UIManager.IsWorldAt(e.Pos), e.Pos == OffScreen);" "a line is laid only when the finger lifts over the world (a pinch's off-screen release lays none)"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "set = CreateDefault(OnZombieShard);" "a Zombie Survival character with no layouts starts on the Survival layout"
need src/ClassicUO.Client/Touch/ActionLayouts.cs 'public const string ZombieShardName = "Zombie Survival";' "the zombie shard is known by its server-list name"
need src/ClassicUO.Client/Touch/ButtonEditGump.cs '("Build", "say:\[build"), ("Towns", "say:\[towns"), ("Vendors", "say:\[vendors")' "the Build / Towns / Vendors presets ([build, [towns, [vendors)"
need src/ClassicUO.Client/Touch/HousePlacementGhost.cs "TouchInput.GhostFingerTravel >= LineSlop()" "a tap that rolls a little lays no line (a line starts after half a tile of travel)"
need src/ClassicUO.Client/GameController.cs "(SDL_EventType)sdlEvent->type == SDL_EventType.SDL_EVENT_FINGER_CANCELED" "a touch the system cancels (Control Center, a call) is passed on as canceled"
need src/ClassicUO.Client/Touch/TouchInput.cs "drop ? OffScreen : pos" "...and a canceled finger laying a line lifts off-screen: nothing is built"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "set.Layouts.Insert(0, CreateDefault(true)" "Zombie Survival characters with layouts saved before build 52 get the Survival layout once"
# Build 53: Zombie Survival's map markers and alerts (servuo Custom/Zombie/PhoneMarkers.cs, PhoneAlerts.cs): the server sends
# 0x7A61 markers by itself after login; only then does the app say hello / ask (0x7A60); alerts come as 0x7A62.
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "public const ushort RequestSub = 0x7A60;" "markers request / hello sub-command is 0x7A60 (servuo ZombieMarkers.RequestSub)"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "public const ushort MarkersSub = 0x7A61;" "markers sub-command is 0x7A61 (ZombieMarkers.MarkersSub)"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "public const ushort AlertSub = 0x7A62;" "alert sub-command is 0x7A62 (ZombieAlerts.AlertSub)"
need src/ClassicUO.Client/Network/PacketHandlers.cs "case Touch.ZombieMarkers.MarkersSub:" "0xBF 0x7A61 reaches the markers"
need src/ClassicUO.Client/Network/PacketHandlers.cs "case Touch.ZombieMarkers.AlertSub:" "0xBF 0x7A62 reaches the alert banner"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "_helloSent = Send(world, FlagAlerts | FlagKinds);" "the app says hello only after the shard sent markers (an old shard never gets 0x7A60)"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "if (world == null || !world.InGame || world.Player == null" "0x7A60 only once the player exists (an in-game sub-command before that gets the client dropped)"
need src/ClassicUO.Client/Game/Scenes/GameScene.cs "Touch.ZombieMarkers.Reset();" "markers and alerts are forgotten at logout (no other shard's markers)"
need src/ClassicUO.Client/Touch/AlertBanner.cs "AcceptMouseInput = false; // drawn, never hit" "the alert banner never takes a touch"
need src/ClassicUO.Client/Touch/AlertBanner.cs "UIManager.MakeTopMostGump(this);" "...and is drawn over every window while it shows, the map too (the stock sort moves one Over gump per pass)"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "CanCloseWithRightClick = false;" "a long press (a right click on the phone) doesn't close the map"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "TakeTexture(); // (a drawn frame" "the map's picture becomes a texture on a drawn frame, on the main thread (the worker only reads files)"
need src/ClassicUO.Client/Touch/TouchInput.cs "UIManager.GetGump<HousePlacementBar>() != null || UIManager.GetGump<SurvivalMapGump>() != null;" "the HUD steps aside while the map is open"
need src/ClassicUO.Client/Touch/TouchInput.cs "!(g is SurvivalMapGump)) // (the map is the screen" "the full-screen map never forces the wide portrait fit (it would keep itself wide)"
need src/ClassicUO.Client/Touch/ButtonEditGump.cs '("Repair All", "say:\[repairall")' "the Repair All preset ([repairall)"
need src/ClassicUO.Client/Touch/ActionLayouts.cs 'B("Repair\\nAll", "say:\[repairall"' "new Survival layouts have Repair All (and Map)"
# Build 54: a finger on any window is the window's - never a HUD button or the target panel under it - and a button a
# window covers steps aside (not drawn, not pressed); the editors draw steps and choices on plates in the bigger font.
need src/ClassicUO.Client/Touch/TouchInput.cs "bool window = !EditMode && WindowOverHud(pos);" "a finger on a window never presses the HUD button under it (build 54)"
need src/ClassicUO.Client/Touch/TouchInput.cs "!EditMode && !window && button < 0 && TouchHudGump.TargetPanel.Contains(pos)" "...nor the target panel"
need src/ClassicUO.Client/Touch/TouchInput.cs "foreach (Rectangle w in _windows)" "a button any window covers steps aside, not just under a shop or trade window"
need src/ClassicUO.Client/Touch/TouchInput.cs "!(g is TouchHudGump || g is Game.UI.Gumps.WorldViewportGump || g is AlertBanner)" "the HUD, the world and the alert banner are not windows a button steps aside for"
need src/ClassicUO.Client/Touch/MacroEditorGump.cs "new PlateButton(x, y, w, h, text, id, hue);" "the macro editor's buttons on plates, bigger font"
need src/ClassicUO.Client/Touch/ButtonEditGump.cs "new AlphaBlendControl(0.97f)" "the button editor's ground is near solid"
none "ClassicUO's world map stays off the phone's presets (a ~120 MB image built off the main thread; the phone's map is SurvivalMapGump)" \
     '"open:WorldMap"' src/ClassicUO.Client/Touch/ButtonEditGump.cs src/ClassicUO.Client/Touch/ActionLayouts.cs

# Build 55 (owner: "menus are still hard to read and navigate"): windows open at a readable zoom fitted to the screen
# (Touch/LargeUi), server menus take taps on a row's words or plate as its button, and get an X when they have no close.
need src/ClassicUO.Client/Touch/GumpScale.cs ": LargeUi.AutoScale(g);" "a window nobody pinched opens at the large-menu zoom (build 55)"
need src/ClassicUO.Client/Touch/GumpScale.cs "Math.Abs(scale - LargeUi.AutoScale(g)) < 0.03f" "...and a pinch back to that size forgets the pinch"
need src/ClassicUO.Client/Network/PacketHandlers.cs "Touch.LargeUi.Adapt(gump); // UO Mobile:" "a server menu with no close button gets the X"
need src/ClassicUO.Client/Touch/LargeUi.cs "!g.CanCloseWithRightClick || !IsServerMenu(g)" "...never a menu the server made unclosable"
need src/ClassicUO.Client/Touch/TouchInput.cs "MovePress(id, snap.Value);" "a tap on a server menu's label or plate presses the button it stands for"
need src/ClassicUO.Client/Touch/TouchInput.cs "!f.LongPressFired && !canceled && !f.Moved && f.Snap.HasValue" "...only a tap: a drag moves the window, a long press still right-clicks, a canceled touch presses nothing"
need src/ClassicUO.Client/Touch/TouchInput.cs "? SnapAtLift(id, f.Start)" "...asked again at the lift (the menu may have been rebuilt meanwhile); under the joystick's ring only real buttons take taps"
need src/ClassicUO.Client/Touch/TouchInput.cs "float zs = root != null ? GumpScale.Of(root) : 1f;" "the keyboard's text area follows a zoomed window's field"
need src/ClassicUO.Client/Touch/GumpScale.cs "LargeUi.FitScale(g, s)" "a pinched zoom is still fitted to the screen (and build 54's server-menu zooms are dropped once)"
need src/ClassicUO.Client/Touch/TouchInput.cs "LargeUi.KeepOnScreen();" "zoomed windows are kept on the screen every frame"
need src/ClassicUO.Client/Touch/LargeUi.cs "if (!On || UIManager.IsDragging" "...but not while a window is being dragged"
need src/ClassicUO.Client/Touch/TouchInput.cs "!LargeUi.Sizes(g) &&" "a fitted window never forces the wide portrait fit (it would shrink the whole UI)"
# Build 56 (build 55's audit notes; the zombie server's rescue missions): the X LargeUi adds beats the joystick ring; a wide
# server menu fits upright (0.4); a paged menu keeps its zoom; a press keeps its touch-down mapping through a rebuild; an HTML
# link is never a button's label; a pinched window isn't pulled back mid-pinch; rescue markers (kind 5, asked with flag 4).
need src/ClassicUO.Client/Touch/TouchInput.cs "c is NiceButton || c is HitBox hit && hit.HasClickHandler" "a server menu's X (a NiceButton) under the joystick ring takes the tap; a plain HitBox doesn't"
need src/ClassicUO.Client/Touch/LargeUi.cs "float min = IsServerMenu(g) ? MENU_MIN : GumpScale.MIN;" "a server menu up to ~1170 wide fits an upright screen (its X on screen)"
need src/ClassicUO.Client/Touch/LargeUi.cs "= new MenuZoom { ScreenW = sw, ScreenH = sh, Scale = s, Seen = now };" "a paged server menu keeps one zoom across Prev / Next"
need src/ClassicUO.Client/Touch/TouchInput.cs "return PressLogical(m, live, pos);" "a press is mapped by its window's zoom and spot at touch-down, onto the menu that replaced it if rebuilt"
need src/ClassicUO.Client/Touch/TouchInput.cs "UIManager.RefreshMouseOver();" "...and the scene is asked about what is under the pointer now (a rebuilt menu's press never falls to the world)"
need src/ClassicUO.Client/Touch/LargeUi.cs "t is HtmlControl html && !html.HasLinks" "an HTML link beside a button is not its label (a tap opens the link)"
need src/ClassicUO.Client/Touch/LargeUi.cs "UIManager.IsDragging || TouchInput.PinchingWindow" "zoomed windows are not pulled back on screen mid-pinch"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "FlagAlerts = 1, FlagSend = 2, FlagRescue = 4, FlagDungeon = 8;" "rescue markers flag 4 (servuo RescueMissions.FlagRescueMarkers), dungeon doors flag 8 (DungeonMarkers.FlagDungeonMarkers)"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "FlagKinds = FlagRescue | FlagDungeon;" "...both said together"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "Send(world, FlagSend | FlagKinds);" "...with every marker request"
need src/ClassicUO.Client/Touch/ZombieMarkers.cs "kind <= SurvivalMarkerKind.Dungeon" "marker kinds 5 (rescue) and 6 (dungeon door) are read, not skipped"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "case SurvivalMarkerKind.Rescue:" "...a rescue drawn as a red SOS on the phone's map"

# Build 57 (QA-011, the zombie server's dungeons and undead players, build 56's audit notes)
need src/ClassicUO.Client/Touch/TouchHudGump.cs "_layoutName.IsVisible = !TouchInput.OverWindow(" "the HUD's layout name steps aside for a window (it was drawn over menus' X, QA-011)"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "case SurvivalMarkerKind.Dungeon:" "a dungeon's door is drawn as a doorway with its name"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "legend.Insert(0, (DoorStone, Label(\"Dungeon\"" "...and named in the legend"
need src/ClassicUO.Client/Touch/SurvivalMapGump.cs "_selected = FindSelected();" "a refresh keeps the map's selection by kind and spot (an escort's name counts down each minute)"
need src/ClassicUO.Client/Touch/GumpScale.cs "scale < MIN && LargeUi.IsServerMenu(g)" "a server menu pinched below 0.6 isn't kept (sideways it stayed tiny)"
need src/ClassicUO.Client/Touch/GumpScale.cs "key.StartsWith(\"server:\", StringComparison.Ordinal) && float.IsFinite(v) && v < MIN" "...and such zooms build 56 kept are dropped on load"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "if (OnZombieShard && !set.UndeadOffered)" "Zombie Survival characters get the Undead layout once"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "set.Active = Math.Max(0, set.Layouts.IndexOf(shown));" "...without switching to it (the layout shown stays)"
need src/ClassicUO.Client/Touch/ActionLayouts.cs 'B("Horde\\nFollow", "say:\[horde follow"' "the Undead layout has the horde's orders"
need src/ClassicUO.Client/Touch/ButtonEditGump.cs '("Leap", "say:\[leap"), ("Climb", "say:\[climb")' "the undead presets ([leap, [climb, ... [undead)"

# Build 58 (owner: menus illegible zoomed - the UI drawn at the screen's pixels, text at its final size, the clean UO font)
need src/ClassicUO.Client/Touch/PhoneText.cs "public static bool UiAtPixels => TouchInput.Enabled" "the pixel-scale UI is touch builds only (desktop unchanged)"
need src/ClassicUO.Client/GameController.cs "uiAtPixels ? Matrix.CreateScale(DpiScale, DpiScale, 1f)" "the phone's UI is drawn at the screen's pixels (not stretched ~2.5x with linear filtering)"
need src/ClassicUO.Client/GameController.cs "_uoSpriteBatch.BaseTransform = Matrix.Identity;" "...and that scale is cleared for the world and after the UI"
need src/ClassicUO.Renderer/RenderTargets.cs "uiAtDevicePixels ? gameWindowOnScreen" "...into a backbuffer-sized UI target, copied 1:1"
need src/ClassicUO.Renderer/Batcher2D.cs "_transformMatrix = transform_matrix \* BaseTransform;" "...applied after every batch's own transform (zoomed windows, scissors)"
need src/ClassicUO.Renderer/Batcher2D.cs "SetSampler(SamplerState.LinearClamp);" "SpriteFont text (the map's labels) stays smooth at that scale"
need src/ClassicUO.Client/Game/GameObjects/RenderedText.cs "atlas.TryGetScaledEntry(" "text is drawn from glyphs rasterised at their screen scale, 1:1 on whole pixels"
need src/ClassicUO.Renderer/FontGlyphAtlas.cs "_frameSpent \* 1000 > FRAME_BUDGET_MS" "...made within a per-frame budget (a menu at a new zoom never stalls a frame)"
need src/ClassicUO.Renderer/FontGlyphAtlas.cs "if (_scaledResetPending)" "...and the scaled cache is dropped only at a frame's start (never while batched)"
need src/ClassicUO.Client/Touch/PhoneText.cs "!_noCrisp && !TouchInput.PinchingWindow" "...not during a window pinch (a new scale every frame)"
need src/ClassicUO.Client/Client.cs "Touch.PhoneText.ApplyFonts(FileManager.Fonts);" "UO's blackletter Unicode font 0 is shown in the clean font 3 on phones"
need src/ClassicUO.Assets/FontsLoader.cs "|| _unicodeFontAddress\[replacement\] == null" "...only if both fonts exist (otherwise unchanged)"
need src/ClassicUO.Client/Touch/PhoneText.cs "CLEAN_TIGHTEN = 2;" "...letter spacing tightened so menus laid out for font 0 still fit"
need src/ClassicUO.Client/Game/Managers/JournalManager.cs "else if (!entry.IsUnicode && Touch.PhoneText.Fonts)" "journal lines sent in ASCII fonts are shown in the clean font"

echo "== Boats"
# Build 49: a boat's helm (servuo BoatHelm.cs). At the helm (0x3E96 on Layer.Mount) the stick steers through
# 0xBF 0x33; a held course is resent flagged 0x80 on the copy of the direction, and the server's watchdog stops
# a flagged boat whose phone goes quiet. Keep the flag in step with BoatHelm.ResendFlag.
need src/ClassicUO.Client/Touch/TouchInput.cs "world.Player.IsDrivingBoat" "at a boat's helm the stick steers the boat"
need src/ClassicUO.Client/Touch/TouchInput.cs "world.BoatMovingManager.MoveRequest(dir, speed, true);" "a held course is resent, flagged for the server's watchdog"
need src/ClassicUO.Client/Touch/TouchInput.cs "world.BoatMovingManager.MoveRequest(_boatDir, 0, true);" "letting go of the stick stops the boat"
need src/ClassicUO.Client/Network/OutgoingPackets.cs "(resends ? (byte) dir | 0x80 : (byte) dir)" "resend flag 0x80 (servuo BoatHelm.ResendFlag)"
need src/ClassicUO.Client/Game/GameObjects/PlayerMobile.cs "if (IsDrivingBoat)" "the pilot never walks (the server refuses the steps)"
need src/ClassicUO.Client/Network/PacketHandlers.cs "if (CarryPlayer(world, boatDX, boatDY, playerWasAt, cx, cy, (sbyte)cz))" "a boat's step carries a walking player without resetting the walker (no resync per tile)"
need src/ClassicUO.Client/Network/PacketHandlers.cs "if (x != wasAt.X + dx || y != wasAt.Y + dy)" "a player already out of step with the server still resyncs"
need src/ClassicUO.Client/Game/Scenes/GameSceneInputHandler.cs "Touch.MobileActions.TillerManAtPointer(_world) is Item tiller)" "a double-tap on the tiller man reaches him through the boat's rail"

echo "== Game-file downloads"
need src/ClassicUO.Client/Main.cs "if (!Touch.GameFiles.Configured &&" "UO folder check waits for the download screen (build 34)"
need src/ClassicUO.Client/Main.cs '"(hidden)"' "passwords redacted from the argument trace"
need src/ClassicUO.Client/GameController.cs "FinishLoadContent();" "UO.Load deferred until the files are in place"
grep -E 'DEFAULT_FILES = "https://' ios/Program.cs >/dev/null && ok "file server URL is HTTPS (iOS blocks plain HTTP)" || bad "DEFAULT_FILES must be https://"
need ios/Program.cs '"-ignore_relay_ip"' "reconnect to the login host (cloud NAT)"
need src/ClassicUO.Client/Game/Scenes/LoginScene.cs "port != 0 ? port : Settings.GlobalSettings.Port" "...but on the relay's port: the shard picked from the list (Zombie Survival: 2594)"
need src/ClassicUO.Client/Game/Scenes/LoginScene.cs "CanAutologin && (Servers.Length <= 1 || Reconnect)" "with two shards listed the player picks one at login"
need src/ClassicUO.Client/Configuration/ProfileManager.cs '("UO Mobile", "Graveyard Battles")' "settings follow the PvP shard's rename (profiles live under the server name)"
grep -E 'DEFAULT_IP = "[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+"' ios/Program.cs >/dev/null && bad "DEFAULT_IP is an IPv4 literal (fails on IPv6-only/NAT64 networks, e.g. Apple review)" || ok "default server is a host name (works on IPv6-only networks)"
unguarded="$(awk '/private bool HandleSdlEvent/,/^        }$/' src/ClassicUO.Client/GameController.cs | grep -c 'when _download != null')"
[ "$unguarded" -ge 10 ] && ok "input cases guarded while the download screen runs ($unguarded)" || bad "download screen: input cases not guarded ($unguarded < 10)"

echo "== SDL3 names"
# Every hint set by string literal must be a real SDL3 hint (SDL2 names are silently ignored,
# e.g. SDL_IOS_ORIENTATIONS -> SDL_ORIENTATIONS).
SDLCS=external/FNA/lib/SDL3-CS/SDL3/SDL3.Core.cs
unknown=""
for h in $(grep -rhoE 'SDL_SetHint\("[A-Z0-9_]+"' ios src --include=*.cs | sed -E 's/.*\("//; s/"$//' | sort -u); do
  grep -q "= \"$h\";" "$SDLCS" || unknown="$unknown $h"
done
[ -z "$unknown" ] && ok "all SDL_SetHint names exist in SDL3" || bad "unknown SDL3 hint names:$unknown"

echo "== Scripts"
none "no 'nm | grep -q' under pipefail (SIGPIPE makes it fail)" '^[^#]*nm [^|]*\| *grep -q' ios/build-ios.sh .github/workflows
# the committed copy is what the macOS runner executes (a Windows working tree may show CRLF)
if command -v git >/dev/null 2>&1 && git rev-parse >/dev/null 2>&1; then
  crs="$(git ls-files --eol ios/*.sh | grep -v 'i/lf' || true)"
  [ -z "$crs" ] && ok "committed shell scripts have LF line endings" || bad "CRLF committed in: $crs"
fi

echo "== UO file names (iOS APFS is case-sensitive; go through GetUOFilePath)"
names="$(grep -rnE '(File\.Exists|Path\.Combine)\([^;]*"[A-Za-z0-9_]+\.(mul|uop|idx|def|enu|rle)"' src ios \
          --include=*.cs --exclude=Main.cs --exclude=Program.cs \
         | grep -vE '^[^:]+:[0-9]+:[[:space:]]*//' | grep -v 'GetUOFilePath(' || true)"
[ -z "$names" ] && ok "hard-coded UO data file names only in the known case-exact checks" \
  || { bad "UO file name used without GetUOFilePath (case-sensitive on iOS)"; printf '%s\n' "$names" | sed 's/^/         /'; }

echo "== CI workflow"
W=.github/workflows/ios.yml
need $W 'pwd -P' "real Xcode directory, not the _x.0 symlink"
need $W 'ValidateXcodeVersion=false' "Xcode version check relaxed"
if command -v python3 >/dev/null 2>&1 && python3 -c "import yaml" 2>/dev/null; then
  python3 -c "import yaml,sys; yaml.safe_load(open('$W'))" && ok "workflow YAML parses" || bad "workflow YAML invalid"
elif command -v python >/dev/null 2>&1 && python -c "import yaml" 2>/dev/null; then
  python -c "import yaml,sys; yaml.safe_load(open('$W'))" && ok "workflow YAML parses" || bad "workflow YAML invalid"
else
  echo "  skip YAML parse (no PyYAML)"
fi

echo
if [ $fail -ne 0 ]; then echo "PREFLIGHT FAILED"; exit 1; fi
echo "preflight passed"
