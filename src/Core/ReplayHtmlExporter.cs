using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireRewind.Core;

/// <summary>Produces a self-contained viewer from bounded metadata, never invoking the game replay parser.</summary>
public static class ReplayHtmlExporter
{
    private const int MaxItems = 2000, MaxJson = 8 * 1024 * 1024;
    /// <summary>Receives the full exception behind every warning. The page itself carries only a fixed code.</summary>
    public static Action<Exception>? Log { get; set; }
    // Exception messages from the file system name the player's folders (user name, account and profile paths); the
    // shared page gets a fixed code and the entry id only, the full exception goes to the local log.
    private static string Code(Exception e)
    {
        try { Log?.Invoke(e); } catch { }
        return e switch
        {
            FileNotFoundException or DirectoryNotFoundException => "file_missing",
            UnauthorizedAccessException => "access_denied",
            JsonException => "invalid_json",
            OverflowException => "invalid_number",
            InvalidDataException or ArgumentException or InvalidOperationException when IsCode(e.Message) => e.Message,
            IOException => "io_error",
            _ => "read_error",
        };
    }
    private static bool IsCode(string s) => s.Length is > 0 and <= 64 && s.All(c => c is >= 'a' and <= 'z' or '_');
    private static void Safe(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("link_refused"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    private static JsonObject Read(string path, ref long budget, int max = MaxJson)
    {
        Safe(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length > budget) throw new InvalidDataException("read_size_limit");
        budget -= f.Length;
        if (f.Length > max) throw new InvalidDataException("size_limit");
        byte[] bytes = new byte[(int)f.Length]; f.ReadExactly(bytes);
        using var doc = JsonDocument.Parse(bytes);
        CheckDuplicates(doc.RootElement);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("invalid_json");
    }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!seen.Add(p.Name)) throw new InvalidDataException("duplicate_key"); CheckDuplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) CheckDuplicates(x);
    }
    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static uint? U(JsonNode? n) => n is JsonValue v && v.TryGetValue<uint>(out var i) ? i : null;
    private static void ValidateChoices(JsonObject c, string runId)
    {
        if (I(c["format_version"]) != 1 || S(c["run_id"]) != runId || c["entries"] is not JsonArray entries || entries.Count > 10000) throw new InvalidDataException("invalid_choices");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in entries)
        {
            if (n is not JsonObject e || S(e["entry_id"]) is not string id || !Guid.TryParseExact(id, "N", out _) || !ids.Add(id)
                || I(e["act"]) is not int act || act is < 1 or > 1000 || I(e["floor"]) is not int floor || floor is < 0 or > 1000000
                || S(e["kind"]) is not ("card_reward" or "shop_purchase" or "card_removal" or "event_option") || S(e["source"]) != "game"
                || !DateTimeOffset.TryParse(S(e["captured_at_utc"]), out var time) || time == default || time.Offset != TimeSpan.Zero) throw new InvalidDataException("invalid_choices");
            foreach (var (key, max) in new[] { ("model_id", 256), ("text_key", 256), ("value", 256), ("ai_reason", 2048) })
                if (e[key] != null && (S(e[key]) is not string s || string.IsNullOrWhiteSpace(s) || s.Length > max || s.Any(char.IsControl))) throw new InvalidDataException("invalid_choices");
        }
    }
    private static IEnumerable<string> Entries(string path, JsonArray warnings)
    {
        Safe(path);
        if (!Directory.Exists(path)) return [];
        var result = Directory.EnumerateFileSystemEntries(path).Take(MaxItems + 1).Order(StringComparer.Ordinal).ToArray();
        if (result.Length > MaxItems) warnings.Add(Path.GetFileName(path) + ": item_limit");
        return result.Take(MaxItems);
    }
    public static IReadOnlyList<string> ListRunIds(string rewindRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rewindRoot);
        var warnings = new JsonArray();
        var ids = new List<string>();
        foreach (string p in Entries(Path.Combine(Path.GetFullPath(rewindRoot), "runs"), warnings))
        {
            if (!CoreFormat.IsId(Path.GetFileName(p))) continue;
            try { Safe(p); if (Directory.Exists(p)) ids.Add(Path.GetFileName(p)); }
            catch (Exception e) when (IsInput(e)) { }
        }
        return ids;
    }
    public static string Export(string rewindRoot, string runId, string language = "zht")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rewindRoot); CoreFormat.RequireId(runId);
        if (language is not ("zht" or "zhs" or "eng")) throw new ArgumentException("invalid_language");
        string root = Path.GetFullPath(rewindRoot), run = Path.Combine(root, "runs", runId); Safe(run);
        if (!Directory.Exists(run)) throw new DirectoryNotFoundException("run_missing");
        var checkpoints = new JsonArray(); var replays = new JsonArray(); var choices = new JsonArray(); var warnings = new JsonArray();
        long readBudget = 64 * 1024 * 1024, outputBudget = 32 * 1024 * 1024;
        void Take(JsonNode n) { int size = Encoding.UTF8.GetByteCount(n.ToJsonString()); if (size > outputBudget) throw new InvalidDataException("export_size_limit"); outputBudget -= size; }
        foreach (string dir in Entries(Path.Combine(run, "checkpoints"), warnings))
        {
            string id = Path.GetFileName(dir); if (!CoreFormat.IsId(id)) continue;
            try
            {
                var m = Read(Path.Combine(dir, "meta.json"), ref readBudget, 1024 * 1024);
                if (I(m["format_version"]) != 1 || S(m["run_id"]) != runId || S(m["checkpoint_id"]) != id) throw new InvalidDataException("invalid_checkpoint_metadata");
                var checkpoint = m.Deserialize<CheckpointMetadata>(CoreFormat.Json) ?? throw new InvalidDataException("invalid_checkpoint_metadata");
                if (!CoreFormat.IsId(checkpoint.RunSha256) || checkpoint.Act is < 1 or > 1000 || checkpoint.Floor is < 0 or > 1000000 || checkpoint.Hp < 0 || checkpoint.MaxHp < 1 || checkpoint.Hp > checkpoint.MaxHp || checkpoint.Gold < 0 || checkpoint.DeckCount < 0 || checkpoint.ModIds == null || checkpoint.CapturedAtUtc == default) throw new InvalidDataException("invalid_checkpoint_metadata");
                Take(m);
                checkpoints.Add(m);
            }
            catch (Exception e) when (IsInput(e)) { warnings.Add(id + ": " + Code(e)); }
        }
        var ordered = checkpoints.Select(x => x!.DeepClone()).OrderBy(x => I(x!["act"]) ?? 0).ThenBy(x => I(x!["floor"]) ?? 0).ThenBy(x => S(x!["captured_at_utc"])).ThenBy(x => S(x!["checkpoint_id"])).ToArray();
        checkpoints.Clear(); foreach (var x in ordered) checkpoints.Add(x);
        try
        {
            var c = Read(Path.Combine(run, "choices.json"), ref readBudget);
            ValidateChoices(c, runId);
            Take(c);
            foreach (var x in (JsonArray)c["entries"]!) choices.Add(x!.DeepClone());
        }
        catch (FileNotFoundException) { }
        catch (Exception e) when (IsInput(e)) { warnings.Add("choices: " + Code(e)); }
        foreach (string path in Entries(Path.Combine(run, "replays"), warnings))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (!path.EndsWith(".mcr", StringComparison.Ordinal) || !ReplayArchiveRepository.IsReplayId(id)) continue;
            var entry = new JsonObject { ["id"] = id, ["metadata"] = null, ["snapshots"] = null }; replays.Add(entry);
            try
            {
                var m = Read(Path.ChangeExtension(path, ".json"), ref readBudget, 1024 * 1024);
                string? hash = S(m["sha256"]);
                if (I(m["format_version"]) != 1 || S(m["run_id"]) != runId || !CoreFormat.IsId(hash)) throw new InvalidDataException("invalid_replay_metadata");
                Take(m); entry["metadata"] = m.DeepClone();
                Safe(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > readBudget) throw new InvalidDataException("read_size_limit");
                readBudget -= file.Length;
                if (file.Length < 12 || file.Length > ReplayArchiveRepository.MaximumInspectionBytes) throw new InvalidDataException("replay_size_limit");
                var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash!), SHA256.HashData(bytes))) throw new InvalidDataException("replay_checksum_mismatch");
                var h = ReplayArchiveRepository.ReadHeader(bytes);
                if (S(m["game_version"]) != h.GameVersion || (m["game_commit"] != null && S(m["game_commit"]) != h.GameCommit) || (m["model_id_hash"] != null && U(m["model_id_hash"]) != h.ModelIdHash)) throw new InvalidDataException("replay_header_mismatch");
                try
                {
                    var s = Read(Path.ChangeExtension(path, ".snapshots.json"), ref readBudget);
                    if (string.IsNullOrWhiteSpace(S(m["game_commit"])) || U(m["model_id_hash"]) == null) throw new InvalidDataException("replay_identity_missing");
                    if (I(s["format_version"]) is not (1 or 2) || S(s["run_id"]) != runId || S(s["replay_sha256"]) != hash || S(s["game_version"]) != h.GameVersion || S(s["game_commit"]) != h.GameCommit || U(s["model_id_hash"]) != h.ModelIdHash || s["frames"] is not JsonArray frames || frames.Count is < 1 or > 10000) throw new InvalidDataException("invalid_replay_snapshots");
                    var snapshot = s.Deserialize<ReplaySnapshotsDocument>(CoreFormat.Json) ?? throw new InvalidDataException("invalid_replay_snapshots");
                    ReplaySnapshotsRepository.ValidateFrames(snapshot);
                    foreach (var f in frames)
                    {
                        if (f is not JsonObject || f["players"] is not JsonArray players || players.Count is < 1 or > 8 || f["enemies"] is not JsonArray enemies || enemies.Count > 64) throw new InvalidDataException("invalid_replay_snapshots");
                        foreach (var actor in players.Concat(enemies)) if (actor is not JsonObject || S(actor["label"]) == null || I(actor["hp"]) == null || I(actor["max_hp"]) == null) throw new InvalidDataException("invalid_replay_snapshots");
                    }
                    if (s["mod_ids"] is not JsonArray sm || m["mod_ids"] is not JsonArray mm || sm.Any(x => string.IsNullOrWhiteSpace(S(x))) || mm.Any(x => string.IsNullOrWhiteSpace(S(x))) || sm.Select(S).Distinct(StringComparer.Ordinal).Count() != sm.Count || mm.Select(S).Distinct(StringComparer.Ordinal).Count() != mm.Count || !sm.Select(S).Order(StringComparer.Ordinal).SequenceEqual(mm.Select(S).Order(StringComparer.Ordinal))) throw new InvalidDataException("replay_snapshots_identity_mismatch");
                    Take(s);
                    entry["snapshots"] = s;
                }
                catch (FileNotFoundException) { warnings.Add(id + ": replay_snapshots_missing"); }
            }
            catch (Exception e) when (IsInput(e)) { warnings.Add(id + ": " + Code(e)); }
        }
        var sortedChoices = choices.Select(x => x!.DeepClone()).OrderBy(x => S(x!["captured_at_utc"])).ThenBy(x => S(x!["entry_id"])).ToArray();
        choices.Clear(); foreach (var x in sortedChoices) choices.Add(x);
        var sortedReplays = replays.Select(x => x!.DeepClone()).OrderBy(x => S(x!["metadata"]?["captured_at_utc"])).ThenBy(x => S(x!["id"])).ToArray();
        replays.Clear(); foreach (var x in sortedReplays) replays.Add(x);
        var data = new JsonObject { ["format_version"] = 1, ["run_id"] = runId, ["language"] = language, ["checkpoints"] = checkpoints, ["replays"] = replays, ["choices"] = choices, ["warnings"] = warnings };
        using var resource = typeof(ReplayHtmlExporter).Assembly.GetManifestResourceStream("SpireRewind.replay-viewer.html") ?? throw new InvalidOperationException("viewer_template_missing");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string html = reader.ReadToEnd().Replace("__REPLAY_DATA__", data.ToJsonString(), StringComparison.Ordinal);
        string exports = Path.Combine(root, "exports"), output = Path.Combine(exports, runId + ".html"); Safe(output);
        Directory.CreateDirectory(exports); Safe(output);
        string temp = Path.Combine(exports, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Safe(temp);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(html);
            Safe(output);
            File.Move(temp, output, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return output;
    }
    private static bool IsInput(Exception e) => e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException;
}
