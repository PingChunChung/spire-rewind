namespace SpireRewind.Core;

public static class VerifiedSaveWriter
{
    public static async Task WriteAsync(
        string expected,
        Func<Task> save,
        Func<Task<string?>> readLocal,
        Func<string, Task>? publish = null,
        Func<Task<string?>>? readRemote = null,
        Func<bool>? isPersisted = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(readLocal);
        bool anyCloud = publish is not null || readRemote is not null || isPersisted is not null;
        if (anyCloud && (publish is null || readRemote is null || isPersisted is null))
            throw new ArgumentException("Cloud callbacks must be supplied together.");

        await save();
        if (!string.Equals(await readLocal(), expected, StringComparison.Ordinal))
            throw new IOException("Local save readback does not match.");

        if (publish is not null)
        {
            await publish(expected);
            if (!string.Equals(await readRemote!(), expected, StringComparison.Ordinal))
                throw new IOException("Remote save readback does not match.");
            if (!isPersisted!())
                throw new IOException("Remote save is not persisted.");
        }
    }
}
