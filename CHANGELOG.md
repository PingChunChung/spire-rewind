# Changelog

Versions follow SemVer and stay at 0.x while the game is in Early Access. A release that only restores compatibility with a game update bumps the patch number; a change to the checkpoint, archive or export formats bumps the minor number.

## 0.1.0

First public release. Tested on game v0.107.1, singleplayer only; not yet tested with Steam Cloud sync.

- Automatic checkpoints: every native run save (room entry, room completion, the regular autosave) is copied into the profile's `rewind/` folder with metadata (act, floor, room, HP, gold, deck size, game version, mod list, SHA-256 of the payload).
- Rewind from the pause menu or the main menu: pick a checkpoint, confirm, and the mod saves a recovery checkpoint, replaces the run save through the game's own save interface and reloads the run. A restore is refused when the game version, save schema, character, run identity or mod version list differs from the checkpoint; if loading fails it tries to put the original save back and reports what happened.
- Per-run choice log (`choices.json`): card rewards, shop purchases, card removals and event options. Decision-point snapshots of each fight (turn, HP, block, hand, cards played, enemy intents) next to the game's own combat replay file.
- Offline replay export: one self-contained HTML page per run with the floor timeline, HP/gold/deck curves, every choice and a decision-by-decision view of each recorded fight; the same page from the command line with `tools/export-replay.py`.
- Settings in the Rewind dialog: checkpoints on or off, number of retained runs (1 to 100), marking rewound runs in the mod's own records, interface language (follow the game, Traditional Chinese, Simplified Chinese, English).
- The Rewind dialog shows a notice when the running game version is not the tested one (v0.107.1). The notice does not change what is restored. The manifest declares `min_game_version` 0.107.1; an older game does not load the mod, and a newer one is untested.
- A restore pins the profile, save path and save store it started in; switching profiles while a restore is in progress is refused, and a rollback writes the original bytes back to that profile. When neither the chosen checkpoint nor the original save could be verified, further rewinds are refused until the game is restarted and the dialog keeps showing the backup checkpoint id.
- Exported pages carry only fixed warning codes (`file_missing`, `invalid_json`, ...); the full error stays in the game log or on the command line. The release DLL carries no build-machine paths.
- Lowering the number of retained runs prunes when the settings are saved, also with no run in progress.
- The file-based test command channel exists only with `SPIRE_REWIND_TEST_MODE=1` in the game's environment; it is for developers on an isolated copy and is off by default.
- Verified on an isolated copy of the game with the Steam backend off and Spire Link loaded alongside: checkpoints, choice log, snapshots, rewind from both menus with the recovery checkpoint, a replayed fight repeating every recorded decision point, in-game export equal to the command-line export, normal exit and continue after restart.
