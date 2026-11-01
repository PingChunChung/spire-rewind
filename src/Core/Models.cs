using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireRewind.Core;

public sealed record CheckpointMetadata
{
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public string CheckpointId { get; init; } = "";
    [JsonRequired] public string RunId { get; init; } = "";
    [JsonRequired] public string GameVersion { get; init; } = "";
    [JsonRequired] public int RunSchemaVersion { get; init; }
    [JsonRequired] public int ProfileId { get; init; }
    [JsonRequired] public string Seed { get; init; } = "";
    [JsonRequired] public long StartTime { get; init; }
    [JsonRequired] public string Character { get; init; } = "";
    [JsonRequired] public int Ascension { get; init; }
    [JsonRequired] public int Act { get; init; }
    [JsonRequired] public int Floor { get; init; }
    [JsonRequired] public string RoomType { get; init; } = "";
    [JsonRequired] public string CaptureKind { get; init; } = "";
    [JsonRequired] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonRequired] public int Hp { get; init; }
    [JsonRequired] public int MaxHp { get; init; }
    [JsonRequired] public int Gold { get; init; }
    [JsonRequired] public int DeckCount { get; init; }
    [JsonRequired] public string RunSha256 { get; init; } = "";
    [JsonRequired] public string[] ModIds { get; init; } = [];
    [JsonRequired] public string Source { get; init; } = "game";
    public string? Label { get; init; }
}

public static class RunIdentity
{
    public static string Create(int profileId, string seed, long startTime, string character) =>
        CoreFormat.Hash(JsonSerializer.SerializeToUtf8Bytes(new object[] { profileId, seed, startTime, character }));
}

public sealed record RewindSettings
{
    public bool Enabled { get; init; } = true;
    public int RetainedRuns { get; init; } = 10;
    public bool MarkRewoundRuns { get; init; } = true;
    public string Language { get; init; } = "auto";
    public RewindSettings Validate() => this with { RetainedRuns = Math.Clamp(RetainedRuns, 1, 100), Language = Language is "auto" or "zht" or "zhs" or "eng" ? Language : "auto" };
}

internal static class CoreFormat
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool IsId(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void RequireId(string? id) { if (!IsId(id)) throw new ArgumentException("Invalid identifier."); }
}

public static class RestoreValidation
{
    public static string? Validate(CheckpointMetadata metadata, string currentRunId, int profileId, string gameVersion, int runSchemaVersion, IEnumerable<string> modIds, bool isSinglePlayer, bool isDaily)
    {
        if (metadata is null || metadata.FormatVersion != 1) return "unsupported_format";
        if (!CoreFormat.IsId(metadata.RunId) || !CoreFormat.IsId(metadata.CheckpointId) || !CoreFormat.IsId(metadata.RunSha256) || !CoreFormat.IsId(currentRunId)) return "invalid_metadata";
        if (!isSinglePlayer) return "not_single_player";
        if (isDaily) return "daily_run";
        if (metadata.RunId != currentRunId) return "run_mismatch";
        if (metadata.ProfileId != profileId) return "profile_mismatch";
        if (metadata.GameVersion != gameVersion) return "game_version_mismatch";
        if (metadata.RunSchemaVersion != runSchemaVersion) return "schema_mismatch";
        if (metadata.ModIds is null || modIds is null || !metadata.ModIds.Order(StringComparer.Ordinal).SequenceEqual(modIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return "mod_mismatch";
        return null;
    }
}
