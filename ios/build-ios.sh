#!/usr/bin/env bash
# UO Mobile: build ClassicUO for iPhone on a Mac.
#
#   ./build-ios.sh check      verify Xcode / .NET 10 / ios workload / cmake / ninja
#   ./build-ios.sh libs       build SDL3, FNA3D(+mojoshader), FAudio, Theorafile as iOS static libs
#   ./build-ios.sh build      dotnet publish the app (.app + .ipa)
#   ./build-ios.sh install    install on the USB-connected iPhone (xcrun devicectl)
#   ./build-ios.sh data       copy the UO data folder into the app's Documents/uo on the phone
#   ./build-ios.sh config     write server ip/port to Documents/uomobile.txt on the phone
#   ./build-ios.sh launch     start the app (prints its console while attached)
#   ./build-ios.sh all        check + libs (if missing) + build + install
#
# Environment (all optional):
#   BUNDLE_ID      unique bundle id; REQUIRED to be unique for a free Apple ID (default com.example.uomobile)
#   CODESIGN_KEY   e.g. "Apple Development: you@example.com (XXXXXXXXXX)"   (default: auto)
#   PROVISION      provisioning profile name or UUID                         (default: auto)
#   CONFIG         Release (default) or Debug
#   NATIVEAOT      1 = publish with NativeAOT instead of Mono AOT (UNVERIFIED path)
#   UNSIGNED       1 = build without code signing and package Payload/*.app as an .ipa
#                  (CI: sign/install it from Windows with Sideloadly)
#   DEVICE_ID      devicectl device identifier (default: first connected iPhone)
#   UO_DATA        folder with the staged UO files (default: ~/uo-mobile/ios-data/uo)
#   SERVER_IP      for 'config' (default 192.168.68.91), SERVER_PORT (default 2593)
#   SDL_REF        SDL git branch/tag for 'libs' (default release-3.2.x)

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
CONFIG="${CONFIG:-Release}"
BUNDLE_ID="${BUNDLE_ID:-com.example.uomobile}"
SDL_REF="${SDL_REF:-release-3.2.x}"
NATIVE_DIR="$HERE/native/iphoneos"
BUILD_DIR="$HERE/.build"
IOS_MIN="13.0"
TFM="net10.0-ios"
RID="ios-arm64"
UO_DATA="${UO_DATA:-$HOME/uo-mobile/ios-data/uo}"
SERVER_IP="${SERVER_IP:-192.168.68.91}"
SERVER_PORT="${SERVER_PORT:-2593}"
FNA_LIB="$REPO/external/FNA/lib"

log()  { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33mWARN: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# ------------------------------------------------------------------------------------------
cmd_check() {
  log "Checking prerequisites"
  [ "$(uname)" = "Darwin" ] || die "run this on macOS"

  local dev; dev="$(xcode-select -p 2>/dev/null || true)"
  case "$dev" in
    *Xcode*.app/Contents/Developer) echo "xcode-select: $dev" ;;
    *) die "xcode-select points to '$dev'. Install Xcode, then: sudo xcode-select -s /Applications/Xcode.app/Contents/Developer" ;;
  esac
  xcodebuild -version
  xcodebuild -version -sdk iphoneos Path >/dev/null || die "iOS SDK missing (open Xcode once, install the iOS platform: Xcode > Settings > Components)"

  command -v dotnet >/dev/null || die "dotnet not found. Install the .NET 10 SDK (arm64) from https://dotnet.microsoft.com/download/dotnet/10.0"
  echo "dotnet $(dotnet --version)"
  case "$(dotnet --version)" in 10.*) ;; *) warn "expected a .NET 10 SDK (global.json-free repo; the newest installed SDK is used)";; esac
  dotnet workload list | grep -qE '^\s*ios\b' || die "iOS workload missing: sudo dotnet workload install ios"

  for t in cmake ninja git; do
    command -v "$t" >/dev/null || die "$t not found. Install Homebrew (https://brew.sh) then: brew install cmake ninja"
  done
  echo "cmake $(cmake --version | head -1 | awk '{print $3}'), ninja $(ninja --version)"
  [ -f "$FNA_LIB/FNA3D/CMakeLists.txt" ] || die "external/FNA submodules missing (run restore-on-mac.sh, or git submodule update --init --recursive)"
  [ -f "$FNA_LIB/FNA3D/MojoShader/mojoshader.c" ] || die "MojoShader submodule missing (git submodule update --init --recursive)"
  echo "OK"
}

# ------------------------------------------------------------------------------------------
# Native libs, following the FNA projects' own CI (FNA3D/FAudio .github/workflows/ci.yml,
# Theorafile ci.yml) and fnalibs-dailies/build-fnalibs-apple.sh.
cmd_libs() {
  log "Building fnalibs for iOS (arm64, min iOS $IOS_MIN) into $NATIVE_DIR"
  mkdir -p "$BUILD_DIR" "$NATIVE_DIR"
  local sysroot; sysroot="$(xcodebuild -version -sdk iphoneos Path)"
  local common=(-G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=iOS
                -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET="$IOS_MIN"
                -DCMAKE_OSX_SYSROOT="$sysroot")

  # SDL3 (static)
  if [ ! -d "$BUILD_DIR/SDL/.git" ]; then
    log "Cloning SDL ($SDL_REF)"
    git clone --depth 1 --branch "$SDL_REF" https://github.com/libsdl-org/SDL.git "$BUILD_DIR/SDL"
  fi
  log "SDL3"
  cmake -S "$BUILD_DIR/SDL" -B "$BUILD_DIR/sdl-ios" "${common[@]}" \
        -DSDL_SHARED=OFF -DSDL_STATIC=ON -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
  ninja -C "$BUILD_DIR/sdl-ios"
  local sdl_a; sdl_a="$(find "$BUILD_DIR/sdl-ios" -maxdepth 2 -name 'libSDL3.a' | head -1)"
  [ -n "$sdl_a" ] || die "libSDL3.a not produced"
  # Headers + lib passed explicitly so FNA3D/FAudio do not depend on SDL's CMake package layout.
  local sdl_vars=(-DSDL3_INCLUDE_DIRS="$BUILD_DIR/SDL/include" -DSDL3_LIBRARIES="$sdl_a")

  # FNA3D + mojoshader (static, SPIRV-Cross linked statically as in FNA3D CI)
  log "FNA3D"
  cmake -S "$FNA_LIB/FNA3D" -B "$BUILD_DIR/fna3d-ios" "${common[@]}" "${sdl_vars[@]}" \
        -DBUILD_SHARED_LIBS=OFF -DMOJOSHADER_STATIC_SPIRVCROSS=ON
  ninja -C "$BUILD_DIR/fna3d-ios"

  # FAudio (static)
  log "FAudio"
  cmake -S "$FNA_LIB/FAudio" -B "$BUILD_DIR/faudio-ios" "${common[@]}" "${sdl_vars[@]}" \
        -DBUILD_SHARED_LIBS=OFF
  ninja -C "$BUILD_DIR/faudio-ios"

  # Theorafile (its Xcode project, as in Theorafile CI)
  log "Theorafile"
  xcodebuild -project "$FNA_LIB/Theorafile/Xcode/theorafile.xcodeproj" -target theorafile-iOS \
             -configuration Release -sdk iphoneos SYMROOT="$BUILD_DIR/theorafile" \
             IPHONEOS_DEPLOYMENT_TARGET="$IOS_MIN" -quiet

  cp -f "$sdl_a" "$NATIVE_DIR/libSDL3.a"
  cp -f "$(find "$BUILD_DIR/fna3d-ios" -maxdepth 2 -name 'libFNA3D.a' | head -1)" "$NATIVE_DIR/libFNA3D.a"
  cp -f "$(find "$BUILD_DIR/fna3d-ios" -maxdepth 3 -name 'libmojoshader.a' | head -1)" "$NATIVE_DIR/libmojoshader.a"
  cp -f "$(find "$BUILD_DIR/faudio-ios" -maxdepth 2 -name 'libFAudio.a' | head -1)" "$NATIVE_DIR/libFAudio.a"
  cp -f "$BUILD_DIR/theorafile/Release-iphoneos/libtheorafile.a" "$NATIVE_DIR/libtheorafile.a"
  xattr -c "$NATIVE_DIR"/*.a || true   # FNA docs: strip extended attributes on newer macOS

  log "Result"
  for a in "$NATIVE_DIR"/*.a; do
    printf '%-20s %s\n' "$(basename "$a")" "$(lipo -info "$a" 2>/dev/null | sed 's/.*: //')"
  done
  # sanity: the entry points our DllImports need
  nm -gU "$NATIVE_DIR/libSDL3.a" 2>/dev/null | grep -q ' _SDL_RunApp$' || warn "SDL_RunApp not found in libSDL3.a"
  nm -gU "$NATIVE_DIR/libFNA3D.a" 2>/dev/null | grep -q ' _FNA3D_CreateDevice$' || warn "FNA3D_CreateDevice not found in libFNA3D.a"
}

libs_present() {
  for l in libSDL3.a libFNA3D.a libmojoshader.a libFAudio.a libtheorafile.a; do
    [ -f "$NATIVE_DIR/$l" ] || return 1
  done
}

# ------------------------------------------------------------------------------------------
publish_dir() { echo "$HERE/bin/$CONFIG/$TFM/$RID"; }

cmd_build() {
  libs_present || die "native libs missing; run: $0 libs"
  local runtime="Mono AOT"; [ "${NATIVEAOT:-}" = "1" ] && runtime="NativeAOT"
  log "dotnet publish ($CONFIG, $RID, bundle id $BUNDLE_ID, $runtime)"
  local props=(-p:ApplicationId="$BUNDLE_ID")
  [ "${NATIVEAOT:-}" = "1" ] && props+=(-p:UseNativeAot=true)
  [ -n "${CODESIGN_KEY:-}" ] && props+=(-p:CodesignKey="$CODESIGN_KEY")
  [ -n "${PROVISION:-}" ]    && props+=(-p:CodesignProvision="$PROVISION")
  [ "${UNSIGNED:-}" = "1" ]  && props+=(-p:EnableCodeSigning=false -p:BuildIpa=false)

  dotnet publish "$HERE/ClassicUO.iOS.csproj" -c "$CONFIG" -f "$TFM" -r "$RID" "${props[@]}"

  local app; app="$(find "$(publish_dir)" -maxdepth 1 -name '*.app' -type d | head -1)"
  local ipa; ipa="$(find "$(publish_dir)/publish" -maxdepth 1 -name '*.ipa' 2>/dev/null | head -1 || true)"
  [ -n "$app" ] || die "no .app produced under $(publish_dir)"
  echo "app: $app"
  [ -n "$ipa" ] && echo "ipa: $ipa"
  du -sh "$app" | awk '{print "app size: " $1}'

  if [ "${UNSIGNED:-}" = "1" ]; then
    local out="$HERE/out"; rm -rf "$out"; mkdir -p "$out/Payload"
    cp -R "$app" "$out/Payload/"
    (cd "$out" && zip -qry UOMobile-unsigned.ipa Payload)
    rm -rf "$out/Payload"
    echo "unsigned ipa: $out/UOMobile-unsigned.ipa ($(du -h "$out/UOMobile-unsigned.ipa" | awk '{print $1}'))"
  fi
}

# ------------------------------------------------------------------------------------------
device_id() {
  if [ -n "${DEVICE_ID:-}" ]; then echo "$DEVICE_ID"; return; fi
  local tmp; tmp="$(mktemp)"
  xcrun devicectl list devices --json-output "$tmp" >/dev/null 2>&1 || die "xcrun devicectl failed (needs Xcode 15+)"
  local id
  id="$(/usr/bin/python3 - "$tmp" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for dev in d.get("result", {}).get("devices", []):
    hp = dev.get("hardwareProperties", {})
    cp = dev.get("connectionProperties", {})
    if hp.get("platform") == "iOS" and cp.get("tunnelState") != "unavailable":
        print(dev.get("identifier")); break
PY
)"
  rm -f "$tmp"
  [ -n "$id" ] || die "no connected iPhone found. Plug it in via USB, unlock it, tap 'Trust', enable Developer Mode. (xcrun devicectl list devices)"
  echo "$id"
}

cmd_install() {
  local app; app="$(find "$(publish_dir)" -maxdepth 1 -name '*.app' -type d | head -1)"
  [ -n "$app" ] || die "build first: $0 build"
  local dev; dev="$(device_id)"
  log "Installing $(basename "$app") on $dev"
  xcrun devicectl device install app --device "$dev" "$app"
  echo "Installed. First launch with a free Apple ID: iPhone Settings > General > VPN & Device Management > trust your developer certificate."
}

cmd_data() {
  [ -f "$UO_DATA/tiledata.mul" ] || die "UO data not found at $UO_DATA (copy the staged ios-data/uo folder from the PC there, or set UO_DATA)"
  local dev; dev="$(device_id)"
  log "Copying $(du -sh "$UO_DATA" | awk '{print $1}') of UO data to $BUNDLE_ID:Documents/uo (this takes a while)"
  # One file at a time keeps devicectl happy with large files and shows progress.
  local n=0 total; total="$(find "$UO_DATA" -type f | wc -l | tr -d ' ')"
  while IFS= read -r -d '' f; do
    n=$((n+1))
    rel="${f#$UO_DATA/}"
    printf '[%3d/%3d] %s\n' "$n" "$total" "$rel"
    xcrun devicectl device copy to --device "$dev" --domain-type appDataContainer \
         --domain-identifier "$BUNDLE_ID" --source "$f" --destination "Documents/uo/$rel" >/dev/null
  done < <(find "$UO_DATA" -type f -print0)
  echo "Done. Alternative: Finder > iPhone > Files > UO Mobile > drag the files into 'uo'."
}

cmd_config() {
  local dev; dev="$(device_id)"
  local tmp; tmp="$(mktemp)"
  printf 'ip=%s\nport=%s\nclientversion=7.0.116.0\n' "$SERVER_IP" "$SERVER_PORT" > "$tmp"
  log "Writing Documents/uomobile.txt (ip=$SERVER_IP port=$SERVER_PORT)"
  xcrun devicectl device copy to --device "$dev" --domain-type appDataContainer \
       --domain-identifier "$BUNDLE_ID" --source "$tmp" --destination "Documents/uomobile.txt"
  rm -f "$tmp"
}

cmd_launch() {
  local dev; dev="$(device_id)"
  log "Launching $BUNDLE_ID (Ctrl+C detaches; the app keeps running)"
  xcrun devicectl device process launch --device "$dev" --terminate-existing --console "$BUNDLE_ID" \
    || xcrun devicectl device process launch --device "$dev" --terminate-existing "$BUNDLE_ID"
}

cmd_all() {
  cmd_check
  libs_present || cmd_libs
  cmd_build
  cmd_install
  echo
  echo "Next: ./build-ios.sh data   (once; ~2 GB)   then   ./build-ios.sh launch"
}

case "${1:-all}" in
  check)   cmd_check ;;
  libs)    cmd_libs ;;
  build)   cmd_build ;;
  install) cmd_install ;;
  data)    cmd_data ;;
  config)  cmd_config ;;
  launch)  cmd_launch ;;
  all)     cmd_all ;;
  *) sed -n '2,25p' "$0"; exit 2 ;;
esac
