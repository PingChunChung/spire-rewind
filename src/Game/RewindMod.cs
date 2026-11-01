using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using SpireRewind.Core;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpireRewind;

[ModInitializer(nameof(Initialize))]
public static class RewindMod
{
    public static bool Busy { get; internal set; }
    // Latched by RestoreService when a failed rewind left the scene or the save unverified; cleared only by a restart.
    public static bool RequiresRestart => RestoreFault.RequiresRestart;
    public static string? LastError { get; private set; }
    public static string GameVersion => ReleaseInfoManager.Instance.ReleaseInfo?.Version
        ?? throw new InvalidOperationException("Game release version unavailable");
    // The game version this release was tested on. The manifest's min_game_version keeps older games from loading the
    // mod; a newer game that still loads it gets a notice in the Rewind dialog. A restore of a checkpoint whose game
    // version differs from the running game is refused by RestoreService regardless of this constant.
    public const string TestedGameVersion = "0.107.1";
    // The game reports versions like "v0.107.1" (release_info.json); a missing version reads as unknown, never as tested.
    public static string? RunningGameVersion
    {
        get { try { return ReleaseInfoManager.Instance.ReleaseInfo?.Version; } catch { return null; } }
    }
    public static bool IsTestedGameVersion => RunningGameVersion is { } version
        && string.Equals(version.Trim().TrimStart('v', 'V'), TestedGameVersion, StringComparison.OrdinalIgnoreCase);
    public static string[] ModIds => ModManager.GetLoadedMods()
        .Select(m => $"{m.manifest!.id}@{m.manifest.version}").Append("SpireRewind@0.1.0")
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static ISaveStore Store => (ISaveStore)(AccessTools.Field(typeof(SaveManager), "_saveStore")
        .GetValue(SaveManager.Instance) ?? throw new InvalidOperationException("Save store unavailable"));
    public static RunSaveManager RunSaves => (RunSaveManager)AccessTools.Field(typeof(SaveManager), "_runSaveManager")
        .GetValue(SaveManager.Instance)!;
    public static CheckpointRepository Repository => new(ProjectSettings.GlobalizePath(
        SaveManager.Instance.GetProfileScopedPath("rewind")));
    public static RewindSettings Settings => Repository.ReadSettings();

    public static void Initialize()
    {
        try
        {
            new Harmony("SpireRewind.core").PatchAll(typeof(RewindMod).Assembly);
            // The shared page carries only warning codes; the exception with the real path goes to the game log.
            ReplayHtmlExporter.Log = ex => GD.PrintErr($"[Spire Rewind] export warning: {ex}");
            if (NGame.Instance != null) { TestCommandNode.Install(NGame.Instance); ReplayFrameDriver.Install(NGame.Instance); }
            GD.Print("[Spire Rewind] 0.1.0 loaded");
        }
        catch (Exception ex) { Error(ex); }
    }

    public static void Error(Exception ex)
    {
        LastError = ex.Message;
        GD.PrintErr($"[Spire Rewind] {ex}");
    }

    public static bool Supported(SerializableRun run) => run.Players?.Count == 1
        && run.SerializableRng?.Seed != null && !run.DailyTime.HasValue
        && run.GameMode is GameMode.Standard or GameMode.Custom;

    public static SerializableRun CurrentSave()
    {
        var result = SaveManager.Instance.LoadRunSave();
        return result.Success && result.SaveData != null ? result.SaveData
            : throw new InvalidDataException("Current run save cannot be read");
    }

    public static string RunId(SerializableRun run) => RunIdentity.Create(SaveManager.Instance.CurrentProfileId,
        run.SerializableRng.Seed!, run.StartTime, run.Players[0].CharacterId!.ToString());

    public static CheckpointMetadata Metadata(SerializableRun run, string kind)
    {
        var player = run.Players[0];
        var room = run.PreFinishedRoom?.RoomType.ToString()
            ?? run.MapPointHistory.LastOrDefault()?.LastOrDefault()?.Rooms.LastOrDefault()?.RoomType.ToString()
            ?? "Map";
        string destination = "Unknown";
        if (run.VisitedMapCoords.Count > 0 && run.CurrentActIndex < run.Acts.Count)
        {
            var coord = run.VisitedMapCoords[^1];
            var map = run.Acts[run.CurrentActIndex].SavedMap;
            var point = map?.Points.FirstOrDefault(p => p.Coord == coord)
                ?? (map?.BossPoint?.Coord == coord ? map.BossPoint : null)
                ?? (map?.SecondBossPoint?.Coord == coord ? map.SecondBossPoint : null)
                ?? (map?.StartingPoint?.Coord == coord ? map.StartingPoint : null);
            destination = point?.PointType.ToString() ?? "Unknown";
        }
        var stage = CheckpointStage.Describe(run.FloorReached,
            run.CurrentActIndex < run.MapPointHistory.Count ? run.MapPointHistory[run.CurrentActIndex].Count : 0,
            run.VisitedMapCoords.Count, run.PreFinishedRoom != null, room, destination, kind);
        return new CheckpointMetadata
        {
            RunId = RunId(run), ProfileId = SaveManager.Instance.CurrentProfileId,
            Seed = run.SerializableRng.Seed!, StartTime = run.StartTime,
            Character = player.CharacterId!.ToString(), GameVersion = GameVersion,
            RunSchemaVersion = run.SchemaVersion, Ascension = run.Ascension,
            Act = run.CurrentActIndex + 1, Floor = stage.Floor, RoomType = stage.RoomType,
            CaptureKind = stage.CaptureKind, CapturedAtUtc = DateTimeOffset.UtcNow,
            Hp = player.CurrentHp, MaxHp = player.MaxHp, Gold = player.Gold, DeckCount = player.Deck.Count,
            ModIds = ModIds
        };
    }

    internal sealed record Capture(CheckpointMetadata Metadata, string Json, int ProfileId);

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(SerializableRun), typeof(bool) })]
    private static class CaptureSavePatch
    {
        private static void Prefix(SerializableRun save, bool isMultiplayer, out Capture? __state)
        {
            __state = null;
            try
            {
                if (Busy || isMultiplayer || !Supported(save) || !Settings.Enabled) return;
                // Serialize before any await; never retain references into a live run.
                __state = new Capture(Metadata(save, save.PreFinishedRoom != null ? "room_completed" : "native_save"),
                    JsonSerializationUtility.ToJson(save), SaveManager.Instance.CurrentProfileId);
            }
            catch (Exception ex) { Error(ex); }
        }

        private static void Postfix(ref Task __result, Capture? __state)
        {
            if (__state != null) __result = AfterSave(__result, __state);
        }

        private static async Task AfterSave(Task original, Capture capture)
        {
            await original; // Keep native errors and completion semantics intact.
            try
            {
                if (Busy || SaveManager.Instance.CurrentProfileId != capture.ProfileId) return;
                string path = RunSaveManager.GetRunSavePath(capture.ProfileId, "current_run.save");
                if (Store.ReadFile(path) != capture.Json)
                    throw new IOException("Checkpoint skipped: saved payload changed during capture");
                var repository = Repository;
                repository.SaveCheckpoint(capture.Metadata, capture.Json);
                repository.ApplyRetention(Settings.RetainedRuns, capture.Metadata.RunId);
            }
            catch (Exception ex) { Error(ex); } // Archiving must not break the game's save.
        }
    }

    internal sealed record ReplayCapture(CheckpointMetadata Metadata, string Path, string? Before, string Kind, bool Complete,
        ReplaySnapshotsDocument? Snapshots);

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.WriteReplay))]
    private static class CaptureReplayPatch
    {
        private static void Prefix(RunManager __instance, bool stopRecording, out ReplayCapture? __state)
        {
            __state = null;
            try
            {
                if (Busy || !__instance.IsInProgress || (!__instance.ShouldSave && !__instance.IsCleaningUp)
                    || __instance.NetService.Type != NetGameType.Singleplayer || !Settings.Enabled) return;
                var run = __instance.ToSave(null);
                if (!Supported(run)) return;
                var path = ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("replays/latest.mcr"));
                bool ended = __instance.DebugOnlyGetState()?.IsGameOver == true;
                string kind = __instance.IsCleaningUp ? (ended ? "run_ended" : "interrupted")
                    : stopRecording ? "combat_completed" : "diagnostic";
                var metadata = Metadata(run, "combat_replay");
                __state = new ReplayCapture(metadata, path, Signature(path), kind,
                    ended || stopRecording && !__instance.IsCleaningUp, ReplaySnapshotRecorder.Freeze(metadata.RunId));
            }
            catch (Exception ex) { Error(ex); }
        }

        private static void Postfix(ReplayCapture? __state)
        {
            try
            {
                if (__state == null || !File.Exists(__state.Path) || Signature(__state.Path) == __state.Before) return;
                var info = new FileInfo(__state.Path);
                if (info.Length > 64 * 1024 * 1024) throw new IOException("Replay exceeds 64 MiB");
                byte[] replayBytes = File.ReadAllBytes(__state.Path);
                string archive = Repository.SaveReplay(__state.Metadata.RunId, __state.Metadata.Act, __state.Metadata.Floor,
                    replayBytes, __state.Metadata, __state.Kind, __state.Complete,
                    ReplayArchiveService.CurrentIdentity);
                if (__state.Snapshots is { } snapshots)
                {
                    try
                    {
                        new ReplaySnapshotsRepository(ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind")))
                            .Write(archive, snapshots with { ReplaySha256 = CoreFormat.Hash(replayBytes) });
                    }
                    catch (Exception ex) { Error(ex); } // Keep the replay and retention if snapshots are invalid.
                }
                Repository.ApplyRetention(Settings.RetainedRuns, __state.Metadata.RunId);
            }
            catch (Exception ex) { Error(ex); }
        }

        private static string? Signature(string path)
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024 * 1024) return $"oversize:{info.LastWriteTimeUtc.Ticks}";
            using var stream = File.OpenRead(path);
            return $"{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }
    }

    // A restore pins the profile it started in; the native save path follows the current profile, so switching
    // profiles while a restore is in flight (possible through the test channel, which has no modal dialog) could
    // write one run's save into another profile. The switch is refused twice: at the profile button, before the
    // native flow shows its loading overlay (ProfileButtonGuard), and at SaveManager.SwitchProfileId as the last
    // line of defence for any other caller (ProfileSwitchGuard), which also takes the overlay down again because
    // the native flow has already shown it by then. Both leave the current profile as it is.
    private static bool ProfileSwitchRefused(int profileId) => Busy && profileId != SaveManager.Instance.CurrentProfileId;

    [HarmonyPatch(typeof(NProfileButton), "OnRelease")]
    private static class ProfileButtonGuard
    {
        private static readonly FieldInfo ProfileIdField = AccessTools.Field(typeof(NProfileButton), "_profileId");

        private static bool Prefix(NProfileButton __instance)
        {
            try
            {
                // A button whose profile id cannot be read is not refused: the SwitchProfileId guard still applies.
                if (ProfileIdField?.GetValue(__instance) is not int profileId || !ProfileSwitchRefused(profileId)) return true;
                Error(new InvalidOperationException("profile_switch_refused_during_rewind"));
            }
            catch (Exception ex) { Error(ex); return true; }
            return false; // Nothing was shown or changed yet; the profile screen stays usable.
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SwitchProfileId))]
    private static class ProfileSwitchGuard
    {
        private static void Prefix(int profileId)
        {
            if (!ProfileSwitchRefused(profileId)) return;
            // NProfileButton.SwitchToThisProfile has called ShowLoading() before reaching here; the exception thrown
            // below ends that flow inside TaskHelper.RunSafely, which only logs it, so the overlay is hidden here.
            try { RewindUi.HideProfileLoading(); } catch (Exception ex) { Error(ex); }
            throw new InvalidOperationException("profile_switch_refused_during_rewind");
        }
    }

    // After a latched fault the run save on disk is unverified. RestoreService already refuses further rewinds; the
    // game's own Continue would still load that save, so its handler is refused here (with the dialog showing the
    // backup id) and RefreshButtons leaves the button disabled until the game is restarted.
    [HarmonyPatch(typeof(NMainMenu), "OnContinueButtonPressed")]
    private static class ContinueGuard
    {
        private static bool Prefix(NMainMenu __instance)
        {
            try
            {
                string? refusal = NativeContinueGate.Refusal();
                if (refusal == null) return true;
                Error(new InvalidOperationException(refusal));
                RewindUi.RefuseContinue(__instance);
            }
            catch (Exception ex) { Error(ex); }
            return false;
        }
    }

    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))]
    private static class PauseMenuPatch
    {
        private static void Postfix(NPauseMenu __instance) => RewindUi.Install(__instance, true);
    }

    [HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.RefreshButtons))]
    private static class MainMenuPatch
    {
        private static void Postfix(NMainMenu __instance)
        {
            RewindUi.Install(__instance, false);
            RewindUi.ApplyContinueGate(__instance);
        }
    }
}
