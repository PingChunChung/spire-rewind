using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using SpireRewind.Core;

public static class ReplayExportTests
{
    public static void Run(string root)
    {
        string run = new string('a', 64), id = new string('b', 64);
        string dir = Path.Combine(root, "runs", run, "checkpoints", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new CheckpointMetadata
        {
            RunId = run, CheckpointId = id, Act = 1, Floor = 1, Hp = 1, MaxHp = 2,
            RunSha256 = new string('c', 64), CapturedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            RoomType = "</script><script>alert(1)</script>"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        string output = ReplayHtmlExporter.Export(root, run, "eng");
        string html = File.ReadAllText(output);
        if (!html.Contains("Content-Security-Policy") || html.Contains("</script><script>alert(1)"))
            throw new Exception("export must escape embedded JSON and include CSP");
        if (!html.Contains("textContent") || !html.Contains("id=\"play\""))
            throw new Exception("viewer controls missing");
        const string start = "<script id=\"replay-data\" type=\"application/json\">";
        int begin = html.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int end = html.IndexOf("</script>", begin, StringComparison.Ordinal);
        using var envelope = JsonDocument.Parse(html[begin..end]);
        var data = envelope.RootElement;
        if (data.GetProperty("format_version").GetInt32() != 1 || data.GetProperty("run_id").GetString() != run
            || data.GetProperty("language").GetString() != "eng" || data.GetProperty("checkpoints").GetArrayLength() != 1
            || data.GetProperty("checkpoints")[0].GetProperty("room_type").GetString() != "</script><script>alert(1)</script>"
            || data.GetProperty("warnings").GetArrayLength() != 0 || data.GetProperty("choices").GetArrayLength() != 0
            || data.GetProperty("replays").GetArrayLength() != 0) throw new Exception("unexpected export envelope");
        if (!ReplayHtmlExporter.ListRunIds(root).Contains(run)) throw new Exception("run listing failed");
        string choicesPath = Path.Combine(root, "runs", run, "choices.json");
        File.WriteAllText(choicesPath, JsonSerializer.Serialize(new { format_version = 1, run_id = run,
            entries = new[] { new { entry_id = "bad", act = 1, floor = 1, kind = "card_reward", source = "ai", captured_at_utc = "2026-01-01T00:00:00Z" } } }));
        html = File.ReadAllText(ReplayHtmlExporter.Export(root, run));
        if (!html.Contains("invalid_choices")) throw new Exception("invalid choices were not reported");
        CheckWarningsCarryNoPaths(root, run);
        var invalid = new ReplaySnapshotsDocument { FormatVersion = 1, Frames = [new ReplaySnapshotFrame { Turn = 1,
            Players = [new ReplaySnapshotActor { Label = "p", Hp = 999, MaxHp = 1 }], Enemies = [] }] };
        bool invalidRejected = false;
        try { using var session = new ReplaySnapshotSession(invalid); }
        catch (InvalidDataException) { invalidRejected = true; }
        if (!invalidRejected) throw new Exception("invalid snapshot accepted");
        bool refused = false;
        try { ReplayHtmlExporter.Export(root, "../escape"); }
        catch (ArgumentException) { refused = true; }
        if (!refused) throw new Exception("unsafe run ID accepted");
        CheckReplayExport(Path.Combine(root, "complete"));
    }

    // A shared page must not name the player's folders: a checkpoint folder without meta.json and an unreadable replay
    // sidecar produce fixed codes, and the full exceptions reach the local log callback instead.
    private static void CheckWarningsCarryNoPaths(string root, string run)
    {
        string missing = new string('e', 64);
        Directory.CreateDirectory(Path.Combine(root, "runs", run, "checkpoints", missing));
        string replays = Path.Combine(root, "runs", run, "replays"); Directory.CreateDirectory(replays);
        string replayId = "act-1-floor-1-" + new string('f', 64);
        File.WriteAllBytes(Path.Combine(replays, replayId + ".mcr"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(replays, replayId + ".json"), "{ not json");
        var logged = new List<Exception>();
        ReplayHtmlExporter.Log = logged.Add;
        string html;
        try { html = File.ReadAllText(ReplayHtmlExporter.Export(root, run)); }
        finally { ReplayHtmlExporter.Log = null; }
        if (!html.Contains(missing + ": file_missing") || !html.Contains(replayId + ": invalid_json"))
            throw new Exception("missing metadata and corrupt sidecar must produce fixed warning codes");
        if (logged.Count < 2 || !logged.Any(e => e is FileNotFoundException) || !logged.Any(e => e is JsonException))
            throw new Exception("full exceptions must reach the log callback");
        // Raw and JSON-escaped forms of the root, the temp folder and the user name; none may appear in the page.
        var leaks = new[] { root, Path.GetTempPath(), Environment.UserName }
            .SelectMany(x => new[] { x, x.Replace('\\', '/'), JsonSerializer.Serialize(x).Trim('"') })
            .Where(x => x.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string leak in leaks)
            if (html.Contains(leak, StringComparison.OrdinalIgnoreCase)) throw new Exception("export leaks a local path: " + leak);
        Directory.Delete(Path.Combine(root, "runs", run, "checkpoints", missing)); Directory.Delete(replays, true);
    }

    private static void CheckReplayExport(string root)
    {
        string run = new string('d', 64);
        var runtime = new ReplayRuntimeIdentity("historical-version", "historical-commit", 7, ["mod@1"]);
        var repo = new CheckpointRepository(root);
        byte[] raw;
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            foreach (string text in new[] { runtime.GameVersion, runtime.GameCommit })
            { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write(runtime.ModelIdHash); raw = stream.ToArray();
        }
        string path = repo.SaveReplay(run, 1, 2, raw, runtime: runtime);
        string hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var doc = new ReplaySnapshotsDocument { FormatVersion = 2, RunId = run, ReplaySha256 = hash,
            GameVersion = runtime.GameVersion, GameCommit = runtime.GameCommit, ModelIdHash = runtime.ModelIdHash,
            ModIds = ["mod@1"], Frames = [new ReplaySnapshotFrame { Turn = 1, Players = [new ReplaySnapshotActor {
                Label = "Hero", Hp = 30, MaxHp = 40, Block = 5 }], Enemies = [], Actions = ["CARD.STRIKE"] }] };
        new ReplaySnapshotsRepository(root).Write(path, doc);
        new RunChoiceRepository(root).Append(run, new RunChoiceEntry { EntryId = new string('1', 32), Act = 1,
            Floor = 2, Kind = "card_reward", ModelId = "CARD.EXAMPLE", CapturedAtUtc = DateTimeOffset.UtcNow });
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        string? sourceHash = null;
        JsonDocument Export()
        {
            string html = File.ReadAllText(ReplayHtmlExporter.Export(root, run));
            const string start = "<script id=\"replay-data\" type=\"application/json\">";
            int begin = html.IndexOf(start, StringComparison.Ordinal) + start.Length;
            return JsonDocument.Parse(html[begin..html.IndexOf("</script>", begin, StringComparison.Ordinal)]);
        }
        sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        using (var data = Export())
        {
            var replay = data.RootElement.GetProperty("replays")[0];
            if (replay.GetProperty("snapshots").GetProperty("frames")[0].GetProperty("actions")[0].GetString() != "CARD.STRIKE"
                || data.RootElement.GetProperty("choices").GetArrayLength() != 1) throw new Exception("complete replay did not export");
        }
        File.WriteAllText(Path.ChangeExtension(path, ".snapshots.json"), JsonSerializer.Serialize(doc with {
            Frames = [doc.Frames[0] with { Turn = -1 }] }, json));
        using (var data = Export())
        {
            if (data.RootElement.GetProperty("replays")[0].GetProperty("snapshots").ValueKind != JsonValueKind.Null
                || !data.RootElement.GetProperty("warnings").EnumerateArray().Any(x => x.GetString()!.Contains("invalid_replay_snapshots")))
                throw new Exception("invalid snapshot must warn without dropping choices");
            if (data.RootElement.GetProperty("choices").GetArrayLength() != 1) throw new Exception("valid choice was lost");
        }
        string afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (sourceHash != afterHash) throw new Exception("export changed recording");
    }
}
