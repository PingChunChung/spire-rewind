using SpireRewind.Core;

internal static class VerifiedSaveWriterTests
{
    internal static async Task Run()
    {
        var events = new List<string>();
        string? local = "save";
        string? remote = "save";
        bool persisted = true;
        Task Save() { events.Add("save"); return Task.CompletedTask; }
        Task<string?> Local() { events.Add("local"); return Task.FromResult<string?>(local); }
        Task Publish(string text) { events.Add("publish:" + text); return Task.CompletedTask; }
        Task<string?> Remote() { events.Add("remote"); return Task.FromResult<string?>(remote); }
        bool Persisted() { events.Add("persisted"); return persisted; }
        void Order(params string[] expected)
        {
            if (!events.SequenceEqual(expected)) throw new Exception("Unexpected order: " + string.Join(", ", events));
            events.Clear();
        }
        async Task Expect<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        await VerifiedSaveWriter.WriteAsync("save", Save, Local);
        Order("save", "local");
        local = "old";
        await Expect<IOException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, Publish, Remote, Persisted));
        Order("save", "local");
        local = "save";
        Task FailedSave() { events.Add("save"); throw new InvalidOperationException("primary"); }
        await Expect<InvalidOperationException>(() => VerifiedSaveWriter.WriteAsync("save", FailedSave, Local, Publish, Remote, Persisted));
        Order("save");
        Task FailedPublish(string _) { events.Add("publish"); throw new InvalidOperationException("cloud"); }
        await Expect<InvalidOperationException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, FailedPublish, Remote, Persisted));
        Order("save", "local", "publish");
        remote = "old";
        await Expect<IOException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, Publish, Remote, Persisted));
        Order("save", "local", "publish:save", "remote");
        remote = "save"; persisted = false;
        await Expect<IOException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, Publish, Remote, Persisted));
        Order("save", "local", "publish:save", "remote", "persisted");
        persisted = true;
        await VerifiedSaveWriter.WriteAsync("save", Save, Local, Publish, Remote, Persisted);
        Order("save", "local", "publish:save", "remote", "persisted");
        await Expect<ArgumentException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, Publish));
        Order();
        await Expect<ArgumentException>(() => VerifiedSaveWriter.WriteAsync("save", Save, Local, readRemote: Remote, isPersisted: Persisted));
        Order();
    }
}
