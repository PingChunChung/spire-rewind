using SpireRewind.Core;

internal static class ProfileAuditTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Profile audit: " + message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException) { return; }
        throw new Exception("Profile audit did not reject: " + message);
    }

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "spire-audit-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(root, "profile"), settings = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(Path.Combine(profile, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(profile, "nested", "save"), "abc");
            File.WriteAllText(Path.Combine(profile, "delete"), "gone");
            File.WriteAllText(settings, "settings");
            var before = ProfileAudit.Capture(profile, settings);
            Assert(before.Files.Select(x => x.RelativePath).SequenceEqual(new[] { "machine/settings.json", "profile/delete", "profile/nested/save" }), "sorted files and machine settings");
            Assert(ProfileAudit.Compare(before, ProfileAudit.Capture(profile, settings)).Unchanged, "unchanged");
            File.WriteAllText(Path.Combine(profile, "nested", "save"), "xyz");
            File.Delete(Path.Combine(profile, "delete"));
            File.WriteAllText(Path.Combine(profile, "added"), "new");
            var diff = ProfileAudit.Compare(before, ProfileAudit.Capture(profile, settings));
            Assert(!diff.Unchanged && diff.Changed.SequenceEqual(new[] { "profile/nested/save" }) && diff.Added.SequenceEqual(new[] { "profile/added" }) && diff.Deleted.SequenceEqual(new[] { "profile/delete" }), "same length change, addition, deletion");
            Throws(() => ProfileAudit.Capture(Path.Combine(root, "missing")), "missing root");
            Throws(() => ProfileAudit.Capture(profile, Path.Combine(root, "missing")), "missing settings");
            Throws(() => ProfileAudit.Capture(profile, limits: new(MaximumFiles: 1)), "file limit");
            Throws(() => ProfileAudit.Capture(profile, limits: new(MaximumBytes: 2)), "byte limit");
            Throws(() => ProfileAudit.Capture(profile, limits: new(MaximumEntries: 1)), "entry limit");
            Throws(() => ProfileAudit.Compare(before, ProfileAudit.Capture(profile)), "scope mismatch");
            var machineDirectory = ProfileAudit.Capture(profile, Path.Combine(profile, "nested"));
            Assert(machineDirectory.Files.Any(x => x.RelativePath == "machine/save"), "machine directory");
            using (var locked = new FileStream(Path.Combine(profile, "added"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws(() => ProfileAudit.Capture(profile), "unreadable file");
            string links = Path.Combine(root, "links"); Directory.CreateDirectory(links);
            try { Directory.CreateSymbolicLink(Path.Combine(links, "link"), profile); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { Console.WriteLine("Profile audit symlink tests skipped: " + e.Message); return; }
            Throws(() => ProfileAudit.Capture(Path.Combine(links, "link")), "root link");
            Throws(() => ProfileAudit.Capture(Path.Combine(links, "link", "nested")), "ancestor link");
            Throws(() => ProfileAudit.Capture(links), "child link");
        }
        finally { Directory.Delete(root, true); }
    }
}
