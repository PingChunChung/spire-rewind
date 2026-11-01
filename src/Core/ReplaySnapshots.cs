using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireRewind.Core;

public sealed record ReplaySnapshotActor
{
    [JsonRequired] public string Label { get; init; } = "";
    [JsonRequired] public int Hp { get; init; }
    [JsonRequired] public int MaxHp { get; init; }
    public int? Block { get; init; }
    public ImmutableArray<string>? Intent { get; init; }
}
public sealed record ReplaySnapshotFrame
{
    [JsonRequired] public int Turn { get; init; }
    [JsonRequired] public ImmutableArray<ReplaySnapshotActor> Players { get; init; }
    [JsonRequired] public ImmutableArray<ReplaySnapshotActor> Enemies { get; init; }
    public ImmutableArray<string>? Cards { get; init; }
    public ImmutableArray<string>? Actions { get; init; }
    public string? AiReason { get; init; }
}
public sealed record ReplaySnapshotsDocument
{
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public string ReplaySha256 { get; init; } = "";
    [JsonRequired] public string RunId { get; init; } = "";
    [JsonRequired] public string GameVersion { get; init; } = "";
    [JsonRequired] public string GameCommit { get; init; } = "";
    [JsonRequired] public uint ModelIdHash { get; init; }
    [JsonRequired] public ImmutableArray<string> ModIds { get; init; }
    [JsonRequired] public ImmutableArray<ReplaySnapshotFrame> Frames { get; init; }
}
public sealed record ReplaySnapshotsReadResult(ReplaySnapshotsDocument? Document, string? Error);

/// <summary>Reads and writes bounded, independent data; never invokes game replay code.</summary>
public sealed class ReplaySnapshotsRepository
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumFrames = 10000;
    public const int MaximumPlayers = 8;
    public const int MaximumEnemies = 64;
    private static readonly JsonSerializerOptions Json = new(CoreFormat.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string root;
    private readonly ReplayArchiveRepository archives;
    public ReplaySnapshotsRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        CheckPath(this.root);
        archives = new(this.root);
    }

    public ReplaySnapshotsReadResult ReadForReplay(ReplayArchiveEntry entry, ReplayRuntimeIdentity runtime)
    {
        var inspected = archives.Inspect(entry, runtime);
        if (!inspected.Compatible) return new(null, inspected.Error);
        try
        {
            string path = CompanionPath(entry.RunId, entry.ReplayId);
            CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("replay_snapshots_limit");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes);
            CheckDuplicates(json.RootElement);
            var document = JsonSerializer.Deserialize<ReplaySnapshotsDocument>(bytes, Json) ?? throw new InvalidDataException("invalid_replay_snapshots");
            Validate(document, inspected.Metadata!, inspected.Header!);
            return new(document, null);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException)
        {
            return new(null, e is FileNotFoundException or DirectoryNotFoundException ? "replay_snapshots_missing"
                : e is InvalidDataException ? e.Message : "invalid_replay_snapshots");
        }
    }

    // The only accepted path is the actual SaveReplay result inside this repository.
    public void Write(string replayPath, ReplaySnapshotsDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath); ArgumentNullException.ThrowIfNull(document);
        string full = Path.GetFullPath(replayPath);
        string? dir = Path.GetDirectoryName(full);
        string? runDir = dir == null ? null : Path.GetDirectoryName(dir);
        string runId = runDir == null ? "" : Path.GetFileName(runDir);
        string replayId = Path.GetFileNameWithoutExtension(full);
        string expected = Path.Combine(root, "runs", runId, "replays", replayId + ".mcr");
        if (!CoreFormat.IsId(runId) || !string.Equals(full, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !string.Equals(replayPath, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("invalid_replay_path");
        CheckPath(full);
        var runtime = new ReplayRuntimeIdentity(document.GameVersion, document.GameCommit, document.ModelIdHash, document.ModIds.IsDefault ? [] : document.ModIds.ToArray());
        var inspected = archives.Inspect(new(runId, replayId, null, null), runtime);
        if (!inspected.Compatible) throw new InvalidDataException(inspected.Error);
        Validate(document, inspected.Metadata!, inspected.Header!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("replay_snapshots_limit");
        string path = CompanionPath(runId, replayId); CheckPath(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CheckPath(temp);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            CheckPath(path); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string CompanionPath(string runId, string replayId) => Path.Combine(root, "runs", runId, "replays", replayId + ".snapshots.json");
    internal static void ValidateFrames(ReplaySnapshotsDocument d)
    {
        if (d.FormatVersion is not (1 or 2)) throw new InvalidDataException("invalid_replay_snapshots");
        if (d.Frames.IsDefault || d.Frames.Length is < 1 or > MaximumFrames) throw new InvalidDataException("invalid_replay_snapshots");
        int previous = 0;
        foreach (var frame in d.Frames)
        {
            if (frame == null || frame.Turn < 1 || frame.Turn < previous || frame.Turn > 1000000 || frame.Players.IsDefault || frame.Enemies.IsDefault
                || frame.Players.Length is < 1 or > MaximumPlayers || frame.Enemies.Length > MaximumEnemies) throw new InvalidDataException("invalid_replay_snapshots");
            if (d.FormatVersion == 1 && (frame.Cards != null || frame.Actions != null || frame.AiReason != null)) throw new InvalidDataException("invalid_replay_snapshots");
            ValidateStrings(frame.Cards); ValidateStrings(frame.Actions); ValidateString(frame.AiReason, 2048);
            previous = frame.Turn;
            foreach (var actor in frame.Players.Concat(frame.Enemies))
                if (actor == null || string.IsNullOrWhiteSpace(actor.Label) || actor.Label.Length > 128 || actor.Label.Any(char.IsControl)
                    || actor.MaxHp is < 1 or > 1000000 || actor.Hp < 0 || actor.Hp > actor.MaxHp
                    || actor.Block is < 0 or > 999999999 || d.FormatVersion == 1 && (actor.Block != null || actor.Intent != null)) throw new InvalidDataException("invalid_replay_snapshots");
                else ValidateStrings(actor.Intent);
        }
    }
    private static void ValidateString(string? value, int max)
    {
        if (value != null && (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))) throw new InvalidDataException("invalid_replay_snapshots");
    }
    private static void ValidateStrings(ImmutableArray<string>? values)
    {
        if (values == null) return;
        if (values.Value.IsDefault || values.Value.Length > 256) throw new InvalidDataException("invalid_replay_snapshots");
        foreach (string value in values.Value) { if (value == null) throw new InvalidDataException("invalid_replay_snapshots"); ValidateString(value, 256); }
    }
    private static void Validate(ReplaySnapshotsDocument d, ReplayArchiveMetadata m, ReplayHeader h)
    {
        if (d.FormatVersion is not (1 or 2) || !CoreFormat.IsId(d.ReplaySha256) || d.ReplaySha256 != m.Sha256 || d.RunId != m.RunId
            || d.GameVersion != h.GameVersion || d.GameCommit != h.GameCommit || d.ModelIdHash != h.ModelIdHash
            || d.ModIds.IsDefault || d.ModIds.Any(string.IsNullOrWhiteSpace) || d.ModIds.Distinct(StringComparer.Ordinal).Count() != d.ModIds.Length
            || !d.ModIds.Order(StringComparer.Ordinal).SequenceEqual(m.ModIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("replay_snapshots_identity_mismatch");
        ValidateFrames(d);
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("invalid_replay_snapshots");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
    private static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("replay_snapshots_link_refused"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}

/// <summary>Pure data timeline. Previous replays from frame zero; Advance handles at most 100 frames per call.</summary>
public sealed class ReplaySnapshotSession : IDisposable
{
    private readonly ReplaySnapshotsDocument document;
    private double remainder;
    private bool disposed;
    public int FrameCount => document.Frames.Length;
    public int Index { get; private set; }
    public ReplaySnapshotFrame Current { get { EnsureActive(); return document.Frames[Index]; } }
    public bool Playing { get; private set; }
    public bool Completed => Index == document.Frames.Length - 1;
    public int Speed { get; private set; } = 1;
    public const string PreviousStrategy = "restart_from_zero_fast_forward";
    public ReplaySnapshotSession(ReplaySnapshotsDocument document) { ArgumentNullException.ThrowIfNull(document); ReplaySnapshotsRepository.ValidateFrames(document); this.document = document; }
    public void Play() { EnsureActive(); if (Completed) { Index = 0; remainder = 0; } Playing = !Completed; }
    public void Pause() { EnsureActive(); Playing = false; remainder = 0; }
    public void SetSpeed(int speed) { EnsureActive(); if (speed is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(speed)); Speed = speed; }
    public void Step() { EnsureActive(); Playing = false; remainder = 0; if (!Completed) Index++; }
    public void Previous() { EnsureActive(); Playing = false; remainder = 0; int target = Math.Max(0, Index - 1); Index = 0; while (Index < target) Index++; }
    public void Advance(double elapsedSeconds)
    {
        EnsureActive();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!Playing) return;
        // Drop elapsed time beyond this tick's work budget instead of creating an unbounded backlog.
        double scaled = Math.Min(elapsedSeconds, 100.0 / Speed) * Speed + remainder;
        int steps = Math.Min(100, (int)scaled); remainder = scaled - Math.Floor(scaled);
        Index = Math.Min(document.Frames.Length - 1, Index + steps);
        if (Completed) { Playing = false; remainder = 0; }
    }
    public void Dispose() { Playing = false; disposed = true; }
    private void EnsureActive() { ObjectDisposedException.ThrowIf(disposed, this); }
}
