namespace SpireRewind.Core;

/// <summary>
/// Process-wide latch for a restore that left the game scene or the run save in a state the mod could not verify.
/// Once raised, every further rewind is refused until the game is restarted; the first fault and its backup
/// checkpoint id stay visible so the player can recover by hand.
/// </summary>
public static class RestoreFault
{
    private static readonly object Gate = new();
    public static bool RequiresRestart { get; private set; }
    public static string? Code { get; private set; }
    public static string? BackupId { get; private set; }

    /// <summary>Latches the fault and returns the diagnostic message for this failure.</summary>
    public static string Raise(string code, string? backupId, params string?[] details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (Gate)
        {
            if (!RequiresRestart) { RequiresRestart = true; Code = code; BackupId = backupId; }
            else BackupId ??= backupId;
        }
        return Describe(code, backupId, details);
    }

    public static string Describe(string code, string? backupId, params string?[] details) =>
        string.Join("; ", new[] { code, backupId == null ? null : "backup=" + backupId }.Concat(details).Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>The latched fault as shown in the dialog, or null when no fault is latched.</summary>
    public static string? Notice { get { lock (Gate) return RequiresRestart ? Describe(Code!, BackupId) : null; } }

    /// <summary>Clears the latch. Only tests call this; a real fault ends with a game restart.</summary>
    public static void ResetForTests() { lock (Gate) { RequiresRestart = false; Code = null; BackupId = null; } }
}

/// <summary>
/// Gate for the game's own main-menu Continue while a <see cref="RestoreFault"/> is latched. The run save on disk is
/// then unverified (the rollback failed, or the scene cleanup did), so loading it through the native button is refused
/// and the button stays disabled until the game is restarted. Pure logic so it can be tested without the game.
/// </summary>
public static class NativeContinueGate
{
    public const string Code = "continue_refused_restart_required";

    /// <summary>Null when the native Continue may load the save; otherwise the diagnostic to show, with the backup id.</summary>
    public static string? Refusal()
    {
        return RestoreFault.RequiresRestart ? RestoreFault.Describe(Code, RestoreFault.BackupId, RestoreFault.Code) : null;
    }

    /// <summary>The enabled state the Continue button must end up in, given what the game itself decided.</summary>
    public static bool ButtonEnabled(bool nativeEnabled) => nativeEnabled && !RestoreFault.RequiresRestart;
}
