# UO Mobile: building ClassicUO for iPhone (Mac session)

Everything that could be prepared on Windows is in `ios/`. On the Mac the job is: install
tools, restore the repo, run `build-ios.sh`, copy the UO data, play.

Steps marked **UNVERIFIED** could not be tested without a Mac.

## How it works

The approach follows the FNA docs ([Appendix C: FNA on Apple Platforms](https://fna-xna.github.io/docs/appendix/Appendix-C:-FNA-on-Apple-Platforms/)):

- `ios/ClassicUO.iOS.csproj` is a `net10.0-ios` app with no AppDelegate. It references
  `src/ClassicUO.Client` (cuo.dll) and FNA.
- SDL3, FNA3D (+ mojoshader), FAudio and Theorafile are built as **static libraries** for
  `iphoneos`/arm64 and force-loaded into the app executable.
- `ios/Program.cs` runs `Main`. It changes the current directory to the app's `Documents`
  folder, sets the SDL hints, and calls `SDL_RunApp`. SDL then starts UIKit and calls back into
  ClassicUO's normal `Bootstrap.Main(args)`. The plugin host (`src/ClassicUO.Bootstrap`) and
  Razor are not used.
- Runtime: **Mono AOT** by default, which is what the FNA docs use. `NATIVEAOT=1` switches to
  NativeAOT through `dotnet publish` (UNVERIFIED).
- The ClassicUO arguments are
  `-touch -ip <ip> -port 2593 -clientversion 7.0.116.0 -uopath <Documents>/uo -language ENU -plugins`.
  `-skiploginscreen` is left out on purpose, so the login screen shows and you type the
  account there. ClassicUO treats `-skiploginscreen` as a switch: `-skiploginscreen off`
  would actually skip the login screen.
- On first launch the app creates `Documents/uomobile.txt`. Edit it to change the server
  without rebuilding (`ip=`, `port=`, `clientversion=`, `args=`). You can also write it with
  `./build-ios.sh config`.

## 0. Before leaving the Windows PC

1. **Stage the UO data.** This is already done: `C:\Users\18166\dev\uo-mobile\ios-data\uo`
   has 80 files, about 2.03 GB. To redo it:
   `powershell -ExecutionPolicy Bypass -File ios\copy-uo-data.ps1`
   (add `-IncludeMusic` for +138 MB of music, `-AllMaps` for the other facets).
2. **Package the repo:**
   `powershell -ExecutionPolicy Bypass -File ios\make-bundle.ps1 -IncludeWorkingTree`
   This writes `C:\Users\18166\dev\uo-mobile\transfer\` containing:
   - `classicuo-mobile.bundle`
   - `submodules\*.bundle`
   - `restore-on-mac.sh`
   - `classicuo-src.tar.gz`

   The Windows checkout is a *shallow* clone of upstream ClassicUO. The bundle therefore
   holds only our commits, and the restore script fetches the base commit from GitHub. The
   tarball works fully offline and also includes uncommitted work such as `ios/`, if the lead
   has not committed it yet.
3. **Copy both folders** (`transfer\`, and `ios-data\uo\` if the data will go through the Mac)
   to a USB stick formatted exFAT, or to a shared folder. The largest file is 253 MB.
4. **Open the firewall for ServUO.** Run this yourself in an elevated PowerShell (Run as
   Administrator):
   ```powershell
   New-NetFirewallRule -DisplayName "ServUO 2593 (UO Mobile)" -Direction Inbound -Action Allow `
     -Protocol TCP -LocalPort 2593 -Profile Private,Domain
   Get-NetConnectionProfile   # the Wi-Fi/Ethernet profile must be Private; if it says Public:
   # Set-NetConnectionProfile -InterfaceAlias "Wi-Fi" -NetworkCategory Private
   ```
   You can use a program rule instead:
   `-Program "C:\Users\18166\dev\uo-mobile\servuo\ServUO.exe"` in place of `-Protocol/-LocalPort`.
5. ServUO must be running (`run-server.sh`) and listening on `0.0.0.0:2593`. `Config/Server.cfg`
   has `@Address=127.0.0.1`. That is fine: `Scripts/Misc/ServerList.cs` sends LAN clients the
   address they connected to (192.168.68.91), and uses `@Address` only for public clients.
6. Check that the PC's IP is still `192.168.68.91` (`ipconfig`). It is the app's built-in
   default.

## 1. Install the tools on the Mac (about 1 hour, mostly downloads)

1. **Xcode** from the App Store. Open it once, accept the license, and install the **iOS**
   platform (Xcode > Settings > Components). Then:
   `sudo xcode-select -s /Applications/Xcode.app/Contents/Developer`
2. **Sign in:** Xcode > Settings > Accounts > add your Apple ID. A free ID gives a
   "Personal Team".
3. **.NET 10 SDK** (macOS Arm64 or x64 installer):
   https://dotnet.microsoft.com/download/dotnet/10.0
4. `sudo dotnet workload install ios`
   **UNVERIFIED:** each iOS workload release requires a specific Xcode version. If the build
   says Xcode is too old or too new, install the matching Xcode from
   https://developer.apple.com/download/all/, or as a last resort pass
   `-p:ValidateXcodeVersion=false`.
5. **Homebrew** (https://brew.sh), then `brew install cmake ninja`.

## 2. Restore the repo

```bash
cd /Volumes/<usb>/transfer          # or wherever you copied it
bash restore-on-mac.sh ~/uo-mobile/ClassicUO
# only if ios/ (or other work) was not committed before bundling:
tar -xzf classicuo-src.tar.gz -C ~/uo-mobile/ClassicUO
# Offline alternative with no git at all:
#   mkdir -p ~/uo-mobile/ClassicUO && tar -xzf classicuo-src.tar.gz -C ~/uo-mobile/ClassicUO
```

## 3. Prepare the iPhone and signing

1. Connect the iPhone by USB, unlock it, and tap **Trust**.
2. Turn on Developer Mode: Settings > Privacy & Security > **Developer Mode** > on, then
   restart. The switch only appears after Xcode has seen the phone once.
3. **Free Apple ID only:** create the provisioning profile with a throwaway Xcode project.
   1. Xcode > File > New > Project > iOS App.
   2. Set the Bundle Identifier to what you will use below, for example
      `com.<yourname>.uomobile`. Free accounts need a globally unique id;
      `com.example.uomobile` will be rejected.
   3. Under Signing & Capabilities, set Team = *Personal Team*.
   4. Select the iPhone and press Run once. This creates the development certificate and a
      7-day profile. Quit Xcode.

   With a paid team, automatic signing usually works directly.

   **UNVERIFIED:** whether `dotnet` finds profiles in Xcode 16+'s new location
   (`~/Library/Developer/Xcode/UserData/Provisioning Profiles`). If the build says no
   profile was found, copy them:
   `mkdir -p ~/Library/MobileDevice/Provisioning\ Profiles && cp ~/Library/Developer/Xcode/UserData/Provisioning\ Profiles/* ~/Library/MobileDevice/Provisioning\ Profiles/`

## 4. Build and install

```bash
cd ~/uo-mobile/ClassicUO/ios
export BUNDLE_ID=com.<yourname>.uomobile     # same id as the Xcode dummy project
bash build-ios.sh check     # tools present?
bash build-ios.sh libs      # SDL3 + FNA3D + FAudio + Theorafile -> ios/native/iphoneos/*.a (~5-10 min)
bash build-ios.sh build     # dotnet publish -> ios/bin/Release/net10.0-ios/ios-arm64/UOMobile.app (+ .ipa)
bash build-ios.sh install   # xcrun devicectl device install app ...
# or all of the above except data: bash build-ios.sh all
```

On the phone, trust the certificate: Settings > General > VPN & Device Management >
*Apple Development: you* > Trust. This is needed once per certificate.

If signing picks the wrong certificate or profile, pass them explicitly:
`CODESIGN_KEY="Apple Development: you@x.com (ABCDE12345)" PROVISION="<profile name>" bash build-ios.sh build`.
`security find-identity -v -p codesigning` lists the certificates.

Free-account apps expire after 7 days. Re-run `build` and `install` to renew; the data in
Documents is kept as long as the app is not deleted.

## 5. Copy the UO data (2.03 GB) into the app

Launch the app once first, so that `Documents/uo` exists. Then use one of these:

- **From the Mac, scripted:** `UO_DATA=/path/to/ios-data/uo bash build-ios.sh data`
  (uses `xcrun devicectl device copy to`, one file at a time; **UNVERIFIED**).
- **Finder:** click the iPhone in the sidebar > **Files** tab > drag the *contents* of `uo` onto
  "UO Mobile", or into its `uo` folder.
- **Directly from Windows, skipping the Mac:** the Apple Devices app (or iTunes) > iPhone >
  Files > UO Mobile > add the files.
- **Over Wi-Fi with the Files app:** share `ios-data` on Windows, then in the Files app use
  "..." > Connect to Server > `smb://192.168.68.91`, and copy `uo` into
  On My iPhone > UO Mobile.

The files must end up in `On My iPhone/UO Mobile/uo/` with `tiledata.mul` directly inside
`uo/`, not in a nested `uo/uo/`.

## 6. Run

`bash build-ios.sh launch` streams the app's console while the phone is attached. You can
also tap the icon.

1. The first time the client connects, iOS asks for **Local Network** access. Tap Allow. If
   the first login attempt fails, try again after allowing.
2. Enter the account name and password on the login screen. The on-screen keyboard is used.
3. To change the server, edit `Documents/uomobile.txt` (Files app), or run
   `SERVER_IP=... bash build-ios.sh config`.

Logs for when something goes wrong:
- `On My iPhone/UO Mobile/Logs/uomobile-console.log`: the full ClassicUO console.
- `On My iPhone/UO Mobile/Logs/uomobile-fatal.txt`: startup exceptions.
- `On My iPhone/UO Mobile/Logs/crash.txt`: ClassicUO's own crash log.
- The Mac's Console.app (select the iPhone, filter "UOMobile"), or `build-ios.sh launch`.

Other checks:
- From the Mac on the same Wi-Fi, check the server is reachable: `nc -vz 192.168.68.91 2593`.

## 7. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Link errors: undefined `_SDL_*`, `_FNA3D_*`, C++ symbols | Rebuild with `-p:FnaLinkMode=mtouch` to use the FNA docs' exact `MtouchExtraArgs` line: `dotnet publish ios/ClassicUO.iOS.csproj -c Release -f net10.0-ios -r ios-arm64 -p:ApplicationId=$BUNDLE_ID -p:FnaLinkMode=mtouch`. Check `ios/native/iphoneos/*.a` with `lipo -info` (must say arm64). |
| Link errors naming a framework symbol (e.g. `_OBJC_CLASS_$_GCController`) | Add that framework to the `Frameworks` metadata of `libSDL3.a` in `ClassicUO.iOS.csproj`. |
| `DllNotFoundException: SDL2` / `SDL3` at start | SDL3 did not resolve, so FNA fell back to SDL2. `Program.Resolve` maps "SDL3" to the main executable only when `SDL_Init` is exported. Symbols were stripped: keep `MtouchNoSymbolStrip` and `_ExportSymbolsExplicitly=false` (FNA docs). |
| `ExecutionEngineException: Attempting to JIT compile` | Mono AOT missed a generic instantiation. `MtouchInterpreter=-all` should cover it. If not, try `-p:MtouchInterpreter=all` (slower) or `NATIVEAOT=1`. |
| "Your UO directory is invalid" | Data is not in `Documents/uo`, or `tiledata.mul`/`MainMisc.uop` is missing. |
| Stuck after choosing the shard | Firewall, or the phone is not on the same LAN; see step 0.4. |
| App letterboxed / wrong orientation | `Info.plist` `UILaunchScreen` / orientation keys; the `SDL_IOS_ORIENTATIONS` hint in `Program.cs`. |
| Keyboard always on screen | `GameController.cs:111` calls `TextInputEXT.StartTextInput()` at startup, so iOS shows the keyboard immediately. Dismissing it stops text input. The touch layer should start/stop text input when a text box gains/loses focus (follow-up work). |
| Build error: `NETSDK1207 Ahead-of-time compilation is not supported` | Do not pass `-p:PublishAot=true` globally (it reaches netstandard/net40 projects). Use `NATIVEAOT=1`, which sets `UseNativeAot`. |

## Files in `ios/`

| File | Purpose |
|---|---|
| `ClassicUO.iOS.csproj` | net10.0-ios app; references cuo + FNA; static fnalibs via `NativeReference` (or `-p:FnaLinkMode=mtouch`); drops cuo's desktop host files from the bundle |
| `Program.cs` | iOS entry: Documents as working dir, `uomobile.txt`, native-lib resolver, SDL hints, `SDL_RunApp` -> `ClassicUO.Bootstrap.Main` |
| `Info.plist` | "UO Mobile", landscape only, full screen, no status bar, Files/Finder sharing, Local Network usage text |
| `build-ios.sh` | check / libs / build / install / data / config / launch / all |
| `copy-uo-data.ps1` | stages the minimal UO file set on Windows |
| `make-bundle.ps1` | git bundles (main + submodules), restore script, optional source tarball |
