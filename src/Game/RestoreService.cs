using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using SpireRewind.Core;
using System.Text;

namespace SpireRewind;

public static class RestoreService
{
    public static bool CanRestore(SerializableRun save)
    {
        var manager = RunManager.Instance;
        return !RewindMod.RequiresRestart && RewindMod.Supported(save) && (!manager.IsInProgress
            || manager.ShouldSave && manager.NetService.Type == NetGameType.Singleplayer
                && manager.DebugOnlyGetState()?.IsGameOver == false
                && RewindMod.RunId(manager.ToSave(null)) == RewindMod.RunId(save));
    }

    // The profile, save path, store and run save manager a restore started with. Every write and read-back of the
    // transaction, including the rollback, goes through these; a profile switch while Busy is refused by
    // RewindMod.ProfileSwitchGuard, and Check() catches anything that slipped past it before a write.
    private sealed class Pin
    {
        public int Profile { get; }
        public string Path { get; }
        public ISaveStore Store { get; }
        public RunSaveManager RunSaves { get; }
        public Pin()
        {
            Profile = SaveManager.Instance.CurrentProfileId;
            Path = RunSaveManager.GetRunSavePath(Profile, "current_run.save");
            Store = RewindMod.Store;
            RunSaves = RewindMod.RunSaves;
        }
        public void Check()
        {
            if (SaveManager.Instance.CurrentProfileId != Profile || !ReferenceEquals(RewindMod.Store, Store))
                throw new InvalidOperationException("profile_changed");
        }
    }

    public static async Task Restore(CheckpointMetadata checkpoint, bool mark)
    {
        if (RewindMod.Busy) throw new InvalidOperationException("restore_busy");
        if (RewindMod.RequiresRestart) throw new InvalidOperationException("restart_required; " + RestoreFault.Notice);
        RewindMod.Busy = true;
        string? originalJson = null;
        CheckpointMetadata? backup = null;
        Pin? pin = null;
        bool wroteTarget = false;
        bool loadedTarget = false;
        try
        {
            var saves = SaveManager.Instance;
            var game = NGame.Instance ?? throw new InvalidOperationException("Game scene unavailable");
            pin = new Pin();
            var repository = RewindMod.Repository;
            if (!RewindMod.Settings.Enabled) throw new InvalidOperationException("disabled");
            var current = RewindMod.CurrentSave();
            Validate(checkpoint, current);
            var targetJson = repository.ReadCheckpoint(checkpoint);
            var read = JsonSerializationUtility.FromJson<SerializableRun>(targetJson);
            var target = read.Success && read.SaveData != null ? read.SaveData
                : throw new InvalidDataException("invalid_checkpoint_payload");
            ValidatePayload(checkpoint, current, target);
            // Resolve models and reject broken saves before leaving the current scene.
            var state = RunState.FromSerializable(target);

            if (RunManager.Instance.IsInProgress)
            {
                NRunMusicController.Instance?.StopMusic();
                try { await game.ReturnToMainMenu(); }
                catch (Exception ex)
                {
                    throw new IOException(RestoreFault.Raise("restore_original_unchanged_restart_required", null, ex.Message), ex);
                }
            }
            if (saves.CurrentRunSaveTask != null) await saves.CurrentRunSaveTask;
            pin.Check();
            current = RewindMod.CurrentSave();
            Validate(checkpoint, current);
            ValidatePayload(checkpoint, current, target);
            originalJson = await pin.Store.ReadFileAsync(pin.Path)
                ?? throw new IOException("current_save_missing");
            if (Encoding.UTF8.GetByteCount(originalJson) > CheckpointRepository.MaxRunBytes)
                throw new IOException("current_save_too_large");
            var originalRead = JsonSerializationUtility.FromJson<SerializableRun>(originalJson);
            if (!originalRead.Success || originalRead.SaveData == null
                || JsonSerializationUtility.ToJson(originalRead.SaveData) != JsonSerializationUtility.ToJson(current))
                throw new IOException("current_save_changed");
            backup = repository.SaveCheckpoint(RewindMod.Metadata(current, "before_restore"), originalJson);
            if (pin.Store is CloudSaveStore beforeCloud)
            {
                // Refuse to overwrite a different remote version, including a newer cloud save.
                if (await beforeCloud.CloudStore.ReadFileAsync(pin.Path) != originalJson
                    || !beforeCloud.CloudStore.IsFilePersisted(pin.Path))
                    throw new IOException("cloud_not_in_sync");
            }

            wroteTarget = true; // A partial local write also requires rollback.
            await WriteVerified(pin, target);
            // Native continuation increments NumReloads and saves again (to the current profile, hence the check).
            pin.Check();
            await RunManager.Instance.SetUpSavedSingleplayer(state, target);
            await VerifyAndPublish(pin, JsonSerializationUtility.ToJson(target));
            await game.Transition.FadeOut(0.8f, state.Players[0].Character.CharacterSelectTransitionPath);
            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await game.LoadRun(state, target.PreFinishedRoom);
            await game.Transition.FadeIn();
            if (saves.CurrentRunSaveTask != null) await saves.CurrentRunSaveTask;
            pin.Check();
            await VerifyAndPublish(pin, JsonSerializationUtility.ToJson(RewindMod.CurrentSave()));
            loadedTarget = true;
            // The marker lives in the mod sidecar; native statistics are unchanged.
            try { repository.RecordRestore(checkpoint.RunId, checkpoint.CheckpointId, mark); }
            catch (Exception ex) { throw new IOException("restored_marker_failed; " + ex.Message, ex); }
        }
        catch (Exception failure)
        {
            RewindMod.Error(failure);
            if (wroteTarget && !loadedTarget && pin != null && originalJson != null)
            {
                string original = originalJson; var target = pin;
                Exception? cleanupFailure;
                try
                {
                    cleanupFailure = await RestoreRecovery.RunAsync(async () =>
                    {
                        var game = NGame.Instance ?? throw new InvalidOperationException("Game scene unavailable");
                        if (RunManager.Instance.IsInProgress) await game.ReturnToMainMenu();
                    }, async () =>
                    {
                        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
                        {
                            try { await pending; } catch (Exception ex) { RewindMod.Error(ex); }
                        }
                        // The original bytes go back to the pinned profile and store, whatever the current profile is.
                        await VerifyAndPublish(target, original, () => target.Store.WriteFileAsync(target.Path, Encoding.UTF8.GetBytes(original)));
                    });
                }
                catch (Exception rollbackFailure)
                {
                    // Neither the target nor the original save is verified: latch the fault before anything else so
                    // that no further rewind can run until the game is restarted, and keep the backup id visible.
                    RewindMod.Error(rollbackFailure);
                    throw new IOException(RestoreFault.Raise("restore_and_rollback_failed", backup?.CheckpointId, failure.Message, rollbackFailure.Message), failure);
                }
                NGame.Instance?.MainMenu?.RefreshButtons();
                if (cleanupFailure != null)
                {
                    RewindMod.Error(cleanupFailure);
                    throw new IOException(RestoreFault.Raise("restore_failed_original_recovered_restart_required", backup?.CheckpointId, failure.Message), failure);
                }
                throw new IOException(RestoreFault.Describe("restore_failed_original_recovered", backup?.CheckpointId, failure.Message), failure);
            }
            throw;
        }
        finally { RewindMod.Busy = false; }
    }

    private static void Validate(CheckpointMetadata checkpoint, SerializableRun current)
    {
        string? reason = RestoreValidation.Validate(checkpoint, RewindMod.RunId(current), SaveManager.Instance.CurrentProfileId,
            RewindMod.GameVersion, SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>(), RewindMod.ModIds,
            CanRestore(current), current.DailyTime.HasValue || current.GameMode == GameMode.Daily);
        if (reason != null) throw new InvalidDataException(reason);
    }

    private static void ValidatePayload(CheckpointMetadata metadata, SerializableRun current, SerializableRun target)
    {
        if (!RewindMod.Supported(target) || RewindMod.RunId(target) != metadata.RunId
            || target.SchemaVersion != metadata.RunSchemaVersion || target.Ascension != current.Ascension
            || target.Ascension != metadata.Ascension || target.GameMode != current.GameMode
            || target.PlatformType != current.PlatformType || target.Players[0].NetId != current.Players[0].NetId
            || target.SerializableRng.Seed != metadata.Seed || target.StartTime != metadata.StartTime
            || target.Players[0].CharacterId?.ToString() != metadata.Character)
            throw new InvalidDataException("payload_identity_mismatch");
    }

    // The native SaveRun writes to the current profile's path, so the pin is checked right before it.
    private static Task WriteVerified(Pin pin, SerializableRun run) => VerifyAndPublish(
        pin, JsonSerializationUtility.ToJson(run), () => { pin.Check(); return pin.RunSaves.SaveRun(run, false); });

    private static Task VerifyAndPublish(Pin pin, string expected, Func<Task>? save = null)
    {
        var store = pin.Store; string path = pin.Path;
        if (store is CloudSaveStore cloud)
            return VerifiedSaveWriter.WriteAsync(expected, save ?? (() => Task.CompletedTask),
                () => store.ReadFileAsync(path),
                content => cloud.CloudStore.WriteFileAsync(path, Encoding.UTF8.GetBytes(content)),
                () => cloud.CloudStore.ReadFileAsync(path), () => cloud.CloudStore.IsFilePersisted(path));
        return VerifiedSaveWriter.WriteAsync(expected, save ?? (() => Task.CompletedTask), () => store.ReadFileAsync(path));
    }
}
