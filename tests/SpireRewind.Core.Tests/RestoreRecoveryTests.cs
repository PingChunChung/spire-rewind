using SpireRewind.Core;

internal static class RestoreRecoveryTests
{
    public static async Task Run()
    {
        var calls = new List<string>();
        var failure = await RestoreRecovery.RunAsync(
            () => { calls.Add("cleanup"); throw new InvalidOperationException("partial initialization"); },
            () => { calls.Add("recover"); return Task.CompletedTask; });
        if (failure?.Message != "partial initialization" || !calls.SequenceEqual(new[] { "cleanup", "recover" }))
            throw new Exception("Cleanup failure prevented original save recovery");
        if (await RestoreRecovery.RunAsync(() => Task.CompletedTask, () => Task.CompletedTask) != null)
            throw new Exception("Successful recovery reported a cleanup failure");
        try
        {
            await RestoreRecovery.RunAsync(() => throw new IOException("cleanup"), () => throw new IOException("cloud"));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 2) { return; }
        throw new Exception("Recovery failure was not reported");
    }
}
