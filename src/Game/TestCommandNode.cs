using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireRewind.Core;
using System.Text.Json;

namespace SpireRewind;

// Test-only polling node; all game state is inspected on Godot's main thread.
internal sealed class TestCommandNode
{
    private static NPauseMenu? pauseMenu;
    private ulong nextPoll;
    private bool executing;
    public static void Install(NGame game)
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_REWIND_TEST_MODE") != "1") return;
        if (game.GetNodeOrNull<Node>("SpireRewindTestControl") != null) return;
        var lifetime = new Node { Name = "SpireRewindTestControl", ProcessMode = Node.ProcessModeEnum.Always };
        game.AddChild(lifetime);
        var controller = new TestCommandNode();
        var tree = game.GetTree();
        // The mod uses Microsoft.NET.Sdk and does not run Godot's script source
        // generator. Use a managed signal delegate instead of a custom _Process.
        tree.ProcessFrame += controller.Poll;
        lifetime.TreeExiting += () => tree.ProcessFrame -= controller.Poll;
    }
    [HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
    private static class InstallPatch { private static void Postfix(NGame __instance) => Install(__instance); }
    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))]
    private static class OpenPatch { private static void Postfix(NPauseMenu __instance) => pauseMenu = __instance; }
    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuClosed))]
    private static class ClosePatch { private static void Postfix(NPauseMenu __instance) { if (pauseMenu == __instance) pauseMenu = null; } }

    private void Poll()
    {
        if (executing) return;
        ulong now = Time.GetTicksMsec();
        if (now < nextPoll) return;
        nextPoll = now + 250;
        try
        {
            string? configured = System.Environment.GetEnvironmentVariable("SPIRE_REWIND_TEST_ROOT");
            string root = configured == null ? ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind"))
                : TestCommandChannel.ResolveExternalRoot(configured, ProjectSettings.GlobalizePath("user://"));
            var channel = new TestCommandChannel(root);
            var command = channel.Claim(DateTimeOffset.UtcNow);
            if (command != null) _ = Handle(channel, command);
        }
        catch (Exception ex) { RewindMod.Error(ex); }
    }
    private static string? UnsafeReason()
    {
        if (RewindMod.Busy) return "busy";
        if (RewindMod.RequiresRestart) return "restart_required";
        if (!RewindMod.Settings.Enabled) return "disabled";
        if (!SaveManager.Instance.HasRunSave) return "no_run_save";
        var current = RewindMod.CurrentSave();
        if (!RewindMod.Supported(current)) return "unsupported_run";
        if (!RestoreService.CanRestore(current)) return "unsupported_state";
        var manager = RunManager.Instance;
        if (!manager.IsInProgress)
            return NGame.Instance?.MainMenu?.IsVisibleInTree() == true ? null : "not_main_menu";
        return TestCommandChannel.PausedStateError(PauseVisible(), CombatManager.Instance.IsInProgress,
            CombatManager.Instance.IsPaused, StableReason() == null);
    }
    private static bool PauseVisible() => pauseMenu != null && GodotObject.IsInstanceValid(pauseMenu)
        && pauseMenu.IsVisibleInTree() && NRun.Instance?.GlobalUi.SubmenuStack.Stack.Peek() == pauseMenu
        && NCapstoneContainer.Instance?.CurrentCapstoneScreen == NRun.Instance.GlobalUi.SubmenuStack;

    private static string? StableReason()
    {
        var manager = RunManager.Instance;
        var combat = CombatManager.Instance;
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) return "not_stable";
        if (combat.IsStarting || combat.IsEnding || combat.IsAboutToLose) return "not_stable";
        if (manager.ActionExecutor == null || manager.ActionQueueSet == null || manager.ActionExecutor.IsRunning
            || manager.ActionExecutor.CurrentlyRunningAction != null || !manager.ActionQueueSet.IsEmpty) return "not_stable";
        if (combat.IsInProgress && (combat.DebugOnlyGetState()?.CurrentSide != CombatSide.Player
            || combat.PlayerActionsDisabled || combat.DebugOnlyGetState()?.Players.All(p => p.PlayerCombatState?.Phase != PlayerTurnPhase.Play) != false)) return "not_stable";
        return null;
    }
    private static object Summary(SerializableRun run)
    {
        var player = run.Players[0];
        using var json = JsonDocument.Parse(JsonSerializationUtility.ToJson(run));
        var deck = json.RootElement.GetProperty("players")[0].GetProperty("deck").Clone();
        return new { hp = player.CurrentHp, max_hp = player.MaxHp, gold = player.Gold,
            deck_count = player.Deck.Count, deck_ids = player.Deck.Select(c => c.Id?.ToString()).ToArray(),
            deck, character_id = player.CharacterId?.ToString() };
    }
    private static object State()
    {
        var save = RewindMod.CurrentSave();
        object? live = RunManager.Instance.IsInProgress ? Summary(RunManager.Instance.ToSave(null)) : null;
        // Describe the stage the way its checkpoint would (a pending room counts as entering the next floor),
        // so a room-entry checkpoint and the save it restored report the same floor and room type.
        var metadata = RewindMod.Metadata(save, "native_save") with { CaptureKind = "current_save" };
        return new { save = metadata, save_summary = Summary(save), live };
    }
    private static string? PauseReason()
    {
        if (RewindMod.Busy) return "busy";
        if (RewindMod.RequiresRestart) return "restart_required";
        var manager = RunManager.Instance;
        if (!manager.IsInProgress || !SaveManager.Instance.HasRunSave) return "no_active_run";
        var save = RewindMod.CurrentSave();
        if (!RewindMod.Settings.Enabled || !RewindMod.Supported(save) || !RestoreService.CanRestore(save)) return "unsupported_state";
        if (PauseVisible()) return UnsafeReason();
        if (NCapstoneContainer.Instance?.CurrentCapstoneScreen != null) return "screen_open";
        if (CombatManager.Instance.IsPaused) return "not_stable";
        return StableReason();
    }
    private async Task Handle(TestCommandChannel channel, TestCommand cmd)
    {
        if (executing) { channel.WriteResult(new(cmd.RequestId, "rejected", "busy", null)); return; }
        executing = true;
        try
        {
            if (cmd.Command?.StartsWith("replay-", StringComparison.Ordinal) == true)
            {
                HandleReplay(channel, cmd); return;
            }
            if (ReplayTestAudit.Active || ReplayPlaybackService.Active)
            { channel.WriteResult(new(cmd.RequestId, "rejected", "replay_session_active", null)); return; }
            if (cmd.Command == "export")
            {
                // Same exporter and output location as the in-game button; reads mod files only.
                if (RewindMod.Busy) { channel.WriteResult(new(cmd.RequestId, "rejected", "busy", null)); return; }
                if (!SaveManager.Instance.HasRunSave) { channel.WriteResult(new(cmd.RequestId, "rejected", "no_run_save", null)); return; }
                string exportRun = RewindMod.RunId(RewindMod.CurrentSave());
                string html = ReplayHtmlExporter.Export(ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind")), exportRun, Text.CurrentLanguage);
                channel.WriteResult(new(cmd.RequestId, "completed", null, new { run_id = exportRun, path = html, language = Text.CurrentLanguage })); return;
            }
            if (cmd.Command is "ui-open" or "ui-confirm" or "ui-close" or "ui-accept")
            {
                string? uiError = HandleUi(cmd);
                channel.WriteResult(new(cmd.RequestId, uiError == null ? "completed" : "rejected", uiError, new { dialog_open = RewindUi.DialogOpen })); return;
            }
            if (cmd.Command == "fault-inject")
            {
                // Test-only: latches RestoreFault as a failed rollback would, so the Continue gate can be checked on
                // the isolated copy. Process-wide and irreversible like the real fault: the game must be restarted.
                if (RewindMod.Busy) { channel.WriteResult(new(cmd.RequestId, "rejected", "busy", null)); return; }
                if (RunManager.Instance.IsInProgress || NGame.Instance?.MainMenu?.IsVisibleInTree() != true)
                { channel.WriteResult(new(cmd.RequestId, "rejected", "not_main_menu", null)); return; }
                string injected = RestoreFault.Raise("test_injected_fault", "test-backup-id", "injected by the test channel");
                NGame.Instance.MainMenu!.RefreshButtons(); // Runs the Continue gate the way a return to the menu does.
                channel.WriteResult(new(cmd.RequestId, "completed", null, new { requires_restart = RewindMod.RequiresRestart,
                    notice = RestoreFault.Notice, injected, continue_refusal = NativeContinueGate.Refusal() })); return;
            }
            if (cmd.Command == "save-and-quit")
            {
                // Same path as the pause menu's Save and Quit button (NPauseMenu.CloseToMenu); needs the visible native pause.
                string? quitReason = UnsafeReason();
                if (quitReason != null || !RunManager.Instance.IsInProgress) { channel.WriteResult(new(cmd.RequestId, "rejected", quitReason ?? "no_active_run", null)); return; }
                var game = NGame.Instance ?? throw new InvalidOperationException("game_scene_unavailable");
                NRunMusicController.Instance?.StopMusic();
                await game.ReturnToMainMenu();
                bool atMenu = !RunManager.Instance.IsInProgress && game.MainMenu?.IsVisibleInTree() == true;
                channel.WriteResult(new(cmd.RequestId, atMenu ? "completed" : "failed", atMenu ? null : "not_main_menu", new { main_menu = atMenu, has_run_save = SaveManager.Instance.HasRunSave })); return;
            }
            if (cmd.Command == "pause")
            {
                string? pauseReason = PauseReason();
                if (pauseReason != null) { channel.WriteResult(new(cmd.RequestId, "rejected", pauseReason, null)); return; }
                if (UnsafeReason() != null)
                {
                    var state = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no_active_run");
                    var gameRun = NRun.Instance ?? throw new InvalidOperationException("run_scene_unavailable");
                    var menu = (NPauseMenu)gameRun.GlobalUi.SubmenuStack.ShowScreen(CapstoneSubmenuType.PauseMenu);
                    menu.Initialize(state);
                }
                string? afterPause = UnsafeReason();
                channel.WriteResult(new(cmd.RequestId, afterPause == null ? "completed" : "failed", afterPause, new { safe_to_execute = afterPause == null })); return;
            }
            string? reason = UnsafeReason();
            if (cmd.Command == "status")
            {
                object? state = null;
                try { if (SaveManager.Instance.HasRunSave && RewindMod.Supported(RewindMod.CurrentSave())) state = State(); } catch { }
                channel.WriteResult(new(cmd.RequestId, "completed", null, new { safe_to_execute = reason == null, reason, state })); return;
            }
            if (reason != null) { channel.WriteResult(new(cmd.RequestId, "rejected", reason, null)); return; }
            var run = RewindMod.CurrentSave(); string runId = RewindMod.RunId(run);
            var repo = RewindMod.Repository; var checkpoints = repo.ListCheckpoints(runId);
            if (cmd.Command == "list")
            {
                channel.WriteResult(new(cmd.RequestId, "completed", null, new { run_id = runId, snapshot_id = TestCommandChannel.SnapshotId(runId, checkpoints), checkpoints })); return;
            }
            var selected = TestCommandChannel.Select(cmd, runId, checkpoints);
            if (selected == null) { channel.WriteResult(new(cmd.RequestId, "rejected", "selection_changed_or_missing", null)); return; }
            object before = State();
            await RestoreService.Restore(selected, RewindMod.Settings.MarkRewoundRuns);
            object after = State();
            var marker = new { marked = repo.IsMarked(runId), restore_count = repo.GetRewindCount(runId) };
            channel.WriteResult(new(cmd.RequestId, "completed", null, new { checkpoint = selected, before, after, marker }));
        }
        catch (Exception ex) { RewindMod.Error(ex); channel.WriteResult(new(cmd.RequestId, "failed", ex.Message, null)); }
        finally { executing = false; }
    }

    // Opens the same dialog as the pause-menu / main-menu buttons so its layout can be captured.
    private static string? HandleUi(TestCommand cmd)
    {
        if (RewindMod.Busy) return "busy";
        if (cmd.Command == "ui-close") { RewindUi.CloseDialog(); return null; }
        // Presses the confirm button of an open confirmation view; the restore then runs in the button handler.
        if (cmd.Command == "ui-accept") return RewindUi.AcceptDialog() ? null : "confirm_not_open";
        if (cmd.Command == "ui-open")
        {
            bool menu = !RunManager.Instance.IsInProgress ? NGame.Instance?.MainMenu?.IsVisibleInTree() == true : PauseVisible();
            if (!menu) return "not_paused";
            return RewindUi.OpenDialog() ? null : "dialog_already_open";
        }
        if (!RewindUi.DialogOpen) return "dialog_not_open";
        if (!SaveManager.Instance.HasRunSave) return "no_run_save";
        var run = RewindMod.CurrentSave(); string runId = RewindMod.RunId(run);
        var selected = TestCommandChannel.Select(cmd, runId, RewindMod.Repository.ListCheckpoints(runId));
        if (selected == null) return "selection_changed_or_missing";
        return RewindUi.OpenConfirm(selected) ? null : "dialog_not_open";
    }

    private static void HandleReplay(TestCommandChannel channel, TestCommand cmd)
    {
        try
        {
            string? configured = System.Environment.GetEnvironmentVariable("SPIRE_REWIND_TEST_ROOT");
            if (configured == null) throw new InvalidOperationException("replay_external_control_required");
            _ = TestCommandChannel.ResolveExternalRoot(configured, ProjectSettings.GlobalizePath("user://"));
            // Exit/end remain usable if a fault has invalidated the menu guard.
            if (cmd.Command is not ("replay-exit" or "replay-audit-end" or "replay-status")) ReplayPlaybackService.RequireMenu();
            object? data;
            switch (cmd.Command)
            {
                case "replay-audit-begin": data = ReplayTestAudit.Begin(); break;
                case "replay-audit-end": data = ReplayTestAudit.End(); break;
                case "replay-list": data = new { entries = ReplayArchiveService.List(), kind = "decision_snapshots", native_playback = false }; break;
                case "replay-inspect":
                    var entry = new ReplayArchiveEntry(cmd.RunId!, cmd.ReplayId!, null, null);
                    data = new { inspection = ReplayArchiveService.Inspect(entry), snapshots = ReplayPlaybackService.Inspect(entry) }; break;
                case "replay-play": ReplayPlaybackService.Start(new(cmd.RunId!, cmd.ReplayId!, null, null)); data = ReplayPlaybackService.Status(); break;
                case "replay-status": data = ReplayPlaybackService.Status(); break;
                case "replay-exit": ReplayPlaybackService.Stop(); data = ReplayPlaybackService.Status(); break;
                default: ReplayPlaybackService.Control(cmd.Command!, cmd.Speed); data = ReplayPlaybackService.Status(); break;
            }
            channel.WriteResult(new(cmd.RequestId, "completed", null, data));
        }
        catch (Exception ex)
        {
            ReplayPlaybackService.Stop(ex.Message);
            channel.WriteResult(new(cmd.RequestId, "rejected", ex.Message, ReplayPlaybackService.Status()));
        }
    }
}

