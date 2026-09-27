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
   world, opening a pack and the paperdoll, and walking.
3. **Agent audit**: an agent reads this file plus `git diff tf-<last>..HEAD` and reviews the
   change against every category below. It also re-checks the whole startup, login and world
   path for any category the diff touches. Its findings are fixed or consciously accepted before
   shipping.
4. After shipping, `git tag tf-<build> <sha>`. When the phone turns up a new failure, add it to
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
- **SDL3 hints and properties**: use SDL3 names (`SDL_ORIENTATIONS`, not the SDL2 `SDL_IOS_*`).
  Check the value types.
- **Screen and UI**: the backbuffer must follow the screen (scenes resize it on desktop); the UI
  scale must fit 480 units of height; HUD positions must stay inside `SDL_GetWindowSafeArea`.
- **Input**: text input has the correct keyboard type, capitalisation and autocorrect; the
  keyboard is raised on demand; nothing is hover-only or right-click-only on a required path.
- **Lifecycle**: no GPU work in the background; everything saves on DID_ENTER_BACKGROUND; saves
  are write-then-rename; a corrupt file must not crash every launch.
- **Network**: connects are bounded (no multi-second block on the main thread); the Local
  Network permission is requested before the first real connect.
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
| 18 | undefined CoreMedia/CoreGraphics symbols | frameworks missing | the full SDL3 framework list |
| 19 | `ld: pointer not aligned` in F3DAudio | packed structs | `aligned(8)` patch in build-ios.sh |
| 21 | App Store ITMS-90683 | no camera/Bluetooth purpose strings | added to Info.plist |
| CI | `xcrun -find clang++` exit 16384 | `Xcode_26.x.0.app` is a symlink | `pwd -P`, `ValidateXcodeVersion=false` |
| 22 | `CannotRegisterSecondResolver` in `DllMap.Init` | ClassicUO's desktop DllMap | skipped on iOS |
| 25 | `PlatformNotSupportedException` | `Console.ForegroundColor` in Logger | guarded |
| 28 | `UnauthorizedAccessException` on settings.json | SDL chdir to the bundle | chdir back in RealMain |
| 29 | "UO directory invalid" | files in `Documents/ios-data/uo` | recursive `FindUoData` |
| 29 | `Exception: CRC mismatch` | the iOS-only switch to ClassicUO's `ZLibManaged` (broken, never used on 64-bit) | `System.IO.Compression.ZLibStream`, verified on desktop with `UOM_DOTNET_ZLIB=1` |
