# Cloud server + in-app game-file download (design)

Goal: friends outside the home network can play, with low ping, and the app fetches its own
game files (UO client data + music) from our server. No more copying 2 GB through Apple
Devices (build 31: the music copy arrived as 0 KB files).

Rule for the whole rollout: the current setup keeps working at every step. The home server
and the manual Documents copy stay as fallbacks until the cloud path has been proven on the phone.

## Decisions (owner, 2026-09-27)

- Host: Google Cloud us-south1 (Dallas), `e2-standard-2`
- Address: free `<ip-with-dashes>.sslip.io` name
- World: copy the current home world (characters, accounts, items)
- Accounts: anyone with the app can create one on first login (as today)

## Live server (2026-09-27)

- Google Cloud project `graveyardbattles`, VM `uo-server`, zone `us-south1-a`, `e2-standard-2`,
  Ubuntu 24.04, static IP **34.174.14.240**. Measured from the owner's home: 31 ms average.
- Game: `34-174-14-240.sslip.io:2593` (a host name, so it works on IPv6-only networks; it resolves to 34.174.14.240). Files: `https://34-174-14-240.sslip.io/files/` (Caddy with Let's
  Encrypt; `files/uo` links to the UO data folder `/srv/uo/client`, and music is in
  `uo/Music/Digital`).
- Shard: `/srv/uo/servuo`, systemd unit `uo` (Mono 6.8). `uo-ctl save | say <text> |
  restart [minutes] | status | log`. A stop saves first (ServiceControl.cs; ServUO doesn't save
  on its own). Nightly backup at 04:15 to `/srv/uo/backups` (keeps 14).
- Deploy files: the servuo repo's `deploy/` folder (setup.sh, unit, scripts, Caddyfile), staged
  through the private bucket `gs://graveyardbattles-uo-data`.
- Linux differences from the Windows copy:
  - `Compiler.Dynamic=False` (use the shipped Scripts.dll, no `dotnet` on the VM);
  - `Server.Address=34.174.14.240` (otherwise ServUO advertises 127.0.0.1 to internet clients
    after login);
  - `Nice=-10` in the unit (Mono refuses ServUO's own High-priority call; it is now caught).
- Downloads: `/srv/uo/bin/make-manifest.py` writes `files/manifest.json` (174 files, 2.15 GB:
  80 UO data files and 94 music tracks). Re-run it after changing anything in `/srv/uo/client`;
  phones pick up only the changed files on their next launch. The music is UO's originals
  (`/srv/uo/music-src`) re-encoded to 48 kHz by `music-48k.sh`. FAudio's linear upsampling of
  the 22 kHz originals was audible as hiss ("static" on Stones).
- Admin SSH: `gcloud compute ssh uoadmin@uo-server --zone us-south1-a` (a Linux user name can't
  start with a digit, so the Windows user name `18166` won't work).

## 1. Where the server runs (ping first)

Measured from the owner's home connection (Spectrum cable, 2026-09-27). ICMP ping, TCP handshake, or
HTTP request on a warm connection; lower is better:

| Host | Round trip |
|---|---|
| **Google Cloud us-south1 (Dallas)** | **~33 ms median** (29 min) |
| Google Cloud us-central1 (Iowa) | ~64 ms |
| Vultr Dallas / Chicago | ~64-67 ms |
| Linode Chicago / Dallas | ~65-68 ms |

The ISP reaches Google's network in ~26 ms but takes ~60 ms to reach the other providers, so
Google Cloud Dallas gets about half the ping. Google's premium network tier also brings each
friend onto its backbone at their nearest edge.

- **Machine**: `e2-standard-2` (2 dedicated-share vCPU, 8 GB), Ubuntu 24.04 LTS, 30 GB
  balanced disk, reserved static IP, premium network tier. Shared-core `e2-medium` costs about
  half but can stutter under CPU bursts (world saves).
- **ServUO on Linux**: run the Windows-built net48 assemblies under Mono (ServUO detects
  Mono: `Core.Unix`, `libz` imports). Fallback if Mono misbehaves: a Windows Server image
  (costs more for the licence).
- **Latency on the server side**: TCP_NODELAY is already set by the ServUO listener and the
  ClassicUO socket.

## 2. Server layout

```
/srv/uo/servuo/   ServUO.exe, Scripts.dll, Config/, Data/, Saves/, Backups/
/srv/uo/client/   UO client data (ServUO's DataPath reads maps/statics/tiledata/multis)
/srv/uo/files/    what the app downloads: manifest.json, uo/ (-> client files), music/
```

- `servuo.service` (systemd): runs as user `uo`, restarts on failure, stdout goes to the
  journal. Check how ServUO's console reader behaves without a TTY before relying on it.
- **Caddy** serves `/srv/uo/files` over HTTPS with an automatic Let's Encrypt certificate.
  It supports HTTP Range, so interrupted downloads resume. iOS blocks plain HTTP by default,
  so HTTPS is required.
- **Hostname**: `<ip-with-dashes>.sslip.io` (free, no account, certificate works) or a bought
  domain (~$12/yr; can move servers without an app update).
- **Firewall**: tcp 2593 (game), 80/443 (files); SSH key-only.
- **Backups**: nightly tar of `Saves/` + accounts, keep 14, plus a weekly disk snapshot.
- **World**: copy the current home world (after a save) so existing characters carry over.
- **Relay IP**: the iOS client sets `ignore_relay_ip`, so it always reconnects to the host it
  logged in to, whatever address ServUO advertises.
- **Accounts**: auto-create on first login (as today). Anyone with the app and the address can
  make an account; add invite codes later if that becomes a problem.

## 3. The manifest (as built)

`https://34-174-14-240.sslip.io/files/manifest.json` is written by `/srv/uo/bin/make-manifest.py`
from everything under `/srv/uo/client`:

```json
{ "version": 50228959978997,
  "files": [ { "path": "uo/artLegacyMUL.uop", "size": 312345678, "sha256": "..." },
             { "path": "uo/Music/Digital/Stones.mp3", "size": 1234567, "sha256": "..." } ] }
```

- The version is derived from the contents, so an unchanged set keeps its version.
- Paths mirror the UO folder, and music is in `uo/Music/Digital`, where the client looks.
- The app rejects a manifest with fewer than 20 files or without `uo/tiledata.mul`, and
  treats it as offline.
- Caddy serves every file with `Cache-Control: no-cache`.
- There is no minimum-app-build field yet.

## 4. App: the download step (as built: Touch/GameFiles.cs, Touch/DownloadScreen.cs)

It runs in place of `UO.Load` (GameController: `LoadContent` stops early, and
`FinishLoadContent` runs when the files are ready, never while backgrounded). The screen draws
with the embedded font.

1. Read `<uo>/.uomobile-installed.json` (manifest path -> sha256, plus a `complete` flag).
2. Fetch the manifest (10 s timeout). If that fails:
   - a complete install plays as it is;
   - otherwise, with nothing adopted yet, the manual copy plays;
   - otherwise the screen shows Retry.
3. For each file not on record with a matching sha256:
   - first hash what's already in the folder (a lost record costs a check, not a download);
   - then adopt from the manual copy (hash, then move);
   - then download.
4. Over 50 MB, ask first ("Download" button).
5. Download with 3 workers into `name.part`:
   - resume with HTTP Range;
   - a 30 s idle timeout per read;
   - 4 tries per file, and an attempt that made progress doesn't count;
   - check the sha256, then rename.
6. Delete files the manifest no longer lists (only now), mark the record complete, load the game.
7. If `UO.Load` throws, the record is dropped, so the next launch re-checks everything by hash.

- **HTTP:** the managed `SocketsHttpHandler` on every platform (iOS's default handler has a
  response cache).
- **iOS launcher (ios/Program.cs):**
  - the default server is `34-174-14-240.sslip.io` (a host name, for IPv6-only networks) and files come from the URL above;
  - `Documents/uomobile.txt` overrides both: `ip=192.168.68.91` for the home server,
    `files=off` for a manual copy;
  - downloads go to `Library/Application Support/uo`, excluded from iCloud backup;
  - a copy found in Documents is passed as `-download_adopt`;
  - the Local Network prompt appears only for private addresses;
  - `-ignore_relay_ip` is always on.
- **Desktop test:** `-download <url> -uopath <folder> [-download_adopt <copy>]` (AUDIT.md
  procedure step 2).

## 5. Rollout order

1. Ship build 32 (portrait + pinch) on the current home setup. It is independent of the cloud work.
2. Owner creates the Google Cloud account and project, with billing and a budget alert.
3. Set up the VM: Mono, ServUO + current world, client data, Caddy, firewall, systemd,
   backups. Test from the Windows client (login, walk, fight, save, restart).
4. Build 34: the downloader + the cloud host. Test it on desktop against the VM, run the
   audit, ship.
5. Phone: build 33 downloads the files and plays on the VM. The home server stays reachable
   through `server.txt`.
6. TestFlight external testing: a public link for friends. The first build goes through Beta
   App Review, which needs a test account and notes, and the server must be up.

## 6. Costs (approximate; the Google console shows exact prices at creation)

- `e2-standard-2` in Dallas: roughly $50-60/month; disk + static IP: ~$5/month.
- Download traffic: ~$0.12/GB, so about $0.25 per new install. Game traffic is negligible.
- New Google Cloud accounts usually come with trial credit.

## 7. Risks

- ServUO under Mono (console without a TTY, zlib, timers): proven on the VM before any
  switch; the Windows image is the fallback.
- Beta App Review for external testers: the reviewer must be able to log in and the 2 GB
  download has to work. Supply a review account and notes.
- The owner's own ping goes from ~3 ms (LAN) to ~30 ms. Friends' ping depends on their ISP.
- Content: the UO client data is EA's free download, which shards commonly redistribute to
  their players. The music is our own.
