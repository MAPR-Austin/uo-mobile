#!/usr/bin/env bash
# UO Mobile, Android phase 0 smoke test (CI, inside the emulator runner): install the hello APK,
# start it with each FNA3D driver, and keep logcat and a screenshot of each run.
# Passes when the OpenGL (GLES3) run draws frames; the Vulkan run is reported, not required
# (the emulator's software Vulkan is not a phone GPU).
#   bash android/smoke.sh <apk>
set -u

APK="$1"
PKG=com.mapraustin.britgraveyard.hello
ACT="$PKG/com.mapraustin.britgraveyard.hello.MainActivity"
OUT=android/smoke-out
mkdir -p "$OUT"

adb install -r "$APK" || { echo "install failed"; exit 1; }
adb shell getprop ro.build.version.release
adb shell getprop ro.product.cpu.abi

gl_ok=0

for drv in OpenGL Vulkan; do
  echo "=== $drv"
  adb shell am force-stop "$PKG"
  adb logcat -c
  adb shell am start -W -n "$ACT" -e driver "$drv"
  sleep 25
  adb shell input tap 400 400
  sleep 3
  adb logcat -d > "$OUT/logcat-$drv.txt"
  adb exec-out screencap -p > "$OUT/screen-$drv.png"
  grep -E "UOM-HELLO|FATAL|AndroidRuntime|libc  |DOTNET|SDL" "$OUT/logcat-$drv.txt" | head -60

  if grep -qE "UOM-HELLO.*frames [0-9]+ at ([2-9]|[1-9][0-9])" "$OUT/logcat-$drv.txt"; then
    echo "$drv: drawing frames"
    [ "$drv" = OpenGL ] && gl_ok=1
  else
    echo "$drv: NO FRAMES"
  fi
done

adb shell am force-stop "$PKG"
[ "$gl_ok" = 1 ] || { echo "smoke test failed: the OpenGL run drew no frames"; exit 1; }
echo "smoke test passed"
