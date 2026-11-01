using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireRewind.Core;

public sealed record TestCommand
{
    public string? RequestId { get; init; }
    public string? Command { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string? RunId { get; init; }
    public string? CheckpointId { get; init; }
    public int? Index { get; init; }
    public string? SnapshotId { get; init; }
    public string? ReplayId { get; init; }
    public int? Speed { get; init; }
}
public sealed record TestCommandResult(string? RequestId, string Status, string? Error, object? Data);

public sealed class TestCommandChannel
{
    public const int MaxCommandBytes = 16384;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    public TestCommandChannel(string rewindRoot) { directory = Path.Combine(Path.GetFullPath(rewindRoot), "test-control"); CheckPath(directory); }
    public TestCommand? Claim(DateTimeOffset now)
    {
        string path = Path.Combine(directory, "command.json"); CheckPath(path);
        if (!File.Exists(path)) return null;
        TestCommand? cmd;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxCommandBytes) throw new InvalidDataException("command_too_large");
            using var buffer = new MemoryStream(); byte[] chunk = new byte[4096]; int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) != 0)
            { if (buffer.Length + n > MaxCommandBytes) throw new InvalidDataException("command_too_large"); buffer.Write(chunk, 0, n); }
            cmd = JsonSerializer.Deserialize<TestCommand>(buffer.ToArray(), Json) ?? throw new InvalidDataException("invalid_command");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { WriteResult(new(null, "rejected", e is JsonException ? "invalid_command" : e.Message, null)); return null; }
        if (!Guid.TryParseExact(cmd.RequestId, "D", out var requestId)) { WriteResult(new(cmd.RequestId, "rejected", "invalid_request_id", null)); return null; }
        string receipts = Path.Combine(directory, "receipts"); CheckPath(receipts); Directory.CreateDirectory(receipts); CheckPath(receipts);
        string receipt = Path.Combine(receipts, Convert.ToHexStringLower(SHA256.HashData(requestId.ToByteArray())) + ".receipt"); CheckPath(receipt);
        try { using var file = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Flush(true); }
        catch (IOException) when (File.Exists(receipt)) { return null; }
        string? error = Validate(cmd, now);
        if (error != null) { WriteResult(new(cmd.RequestId, "rejected", error, null)); return null; }
        return cmd;
    }
    public static string? Validate(TestCommand cmd, DateTimeOffset now)
    {
        if (cmd.CreatedAtUtc == default || cmd.CreatedAtUtc > now.AddSeconds(10) || cmd.CreatedAtUtc < now.AddMinutes(-2)) return "expired_command";
        if (cmd.Command?.StartsWith("replay-", StringComparison.Ordinal) == true)
        {
            if (cmd.CheckpointId != null || cmd.Index != null || cmd.SnapshotId != null) return "unexpected_fields";
            if (cmd.Command is "replay-play" or "replay-inspect")
                return CoreFormat.IsId(cmd.RunId) && ReplayArchiveRepository.IsReplayId(cmd.ReplayId) && cmd.Speed == null ? null : "invalid_replay_selection";
            if (cmd.RunId != null || cmd.ReplayId != null) return "unexpected_fields";
            if (cmd.Command == "replay-speed") return cmd.Speed is 1 or 2 or 4 ? null : "invalid_speed";
            if (cmd.Command is not ("replay-list" or "replay-resume" or "replay-pause" or "replay-step" or "replay-previous"
                or "replay-exit" or "replay-status" or "replay-audit-begin" or "replay-audit-end")) return "unknown_command";
            return cmd.Speed == null ? null : "unexpected_fields";
        }
        // export writes the current run's HTML replay; ui-open/ui-confirm/ui-close drive the Rewind dialog for screenshots
        // and ui-accept presses the confirm button of an open confirmation (a restore through the dialog's own handler);
        // save-and-quit leaves the run through the pause menu's own path so a copy can exit normally.
        // fault-inject latches the restart-required fault on the isolated copy so the disabled native Continue can be checked.
        if (cmd.Command is not ("list" or "status" or "pause" or "restore" or "export" or "ui-open" or "ui-confirm" or "ui-close" or "ui-accept" or "save-and-quit" or "fault-inject")) return "unknown_command";
        if (cmd.ReplayId != null || cmd.Speed != null) return "unexpected_fields";
        if (cmd.Command is not ("restore" or "ui-confirm")) return cmd.RunId != null || cmd.CheckpointId != null || cmd.Index != null || cmd.SnapshotId != null ? "unexpected_fields" : null;
        if (!CoreFormat.IsId(cmd.RunId)) return "invalid_run_id";
        if (cmd.CheckpointId != null) return CoreFormat.IsId(cmd.CheckpointId) ? null : "invalid_checkpoint_id";
        return cmd.Index > 0 && CoreFormat.IsId(cmd.SnapshotId) ? null : "invalid_selection";
    }
    // Replay audit must never exclude command files from the profile hash.
    // Put the whole channel outside Godot user:// instead, before Claim writes a receipt.
    public static string ResolveExternalRoot(string configuredRoot, string protectedRoot)
    {
        if (!Path.IsPathFullyQualified(configuredRoot)) throw new InvalidDataException("test_control_absolute_path_required");
        string control = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        string protectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Within(string child, string parent) => child.Equals(parent, comparison)
            || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
        if (Within(control, protectedPath) || Within(protectedPath, control)) throw new InvalidDataException("test_control_overlaps_player_data");
        CheckPath(control);
        return control;
    }
    public static string SnapshotId(string runId, IEnumerable<CheckpointMetadata> checkpoints) => CoreFormat.Hash(JsonSerializer.SerializeToUtf8Bytes(new object[] { runId, checkpoints.Select(x => x.CheckpointId).ToArray() }));
    // Outside combat the native Pause() does not set IsPaused. A visible pause
    // screen and idle gameplay are still required, including an already open menu.
    public static string? PausedStateError(bool pauseVisible, bool inCombat, bool combatPaused, bool stable)
    {
        if (!pauseVisible || inCombat && !combatPaused) return "not_paused";
        return stable ? null : "not_stable";
    }
    public static CheckpointMetadata? Select(TestCommand command, string currentRunId, IReadOnlyList<CheckpointMetadata> checkpoints)
    {
        if (command.RunId != currentRunId) return null;
        if (command.CheckpointId != null) return checkpoints.FirstOrDefault(x => x.CheckpointId == command.CheckpointId);
        if (command.SnapshotId != SnapshotId(currentRunId, checkpoints) || command.Index is not > 0 || command.Index > checkpoints.Count) return null;
        return checkpoints[command.Index.Value - 1];
    }
    public void WriteResult(TestCommandResult result)
    {
        CheckPath(directory); Directory.CreateDirectory(directory); CheckPath(directory);
        string path = Path.Combine(directory, "result.json"); CheckPath(path);
        string temp = Path.Combine(directory, "result." + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, result, Json); file.Flush(true); } CheckPath(path); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void CheckPath(string path)
    {
        string full = Path.GetFullPath(path); string? current = Path.GetPathRoot(full);
        foreach (string part in full[current!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue; current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse_point"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
