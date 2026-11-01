import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("export_replay", ROOT / "tools" / "export-replay.py")
export = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export)

class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "rewind"
        self.run = "a" * 64
        self.dir = self.root / "runs" / self.run
        self.dir.mkdir(parents=True)

    def test_safe_json_and_languages(self):
        d = self.dir / "checkpoints" / ("b" * 64)
        d.mkdir(parents=True)
        (d / "meta.json").write_text(json.dumps(dict(format_version=1, run_id=self.run, checkpoint_id="b"*64, room_type="</script><script>alert(1)</script>", act=1, floor=1, hp=1, max_hp=2, gold=0, deck_count=0, game_version="", run_schema_version=0, profile_id=0, seed="", start_time=0, character="", ascension=0, capture_kind="", captured_at_utc="2026-01-01T00:00:00Z", run_sha256="c"*64, mod_ids=[], source="game", label=None)))
        for lang in ("zht", "zhs", "eng"):
            data = export.collect(self.root, self.run, lang)
            self.assertEqual((data["format_version"], data["run_id"], data["language"]), (1, self.run, lang))
            self.assertEqual(data["checkpoints"][0]["room_type"], "</script><script>alert(1)</script>")
            self.assertEqual((data["warnings"], data["choices"], data["replays"]), ([], [], []))
            html = export.render(data)
            self.assertNotIn("</script><script>alert(1)", html)
            self.assertIn("\\u003c/script", html)
            self.assertIn('"language":"' + lang + '"', html)
            self.assertIn("textContent", html)
            self.assertIn("Content-Security-Policy", html)
            for control in ('id="prev"', 'id="next"', 'id="play"', 'id="speed"'):
                self.assertIn(control, html)
        with self.assertRaises(ValueError):
            export.collect(self.root, "../escape", "eng")

    def test_header_hash_missing_and_mismatch(self):
        rid = "act-1-floor-2-" + "c" * 64
        p = self.dir / "replays" / (rid + ".mcr")
        p.parent.mkdir()
        raw = struct.pack("<i", 1) + b"v" + struct.pack("<i", 1) + b"c" + struct.pack("<I", 7)
        p.write_bytes(raw)
        m = dict(format_version=1, run_id=self.run, act=1, floor=2, sha256=hashlib.sha256(raw).hexdigest(), game_version="v", game_commit="c", model_id_hash=7, mod_ids=["mod"])
        p.with_suffix(".json").write_text(json.dumps(m))
        data = export.collect(self.root, self.run, "eng")
        self.assertTrue(any("replay_snapshots_missing" in x for x in data["warnings"]))
        snapshot = dict(format_version=2, run_id=self.run, replay_sha256=m["sha256"], game_version="v", game_commit="c", model_id_hash=7, mod_ids=["different"], frames=[dict(turn=1, players=[dict(label="p", hp=1, max_hp=2)], enemies=[])])
        p.with_suffix(".snapshots.json").write_text(json.dumps(snapshot))
        data = export.collect(self.root, self.run, "eng")
        self.assertTrue(any("identity_mismatch" in x for x in data["warnings"]))
        snapshot["mod_ids"] = ["mod"]
        p.with_suffix(".snapshots.json").write_text(json.dumps(snapshot))
        self.assertEqual(export.collect(self.root, self.run, "eng")["replays"][0]["snapshots"]["format_version"], 2)
        snapshot["frames"][0]["players"][0]["hp"] = 999
        p.with_suffix(".snapshots.json").write_text(json.dumps(snapshot))
        self.assertTrue(any("invalid_replay_snapshots" in x for x in export.collect(self.root, self.run, "eng")["warnings"]))
        p.write_bytes(raw + b"x")
        data = export.collect(self.root, self.run, "eng")
        self.assertTrue(any("replay_checksum_mismatch" in x for x in data["warnings"]))
        with self.assertRaises(ValueError):
            export.header(b"bad")

    def test_choices_capacity(self):
        entries = [dict(entry_id=format(i, "032x"), kind="card_reward", act=1, floor=1, captured_at_utc="2026-01-01T00:00:00Z", source="game") for i in range(2001)]
        (self.dir / "choices.json").write_text(json.dumps(dict(format_version=1, run_id=self.run, entries=entries)))
        self.assertEqual(len(export.collect(self.root, self.run, "eng")["choices"]), 2001)
        entries.append(entries[0])
        (self.dir / "choices.json").write_text(json.dumps(dict(format_version=1, run_id=self.run, entries=entries)))
        self.assertTrue(any("invalid_choices" in x for x in export.collect(self.root, self.run, "eng")["warnings"]))

    def test_warnings_carry_codes_not_paths(self):
        (self.dir / "checkpoints" / ("e" * 64)).mkdir(parents=True)
        rid = "act-1-floor-1-" + "f" * 64
        (self.dir / "replays").mkdir()
        (self.dir / "replays" / (rid + ".mcr")).write_bytes(b"\x01\x02\x03")
        (self.dir / "replays" / (rid + ".json")).write_text("{ not json")
        logged = []
        data = export.collect(self.root, self.run, "eng", log=logged.append)
        self.assertIn("e" * 64 + ": file_missing", data["warnings"])
        self.assertIn(rid + ": invalid_json", data["warnings"])
        self.assertEqual(len(logged), 2)
        self.assertTrue(any("meta.json" in x for x in logged), logged)
        html = export.render(data)
        for leak in (str(self.root), str(self.root).replace("\\", "/"), json.dumps(str(self.root))[1:-1],
                     tempfile.gettempdir(), os.environ.get("USERNAME") or os.environ.get("USER") or "\0"):
            self.assertNotIn(leak.lower(), html.lower(), leak)
        self.assertEqual(export.warning_code(PermissionError(13, "denied", "C:/secret")), "access_denied")
        self.assertEqual(export.warning_code(OSError(5, "io", "C:/secret")), "io_error")
        self.assertEqual(export.warning_code(ValueError("link_refused: C:/secret")), "link_refused")
        self.assertEqual(export.warning_code(ValueError("Out of range float values")), "invalid_data")
        self.assertEqual(export.warning_code(UnicodeDecodeError("utf-8", b"\xff", 0, 1, "bad")), "invalid_encoding")

    def test_nan_and_infinity_rejected(self):
        d = self.dir / "checkpoints" / ("b" * 64)
        d.mkdir(parents=True)
        meta = dict(format_version=1, run_id=self.run, checkpoint_id="b"*64, room_type="Monster", act=1, floor=1, hp=1, max_hp=2, gold=0, deck_count=0, game_version="", run_schema_version=0, profile_id=0, seed="", start_time=0, character="", ascension=0, capture_kind="", captured_at_utc="2026-01-01T00:00:00Z", run_sha256="c"*64, mod_ids=[], source="game", label=None)
        for constant in ("NaN", "Infinity", "-Infinity"):
            (d / "meta.json").write_text(json.dumps(meta).replace("null", constant))
            data = export.collect(self.root, self.run, "eng")
            self.assertEqual(data["checkpoints"], [], constant)
            self.assertIn("b" * 64 + ": invalid_number", data["warnings"])
        with self.assertRaises(ValueError):
            export.render(dict(format_version=1, run_id=self.run, language="eng", checkpoints=[dict(label=float("nan"))], replays=[], choices=[], warnings=[]))
        (d / "meta.json").write_text(json.dumps(meta))
        self.assertEqual(len(export.collect(self.root, self.run, "eng")["checkpoints"]), 1)

    def test_symlink_refused(self):
        link = self.dir / "choices.json"
        target = self.root / "target"
        target.write_text("{}")
        try:
            link.symlink_to(target)
        except OSError:
            self.skipTest("symlinks unavailable")
        data = export.collect(self.root, self.run, "eng")
        self.assertTrue(any("link_refused" in x for x in data["warnings"]))

    def test_cli_preserves_sources_and_rejects_unsafe_outputs(self):
        source = self.dir / "choices.json"
        source.write_text(json.dumps(dict(format_version=1, run_id=self.run, entries=[dict(
            entry_id="1" * 32, act=1, floor=1, kind="card_reward", model_id="CARD.EXAMPLE",
            source="game", captured_at_utc="2026-01-01T00:00:00Z")])))
        before = hashlib.sha256(source.read_bytes()).digest()
        def cli(*args):
            return subprocess.run([sys.executable, str(ROOT / "tools/export-replay.py"), self.temp.name, self.run, *args],
                capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        result = cli("--language", "eng")
        self.assertEqual(result.returncode, 0, result.stderr)
        output = self.root / "exports" / (self.run + ".html")
        self.assertIn('"model_id":"CARD.EXAMPLE"', output.read_text(encoding="utf-8"))
        self.assertEqual(hashlib.sha256(source.read_bytes()).digest(), before)
        forbidden = self.dir / "overwrite.html"
        result = cli("--out", str(forbidden))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(forbidden.exists())
        with tempfile.TemporaryDirectory() as target:
            explicit = Path(target) / "external.html"
            result = cli("--out", str(explicit))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue(explicit.is_file())
        self.assertEqual(hashlib.sha256(source.read_bytes()).digest(), before)

if __name__ == "__main__":
    unittest.main()
