#!/usr/bin/env bash
# UO Mobile, Android phase 1 smoke test (CI, inside the emulator runner): install the game, start
# it, and check ClassicUO comes up and stays up - with nothing tapped, it waits on the "the game
# needs N GB of game files" screen, so no download starts. Keeps logcat, the console log and a
# screenshot in portrait and in landscape.
#   bash android/app-smoke.sh <apk>
set -u

APK="$1"
PKG=com.mapraustin.britgraveyard
ACT="$PKG/com.mapraustin.britgraveyard.MainActivity"
OUT=android/smoke-out
mkdir -p "$OUT"

adb install -r "$APK" || { echo "install failed"; exit 1; }
adb logcat -c
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1   # landscape
adb shell am start -W -n "$ACT"
sleep 45
adb exec-out screencap -p > "$OUT/app-landscape.png"
adb shell settings put system user_rotation 0   # portrait
sleep 8
adb exec-out screencap -p > "$OUT/app-portrait.png"
adb logcat -d > "$OUT/logcat-app.txt"
adb pull "/sdcard/Android/data/$PKG/files/uomobile-console.log" "$OUT/" >/dev/null 2>&1 || true
adb pull "/sdcard/Android/data/$PKG/files/crash.txt" "$OUT/" >/dev/null 2>&1 || true
adb pull "/sdcard/Android/data/$PKG/files/Logs" "$OUT/cuo-logs" >/dev/null 2>&1 || true

grep -E "UOMobile|DOTNET|FATAL|AndroidRuntime|SDL" "$OUT/logcat-app.txt" | grep -v "Accessing hidden" | head -80
[ -f "$OUT/crash.txt" ] && { echo "--- crash.txt"; cat "$OUT/crash.txt"; }

alive="$(adb shell pidof "$PKG" | tr -d '\r')"
echo "pid: ${alive:-none}"
adb shell am force-stop "$PKG"

grep -q "starting ClassicUO" "$OUT/logcat-app.txt" || { echo "app smoke failed: ClassicUO never started"; exit 1; }
[ -n "$alive" ] || { echo "app smoke failed: the app died"; exit 1; }
grep -qE "FATAL|FAILED:" "$OUT/logcat-app.txt" && { echo "app smoke failed: a fatal error was logged"; exit 1; }
echo "app smoke passed"
