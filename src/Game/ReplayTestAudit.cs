using Godot;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;
using SpireRewind.Core;

namespace SpireRewind;

// Test-only evidence. This never restores files or swaps the live SaveManager.
internal static class ReplayTestAudit
{
    private static ProfileAuditSnapshot? baseline;
    private static string? memory;
    private static int profile;
    internal static bool Active => baseline != null;
    private static string Memory() => JsonSerializationUtility.ToJson(SaveManager.Instance.Progress.ToSerializable())
        + "\n" + JsonSerializationUtility.ToJson(SaveManager.Instance.PrefsSave)
        + "\n" + JsonSerializationUtility.ToJson(SaveManager.Instance.SettingsSave);
    internal static object Begin()
    {
        ReplayPlaybackService.RequireMenu();
        if (Active || ReplayPlaybackService.Active) throw new InvalidOperationException("replay_audit_already_active");
        if (PlatformUtil.PrimaryPlatform != PlatformType.None) throw new InvalidOperationException("replay_audit_requires_none_platform");
        string profileRoot = ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath(""));
        string accountRoot = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null));
        var captured = ProfileAudit.Capture(profileRoot, accountRoot);
        string capturedMemory = Memory();
        profile = SaveManager.Instance.CurrentProfileId; memory = capturedMemory; baseline = captured;
        return new { file_count = captured.Files.Count, profile_root = profileRoot, account_root = accountRoot,
            user_root = ProjectSettings.GlobalizePath("user://"), scope = "profile_and_account_all_files", platform = "None" };
    }
    internal static object End()
    {
        var before = baseline ?? throw new InvalidOperationException("replay_audit_not_active");
        // End first; a failed hash read must not be mistaken for a reusable baseline.
        baseline = null;
        ReplayPlaybackService.Stop();
        if (profile != SaveManager.Instance.CurrentProfileId) throw new InvalidOperationException("replay_audit_profile_changed");
        var after = ProfileAudit.Capture(before.ProfileRoot, before.MachineSettingsPath);
        var difference = ProfileAudit.Compare(before, after);
        bool memoryUnchanged = Memory() == memory; memory = null;
        return new { unchanged = difference.Unchanged && memoryUnchanged, files_unchanged = difference.Unchanged,
            memory_unchanged = memoryUnchanged, difference, file_count = after.Files.Count };
    }
}
