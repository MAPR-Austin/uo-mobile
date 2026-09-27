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
missing="$(grep -rn "new ActionButtonDef *{" src/ClassicUO.Client/Touch --include=*.cs | grep -v "PX =" || true)"
[ -z "$missing" ] && ok "every new HUD button gets a portrait position (PX/PY)" || { bad "HUD button created without PX/PY (lands off-screen in portrait)"; printf '%s
' "$missing" | sed 's/^/         /'; }
need src/ClassicUO.Client/GameController.cs "UOM_PHONE_FIT" "phone screen-fit path can be forced on desktop (UOM_PHONE_FIT=1)"
need src/ClassicUO.Client/GameController.cs "FollowPhoneRotation();" "backbuffer follows rotation (FNA does not update PreferredBackBuffer*)"
need src/ClassicUO.Client/Touch/ActionLayouts.cs "FillPortraitFromDefaults(set)" "old layouts get portrait positions (build-31 files have none)"
need src/ClassicUO.Client/Game/Managers/UIManager.cs "HitTestGumps(position, TouchInput.PointerGump" "hit testing maps zoomed windows like the pointer does"
need src/ClassicUO.Client/GameController.cs "GumpScale.Save();" "window zoom saved on suspend"

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
