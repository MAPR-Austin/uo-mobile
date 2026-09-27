"""
UO Mobile: follow a pushed commit to TestFlight.
  1. waits for the GitHub Actions iOS run of the commit and prints its conclusion,
  2. waits for App Store Connect to receive the upload (buildUploads: errors show up here),
  3. waits for build processing to finish (VALID / INVALID / FAILED).
Usage:
  python ios/ship_watch.py [--sha <commit>] [--secrets C:\\Users\\18166\\dev\\uo-mobile\\secrets]
The build number is the workflow's run number (ApplicationVersion = run_number).
"""
import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from asc_setup import Asc  # noqa: E402

REPO = "MAPR-Austin/uo-mobile"
BUNDLE_ID = "com.mapraustin.britgraveyard"
GH = os.environ.get("GH", r"C:\Users\18166\dev\gh\bin\gh.exe")


def gh_run(sha):
    out = subprocess.run([GH, "run", "list", "-R", REPO, "--commit", sha, "--workflow", "ios.yml",
                          "--json", "databaseId,number,status,conclusion", "--limit", "1"],
                         capture_output=True, text=True, check=True).stdout
    runs = json.loads(out or "[]")
    return runs[0] if runs else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sha", default=None)
    ap.add_argument("--secrets", default=r"C:\Users\18166\dev\uo-mobile\secrets")
    args = ap.parse_args()

    sha = args.sha or subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()

    run = None
    for _ in range(240):  # up to ~2 h
        run = gh_run(sha)
        if run and run["status"] == "completed":
            break
        time.sleep(30)
    if not run or run["status"] != "completed":
        raise SystemExit("ci: timed out waiting for the run")
    print(f"ci: {run['conclusion']} (run {run['number']})", flush=True)
    if run["conclusion"] != "success":
        raise SystemExit(1)
    build = str(run["number"])

    with open(os.path.join(args.secrets, "signing.json"), encoding="utf-8") as f:
        sj = json.load(f)
    asc = Asc(sj["key_id"], sj["issuer_id"], os.path.join(args.secrets, f"AuthKey_{sj['key_id']}.p8"))
    app_id = asc.call("GET", f"/apps?filter[bundleId]={BUNDLE_ID}")["data"][0]["id"]

    # 2. the upload itself (Apple reports post-upload rejections such as ITMS-90683 here)
    for _ in range(60):
        try:
            ups = asc.call("GET", f"/apps/{app_id}/buildUploads?limit=10").get("data", [])
        except SystemExit as e:  # older API versions: skip straight to builds
            print(f"upload: (buildUploads unavailable: {e})", flush=True)
            break
        mine = [u for u in ups if str(u["attributes"].get("cfBundleVersion")) == build]
        if mine:
            state = mine[0]["attributes"].get("state") or {}
            errors = [e.get("description") or e.get("code") for e in (state.get("errors") or [])]
            print(f"upload {build} {state.get('state')} {errors}", flush=True)
            if state.get("state") in ("COMPLETE", "FAILED"):
                if state.get("state") == "FAILED":
                    raise SystemExit(1)
                break
        time.sleep(30)

    # 3. processing
    for _ in range(120):
        builds = asc.call("GET", f"/builds?filter[app]={app_id}&filter[version]={build}").get("data", [])
        if builds:
            st = builds[0]["attributes"].get("processingState")
            if st != "PROCESSING":
                print(f"build {build} {st}", flush=True)
                raise SystemExit(0 if st == "VALID" else 1)
        time.sleep(30)
    raise SystemExit(f"build {build}: still processing after an hour")


if __name__ == "__main__":
    main()
