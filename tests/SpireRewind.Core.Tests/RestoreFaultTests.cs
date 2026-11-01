using SpireRewind.Core;

// The latch RestoreService raises when a rewind fails and the rollback of the original save fails as well (or the scene
// cleanup fails after a successful rollback). It must refuse further rewinds immediately and keep the backup id.
internal static class RestoreFaultTests
{
    public static void Run()
    {
        void Assert(bool b, string message) { if (!b) throw new Exception(message); }
        RestoreFault.ResetForTests();
        Assert(!RestoreFault.RequiresRestart && RestoreFault.Notice == null, "no fault at start");
        // The native main-menu Continue follows the latch: allowed and left as the game set it before a fault, refused
        // with the backup id and forced off afterwards, usable again only after a reset (a restart in the game).
        Assert(NativeContinueGate.Refusal() == null, "continue allowed without a fault");
        Assert(NativeContinueGate.ButtonEnabled(true) && !NativeContinueGate.ButtonEnabled(false), "continue button follows the game without a fault");
        RestoreFault.Raise("restore_and_rollback_failed", "backup0", "why");
        Assert(NativeContinueGate.Refusal() == "continue_refused_restart_required; backup=backup0; restore_and_rollback_failed", "continue refused with the backup id");
        Assert(!NativeContinueGate.ButtonEnabled(true) && !NativeContinueGate.ButtonEnabled(false), "continue button forced off by a fault");
        RestoreFault.ResetForTests();
        RestoreFault.Raise("restore_failed_original_recovered_restart_required", null, "cleanup");
        Assert(NativeContinueGate.Refusal() == "continue_refused_restart_required; restore_failed_original_recovered_restart_required", "continue refused without a backup id");
        RestoreFault.ResetForTests();
        Assert(NativeContinueGate.Refusal() == null && NativeContinueGate.ButtonEnabled(true), "continue allowed again after reset");
        Assert(RestoreFault.Describe("restore_failed_original_recovered", "b1", "first") == "restore_failed_original_recovered; backup=b1; first", "describe with backup");
        Assert(RestoreFault.Describe("restore_original_unchanged_restart_required", null, "why") == "restore_original_unchanged_restart_required; why", "describe without backup");
        Assert(!RestoreFault.RequiresRestart, "describe does not latch");

        string message = RestoreFault.Raise("restore_and_rollback_failed", "backup1", "target write failed", "rollback failed");
        Assert(message == "restore_and_rollback_failed; backup=backup1; target write failed; rollback failed", "rollback failure message");
        Assert(RestoreFault.RequiresRestart && RestoreFault.Code == "restore_and_rollback_failed" && RestoreFault.BackupId == "backup1", "fault latched with backup id");
        Assert(RestoreFault.Notice == "restore_and_rollback_failed; backup=backup1", "notice keeps the backup id");

        // A later fault cannot replace the first one: the backup id the player needs is the first one.
        string second = RestoreFault.Raise("restore_failed_original_recovered_restart_required", "backup2", "later");
        Assert(second.StartsWith("restore_failed_original_recovered_restart_required; backup=backup2", StringComparison.Ordinal), "second message describes itself");
        Assert(RestoreFault.Code == "restore_and_rollback_failed" && RestoreFault.BackupId == "backup1" && RestoreFault.RequiresRestart, "first fault is kept");

        RestoreFault.ResetForTests();
        RestoreFault.Raise("restore_original_unchanged_restart_required", null, "cleanup");
        RestoreFault.Raise("restore_and_rollback_failed", "backup3");
        Assert(RestoreFault.Code == "restore_original_unchanged_restart_required" && RestoreFault.BackupId == "backup3", "a missing backup id is filled in by a later fault");
        bool rejected = false;
        try { RestoreFault.Raise(" ", null); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "blank code rejected");
        RestoreFault.ResetForTests();
        Assert(!RestoreFault.RequiresRestart && RestoreFault.Notice == null, "reset clears the latch");
    }
}
