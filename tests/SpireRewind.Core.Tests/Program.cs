using SpireRewind.Core;
using System.Text.Json;

string root = Path.Combine(Path.GetTempPath(), "spire-rewind-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Assert(bool b, string message) { if (!b) throw new Exception(message); }
    void Throws(Action a) { try { a(); } catch (Exception) { return; } throw new Exception("Expected failure"); }
    string run = RunIdentity.Create(1, "seed/../", 100, "ironclad");
    Assert(run.Length == 64 && run.All(c => "0123456789abcdef".Contains(c)), "safe identity");
    Assert(run != RunIdentity.Create(1, "seed/../", 101, "ironclad"), "start time identity");
    var repo = new CheckpointRepository(root);
    var m = new CheckpointMetadata { RunId = run, ProfileId = 1, Seed = "seed/../", StartTime = 100, Character = "ironclad", GameVersion = "v", RunSchemaVersion = 3, Act = 1, Floor = 5, ModIds = ["a", "b"], CapturedAtUtc = DateTimeOffset.UtcNow };
    var first = repo.SaveCheckpoint(m, "{\"hp\":42}");
    var second = repo.SaveCheckpoint(m with { CapturedAtUtc = m.CapturedAtUtc.AddSeconds(1) }, "{}");
    Assert(first.CheckpointId != second.CheckpointId && repo.ListCheckpoints(run)[0].CheckpointId == second.CheckpointId, "IDs and ordering");
    Assert(repo.ReadCheckpoint(first) == "{\"hp\":42}", "read");
    string meta = Path.Combine(root, "runs", run, "checkpoints", first.CheckpointId, "meta.json");
    string metaJson = File.ReadAllText(meta);
    Assert(metaJson.Contains("\"run_sha256\"") && metaJson.Contains("\"checkpoint_id\""), "snake case");
    Assert(JsonSerializer.Deserialize<CheckpointMetadata>(metaJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })?.RunId == run, "roundtrip");
    Assert(RestoreValidation.Validate(first, run, 1, "v", 3, ["b", "a"], true, false) is null, "valid restore");
    Assert(RestoreValidation.Validate(first, run, 2, "v", 3, ["a", "b"], true, false) == "profile_mismatch", "profile");
    Assert(RestoreValidation.Validate(first, run, 1, "v", 3, ["a"], true, false) == "mod_mismatch", "mods");
    Assert(RestoreValidation.Validate(first, run, 1, "other", 3, ["a", "b"], true, false) == "game_version_mismatch", "version");
    Assert(RestoreValidation.Validate(first, run, 1, "v", 4, ["a", "b"], true, false) == "schema_mismatch", "schema");
    Assert(RestoreValidation.Validate(first, RunIdentity.Create(1, "other", 1, "ironclad"), 1, "v", 3, ["a", "b"], true, false) == "run_mismatch", "run");
    Assert(RestoreValidation.Validate(first with { FormatVersion = 2 }, run, 1, "v", 3, ["a", "b"], true, false) == "unsupported_format", "format");
    Assert(RestoreValidation.Validate(first, run, 1, "v", 3, ["a", "b"], false, false) == "not_single_player", "multiplayer");
    Assert(RestoreValidation.Validate(first, run, 1, "v", 3, ["a", "b"], true, true) == "daily_run", "daily");
    Assert(RestoreValidation.Validate(first with { RunSha256 = "../x" }, run, 1, "v", 3, [], true, false) == "invalid_metadata", "invalid hash");
    Throws(() => repo.ListCheckpoints("../escape"));
    string replay = repo.SaveReplay(run, 1, 5, [1, 2, 3]);
    Assert(File.Exists(replay) && File.Exists(Path.ChangeExtension(replay, ".json")) && replay != repo.SaveReplay(run, 1, 5, [1]), "replay sidecar");
    Assert(CheckpointRepository.MaxReplayBytes == 64 * 1024 * 1024, "replay limit");
    Throws(() => repo.SaveReplay(run, 1, 5, new byte[CheckpointRepository.MaxReplayBytes + 1]));
    Assert(repo.WriteSettings(new RewindSettings { RetainedRuns = 0 }).RetainedRuns == 1 && repo.ReadSettings().RetainedRuns == 1, "settings min");
    Assert(repo.WriteSettings(new RewindSettings { RetainedRuns = 101 }).RetainedRuns == 100, "settings max");
    Assert(repo.WriteSettings(new RewindSettings { Language = "zht" }).Language == "zht" && repo.ReadSettings().Language == "zht", "language persistence");
    Assert(repo.WriteSettings(new RewindSettings { Language = "invalid" }).Language == "auto", "invalid language");
    Assert(!repo.IsMarked(run), "missing mark");
    repo.RecordRestore(run, first.CheckpointId, true); repo.RecordRestore(run, second.CheckpointId, false);
    Assert(repo.GetRewindCount(run) == 2 && repo.IsMarked(run), "restore record");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(meta)!, "run.save.json"), "corrupt");
    Throws(() => repo.ReadCheckpoint(first));
    File.WriteAllText(meta, "{");
    Assert(repo.ListCheckpoints(run).Count == 1, "truncated metadata omitted");
    Throws(() => repo.ReadCheckpoint(first));
    File.WriteAllText(meta, "{}");
    Assert(repo.ListCheckpoints(run).Count == 1, "incomplete metadata omitted");
    string old = RunIdentity.Create(1, "other", 1, "ironclad");
    repo.SaveCheckpoint(m with { RunId = old, CapturedAtUtc = m.CapturedAtUtc.AddDays(-1) }, "{}");
    repo.ApplyRetention(1, run);
    Assert(Directory.Exists(Path.Combine(root, "runs", run)) && !Directory.Exists(Path.Combine(root, "runs", old)), "active retention");
    // Lowering the count with no active run (settings saved after the run ended) prunes to the N newest runs.
    string older = RunIdentity.Create(1, "older", 1, "ironclad"), oldest = RunIdentity.Create(1, "oldest", 1, "ironclad");
    repo.SaveCheckpoint(m with { RunId = older, CapturedAtUtc = m.CapturedAtUtc.AddDays(-1) }, "{}");
    repo.SaveCheckpoint(m with { RunId = oldest, CapturedAtUtc = m.CapturedAtUtc.AddDays(-2) }, "{}");
    repo.ApplyRetention(10, null);
    Assert(new[] { run, older, oldest }.All(id => Directory.Exists(Path.Combine(root, "runs", id))), "retention 10 without an active run keeps all three");
    repo.ApplyRetention(1, null);
    Assert(Directory.Exists(Path.Combine(root, "runs", run)) && !Directory.Exists(Path.Combine(root, "runs", older)) && !Directory.Exists(Path.Combine(root, "runs", oldest)), "retention 1 without an active run keeps only the newest");
    Throws(() => repo.ApplyRetention(1, "../x"));
    try
    {
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        string linkedId = RunIdentity.Create(1, "link", 1, "ironclad");
        Directory.CreateSymbolicLink(Path.Combine(root, "runs", linkedId), outside);
        Throws(() => repo.SaveCheckpoint(m with { RunId = linkedId }, "{}"));
        repo.ApplyRetention(1, run);
        Assert(Directory.Exists(outside), "symlink target retained");
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("Symlink test skipped: unavailable permissions."); }
    await VerifiedSaveWriterTests.Run();
    await RestoreRecoveryTests.Run();
    RestoreFaultTests.Run();
    CheckpointStageTests.Run();
    TestCommandTests.Run();
    ReplayArchiveTests.Run();
    ProfileAuditTests.Run();
    ReplaySnapshotTests.Run();
    RunChoiceTests.Run();
    RoomHistoryCursorTests.Run();
    ReplayExportTests.Run(Path.Combine(root, "html-export"));
    Console.WriteLine("All core tests passed.");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally { try { Directory.Delete(root, true); } catch { } }
