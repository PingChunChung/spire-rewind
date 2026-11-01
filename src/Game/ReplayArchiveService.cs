using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using SpireRewind.Core;

namespace SpireRewind;

/// <summary>The production replay entry point is inspection only until persistence isolation exists.</summary>
internal static class ReplayArchiveService
{
    public static ReplayRuntimeIdentity CurrentIdentity
    {
        get
        {
            var release = ReleaseInfoManager.Instance.ReleaseInfo ?? throw new InvalidOperationException("replay_runtime_unavailable");
            return new(release.Version, release.Commit, ModelIdSerializationCache.Hash, RewindMod.ModIds);
        }
    }

    public static IReadOnlyList<ReplayArchiveEntry> List()
    {
        RequireMenu();
        return RewindMod.Repository.ListReplays();
    }

    public static ReplayInspection Inspect(ReplayArchiveEntry entry)
    {
        RequireMenu();
        return RewindMod.Repository.InspectReplay(entry, CurrentIdentity);
    }

    private static void RequireMenu()
    {
        ReplayPlaybackService.RequireMenu();
    }

    // Deliberately no parser, scene creation, SetUpReplay, or playback escape hatch.
    public static void Play(ReplayArchiveEntry entry)
    {
        var result = Inspect(entry);
        throw new InvalidOperationException(result.Error ?? ReplayInspection.PlaybackBlockedReason);
    }
}
