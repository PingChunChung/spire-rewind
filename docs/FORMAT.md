# Spire Rewind data formats: archive v1, snapshots v1/v2, AI observation v1

The formats use UTF-8 JSON plus the game's native `.mcr`, and are meant to be read or produced by external tools. All locations are relative to `rewind/` under the game's current account and current modded profile. Data stored here stays on the local machine; it is not saved through the game's cloud save area.

```text
rewind/
  settings.json
  exports/<run_id>.html
  test-control/command.json
  test-control/result.json
  test-control/receipts/<request-hash>.receipt
  runs/<run_id>/
    checkpoints/<checkpoint_id>/
      run.save.json
      meta.json
    replays/act-<act>-floor-<floor>-<random_id>.mcr
    replays/act-<act>-floor-<floor>-<random_id>.json
    replays/act-<act>-floor-<floor>-<random_id>.snapshots.json
    rewind.json
    choices.json
```

## Identifiers and write rules

`run_id` is the SHA-256 of the UTF-8 bytes of the JSON array `[profileId, seed, startTime, character]`, as 64 lowercase hex characters. The array is serialized with .NET's default `JsonSerializer.SerializeToUtf8Bytes(object[])`: no indentation, default string escaping; the game's current seeds and character identifiers are ASCII. `character` is the game's `ModelId.ToString()`, never just the character's display name. Within one account/profile the ID maps to one run; accounts are isolated by the outer data directory.

`startTime` is the game's own Unix-second start time. It is not a globally unique identifier: restarting the same seed and character within the same second, a clock rollback or a copied save can collide. Restore additionally checks difficulty, mode, platform and player ID, but the format still must not be treated as an import system with a uniqueness guarantee.

`checkpoint_id` and the replay archive's `random_id` are 32 random bytes encoded as 64 lowercase hex characters. Paths must never be built directly from seeds, labels or external file names. All IDs are restricted to lowercase hex; paths, symbolic links, junctions and reparse points are not accepted.

A checkpoint writes its payload first, then `meta.json` as the completion marker. Each file is written to a temp file in the same directory, flushed and renamed; the two files are not a single OS-level transaction. The listing ignores entries with missing files, malformed content or missing required fields; restore reads the files again and verifies the SHA-256. Listing order is `captured_at_utc` descending, ties broken by `checkpoint_id` ascending. Temp and incomplete data are not listed.

## Checkpoint metadata

Every field in the table except `label` must be present. Readers tolerate unknown fields; a breaking change must bump `format_version`. SHA-256 detects corruption; there is no digital signature or origin authentication.

| Field | Type | Description |
| --- | --- | --- |
| `format_version` | integer | Version of this format, currently `1` |
| `checkpoint_id`, `run_id` | string | 64 lowercase hex characters |
| `game_version` | string | Game version as reported by the game, e.g. `v0.107.1` |
| `run_schema_version` | integer | The payload's native `schema_version`, separate from the format version |
| `profile_id` | integer | Game profile id |
| `seed` | string | Raw seed |
| `start_time` | integer | Native run start time, Unix seconds |
| `character` | string | Native character ModelId string |
| `ascension` | integer | Difficulty |
| `act` | integer | Act number, starting at 1 |
| `floor` | integer | Global floor; 0 before the first node is entered; before entering a room it includes the floor about to be entered |
| `room_type` | string | RoomType for completed/native saves; MapPointType before room entry, `Unknown` for question-mark nodes |
| `capture_kind` | string | `room_entry`, `native_save`, `room_completed`, `before_restore` |
| `captured_at_utc` | string | ISO 8601 UTC time with time zone |
| `hp`, `max_hp`, `gold`, `deck_count` | integer | Player summary for that payload |
| `run_sha256` | string | SHA-256 of the actual UTF-8 bytes of `run.save.json` |
| `mod_ids` | string[] | Sorted `id@version` list including SpireRewind; order is ignored when comparing |
| `source` | string | Currently `game`; external tools may use `ai` or another source |
| `label` | string/null | Optional tool label, not used by the current UI |

`run.save.json` is the complete native `SerializableRun` JSON written with the game's serializer, keeping `rng`, the player's random state, `pre_finished_room`, the map and the other native fields. It cannot be rebuilt from the seed alone. One payload is limited to 32 MiB; metadata to 1 MiB. When the native schema changes, this version refuses to restore and does not migrate archived data.

## Replay archives

`.mcr` files keep the binary content produced by the game; the mod neither decodes nor re-encodes them. The act and floor in the file name are for lookup; repeated fights on the same floor use different random IDs. A replay archive is limited to 64 MiB.

The `.json` sidecar with the same name contains `format_version=1`, `run_id`, `act`, `floor`, `sha256`, `captured_at_utc`, `game_version`, `run_schema_version`, `mod_ids`, `source`, `capture_kind`, `complete`. Later versions add the optional `game_commit` (string) and `model_id_hash` (uint32), saved from the current engine for new archives; when the core API does not provide a context/runtime the corresponding fields may be null and the mod list an empty array. Existing v1 files are not migrated; they can still be listed without the new fields, but the compatibility result is `replay_identity_missing`.

`capture_kind` is `combat_completed` (combat finished normally), `run_ended` (cleanup at the end of the run), `interrupted` (left mid-fight) or `diagnostic` (diagnostic output while recording was still active); unknown origins may use `unknown`. `complete` is inferred from the game state at the time of the call, estimating whether the fight or ending was played through; it is not a guarantee that the binary is intact or playable. If the native writer did not change `latest.mcr`, no archive is added.

The read-only replay library lists the archives of each run in the current profile, up to 2,000 entries. Viewing one entry re-reads the sidecar, verifies the SHA-256, then reads two UTF-8 header strings of at most 128 bytes each and the uint32 model hash; viewing is limited to 16 MiB, larger files can still be archived. A mismatch in version, commit, model hash, mod version list or required identity is reported as an error without entering the native `PacketReader`. `Compatible` only means these checks passed; `PayloadDecoded` and `PlaybackAvailable` in the native archive view are always false. Native playback reports `replay_isolation_unavailable` because persistence isolation is not finished; the decision snapshot player described below is available instead.

## Decision snapshot sidecar

A newly recorded, supported single-player normal combat may produce `<replay_id>.snapshots.json` in the same directory with the same replay ID, if decision points were observed and archived successfully. Observation requires two consecutive frames in a stable, idle player `Play` phase, deduplicated by `NextActionId` and summary. This is observational data: decision points may be missed and the last frame is not necessarily the end of the fight; it contains no animation or native combat reconstruction, and v1 contains no card-play record. Old `.mcr` files do not get snapshots generated after the fact; an archive that passes the checks but lacks snapshots reports `replay_snapshots_missing`.

Required JSON fields use snake_case; unknown or duplicate fields are rejected:

| Field | Content |
| --- | --- |
| `format_version` | Integer `1` or `2`; new files are written as `2` |
| `replay_sha256` | 64-character lowercase SHA-256 of the `.mcr`, must match the archive metadata |
| `run_id` | 64-character lowercase ID of the owning run |
| `game_version`, `game_commit`, `model_id_hash` | Version, commit and uint32 model hash matching the `.mcr` header, the current game and the archive identity |
| `mod_ids` | Mod version list matching the archive and the current environment; order is ignored, duplicates are not allowed |
| `frames` | Ordered array of decision points; the sequence number is the array index starting at 0 |

Each frame has `turn`, `players` and `enemies`; each actor has `label`, `hp`, `max_hp`. A snapshot file is limited to 8 MiB and 1–10,000 frames; each frame has 1–8 players and 0–64 enemies. Turn is 1–1,000,000 and never decreases; labels are non-empty, free of control characters and at most 128 UTF-16 characters; max HP is 1–1,000,000 and HP is 0 to max HP. Reading first re-checks the native archive, compatibility and actual SHA, then verifies the snapshot identity; metadata supplied by the caller is not trusted. Corrupt, oversized or mismatched data is rejected. Paths must not traverse or contain reparse points. Writes use a same-directory temp file, flush and atomic rename; reads never write. The SHA detects corruption and is not origin authentication.

Newly written snapshots use `format_version=2`; v1 is still read and existing files are not migrated. Checkpoint and `.mcr` sidecar versions and contents stay at v1. v2 adds the optional fields below; a missing or null field means nothing was recorded, which is not the same as a real 0 or empty list. v1 does not accept non-null v2 fields.

| Location/field | Type | Meaning and limits |
| --- | --- | --- |
| actor `block` | integer/null | Observed block, 0–999,999,999. |
| actor `intent` | string[]/null | Intent type names of the enemy's NextMove; damage calculation and execution hooks are not queried. |
| frame `cards` | string[]/null | Model IDs of the current hand, which differs from the permanent deck count. |
| frame `actions` | string[]/null | Model IDs of cards completed in `CardPlaysFinished` since the previous recorded point; may include automatic/repeated plays. An empty array means no completed play was observed. |
| frame `ai_reason` | string/null | Per-step reasoning that an external tool may supply; the mod does not produce AI judgments. At most 2,048 UTF-16 characters. |

String arrays hold at most 256 items, each non-empty, free of control characters and at most 256 UTF-16 characters. The in-game main-menu player still shows turns and HP; the web page additionally shows the v2 block, hand, completed plays and intents. Observation only happens at the existing stable decision points, so the winning card may be absent from the snapshots if no further decision point follows; missing actions are not reconstructed from the `.mcr`.

The snapshot player starts only from the real main menu, with no active run and no save in progress; it reads saved data and does not create a run, execute a native replay or play game animations. It starts paused at frame 0; playback advances one frame per second at 1x, with 2x and 4x also supported. Pause, next and previous all stop on the selected frame; previous uses the slower strategy of fast-forwarding from frame 0 to the preceding point. Reaching the last frame pauses automatically and marks completion; resuming after completion restarts from frame 0. A single update advances at most 100 frames, and excess elapsed time does not accumulate. Exiting releases the snapshot session.

## Per-floor choices sidecar

`runs/<run_id>/choices.json` is a standalone v1 document with required fields `format_version`, `run_id` and `entries`. It is limited to 8 MiB and 10,000 entries; each entry is described below. Unknown/duplicate JSON fields, duplicate identifiers, invalid times and out-of-range values are rejected by the recorder. Adding this document does not rewrite old checkpoints, native saves or `.mcr` files.

| Field | Type | Description |
| --- | --- | --- |
| `entry_id` | string | N-format UUID (32 hex digits); resending the same content with the same ID is not appended again, different IDs are kept as different choices. |
| `act`, `floor` | integer | Act 1–1,000, global floor 0–1,000,000. |
| `kind` | string | `card_reward`, `shop_purchase`, `card_removal`, `event_option`. |
| `captured_at_utc` | string | Non-default time with UTC time zone. |
| `source` | string | Currently `game`. The optional AI reasoning is separate from the source identifier. |
| `model_id`, `text_key`, `value` | string/null | Chosen model ID, event text key and extra semantic value, each at most 256 UTF-16 characters; null when there is no data. |
| `ai_reason` | string/null | Optional reasoning, at most 2,048 UTF-16 characters; hidden on the web page when absent. |

Card rewards only observe newly added history entries with `wasPicked`; shops save the merchandise model IDs from a pre-purchase snapshot after the purchase-completed notification, covering cards, relics and potions. Card removal is matched against recorded permanent-deck removals after `RemoveFromState` completes; entries may be missed right after entering a room, before the history cursor exists. Event options are recorded by the text key the game has accepted and written to history; asynchronous event results, later confirmations and final rewards are outside that entry's guarantee. Recording failures are only logged and never interrupt the game.

History from before the mod first attached to the current run is not backfilled; an old run without this file simply has no choice record. After a rewind, choices on the same floor are appended while earlier attempts are kept; there is no branch ID yet, so a single path per branch cannot be restored automatically. The web page lists entries by time and notes this limitation.

## Single-file offline HTML

The in-game C# export and `tools/export-replay.py` share `tools/replay-viewer.html`, embedding `format_version=1` page data in an `application/json` script: `run_id`, `language`, `checkpoints`, `replays`, `choices`, `warnings`. Each replay has `id`, `metadata` and `snapshots` (null when missing or when the checks failed). The JSON is escaped and the display uses text DOM only; the CSP forbids external resources, connections, forms and base URLs. The HTML contains no native run payload or `.mcr` binary, and never calls the game or the network.

The CLI takes a profile folder and a safe 64-digit run ID and writes `rewind/exports/<run_id>.html` by default; `--out` sets the HTML location and `--language zht|zhs|eng` the initial language. The in-game export always writes to the current profile's exports and shows the location; it does not open a browser. Both write a same-directory temp file and then replace the output; exporting again updates the same HTML and never changes the source archive.

Export reads metadata, choices and snapshots; for `.mcr` it only verifies the SHA and the bounded header and does not decode actions. Viewing history does not require the current game version, but snapshots must still match their archive header, SHA, run and mod list. At most 2,000 entries per directory are read, with a total source budget of 64 MiB and an output JSON budget of 32 MiB; over-limit or corrupt data leaves a warning or refuses output. Snapshot limits remain 8 MiB/10,000 frames, native replay viewing 16 MiB; source and output paths reject reparse points. Export does not validate the native run payload, so checkpoint information on the page does not mean it passed the in-game restore checks.

The curve is ordered by act/floor and by save time within a floor, includes repeated attempts, and is not a complete real-time series. Maximum combat loss is the sum of positive HP drops between adjacent observation points; healing does not offset an observed drop, and damage and healing between two points may still be missed. All HTML controls only switch local data; "Retry from here" is an explanation of the in-game rewind operation and cannot bypass the existing restore limits.

## AI observation archive v1 (optional, external)

An external importer may write an AI observation archive for the Python exporter and the offline page, identified by `runs/<run_id>/ai-run.json`. This source branch does not apply the native save and `.mcr` identity rules and is not used by the in-game C# restore or the native replay player. The existing native archive, snapshot and choice formats and their validation are unchanged.

```text
<out>/
  <source-parent-directory-name>-<seed>.html
  rewind/runs/<run_id>/
    ai-run.json
    checkpoints/<checkpoint_id>/meta.json
    replays/<replay_id>.snapshots.json
    choices.json
```

No `run.save.json`, `.mcr`, fake replay headers or fake native SHAs are produced. The AI run ID is the SHA-256 of the compact ASCII JSON array `["ai","spire-rewind-ai/1",session_id,events_sha256]`, as 64 lowercase hex digits. Checkpoint/replay IDs are derived from that run ID, the source event line number and the purpose; choice IDs are the first 32 digits of a hash computed the same way. The same source reproduces the same IDs; source file names are never concatenated into IDs. Path characters are stripped from the seed file name in the output HTML.

`ai-run.json` contains `format_version=1`, `source="ai"`, `importer_version="spire-rewind-ai/1"`, `run_id`, `seed`, `character`, `ascension`, `outcome`, `started_at_utc`, `ended_at_utc`, `terminal`, `summary`, `limitations` and `statistics`. `provenance` lists the file names and SHA-256 of the logs, decision archive and notes actually read, without local absolute paths. The summary is an excerpt of the outcome, build and boss paragraphs from the original notes; no additional tactical judgment is generated.

The manifest's `checkpoint_files` entries carry `id` and the metadata file `sha256`; `replay_files` entries carry `id`, the snapshot file `sha256` and the combat `metadata` (`source="ai"`, `run_id`, act, floor, time and `complete`); `choices_sha256` binds the choices file. Export checks the actual bytes; this does not claim the source is authenticated. The manifest, each JSON file and the directories reject symbolic links and reparse points; a bad index, missing hash or corruption rejects the whole AI archive.

AI checkpoint metadata uses `format_version=1`, `source="ai"`, `capture_kind="ai_observation"`, `restorable=false`, with the run/checkpoint IDs, act, floor, room type, time and the observed player summary. `hp`, `max_hp`, `gold`, `deck_count` are null when not observed. `game_version`, `run_schema_version`, `profile_id`, `run_sha256`, `mod_ids` are also null, making explicit that there is no native restore identity. The first observation per floor is kept; if the last observed summary differs, the last one is kept too; intermediate combat states remain in the frames.

Combat files contain `format_version=2`, `source="ai"`, `run_id`, `frames`, without the native `replay_sha256`/header identity. Frames reuse `turn`, `players`, `enemies`, `cards`, `actions`, `ai_reason`; turn and actor lists may be null when data is missing. Actor HP, max HP, block and intent may be null; enemy intents keep the source object as a compact JSON string including the original damage, hit count and description. `cards` is the hand IDs at observation time. `actions` is always null because the source does not record "plays completed since the previous point".

Frames also carry `action` (the name and label of the action requested and completed at that point), `captured_at_utc`, `ai_reason_source` and `source_ref`. When a decision archive exists, state and reasoning come from the same record with `ai_reason_source="decisions.answer.note"`; without one, only `game01.reason(manual)` is kept. Rule-based actions have null reasoning with source `game01.reason(rule)`. Short notes are kept verbatim and never expanded into reasoning that does not exist. `source_ref` stores the event line number, the game01 line number and the optional decision seq, all starting at 1.

AI `choices.json` reuses the v1 envelope and base fields with source `ai`; besides the existing kinds it adds `map_select`, `rest_site`, `reward`, `card_selection`, `card_upgrade`, `removal_service` and `other` (for example deciding to leave a shop). Buying a removal service and choosing which card to remove are recorded separately; without prompt text only the card selection is recorded, with no guess whether it was a removal or an upgrade. Model IDs are taken only from real IDs in the decision options, otherwise null; the event text key is taken from `event_options.keys[index]` of the preceding request, otherwise null. `value` keeps the original action label, and `action`, `ai_reason_source` and `source_ref` are stored alongside.

Map selections are listed at the destination floor, with `source_ref.observed_act`/`observed_floor` holding the position at decision time; on a cross-act map the act has already increased while the global floor has not. The importer counts completed map moves starting from the initial Ancient F1 and increments the act when the map row resets; when an archive exists each entry's act/floor is cross-checked, and `run_ended` is checked last. Win/loss is taken only from `run_ended.outcome`; an `hp=0` on the end screen is not treated as a combat death or added to the HP curve.

The source must contain exactly one complete run; every action in the events and game01 logs is checked in order for state version, name, label, available arguments, success result and session. If a decision archive is supplied, its count must exactly match the manual actions, cross-checked by seq, screen, kind, reasoning and run identity; any mismatch stops the import. The output must not overlap the input folder, decision file or notes, and the importer never launches a game session or rebuilds missing state from the seed.

Each source file, or its gzip-decompressed data, is limited to 128 MiB, 2 MiB per line and 100,000 records; the AI export keeps the limits of 64 MiB total read, 32 MiB output JSON, 8 MiB per sidecar, 2,000 entries per index type, 10,000 points per combat and 10,000 choices. Reasoning keeps the 2,048 UTF-16 character limit; the notes summary is limited to 20,000 characters. A single-file AI export relies on checksum, field and size checks and cannot fill in fields that were never archived.

The page envelope stays at v1 and adds the optional `ai_run` (the manifest with the file indexes removed). The header shows source, outcome, notes and limitations; AI checkpoints and the footer state explicitly that rewind is not possible. Unknown values and missing arrays are displayed separately, and HP loss only counts segments where both adjacent points have real HP. Page behavior for native data is unchanged.

## Test command protocol

The watcher node is installed only when the game process has the environment variable `SPIRE_REWIND_TEST_MODE=1`; the channel is not created by default. It checks the test command channel's `command.json` every 250 ms on the Godot main thread and opens no network service. Without an external root, the `rewind/test-control/` channel in the current profile is kept, but every `replay-*` command requires an external root. Set `SPIRE_REWIND_TEST_ROOT` to an absolute path before starting the game; the path must not equal Godot's `user://`, be an ancestor or descendant of it, or contain a reparse point; the channel lives in `test-control/` under that root. The channel can request restores of saved progress; it is intended only for isolated testing and allows a single client per channel.

The client first writes a complete UTF-8 temp file in the same directory, then atomically publishes it as `command.json`; an unfinished request must not be overwritten. Base fields:

```json
{
  "request_id": "20bf64a3-329a-42f7-b94b-e2d9e1dadbf7",
  "command": "restore",
  "created_at_utc": "2026-10-30T06:00:00Z",
  "run_id": "<64 lowercase hex characters>",
  "checkpoint_id": "<64 lowercase hex characters>"
}
```

`request_id` must be a D-format UUID. The time may not be more than 10 seconds in the future or 2 minutes in the past; a request is limited to 16 KiB, and unknown fields, arbitrary paths and reparse points are not accepted. `status` is read-only and reports `safe_to_execute`, the reason and the state; `pause` opens the native pause only at a stable decision point of a supported single-player active run and rejects in-flight actions, enemy turns and other open screens. It is a test operation that prepares a safe state and does not replace the save.

`list` and `restore` run only in the main menu of a restorable single-player run or in a visible, actually paused native pause menu. Daily, multiplayer, Replay, ended runs, busy, disabled and restart-required states are all rejected. `list` returns `run_id`, `snapshot_id` and `checkpoints` in listing order. `restore` prefers `run_id + checkpoint_id`; `run_id + index + snapshot_id` also works, with index starting at 1, and is rejected when the list has changed rather than silently picking another entry.

The mod atomically publishes the result to `result.json` with `request_id`, `status` (`completed`/`rejected`/`failed`), `error` and `data`. The client accepts only results with the same request ID and deletes its own `command.json` after receiving one. `restore.data` contains the selected `checkpoint`, `before`, `after` and `marker`. State includes the `save` metadata, `save_summary` and, when a run is active, a `live` summary; summaries include HP, gold, the fully serialized `deck`, `deck_ids`, card count and character.

`export` writes the current run's HTML with the same exporter as the in-game button and returns `run_id`, `path`, `language`; it reads only mod files and does not require a pause. `ui-open` opens the same rewind dialog as the button from the main menu or a visible native pause; `ui-confirm` uses the selection fields of `restore` to enter the confirmation screen for that checkpoint; `ui-close` closes the dialog. These three are for screenshots and layout checks only and never submit a restore. `ui-accept` presses the "Confirm rewind" button on an open confirmation screen; the restore goes through the dialog's own handler and the result is read from a later `status`; without a confirmation screen it returns `confirm_not_open`. `save-and-quit` requires a visible, stable native pause and returns to the main menu through the same path as the pause menu's "Save and quit", returning `main_menu` and `has_run_save`. `fault-inject` is accepted only on the main menu with no run in progress: it latches the restart-required fault exactly as a failed rollback would (`requires_restart`, `notice`, `continue_refusal` in the result), after which the mod refuses every rewind and the game's own Continue stays disabled until the process is restarted; it exists so that gate can be checked on an isolated copy. None of these commands take `replay_id`/`speed`, and none except `ui-confirm` take selection fields.

Before execution, a persistent receipt is created exclusively, named by the SHA-256 of the normalized UUID. A repeated ID is not executed again, including after a restart; this provides at-most-once acceptance, not a guarantee of one successful execution. If the process stops after acceptance but before the result is written, the client must record the result as unknown and must not resend or blindly retry the restore under a new ID. The result stays in `result.json`; receipts are not cleaned up automatically and can only be archived or removed together with the whole dedicated channel after the test process exits normally.

### Snapshot playback and audit commands

`replay-list` lists archives; `replay-inspect` and `replay-play` require `run_id` plus `replay_id`. The latter starts a paused snapshot session. `replay-resume`, `replay-pause`, `replay-step`, `replay-previous`, `replay-exit` and `replay-status` take no selection fields; `replay-speed` accepts only `speed` of 1, 2 or 4. Status reports `kind=decision_snapshots`, `native_playback=false`, `index`, `frame_count`, `playing`, `completed`, `speed`, `frame` and `previous_strategy`. "play" in the command names means starting the snapshot session.

`replay-audit-begin` is allowed only in the real main menu on the None platform; it captures every file in the full profile and account directories and records the in-memory progress, prefs and settings. `replay-audit-end` compares again after the session ends and can be attempted even if the main-menu condition no longer holds. It reports `unchanged`, `files_unchanged`, `memory_unchanged` and the added, removed and changed lists. All files are included, settings, prefs, replay archives and sidecars among them; when the profile is inside the account scope, its files are counted once in each scope. This is per-file hashing, not an atomic file-system snapshot, and it does not guarantee that the platform or other processes made no background writes.

## Settings and rewind record

`settings.json` contains `enabled` (default true), `retained_runs` (default 10, normalized to 1–100), `mark_rewound_runs` (default true) and `language` (`auto`, `zht`, `zhs`, `eng`, default `auto`). Disabling does not delete data; lowering the retained count removes the oldest runs whenever the settings are saved, with or without a run in progress. The current run, when there is one, is never removed and occupies one retained slot; without one, the newest runs fill every slot.

`rewind.json` contains `restore_count`, `marked`, `checkpoint_id` and `restored_at_utc`. It is updated after the rewind load and validation complete; unchecking the mark still increments the count, and a run already marked does not lose its mark when the box is unchecked later. A write failure produces a separate notice that the load succeeded but the record failed. This file never modifies the native run or history and is not synced to the cloud.

External tools may describe AI runs in the same format, but the rewind UI lists only the run that still has a save, and restore requires the payload's run, profile, version and mod list to match. There is no cross-run import or native playback of external replay archives; snapshot playback is limited to local archives that pass the checks above.
