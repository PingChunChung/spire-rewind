using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireRewind.Core;

public sealed record RunChoiceEntry
{
    [JsonRequired] public string EntryId { get; init; } = "";
    [JsonRequired] public int Act { get; init; }
    [JsonRequired] public int Floor { get; init; }
    [JsonRequired] public string Kind { get; init; } = "";
    public string? ModelId { get; init; }
    public string? TextKey { get; init; }
    public string? Value { get; init; }
    [JsonRequired] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonRequired] public string Source { get; init; } = "game";
    public string? AiReason { get; init; }
}
public sealed record RunChoicesDocument
{
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public string RunId { get; init; } = "";
    [JsonRequired] public RunChoiceEntry[] Entries { get; init; } = [];
}

public sealed class RunChoiceRepository
{
    public const int MaximumBytes = 8 * 1024 * 1024, MaximumEntries = 10000;
    private readonly string root;
    private static readonly JsonSerializerOptions Json = new(CoreFormat.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public RunChoiceRepository(string root) { ArgumentException.ThrowIfNullOrWhiteSpace(root); this.root = Path.GetFullPath(root); CheckPath(this.root); }
    private string PathFor(string runId) { CoreFormat.RequireId(runId); return Path.Combine(root, "runs", runId, "choices.json"); }
    public RunChoicesDocument Read(string runId)
    {
        string path = PathFor(runId); CheckPath(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length > MaximumBytes) throw new InvalidDataException("run_choices_limit");
        var bytes = new byte[checked((int)f.Length)]; f.ReadExactly(bytes);
        using var parsed = JsonDocument.Parse(bytes); CheckDuplicates(parsed.RootElement);
        var doc = JsonSerializer.Deserialize<RunChoicesDocument>(bytes, Json) ?? throw new InvalidDataException("invalid_run_choices");
        Validate(doc, runId); return doc;
    }
    public void Append(string runId, RunChoiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path = PathFor(runId); CheckPath(path);
        RunChoicesDocument old = File.Exists(path) ? Read(runId) : new() { RunId = runId };
        // Re-delivery of the same observation is idempotent; a new ID is a distinct choice.
        if (old.Entries.Any(x => x.EntryId == entry.EntryId))
        {
            if (old.Entries.Any(x => x == entry)) return;
            throw new InvalidDataException("duplicate_run_choice_id");
        }
        var doc = old with { Entries = [.. old.Entries, entry] }; Validate(doc, runId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(doc, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("run_choices_limit");
        string? dir = Path.GetDirectoryName(path); CheckPath(dir!); Directory.CreateDirectory(dir!); CheckPath(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { CheckPath(temp); using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); } CheckPath(path); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Validate(RunChoicesDocument d, string runId)
    {
        if (d.FormatVersion != 1 || d.RunId != runId || d.Entries == null || d.Entries.Length > MaximumEntries) throw new InvalidDataException("invalid_run_choices");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in d.Entries)
        {
            if (e == null || !Guid.TryParseExact(e.EntryId, "N", out _) || !ids.Add(e.EntryId) || e.Act is < 1 or > 1000 || e.Floor is < 0 or > 1000000 || e.CapturedAtUtc == default || e.CapturedAtUtc.Offset != TimeSpan.Zero || e.Kind is not ("card_reward" or "shop_purchase" or "card_removal" or "event_option") || e.Source != "game") throw new InvalidDataException("invalid_run_choices");
            CheckText(e.ModelId, 256); CheckText(e.TextKey, 256); CheckText(e.Value, 256); CheckText(e.AiReason, 2048);
        }
    }
    private static void CheckText(string? s, int max) { if (s != null && (string.IsNullOrWhiteSpace(s) || s.Length > max || s.Any(char.IsControl))) throw new InvalidDataException("invalid_run_choices"); }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("invalid_run_choices"); CheckDuplicates(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) CheckDuplicates(x);
    }
    private static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null) { try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("run_choices_link_refused"); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } current = Path.GetDirectoryName(current); }
    }
}

// Counts of one player's per-room history lists at one observation.
public readonly record struct RoomHistoryCounts(int CardChoices, int CardsRemoved, int BoughtRelics, int BoughtPotions, int BoughtColorless);

// Cursor over one player's per-room history. Entries that already exist when the cursor attaches to a run (a continued
// save, or a run that was already in progress when the mod attached) are historical and never reported; only entries
// appended afterwards are new. Card removals advance separately, because the game reports them through another hook.
public sealed class RoomHistoryCursor
{
    public bool Attached { get; private set; }
    public int CardChoices { get; private set; }
    public int CardsRemoved { get; private set; }
    public int BoughtRelics { get; private set; }
    public int BoughtPotions { get; private set; }
    public int BoughtColorless { get; private set; }

    public void Detach() { Attached = false; CardChoices = CardsRemoved = BoughtRelics = BoughtPotions = BoughtColorless = 0; }

    // Returns the indices of the card choices appended since the previous observation. Returns null and detaches when a
    // list shrank (another run state, or a replaced history); the next observation attaches again as historical.
    public int[]? Observe(RoomHistoryCounts counts, bool newRun, bool newRoom)
    {
        if (newRun || !Attached)
        {
            Attached = true;
            CardChoices = counts.CardChoices; CardsRemoved = counts.CardsRemoved;
            BoughtRelics = counts.BoughtRelics; BoughtPotions = counts.BoughtPotions; BoughtColorless = counts.BoughtColorless;
            return [];
        }
        if (newRoom) CardChoices = CardsRemoved = BoughtRelics = BoughtPotions = BoughtColorless = 0;
        if (CardChoices > counts.CardChoices || CardsRemoved > counts.CardsRemoved || BoughtRelics > counts.BoughtRelics
            || BoughtPotions > counts.BoughtPotions || BoughtColorless > counts.BoughtColorless) { Detach(); return null; }
        var added = Enumerable.Range(CardChoices, counts.CardChoices - CardChoices).ToArray();
        CardChoices = counts.CardChoices;
        BoughtRelics = counts.BoughtRelics; BoughtPotions = counts.BoughtPotions; BoughtColorless = counts.BoughtColorless;
        return added;
    }

    // The next unobserved removal index, or -1 when every known removal was already reported.
    public int NextRemoval(int available) => Attached && CardsRemoved < available ? CardsRemoved : -1;
    public void AdvanceRemoval() { if (Attached) CardsRemoved++; }
}
