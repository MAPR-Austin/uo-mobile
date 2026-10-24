# iOS pre-ship audit

Run before **every** push that produces a TestFlight build. A phone round trip costs about
35 minutes (CI plus Apple processing) plus the player's time, so anything catchable on Windows
must be caught here.

## Procedure

1. `bash ios/preflight.sh`: fast static checks (the same script runs first in CI). It must pass.
2. **Desktop exercise of every iOS-only path touched since the last shipped build.** An iOS
   branch (`OperatingSystem.IsIOS()`, `#if IOS`, `HasVirtualKeyboard`) that has not run
   anywhere is untested. Where the code allows it, force the branch on in the desktop client
   with an env flag (`UOM_DOTNET_ZLIB=1` is the example) and play through login, entering the
   world, opening a pack and the paperdoll, and walking. Screen fitting and rotation:
   `UOM_PHONE_FIT=1` runs the phone path on desktop, and the dev command `window W H` "rotates"
   (e.g. `window 480 1000`, then `window 1560 720`). With the flag, the desktop resize handler
   stands aside, so the resize reaches the game through `FollowPhoneRotation`, the only path on
   iOS. Drive two-finger gestures with `down/move/up <id> x y` in `dev/cmd.txt`. Desktop can't
   produce FNA's orientation events: anything that depends on them goes in the phone checks.
   Windows and items: `gumps` lists windows with position, size and zoom; `packitems` gives
   each backpack item's on-screen centre (zoom applied) to aim at; `pack` and `near [tiles]`
   show where an item ended up after a drag.
   Game-file downloads: run the desktop client with `-download <url> -uopath <empty folder>`
   (plus `-download_adopt <copy>` for the reuse path, or an unreachable URL for offline). Cover
   the first install, an update (change a file on the server and rerun make-manifest.py),
   adopting a copy, offline with an install, and offline without one.
3. **Phone checks** for the build, listed in the ship message so the player runs them.
   - Build 32: launch while holding the phone in portrait; rotate both ways in game and on the
     login screen; pinch in and out.
   - Build 33:
     - pinch the backpack and the paperdoll separately (the world zoom must not change);
     - pinch a window with a slow second finger (the window must stay where it is);
     - start a pinch on a scroll arrow or a skill row, then drag a mobile off the world (a health
       bar must appear);
     - drag a stack out of a backpack zoomed to 0.6 (the amount window must open under the finger);
     - tap Names, then pinch the world (name plates must not zoom);
     - read backpack and journal text at 0.6;
     - music plays after copying the music folder in as a whole folder.
   - Build 34:
     - first launch reuses the files already on the phone and downloads only the music (about
       117 MB);
     - the download survives leaving the app and coming back;
     - login goes to the cloud server (34.174.14.240) with no Local Network prompt;
     - music has no static (48 kHz re-encode) and region tracks change as you walk;
     - `ip=192.168.68.91` in uomobile.txt still reaches the home server.
   - Build 35:
     - login reaches the cloud shard by host name on Wi-Fi and on cellular, with no Local
       Network prompt;
     - the console log shows `Connecting to tcp://34-174-14-240.sslip.io:2593/`;
     - `ip=192.168.68.91` still reaches the home server.
     The IPv6-only (NAT64) path itself can't be run from Windows (it needs macOS Internet
     Sharing's NAT64). It was accepted on the code reading (.NET resolves host names with
     getaddrinfo, so DNS64 applies) and on the host name's DNS records.
   - Build 36:
     - sound effects (war/peace, footsteps, spells, hits) have no static;
     - no hitch the first time a sound plays in combat.
   - Build 41:
     - the login, server and character screens sit centred and clear of the notch, Dynamic Island
       and home indicator in landscape both ways and in portrait; rotate on each screen;
     - portrait account screen: Quit and Credits below the chest work; the keyboard comes up for
       the account and password boxes and the boxes stay in view; login music plays;
     - hide (Hiding 80+, some Stealth): after 5 s "You are moving stealthily" and a "Stealth: N"
       counter over your head; a full joystick push walks; the counter goes when you're seen;
     - a hidden guildmate shows in gray. Desktop: `UOM_PHONE_FIT=1 UOM_SAFE_INSETS=iphone` with
       `-touch` fakes an iPhone 15's insets for the login layout and the HUD.
   - Build 42:
     - after logging in, the HUD labels sit on their buttons (build 41 could offset the whole HUD);
     - tap Chat in landscape and in portrait: the view doesn't slide up; the speech line sits just
       above the keyboard and the camera eases up with the keyboard so your character and what's
       said stay in sight; closing the keyboard eases it back; rotate with the keyboard up;
     - other text boxes (a gump's text field, the login boxes) still slide above the keyboard.
       Desktop: `UOM_FAKE_KEYBOARD=0.5` fakes a keyboard covering half the screen for the speech line.
   - Build 43 (needs the server's PlacementCheck):
     - house placement tool, pick a house: the HUD steps aside, the ghost shows a few steps from you,
       a finger on the ground drags it; it turns red with a reason where it can't go (town, trees,
       out of sight) and back to normal where it can; Place on a clear spot brings the warning, and
       its OKAY works even under the joystick ring; Cancel ends it and brings the HUD back;
     - a server gump's buttons under the joystick ring take taps; the joystick still works elsewhere.
   - Build 44 (phone helpers; a new character gets the defaults):
     - the counter strip shows at the top left with real counts at login (no backpack window opens);
       drag it by its frame to move it; red under 5; double-tap a counter to use it;
     - bandage yourself: "Bandage Ns" counts down under the strip and goes when done;
     - Closest Red / Blue / Monster and Next buttons pick the right colours; after a pick,
       E-Bolt > Last goes to the new pick; a heal button goes to you or your last friend;
     - out of range: "X is out of range" and the cursor stays up; a fizzle doesn't leave a target
       waiting for the next cursor (bandage after an interrupted E-Bolt targets normally);
     - Save Dress 1 / Undress 1 / Dress 1, also from sword+shield to a two-handed weapon and back;
     - Trapped Pouch uses a pouch each press;
     - a macro using while / break / call / waitforjournal runs; stopping it from its button works.
   - Build 45 (helpers, phase 2):
     - macro editor > Presets > Triggers > Auto Cure; get poisoned: it casts Cure on you by
       itself (not while hidden); the Triggers button turns triggers off ("Triggers off.") and on
       (lists them); a looping macro (Mine) pauses while a trigger runs and then carries on;
     - after going gray and taking a step, "Criminal 1:59" counts down under the counter strip;
     - Auto Loot button: "Auto loot on"; kill a monster beside you: its gold and reagents come to
       the pack and no corpse window opens; a blue's corpse is left alone;
     - Record Macro: tap it, cast a spell and tap a target, use a bandage on yourself, say
       something, tap it again: "Recorded 1" opens in the editor, reads sensibly and runs;
     - with the bank box open, Restock tops up bandages and reagents; Organize Gold moves the
       pack's gold into the bank;
     - (server) the item teleporter in the owner's bank: Send to bank box, then Choose a secure
       container and send a bag to it.
     - after relaunching the app and logging in, get poisoned without opening any macro: Auto Cure
       still fires;
     - poisoned and paralyzed with Auto Cure on: break free and cast Magic Arrow from the book -
       it never lands on you ("not targeting yourself with that cursor" if a trigger saw it);
     - auto loot: a monster another player killed is left alone ("Looting this monster corpse
       will be a criminal act!" and nothing taken); nothing is looted while hidden or in war mode;
     - Organize Gold with a corpse window on top still goes to the bank box (never into the corpse).
   - Build 46 (HUD placement):
     - turn to portrait in each layout (Combat, Mage, Utility, and your own): the action buttons keep
       their landscape arrangement as one cluster in the lower right beside the joystick, the top row
       stays a row across the top, nothing lands mid-screen or on the joystick; turn back: unchanged;
     - Edit in landscape, drag a button, turn to portrait: it moved there too; Edit in portrait, drag a
       button: it stays where you put it in portrait; tap it, "Portrait: auto": it follows landscape
       again;
     - "+" adds the new button in a free spot on the right in both orientations (not mid-screen);
       in portrait the "+" sits below the top row and the counter strip;
     - buttons you had placed by hand in portrait before this build are still there after updating;
     - in portrait the counter strip sits below the top row (War, Chat...), not under it;
     - long press on the counter strip: it hides ("Counter strip hidden..."); edit a button, preset
       "Counters", tap it: the strip comes back; relaunch while hidden: it stays hidden.
   - Build 47 (vanity dyes, shop windows):
     - dye a custom-built house from its sign with a house dye: the whole house (floors, walls,
       stairs) turns the new color at once and nothing disappears; walk away and come back (or
       relog): still that color; a classic house (castle, tower) recolors too;
     - say "vendor buy" at the vanity merchant (West Britain bank) in landscape and portrait: the
       buttons over the shop window step aside and Accept, Clear and the arrows take taps; close
       the shop: the buttons are back; same for a secure trade window;
     - with the shop open, the joystick still walks, and a tap on Accept under its ring buys; in a
       secure trade with the window over the joystick ring, the Accept box ticks;
     - with a target selected, the target panel also steps aside while the shop is open;
     - doors, the sign and addons keep their own colour when a house is dyed (they are separate items).
   - Build 48 (furniture deed ghost):
     - double-tap a furniture deed (a stolen sarcophagus or cannon, a ladder after picking its facing) in
       your house: its ghost appears a step in front of you with the Place/Cancel bar and the HUD steps
       aside; drag a finger on the floor: it follows, red with a reason where it won't fit ("a low
       ceiling", "too close to a door", "only inside a house you own"), green "clear" where it will;
       Place sets it there; Cancel leaves the deed in the pack;
     - a ladder in a ground-floor room is red (35 tall under a 20-high floor); in the courtyard it's green;
     - the ghost starts in front of you, not where you tapped the ladder's facing menu;
     - house placement (the placement tool) still works as in build 43.
   - Build 49 (boat helm; needs the server's BoatHelm.cs):
     - buy a small ship deed from a shipwright, place it off a dock, step aboard over the plank, stand next
       to the tiller man and double-tap him anywhere on his figure (the boat's rail is drawn over most of
       him): "You are now piloting this vessel." and the joystick hint; a single tap on him names him, not
       the "ship"; the "Helm" button preset (Edit, a button, Helm) takes the helm within 2 tiles of him and
       leaves it from up to 3;
     - at the helm the stick no longer walks you: a light push sails the way it points, 1 tile a second; a
       full push 4 tiles a second; pointing 90 degrees or more off the bow turns the ship to the nearest
       heading first, within 45 degrees it sails diagonally without turning; letting go stops it at once;
     - hold the stick into the shore: the tiller man says "we've stopped" once, not every second; point
       elsewhere and it sails again;
     - sail, then send the app to the background: the ship stops (at once is a pass: iOS cancels the touch,
       which sends the stop; otherwise within about 2 seconds); come back and push: it sails again;
     - rest your thumb at the light/full push edge, then at the edge of the dead zone: the ship keeps a steady
       pace and keeps moving (no stutter, no stall);
     - double-tap the tiller man again: "You are no longer piloting this vessel." and the stick walks again;
       three tiles from him you can't take the helm ("That is too far away."), nor as a ghost; double-tapping
       yourself at the helm lets go of it too (as a dismount) and the ship stops;
     - with a second player aboard: they walk the deck smoothly while you sail (no snapping back every
       tile), can't take your helm ("... has the helm"), but the ship's owner can; nobody can dry-dock it
       while someone is at the helm. Alone: staff can `[UOMSelfTest boatride sail` (a deckhand sails a
       square while you walk the deck; `[UOMSelfTest boatride end` puts you back). Leave the desktop
       option "smooth boat movement" off: that stock path still snaps a walking player back.
   - Build 50 (each orientation keeps its own button layout):
     - turn the phone to portrait: the buttons sit where they did in build 49; Edit, drag one button, Edit
       again; turn to landscape: nothing there moved; drag a different button in landscape, turn back to
       portrait: every portrait button is exactly where you left it (before, moving a landscape button
       shifted the portrait ones that hadn't been dragged in portrait); the joystick too;
     - drag button X in landscape; turn to portrait: X stays where it was; Edit X, tap "Match landscape",
       close the editor: X moves to the spot worked out from landscape and stays there through further turns;
     - close and relaunch the app: portrait and landscape spots are as you left them; switch layouts with
       Layout while in portrait: each shows where build 49 had it (then stays);
     - "+" in landscape adds a button; turn to portrait: it gets a spot near the others and keeps it;
     - with the counter strip under the top row (War, Chat, ...) in portrait, turn the phone: the strip still
       moves down below the row in each orientation.
   - Build 51 (the server list; needs the Graveyard server's ExtraServers and the zombie server on 2594):
     - log in with auto-login on: the list shows "UO Mobile" (until the rename) and Zombie Survival, rows big enough
       to tap (before, auto-login skipped the list); pick Zombie Survival: a new account is made on first login
       there; the stock character creation, then the new character has every skill at 100 and a spear; log out and
       pick the PvP shard: your old characters and button layout, as before;
     - leave the list open with the phone locked for 2 minutes, then pick a shard: it logs in (the list pings); if a
       shard never answers, "Logging into shard..." has a Cancel button and goes back to the list by itself after 15 s;
     - drop the connection (airplane mode for a few seconds) while in the zombie world: "Connection lost", then the
       login screen; Log In, the list, pick Zombie Survival: auto-login enters the zombie character (the app runs
       without -reconnect, so there is no automatic reconnect).
     - after the PvP shard's rename to "Graveyard Battles" (done on the server once 51 is approved): log in there with
       a character that had a custom button layout, macros and window spots under "UO Mobile": all still there.
   - Build 52 (Zombie Survival building; needs the zombie server's BuildLines.cs):
     - a character on Zombie Survival that never saved a button layout starts on "Survival": Attack Nearest, Bandage,
       Heal Pot, Build, Next Target, Towns, Vendors, Arm/Disarm, Pack under the system row, in landscape and portrait;
       Layout goes on to Combat, Mage, Utility; a Graveyard Battles character's layouts are as before (no Survival);
     - Build opens the build menu; pick the palisade E-W: the ghost and a bar with Place, Turn and Cancel; Turn walks the
       ghost through north-south, corner, post and back (the menu's highlight follows); doors and spear walls turn two ways,
       stairs four, a walkway has no Turn;
     - tap a spot: the ghost goes there and Place builds one piece, as in build 51; quick taps near a tile's edge, with a
       little finger roll, never build anything by themselves (a line needs about half a tile of drag);
     - drag a finger across the ground: a ghost on every tile from where it went down, along the drag's longer way (mostly
       east-west: east-west palisades; mostly north-south: north-south ones), red where one can't go (a person, a wall, out
       of sight, too far), the bar reads "Lift to build 7 of 9 - 2 can't: ..."; lift: they are all built at once with one
       hammer sound, the journal says "Built 7 of 9 (2 blocked: ...)", and the cursor is back with the ghost on the next
       tile along;
     - drag back onto the first tile, or lift over the bar: nothing is built ("No line laid"); a drag longer than 24 tiles
       shows 24 ("the most one line lays"); a second finger while dragging zooms and lays nothing; pull down Control
       Center (or take a call) in the middle of a drag: nothing is built;
     - standing on a raised walkway, drag a palisade line along it: the walls stand on the walkway, not on the ground under
       it; a tap on the walkway puts the one ghost on it too;
     - a walkway drag lays a strip of floors 20 up; standing on a walkway on top of a wall (stairs up to it), a walkway drag
       along the wall lays the rest of the wall top; with stairs a drag only moves the one ghost;
     - a hidden player standing in the line's way: the ghost there isn't red (the answers don't give hidden players away),
       but that piece isn't built ("1 blocked: something in the way"); the same for another player's hidden blast trap;
     - a Zombie Survival character that already had saved buttons (build 51) finds "Survival" first in the Layout cycle,
       still on the layout it was using;
     - Edit > a button: the presets Build, Towns, Vendors and Heal / Cure / Refresh Potion work (a potion needs a free hand:
       Arm/Disarm puts the spear away);
     - the build menu's Stop ends the cursor and the bar goes (before, the bar stayed up);
     - Graveyard Battles: house placement and furniture deeds exactly as in build 51 (no Turn, a drag moves the ghost).
   - Build 53 (Zombie Survival map, alerts, repair all; needs the zombie server's PhoneMarkers/PhoneAlerts/RepairAll.cs):
     - a Zombie Survival character that never saved buttons starts on "Survival" with two more buttons, Map and Repair All
       (top right, above Vendors/Towns; in portrait above the cluster); a character that already has Survival keeps its
       buttons as they were (Edit > a button: the presets "Repair All" and "Map" add them);
     - Map: the HUD steps aside and the map fills the screen; the first time per launch "Drawing the map..." for a few
       seconds, then the land around you with You on it; a finger drags it; - and + zoom (the first - after the widest
       shows the whole facet, centred); a double tap zooms in there; You centres on you again and follows; Close closes
       it and the HUD comes back; turn the phone with the map open: it refits;
     - on Zombie Survival: a legend; every town coloured by its state (red overrun, yellow cleared, green held) with its
       outline when zoomed in; the six peddlers in orange (blinking red-orange while running from the dead); your camp in
       gold (blinking red ring for 2 minutes after it is hit), a friend's camp in blue; tap one: its name, state, distance
       and direction in the bar; "Markers N s old" never passes ~32 s while the map is open (it asks every 30 s);
       Graveyard Battles: the same map, no legend, no markers;
     - alerts: a banner just below the top row of buttons (landscape: between the counter strip and the buttons on the
       right; portrait: below the counter strip), never over a button, with a sound - a horde marching on a town you are
       at or camped by (war drums), your camp's wall or fire bashed by a zombie or a stranger (a zombie's moan; once per
       90 s per camp), a town held (fanfare), cleared, or fallen (drums) for everyone, a tripwire; the same line is in the
       journal; a tap on the banner reaches what is under it (a button, the world); several at once queue (3.5 s each);
       landscape and portrait, and with the map open (then at the top);
     - Repair All among damaged walls of yours or of a camp that lists you as a friend: "You repair all N damaged pieces
       within reach." with one hammer sound; each gets a single repair's worth for a single repair's boards or ore (not
       with free building); again within 5 s: "Catch your breath..."; with no ore: "... (1 still needs ore)"; a
       stranger's wall nearby is left alone;
     - the map on the oldest phone in use: open it, drag about and zoom for a minute (one ~29 MB picture, ~36 MB more while
       it is drawn): no crash, no hitch when it appears;
     - a build-52 app on the same server: plays as before (no banners, the alert lines in the journal).
   - Build 54 (taps on windows, the editors' legibility; the zombie server's build 54 menus and kits):
     - held upright, open the build menu ([build or the Build button): the top row of buttons (Peace/War, Chat, Cancel,
       Edit, Layout) steps aside wherever the menu covers it - not drawn, not pressed; tap the menu's Wood / Stone / More
       tabs: the menu answers and war mode, the chat line and Cancel do not change; close the menu: the buttons are back
       and work; the same with the More menu (its tabs) and My blueprints, and held sideways over the joystick's corner;
     - any window (backpack, paperdoll, a shop) over a button: the button steps aside while the window covers it; drag
       the window off it: the button is back; every button no window covers works as before (Pack, Build, Attack Nearest);
     - drag the build menu and the backpack by a finger on them: they move (the HUD never takes the drag);
     - the target panel under a window doesn't take a tap there; the joystick still steers under a window (only a
       window's button or text field under it takes the tap, as before);
     - Edit mode: buttons still drag and edit under windows;
     - the macro editor (Macros button) and a button's editor (Edit > a button): solid dark window, steps and choices on
       plates in the bigger font, the name and line fields dark with white text; type into both fields (the keyboard
       comes up, the text shows), pick steps, scroll, Save, Run, Close; landscape and portrait;
     - the server's build menus (Zombie Survival): big white and gold text on black, nothing cut off or wrapped (E-W,
       N-S, the stairs' N E S W, Take down, the More tabs); the More menu's X is bottom right; Plans: Watchtower,
       Bunker, Warcamp with piece counts and "Your pieces: N of 400 standing"; place each (the ghost green only where
       all of it fits; a warcamp over a stranger's wall says why and builds nothing).
   - Build 55 (large menus; the servers' new phone menus - rows on plates, "Page 2/3", the Towns and Vendors menus - need
     servuo's PhoneMenu.cs / PhoneLists.cs; the zoom, the taps on a button's words and the X work on an old server too):
     - held sideways and upright, open the build menu, the camp menu, a survivor's duties, the leaderboard, Towns and
       Vendors (Zombie Survival), a shop's buy list (say "vendor buy"), skills, the spellbook, status and the paperdoll:
       each opens larger than in build 54 and wholly on the screen (nothing off an edge); text reads at arm's length;
     - on a server menu tap a row's words (not its little arrow), a row's dark plate, and just beside a small button:
       each presses that row's button; a tab's word changes the tab; Prev / Next turn the pages ("Page 2/3");
     - drag a server menu by its words: it moves (no button pressed); a long press on it still closes it;
     - held sideways with a menu over the joystick: a finger on the ring (not on a menu's button) still walks; the
       menu's buttons there take taps (as in build 43);
     - a text field in a zoomed menu (clan name, blueprint name, a stall's price): the keyboard comes up and the field
       stays in sight above it (the keyboard area follows the zoom), sideways and upright;
     - pinch a server menu smaller, close it and open it again: it keeps your size; pinch it back to its first size: it
       snaps there; a menu you had pinched in build 54 opens at the new size once (old server-menu zooms are dropped);
       pinched large sideways then turned upright, it still fits the screen;
     - a server menu with no close button (an old server's carpet or dye menu) has a red X at its top
       right; the X closes it (the server hears it closed, as a right click);
     - upright, the macro and button editors and UO's options still widen the screen as before (options' drop-down
       lists open in place); a server menu wider than the screen (the help menu) is shrunk to fit instead and the HUD
       stays as it is;
     - Graveyard Battles: the vanity merchant's shop, the item teleporter, a carpet deed's size menu and a vanity dye
       read and work as above; the HUD buttons a window covers still step aside (build 54);
     - (new server) every custom menu: title and X at the top, tabs under it, one action per row, Back bottom left,
       "Page 2/3" with Prev / Next bottom right; the build menu's Stop and the other tools are on its page 2 (build 52's
       Stop check); a build-54 app on the same server still works (smaller, the arrows take the taps).
4. **Agent audit**: an agent reads this file plus `git diff tf-<last>..HEAD` and reviews the
   change against every category below. It also re-checks the whole startup, login and world
   path for any category the diff touches. Its findings are fixed or consciously accepted before
   shipping.
5. After shipping, `git tag tf-<build> <sha>`. When the phone turns up a new failure, add it to
   **Known failures** and, if a grep can catch it, to `preflight.sh`.

## Categories to check

- **APIs that throw PlatformNotSupportedException on iOS/Mono**: Console members other than
  Write/WriteLine/Out; Process; named sync objects; Pipes; Registry; Ping and ICMP; Reflection.Emit;
  Assembly.Load*; runtime loading of files. Guard them, or give them an iOS path.
- **Paths**: every write must land under Documents (`CUOEnviroment.ExecutablePath` is the working
  directory, and SDL's UIKit startup changes it to the read-only bundle). Nothing may assume files
  shipped next to the executable unless they are embedded.
- **Native code**: every `DllImport`/`LibraryImport` target must be statically linked (SDL3,
  FNA3D, FAudio, libtheorafile) or guarded. There is exactly one DllImport resolver per assembly
  (FNA's own on FNA.dll). All linked frameworks are listed in `MtouchExtraArgs`.
- **iOS-only substitutes**: an alternative implementation chosen on iOS (zlib, compressors, file
  access) must be run on desktop via an env flag. A code path that is "never used on desktop"
  is untested.
- **Substitute contract**: an iOS replacement must match the original on empty input, short
  output and error codes, not just the happy path (DotNetZLib versus native `uncompress`).
- **Launch watchdog**: everything before the first frame (`UO.Load` runs in LoadContent on the
  main thread) must stay well under about 20 s, or iOS kills the app with 0x8badf00d and no
  crash.txt is written. Check "Files loaded in N ms" in the console log.
- **SDL3 hints and properties**: use SDL3 names (`SDL_ORIENTATIONS`, not the SDL2 `SDL_IOS_*`).
  Check the value types.
- **Screen and UI**: the backbuffer must follow the screen (scenes resize it on desktop, and on
  rotation FNA flips the device but not `PreferredBackBuffer*`, so it is compared every frame).
  The UI scale must give 480 units on the short side in game, and contain 640x480 on the login
  screens and while a window wider than 480 is open in portrait. HUD positions must stay inside
  `SDL_GetWindowSafeArea` in both orientations, and windows must be pulled back on screen after
  a rotation. Info.plist and the `SDL_ORIENTATIONS` hint must list the same orientations.
- **Saved data from older builds**: a new field in a saved file (layouts, macros, profile)
  needs a migration for files written before it existed. Missing is not the same as default:
  build-31 layouts have no portrait positions.
- **FNA on iOS** (`supportsOrientations`): FNA shapes the backbuffer from its last orientation
  *event*, not from the window. `PreparingDeviceSettings` forces the window's shape, and
  `FollowPhoneRotation` also compares the device's `PresentationParameters`. FNA overrides the
  `SDL_ORIENTATIONS` hint, so Info.plist is the only orientation gate. No resize event reaches the
  game (the window isn't resizable), so iOS size changes are polled.
- **Input**: text input has the correct keyboard type, capitalisation and autocorrect; the
  keyboard is raised on demand; nothing is hover-only or right-click-only on a required path.
  Multi-touch: each finger has exactly one owner until it lifts. A gesture that takes over a
  finger (pinch) must cancel that finger's click without dropping another finger's queued
  release, since a lost mouse-up leaves the button held. A press is only taken over *before the
  game has seen it*. Presses on windows are held back for up to 130 ms, or until the finger moves
  or lifts, so a second finger can claim them for a window pinch. Once a window press has reached
  the game, never cancel it: its control may already be pressed, scrolling, dragging or
  resizing, and forgetting the press leaves it stuck (scroll arrows keep scrolling, a skill row
  blocks health-bar pulls). A world press may still be taken over after dispatch, by releasing it
  off-screen. A finger landing mid-gesture must not click.
  Accepted: the first finger's press reaches the game after 2 frames, so a pinch that starts
  within the double-click time of an earlier tap can still double-click.
- **Audio**: give FAudio 48 kHz material. It upsamples with linear interpolation, and for UO's
  22 kHz originals that leaves images only ~13 dB down (hiss, "static", on a phone speaker).
  Music is re-encoded on the server (`music-48k.sh`). Sound effects are resampled on load with
  a windowed sinc (`ClassicUO.IO/Audio/Resampler.cs`, images ~83 dB down).
- **Game-file downloads** (Touch/GameFiles, DownloadScreen):
  - Nothing may read the UO files before the download screen is done. That includes startup
    checks: Main's "UO directory invalid" check killed a fresh install before it could download
    (desktop test, build 34).
  - A partial set never reaches UO.Load: installed.json is only marked complete at the end.
  - Every file is checked against the manifest's sha256; resume uses HTTP Range.
  - Manifest paths are confined to the UO folder (no `..`, nothing absolute).
  - The manifest is fetched with no-cache (Caddy also sends it), so updates are seen.
  - The file server must be HTTPS (iOS blocks plain HTTP).
  - Downloads go to Library/Application Support, excluded from iCloud backup. A manual copy
    in Documents is adopted (moved) when its hash matches.
  - While the screen runs there is no scene and no world. Every SDL input case that reaches
    Scene or World must be guarded.
  - Logs never contain passwords (the ARG trace redacts them; the phone's log is in Documents).
- **Per-window zoom** (Touch/GumpScale): a zoomed window is drawn in its own pass. That pass
  must stay in true bottom-to-top order, because translucent pixels above it (the HUD) otherwise
  block it through the depth buffer. Every pointer event is mapped into the zoomed window's own
  layout, and hit testing must go through the same mapping (`HitTestGumps`), so the game and
  the finger agree on what's under it. While an item is carried, the window under the finger is
  re-checked on every event, so a drop lands under the finger. Anything new that reads
  `Mouse.Position` to *draw* at the finger (held item, tooltip, target cursor) must use
  `TouchInput.CursorPosition`.
- **Server-era features** (house designer): the shard runs pre-AOS rules, so the client's
  feature flags hide anything newer. Touch builds unlock what the shard deliberately allows
  (every custom-house piece). A piece the client offers must also be in the server's
  `Data/Components` lists, or the server drops it and it silently vanishes. A number the client
  estimates locally (the designer's cost label) must match the shard's rules: 1 gold per commit.
- **First-open stalls**: a window that builds many images or labels the first time it opens stalls
  that one frame (the magery spellbook: 69 images, ~430 ms on desktop). Measure with the dev
  `perf SECONDS` command, then load its art ahead in Touch/GumpWarmup (drawn frames only, a small
  per-frame budget, never in the background); leave out single images that cost a visible hitch
  on their own. Anything that runs from Update must skip the frame limiter's idle ticks.
- **Lifecycle**: no GPU work in the background; everything saves on DID_ENTER_BACKGROUND; saves
  are write-then-rename; a corrupt file must not crash every launch.
- **Network**: connects are bounded (no multi-second block on the main thread); the Local
  Network permission is requested before the first real connect. Server addresses are host
  names. On IPv6-only NAT64 networks (Apple's review network, some carriers without 464XLAT)
  only a DNS lookup yields a synthesized IPv6 address; a raw IPv4 literal fails.
- **Threads**: SDL, UIKit, FNA and gump work happen only on the main thread (FakeMain's thread).
- **Memory**: no large one-shot allocations (the world map builds a ~120 MB image); UO files
  are memory-mapped.
- **Packaging**: Info.plist purpose strings cover every API SDL references (camera,
  microphone, Bluetooth); the icon is 1024 px with no alpha; the build number increases.
- **Build and CI**: use the real Xcode directory (not the `_x.0` symlink); keep the fnalibs
  cache key in sync with the lib patches; the symbol check runs; no `nm | grep -q` under
  `pipefail`.
- **Editing hazards**: a Python or heredoc edit containing `\n` can write a literal newline into
  a C# string or YAML, and `\b` can arrive as a backspace character (a regex word boundary
  became 0x08 in build 38's server work). Build and YAML-validate after scripted edits, grep the
  changed files for control characters (`grep -P '\x08'`), or write edit scripts with a file tool.

## Known failures (newest last)

| Build | Symptom | Cause | Fix |
|---|---|---|---|
| 14-16 | abort at launch | our resolver + FNA's: `CannotRegisterSecondResolver` | never register on the FNA assembly |
| 17 | `EntryPointNotFoundException: SDL_SetHint` | `<NativeReference>` items were ignored, so no SDL in the binary | `MtouchExtraArgs --gcc_flags -force_load`, plus the CI symbol check |
| CI | `xcrun -find clang++` exit 16384 | `Xcode_26.x.0.app` is a symlink | `pwd -P`, `ValidateXcodeVersion=false` |
| 18 | undefined CoreMedia/CoreGraphics symbols | frameworks missing | the full SDL3 framework list |
| 19 | `ld: pointer not aligned` in F3DAudio | packed structs | `aligned(8)` patch in build-ios.sh |
| 21 | App Store ITMS-90683 | no camera/Bluetooth purpose strings | added to Info.plist |
| 22 | `CannotRegisterSecondResolver` in `DllMap.Init` | ClassicUO's desktop DllMap | skipped on iOS |
| 25 | `PlatformNotSupportedException` | `Console.ForegroundColor` in Logger | guarded |
| 28 | `UnauthorizedAccessException` on settings.json | SDL chdir to the bundle | chdir back in RealMain |
| 29 | "UO directory invalid" | files in `Documents/ios-data/uo` | recursive `FindUoData` |
| 29 | `Exception: CRC mismatch` | the iOS-only switch to ClassicUO's `ZLibManaged` (broken, never used on 64-bit) | `System.IO.Compression.ZLibStream`, verified on desktop with `UOM_DOTNET_ZLIB=1` |
| audit | (pre-ship, build 31) | DotNetZLib threw on empty input; FindUoData could pick up `.Trash`; the log listed the wrong folder; preflight `none()` passed when grep errored | contract guards; skip dot-folders; log the chosen folder; exit code 2 now fails the check |
| 31 | no music | the Apple Devices folder copy wrote 0 KB mp3s | build 34: the app downloads the music (48 kHz) with the game files |
| audit | (pre-ship, build 32) | build-31 `mobile_layouts.json` has no portrait positions, so derivation put Attack Last on the joystick; editors are 520/640 wide against a 480-wide portrait UI | fill from the default layout by action; widen the fit while a wide window is open |
| audit | (agent, build 32) | a launch held in portrait likely got a landscape backbuffer (FNA orientation shaping); a pinch started on a window threw it off-screen; fingers landing mid-pinch clicked; macro-bar buttons had no portrait position; a null layout crashed every login; the desktop rotation test used a path iOS never takes | `PreparingDeviceSettings` forces the window's shape; pinch only from world presses; re-pinch and ignore extra fingers; PX/PY on every new button (preflight); drop nulls on load; the desktop resize handler stands aside under `UOM_PHONE_FIT` |
| audit | (agent, build 33) | a resting finger's tiny motion turned a window pinch into a window drag; cancelling a dispatched window press left controls stuck; windows dragged out of a zoomed window opened away from the finger; name plates could be zoomed; the aura drew away from the finger | hold window presses until move, lift or 130 ms, and pinch only while held; a drag of another window drops the mapping; world-anchored windows and health bars are not scalable; the aura and range text draw at `CursorPosition` |
| desktop | (pre-ship, build 34) | a fresh download install showed "Your UO directory is invalid" and quit | Main's folder check waits for the download screen when a file server is configured |
| audit | (agent, build 34) | iOS's default HTTP handler caches small files (an update could get a stale copy); UO.Load could run while backgrounded; a leading "/" in a manifest path escaped the folder; a wrong manifest could delete a good install; each app switch used up a retry; quitting during the download crashed in Unload; the console log could hold a password | the managed SocketsHttpHandler everywhere and no-cache on the server; load only in the foreground; full-path confinement; manifest sanity check, deletions after success, re-check by hash after a load failure; progress resets the retry count; `FileManager?.Dispose()`; redacted args |
| 34 | static behind every sound effect (music fixed) | FAudio linear-upsamples the 22 kHz effects | effects resampled to 48 kHz on load (windowed sinc) |
| audit | (pre-ship, build 37) | the designer hid every piece on the pre-AOS shard and priced pieces at 500 gold; the client offered Celtic walls the server lacked; server side, a corpse could be stolen whole, stolen spawned chests came back twice (spawner + respawn marker with fresh loot) and 1-gold deeds sold back for their old price | touch builds offer every piece and show 1 gold; Celtic rows added to the server's lists; corpses protected, respawns are plain statics and skip spawned items, deeds sell for 80% of 1 gold (nothing) |
| audit | (agent, build 38) | the spellbook warm-up ran on every Update tick, including the frame limiter's idle ticks, so the per-frame budget never applied; logging out mid-warm-up skipped the start delay on relogin; the book background alone cost ~47 ms in one frame; at 1.84 zoom-out the screen corners pass the 24-tile view range (objects pop in there) | warm-up only on drawn frames (after the limiter, not in the background), re-armed on every login, 6 ms budget, book background left to the open, totals logged; corner pop-in noted for the owner |
| CI | (run 38) | MT4162: iOS 27 types "not available in iOS 26.5" | `dotnet workload install ios` took workload set 10.0.401.1 (iOS SDK 27.0, needs Xcode 27; the image tops out at 26.6) | workload set pinned (`--version 10.0.401`); move the pin together with the Xcode selection |
| audit | (agent, build 41) | the login re-fit followed every safe-area change, so iOS sliding the view up for the keyboard would have re-fitted and put the password box behind it (landscape); the login re-fit could reset the device in the background; the stealth counter never came down on a desktop client without touch; a window opened by a tap drew one frame unplaced | while the keyboard is up only a rotation or a new screen re-fits; nothing re-fits in the background; the counter is touch-only; placement runs again after taps. Server (146052d): auto stealth ran in the house designer (endless AFK gains), outsiders could lift loose items 2 tiles into a house once its contents showed, a guildmate could start a hidden player's Invisibility reveal timer | designer excluded, outsiders keep the old reach (steal 1 tile only), no Invisibility on someone hidden, one Stealth gain per 10 s |
| desktop | (pre-ship, build 42) | build 41's login layout moved every window in the login scene, and the touch HUD is created there while the world is entering, so the HUD (labels, target panel) kept the login offset | only the login flow's own windows (Login/CharCreation namespaces, credits, message boxes, the colour picker) are placed |
| audit | (agent, build 42) | the raised speech line sat in WorldViewportGump.Contains' tap-through area, so a tap on it closed the keyboard and clicked the world; nothing caught a missing keyboard report (the line would sit under the keyboard) | the tap-through area is measured from the speech line's own height; no report within 0.7 s gives the line SDL's slide again (logged), and the first reports are logged |
| audit | (agent, build 43) | a boat's placement cursor showed the house ghost red and refused Place (the server only judges houses); the joystick exception let health bars, journal and paperdoll controls steal walk presses; surface statics were asked about at the wrong z (false 'blocked'); a disconnect mid-drag left the ghost finger set (a reused touch id lost its release); a mobile under the finger carried the ghost off; big houses started out of range | no-opinion answers keep the ghost plain and Place allowed; only server gumps' buttons/check boxes/text fields beat the joystick; the query adds a surface's height like the real target; the finger resets on unload and on a normal press; anchors become the ground under them; the start is capped at 9 tiles; pinch works while placing |
| audit | (agent, build 44) | Last Target kept an old harmful/beneficial memory over a new pick (Dump E-Bolt broke after its first kill); the target queue answered any later cursor (a bandage could hit the enemy); dress left a weapon swap half done; the default counter strip was editable so a drag tore counters off; a macro button couldn't stop a macro inside a call; dress sets were written in place | picks feed the memories (harmful for foes, beneficial for friends; dead/missing skipped; reset on logout); only spell buttons queue, only for a harmful/beneficial cursor, cleared by a fizzle or any other action; both hands cleared as needed; the strip is created locked and without explosion potions; the root macro's name decides the stop; ConfigurationResolver.Save; unknown item names fail at validation |
| audit | (agent, build 47) | the trade window's Accept is a check box and the shop's +/- are buttons, so under the joystick ring a tap walked instead; the target panel still drew over a shop window and took taps; the stepped-aside rectangles survived a logout; a dye landing while in the designer recolored its floor grid, and pieces placed in the designer showed undyed | every interactive control in a shop/trade window beats the joystick; the target panel steps aside too; cleared on unload; the designer's grid keeps its colours and new pieces take the house hue. Doors, the sign and addons are separate items and keep their own colour |
| audit | (agent, build 48) | a deed's pieces were never forgotten, so after a server restart (cursor ids start over) a spell's cursor could take the furniture ghost; preview tiles destroyed by a map change stayed invisible; server: a probe addon saved mid-placement would stay in the world, and spot answers anywhere on the map could reveal hidden players | pieces dropped with their cursor and on logout; previews rebuilt; probes dropped before saves and at shutdown; answers only within 24 tiles and in a house the player co-owns |
| audit | (agent, build 49) | speed and stop had no hysteresis, so a thumb on the light/full or dead-zone edge restarted the boat's step timer every 150 ms (stutter, a slow boat never stepping); the tiller-man pick-through used stale screen positions of far tiller men and took a double-tap on yourself on a dock; a phone check said the helm could be taken from 3 tiles (2) | fast from 0.62, slow again below 0.52, once sailing stop only below 0.15; pick-through only within 4 tiles; the self double-tap goes to the tiller man only at the helm or on a deck; wording fixed, preflight pattern tightened |
| audit | (agent, build 52) | a tap that rolled across a tile edge laid a two-piece line (no slop before a line started); a touch the system cancels (Control Center, a call) was a lift, so it built the dragged line; the ghost's answers (line and single spot) showed where hidden players and someone's hidden blast traps stand; from a raised walkway the ghost and the line used the ground under it; a line over a mound found no surface; overlapping async build suites could leave free building off; stale line answers across a new cursor; build-51 zombie characters with saved buttons never got Survival | half a tile of travel before a line starts; FINGER_CANCELED reaches TouchInput as canceled and a canceled world finger lifts off-screen; answers count only what the asker sees (placing still fails on it); floor items are surfaces for build cursors (Place targets the item); the ground up to 25 above the line's height; a hold counter; the sequence moves on with the cursor; Survival inserted once (preflight) |
| audit | (agent, build 53) | the alert banner, made at login, drew under the map (added later, nearly opaque) and under windows opened after it (the stock sort moves one Over gump per pass); a long press - a right click on the phone - closed the map; the banner could flip between two spots as its text re-wrapped; a refresh lost the map's selection; held/cleared towns rang red; server: 0x7A60 registered as in-game (a sub-command before the character drops the client), town banners chosen by parsing the broadcast's words, a stranger's wall beside a camp or a friend's pet raised the camp's alarm, the login push could be throttled | the banner is made topmost while it shows; the map never closes on a right click; the placement is kept until the screen, HUD or text changes; the selection is found again by kind and name; rings take the town's colour; server: not in-game, banners by what changed (state, hordes), only the camp's people's pieces, a pet's blow is its master's, the login push is never held back |
