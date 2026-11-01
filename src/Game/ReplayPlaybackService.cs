using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireRewind.Core;

namespace SpireRewind;

// Safe alternative: detached data snapshots, never the game's replay executor.
internal static class ReplayPlaybackService
{
    internal static ReplaySnapshotSession? Session { get; private set; }
    internal static bool Active => Session != null;
    internal static string? LastError { get; private set; }
    internal static event Action? Changed;
    private static int profile;
    private static string? profileRoot;

    internal static void RequireMenu()
    {
        if (RewindMod.Busy || RewindMod.RequiresRestart || RunManager.Instance.IsInProgress
            || NGame.Instance?.MainMenu?.IsVisibleInTree() != true)
            throw new InvalidOperationException("replay_main_menu_only");
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false })
            throw new InvalidOperationException("replay_save_in_flight");
    }
    internal static ReplaySnapshotsReadResult Inspect(ReplayArchiveEntry entry)
    {
        RequireMenu();
        return new ReplaySnapshotsRepository(RewindRoot).ReadForReplay(entry, ReplayArchiveService.CurrentIdentity);
    }
    private static string RewindRoot => ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind"));
    internal static void Start(ReplayArchiveEntry entry)
    {
        RequireMenu();
        if (Active) throw new InvalidOperationException("replay_already_active");
        var result = Inspect(entry);
        if (result.Document == null) throw new InvalidDataException(result.Error);
        profile = SaveManager.Instance.CurrentProfileId; profileRoot = RewindRoot;
        Session = new ReplaySnapshotSession(result.Document); LastError = null;
    }
    internal static void Control(string command, int? speed = null)
    {
        RequireMenu();
        var session = Session ?? throw new InvalidOperationException("replay_not_active");
        switch (command)
        {
            case "replay-resume": session.Play(); break;
            case "replay-pause": session.Pause(); break;
            case "replay-step": session.Step(); break;
            case "replay-previous": session.Previous(); break;
            case "replay-speed": session.SetSpeed(speed ?? throw new ArgumentException("invalid_speed")); break;
            default: throw new ArgumentException("unknown_command");
        }
        Changed?.Invoke();
    }
    internal static void Tick(double elapsed)
    {
        if (Session == null) return;
        try
        {
            RequireMenu();
            if (SaveManager.Instance.CurrentProfileId != profile || RewindRoot != profileRoot)
                throw new InvalidOperationException("replay_profile_changed");
            int before = Session.Index; Session.Advance(elapsed);
            if (before != Session.Index) Changed?.Invoke();
        }
        catch (Exception ex) { Stop(ex.Message); }
    }
    internal static void Stop(string? error = null)
    {
        Session?.Dispose(); Session = null; profileRoot = null;
        LastError = error;
        // No run scene was ever entered. Disposing leaves the existing menu intact.
        try { Changed?.Invoke(); } catch (Exception ex) { RewindMod.Error(ex); }
    }
    internal static object Status() => new
    {
        kind = "decision_snapshots", native_playback = false, active = Active,
        index = Session == null ? (int?)null : Session.Index, frame_count = Session?.FrameCount ?? 0,
        playing = Session?.Playing ?? false, speed = Session?.Speed ?? 1,
        completed = Session?.Completed ?? false, frame = Session?.Current, error = LastError,
        previous_strategy = ReplaySnapshotSession.PreviousStrategy
    };
}
