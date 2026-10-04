#!/usr/bin/env bash
# UO Mobile: build the FNA native libraries for Android with the NDK (CI: ubuntu-24.04 has
# NDK 27.3, CMake and Ninja preinstalled).
#
# Output:
#   android/native/<abi>/libSDL3.so libFNA3D.so libFAudio.so   (arm64-v8a: phones, x86_64: emulators)
#   android/sdl-java/*.java   SDL's Java glue from the same tag - SDLActivity refuses to start
#                             when the Java and native SDL versions differ.
#
# Environment (optional):
#   SDL_TAG   SDL release tag (default release-3.4.18; iOS stays on release-3.2.x)
#   ABIS      default "arm64-v8a x86_64"
#   ANDROID_NDK_HOME / ANDROID_NDK_ROOT   the NDK (r27+)
#
# Flags follow CelesteAndroid's build-natives-android.ps1 (same FNA + SDL3 stack): API 26, and
# 16 KB page alignment, which Google Play requires (ANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES).
# Theorafile is left out: ClassicUO never plays video, and its P/Invokes load only when called.

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SDL_TAG="${SDL_TAG:-release-3.4.18}"
ABIS="${ABIS:-arm64-v8a x86_64}"
API=26
NDK="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-${ANDROID_NDK_LATEST_HOME:-}}}"
BUILD="$HERE/.build"
OUT="$HERE/native"
FNA_LIB="$REPO/external/FNA/lib"

log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[ -n "$NDK" ] && [ -f "$NDK/build/cmake/android.toolchain.cmake" ] || die "no NDK (set ANDROID_NDK_HOME)"
[ -f "$FNA_LIB/FNA3D/CMakeLists.txt" ] || die "external/FNA submodules missing (git submodule update --init --recursive)"
command -v cmake >/dev/null && command -v ninja >/dev/null || die "cmake and ninja are needed"
echo "NDK: $NDK"

mkdir -p "$BUILD" "$OUT"

if [ ! -d "$BUILD/SDL/.git" ]; then
  log "Cloning SDL ($SDL_TAG)"
  git clone --depth 1 --branch "$SDL_TAG" https://github.com/libsdl-org/SDL.git "$BUILD/SDL"
fi

for abi in $ABIS; do
  common=(-G Ninja -DCMAKE_BUILD_TYPE=Release
          -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake"
          -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$API"
          -DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON)

  log "SDL3 ($abi)"
  cmake -S "$BUILD/SDL" -B "$BUILD/sdl-$abi" "${common[@]}" \
        -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
  ninja -C "$BUILD/sdl-$abi"
  sdl_so="$(find "$BUILD/sdl-$abi" -maxdepth 2 -name 'libSDL3.so' | head -1)"
  [ -n "$sdl_so" ] || die "libSDL3.so not produced"
  sdl_vars=(-DBUILD_SDL3=ON -DSDL3_INCLUDE_DIRS="$BUILD/SDL/include" -DSDL3_LIBRARIES="$sdl_so")

  log "FNA3D ($abi)"
  cmake -S "$FNA_LIB/FNA3D" -B "$BUILD/fna3d-$abi" "${common[@]}" "${sdl_vars[@]}" \
        -DBUILD_SHARED_LIBS=ON -DMOJOSHADER_STATIC_SPIRVCROSS=ON
  ninja -C "$BUILD/fna3d-$abi"

  log "FAudio ($abi)"
  cmake -S "$FNA_LIB/FAudio" -B "$BUILD/faudio-$abi" "${common[@]}" "${sdl_vars[@]}" \
        -DBUILD_SHARED_LIBS=ON
  ninja -C "$BUILD/faudio-$abi"

  mkdir -p "$OUT/$abi"
  cp -f "$sdl_so" "$OUT/$abi/libSDL3.so"
  cp -f "$(find "$BUILD/fna3d-$abi" -maxdepth 2 -name 'libFNA3D.so' | head -1)" "$OUT/$abi/libFNA3D.so"
  cp -f "$(find "$BUILD/faudio-$abi" -maxdepth 2 -name 'libFAudio.so' | head -1)" "$OUT/$abi/libFAudio.so"
done

log "SDL Java glue"
rm -rf "$HERE/sdl-java"
mkdir -p "$HERE/sdl-java"
cp "$BUILD/SDL/android-project/app/src/main/java/org/libsdl/app/"*.java "$HERE/sdl-java/"
ls "$HERE/sdl-java"

log "Result"
READELF="$(find "$NDK/toolchains/llvm/prebuilt" -name 'llvm-readelf' | head -1)"
for so in "$OUT"/*/*.so; do
  printf '%-34s %8s bytes' "${so#$OUT/}" "$(stat -c %s "$so")"
  # 16 KB pages: every LOAD segment aligned to 0x4000 or more
  if [ -n "$READELF" ]; then
    align="$("$READELF" -lW "$so" | awk '$1=="LOAD"{print $NF}' | sort -u | tr '\n' ' ')"
    printf '   LOAD align %s' "$align"
  fi
  echo
done
