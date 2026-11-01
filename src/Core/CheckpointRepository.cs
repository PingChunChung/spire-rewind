using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace SpireRewind.Core;

public sealed class CheckpointRepository
{
    public const int MaxRunBytes = 32 * 1024 * 1024;
    public const int MaxReplayBytes = 64 * 1024 * 1024;
    private const int MaxMetadataBytes = 1024 * 1024;
    private readonly string root;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public CheckpointRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        CheckPath(this.root);
    }

    public CheckpointMetadata SaveCheckpoint(CheckpointMetadata metadata, string runJson)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(runJson);
        CoreFormat.RequireId(metadata.RunId);
        if (metadata.FormatVersion != 1 || metadata.ModIds is null) throw new ArgumentException("Invalid metadata.");
        byte[] bytes = Utf8.GetBytes(runJson);
        if (bytes.Length > MaxRunBytes) throw new ArgumentException("Run payload too large.");
        var saved = metadata with { CheckpointId = NewId(), RunSha256 = CoreFormat.Hash(bytes), CapturedAtUtc = metadata.CapturedAtUtc.ToUniversalTime() };
        byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(saved, CoreFormat.Json);
        if (metaBytes.Length > MaxMetadataBytes) throw new ArgumentException("Metadata too large.");
        string dir = CheckpointDir(saved.RunId, saved.CheckpointId);
        EnsureDirectory(dir);
        AtomicWrite(Path.Combine(dir, "run.save.json"), bytes);
        AtomicWrite(Path.Combine(dir, "meta.json"), metaBytes);
        return saved;
    }

    public IReadOnlyList<CheckpointMetadata> ListCheckpoints(string runId)
    {
        CoreFormat.RequireId(runId);
        string dir = Path.Combine(RunDir(runId), "checkpoints");
        CheckPath(dir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<CheckpointMetadata>();
        foreach (string child in Directory.EnumerateDirectories(dir))
        {
            string id = Path.GetFileName(child);
            if (!CoreFormat.IsId(id)) continue;
            try
            {
                CheckPath(child);
                var metadata = ReadJson<CheckpointMetadata>(Path.Combine(child, "meta.json"));
                if (metadata is not null && metadata.FormatVersion == 1 && metadata.RunId == runId && metadata.CheckpointId == id && CoreFormat.IsId(metadata.RunSha256) && metadata.ModIds is not null && File.Exists(Path.Combine(child, "run.save.json"))) result.Add(metadata);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return result.OrderByDescending(m => m.CapturedAtUtc).ThenBy(m => m.CheckpointId, StringComparer.Ordinal).ToArray();
    }

    public string ReadCheckpoint(CheckpointMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        CoreFormat.RequireId(metadata.RunId); CoreFormat.RequireId(metadata.CheckpointId);
        if (!CoreFormat.IsId(metadata.RunSha256) || metadata.FormatVersion != 1) throw new InvalidDataException("Invalid metadata.");
        string dir = CheckpointDir(metadata.RunId, metadata.CheckpointId);
        var stored = ReadJson<CheckpointMetadata>(Path.Combine(dir, "meta.json")) ?? throw new InvalidDataException("Missing metadata.");
        if (stored.FormatVersion != 1 || stored.RunId != metadata.RunId || stored.CheckpointId != metadata.CheckpointId || stored.RunSha256 != metadata.RunSha256) throw new InvalidDataException("Metadata mismatch.");
        byte[] bytes = ReadBounded(Path.Combine(dir, "run.save.json"), MaxRunBytes);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(stored.RunSha256), SHA256.HashData(bytes))) throw new InvalidDataException("Run checksum mismatch.");
        return Utf8.GetString(bytes);
    }

    public string SaveReplay(string runId, int act, int floor, byte[] bytes, CheckpointMetadata? context = null,
        string captureKind = "unknown", bool complete = false, ReplayRuntimeIdentity? runtime = null)
    {
        CoreFormat.RequireId(runId); ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxReplayBytes) throw new ArgumentException("Replay too large.");
        string dir = Path.Combine(RunDir(runId), "replays"); EnsureDirectory(dir);
        string name = $"act-{act}-floor-{floor}-{NewId()}";
        string path = Path.Combine(dir, name + ".mcr");
        AtomicWrite(path, bytes);
        AtomicWrite(Path.Combine(dir, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(new ReplayArchiveMetadata
        {
            FormatVersion = 1, RunId = runId, Act = act, Floor = floor, Sha256 = CoreFormat.Hash(bytes),
            CapturedAtUtc = DateTimeOffset.UtcNow, GameVersion = runtime?.GameVersion ?? context?.GameVersion,
            GameCommit = runtime?.GameCommit, ModelIdHash = runtime?.ModelIdHash,
            RunSchemaVersion = context?.RunSchemaVersion, ModIds = runtime?.ModIds ?? context?.ModIds ?? [],
            Source = context?.Source ?? "game", CaptureKind = captureKind, Complete = complete
        }, CoreFormat.Json));
        return path;
    }

    public IReadOnlyList<ReplayArchiveEntry> ListReplays() => new ReplayArchiveRepository(root).List();
    public ReplayInspection InspectReplay(ReplayArchiveEntry entry, ReplayRuntimeIdentity runtime) =>
        new ReplayArchiveRepository(root).Inspect(entry, runtime);

    /// <summary>Keeps the <paramref name="retainedRuns"/> most recent runs. The active run, when there is one, is never
    /// deleted and occupies one slot; with no active run the newest runs fill every slot.</summary>
    public void ApplyRetention(int retainedRuns, string? activeRunId)
    {
        if (activeRunId != null) CoreFormat.RequireId(activeRunId);
        int count = Math.Clamp(retainedRuns, 1, 100);
        string runs = Path.Combine(root, "runs"); CheckPath(runs);
        if (!Directory.Exists(runs)) return;
        var candidates = new List<(string Id, string Path, DateTimeOffset Time)>();
        foreach (string dir in Directory.EnumerateDirectories(runs))
        {
            string id = Path.GetFileName(dir);
            if (!CoreFormat.IsId(id)) continue;
            try { CheckPath(dir); var newest = ListCheckpoints(id).FirstOrDefault()?.CapturedAtUtc ?? Directory.GetLastWriteTimeUtc(dir); candidates.Add((id, dir, newest)); }
            catch (IOException) { }
        }
        var keep = candidates.OrderByDescending(x => x.Time).ThenBy(x => x.Id, StringComparer.Ordinal).Where(x => x.Id != activeRunId).Take(activeRunId == null ? count : count - 1).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (activeRunId != null) keep.Add(activeRunId);
        foreach (var candidate in candidates.Where(x => !keep.Contains(x.Id)))
        {
            // Refuse to traverse any links; leave a suspect run intact.
            try { CheckTree(candidate.Path); Directory.Delete(candidate.Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public RewindSettings ReadSettings()
    {
        string path = Path.Combine(root, "settings.json");
        CheckPath(path);
        return File.Exists(path) ? (ReadJson<RewindSettings>(path) ?? throw new InvalidDataException("Invalid settings.")).Validate() : new RewindSettings();
    }
    public RewindSettings WriteSettings(RewindSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Validate(); EnsureDirectory(root);
        AtomicWrite(Path.Combine(root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, CoreFormat.Json));
        return settings;
    }
    private sealed record RestoreRecord(int RestoreCount, bool Marked, string CheckpointId, DateTimeOffset RestoredAtUtc);
    public void RecordRestore(string runId, string checkpointId, bool mark)
    {
        CoreFormat.RequireId(runId); CoreFormat.RequireId(checkpointId);
        string dir = RunDir(runId); EnsureDirectory(dir);
        string path = Path.Combine(dir, "rewind.json");
        RestoreRecord? old = File.Exists(path) ? ReadJson<RestoreRecord>(path) : null;
        var record = new RestoreRecord(checked((old?.RestoreCount ?? 0) + 1), mark || (old?.Marked ?? false), checkpointId, DateTimeOffset.UtcNow);
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(record, CoreFormat.Json));
    }
    public int GetRewindCount(string runId)
    {
        CoreFormat.RequireId(runId); string path = Path.Combine(RunDir(runId), "rewind.json"); CheckPath(path);
        if (!File.Exists(path)) return 0;
        var record = ReadJson<RestoreRecord>(path) ?? throw new InvalidDataException("Invalid rewind record.");
        if (record.RestoreCount < 0) throw new InvalidDataException("Invalid rewind count.");
        return record.RestoreCount;
    }
    public bool IsMarked(string runId)
    {
        CoreFormat.RequireId(runId); string path = Path.Combine(RunDir(runId), "rewind.json"); CheckPath(path);
        if (!File.Exists(path)) return false;
        var record = ReadJson<RestoreRecord>(path) ?? throw new InvalidDataException("Invalid rewind record.");
        if (record.RestoreCount < 0) throw new InvalidDataException("Invalid rewind count.");
        return record.Marked;
    }

    private string RunDir(string runId) { CoreFormat.RequireId(runId); return Path.Combine(root, "runs", runId); }
    private string CheckpointDir(string runId, string checkpointId) { CoreFormat.RequireId(checkpointId); return Path.Combine(RunDir(runId), "checkpoints", checkpointId); }
    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static T? ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(ReadBounded(path, MaxMetadataBytes), CoreFormat.Json);
    private static byte[] ReadBounded(string path, int max)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max) throw new InvalidDataException("File too large.");
        using var result = new MemoryStream();
        byte[] buffer = new byte[81920]; int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) != 0) { if (result.Length + n > max) throw new InvalidDataException("File too large."); result.Write(buffer, 0, n); }
        return result.ToArray();
    }
    private static void EnsureDirectory(string dir) { CheckPath(dir); Directory.CreateDirectory(dir); CheckPath(dir); }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        CheckPath(path); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); } CheckPath(path); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void CheckPath(string path)
    {
        string full = Path.GetFullPath(path); string? current = Path.GetPathRoot(full);
        foreach (string part in full[current!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links are not allowed in repository paths."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void CheckTree(string dir)
    {
        CheckPath(dir);
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
        {
            CheckPath(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) CheckTree(entry);
        }
    }
}
