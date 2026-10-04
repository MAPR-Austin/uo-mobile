#!/usr/bin/env bash
# UO Mobile on Android, the whole path (CI, run by hand - the owner allowed a few runs): install
# the game, download the 2.1 GB of game files from the live server, log in as the throwaway
# account "ciandroid" (character Ciandroid, made from the PC), walk, rotate. Keeps screenshots,
# logcat and the game's logs - never uomobile.txt, which holds the password.
#   CI_PASSWORD=... bash android/live-test.sh <apk>
set -u

APK="$1"
PKG=com.mapraustin.britgraveyard
ACT="$PKG/com.mapraustin.britgraveyard.MainActivity"
OUT=android/live-out
D="/sdcard/Android/data/$PKG/files"
mkdir -p "$OUT"

shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
pullout() {
  adb logcat -d > "$OUT/logcat.txt"
  adb pull "$D/uomobile-console.log" "$OUT/" >/dev/null 2>&1 || true
  adb pull "$D/crash.txt" "$OUT/" >/dev/null 2>&1 || true
  adb pull "$D/Logs" "$OUT/cuo-logs" >/dev/null 2>&1 || true
}

[ -n "${CI_PASSWORD:-}" ] || { echo "no CI_PASSWORD"; exit 1; }
adb install -r "$APK" || { echo "install failed"; exit 1; }
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1   # landscape

# first launch: makes the app's folder; Android's one-time "Viewing full screen" note is dismissed
adb shell am start -W -n "$ACT"
sleep 10
shot 0-first-launch
adb shell input tap 510 190   # "Got it"
sleep 2
adb shell am force-stop "$PKG"

# the test account, as uomobile.txt args (the game's log hides -password values)
printf 'args=-username ciandroid -password %s -skiploginscreen -autologin true -lastcharactername Ciandroid\n' "$CI_PASSWORD" > "${RUNNER_TEMP:-/tmp}/uomobile.txt"
adb push "${RUNNER_TEMP:-/tmp}/uomobile.txt" "$D/uomobile.txt" >/dev/null
rm -f "${RUNNER_TEMP:-/tmp}/uomobile.txt"

adb logcat -c
adb shell am start -W -n "$ACT"
sleep 12
shot 1-download-screen
adb shell input tap 320 255   # Download
start=$(date +%s)

# the download, the login, the world: up to 25 minutes
inworld=0
for i in $(seq 1 50); do
  sleep 30
  shot 2-progress
  if adb logcat -d | grep -q "GumpWarmup:"; then inworld=1; break; fi
  if ! adb shell pidof "$PKG" >/dev/null; then echo "the app died"; break; fi
done
echo "after $(( $(date +%s) - start )) s: in world = $inworld"

if [ "$inworld" = 1 ]; then
  sleep 10
  shot 3-world-landscape
  adb shell input swipe 90 240 150 240 2500   # the joystick, held: walk
  sleep 1
  shot 4-after-walk
  adb shell settings put system user_rotation 0   # portrait
  sleep 8
  shot 5-world-portrait
fi

pullout
grep -E "UOMobile|FATAL|AndroidRuntime|GumpWarmup|download|Download" "$OUT/logcat.txt" | grep -v "Accessing hidden" | head -60
[ -f "$OUT/crash.txt" ] && { echo "--- crash.txt"; cat "$OUT/crash.txt"; }
adb shell am force-stop "$PKG"

[ "$inworld" = 1 ] || { echo "live test failed: never reached the world"; exit 1; }
echo "live test passed"
