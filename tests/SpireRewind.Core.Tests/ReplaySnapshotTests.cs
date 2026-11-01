using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SpireRewind.Core;

internal static class ReplaySnapshotTests
{
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception("Snapshot test: " + message); }
    private static void Throws(Action a) { try { a(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or ObjectDisposedException) { return; } throw new Exception("Expected rejection"); }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "spire-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Test(root); } finally { Directory.Delete(root, true); }
    }
    private static void Test(string root)
    {
        var runtime = new ReplayRuntimeIdentity("v1", "commit", 42, ["a", "b"]);
        byte[] payload;
        using (var stream = new MemoryStream())
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, true);
            foreach (string part in new[] { "v1", "commit" }) { byte[] b = Encoding.UTF8.GetBytes(part); w.Write(b.Length); w.Write(b); }
            w.Write((uint)42); payload = stream.ToArray();
        }
        string profile = Path.Combine(root, "profile"), settings = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(Path.Combine(profile, "saves"));
        File.WriteAllText(Path.Combine(profile, "saves", "run.save"), "unchanged save");
        File.WriteAllText(settings, "unchanged machine settings");
        var archives = new CheckpointRepository(profile);
        string run = RunIdentity.Create(1, "seed", 1, "hero");
        string replay = archives.SaveReplay(run, 1, 2, payload, runtime: runtime);
        var entry = archives.ListReplays().Single();
        var actor = new ReplaySnapshotActor { Label = "Hero", Hp = 20, MaxHp = 30 };
        var frame = new ReplaySnapshotFrame { Turn = 1, Players = [actor], Enemies = [actor] };
        var doc = new ReplaySnapshotsDocument { ReplaySha256 = entry.Metadata!.Sha256, RunId = run, GameVersion = "v1", GameCommit = "commit", ModelIdHash = 42, ModIds = ["a", "b"], Frames = [frame, frame with { Turn = 2 }, frame with { Turn = 3 }] };
        var repo = new ReplaySnapshotsRepository(profile);
        string sidecar = Path.Combine(Path.GetDirectoryName(replay)!, entry.ReplayId + ".snapshots.json");
        Assert(repo.ReadForReplay(entry, runtime).Error == "replay_snapshots_missing", "missing does not invent frames");
        repo.Write(replay, doc);
        Assert(repo.ReadForReplay(entry with { Metadata = null }, runtime).Document!.Frames.Length == 3, "read reloads metadata");
        Assert(repo.ReadForReplay(entry, runtime with { GameVersion = "wrong" }).Error == "replay_version_mismatch", "inspect before sidecar");
        string good = File.ReadAllText(sidecar);
        var v2 = doc with { FormatVersion = 2, Frames = [frame with { Players = [actor with { Block = 5, Intent = ["Attack"] }], Cards = ["card"], Actions = ["played"] }] };
        repo.Write(replay, v2);
        var readV2 = repo.ReadForReplay(entry, runtime).Document!;
        Assert(readV2.Frames[0].Players[0].Block == 5 && readV2.Frames[0].Actions!.Value[0] == "played", "v2 round trip");
        Throws(() => repo.Write(replay, v2 with { Frames = [frame with { Players = [actor with { Block = -1 }] }] }));
        Throws(() => repo.Write(replay, v2 with { Frames = [frame with { Cards = [new string('x', 257)] }] }));
        Throws(() => repo.Write(replay, doc with { Frames = [frame with { Cards = [] }] }));
        repo.Write(replay, doc);
        Assert(repo.ReadForReplay(entry, runtime).Document!.Frames[0].Players[0].Block == null, "v1 does not invent block");
        void Reject(ReplaySnapshotsDocument d) { Throws(() => repo.Write(replay, d)); }
        Reject(doc with { FormatVersion = 3 }); Reject(doc with { ReplaySha256 = new string('0', 64) }); Reject(doc with { RunId = new string('0', 64) });
        Reject(doc with { GameVersion = "other" }); Reject(doc with { GameCommit = "other" }); Reject(doc with { ModelIdHash = 1 }); Reject(doc with { ModIds = ["a"] });
        Reject(doc with { Frames = [] }); Reject(doc with { Frames = ImmutableArray.CreateRange(Enumerable.Repeat(frame, 10001)) });
        Reject(doc with { Frames = [frame with { Turn = -1 }] }); Reject(doc with { Frames = [frame with { Turn = 0 }] }); Reject(doc with { Frames = [frame with { Turn = 2 }, frame] });
        Reject(doc with { Frames = [frame with { Players = [] }] }); Reject(doc with { Frames = [frame with { Enemies = ImmutableArray.CreateRange(Enumerable.Repeat(actor, 65)) }] });
        Reject(doc with { Frames = [frame with { Players = [actor with { Label = new string('x', 129) }] }] });
        Reject(doc with { Frames = [frame with { Players = [actor with { Hp = 31 }] }] });
        Throws(() => repo.Write(Path.Combine(root, "..", "other.mcr"), doc));
        File.WriteAllText(sidecar, "{"); Assert(repo.ReadForReplay(entry, runtime).Error == "invalid_replay_snapshots", "corrupt");
        File.WriteAllText(sidecar, good.Replace("\"format_version\": 1", "\"format_version\": 1, \"format_version\": 1"));
        Assert(repo.ReadForReplay(entry, runtime).Error == "invalid_replay_snapshots", "duplicate properties");
        File.WriteAllBytes(sidecar, new byte[ReplaySnapshotsRepository.MaximumBytes + 1]);
        Assert(repo.ReadForReplay(entry, runtime).Error == "replay_snapshots_limit", "size");
        File.WriteAllText(sidecar, good);
        Assert(repo.ReadForReplay(entry with { ReplayId = "../bad" }, runtime).Error != null, "traversal");
        try
        {
            File.Delete(sidecar); File.CreateSymbolicLink(sidecar, replay);
            Assert(repo.ReadForReplay(entry, runtime).Error == "replay_snapshots_link_refused", "link read");
            Throws(() => repo.Write(replay, doc));
            File.Delete(sidecar);
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("Snapshot symlink test skipped: permission unavailable."); }
        repo.Write(replay, doc);
        var before = File.ReadAllBytes(sidecar);
        repo.ReadForReplay(entry, runtime);
        Assert(before.SequenceEqual(File.ReadAllBytes(sidecar)), "read is read-only");

        void AuditRead(string? expected)
        {
            var auditBefore = ProfileAudit.Capture(profile, settings);
            var result = repo.ReadForReplay(entry, runtime);
            Assert(result.Error == expected, "audit expected result " + expected);
            Assert(ProfileAudit.Compare(auditBefore, ProfileAudit.Capture(profile, settings)).Unchanged, "profile and settings unchanged: " + expected);
        }
        AuditRead(null);
        var mismatchBefore = ProfileAudit.Capture(profile, settings);
        Assert(repo.ReadForReplay(entry, runtime with { GameVersion = "different" }).Error == "replay_version_mismatch", "audit version rejection");
        Assert(ProfileAudit.Compare(mismatchBefore, ProfileAudit.Capture(profile, settings)).Unchanged, "version rejection read-only");
        File.Delete(sidecar); AuditRead("replay_snapshots_missing");
        File.WriteAllText(sidecar, "{"); AuditRead("invalid_replay_snapshots");
        File.WriteAllText(sidecar, good);

        using var s = new ReplaySnapshotSession(doc);
        Assert(s.FrameCount == 3 && s.Index == 0 && !s.Playing && !s.Completed, "initial");
        s.Play(); s.Advance(0.5); Assert(s.Index == 0, "fractional"); s.Pause(); s.Play(); s.Advance(0.5); Assert(s.Index == 0, "pause clears elapsed");
        s.SetSpeed(2); s.Advance(0.25); Assert(s.Index == 1, "speed");
        s.Step(); Assert(s.Index == 2 && s.Completed && !s.Playing, "step eof");
        s.Play(); Assert(s.Index == 0 && s.Playing, "restart");
        s.SetSpeed(4); s.Advance(0.5); Assert(s.Index == 2 && s.Completed && !s.Playing, "eof pause");
        s.Previous(); Assert(s.Index == 1 && !s.Completed && !s.Playing && ReplaySnapshotSession.PreviousStrategy == "restart_from_zero_fast_forward", "previous");
        s.Previous(); s.Previous(); Assert(s.Index == 0, "previous boundary");
        Throws(() => s.SetSpeed(3)); Throws(() => s.Advance(-1)); Throws(() => s.Advance(double.NaN)); Throws(() => s.Advance(double.PositiveInfinity));
        s.Dispose(); Throws(() => s.Play());
        using var one = new ReplaySnapshotSession(doc with { Frames = [frame] }); one.Play(); Assert(one.Completed && !one.Playing, "single frame");
        using var many = new ReplaySnapshotSession(doc with { Frames = ImmutableArray.CreateRange(Enumerable.Repeat(frame, 200)) });
        many.Play(); many.Advance(100000); Assert(many.Index == 100, "tick budget");
    }
}
