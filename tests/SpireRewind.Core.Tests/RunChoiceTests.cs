using SpireRewind.Core;

internal static class RunChoiceTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Choice test: " + message); }
    private static void Throws(Action a) { try { a(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or System.Text.Json.JsonException) { return; } throw new Exception("Expected rejection"); }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "spire-choices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = RunIdentity.Create(1, "seed", 1, "hero"), b = RunIdentity.Create(2, "seed", 1, "hero");
            var repo = new RunChoiceRepository(root);
            var first = new RunChoiceEntry { EntryId = Guid.NewGuid().ToString("N"), Act = 1, Floor = 3, Kind = "card_reward", ModelId = "Strike", CapturedAtUtc = DateTimeOffset.UtcNow };
            repo.Append(a, first); repo.Append(a, first);
            var again = first with { EntryId = Guid.NewGuid().ToString("N") };
            repo.Append(a, again); repo.Append(b, first);
            Assert(repo.Read(a).Entries.Length == 2 && repo.Read(b).Entries.Length == 1, "same ID idempotent, new ID preserved and runs isolated");
            Throws(() => repo.Append(a, first with { Kind = "event_option" }));
            Throws(() => repo.Append("../escape", first));
            Throws(() => repo.Append(a, first with { EntryId = Guid.NewGuid().ToString("N"), Floor = -1 }));
            Throws(() => repo.Append(a, first with { EntryId = Guid.NewGuid().ToString("N"), ModelId = new string('a', 257) }));
            Throws(() => repo.Append(a, first with { EntryId = Guid.NewGuid().ToString("N"), CapturedAtUtc = DateTimeOffset.MinValue }));
            string path = Path.Combine(root, "runs", a, "choices.json");
            string good = File.ReadAllText(path);
            File.WriteAllText(path, good.Replace(a, b)); Throws(() => repo.Read(a));
            File.WriteAllText(path, good.Replace("\"format_version\": 1", "\"format_version\": 1, \"format_version\": 1")); Throws(() => repo.Read(a));
            File.WriteAllBytes(path, new byte[RunChoiceRepository.MaximumBytes + 1]); Throws(() => repo.Read(a));
        }
        finally { Directory.Delete(root, true); }
    }
}

internal static class RoomHistoryCursorTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Cursor test: " + message); }
    internal static void Run()
    {
        var cursor = new RoomHistoryCursor();
        // Continue on a reward screen: the room already holds the reward's history. Nothing is reported for it.
        var added = cursor.Observe(new RoomHistoryCounts(3, 1, 0, 0, 0), newRun: true, newRoom: true);
        Assert(added is { Length: 0 } && cursor.Attached && cursor.CardChoices == 3 && cursor.CardsRemoved == 1, "existing entries are historical");
        // The pick made after continuing is appended and reported exactly once.
        added = cursor.Observe(new RoomHistoryCounts(4, 1, 0, 0, 0), newRun: false, newRoom: false);
        Assert(added is [3], "one new pick");
        added = cursor.Observe(new RoomHistoryCounts(4, 1, 0, 0, 0), newRun: false, newRoom: false);
        Assert(added is { Length: 0 }, "no duplicate on the next observation");
        // A removal is reported through the removal hook and advances separately.
        Assert(cursor.NextRemoval(1) == -1 && cursor.NextRemoval(2) == 1, "next removal index");
        cursor.AdvanceRemoval();
        Assert(cursor.NextRemoval(2) == -1, "removal consumed");
        // A new room in the same run starts from empty lists.
        added = cursor.Observe(new RoomHistoryCounts(2, 0, 1, 0, 0), newRun: false, newRoom: true);
        Assert(added is [0, 1] && cursor.BoughtRelics == 1 && cursor.CardsRemoved == 0, "new room counts from zero");
        // A shrinking list means another history: detach, then attach again as historical.
        added = cursor.Observe(new RoomHistoryCounts(1, 0, 1, 0, 0), newRun: false, newRoom: false);
        Assert(added == null && !cursor.Attached && cursor.NextRemoval(5) == -1, "shrink detaches");
        added = cursor.Observe(new RoomHistoryCounts(1, 0, 1, 0, 0), newRun: false, newRoom: false);
        Assert(added is { Length: 0 } && cursor.Attached && cursor.CardChoices == 1, "re-attach is historical");
        // A new run object re-attaches even without a room change.
        added = cursor.Observe(new RoomHistoryCounts(5, 2, 0, 1, 1), newRun: true, newRoom: false);
        Assert(added is { Length: 0 } && cursor.CardChoices == 5 && cursor.BoughtPotions == 1, "new run is historical");
        cursor.Detach();
        Assert(!cursor.Attached && cursor.CardChoices == 0, "detach clears");
    }
}
