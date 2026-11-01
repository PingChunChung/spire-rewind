# Spire Rewind

A mod for Slay the Spire 2 that keeps a checkpoint every time the game saves your single-player run, lets you rewind to an earlier floor from the pause menu or the main menu, logs the choices and fight decisions of every floor, and exports the whole run as a single offline HTML page.

Version 0.1.0, built against game v0.107.1. Early release: it has been played through on an isolated copy of the game with the Steam backend off, but **not with Steam Cloud sync**. Read [What is restored](#what-is-restored-and-what-is-not) and [Limits](#limits) before using it on a run you care about.

## What it does

- **Automatic checkpoints.** Whenever the game writes its own run save (entering a room, finishing a room, the regular autosave), the mod copies the full native save into the profile's `rewind/` folder with a small metadata file: act, floor, room, HP, gold, deck size, game version, mod list and a SHA-256 of the payload. Nothing is changed in the game's own files at this point.
- **Rewind.** The pause menu and the main menu (when a run is in progress) get a **Rewind** entry. The dialog lists the checkpoints of the current run; pick one, read the warning, confirm. The mod first saves a *recovery checkpoint* of the current save, then replaces the run save through the game's own save interface and reloads the run. If the load fails it tries to put the original save back and tells you what happened.
- **Choice log and decision-point snapshots.** Card rewards, shop purchases, card removals and event options are appended to a per-run `choices.json`. During fights the mod records each stable decision point (turn, HP, block, hand, cards played since the last point, enemy intents) next to the game's own combat replay file.
- **Offline replay export.** **Export this run replay** writes one self-contained HTML file to `rewind/exports/`: a per-floor timeline, HP/gold/deck curves, the fight with the biggest HP loss, every choice, and a decision-by-decision view of each recorded fight. It opens in any browser without the game, a network or any install. The same page can be produced from the command line with `tools/export-replay.py`. A fixed fictional example is in [docs/sample-replay.html](docs/sample-replay.html).

The mod has no telemetry and no network service of its own, and needs no other mod. A rewind writes the run save through the game's own save system, which Steam Cloud may sync like any other save; checkpoints, archives and exports stay on this machine.

## Install

Install it one way only. With the same mod both in the Workshop and in `mods/`, the game loads only one of the two copies.

**Steam Workshop.** Subscribe and start the game. The first time the game finds a new mod it asks whether to load mods; allow it.

**Manual.**

1. Close the game.
2. Unzip `SpireRewind-<version>.zip`. It contains one folder, `SpireRewind/`, with `SpireRewind.dll`, `SpireRewind.json`, `LICENSE` and a short `README.md`.
3. Move that folder into the game's `mods/` folder so that you have `Slay the Spire 2/mods/SpireRewind/SpireRewind.dll`.
4. Start the game and allow mods to load.

A modded game uses a separate set of save files (`modded/profileN`), so your unmodded saves and progress are not touched; they also do not appear in the modded saves.

**Temporary disable.** Turn the mod off in the Modding tab of the game's settings, or start the game with the `-nomods` launch option.

**Removal.** Unsubscribe, or delete `mods/SpireRewind/`. Checkpoints and exports stay in the profile's `rewind/` folder until you delete them.

**Game versions.** Minimum game version 0.107.1 (`min_game_version` in the manifest; an older game does not load the mod), tested only on v0.107.1. On a newer game version the mod may or may not load; if it loads, the Rewind dialog shows a notice. A restore always refuses a checkpoint taken on a different game version than the running one.

## How to use

**Rewind.** Open the pause menu during a run, or the main menu while a run can be continued, and choose **Rewind**. Each row shows act, floor, room, kind of checkpoint, HP, gold, deck size and the time it was taken; one floor can have several rows.

- *Entering room* is the save taken before the room starts. For a question-mark node the room is still undecided; the mod never draws the room early just to show its name.
- *Room completed* loads with the room already finished (the state the game itself saved), so it cannot be used to replay that fight. To replay a fight, choose its *Entering room* checkpoint.
- *Recovery checkpoint* is the copy of the save the mod made right before a rewind.

Confirm the selection in the second screen. Unsaved actions of the current room are lost. If the dialog reports that the original save was recovered, or that the game should be restarted, follow that message and keep the `rewind/` folder. When neither the chosen checkpoint nor the original save could be verified, the message names the recovery checkpoint as `backup=<id>`, no further rewind is possible until the game is restarted, and the dialog keeps showing that id; see [Recovering a save by hand](#recovering-a-save-by-hand).

**Settings** (same dialog): enable or disable checkpoints and rewind; how many recent runs to keep (1 to 100, the current run included; lowering it deletes the oldest runs when the settings are saved, also when no run is active); whether rewound runs are marked in the mod's own records; interface language (follow the game, Traditional Chinese, Simplified Chinese, English). Disabling stops new checkpoints, replay archives and rewinds; existing data is kept.

**Export.** **Export this run replay** exports the current run; **Export saved run replays** lists the retained runs. The dialog shows the output path. From a terminal, with Python 3 and no extra packages:

```powershell
python tools/export-replay.py '<profile folder>' '<run id>'
python tools/export-replay.py '<profile folder>' '<run id>' --out 'my-replay.html' --language eng
```

`<profile folder>` is the folder that contains `rewind/`; `<run id>` is one of the 64-character folder names under `rewind/runs/`. `tools/localize-replay.py` can replace card, relic, monster and other names in an exported page with the game's official Chinese names, read from your own game files.

**Combat archive.** The main menu's Rewind dialog can also open the combat archive: the archived `.mcr` replays of the retained runs with their version and integrity checks, and a read-only player for the recorded decision points (turn and HP only, no card animation; the last point is the last one observed, not necessarily the end of the fight). Native combat playback stays blocked.

## What is restored and what is not

A checkpoint is the complete native run save, so everything the game saves is restored: map, deck, relics, potions, gold, HP, the room state the game had saved, and the game's random-number seeds and counters. With the same game version, mods and actions, draws and enemy behaviour are expected to repeat.

**Progress, unlocks, statistics, achievements and run history are not rolled back.** The mod never deletes or rewrites those files and does not stop the game from recording later play, so a replayed fight can count again in the statistics. The optional "rewound run" mark lives only in the mod's own `rewind.json`; it never appears in the native history or on leaderboards.

Checkpoints follow the game's save points; you cannot return to an arbitrary turn. State that other mods keep outside the save, timers, and anything the game does not save are outside the rewind.

## Limits

- Single-player standard and custom runs with a current save only. Daily runs, multiplayer, combat replays, ended runs and imports from other runs are not supported.
- A restore is refused when the game version, save schema, character, run identity or mod version list does not match the checkpoint.
- The mod rewrites the active run save through the game's own save system. It keeps a recovery checkpoint first and verifies what it wrote, but **it has not been tested with Steam Cloud sync yet**. Back up your save folder before using it on an important run, and expect cloud conflicts to be possible until that test exists. On the Steam backend the restore checks that the local and cloud copies agree, writes, reads back and checks the persisted state; it may refuse when offline, over quota or when the copies differ. The manual test the release still needs is listed in [docs/TESTING.md](docs/TESTING.md#steam-cloud-manual-acceptance).
- Checkpoints, archives and exports are stored only on this machine under the current account and profile (`modded/profileN/rewind/`); they are not synced. The number of retained runs is capped, the size of a long run with many rewinds is not. One checkpoint is at most 32 MiB, one replay archive 64 MiB.
- The game's own decision history is recorded only at stable decision points; a card that ends a fight may be missing from the snapshots, and the recorded choices do not form branches. Choices made again after a rewind are appended, with the earlier attempt kept.

## Recovering a save by hand

Everything a recovery needs is in the profile folder of the modded game. On Windows that folder is

```
%APPDATA%\SlayTheSpire2\<platform>\<user id>\modded\profile<N>\
```

where `<platform>` is `steam` when the game runs through Steam (otherwise `default`), `<user id>` is the numeric account id and `<N>` is the profile number (1 for the first profile). Inside it:

- `saves\current_run.save` is the run save the game loads with **Continue**.
- `rewind\runs\<run id>\checkpoints\<checkpoint id>\run.save.json` is one checkpoint: the complete native run save, byte for byte as the game wrote it. `meta.json` next to it has `capture_kind`, `act`, `floor`, `hp`, `captured_at_utc` and `run_sha256`, the SHA-256 of `run.save.json`.
- `rewind\runs\<run id>\rewind.json` records the last rewind of that run.

**Which checkpoint is the recovery checkpoint.** The copy the mod made right before a rewind has `"capture_kind": "before_restore"` in its `meta.json`; the dialog lists it as *Recovery checkpoint*, and a failure message names it as `backup=<checkpoint id>`. After a failure that could not be rolled back, the mod refuses further rewinds and also disables the main menu's **Continue** until the game is restarted, because the save on disk is unverified; the Rewind dialog keeps showing the backup id meanwhile. With several, take the one with the latest `captured_at_utc`. Any other checkpoint can be restored the same way; `room_entry` is the state before the room, `room_completed` the state after it. To check that a file is intact, compare its SHA-256 (`Get-FileHash run.save.json` in PowerShell) with `run_sha256`.

**Restore it with the game closed.**

1. Exit the game completely, not only to the main menu.
2. Copy `saves\current_run.save`, if it still exists, somewhere outside the game's folders.
3. Copy the checkpoint's `run.save.json` over `saves\current_run.save` (same name, same folder).
4. Start the game and choose **Continue**.

The same steps apply when the Rewind dialog cannot list the run because the current save is unreadable: the run is the folder under `rewind\runs\` with the newest checkpoints. Progress, unlocks and statistics are separate files and are not touched by this.

**Steam Cloud.** With cloud sync on, Steam may detect the hand-written file at the next launch and show its sync conflict dialog; choose to keep the local files. To avoid the dialog, turn off Steam Cloud for the game in its Steam properties before editing and turn it back on afterwards.

## Verification status

Tested on 2026-11-01 on an isolated copy of game v0.107.1 (Ironclad, Ascension 0, Steam backend off, Spire Link loaded alongside as the test driver): both mods load without errors; checkpoints on room entry and room completion match floor, HP, gold and deck; `choices.json` records card reward, shop purchase, card removal and event option; decision snapshots carry block, hand, played cards and intents; rewind from the pause menu and from the main menu, including the recovery checkpoint and `rewind.json`; the replayed fight repeated every decision point (15/15 and 14/14: hands, draw piles, enemy HP and intents); the in-game export equals the command-line export; normal exit and continue after restart. The restyled dialog and a rewind through its confirm button were checked the same day. Steam Cloud sync has not been tested.

## Build and test

Requirements: .NET 9 SDK, the game installed (the mod references `sts2.dll`, `GodotSharp.dll` and `0Harmony.dll` from the game folder; nothing from the game is copied into this repository or the package), PowerShell. Python 3 and Node are optional for the exporter tests.

```powershell
$env:STS2_GAME_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
./build.ps1            # builds Release and writes dist/SpireRewind-<version>.zip
./build.ps1 -Install   # also installs into <game>/mods/SpireRewind/ (game must not be running; existing files are backed up)
./build.ps1 -Workshop  # also fills workshop/content/ for the Steam Workshop uploader (see workshop/README.md)
./test.ps1             # core tests, then the Python and Node tests when those tools exist
```

`build.ps1` accepts `-GameDir` and `-DotnetPath`. The package contains exactly one `.json` (the manifest, renamed to the mod id) because the game treats every `.json` under `mods/` as a manifest; at runtime the mod writes only under the profile's `rewind/` folder. The tested game version is the constant `RewindMod.TestedGameVersion`; update it, the manifest's `min_game_version` and the docs together after testing on a new game version. Release notes are in [CHANGELOG.md](CHANGELOG.md).

The data formats (checkpoints, replay archives, decision snapshots, choices, the HTML page, the test command channel) are described in [docs/FORMAT.md](docs/FORMAT.md). Offline and in-game testing is described in [docs/TESTING.md](docs/TESTING.md).

The file-based test command channel is a developer tool for automated tests on an isolated copy of the game. It is off by default and exists only when the game process is started with `SPIRE_REWIND_TEST_MODE=1` in its environment; players never need to set it, and it must not be enabled on real player data.

## License

MIT, see [LICENSE](LICENSE).
