using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace SpireRewind.Core;

public sealed record ProfileAuditLimits(int MaximumFiles = 100_000, long MaximumBytes = 8L * 1024 * 1024 * 1024, int MaximumEntries = 200_000);
public sealed record ProfileAuditFile(string RelativePath, long Length, string Sha256);
public sealed record ProfileAuditDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Deleted, IReadOnlyList<string> Changed)
{
    public bool Unchanged => Added.Count == 0 && Deleted.Count == 0 && Changed.Count == 0;
}

public sealed class ProfileAuditSnapshot
{
    internal ProfileAuditSnapshot(string profileRoot, string? machinePath, ProfileAuditFile[] files)
    {
        ProfileRoot = profileRoot; MachineSettingsPath = machinePath;
        Files = Array.AsReadOnly(files);
    }
    public string ProfileRoot { get; }
    public string? MachineSettingsPath { get; }
    public ReadOnlyCollection<ProfileAuditFile> Files { get; }
}

/// <summary>Read-only, in-memory file audit. A failed capture never yields a snapshot.</summary>
public static class ProfileAudit
{
    public static ProfileAuditSnapshot Capture(string profileRoot, string? machineSettingsPath = null, ProfileAuditLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        limits ??= new();
        if (limits.MaximumFiles < 1 || limits.MaximumBytes < 0 || limits.MaximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
        string? machine = machineSettingsPath == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(machineSettingsPath));
        var files = new List<ProfileAuditFile>();
        long totalBytes = 0;
        int entries = 0;

        void Visit(string path, string name, bool directoryRequired = false, int depth = 0)
        {
            if (depth > 256) throw new InvalidDataException("profile_audit_depth_limit");
            if (++entries > limits.MaximumEntries) throw new InvalidDataException("profile_audit_entry_limit");
            CheckPath(path);
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    Visit(child, name + "/" + Path.GetFileName(child), depth: depth + 1);
                CheckPath(path);
                return;
            }
            if (directoryRequired) throw new IOException("profile_audit_root_not_directory");
            if (files.Count >= limits.MaximumFiles) throw new InvalidDataException("profile_audit_file_limit");
            var info = new FileInfo(path);
            long length = info.Length;
            DateTime modified = info.LastWriteTimeUtc;
            if (length > limits.MaximumBytes - totalBytes) throw new InvalidDataException("profile_audit_byte_limit");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != length) throw new IOException("profile_audit_file_changed_during_read");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long readTotal = 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                readTotal += count;
                if (readTotal > length || readTotal > limits.MaximumBytes - totalBytes)
                    throw new IOException("profile_audit_file_changed_during_read");
                hash.AppendData(buffer, 0, count);
            }
            CheckPath(path);
            info.Refresh();
            if (readTotal != length || stream.Length != length || info.Length != length || info.LastWriteTimeUtc != modified)
                throw new IOException("profile_audit_file_changed_during_read");
            totalBytes += length;
            files.Add(new(name, length, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }

        Visit(profile, "profile", true);
        if (machine != null)
        {
            CheckPath(machine);
            var attributes = File.GetAttributes(machine);
            Visit(machine, (attributes & FileAttributes.Directory) != 0 ? "machine" : "machine/" + Path.GetFileName(machine));
        }
        return new(profile, machine, files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
    }

    public static ProfileAuditDifference Compare(ProfileAuditSnapshot before, ProfileAuditSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(before.ProfileRoot, after.ProfileRoot, pathComparison) ||
            !string.Equals(before.MachineSettingsPath, after.MachineSettingsPath, pathComparison))
            throw new ArgumentException("profile_audit_scope_mismatch");
        var oldFiles = before.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal);
        var newFiles = after.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal);
        return new(
            Array.AsReadOnly(newFiles.Keys.Except(oldFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(oldFiles.Keys.Except(newFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(oldFiles.Keys.Intersect(newFiles.Keys, StringComparer.Ordinal)
                .Where(name => oldFiles[name].Length != newFiles[name].Length || oldFiles[name].Sha256 != newFiles[name].Sha256)
                .Order(StringComparer.Ordinal).ToArray()));
    }

    private static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("profile_audit_link_refused");
            current = Path.GetDirectoryName(current);
        }
    }
}
