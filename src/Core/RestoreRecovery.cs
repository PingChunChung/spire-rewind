namespace SpireRewind.Core;

public static class RestoreRecovery
{
    // Restoring the save must run even if native scene teardown is incomplete.
    public static async Task<Exception?> RunAsync(Func<Task> cleanup, Func<Task> recoverSave)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(recoverSave);
        Exception? cleanupFailure = null;
        try { await cleanup(); }
        catch (Exception ex) { cleanupFailure = ex; }
        try { await recoverSave(); }
        catch (Exception ex)
        {
            throw new AggregateException("Save recovery could not be verified.",
                cleanupFailure == null ? [ex] : new[] { cleanupFailure, ex });
        }
        return cleanupFailure;
    }
}
