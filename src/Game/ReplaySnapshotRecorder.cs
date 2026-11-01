using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireRewind.Core;

namespace SpireRewind;

// Observe normal gameplay only. This recorder never drives an action or saves a run.
internal static class ReplaySnapshotRecorder
{
    private static CombatReplayWriter? writer;
    private static string? runId;
    private static int profileId;
    private static ReplayRuntimeIdentity? identity;
    private static readonly List<ReplaySnapshotFrame> frames = [];
    private static string? candidate, recorded;
    private static bool overflow;
    private static int recordedPlays;
    internal static void Clear()
    {
        writer = null; runId = null; identity = null; frames.Clear();
        candidate = recorded = null; overflow = false; recordedPlays = 0;
    }

    [HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState))]
    private static class BeginPatch
    {
        private static void Postfix(CombatReplayWriter __instance, SerializableRun serializableRun)
        {
            Clear();
            try
            {
                // A rewind into a room-entry checkpoint starts its combat while the restore is still Busy;
                // attach anyway so that fight gets frames (Observe skips frames until Busy clears).
                if (!__instance.IsEnabled || !__instance.IsRecordingReplay
                    || !RewindMod.Supported(serializableRun) || !RewindMod.Settings.Enabled
                    || RunManager.Instance.NetService is not { Type: NetGameType.Singleplayer }) return;
                profileId = SaveManager.Instance.CurrentProfileId;
                runId = RewindMod.RunId(serializableRun);
                identity = ReplayArchiveService.CurrentIdentity;
                writer = __instance;
            }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
    }

    [HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.StopRecording))]
    private static class StopPatch
    {
        private static void Postfix(CombatReplayWriter __instance)
        {
            if (writer == __instance) Clear(); // WriteReplay Prefix already froze detached frames.
        }
    }

    internal static void Observe()
    {
        if (writer == null || overflow || ReplayPlaybackService.Active) return;
        try
        {
            var manager = RunManager.Instance;
            var combat = CombatManager.Instance;
            var executor = manager.ActionExecutor;
            var queues = manager.ActionQueueSet;
            var state = combat.DebugOnlyGetState();
            // Do not call IsEnding: that getter invokes game/mod hooks.
            if (RewindMod.Busy || profileId != SaveManager.Instance.CurrentProfileId || !manager.IsInProgress
                || manager.NetService is not { Type: NetGameType.Singleplayer } || manager.CombatReplayWriter != writer
                || !writer.IsRecordingReplay || !combat.IsInProgress || combat.IsStarting || combat.IsAboutToLose
                || combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo || combat.PlayerActionsDisabled
                || state == null || state.CurrentSide != CombatSide.Player || executor == null || queues == null
                || executor.IsPaused || executor.IsRunning || executor.CurrentlyRunningAction != null || !queues.IsEmpty
                || state.Players.Count != 1 || state.Players[0].PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            { candidate = null; return; }
            if (state.Enemies.Count > ReplaySnapshotsRepository.MaximumEnemies)
            { overflow = true; frames.Clear(); return; }
            var frame = new ReplaySnapshotFrame
            {
                Turn = state.Players[0].PlayerCombatState!.TurnNumber,
                Players = state.PlayerCreatures.Select(Actor).ToImmutableArray(),
                Enemies = state.Enemies.Select(Actor).ToImmutableArray(),
                Cards = state.Players[0].PlayerCombatState!.Hand.Cards.Select(c => c.Id.ToString()).ToImmutableArray()
            };
            // Action ID distinguishes a decision that leaves turn/HP unchanged.
            string key = queues.NextActionId + ":" + JsonSerializer.Serialize(frame);
            if (key == candidate && key != recorded)
            {
                if (frames.Count >= ReplaySnapshotsRepository.MaximumFrames) { overflow = true; frames.Clear(); return; }
                var plays = combat.History.CardPlaysFinished.ToArray();
                if (plays.Length < recordedPlays) { overflow = true; frames.Clear(); return; }
                frame = frame with { Actions = plays.Skip(recordedPlays).Select(p => p.CardPlay.Card.Id.ToString()).ToImmutableArray() };
                frames.Add(frame); recorded = key; recordedPlays = plays.Length;
            }
            candidate = key;
        }
        catch (Exception ex) { Clear(); RewindMod.Error(ex); }
    }

    private static ReplaySnapshotActor Actor(Creature creature) => new()
    {
        Label = $"{creature.ModelId} #{creature.CombatId}", Hp = creature.CurrentHp, MaxHp = creature.MaxHp,
        Block = creature.Block, Intent = creature.Monster?.NextMove.Intents.Select(i => i.GetType().Name).ToImmutableArray()
    };

    // Copy before native WriteReplay can stop recording or clear combat state.
    internal static ReplaySnapshotsDocument? Freeze(string expectedRunId)
    {
        if (writer == null || identity == null || overflow || frames.Count == 0 || runId != expectedRunId
            || profileId != SaveManager.Instance.CurrentProfileId || RunManager.Instance.CombatReplayWriter != writer
            || !writer.IsRecordingReplay) return null;
        return new ReplaySnapshotsDocument
        {
            FormatVersion = 2, RunId = runId, GameVersion = identity.GameVersion, GameCommit = identity.GameCommit,
            ModelIdHash = identity.ModelIdHash, ModIds = identity.ModIds.ToImmutableArray(), Frames = frames.ToImmutableArray()
        };
    }
}

// Use managed delegates; the mod does not run Godot's script source generator.
internal static class ReplayFrameDriver
{
    internal static void Install(NGame game)
    {
        if (game.GetNodeOrNull<Node>("SpireRewindReplayFrames") != null) return;
        var lifetime = new Node { Name = "SpireRewindReplayFrames", ProcessMode = Node.ProcessModeEnum.Always };
        game.AddChild(lifetime);
        var tree = game.GetTree();
        ulong previous = Time.GetTicksMsec();
        void Tick()
        {
            ulong now = Time.GetTicksMsec();
            double elapsed = (now - previous) / 1000.0; previous = now;
            ReplaySnapshotRecorder.Observe(); RunChoiceRecorder.Observe(); ReplayPlaybackService.Tick(elapsed);
        }
        tree.ProcessFrame += Tick;
        lifetime.TreeExiting += () => { tree.ProcessFrame -= Tick; ReplaySnapshotRecorder.Clear(); RunChoiceRecorder.Clear(); ReplayPlaybackService.Stop(); };
    }
    [HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
    private static class InstallPatch { private static void Postfix(NGame __instance) => Install(__instance); }
}
