"""
UO Mobile: one-time App Store Connect signing setup, run on the Windows PC.

Uses an App Store Connect API key (Admin) to:
  1. register the bundle id,
  2. create an Apple Distribution certificate from a locally generated private key,
  3. create an App Store provisioning profile for the bundle id + certificate,
and writes everything the CI needs into the secrets folder (never into the repo):
  dist.key, dist.p12, dist.p12.password, profile.mobileprovision, signing.json

Usage:
  python asc_setup.py --key-id MJU8QWWXD5 --issuer-id <uuid> --secrets C:\\Users\\18166\\dev\\uo-mobile\\secrets
Re-running reuses the bundle id and the certificate (while dist.key exists) and replaces the profile.
"""

import argparse
import base64
import email.utils
import json
import os
import secrets as pysecrets
import subprocess
import sys
import time

import jwt
import requests

API = "https://api.appstoreconnect.apple.com/v1"


class Asc:
    def __init__(self, key_id, issuer_id, key_path):
        self.key_id = key_id
        self.issuer_id = issuer_id
        with open(key_path, "r", encoding="utf-8") as f:
            self.private_key = f.read()

    def _token(self):
        # Apple rejects tokens issued "in the future"; the PC clock was ~5 min fast, so use Apple's.
        if not hasattr(self, "_skew"):
            date = requests.head(API + "/apps", timeout=30).headers.get("Date")
            self._skew = int(email.utils.parsedate_to_datetime(date).timestamp() - time.time()) if date else 0
        now = int(time.time()) + self._skew - 30
        payload = {"iss": self.issuer_id, "iat": now, "exp": now + 900, "aud": "appstoreconnect-v1"}
        return jwt.encode(payload, self.private_key, algorithm="ES256", headers={"kid": self.key_id, "typ": "JWT"})

    def call(self, method, path, **kw):
        r = requests.request(method, API + path, headers={"Authorization": "Bearer " + self._token()}, timeout=60, **kw)

        if r.status_code >= 400:
            try:
                errors = r.json().get("errors", [])
                detail = "; ".join(f"{e.get('title')}: {e.get('detail')}" for e in errors)
            except ValueError:
                detail = r.text[:500]
            raise SystemExit(f"App Store Connect API {method} {path} failed ({r.status_code}): {detail}")

        return r.json() if r.content else {}


def run(cmd):
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--key-id", required=True)
    ap.add_argument("--issuer-id", required=True)
    ap.add_argument("--secrets", required=True)
    ap.add_argument("--bundle-id", default="com.mapraustin.britgraveyard")
    ap.add_argument("--bundle-name", default="Brit Graveyard Battles")
    ap.add_argument("--profile-name", default="Brit Graveyard Battles AppStore")
    a = ap.parse_args()

    sec = a.secrets
    asc = Asc(a.key_id, a.issuer_id, os.path.join(sec, f"AuthKey_{a.key_id}.p8"))

    # 1. bundle id
    found = asc.call("GET", "/bundleIds", params={"filter[identifier]": a.bundle_id, "limit": 5})["data"]
    found = [b for b in found if b["attributes"]["identifier"] == a.bundle_id]

    if found:
        bundle = found[0]
        print(f"bundle id {a.bundle_id}: already registered")
    else:
        bundle = asc.call("POST", "/bundleIds", json={"data": {"type": "bundleIds", "attributes": {
            "identifier": a.bundle_id, "name": a.bundle_name, "platform": "IOS"}}})["data"]
        print(f"bundle id {a.bundle_id}: registered")

    # 2. distribution certificate from a local key
    key_file = os.path.join(sec, "dist.key")
    cert_json = os.path.join(sec, "dist.cert.json")
    cert = None

    if os.path.exists(key_file) and os.path.exists(cert_json):
        with open(cert_json, "r", encoding="utf-8") as f:
            cert_id = json.load(f)["id"]
        try:
            cert = asc.call("GET", f"/certificates/{cert_id}")["data"]
            print("distribution certificate: reusing", cert["attributes"].get("name"), cert["attributes"].get("expirationDate"))
        except SystemExit:
            cert = None

    if cert is None:
        csr_file = os.path.join(sec, "dist.csr")
        run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", key_file, "-out", csr_file,
             "-subj", "/CN=Brit Graveyard Battles CI/C=US"])
        with open(csr_file, "r", encoding="utf-8") as f:
            csr = f.read()
        csr_b64 = "".join(l for l in csr.splitlines() if "-----" not in l)
        cert = asc.call("POST", "/certificates", json={"data": {"type": "certificates", "attributes": {
            "certificateType": "DISTRIBUTION", "csrContent": csr_b64}}})["data"]
        with open(cert_json, "w", encoding="utf-8") as f:
            json.dump({"id": cert["id"]}, f)
        print("distribution certificate: created", cert["attributes"].get("name"), cert["attributes"].get("expirationDate"))

    der = base64.b64decode(cert["attributes"]["certificateContent"])
    cer_file = os.path.join(sec, "dist.cer")
    with open(cer_file, "wb") as f:
        f.write(der)

    # p12 in the legacy (3DES/SHA1) format that macOS `security import` accepts everywhere
    pw_file = os.path.join(sec, "dist.p12.password")
    if not os.path.exists(pw_file):
        with open(pw_file, "w", encoding="utf-8") as f:
            f.write(pysecrets.token_urlsafe(24))
    with open(pw_file, "r", encoding="utf-8") as f:
        pw = f.read().strip()
    pem_file = os.path.join(sec, "dist.pem")
    run(["openssl", "x509", "-inform", "DER", "-in", cer_file, "-out", pem_file])
    run(["openssl", "pkcs12", "-export", "-legacy", "-inkey", key_file, "-in", pem_file,
         "-out", os.path.join(sec, "dist.p12"), "-passout", "pass:" + pw, "-name", "Apple Distribution"])
    print("dist.p12: written")

    # 3. App Store provisioning profile (replace any old one with the same name)
    for p in asc.call("GET", "/profiles", params={"filter[name]": a.profile_name, "limit": 20})["data"]:
        asc.call("DELETE", f"/profiles/{p['id']}")
        print("profile: removed old", p["id"])

    prof = asc.call("POST", "/profiles", json={"data": {"type": "profiles",
        "attributes": {"name": a.profile_name, "profileType": "IOS_APP_STORE"},
        "relationships": {
            "bundleId": {"data": {"type": "bundleIds", "id": bundle["id"]}},
            "certificates": {"data": [{"type": "certificates", "id": cert["id"]}]}}}})["data"]
    with open(os.path.join(sec, "profile.mobileprovision"), "wb") as f:
        f.write(base64.b64decode(prof["attributes"]["profileContent"]))
    print("profile:", prof["attributes"]["name"], "uuid", prof["attributes"]["uuid"], "expires", prof["attributes"]["expirationDate"])

    with open(os.path.join(sec, "signing.json"), "w", encoding="utf-8") as f:
        json.dump({"bundle_id": a.bundle_id, "profile_name": a.profile_name, "profile_uuid": prof["attributes"]["uuid"],
                   "certificate_name": cert["attributes"].get("name"), "key_id": a.key_id, "issuer_id": a.issuer_id}, f, indent=2)
    print("signing.json: written")


if __name__ == "__main__":
    sys.exit(main())
