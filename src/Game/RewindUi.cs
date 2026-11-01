using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.addons.mega_text;
using SpireRewind.Core;

namespace SpireRewind;

internal static class RewindUi
{
    private const string EntryName = "SpireRewindEntry";
    private const string StatusName = "SpireRewindStatus";
    private static CanvasLayer? openLayer;
    private static VBoxContainer? openContent;
    private static Action? openClose;

    // Test-mode hooks (TestCommandNode): same code paths as the buttons.
    internal static bool DialogOpen => openLayer != null && GodotObject.IsInstanceValid(openLayer);
    internal static bool OpenDialog() { if (DialogOpen) return false; Show(); return DialogOpen; }
    internal static void CloseDialog()
    {
        if (!DialogOpen || RewindMod.Busy) return;
        openClose?.Invoke(); // QueueFree is deferred; TreeExiting resets the statics again later.
        openLayer = null; openContent = null; openClose = null;
    }
    internal static bool OpenConfirm(CheckpointMetadata checkpoint)
    {
        if (!DialogOpen || openContent == null || !GodotObject.IsInstanceValid(openContent) || openClose == null) return false;
        Confirm(openContent, openLayer!, checkpoint, openClose); return true;
    }
    // Presses the confirm button of the open confirmation view (the same handler as a click).
    internal static bool AcceptDialog()
    {
        if (!DialogOpen || RewindMod.Busy || openPrimary == null || !GodotObject.IsInstanceValid(openPrimary) || !openPrimary.IsInsideTree()) return false;
        openPrimary.EmitSignal(BaseButton.SignalName.Pressed); return true;
    }
    private static Button? openPrimary;
    private static Control? openPanel;

    // The list views use the full panel; the confirmation and its results use a shorter one.
    private static void Resize(float height)
    {
        if (openPanel != null && GodotObject.IsInstanceValid(openPanel)) Style.Resize(openPanel, height);
    }

    // The game's Continue button on the main menu: RefreshButtons enables it whenever a run save exists; after a
    // latched fault it is switched off again here (RefreshButtons runs on every return to the menu). The game's
    // default focus skips disabled buttons, so keyboard and controller cannot reach it either.
    internal static void ApplyContinueGate(NMainMenu menu)
    {
        try
        {
            if (AccessTools.Field(typeof(NMainMenu), "_continueButton")?.GetValue(menu) is not NClickableControl button) return;
            bool enabled = NativeContinueGate.ButtonEnabled(button.IsEnabled);
            if (button.IsEnabled != enabled) button.SetEnabled(enabled);
        }
        catch (Exception ex) { RewindMod.Error(ex); }
    }

    // A Continue press that still got through (the handler is patched, not only the button): disable the button and
    // open the dialog, whose header carries the fault, its backup id and the restart instruction.
    internal static void RefuseContinue(NMainMenu menu)
    {
        ApplyContinueGate(menu);
        if (!RewindMod.Busy && !DialogOpen) Show();
    }

    // The profile screen shows a loading overlay before the native switch calls SaveManager.SwitchProfileId; when the
    // mod refuses the switch there, the overlay would stay up with no way back. It is a private field of the profile
    // screen, found among the main menu's descendants (never instantiated here).
    internal static void HideProfileLoading()
    {
        var menu = NGame.Instance?.MainMenu;
        if (menu == null || !GodotObject.IsInstanceValid(menu)) return;
        var field = AccessTools.Field(typeof(NProfileScreen), "_loadingOverlay");
        foreach (var screen in Descendants(menu).OfType<NProfileScreen>())
            if (field?.GetValue(screen) is Control overlay && GodotObject.IsInstanceValid(overlay)) overlay.Visible = false;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }

    // Entry points: one button in the native button column of the pause menu and of the main menu, built from the
    // game's own button scene / node so it is styled like its neighbours. If the native column cannot be found
    // (a game update renamed it) a restyled button is placed in the top-right corner instead.
    public static void Install(Control parent, bool inRun)
    {
        try
        {
            string text = Text.Get(inRun || SaveManager.Instance.HasRunSave ? "rewind" : "settings");
            Control? entry = inRun ? InstallPauseEntry(parent, text) : InstallMainMenuEntry(parent, text);
            entry ??= InstallCornerEntry(parent, text);
            if (entry is NClickableControl native) native.SetEnabled(!RewindMod.Busy);
            else if (entry is Button button) Style.Enable(button, !RewindMod.Busy);
        }
        catch (Exception ex) { RewindMod.Error(ex); }
    }

    private static Control? InstallPauseEntry(Control menu, string text)
    {
        var container = menu.GetNodeOrNull<Control>("%ButtonContainer") ?? menu.FindChild("ButtonContainer", true, false) as Control;
        if (container == null) return null;
        var entry = container.GetNodeOrNull<Control>(EntryName);
        if (entry == null)
        {
            NPauseMenuButton? native = null;
            try { native = Style.Load<PackedScene>("res://scenes/pause_menu/pause_menu_button.tscn")?.Instantiate<NPauseMenuButton>(PackedScene.GenEditState.Disabled); }
            catch (Exception ex) { RewindMod.Error(ex); }
            if (native != null)
            {
                native.Name = EntryName;
                native.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                native.FocusMode = Control.FocusModeEnum.All; // read by the game's button in _Ready
                container.AddChild(native);
                native.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => Show()));
                entry = native;
            }
            else
            {
                var button = Style.Button(container, text, Show, minWidth: 372, height: 80, fontSize: 30, expand: false);
                button.Name = EntryName;
                button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                entry = button;
            }
        }
        if (entry is NPauseMenuButton pauseButton)
        {
            var label = pauseButton.GetNodeOrNull<MegaLabel>("Label");
            if (label != null) { Style.ApplyFont(label, true); label.SetTextAutoSize(text); }
        }
        else if (entry is Button plain) Style.SetText(plain, text);
        // The native buttons form a closed vertical focus loop; splice the entry in after the last one.
        var siblings = container.GetChildren().OfType<Control>().Where(c => c != entry && c.Visible && c.FocusMode != Control.FocusModeEnum.None).ToList();
        if (siblings.Count > 0)
        {
            Control first = siblings[0], last = siblings[^1];
            last.FocusNeighborBottom = entry.GetPath();
            first.FocusNeighborTop = entry.GetPath();
            entry.FocusNeighborTop = last.GetPath();
            entry.FocusNeighborBottom = first.GetPath();
            entry.FocusNeighborLeft = entry.GetPath();
            entry.FocusNeighborRight = entry.GetPath();
        }
        return entry;
    }

    private static Control? InstallMainMenuEntry(Control menu, string text)
    {
        var column = menu.GetNodeOrNull<Control>("%MainMenuTextButtons") ?? menu.FindChild("MainMenuTextButtons", true, false) as Control;
        if (column == null) return null;
        var entry = column.GetNodeOrNull<Control>(EntryName);
        if (entry == null)
        {
            var template = column.GetNodeOrNull<NMainMenuTextButton>("QuitButton") ?? column.GetChildren().OfType<NMainMenuTextButton>().LastOrDefault();
            var after = column.GetNodeOrNull<Control>("SettingsButton");
            NMainMenuTextButton? copy = null;
            try { copy = template?.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts)) as NMainMenuTextButton; }
            catch (Exception ex) { RewindMod.Error(ex); }
            if (copy != null)
            {
                copy.Name = EntryName;
                copy.Visible = true;
                column.AddChild(copy);
                if (after != null) column.MoveChild(copy, after.GetIndex() + 1);
                copy.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => Show()));
                // The gold reticle around the focused entry is drawn by the menu's private handlers.
                var focused = AccessTools.Method(menu.GetType(), "MainMenuButtonFocused");
                var unfocused = AccessTools.Method(menu.GetType(), "MainMenuButtonUnfocused");
                if (focused != null)
                    copy.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ => Callable.From(() =>
                    {
                        try { if (GodotObject.IsInstanceValid(menu) && GodotObject.IsInstanceValid(copy)) focused.Invoke(menu, [copy]); }
                        catch (Exception ex) { RewindMod.Error(ex); }
                    }).CallDeferred()));
                if (unfocused != null)
                    copy.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ =>
                    {
                        try { if (GodotObject.IsInstanceValid(menu) && GodotObject.IsInstanceValid(copy)) unfocused.Invoke(menu, [copy]); }
                        catch (Exception ex) { RewindMod.Error(ex); }
                    }));
                var label = copy.GetChildOrNull<MegaLabel>(0);
                if (label != null) label.Resized += () => label.PivotOffset = label.Size / 2; // hover scale around the centre
                entry = copy;
            }
            else
            {
                var button = Style.Button(column, text, Show, minWidth: 200, height: 50, fontSize: 28, expand: false);
                button.Name = EntryName;
                button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                if (after != null) column.MoveChild(button, after.GetIndex() + 1);
                entry = button;
            }
        }
        if (entry is NMainMenuTextButton textButton)
        {
            var label = textButton.GetChildOrNull<MegaLabel>(0);
            if (label != null) { Style.ApplyFont(label, false); label.Text = text; }
        }
        else if (entry is Button plain) Style.SetText(plain, text);
        return entry;
    }

    private static Control InstallCornerEntry(Control parent, string text)
    {
        var entry = parent.GetNodeOrNull<Button>(EntryName);
        if (entry == null)
        {
            entry = Style.Button(parent, text, Show, minWidth: 240, height: 56, fontSize: 26, expand: false);
            entry.Name = EntryName;
            entry.ZIndex = 20;
            entry.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            entry.OffsetLeft = -270; entry.OffsetRight = -30;
            entry.OffsetTop = 24; entry.OffsetBottom = 80;
        }
        Style.SetText(entry, text);
        return entry;
    }

    private static void Show()
    {
        if (RewindMod.Busy || openLayer != null && GodotObject.IsInstanceValid(openLayer)) return;
        var tree = (SceneTree)Engine.GetMainLoop();
        var previousFocus = tree.Root.GuiGetFocusOwner();
        var layer = new CanvasLayer { Layer = 120, Name = "SpireRewindDialog" };
        openLayer = layer;
        var screen = new Control { MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        layer.AddChild(screen); tree.Root.AddChild(layer);
        screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.85f), MouseFilter = Control.MouseFilterEnum.Stop };
        screen.AddChild(dim); dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var (panel, margin) = Style.Panel(screen);
        openPanel = panel;
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 10); margin.AddChild(content);
        var hotkeys = NHotkeyManager.Instance;
        hotkeys?.AddBlockingScreen(screen);
        // Scene loading can focus a native menu button while this modal persists.
        // Keep keyboard/controller selection inside the modal during the transaction.
        void ConstrainFocus(Control focused)
        {
            if (GodotObject.IsInstanceValid(screen) && focused != screen
                && (focused == null || !screen.IsAncestorOf(focused))) screen.GrabFocus();
        }
        tree.Root.GuiFocusChanged += ConstrainFocus;
        screen.GuiInput += _ => screen.AcceptEvent();
        layer.TreeExiting += () =>
        {
            ReplayPlaybackService.Stop();
            tree.Root.GuiFocusChanged -= ConstrainFocus;
            if (hotkeys != null && GodotObject.IsInstanceValid(hotkeys)) hotkeys.RemoveBlockingScreen(screen);
            openLayer = null; openContent = null; openClose = null; openPanel = null; openPrimary = null;
            if (previousFocus != null && GodotObject.IsInstanceValid(previousFocus) && previousFocus.IsVisibleInTree()) previousFocus.GrabFocus();
        };
        void Close() { if (!RewindMod.Busy) layer.QueueFree(); }
        openContent = content; openClose = Close;
        Populate(content, layer, Close);
    }

    private static void Populate(VBoxContainer content, CanvasLayer layer, Action close)
    {
        Resize(Style.PanelHeight);
        Style.Title(content, Text.Get("title"));
        Style.Label(content, Text.Get("stats"), 21, color: Style.Dim);
        // Informational only: restore behaviour on an untested game version is unchanged (RestoreService still refuses a
        // checkpoint whose game version differs from the running game).
        if (!RewindMod.IsTestedGameVersion)
            Style.Label(content, string.Format(Text.Get("untested_game_version"), RewindMod.RunningGameVersion ?? Text.Get("unknown_version"), RewindMod.TestedGameVersion), 21, color: Style.Gold);
        // A latched fault stays visible with its backup id until the game is restarted; CanRestore refuses meanwhile.
        if (RestoreFault.Notice is { } fault)
        {
            Style.Label(content, Text.Get("fault") + " · " + fault, 21, color: Style.Gold);
            // On the main menu the game's own Continue is disabled as well (RewindMod.ContinueGuard); say so.
            if (!RunManager.Instance.IsInProgress && SaveManager.Instance.HasRunSave)
                Style.Label(content, Text.Get("continue_blocked"), 21, color: Style.Gold);
        }
        var scroll = Style.Scroll(content, 160);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(rows);
        Action? save = null;
        try
        {
            var settings = RewindMod.Settings;
            if (!settings.Enabled) Label(rows, Text.Get("disabled"));
            else if (!SaveManager.Instance.HasRunSave) Label(rows, Text.Get("unsupported"));
            else
            {
                var run = RewindMod.CurrentSave();
                if (!RestoreService.CanRestore(run)) Label(rows, Text.Get("unsupported"));
                else
                {
                    string id = RewindMod.RunId(run);
                    var repository = RewindMod.Repository;
                    if (repository.IsMarked(id)) Style.Label(rows, Text.Get("marked"), 21, color: Style.Gold);
                    var checkpoints = repository.ListCheckpoints(id);
                    if (checkpoints.Count == 0) Label(rows, Text.Get("empty"));
                    foreach (var checkpoint in checkpoints)
                    {
                        var button = Style.ListRow(rows, RowTitle(checkpoint), RowDetail(checkpoint), RowTime(checkpoint), () => Confirm(content, layer, checkpoint, close));
                        button.TooltipText = Text.Get(checkpoint.CaptureKind) + " · " + checkpoint.CapturedAtUtc.ToString("O");
                    }
                }
            }
            var actions = Style.Row(content);
            if (SaveManager.Instance.HasRunSave)
            {
                string exportRunId = RewindMod.RunId(RewindMod.CurrentSave());
                Style.Button(actions, Text.Get("export_replay"), () => ExportReplay(content, exportRunId), fontSize: 22);
            }
            if (!RunManager.Instance.IsInProgress)
                Style.Button(actions, Text.Get("replay_archive"), () => { Clear(content); PopulateReplays(content, layer, close); }, fontSize: 22);
            Style.Button(actions, Text.Get("export_runs"), () => { Clear(content); PopulateExports(content, layer, close); }, fontSize: 22);
            save = AddSettings(content, layer);
        }
        catch (Exception ex)
        {
            RewindMod.Error(ex);
            Label(rows, Text.Get("error") + " · " + ex.Message);
        }
        Status(content);
        var footer = Style.Row(content);
        if (save != null) Style.Button(footer, Text.Get("save"), save, minWidth: 260, expand: false);
        Style.Spacer(footer);
        Style.Button(footer, Text.Get("close"), close, minWidth: 220, expand: false).GrabFocus();
    }

    private static string RowTitle(CheckpointMetadata c) =>
        string.Format(Text.Get("row_title"), c.Act, c.Floor, Text.Get(c.RoomType)) + " · " + Text.Get(c.CaptureKind);
    private static string RowDetail(CheckpointMetadata c) => string.Format(Text.Get("row_detail"), c.Hp, c.MaxHp, c.Gold, c.DeckCount);
    private static string RowTime(CheckpointMetadata c) => c.CapturedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    private static string Row(CheckpointMetadata c) => string.Format(Text.Get("row"), c.Act, c.Floor,
        Text.Get(c.RoomType), c.Hp, c.MaxHp, c.Gold, c.DeckCount, RowTime(c)) + " · " + Text.Get(c.CaptureKind);

    private static string ExportRoot => ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind"));

    // Hidden status line above the footer; export results and errors appear here instead of below the buttons.
    private static Label Status(VBoxContainer content)
    {
        var existing = content.GetNodeOrNull<Label>(StatusName);
        if (existing != null) return existing;
        var label = Style.Label(content, "", 20, color: Style.Gold);
        label.Name = StatusName; label.Visible = false;
        return label;
    }

    private static void SetStatus(VBoxContainer content, string text)
    {
        var status = Status(content);
        status.Text = text; status.Visible = true;
    }

    private static void ExportReplay(VBoxContainer content, string runId)
    {
        try
        {
            string path = ReplayHtmlExporter.Export(ExportRoot, runId, Text.CurrentLanguage);
            SetStatus(content, Text.Get("export_done") + "\n" + path);
        }
        catch (Exception ex) { RewindMod.Error(ex); SetStatus(content, Text.Get("error") + " · " + ex.Message); }
    }

    private static void PopulateExports(VBoxContainer content, CanvasLayer layer, Action close)
    {
        Resize(Style.PanelHeight);
        Style.Title(content, Text.Get("export_runs"));
        Style.Label(content, Text.Get("export_scope"), 21, color: Style.Dim);
        var scroll = Style.Scroll(content, 180);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(rows);
        try
        {
            var ids = ReplayHtmlExporter.ListRunIds(ExportRoot);
            if (ids.Count == 0) Label(rows, Text.Get("export_empty"));
            foreach (string id in ids)
            {
                var latest = RewindMod.Repository.ListCheckpoints(id).FirstOrDefault();
                if (latest == null) Style.ListRow(rows, id, "", null, () => ExportReplay(content, id));
                else Style.ListRow(rows, RowTitle(latest) + " · " + RowDetail(latest), id, RowTime(latest), () => ExportReplay(content, id));
            }
        }
        catch (Exception ex) { Label(rows, Text.Get("error") + " · " + ex.Message); }
        Status(content);
        var footer = Style.Row(content);
        Style.Button(footer, Text.Get("back"), () => { Clear(content); Populate(content, layer, close); }, minWidth: 220, expand: false).GrabFocus();
        Style.Spacer(footer);
        Style.Button(footer, Text.Get("close"), close, minWidth: 220, expand: false);
    }

    private static void PopulateReplays(VBoxContainer content, CanvasLayer layer, Action close)
    {
        Resize(Style.PanelHeight);
        Style.Title(content, Text.Get("replay_archive"));
        Style.Label(content, Text.Get("replay_readonly"), 21, color: Style.Dim);
        var scroll = Style.Scroll(content, 180);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(rows);
        try
        {
            var entries = ReplayArchiveService.List();
            if (entries.Count == 0) Label(rows, Text.Get("replay_empty"));
            foreach (var entry in entries)
            {
                var m = entry.Metadata;
                string title = m == null ? entry.ReplayId : $"{Text.Get("replay_combat")} · {m.Act}/{m.Floor} · {Text.Get(m.CaptureKind)}";
                string detail = m == null ? "" : entry.ReplayId;
                string? right = m?.CapturedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
                Style.ListRow(rows, title, detail, right, () =>
                {
                    Clear(content);
                    Style.Title(content, Text.Get("replay_archive"));
                    try
                    {
                        var result = ReplayArchiveService.Inspect(entry);
                        Style.Label(content, entry.ReplayId, 20, color: Style.Dim);
                        if (result.Header is { } h)
                            Label(content, $"{h.GameVersion} · {h.GameCommit} · {h.ModelIdHash:x8} · {result.Bytes:N0} bytes");
                        Label(content, Text.Get(result.Compatible ? "replay_header_ok" : "replay_refused")
                            + (result.Error == null ? "" : " · " + Text.Get(result.Error)));
                        Style.Label(content, Text.Get("replay_readonly"), 20, color: Style.Dim);
                        if (result.Compatible)
                        {
                            var summary = ReplayPlaybackService.Inspect(entry);
                            if (summary.Document != null)
                                Style.Button(content, Text.Get("replay_summary_play"), () =>
                                {
                                    try { ReplayPlaybackService.Start(entry); Clear(content); PopulateSummary(content, layer, close); }
                                    catch (Exception ex) { ReplayPlaybackService.Stop(ex.Message); Label(content, Text.Get(ex.Message)); }
                                }, Style.Tone.Green, expand: false, minWidth: 320);
                            else Label(content, Text.Get(summary.Error ?? "invalid_replay_snapshots"));
                        }
                    }
                    catch (Exception ex) { Label(content, Text.Get("error") + " · " + ex.Message); }
                    var play = Style.Button(content, Text.Get("replay_blocked"), () => { }, expand: false, minWidth: 320);
                    Style.Enable(play, false);
                    Style.Spacer(content, vertical: true);
                    var footer = Style.Row(content);
                    Style.Button(footer, Text.Get("back"), () => { Clear(content); PopulateReplays(content, layer, close); }, minWidth: 220, expand: false).GrabFocus();
                    Style.Spacer(footer);
                    Style.Button(footer, Text.Get("close"), close, minWidth: 220, expand: false);
                });
            }
        }
        catch (Exception ex) { Label(rows, Text.Get("error") + " · " + ex.Message); }
        var footer = Style.Row(content);
        Style.Button(footer, Text.Get("back"), () => { Clear(content); Populate(content, layer, close); }, minWidth: 220, expand: false).GrabFocus();
        Style.Spacer(footer);
        Style.Button(footer, Text.Get("close"), close, minWidth: 220, expand: false);
    }

    private static void PopulateSummary(VBoxContainer content, CanvasLayer layer, Action close)
    {
        Resize(Style.PanelHeight);
        Style.Title(content, Text.Get("replay_summary_title"));
        Style.Label(content, Text.Get("replay_summary_scope"), 20, color: Style.Dim);
        var state = Style.Label(content, "", 24, bold: true, color: Style.Gold);
        var scroll = Style.Scroll(content, 180);
        var actors = Label(scroll, "");
        var controls = new HFlowContainer(); content.AddChild(controls);
        controls.AddThemeConstantOverride("h_separation", 10); controls.AddThemeConstantOverride("v_separation", 10);
        void Execute(string command, int? speed = null)
        {
            try { ReplayPlaybackService.Control(command, speed); }
            catch (Exception ex) { ReplayPlaybackService.Stop(ex.Message); }
        }
        var play = Style.Button(controls, Text.Get("replay_play_pause"), () =>
            Execute(ReplayPlaybackService.Session?.Playing == true ? "replay-pause" : "replay-resume"), expand: false, minWidth: 200, fontSize: 22);
        var step = Style.Button(controls, Text.Get("replay_next"), () => Execute("replay-step"), expand: false, minWidth: 200, fontSize: 22);
        var previous = Style.Button(controls, Text.Get("replay_previous"), () => Execute("replay-previous"), expand: false, minWidth: 200, fontSize: 22);
        var speeds = new List<Button>();
        foreach (int speed in new[] { 1, 2, 4 })
            speeds.Add(Style.Button(controls, speed + "×", () => Execute("replay-speed", speed), expand: false, minWidth: 90, fontSize: 22));
        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(state) || !GodotObject.IsInstanceValid(actors)) return;
            var session = ReplayPlaybackService.Session;
            foreach (var button in speeds) Style.Enable(button, session != null);
            Style.Enable(play, session != null); Style.Enable(step, session != null); Style.Enable(previous, session != null);
            if (session == null)
            {
                state.Text = Text.Get(ReplayPlaybackService.LastError ?? "replay_summary_closed"); actors.Text = ""; return;
            }
            state.Text = string.Format(Text.Get("replay_summary_state"), session.Current.Turn,
                session.Index + 1, session.FrameCount, session.Speed,
                Text.Get(session.Completed ? "replay_summary_eof" : session.Playing ? "replay_playing" : "replay_paused"));
            actors.Text = Text.Get("replay_players") + "\n" + string.Join("\n", session.Current.Players.Select(x => $"{x.Label} · HP {x.Hp}/{x.MaxHp}"))
                + "\n\n" + Text.Get("replay_enemies") + "\n" + string.Join("\n", session.Current.Enemies.Select(x => $"{x.Label} · HP {x.Hp}/{x.MaxHp}"));
        }
        ReplayPlaybackService.Changed += Refresh;
        // The dialog persists when its contents are replaced. Detach on this view's lifetime.
        state.TreeExiting += () => ReplayPlaybackService.Changed -= Refresh;
        var footer = Style.Row(content);
        Style.Spacer(footer);
        Style.Button(footer, Text.Get("replay_exit"), () => { ReplayPlaybackService.Stop(); close(); }, Style.Tone.Red, minWidth: 260, expand: false).GrabFocus();
        Refresh();
    }

    // Settings block below the list; returns the action of the "Save settings" footer button.
    private static Action AddSettings(VBoxContainer content, CanvasLayer layer)
    {
        var settings = RewindMod.Settings;
        Style.Divider(content);
        Style.Label(content, Text.Get("settings"), 26, bold: true, color: Style.Gold, outline: Style.GoldOutline);
        var enabled = Style.Tickbox(content, Text.Get("enabled"), settings.Enabled);
        var mark = Style.Tickbox(content, Text.Get("mark"), settings.MarkRewoundRuns);
        var retentionRow = Style.Row(content, 16);
        var retentionText = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        retentionText.AddThemeConstantOverride("separation", -2);
        retentionRow.AddChild(retentionText);
        Style.Label(retentionText, Text.Get("retention"), 24);
        Style.Label(retentionText, Text.Get("retention_hint"), 18, color: Style.Dim);
        int retained = Math.Clamp(settings.RetainedRuns, 1, 100);
        Style.Paginator(retentionRow, retained.ToString(),
            () => (retained = StepRetained(retained, -1)).ToString(), () => (retained = StepRetained(retained, 1)).ToString());
        var languageRow = Style.Row(content, 16);
        Style.Label(languageRow, Text.Get("language"), 24);
        string[] languages = ["auto", "zht", "zhs", "eng"];
        string[] names = [Text.Get("auto"), "繁體中文", "简体中文", "English"];
        int index = Math.Max(0, Array.IndexOf(languages, settings.Language));
        Style.Paginator(languageRow, names[index], () => names[index = (index + 3) % 4], () => names[index = (index + 1) % 4]);
        return () =>
        {
            try
            {
                RewindMod.Repository.WriteSettings(settings with { Enabled = enabled.ButtonPressed,
                    MarkRewoundRuns = mark.ButtonPressed, RetainedRuns = retained, Language = languages[index] });
                // Retention applies on every save of the settings; with no current run the newest runs fill all slots.
                RewindMod.Repository.ApplyRetention(retained, SaveManager.Instance.HasRunSave ? RewindMod.RunId(RewindMod.CurrentSave()) : null);
                layer.QueueFree();
                NGame.Instance?.MainMenu?.RefreshButtons();
            }
            catch (Exception ex) { RewindMod.Error(ex); SetStatus(content, Text.Get("error") + " · " + ex.Message); }
        };
    }

    // 1..10 by one, 10..100 by ten.
    private static int StepRetained(int value, int direction) => direction > 0
        ? (value < 10 ? value + 1 : Math.Min(100, value + 10))
        : (value <= 10 ? Math.Max(1, value - 1) : Math.Max(10, value - 10));

    private static void Confirm(VBoxContainer content, CanvasLayer layer, CheckpointMetadata checkpoint, Action close)
    {
        Clear(content);
        Resize(Style.ConfirmHeight);
        Style.Title(content, Text.Get("confirm"));
        Style.ListRow(content, RowTitle(checkpoint), RowDetail(checkpoint), RowTime(checkpoint), null);
        Style.Label(content, Text.Get("warning"), 22);
        var mark = Style.Tickbox(content, Text.Get("mark"), RewindMod.Settings.MarkRewoundRuns);
        Style.Spacer(content, vertical: true);
        var footer = Style.Row(content);
        Style.Button(footer, Text.Get("cancel"), () => { Clear(content); Populate(content, layer, close); }, Style.Tone.Red, minWidth: 240, expand: false).GrabFocus();
        Style.Spacer(footer);
        var confirm = Style.Button(footer, Text.Get("confirm"), () => { }, Style.Tone.Green, minWidth: 280, expand: false);
        openPrimary = confirm;
        confirm.Pressed += async () =>
        {
            openPrimary = null;
            bool shouldMark = mark.ButtonPressed;
            Clear(content);
            Style.Title(content, Text.Get("confirm"));
            Label(content, Text.Get("working"));
            try { await RestoreService.Restore(checkpoint, shouldMark); layer.QueueFree(); }
            catch (Exception ex)
            {
                Clear(content);
                Style.Title(content, Text.Get("confirm"));
                bool markerFailed = ex.Message.StartsWith("restored_marker_failed", StringComparison.Ordinal);
                if (!markerFailed) Label(content, Text.Get("failed"));
                if (ex.Message.StartsWith("restore_failed_original_recovered_restart_required", StringComparison.Ordinal)) Label(content, Text.Get("restart"));
                else if (ex.Message.StartsWith("restore_original_unchanged_restart_required", StringComparison.Ordinal)) Label(content, Text.Get("restart_unchanged"));
                else if (ex.Message.StartsWith("restore_failed_original_recovered", StringComparison.Ordinal)) Label(content, Text.Get("recovered"));
                if (ex.Message.StartsWith("restore_and_rollback_failed", StringComparison.Ordinal)) Label(content, Text.Get("unrecovered"));
                if (ex.Message.StartsWith("restored_marker_failed", StringComparison.Ordinal)) Label(content, Text.Get("marker_failed"));
                Style.Label(content, Text.Get("error") + " · " + ex.Message, 20, color: Style.Dim);
                Style.Spacer(content, vertical: true);
                var errorFooter = Style.Row(content);
                Style.Spacer(errorFooter);
                Style.Button(errorFooter, Text.Get("close"), close, minWidth: 220, expand: false).GrabFocus();
            }
            finally { NGame.Instance?.MainMenu?.RefreshButtons(); }
        };
    }

    private static void Clear(Node node)
    {
        foreach (Node child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); }
    }

    private static Label Label(Node parent, string text) => Style.Label(parent, text, 22);
}
