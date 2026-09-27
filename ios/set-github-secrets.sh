#!/bin/bash
# UO Mobile: store the App Store signing material as encrypted GitHub Actions secrets.
# Run from Git Bash on the PC (after ios/asc_setup.py). Nothing is printed.
#   bash ClassicUO/ios/set-github-secrets.sh [secrets-dir] [owner/repo]
set -euo pipefail
SEC="${1:-/c/Users/18166/dev/uo-mobile/secrets}"
REPO="${2:-MAPR-Austin/uo-mobile}"
GH="${GH:-/c/Users/18166/dev/gh/bin/gh.exe}"
KEY_ID="$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['key_id'])" "$SEC/signing.json")"
ISSUER_ID="$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['issuer_id'])" "$SEC/signing.json")"

set_secret() { printf '%s' "$2" | "$GH" secret set "$1" -R "$REPO" >/dev/null && echo "set $1"; }

set_secret ASC_KEY_ID "$KEY_ID"
set_secret ASC_ISSUER_ID "$ISSUER_ID"
"$GH" secret set ASC_KEY_P8 -R "$REPO" < "$SEC/AuthKey_$KEY_ID.p8" >/dev/null && echo "set ASC_KEY_P8"
set_secret DIST_P12_BASE64 "$(base64 -w0 "$SEC/dist.p12")"
set_secret DIST_P12_PASSWORD "$(tr -d '\r\n' < "$SEC/dist.p12.password")"
set_secret PROFILE_BASE64 "$(base64 -w0 "$SEC/profile.mobileprovision")"
echo "Done. Secrets on $REPO:"
"$GH" secret list -R "$REPO"
