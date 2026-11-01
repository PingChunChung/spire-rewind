#!/usr/bin/env python3
"""Export bounded, untrusted archive metadata without executing game replay data."""
import argparse
import datetime
import hashlib
import itertools
import json
import os
from pathlib import Path
import re
import struct
import sys
import tempfile

ID = re.compile(r"^[0-9a-f]{64}$")
REPLAY = re.compile(r"^act-[a-z0-9-]+-floor-[a-z0-9-]+[0-9a-f]{64}$")
MAX_JSON = 8 * 1024 * 1024
MAX_REPLAY = 16 * 1024 * 1024
MAX_ITEMS = 2000

def text_ok(value, maximum):
    return value is None or (isinstance(value, str) and bool(value.strip()) and len(value.encode("utf-16-le")) // 2 <= maximum and not any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in value))

def integer(value, low, high):
    return type(value) is int and low <= value <= high

def utc(value):
    try:
        dt = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return dt.tzinfo is not None and dt.utcoffset() == datetime.timedelta(0) and dt.year > 1
    except (TypeError, ValueError, AttributeError):
        return False

def valid_checkpoint(m):
    strings = ("game_version", "seed", "character", "room_type", "capture_kind", "source")
    numbers = ("run_schema_version", "profile_id", "start_time", "ascension")
    return (all(isinstance(m.get(k), str) for k in strings) and all(type(m.get(k)) is int for k in numbers)
        and ID.fullmatch(str(m.get("run_sha256", ""))) and isinstance(m.get("mod_ids"), list)
        and all(isinstance(x, str) for x in m["mod_ids"]) and utc(m.get("captured_at_utc"))
        and integer(m.get("act"), 1, 1000) and integer(m.get("floor"), 0, 1000000)
        and integer(m.get("max_hp"), 1, 2147483647) and integer(m.get("hp"), 0, m.get("max_hp", 0))
        and integer(m.get("gold"), 0, 2147483647) and integer(m.get("deck_count"), 0, 2147483647))

def valid_choices(entries):
    seen = set()
    for e in entries:
        if not isinstance(e, dict):
            return False
        ident = e.get("entry_id")
        if not isinstance(ident, str) or not re.fullmatch(r"[0-9a-fA-F]{32}", ident) or ident in seen:
            return False
        seen.add(ident)
        if not integer(e.get("act"), 1, 1000) or not integer(e.get("floor"), 0, 1000000) or e.get("kind") not in ("card_reward", "shop_purchase", "card_removal", "event_option") or e.get("source") != "game" or not utc(e.get("captured_at_utc")):
            return False
        if not all(text_ok(e.get(k), limit) for k, limit in (("model_id", 256), ("text_key", 256), ("value", 256), ("ai_reason", 2048))):
            return False
    return True

def valid_frames(s):
    previous = 0
    version = s["format_version"]
    def strings(value):
        return value is None or (isinstance(value, list) and len(value) <= 256 and all(x is not None and text_ok(x, 256) for x in value))
    for f in s["frames"]:
        if not isinstance(f, dict) or not integer(f.get("turn"), max(1, previous), 1000000):
            return False
        previous = f["turn"]
        if not isinstance(f.get("players"), list) or not 1 <= len(f["players"]) <= 8 or not isinstance(f.get("enemies"), list) or len(f["enemies"]) > 64:
            return False
        if version == 1 and any(f.get(k) is not None for k in ("cards", "actions", "ai_reason")):
            return False
        if not strings(f.get("cards")) or not strings(f.get("actions")) or not text_ok(f.get("ai_reason"), 2048):
            return False
        for a in f["players"] + f["enemies"]:
            if not isinstance(a, dict) or a.get("label") is None or not text_ok(a["label"], 128) or not integer(a.get("max_hp"), 1, 1000000) or not integer(a.get("hp"), 0, a["max_hp"]):
                return False
            if a.get("block") is not None and not integer(a["block"], 0, 999999999):
                return False
            if version == 1 and (a.get("block") is not None or a.get("intent") is not None):
                return False
            if not strings(a.get("intent")):
                return False
    return True

def safe(path):
    path = Path(os.path.abspath(path))
    for item in (path, *path.parents):
        if os.path.lexists(item):
            stat = item.lstat()
            if item.is_symlink() or getattr(stat, "st_file_attributes", 0) & 0x400:
                raise ValueError("link_refused: " + str(item))
    return path

def reject_constant(value):
    """NaN and Infinity are not JSON; a page embedding them would not parse in the browser."""
    raise ValueError("invalid_number")

def warning_code(error):
    """Fixed code for the shared page; file-system messages name the player's folders, so they never go in."""
    if isinstance(error, json.JSONDecodeError):
        return "invalid_json"
    if isinstance(error, UnicodeError):
        return "invalid_encoding"
    if isinstance(error, ValueError):
        head = str(error).split(":", 1)[0]
        return head if re.fullmatch(r"[a-z_]{1,64}", head) else "invalid_data"
    if isinstance(error, (FileNotFoundError, NotADirectoryError)):
        return "file_missing"
    if isinstance(error, PermissionError):
        return "access_denied"
    if isinstance(error, OSError):
        return "io_error"
    return "read_error"

def read_json(path, limit=MAX_JSON, budget=None):
    path = safe(path)
    with path.open("rb") as f:
        if budget is not None:
            size = os.fstat(f.fileno()).st_size
            if size > budget[0]:
                raise ValueError("read_size_limit")
            budget[0] -= size
        raw = f.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("size_limit")
    def unique(pairs):
        result = {}
        for k, v in pairs:
            if k in result:
                raise ValueError("duplicate_key")
            result[k] = v
        return result
    return json.loads(raw, object_pairs_hook=unique, parse_constant=reject_constant)

def header(raw):
    pos = 0
    result = []
    for _ in range(2):
        if pos + 4 > len(raw):
            raise ValueError("truncated_replay_header")
        size = struct.unpack_from("<i", raw, pos)[0]
        pos += 4
        if size < 1 or size > 128 or pos + size > len(raw):
            raise ValueError("invalid_replay_header")
        text = raw[pos:pos + size].decode("utf-8")
        if any(ord(c) < 32 or ord(c) == 127 for c in text):
            raise ValueError("invalid_replay_header")
        result.append(text)
        pos += size
    if pos + 4 > len(raw):
        raise ValueError("truncated_replay_header")
    return result + [struct.unpack_from("<I", raw, pos)[0]]

AI_CHOICE_KINDS = {"card_reward", "shop_purchase", "card_removal", "event_option", "map_select",
                   "card_selection", "card_upgrade", "removal_service", "rest_site", "reward", "other"}

def valid_ai_frames(s):
    """AI observations have no native replay identity; missing facts stay null."""
    if not isinstance(s, dict) or s.get("format_version") != 2 or s.get("source") != "ai":
        return False
    frames = s.get("frames")
    if not isinstance(frames, list) or not 1 <= len(frames) <= 10000:
        return False
    previous = 0
    for frame in frames:
        if not isinstance(frame, dict):
            return False
        turn = frame.get("turn")
        if turn is not None:
            if not integer(turn, max(previous, 1), 1000000):
                return False
            previous = turn
        for group, maximum in (("players", 8), ("enemies", 64)):
            actors = frame.get(group)
            if actors is None:
                continue
            if not isinstance(actors, list) or len(actors) > maximum or (group == "players" and not actors):
                return False
            for actor in actors:
                if not isinstance(actor, dict) or not actor.get("label") or not text_ok(actor["label"], 128):
                    return False
                for key, low in (("hp", 0), ("max_hp", 1), ("block", 0)):
                    value = actor.get(key)
                    if value is not None and not integer(value, low, 999999999):
                        return False
                if actor.get("hp") is not None and actor.get("max_hp") is not None and actor["hp"] > actor["max_hp"]:
                    return False
                intent = actor.get("intent")
                if intent is not None and (not isinstance(intent, list) or len(intent) > 256 or any(x is None or not text_ok(x, 256) for x in intent)):
                    return False
        for key in ("cards", "actions"):
            value = frame.get(key)
            if value is not None and (not isinstance(value, list) or len(value) > 256 or any(x is None or not text_ok(x, 256) for x in value)):
                return False
        if not all(text_ok(frame.get(k), limit) for k, limit in (("ai_reason", 2048), ("action", 2048), ("ai_reason_source", 256))):
            return False
        if not utc(frame.get("captured_at_utc")):
            return False
    return True

def collect_ai(run, run_id, language):
    """Read only a hash-indexed AI archive, never reinterpret it as .mcr/save data."""
    budget = [64 * 1024 * 1024]
    def read(path, sha=None, maximum=MAX_JSON):
        path = safe(path)
        if path.name != "ai-run.json" and not ID.fullmatch(str(sha)):
            raise ValueError("invalid_ai_checksum")
        with path.open("rb") as stream:
            raw = stream.read(min(maximum, budget[0]) + 1)
        if len(raw) > maximum or len(raw) > budget[0]:
            raise ValueError("ai_size_limit")
        budget[0] -= len(raw)
        if sha is not None and hashlib.sha256(raw).hexdigest() != sha:
            raise ValueError("ai_checksum_mismatch: " + path.name)
        def unique(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise ValueError("duplicate_key")
                result[key] = value
            return result
        return json.loads(raw, object_pairs_hook=unique, parse_constant=reject_constant)
    ai = read(run / "ai-run.json")
    if (not isinstance(ai, dict) or ai.get("format_version") != 1 or ai.get("source") != "ai"
            or ai.get("run_id") != run_id or not isinstance(ai.get("importer_version"), str) or not text_ok(ai["importer_version"], 128)
            or not isinstance(ai.get("summary"), str) or len(ai["summary"]) > 20000
            or not all(isinstance(ai.get(k), str) for k in ("seed", "character", "outcome"))
            or not integer(ai.get("ascension"), 0, 1000)
            or not utc(ai.get("started_at_utc")) or not utc(ai.get("ended_at_utc"))):
        raise ValueError("invalid_ai_manifest")
    if not isinstance(ai.get("limitations"), list) or len(ai["limitations"]) > 100 or any(not isinstance(x, str) or not text_ok(x, 2048) for x in ai["limitations"]):
        raise ValueError("invalid_ai_limitations")
    provenance = ai.get("provenance")
    if not isinstance(provenance, list) or not 1 <= len(provenance) <= 16 or any(not isinstance(x, dict) or not isinstance(x.get("file"), str) or not text_ok(x["file"], 512) or not ID.fullmatch(str(x.get("sha256", ""))) for x in provenance):
        raise ValueError("invalid_ai_provenance")
    result = dict(format_version=1, run_id=run_id, language=language, checkpoints=[], replays=[], choices=[], warnings=[])
    for key in ("checkpoint_files", "replay_files"):
        entries = ai.get(key)
        if not isinstance(entries, list) or len(entries) > MAX_ITEMS:
            raise ValueError("invalid_ai_index")
        seen = set()
        for entry in entries:
            if not isinstance(entry, dict):
                raise ValueError("invalid_ai_index")
            ident = entry.get("id")
            pattern = ID if key == "checkpoint_files" else REPLAY
            if not isinstance(ident, str) or not pattern.fullmatch(ident) or ident in seen:
                raise ValueError("invalid_ai_item_id")
            seen.add(ident)
            if key == "checkpoint_files":
                m = read(run / "checkpoints" / ident / "meta.json", entry.get("sha256"), 1024 * 1024)
                if (not isinstance(m, dict) or m.get("format_version") != 1 or m.get("source") != "ai" or m.get("run_id") != run_id
                        or m.get("checkpoint_id") != ident or m.get("restorable") is not False or m.get("capture_kind") != "ai_observation"
                        or not integer(m.get("act"), 1, 1000) or not integer(m.get("floor"), 0, 1000000) or not utc(m.get("captured_at_utc"))):
                    raise ValueError("invalid_ai_checkpoint")
                for field in ("hp", "max_hp", "gold", "deck_count"):
                    if m.get(field) is not None and not integer(m[field], 1 if field == "max_hp" else 0, 2147483647):
                        raise ValueError("invalid_ai_checkpoint_stat")
                if m.get("hp") is not None and m.get("max_hp") is not None and m["hp"] > m["max_hp"]:
                    raise ValueError("invalid_ai_checkpoint_stat")
                result["checkpoints"].append(m)
            else:
                m = entry.get("metadata")
                s = read(run / "replays" / (ident + ".snapshots.json"), entry.get("sha256"))
                if (not isinstance(m, dict) or m.get("source") != "ai" or m.get("run_id") != run_id
                        or not integer(m.get("act"), 1, 1000) or not integer(m.get("floor"), 0, 1000000)
                        or not utc(m.get("captured_at_utc")) or not isinstance(s, dict) or s.get("run_id") != run_id or not valid_ai_frames(s)):
                    raise ValueError("invalid_ai_snapshots")
                result["replays"].append(dict(id=ident, metadata=m, snapshots=s))
    c = read(run / "choices.json", ai.get("choices_sha256"))
    if not isinstance(c, dict) or c.get("format_version") != 1 or c.get("run_id") != run_id or not isinstance(c.get("entries"), list) or len(c["entries"]) > 10000:
        raise ValueError("invalid_ai_choices")
    seen = set()
    for e in c["entries"]:
        if (not isinstance(e, dict) or not re.fullmatch(r"[0-9a-f]{32}", str(e.get("entry_id", "")))
                or e["entry_id"] in seen or e.get("source") != "ai" or e.get("kind") not in AI_CHOICE_KINDS
                or not integer(e.get("act"), 1, 1000) or not integer(e.get("floor"), 0, 1000000)
                or not utc(e.get("captured_at_utc"))
                or not all(text_ok(e.get(k), n) for k, n in (("model_id", 256), ("text_key", 256), ("value", 256), ("ai_reason", 2048), ("ai_reason_source", 256)))):
            raise ValueError("invalid_ai_choice")
        seen.add(e["entry_id"])
    result["choices"] = c["entries"]
    result["ai_run"] = {k: v for k, v in ai.items() if k not in ("checkpoint_files", "replay_files", "choices_sha256")}
    result["checkpoints"].sort(key=lambda x: (x["act"], x["floor"], x["captured_at_utc"], x["checkpoint_id"]))
    result["replays"].sort(key=lambda x: (x["metadata"]["act"], x["metadata"]["floor"], x["metadata"]["captured_at_utc"]))
    result["choices"].sort(key=lambda x: (x["captured_at_utc"], x["entry_id"]))
    if len(json.dumps(result, ensure_ascii=True, allow_nan=False).encode("ascii")) > 32 * 1024 * 1024:
        raise ValueError("export_size_limit")
    return result

def collect(root, run_id, language, log=None):
    """`log` receives the full detail of every warning (local diagnostics); the page gets `warning_code` only."""
    if not ID.fullmatch(run_id):
        raise ValueError("invalid_run_id")
    if language not in ("zht", "zhs", "eng"):
        raise ValueError("invalid_language")
    run = safe(Path(root) / "runs" / run_id)
    if not run.is_dir():
        raise ValueError("run_missing")
    if safe(run / "ai-run.json").exists():
        return collect_ai(run, run_id, language)
    result = dict(format_version=1, run_id=run_id, language=language, checkpoints=[], replays=[], choices=[], warnings=[])
    warnings = result["warnings"]
    budget = [32 * 1024 * 1024]
    read_budget = [64 * 1024 * 1024]
    def read(path, limit=MAX_JSON):
        return read_json(path, limit, read_budget)
    def take(value):
        size = len(json.dumps(value, ensure_ascii=True, allow_nan=False).encode("ascii"))
        if size > budget[0]:
            raise ValueError("export_size_limit")
        budget[0] -= size
    def warn(name, error):
        if log is not None:
            log(name + ": " + (error if isinstance(error, str) else type(error).__name__ + ": " + str(error)))
        warnings.append(name + ": " + (error if isinstance(error, str) else warning_code(error)))
    def files(path):
        path = safe(path)
        if not path.exists():
            return []
        entries = sorted(itertools.islice(path.iterdir(), MAX_ITEMS + 1), key=lambda p: p.name)
        if len(entries) > MAX_ITEMS:
            warnings.append(path.name + ": item_limit")
        return entries[:MAX_ITEMS]
    for directory in files(run / "checkpoints"):
        if not ID.fullmatch(directory.name):
            continue
        try:
            m = read(directory / "meta.json", 1024 * 1024)
            if not isinstance(m, dict) or m.get("format_version") != 1 or m.get("run_id") != run_id or m.get("checkpoint_id") != directory.name or not valid_checkpoint(m):
                raise ValueError("invalid_checkpoint_metadata")
            take(m)
            result["checkpoints"].append(m)
        except (OSError, ValueError, UnicodeError) as e:
            warn(directory.name, e)
    result["checkpoints"].sort(key=lambda x: (x.get("act", 0), x.get("floor", 0), x.get("captured_at_utc", ""), x.get("checkpoint_id", "")))
    choices = run / "choices.json"
    try:
        c = read(choices)
        if not isinstance(c, dict) or c.get("format_version") != 1 or c.get("run_id") != run_id or not isinstance(c.get("entries"), list) or len(c["entries"]) > 10000:
            raise ValueError("invalid_choices")
        if not valid_choices(c["entries"]):
            raise ValueError("invalid_choices")
        take(c)
        result["choices"] = [x for x in c["entries"] if isinstance(x, dict)]
    except FileNotFoundError:
        pass
    except (OSError, ValueError, UnicodeError) as e:
        warn("choices", e)
    for path in files(run / "replays"):
        if path.suffix != ".mcr" or not REPLAY.fullmatch(path.stem):
            continue
        rid = path.stem
        entry = dict(id=rid, metadata=None, snapshots=None)
        result["replays"].append(entry)
        try:
            m = read(path.with_suffix(".json"), 1024 * 1024)
            if not isinstance(m, dict) or m.get("format_version") != 1 or m.get("run_id") != run_id or not ID.fullmatch(str(m.get("sha256", ""))):
                raise ValueError("invalid_replay_metadata")
            take(m)
            entry["metadata"] = m
            path = safe(path)
            with path.open("rb") as f:
                size = os.fstat(f.fileno()).st_size
                if size > read_budget[0]:
                    raise ValueError("read_size_limit")
                read_budget[0] -= size
                raw = f.read(MAX_REPLAY + 1)
            if len(raw) > MAX_REPLAY:
                raise ValueError("replay_size_limit")
            if hashlib.sha256(raw).hexdigest() != m["sha256"]:
                raise ValueError("replay_checksum_mismatch")
            h = header(raw)
            if m.get("game_version") != h[0] or (m.get("game_commit") is not None and m["game_commit"] != h[1]) or (m.get("model_id_hash") is not None and m["model_id_hash"] != h[2]):
                raise ValueError("replay_header_mismatch")
            try:
                s = read(path.with_suffix(".snapshots.json"))
                if not isinstance(m.get("game_commit"), str) or not m["game_commit"].strip() or type(m.get("model_id_hash")) is not int:
                    raise ValueError("replay_identity_missing")
                if not isinstance(s, dict) or s.get("format_version") not in (1, 2) or s.get("run_id") != run_id or s.get("replay_sha256") != m["sha256"] or s.get("game_version") != h[0] or s.get("game_commit") != h[1] or s.get("model_id_hash") != h[2] or not isinstance(s.get("frames"), list) or not 1 <= len(s["frames"]) <= 10000:
                    raise ValueError("invalid_replay_snapshots")
                if not all(isinstance(f, dict) and isinstance(f.get("players"), list) and 1 <= len(f["players"]) <= 8 and isinstance(f.get("enemies"), list) and len(f["enemies"]) <= 64 for f in s["frames"]):
                    raise ValueError("invalid_replay_snapshots")
                if not all(isinstance(a, dict) and isinstance(a.get("label"), str) and isinstance(a.get("hp"), int) and isinstance(a.get("max_hp"), int) for f in s["frames"] for a in f["players"] + f["enemies"]):
                    raise ValueError("invalid_replay_snapshots")
                if not valid_frames(s):
                    raise ValueError("invalid_replay_snapshots")
                if not isinstance(s.get("mod_ids"), list) or not isinstance(m.get("mod_ids"), list) or not all(isinstance(x, str) and x.strip() for x in s["mod_ids"] + m["mod_ids"]) or len(set(s["mod_ids"])) != len(s["mod_ids"]) or len(set(m["mod_ids"])) != len(m["mod_ids"]) or sorted(s["mod_ids"]) != sorted(m["mod_ids"]):
                    raise ValueError("replay_snapshots_identity_mismatch")
                take(s)
                entry["snapshots"] = s
            except FileNotFoundError:
                warn(rid, "replay_snapshots_missing")
        except (OSError, ValueError, UnicodeError, TypeError) as e:
            warn(rid, e)
    result["choices"].sort(key=lambda x: (x.get("captured_at_utc", ""), x.get("entry_id", "")))
    result["replays"].sort(key=lambda x: ((x.get("metadata") or {}).get("captured_at_utc", ""), x["id"]))
    return result

def render(data):
    template = (Path(__file__).parent / "replay-viewer.html").read_text(encoding="utf-8-sig")
    payload = json.dumps(data, ensure_ascii=True, separators=(",", ":"), allow_nan=False).replace("<", "\\u003c").replace(">", "\\u003e").replace("&", "\\u0026")
    return template.replace("__REPLAY_DATA__", payload)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile")
    parser.add_argument("run_id")
    parser.add_argument("--out")
    parser.add_argument("--language", choices=("zht", "zhs", "eng"), default="zht")
    args = parser.parse_args()
    try:
        root = safe(Path(args.profile) / "rewind")
        result = collect(root, args.run_id, args.language, log=lambda message: print(message, file=sys.stderr))
        output = safe(Path(args.out) if args.out else root / "exports" / (args.run_id + ".html"))
        exports = safe(root / "exports")
        profile = safe(args.profile)
        if (output == profile or profile in output.parents) and not (exports in output.parents):
            raise ValueError("output_inside_source")
        if output.suffix.lower() != ".html":
            raise ValueError("output_must_be_html")
        output.parent.mkdir(parents=True, exist_ok=True)
        safe(output)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=output.parent, prefix=".spire-rewind-", suffix=".tmp", delete=False) as f:
                temporary = Path(f.name)
                f.write(render(result))
            safe(output)
            os.replace(temporary, output)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
        print(output)
    except (OSError, ValueError, UnicodeError) as e:
        print(str(e), file=sys.stderr)
        return 1
    return 0

if __name__ == "__main__":
    sys.exit(main())
