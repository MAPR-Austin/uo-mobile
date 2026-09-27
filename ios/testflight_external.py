"""
UO Mobile: set up TestFlight external testing for a build and submit it for Beta App Review.
Idempotent: reuses the group, localizations and testers that already exist.

  python ios/testflight_external.py --build 35 --group Friends
      --contact "First Last" --phone "+1 555-555-5555" --email you@example.com
      --tester "First Last <friend@example.com>" [--tester ...]

Personal details are passed on the command line only; nothing is stored in the repo.
Uses the App Store Connect API key from the secrets folder (see asc_setup.py).
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from asc_setup import Asc  # noqa: E402

BUNDLE_ID = "com.mapraustin.britgraveyard"
LOCALE = "en-US"

DESCRIPTION = (
    "Graveyard Battles is a small Ultima Online-style game for iPhone, played with a few friends: "
    "explore the lands around the Britain graveyard, fight, gather, craft and build, with touch "
    "controls (joystick, action buttons, macros, pinch to zoom)."
)
WHATS_NEW = (
    "First external build. Please try: the first-launch download of the game files, creating an "
    "account and a character, walking with the joystick, fighting (Attack Nearest), the action "
    "buttons, and pinch-to-zoom on the world and on windows."
)
REVIEW_NOTES = (
    "On first launch the app downloads its game files (about 2 GB) from our server; please use "
    "Wi-Fi and allow a few minutes. No demo account is needed: on the login screen enter any new "
    "account name and password, and the account is created on first login; then create a "
    "character. Walk with the joystick at the bottom left, tap things to select or target them, "
    "and use the buttons on the right to attack."
)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--build", required=True)
    ap.add_argument("--group", default="Friends")
    ap.add_argument("--contact", required=True, help='"First Last" for Apple\'s reviewer')
    ap.add_argument("--phone", required=True)
    ap.add_argument("--email", required=True, help="reviewer contact and feedback email")
    ap.add_argument("--tester", action="append", default=[], help='"First Last <email>"')
    ap.add_argument("--no-submit", action="store_true")
    ap.add_argument("--secrets", default=r"C:\Users\18166\dev\uo-mobile\secrets")
    a = ap.parse_args()

    with open(os.path.join(a.secrets, "signing.json"), encoding="utf-8") as f:
        sj = json.load(f)
    asc = Asc(sj["key_id"], sj["issuer_id"], os.path.join(a.secrets, f"AuthKey_{sj['key_id']}.p8"))

    app_id = asc.call("GET", f"/apps?filter[bundleId]={BUNDLE_ID}")["data"][0]["id"]

    # --- build
    builds = asc.call("GET", f"/builds?filter[app]={app_id}&filter[version]={a.build}")["data"]
    if not builds:
        raise SystemExit(f"build {a.build} not found")
    build = builds[0]
    if build["attributes"].get("processingState") != "VALID":
        raise SystemExit(f"build {a.build} is {build['attributes'].get('processingState')}, not VALID yet")
    build_id = build["id"]
    print(f"build {a.build}: {build_id}")

    # --- external group
    groups = asc.call("GET", f"/apps/{app_id}/betaGroups?limit=50")["data"]
    group = next((g for g in groups if g["attributes"]["name"] == a.group and not g["attributes"].get("isInternalGroup")), None)
    if group is None:
        group = asc.call("POST", "/betaGroups", json={"data": {
            "type": "betaGroups",
            "attributes": {"name": a.group},
            "relationships": {"app": {"data": {"type": "apps", "id": app_id}}}}})["data"]
        print(f"created external group {a.group!r}")
    else:
        print(f"external group {a.group!r} exists")
    group_id = group["id"]

    # --- test information (app level)
    locs = asc.call("GET", f"/betaAppLocalizations?filter[app]={app_id}")["data"]
    loc = next((l for l in locs if l["attributes"]["locale"] == LOCALE), None)
    attrs = {"description": DESCRIPTION, "feedbackEmail": a.email}
    if loc is None:
        asc.call("POST", "/betaAppLocalizations", json={"data": {
            "type": "betaAppLocalizations", "attributes": dict(attrs, locale=LOCALE),
            "relationships": {"app": {"data": {"type": "apps", "id": app_id}}}}})
    else:
        asc.call("PATCH", f"/betaAppLocalizations/{loc['id']}", json={"data": {
            "type": "betaAppLocalizations", "id": loc["id"], "attributes": attrs}})
    print("test information set")

    # --- reviewer contact
    first, _, last = a.contact.partition(" ")
    detail = asc.call("GET", f"/apps/{app_id}/betaAppReviewDetail")["data"]
    asc.call("PATCH", f"/betaAppReviewDetails/{detail['id']}", json={"data": {
        "type": "betaAppReviewDetails", "id": detail["id"], "attributes": {
            "contactFirstName": first, "contactLastName": last or first,
            "contactPhone": a.phone, "contactEmail": a.email,
            "demoAccountRequired": False, "notes": REVIEW_NOTES}}})
    print("review contact set")

    # --- what to test (build level)
    blocs = asc.call("GET", f"/builds/{build_id}/betaBuildLocalizations")["data"]
    bloc = next((l for l in blocs if l["attributes"]["locale"] == LOCALE), None)
    if bloc is None:
        asc.call("POST", "/betaBuildLocalizations", json={"data": {
            "type": "betaBuildLocalizations", "attributes": {"locale": LOCALE, "whatsNew": WHATS_NEW},
            "relationships": {"build": {"data": {"type": "builds", "id": build_id}}}}})
    else:
        asc.call("PATCH", f"/betaBuildLocalizations/{bloc['id']}", json={"data": {
            "type": "betaBuildLocalizations", "id": bloc["id"], "attributes": {"whatsNew": WHATS_NEW}}})
    print("what-to-test set")

    # --- build into the group
    asc.call("POST", f"/betaGroups/{group_id}/relationships/builds", json={"data": [{"type": "builds", "id": build_id}]})
    print(f"build {a.build} added to {a.group!r}")

    # --- testers (Apple emails the invite once the build is approved)
    for t in a.tester:
        m = re.match(r"\s*(.*?)\s*<([^>]+)>\s*$", t)
        if not m:
            raise SystemExit(f"tester must look like 'First Last <email>': {t}")
        name, email = m.group(1), m.group(2)
        tfirst, _, tlast = name.partition(" ")
        existing = asc.call("GET", f"/betaTesters?filter[email]={email}")["data"]
        if existing:
            asc.call("POST", f"/betaGroups/{group_id}/relationships/betaTesters",
                     json={"data": [{"type": "betaTesters", "id": existing[0]["id"]}]})
        else:
            asc.call("POST", "/betaTesters", json={"data": {
                "type": "betaTesters", "attributes": {"email": email, "firstName": tfirst, "lastName": tlast},
                "relationships": {"betaGroups": {"data": [{"type": "betaGroups", "id": group_id}]}}}})
        print(f"tester added: {name}")

    # --- Beta App Review
    if not a.no_submit:
        subs = asc.call("GET", f"/betaAppReviewSubmissions?filter[build]={build_id}")["data"]
        if subs:
            print(f"already submitted: {subs[0]['attributes'].get('betaReviewState')}")
        else:
            sub = asc.call("POST", "/betaAppReviewSubmissions", json={"data": {
                "type": "betaAppReviewSubmissions",
                "relationships": {"build": {"data": {"type": "builds", "id": build_id}}}}})["data"]
            print(f"submitted for Beta App Review: {sub['attributes'].get('betaReviewState')}")


if __name__ == "__main__":
    main()
