using System.Text.Json;
using SpireRewind.Core;

internal static class TestCommandTests
{
    public static void Run()
    {
        void Assert(bool b, string message) { if (!b) throw new Exception(message); }
        Assert(TestCommandChannel.PausedStateError(true, false, false, true) == null, "native pause outside combat does not set combat flag");
        Assert(TestCommandChannel.PausedStateError(true, true, true, false) == "not_stable", "visible pause cannot bypass an in-flight action");
        Assert(TestCommandChannel.PausedStateError(true, true, false, true) == "not_paused", "combat must actually be paused");
        Assert(TestCommandChannel.PausedStateError(false, false, false, true) == "not_paused", "idle map without menu is unsafe");
        string root = Path.Combine(Path.GetTempPath(), "rewind-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "test-control"));
        try
        {
            var channel = new TestCommandChannel(root); string path = Path.Combine(root, "test-control", "command.json");
            var now = DateTimeOffset.UtcNow; string id = Guid.NewGuid().ToString();
            void Put(object value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
            Put(new { request_id = id, command = "list", created_at_utc = now });
            Assert(channel.Claim(now)?.RequestId == id, "claim valid");
            Assert(channel.Claim(now) == null && new TestCommandChannel(root).Claim(now) == null, "durable duplicate");
            Put(new { request_id = id.ToUpperInvariant(), command = "list", created_at_utc = now });
            Assert(channel.Claim(now) == null, "UUID case cannot replay");
            Assert(TestCommandChannel.Validate(new TestCommand { Command = "pause", CreatedAtUtc = now }, now) == null, "pause accepted");
            Put(new { request_id = Guid.NewGuid().ToString(), command = "list", created_at_utc = now.AddMinutes(-3) });
            Assert(channel.Claim(now) == null && File.ReadAllText(Path.Combine(root, "test-control", "result.json")).Contains("expired_command"), "ttl");
            Put(new { request_id = Guid.NewGuid().ToString(), command = "list", created_at_utc = now, path = "../x" });
            Assert(channel.Claim(now) == null && File.ReadAllText(Path.Combine(root, "test-control", "result.json")).Contains("invalid_command"), "reject paths");
            File.WriteAllText(path, new string('x', TestCommandChannel.MaxCommandBytes + 1));
            Assert(channel.Claim(now) == null && File.ReadAllText(Path.Combine(root, "test-control", "result.json")).Contains("command_too_large"), "size limit");
            string run = new('a', 64); var first = new CheckpointMetadata { RunId = run, CheckpointId = new string('b', 64) };
            var second = first with { CheckpointId = new string('c', 64) }; var items = new[] { first, second };
            var cmd = new TestCommand { Command = "restore", RunId = run, Index = 1, SnapshotId = TestCommandChannel.SnapshotId(run, items), CreatedAtUtc = now };
            Assert(TestCommandChannel.Validate(cmd, now) == null && TestCommandChannel.Select(cmd, run, items) == first, "snapshot selection");
            Assert(TestCommandChannel.Select(cmd, run, new[] { second, first }) == null, "index drift rejected");
            Assert(TestCommandChannel.Select(cmd, new string('d', 64), items) == null, "run mismatch");
            Assert(TestCommandChannel.Select(cmd with { CheckpointId = second.CheckpointId, Index = null, SnapshotId = null }, run, items) == second, "id selection");
            ReplayCommands(now, run);
            ExternalControl(root, now, run);
            try
            {
                string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
                string linked = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(linked, outside);
                bool rejected = false; try { _ = new TestCommandChannel(linked); } catch (IOException) { rejected = true; }
                Assert(rejected, "reparse rejected");
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("Control symlink test skipped: unavailable permissions."); }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ReplayCommands(DateTimeOffset now, string run)
    {
        void Check(TestCommand command, string? expected)
        {
            string? actual = TestCommandChannel.Validate(command, now);
            if (actual != expected) throw new Exception($"{command.Command}: expected {expected ?? "accepted"}, got {actual ?? "accepted"}");
        }
        string replay = "act-1-floor-2-" + new string('b', 64);
        var baseCommand = new TestCommand { CreatedAtUtc = now };
        foreach (string name in new[] { "replay-list", "replay-resume", "replay-pause", "replay-step", "replay-previous", "replay-exit", "replay-status", "replay-audit-begin", "replay-audit-end" })
        {
            var command = baseCommand with { Command = name };
            Check(command, null);
            Check(command with { RunId = run }, "unexpected_fields");
            Check(command with { ReplayId = replay }, "unexpected_fields");
            Check(command with { Speed = 1 }, "unexpected_fields");
            Check(command with { CheckpointId = run }, "unexpected_fields");
            Check(command with { Index = 1 }, "unexpected_fields");
            Check(command with { SnapshotId = run }, "unexpected_fields");
        }
        foreach (string name in new[] { "replay-play", "replay-inspect" })
        {
            var command = baseCommand with { Command = name, RunId = run, ReplayId = replay };
            Check(command, null);
            Check(command with { RunId = null }, "invalid_replay_selection");
            Check(command with { RunId = "../bad" }, "invalid_replay_selection");
            Check(command with { ReplayId = null }, "invalid_replay_selection");
            Check(command with { ReplayId = "../" + replay }, "invalid_replay_selection");
            Check(command with { Speed = 1 }, "invalid_replay_selection");
            Check(command with { CheckpointId = run }, "unexpected_fields");
            Check(command with { Index = 1 }, "unexpected_fields");
            Check(command with { SnapshotId = run }, "unexpected_fields");
        }
        var speed = baseCommand with { Command = "replay-speed" };
        foreach (int n in new[] { 1, 2, 4 }) Check(speed with { Speed = n }, null);
        foreach (int n in new[] { -1, 0, 3, 5, int.MaxValue }) Check(speed with { Speed = n }, "invalid_speed");
        Check(speed, "invalid_speed");
        Check(speed with { Speed = 1, ReplayId = replay }, "unexpected_fields");
        Check(speed with { Speed = 1, RunId = run }, "unexpected_fields");
        Check(baseCommand with { Command = "replay-unknown" }, "unknown_command");
        Check(baseCommand with { Command = "unknown" }, "unknown_command");
        foreach (string name in new[] { "list", "status", "pause", "restore", "ui-accept" })
        {
            var command = baseCommand with { Command = name, RunId = name == "restore" ? run : null, CheckpointId = name == "restore" ? run : null };
            Check(command, null);
            Check(command with { ReplayId = replay }, "unexpected_fields");
            Check(command with { Speed = 1 }, "unexpected_fields");
        }
    }

    private static void ExternalControl(string root, DateTimeOffset now, string run)
    {
        string profile = Path.Combine(root, "user-data");
        void Assert(bool value, string name) { if (!value) throw new Exception("External control: " + name); }
        void Reject(string path, string expected)
        {
            try { TestCommandChannel.ResolveExternalRoot(path, profile); }
            catch (InvalidDataException e) when (e.Message == expected) { return; }
            throw new Exception("External control accepted " + path);
        }
        string external = Path.Combine(root, "control");
        string sibling = Path.Combine(root, "user-data-else");
        Directory.CreateDirectory(Path.Combine(profile, "saves"));
        File.WriteAllText(Path.Combine(profile, "saves", "profile.save"), "profile content");
        File.WriteAllText(Path.Combine(profile, "settings.json"), "profile settings");
        string settings = Path.Combine(root, "machine-settings.json"); File.WriteAllText(settings, "machine settings");
        Assert(TestCommandChannel.ResolveExternalRoot(external, profile) == external, "external sibling");
        Assert(TestCommandChannel.ResolveExternalRoot(sibling, profile) == sibling, "similar prefix sibling");
        Reject(profile, "test_control_overlaps_player_data");
        Reject(Path.Combine(profile, "nested"), "test_control_overlaps_player_data");
        Reject(root, "test_control_overlaps_player_data");
        Reject("relative-control", "test_control_absolute_path_required");
        try
        {
            string link = Path.Combine(root, "control-link"); Directory.CreateSymbolicLink(link, profile);
            bool rejected = false;
            try { TestCommandChannel.ResolveExternalRoot(link, profile); } catch (IOException) { rejected = true; }
            Assert(rejected, "symlink");
            Directory.Delete(link);
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("External control symlink test skipped: unavailable permissions."); }
        catch (PlatformNotSupportedException) { Console.WriteLine("External control symlink test skipped: unsupported platform."); }

        string selected = TestCommandChannel.ResolveExternalRoot(external, profile);
        string commandDir = Path.Combine(selected, "test-control"); Directory.CreateDirectory(commandDir);
        var audit = ProfileAudit.Capture(profile, settings);
        var command = new TestCommand { RequestId = Guid.NewGuid().ToString("D"), Command = "replay-play", CreatedAtUtc = now, RunId = run, ReplayId = "act-1-floor-2-" + new string('b', 64) };
        var json = JsonSerializer.Serialize(command, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert(json.Contains("\"request_id\"") && json.Contains("\"replay_id\"") && json.Contains("\"run_id\""), "snake case serialization");
        File.WriteAllText(Path.Combine(commandDir, "command.json"), json);
        var channel = new TestCommandChannel(selected);
        var claimed = channel.Claim(now);
        Assert(claimed?.ReplayId == command.ReplayId && claimed.RunId == run, "claim replay selection");
        channel.WriteResult(new(command.RequestId, "ok", null, new { frame_count = 2 }));
        Assert(Directory.GetFiles(Path.Combine(commandDir, "receipts"), "*.receipt").Length == 1, "external receipt");
        Assert(File.ReadAllText(Path.Combine(commandDir, "result.json")).Contains("\"request_id\""), "snake case result");
        Assert(ProfileAudit.Compare(audit, ProfileAudit.Capture(profile, settings)).Unchanged, "all profile files and machine settings unchanged");
    }
}
