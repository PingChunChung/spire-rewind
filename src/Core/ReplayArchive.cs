using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireRewind.Core;

public sealed record ReplayRuntimeIdentity(string GameVersion, string GameCommit, uint ModelIdHash, string[] ModIds);

public sealed record ReplayArchiveMetadata
{
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public string RunId { get; init; } = "";
    [JsonRequired] public int Act { get; init; }
    [JsonRequired] public int Floor { get; init; }
    [JsonRequired] public string Sha256 { get; init; } = "";
    [JsonRequired] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonRequired] public string? GameVersion { get; init; }
    public string? GameCommit { get; init; }
    public uint? ModelIdHash { get; init; }
    public int? RunSchemaVersion { get; init; }
    [JsonRequired] public string[] ModIds { get; init; } = [];
    public string Source { get; init; } = "game";
    public string CaptureKind { get; init; } = "unknown";
    public bool Complete { get; init; }
}

// An entry contains identifiers, never an arbitrary input path supplied by a caller.
public sealed record ReplayArchiveEntry(string RunId, string ReplayId, ReplayArchiveMetadata? Metadata, string? Error);
public sealed record ReplayHeader(string GameVersion, string GameCommit, uint ModelIdHash, int HeaderBytes);
public sealed record ReplayInspection(ReplayArchiveMetadata? Metadata, ReplayHeader? Header, long Bytes, string? Error)
{
    public bool Compatible => Error == null;
    public bool PayloadDecoded => false;
    public bool PlaybackAvailable => false;
    public const string PlaybackBlockedReason = "replay_isolation_unavailable";
}

/// <summary>Only reads repository archives. Never calls the game parser or creates a run.</summary>
public sealed class ReplayArchiveRepository
{
    public const int MaximumInspectionBytes = 16 * 1024 * 1024;
    public const int MaximumListedEntries = 2000;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string root;

    public ReplayArchiveRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        CheckPath(this.root);
    }

    public IReadOnlyList<ReplayArchiveEntry> List()
    {
        string runs = Path.Combine(root, "runs"); CheckPath(runs);
        if (!Directory.Exists(runs)) return [];
        var entries = new List<ReplayArchiveEntry>();
        foreach (string dir in Directory.EnumerateDirectories(runs))
        {
            string runId = Path.GetFileName(dir);
            if (!CoreFormat.IsId(runId)) continue;
            string replays = Path.Combine(dir, "replays");
            try
            {
                CheckPath(replays);
                if (!Directory.Exists(replays)) continue;
                foreach (string path in Directory.EnumerateFiles(replays, "*.mcr"))
                {
                    string replayId = Path.GetFileNameWithoutExtension(path);
                    if (!IsReplayId(replayId)) continue;
                    try
                    {
                        CheckPath(path);
                        entries.Add(new(runId, replayId, ReadMetadata(runId, replayId), null));
                    }
                    catch (Exception e) when (IsInputFailure(e)) { entries.Add(new(runId, replayId, null, ErrorCode(e))); }
                    // Bounded discovery keeps a damaged directory from growing the menu without limit.
                    if (entries.Count >= MaximumListedEntries) return Order(entries);
                }
            }
            catch (Exception e) when (IsInputFailure(e)) { /* Do not traverse an unsafe run directory. */ }
        }
        return Order(entries);
    }

    private static ReplayArchiveEntry[] Order(List<ReplayArchiveEntry> entries) => entries
        .OrderByDescending(x => x.Metadata?.CapturedAtUtc ?? DateTimeOffset.MinValue)
        .ThenBy(x => x.RunId, StringComparer.Ordinal).ThenBy(x => x.ReplayId, StringComparer.Ordinal).ToArray();

    public ReplayInspection Inspect(ReplayArchiveEntry entry, ReplayRuntimeIdentity runtime)
    {
        ReplayArchiveMetadata? metadata = null;
        ReplayHeader? header = null;
        long length = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(runtime);
            CoreFormat.RequireId(entry.RunId);
            if (!IsReplayId(entry.ReplayId)) throw new InvalidDataException("invalid_replay_id");
            // Reload from disk; caller-provided metadata and earlier listing results are untrusted.
            metadata = ReadMetadata(entry.RunId, entry.ReplayId);
            string path = ArchivePath(entry.RunId, entry.ReplayId, ".mcr");
            CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = stream.Length;
            if (length < 12 || length > MaximumInspectionBytes) throw new InvalidDataException("replay_size_limit");
            var bytes = new byte[checked((int)length)]; stream.ReadExactly(bytes);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(metadata.Sha256), SHA256.HashData(bytes)))
                throw new InvalidDataException("replay_checksum_mismatch");
            header = ReadHeader(bytes);
            string? error = ValidateCompatibility(metadata, header, runtime);
            return new(metadata, header, length, error);
        }
        catch (Exception e) when (IsInputFailure(e)) { return new(metadata, header, length, ErrorCode(e)); }
    }

    public static ReplayHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes.Length > MaximumInspectionBytes) throw new InvalidDataException("replay_size_limit");
        int offset = 0;
        string version = ReadString(bytes, ref offset), commit = ReadString(bytes, ref offset);
        if (offset > bytes.Length - 4) throw new InvalidDataException("truncated_replay_header");
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        return new(version, commit, hash, offset + 4);
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset > bytes.Length - 4) throw new InvalidDataException("truncated_replay_header");
        int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)); offset += 4;
        if (size < 1 || size > 128 || size > bytes.Length - offset) throw new InvalidDataException("invalid_replay_header");
        string value;
        try { value = Utf8.GetString(bytes.Slice(offset, size)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("invalid_replay_header"); }
        offset += size;
        if (value.Any(char.IsControl)) throw new InvalidDataException("invalid_replay_header");
        return value;
    }

    private static string? ValidateCompatibility(ReplayArchiveMetadata m, ReplayHeader h, ReplayRuntimeIdentity r)
    {
        if (string.IsNullOrWhiteSpace(r.GameVersion) || string.IsNullOrWhiteSpace(r.GameCommit) || r.ModIds == null)
            return "replay_runtime_unavailable";
        if (h.GameVersion != r.GameVersion || m.GameVersion != r.GameVersion) return "replay_version_mismatch";
        if (h.GameCommit != r.GameCommit || m.GameCommit != null && m.GameCommit != h.GameCommit) return "replay_commit_mismatch";
        if (h.ModelIdHash != r.ModelIdHash || m.ModelIdHash.HasValue && m.ModelIdHash != h.ModelIdHash) return "replay_model_hash_mismatch";
        if (!m.ModIds.Order(StringComparer.Ordinal).SequenceEqual(r.ModIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return "replay_mod_mismatch";
        if (string.IsNullOrWhiteSpace(m.GameCommit) || !m.ModelIdHash.HasValue) return "replay_identity_missing";
        return null;
    }

    private ReplayArchiveMetadata ReadMetadata(string runId, string replayId)
    {
        string path = ArchivePath(runId, replayId, ".json"); CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMetadataBytes) throw new InvalidDataException("replay_metadata_limit");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var m = JsonSerializer.Deserialize<ReplayArchiveMetadata>(bytes, CoreFormat.Json) ?? throw new InvalidDataException("invalid_replay_metadata");
        if (m.FormatVersion != 1 || m.RunId != runId || !CoreFormat.IsId(m.Sha256) || m.ModIds == null
            || m.ModIds.Any(string.IsNullOrWhiteSpace) || m.ModIds.Distinct(StringComparer.Ordinal).Count() != m.ModIds.Length)
            throw new InvalidDataException("invalid_replay_metadata");
        return m;
    }

    private string ArchivePath(string runId, string replayId, string extension)
    {
        CoreFormat.RequireId(runId);
        if (!IsReplayId(replayId)) throw new InvalidDataException("invalid_replay_id");
        return Path.Combine(root, "runs", runId, "replays", replayId + extension);
    }

    public static bool IsReplayId(string? id) => id is { Length: > 64 and <= 128 }
        && id.StartsWith("act-", StringComparison.Ordinal) && id.Contains("-floor-", StringComparison.Ordinal)
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && CoreFormat.IsId(id[^64..]);
    private static bool IsInputFailure(Exception e) => e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static string ErrorCode(Exception e) => e is InvalidDataException ? e.Message
        : e is FileNotFoundException or DirectoryNotFoundException ? "replay_archive_missing"
        : e is JsonException or ArgumentException ? "invalid_replay_metadata" : "replay_archive_unreadable";

    private static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("replay_archive_link_refused"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
