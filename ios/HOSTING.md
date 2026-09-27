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
- Game: `34.174.14.240:2593`. Files: `https://34-174-14-240.sslip.io/files/` (Caddy with Let's
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

## 3. The manifest

`https://<host>/files/manifest.json`, generated on the server by a script whenever files
change:

```json
{ "version": 3, "min_app_build": 33,
  "files": [ { "path": "uo/artLegacyMUL.uop", "size": 312345678, "sha256": "..." },
             { "path": "uo/Music/Digital/Stones.mp3", "size": 912345, "sha256": "..." } ] }
```

The paths mirror the folder that ClassicUO's `-uopath` expects. Music (the 4 mobile tracks
plus Config.txt, 3.7 MB) lives under `uo/Music/Digital`, where the client already looks.

## 4. App: the download step

Runs in `GameController.LoadContent` **before** `UO.Load`. The renderer's fonts and the
background image are embedded, so a progress screen can draw before any UO file exists.

1. Read `installed.json` (the manifest last installed completely).
2. Fetch `manifest.json` (short timeout). If that fails and an install is complete, start the
   game as-is. If nothing is installed, show an error with a Retry button.
3. Compare by path + size + sha256 against `installed.json`, not by re-hashing 2 GB on every
   launch. List the files to fetch and the files to delete.
4. First install, or a large update: ask first ("Download 2.1 GB of game files? Wi-Fi
   recommended."). Small updates start on their own.
5. Download on a background task with `HttpClient`, one file at a time, into `name.part`,
   resuming with `Range`. Check the size and sha256, then rename atomically. The main thread
   keeps drawing (progress bar, MB/s, ETA), so the iOS launch watchdog never fires.
6. Write `installed.json` only after every file checks out, then call `UO.Load` as today. A
   partial set never reaches `UO.Load`.
7. If the app is backgrounded, the download is suspended and resumes with `Range` on return
   or on the next launch.

- **Storage**: `Library/Application Support/uo`, marked excluded from iCloud backup. This is
  not `Documents`, so 2 GB doesn't land in the user's iCloud backup.
- **Precedence**: complete download, then the manual copy (`FindUoData` in Documents), then
  the error screen. After the download works, the old 2 GB manual copy in Documents can be
  deleted in the Files app.
- **Server address**: the default host is baked into the build. `Documents/server.txt`
  (`host[:port]`) overrides it for home/LAN testing. The Local Network permission prompt is
  only triggered when the host is a private address.
- **Desktop test** (AUDIT rule: every iOS-only path runs on desktop): `UOM_DOWNLOAD=1` runs
  the same step in the Windows client against the real server, into a scratch folder.

## 5. Rollout order

1. Ship build 32 (portrait + pinch) on the current home setup. It is independent of the cloud work.
2. Owner creates the Google Cloud account and project, with billing and a budget alert.
3. Set up the VM: Mono, ServUO + current world, client data, Caddy, firewall, systemd,
   backups. Test from the Windows client (login, walk, fight, save, restart).
4. Build 33: the downloader + the cloud host. Test it on desktop against the VM, run the
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
