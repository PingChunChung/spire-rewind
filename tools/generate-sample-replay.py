#!/usr/bin/env python3
"""Generate a deterministic fictional offline sample; never accesses a game profile."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("export_replay", HERE / "export-replay.py")
export = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export)
run = "a" * 64

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")

with tempfile.TemporaryDirectory() as tmp:
    root = Path(tmp) / "rewind"
    run_dir = root / "runs" / run
    rooms = ["Monster", "Elite", "RestSite", "Shop", "Treasure", "Event", "Boss"]
    for i, room in enumerate(rooms, 1):
        for attempt in range(2 if i == 2 else 1):
            checkpoint = format(i * 10 + attempt, "064x")
            payload = b"{}"
            cp_dir = run_dir / "checkpoints" / checkpoint
            cp_dir.mkdir(parents=True, exist_ok=True)
            (cp_dir / "run.save.json").write_bytes(payload)
            write(cp_dir / "meta.json", dict(format_version=1, run_id=run, checkpoint_id=checkpoint, game_version="v", run_schema_version=1, profile_id=1, seed="SAMPLE", start_time=1767225600, character="IRONCLAD", ascension=0, act=1, floor=i, room_type=room, capture_kind="room_entry", captured_at_utc="2026-01-01T00:%02d:%02dZ" % (i, attempt), hp=75-i*4+attempt, max_hp=80, gold=90+i*17, deck_count=10+i, run_sha256=hashlib.sha256(payload).hexdigest(), mod_ids=[], source="game", label=None))
    sample_choices = [
        (1, "card_reward", "CARD.STRIKE", None, None),
        (4, "shop_purchase", "CARD.DEFEND", None, None),
        (4, "shop_purchase", "RELIC.EXAMPLE", None, None),
        (4, "shop_purchase", "POTION.EXAMPLE", None, None),
        (4, "card_removal", "CARD.STRIKE", None, None),
        (6, "event_option", None, "EVENT.OPTION", "choice"),
    ]
    entries = [dict(entry_id=format(i, "032x"), act=1, floor=floor, kind=kind, model_id=model, text_key=key, value=value, captured_at_utc="2026-01-01T00:%02d:01Z" % floor, source="game", ai_reason="保留防禦資源" if i == 6 else None) for i, (floor, kind, model, key, value) in enumerate(sample_choices, 1)]
    write(run_dir / "choices.json", dict(format_version=1, run_id=run, entries=entries))
    for i, version in [(2, 1), (2, 2), (7, 2)]:
        rid = "act-1-floor-%d-%s" % (i, format(i * 10 + version, "064x"))
        path = run_dir / "replays" / (rid + ".mcr")
        path.parent.mkdir(parents=True, exist_ok=True)
        raw = struct.pack("<i", 1) + b"v" + struct.pack("<i", 1) + b"c" + struct.pack("<I", 123)
        path.write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        meta = dict(format_version=1, run_id=run, act=1, floor=i, sha256=digest, captured_at_utc="2026-01-01T00:%02d:00Z" % i, game_version="v", game_commit="c", model_id_hash=123, mod_ids=[])
        write(path.with_suffix(".json"), meta)
        frames = []
        for turn in range(1, 4):
            actor = dict(label="Ironclad", hp=75-turn*3, max_hp=80)
            enemy = dict(label="敵人", hp=40-turn*10, max_hp=40)
            frame = dict(turn=turn, players=[actor], enemies=[enemy])
            if version == 2:
                actor["block"] = 5
                enemy["intent"] = ["Attack"]
                frame.update(cards=["CARD.DEFEND"], actions=["CARD.STRIKE"], ai_reason=None)
            frames.append(frame)
        write(path.with_suffix(".snapshots.json"), dict(format_version=version, replay_sha256=digest, run_id=run, game_version="v", game_commit="c", model_id_hash=123, mod_ids=[], frames=frames))
    rid = "act-1-floor-1-" + format(100, "064x")
    path = run_dir / "replays" / (rid + ".mcr")
    path.write_bytes(raw)
    write(path.with_suffix(".json"), dict(format_version=1, run_id=run, act=1, floor=1, sha256=hashlib.sha256(raw).hexdigest(), captured_at_utc="2026-01-01T00:01:00Z", game_version="v", game_commit="c", model_id_hash=123, run_schema_version=1, mod_ids=[], source="game", capture_kind="combat_completed", complete=True))
    html = export.render(export.collect(root, run, "zht"))
    (HERE.parent / "docs" / "sample-replay.html").write_text(html, encoding="utf-8")
