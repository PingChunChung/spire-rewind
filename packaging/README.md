# Spire Rewind

Spire Rewind is a mod for Slay the Spire 2. It keeps a checkpoint every time the game itself saves your single-player run, lets you go back to an earlier floor from the pause menu or the main menu, records the choices and fight decisions of each floor, and exports the whole run as an offline HTML page you can open in a browser.

## Install

Install it one way only: either subscribe on the Steam Workshop or put this folder into `mods`, not both. With the same mod in both places the game loads only one of them.

**Steam Workshop.** Subscribe and start the game. The first time the game finds a new mod it asks whether to load mods; allow it.

**Manual.**

1. Close the game.
2. Put the whole `SpireRewind` folder from the archive into the game's `mods` folder, so that you end up with `Slay the Spire 2/mods/SpireRewind/SpireRewind.dll`.
3. Start the game and allow mods to load.

A modded game uses a separate set of save files; your unmodded saves and progress are not affected.

To turn the mod off for a while, disable it in the Modding tab of the game's settings, or start the game with the `-nomods` launch option. To remove it, unsubscribe or delete the `mods/SpireRewind` folder. Your checkpoints and exports stay in the profile's `rewind` folder until you delete them.

Minimum game version 0.107.1; tested only on v0.107.1. On a newer game version the mod may or may not load; if it loads, the Rewind dialog shows a notice.

## Use

- Pause menu: choose **Rewind**. Main menu with a run in progress: choose **Rewind**.
- Pick a checkpoint from the list (act, floor, room, HP, gold, deck size, time), then confirm. The mod saves a recovery checkpoint of the current save first, replaces the run save and reloads the game at the chosen floor.
- **Rewind settings** in the same dialog: turn checkpoints on or off, how many recent runs to keep (1 to 100), whether rewound runs are marked in the mod's own records, and the interface language (follows the game, or Traditional Chinese, Simplified Chinese, English).
- **Export this run replay** writes an HTML file to the profile's `rewind/exports` folder; open it in any browser, offline.

## Before you rely on it

- Rewinding **rewrites the active run save** through the game's own save system (which Steam Cloud may sync). The mod keeps a backup checkpoint first, and tries to restore the original save if loading fails, and refuses to rewind when the local and Steam Cloud copies differ. It was tested with Steam Cloud sync on one machine; backing up your save folder before using it on a run you care about is still a good idea. The mod has no telemetry and no network service of its own; checkpoints and exports stay on this computer.
- Progress, unlocks, statistics, achievements and run history are **not** rolled back. Replaying a fight may count again in the statistics.
- Only single-player standard and custom runs with a current save are supported. Daily runs, multiplayer and ended runs are not.
- Checkpoints are taken when the game saves (entering and finishing a room), not on every turn.
- A checkpoint from another game version, another profile or a different set of mods is refused.

## If a rewind fails

Follow the message in the dialog. If it names a `backup=<id>` and asks for a restart, exit the game and restore by hand:

1. Open the modded profile folder, on Windows `%APPDATA%\SlayTheSpire2\<steam or default>\<user id>\modded\profile<N>\`.
2. The recovery checkpoint is `rewind\runs\<run id>\checkpoints\<that id>\run.save.json` (its `meta.json` says `"capture_kind": "before_restore"`; with several, take the newest `captured_at_utc`).
3. With the game closed, copy `run.save.json` over `saves\current_run.save` in the same profile folder, then start the game and continue.

With Steam Cloud on, Steam may show a sync conflict at the next launch; keep the local files. The full guide is in the project README.

## License

MIT, see `LICENSE`.
