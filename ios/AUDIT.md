# iOS pre-ship audit

Run before **every** push that produces a TestFlight build. A phone round trip costs about
35 minutes (CI plus Apple processing) plus the player's time, so anything catchable on Windows
must be caught here.

## Procedure

1. `bash ios/preflight.sh`: fast static checks (the same script runs first in CI). It must pass.
2. **Desktop exercise of every iOS-only path touched since the last shipped build.** An iOS
   branch (`OperatingSystem.IsIOS()`, `#if IOS`, `HasVirtualKeyboard`) that has not run
   anywhere is untested. Where the code allows it, force the branch on in the desktop client
   with an env flag (`UOM_DOTNET_ZLIB=1` is the example) and play through login, entering the
   world, opening a pack and the paperdoll, and walking. Screen fitting and rotation:
   `UOM_PHONE_FIT=1` runs the phone path on desktop, and the dev command `window W H` "rotates"
   (e.g. `window 480 1000`, then `window 1560 720`). With the flag, the desktop resize handler
   stands aside, so the resize reaches the game through `FollowPhoneRotation`, the only path on
   iOS. Drive two-finger gestures with `down/move/up <id> x y` in `dev/cmd.txt`. Desktop can't
   produce FNA's orientation events: anything that depends on them goes in the phone checks.
   Windows and items: `gumps` lists windows with position, size and zoom; `packitems` gives
   each backpack item's on-screen centre (zoom applied) to aim at; `pack` and `near [tiles]`
   show where an item ended up after a drag.
   Game-file downloads: run the desktop client with `-download <url> -uopath <empty folder>`
   (plus `-download_adopt <copy>` for the reuse path, or an unreachable URL for offline). Cover
   the first install, an update (change a file on the server and rerun make-manifest.py),
   adopting a copy, offline with an install, and offline without one.
3. **Phone checks** for the build, listed in the ship message so the player runs them.
   - Build 32: launch while holding the phone in portrait; rotate both ways in game and on the
     login screen; pinch in and out.
   - Build 33:
     - pinch the backpack and the paperdoll separately (the world zoom must not change);
     - pinch a window with a slow second finger (the window must stay where it is);
     - start a pinch on a scroll arrow or a skill row, then drag a mobile off the world (a health
       bar must appear);
     - drag a stack out of a backpack zoomed to 0.6 (the amount window must open under the finger);
     - tap Names, then pinch the world (name plates must not zoom);
     - read backpack and journal text at 0.6;
     - music plays after copying the music folder in as a whole folder.
   - Build 34:
     - first launch reuses the files already on the phone and downloads only the music (about
       117 MB);
     - the download survives leaving the app and coming back;
     - login goes to the cloud server (34.174.14.240) with no Local Network prompt;
     - music has no static (48 kHz re-encode) and region tracks change as you walk;
     - `ip=192.168.68.91` in uomobile.txt still reaches the home server.
   - Build 35:
     - login reaches the cloud shard by host name on Wi-Fi and on cellular, with no Local
       Network prompt;
     - the console log shows `Connecting to tcp://34-174-14-240.sslip.io:2593/`;
     - `ip=192.168.68.91` still reaches the home server.
     The IPv6-only (NAT64) path itself can't be run from Windows (it needs macOS Internet
     Sharing's NAT64). It was accepted on the code reading (.NET resolves host names with
     getaddrinfo, so DNS64 applies) and on the host name's DNS records.
4. **Agent audit**: an agent reads this file plus `git diff tf-<last>..HEAD` and reviews the
   change against every category below. It also re-checks the whole startup, login and world
   path for any category the diff touches. Its findings are fixed or consciously accepted before
   shipping.
5. After shipping, `git tag tf-<build> <sha>`. When the phone turns up a new failure, add it to
   **Known failures** and, if a grep can catch it, to `preflight.sh`.

## Categories to check

- **APIs that throw PlatformNotSupportedException on iOS/Mono**: Console members other than
  Write/WriteLine/Out; Process; named sync objects; Pipes; Registry; Ping and ICMP; Reflection.Emit;
  Assembly.Load*; runtime loading of files. Guard them, or give them an iOS path.
- **Paths**: every write must land under Documents (`CUOEnviroment.ExecutablePath` is the working
  directory, and SDL's UIKit startup changes it to the read-only bundle). Nothing may assume files
  shipped next to the executable unless they are embedded.
- **Native code**: every `DllImport`/`LibraryImport` target must be statically linked (SDL3,
  FNA3D, FAudio, libtheorafile) or guarded. There is exactly one DllImport resolver per assembly
  (FNA's own on FNA.dll). All linked frameworks are listed in `MtouchExtraArgs`.
- **iOS-only substitutes**: an alternative implementation chosen on iOS (zlib, compressors, file
  access) must be run on desktop via an env flag. A code path that is "never used on desktop"
  is untested.
- **Substitute contract**: an iOS replacement must match the original on empty input, short
  output and error codes, not just the happy path (DotNetZLib versus native `uncompress`).
- **Launch watchdog**: everything before the first frame (`UO.Load` runs in LoadContent on the
  main thread) must stay well under about 20 s, or iOS kills the app with 0x8badf00d and no
  crash.txt is written. Check "Files loaded in N ms" in the console log.
- **SDL3 hints and properties**: use SDL3 names (`SDL_ORIENTATIONS`, not the SDL2 `SDL_IOS_*`).
  Check the value types.
- **Screen and UI**: the backbuffer must follow the screen (scenes resize it on desktop, and on
  rotation FNA flips the device but not `PreferredBackBuffer*`, so it is compared every frame).
  The UI scale must give 480 units on the short side in game, and contain 640x480 on the login
  screens and while a window wider than 480 is open in portrait. HUD positions must stay inside
  `SDL_GetWindowSafeArea` in both orientations, and windows must be pulled back on screen after
  a rotation. Info.plist and the `SDL_ORIENTATIONS` hint must list the same orientations.
- **Saved data from older builds**: a new field in a saved file (layouts, macros, profile)
  needs a migration for files written before it existed. Missing is not the same as default:
  build-31 layouts have no portrait positions.
- **FNA on iOS** (`supportsOrientations`): FNA shapes the backbuffer from its last orientation
  *event*, not from the window. `PreparingDeviceSettings` forces the window's shape, and
  `FollowPhoneRotation` also compares the device's `PresentationParameters`. FNA overrides the
  `SDL_ORIENTATIONS` hint, so Info.plist is the only orientation gate. No resize event reaches the
  game (the window isn't resizable), so iOS size changes are polled.
- **Input**: text input has the correct keyboard type, capitalisation and autocorrect; the
  keyboard is raised on demand; nothing is hover-only or right-click-only on a required path.
  Multi-touch: each finger has exactly one owner until it lifts. A gesture that takes over a
  finger (pinch) must cancel that finger's click without dropping another finger's queued
  release, since a lost mouse-up leaves the button held. A press is only taken over *before the
  game has seen it*. Presses on windows are held back for up to 130 ms, or until the finger moves
  or lifts, so a second finger can claim them for a window pinch. Once a window press has reached
  the game, never cancel it: its control may already be pressed, scrolling, dragging or
  resizing, and forgetting the press leaves it stuck (scroll arrows keep scrolling, a skill row
  blocks health-bar pulls). A world press may still be taken over after dispatch, by releasing it
  off-screen. A finger landing mid-gesture must not click.
  Accepted: the first finger's press reaches the game after 2 frames, so a pinch that starts
  within the double-click time of an earlier tap can still double-click.
- **Game-file downloads** (Touch/GameFiles, DownloadScreen):
  - Nothing may read the UO files before the download screen is done. That includes startup
    checks: Main's "UO directory invalid" check killed a fresh install before it could download
    (desktop test, build 34).
  - A partial set never reaches UO.Load: installed.json is only marked complete at the end.
  - Every file is checked against the manifest's sha256; resume uses HTTP Range.
  - Manifest paths are confined to the UO folder (no `..`, nothing absolute).
  - The manifest is fetched with no-cache (Caddy also sends it), so updates are seen.
  - The file server must be HTTPS (iOS blocks plain HTTP).
  - Downloads go to Library/Application Support, excluded from iCloud backup. A manual copy
    in Documents is adopted (moved) when its hash matches.
  - While the screen runs there is no scene and no world. Every SDL input case that reaches
    Scene or World must be guarded.
  - Logs never contain passwords (the ARG trace redacts them; the phone's log is in Documents).
- **Per-window zoom** (Touch/GumpScale): a zoomed window is drawn in its own pass. That pass
  must stay in true bottom-to-top order, because translucent pixels above it (the HUD) otherwise
  block it through the depth buffer. Every pointer event is mapped into the zoomed window's own
  layout, and hit testing must go through the same mapping (`HitTestGumps`), so the game and
  the finger agree on what's under it. While an item is carried, the window under the finger is
  re-checked on every event, so a drop lands under the finger. Anything new that reads
  `Mouse.Position` to *draw* at the finger (held item, tooltip, target cursor) must use
  `TouchInput.CursorPosition`.
- **Lifecycle**: no GPU work in the background; everything saves on DID_ENTER_BACKGROUND; saves
  are write-then-rename; a corrupt file must not crash every launch.
- **Network**: connects are bounded (no multi-second block on the main thread); the Local
  Network permission is requested before the first real connect. Server addresses are host
  names. On IPv6-only NAT64 networks (Apple's review network, some carriers without 464XLAT)
  only a DNS lookup yields a synthesized IPv6 address; a raw IPv4 literal fails.
- **Threads**: SDL, UIKit, FNA and gump work happen only on the main thread (FakeMain's thread).
- **Memory**: no large one-shot allocations (the world map builds a ~120 MB image); UO files
  are memory-mapped.
- **Packaging**: Info.plist purpose strings cover every API SDL references (camera,
  microphone, Bluetooth); the icon is 1024 px with no alpha; the build number increases.
- **Build and CI**: use the real Xcode directory (not the `_x.0` symlink); keep the fnalibs
  cache key in sync with the lib patches; the symbol check runs; no `nm | grep -q` under
  `pipefail`.
- **Editing hazards**: a Python or heredoc edit containing `\n` can write a literal newline into
  a C# string or YAML. Build and YAML-validate after scripted edits.

## Known failures (newest last)

| Build | Symptom | Cause | Fix |
|---|---|---|---|
| 14-16 | abort at launch | our resolver + FNA's: `CannotRegisterSecondResolver` | never register on the FNA assembly |
| 17 | `EntryPointNotFoundException: SDL_SetHint` | `<NativeReference>` items were ignored, so no SDL in the binary | `MtouchExtraArgs --gcc_flags -force_load`, plus the CI symbol check |
| CI | `xcrun -find clang++` exit 16384 | `Xcode_26.x.0.app` is a symlink | `pwd -P`, `ValidateXcodeVersion=false` |
| 18 | undefined CoreMedia/CoreGraphics symbols | frameworks missing | the full SDL3 framework list |
| 19 | `ld: pointer not aligned` in F3DAudio | packed structs | `aligned(8)` patch in build-ios.sh |
| 21 | App Store ITMS-90683 | no camera/Bluetooth purpose strings | added to Info.plist |
| 22 | `CannotRegisterSecondResolver` in `DllMap.Init` | ClassicUO's desktop DllMap | skipped on iOS |
| 25 | `PlatformNotSupportedException` | `Console.ForegroundColor` in Logger | guarded |
| 28 | `UnauthorizedAccessException` on settings.json | SDL chdir to the bundle | chdir back in RealMain |
| 29 | "UO directory invalid" | files in `Documents/ios-data/uo` | recursive `FindUoData` |
| 29 | `Exception: CRC mismatch` | the iOS-only switch to ClassicUO's `ZLibManaged` (broken, never used on 64-bit) | `System.IO.Compression.ZLibStream`, verified on desktop with `UOM_DOTNET_ZLIB=1` |
| audit | (pre-ship, build 31) | DotNetZLib threw on empty input; FindUoData could pick up `.Trash`; the log listed the wrong folder; preflight `none()` passed when grep errored | contract guards; skip dot-folders; log the chosen folder; exit code 2 now fails the check |
| 31 | no music | the Apple Devices folder copy wrote 0 KB mp3s | build 34: the app downloads the music (48 kHz) with the game files |
| audit | (pre-ship, build 32) | build-31 `mobile_layouts.json` has no portrait positions, so derivation put Attack Last on the joystick; editors are 520/640 wide against a 480-wide portrait UI | fill from the default layout by action; widen the fit while a wide window is open |
| audit | (agent, build 32) | a launch held in portrait likely got a landscape backbuffer (FNA orientation shaping); a pinch started on a window threw it off-screen; fingers landing mid-pinch clicked; macro-bar buttons had no portrait position; a null layout crashed every login; the desktop rotation test used a path iOS never takes | `PreparingDeviceSettings` forces the window's shape; pinch only from world presses; re-pinch and ignore extra fingers; PX/PY on every new button (preflight); drop nulls on load; the desktop resize handler stands aside under `UOM_PHONE_FIT` |
| audit | (agent, build 33) | a resting finger's tiny motion turned a window pinch into a window drag; cancelling a dispatched window press left controls stuck; windows dragged out of a zoomed window opened away from the finger; name plates could be zoomed; the aura drew away from the finger | hold window presses until move, lift or 130 ms, and pinch only while held; a drag of another window drops the mapping; world-anchored windows and health bars are not scalable; the aura and range text draw at `CursorPosition` |
| desktop | (pre-ship, build 34) | a fresh download install showed "Your UO directory is invalid" and quit | Main's folder check waits for the download screen when a file server is configured |
| audit | (agent, build 34) | iOS's default HTTP handler caches small files (an update could get a stale copy); UO.Load could run while backgrounded; a leading "/" in a manifest path escaped the folder; a wrong manifest could delete a good install; each app switch used up a retry; quitting during the download crashed in Unload; the console log could hold a password | the managed SocketsHttpHandler everywhere and no-cache on the server; load only in the foreground; full-path confinement; manifest sanity check, deletions after success, re-check by hash after a load failure; progress resets the retry count; `FileManager?.Dispose()`; redacted args |
