using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpireRewind.Core;

internal static class ReplayArchiveTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "spire-rewind-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { CheckArchive(root); }
        finally { Directory.Delete(root, true); }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static void Assert(bool value, string name) { if (!value) throw new Exception("Replay test failed: " + name); }
    private static void Throws(Action action, string name)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return; }
        throw new Exception("Replay test did not reject: " + name);
    }

    private static byte[] Bytes(string version = "v1", string commit = "commit", uint model = 42)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (string value in new[] { version, commit })
        {
            byte[] text = Encoding.UTF8.GetBytes(value); writer.Write(text.Length); writer.Write(text);
        }
        writer.Write(model);
        // Intentionally invalid game payload. Inspection must never decode or dispatch it.
        writer.Write(new byte[] { 255, 255, 255, 255 });
        return stream.ToArray();
    }

    private static void CheckArchive(string root)
    {
        var repo = new CheckpointRepository(root);
        var runtime = new ReplayRuntimeIdentity("v1", "commit", 42, ["Rewind@1", "other@2"]);
        string run = RunIdentity.Create(1, "seed", 1, "character");
        string oldRun = RunIdentity.Create(1, "older", 1, "character");
        byte[] original = Bytes();
        string path = repo.SaveReplay(run, 1, 2, original, runtime: runtime, captureKind: "combat_completed", complete: true);
        repo.SaveReplay(oldRun, 2, 3, original, runtime: runtime);
        var entries = repo.ListReplays();
        Assert(entries.Count == 2 && entries.Select(x => x.RunId).Distinct().Count() == 2, "all runs listed");
        var entry = entries.Single(x => x.RunId == run);
        var originalMetadata = entry.Metadata!;
        string metadataPath = Path.ChangeExtension(path, ".json");
        void Meta(ReplayArchiveMetadata m) => File.WriteAllText(metadataPath, JsonSerializer.Serialize(m, Json));
        ReplayInspection Inspect() => repo.InspectReplay(entry, runtime);
        void Error(string expected, string test) => Assert(Inspect().Error == expected, test + ": " + Inspect().Error);
        var result = Inspect();
        Assert(result.Compatible && result.Header is { GameVersion: "v1", GameCommit: "commit", ModelIdHash: 42 }
            && !result.PayloadDecoded && !result.PlaybackAvailable && result.Metadata!.Complete, "header only, no fake playback");
        Assert(repo.InspectReplay(entry with { Metadata = originalMetadata with { Sha256 = new string('0', 64) } }, runtime).Compatible,
            "caller metadata reloaded");
        Assert(repo.InspectReplay(entry, runtime with { ModIds = ["other@2", "Rewind@1"] }).Compatible, "mod order irrelevant");

        Assert(repo.InspectReplay(entry, runtime with { GameVersion = "v2" }).Error == "replay_version_mismatch", "runtime version");
        Assert(repo.InspectReplay(entry, runtime with { GameCommit = "other" }).Error == "replay_commit_mismatch", "runtime commit");
        Assert(repo.InspectReplay(entry, runtime with { ModelIdHash = 43 }).Error == "replay_model_hash_mismatch", "runtime model");
        Assert(repo.InspectReplay(entry, runtime with { ModIds = ["Rewind@1", "other@3"] }).Error == "replay_mod_mismatch", "runtime mods version");
        Assert(repo.InspectReplay(entry, runtime with { GameCommit = "" }).Error == "replay_runtime_unavailable", "runtime absent");
        Meta(originalMetadata with { GameVersion = "other" }); Error("replay_version_mismatch", "manifest version");
        Meta(originalMetadata with { GameCommit = "other" }); Error("replay_commit_mismatch", "manifest commit");
        Meta(originalMetadata with { ModelIdHash = 0 }); Error("replay_model_hash_mismatch", "manifest model");
        Meta(originalMetadata with { ModIds = ["other@2"] }); Error("replay_mod_mismatch", "manifest mods");
        Meta(originalMetadata with { ModIds = ["other@2", "other@2"] }); Error("invalid_replay_metadata", "duplicate mods");
        Meta(originalMetadata with { GameCommit = null, ModelIdHash = null }); Error("replay_identity_missing", "legacy manifest inspect only");
        Meta(originalMetadata with { Sha256 = new string('0', 64) }); Error("replay_checksum_mismatch", "integrity");
        Meta(originalMetadata with { RunId = oldRun }); Error("invalid_replay_metadata", "run binding");
        Meta(originalMetadata with { Sha256 = "../outside" }); Error("invalid_replay_metadata", "invalid sha");
        File.WriteAllText(metadataPath, "{}"); Error("invalid_replay_metadata", "required metadata");
        File.WriteAllText(metadataPath, "{"); Error("invalid_replay_metadata", "truncated metadata");
        Assert(repo.ListReplays().Single(x => x.RunId == run).Error != null, "bad archive remains discoverable");
        File.Delete(metadataPath); Error("replay_archive_missing", "missing metadata");
        Meta(originalMetadata);

        void Payload(byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            Meta(originalMetadata with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }
        Payload(Bytes("v2")); Error("replay_version_mismatch", "header version");
        Payload(Bytes(commit: "different")); Error("replay_commit_mismatch", "header commit");
        Payload(Bytes(model: 0)); Error("replay_model_hash_mismatch", "header model");
        Payload([1, 2]); Error("replay_size_limit", "tiny input");
        Payload(new byte[ReplayArchiveRepository.MaximumInspectionBytes + 1]); Error("replay_size_limit", "size bound");
        Payload(original);
        Assert(repo.InspectReplay(entry with { RunId = "../x" }, runtime).Error == "invalid_replay_metadata", "run traversal");
        Assert(repo.InspectReplay(entry with { ReplayId = "../" + entry.ReplayId }, runtime).Error == "invalid_replay_id", "replay traversal");
        Assert(repo.InspectReplay(entry with { ReplayId = Path.GetFullPath(path) }, runtime).Error == "invalid_replay_id", "absolute path");

        foreach (int n in Enumerable.Range(0, ReplayArchiveRepository.ReadHeader(original).HeaderBytes))
            Throws(() => ReplayArchiveRepository.ReadHeader(original.AsSpan(0, n)), "truncated at " + n);
        foreach (int n in new[] { -1, 0, 129, int.MaxValue })
        {
            byte[] bad = (byte[])original.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bad, n);
            Throws(() => ReplayArchiveRepository.ReadHeader(bad), "header string bound " + n);
        }
        byte[] utf8 = (byte[])original.Clone(); utf8[4] = 255;
        Throws(() => ReplayArchiveRepository.ReadHeader(utf8), "strict utf8");
        byte[] control = (byte[])original.Clone(); control[4] = 10;
        Throws(() => ReplayArchiveRepository.ReadHeader(control), "control characters");

        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
        for (int i = 0; i < 3; i++) { repo.ListReplays(); Assert(Inspect().Compatible, "repeat inspection"); }
        var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert(after.Length == before.Count && after.All(p => before[p] == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))))
            && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "no archive writes");

        string link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, root);
            Throws(() => new ReplayArchiveRepository(link), "linked root refused");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("Replay symlink test skipped: permission unavailable; run manual reparse test."); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }

        string replayLink = Path.Combine(root, "runs", run, "replays", "act-1-floor-3-" + new string('a', 64) + ".mcr");
        try
        {
            File.CreateSymbolicLink(replayLink, path);
            var linked = repo.ListReplays().Single(x => x.ReplayId == Path.GetFileNameWithoutExtension(replayLink));
            Assert(linked.Error == "replay_archive_unreadable" && repo.InspectReplay(linked, runtime).Error != null, "linked archive refused");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("Replay file symlink test skipped: permission unavailable."); }
        finally { if (File.Exists(replayLink)) File.Delete(replayLink); }
    }
}
