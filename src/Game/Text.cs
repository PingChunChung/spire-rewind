using MegaCrit.Sts2.Core.Localization;

namespace SpireRewind;

internal static class Text
{
    // Keep all player-facing strings together. Missing language falls back to English.
    // Chinese follows the game's own zhs strings: acts are 阶段{n} (gameplay_ui ACT_NUMBER) and floors 第{n}层
    // (run_history MAP_POINT_HISTORY.header), with no spaces around numbers.
    private static readonly Dictionary<string, string[]> Strings = new()
    {
        ["rewind"] = ["回溯", "回溯", "Rewind"],
        ["export_replay"] = ["匯出此局回放", "导出此局回放", "Export this run replay"],
        ["export_runs"] = ["匯出保留局回放", "导出保留局回放", "Export saved run replays"],
        ["export_scope"] = ["匯出單一離線 HTML，包含已保存的關鍵點、戰鬥摘要與選擇紀錄。以瀏覽器開啟；網頁不會操作遊戲。", "导出单一离线 HTML，包含已保存的关键点、战斗摘要与选择记录。用浏览器打开；网页不会操作游戏。", "Export one offline HTML with saved checkpoints, combat snapshots and choices. Open it in a browser; the page never controls the game."],
        ["export_done"] = ["已匯出，請用瀏覽器開啟：", "已导出，请用浏览器打开：", "Exported. Open in a browser:"],
        ["export_empty"] = ["此 profile 尚無可匯出的保留局。", "此 profile 尚无可导出的保留局。", "No saved runs to export in this profile."],
        ["settings"] = ["回溯設定", "回溯设置", "Rewind settings"],
        ["back"] = ["返回", "返回", "Back"],
        ["replay_archive"] = ["戰鬥錄影庫", "战斗录像库", "Combat archive"],
        ["replay_combat"] = ["階段／層", "阶段／层", "Act/Floor"],
        ["replay_empty"] = ["此 profile 尚無封存錄影。", "此 profile 尚无封存录像。", "No archived replays in this profile."],
        ["replay_header_ok"] = ["檔案完整性與版本標頭檢查通過；尚未驗證戰鬥內容。", "文件完整性与版本标头检查通过；尚未验证战斗内容。", "Integrity and version header checks passed; combat payload is unverified."],
        ["replay_refused"] = ["錄影檢查未通過", "录像检查未通过", "Replay inspection refused"],
        ["replay_readonly"] = ["可檢視錄影資訊；有決策點摘要時可播放回合與 HP 紀錄。原生戰鬥動畫仍因隔離限制而封鎖。", "可查看录像信息；有决策点摘要时可播放回合与 HP 记录。原生战斗动画仍因隔离限制而封锁。", "Inspect replay information or play recorded turn/HP snapshots when available. Native combat playback remains blocked."],
        ["replay_blocked"] = ["原生戰鬥播放暫未開放", "原生战斗播放暂未开放", "Native combat playback unavailable"],
        ["replay_summary_play"] = ["播放決策點摘要", "播放决策点摘要", "Play decision snapshots"],
        ["replay_summary_title"] = ["戰鬥決策點摘要", "战斗决策点摘要", "Combat decision snapshots"],
        ["replay_summary_scope"] = ["顯示錄製時保存的回合與 HP，不重演出牌或戰鬥動畫。1× 每秒前進一點；最後一點是最後記錄的決策點，未必是最終戰果。", "显示录制时保存的回合与 HP，不重演出牌或战斗动画。1× 每秒前进一点；最后一点是最后记录的决策点，未必是最终战果。", "Recorded turns and HP only; no card or combat animation. 1× advances one snapshot per second. The last snapshot may precede the final outcome."],
        ["replay_play_pause"] = ["播放／暫停", "播放／暂停", "Play / Pause"],
        ["replay_next"] = ["下一個記錄決策點", "下一个记录决策点", "Next recorded decision"],
        ["replay_previous"] = ["上一步（從頭快轉，較慢）", "上一步（从头快进，较慢）", "Previous (restart and advance; slower)"],
        ["replay_exit"] = ["退出回主選單", "退出回主菜单", "Exit to main menu"],
        ["replay_summary_closed"] = ["摘要播放已結束。", "摘要播放已结束。", "Snapshot playback ended."],
        ["replay_summary_state"] = ["回合{0} · 決策點{1}/{2} · {3}× · {4}", "回合{0} · 决策点{1}/{2} · {3}× · {4}", "Turn {0} · Snapshot {1}/{2} · {3}× · {4}"],
        ["replay_summary_eof"] = ["最後記錄點", "最后记录点", "Last recorded snapshot"],
        ["replay_playing"] = ["播放中", "播放中", "Playing"],
        ["replay_paused"] = ["已暫停", "已暂停", "Paused"],
        ["replay_players"] = ["玩家", "玩家", "Players"],
        ["replay_enemies"] = ["敵人", "敌人", "Enemies"],
        ["replay_snapshots_missing"] = ["這筆錄影沒有決策點摘要。舊錄影無法補回回合與 HP 紀錄；請在新版錄製新的戰鬥。", "这笔录像没有决策点摘要。旧录像无法补回回合与 HP 记录；请在新版录制新的战斗。", "No decision snapshots. Older recordings cannot recover turn/HP history; record a new battle with this version."],
        ["replay_snapshots_limit"] = ["決策點摘要超過大小限制。", "决策点摘要超过大小限制。", "Decision snapshots exceed the size limit."],
        ["invalid_replay_snapshots"] = ["決策點摘要損毀或格式不正確。", "决策点摘要损坏或格式不正确。", "Invalid decision snapshots."],
        ["replay_snapshots_identity_mismatch"] = ["決策點摘要與錄影的雜湊或版本不符。", "决策点摘要与录像的哈希或版本不符。", "Decision snapshots do not match this recording."],
        ["replay_snapshots_link_refused"] = ["決策點摘要路徑包含連結，已拒絕讀取。", "决策点摘要路径包含链接，已拒绝读取。", "Linked snapshot paths are refused."],
        ["replay_save_in_flight"] = ["請等待目前存檔完成後再開啟。", "请等待当前存档完成后再打开。", "Wait for the pending save to complete."],
        ["replay_profile_changed"] = ["Profile 已變更，摘要播放已停止。", "Profile 已更改，摘要播放已停止。", "Profile changed; snapshot playback stopped."],
        ["replay_already_active"] = ["請先退出目前的摘要播放。", "请先退出当前摘要播放。", "Exit the current snapshot playback first."],
        ["replay_not_active"] = ["目前沒有摘要播放。", "当前没有摘要播放。", "No active snapshot playback."],
        ["replay_isolation_unavailable"] = ["原生回放的非同步工作無法確認已完全停止，進度與平台隔離仍未驗證。", "原生回放的异步工作无法确认已完全停止，进度与平台隔离仍未验证。", "Native replay tasks cannot be confirmed stopped; progress and platform isolation remain unverified."],
        ["replay_main_menu_only"] = ["請先返回主選單。", "请先返回主菜单。", "Return to the main menu first."],
        ["replay_runtime_unavailable"] = ["無法取得目前遊戲版本資訊。", "无法获取当前游戏版本信息。", "Current game identity is unavailable."],
        ["replay_size_limit"] = ["錄影大小不在可檢視範圍內（12 bytes 至 16 MiB）。", "录像大小不在可查看范围内（12 bytes 至 16 MiB）。", "Replay size is outside inspection limits (12 bytes to 16 MiB)."],
        ["replay_metadata_limit"] = ["錄影描述檔超過大小限制。", "录像描述文件超过大小限制。", "Replay metadata exceeds its size limit."],
        ["invalid_replay_metadata"] = ["錄影描述檔格式不正確。", "录像描述文件格式不正确。", "Invalid replay metadata."],
        ["invalid_replay_id"] = ["錄影識別碼不正確。", "录像标识码不正确。", "Invalid replay identifier."],
        ["replay_checksum_mismatch"] = ["錄影內容與保存時的檔案雜湊不符。", "录像内容与保存时的文件哈希不符。", "Replay file hash does not match the archive."],
        ["truncated_replay_header"] = ["錄影標頭不完整。", "录像标头不完整。", "Truncated replay header."],
        ["invalid_replay_header"] = ["錄影標頭格式不正確。", "录像标头格式不正确。", "Invalid replay header."],
        ["replay_version_mismatch"] = ["錄影與目前遊戲版本不符。", "录像与当前游戏版本不符。", "Replay game version differs."],
        ["replay_commit_mismatch"] = ["錄影與目前遊戲建置不符。", "录像与当前游戏构建不符。", "Replay game build differs."],
        ["replay_model_hash_mismatch"] = ["錄影與目前遊戲模型對照表不符。", "录像与当前游戏模型对照表不符。", "Replay model table hash differs."],
        ["replay_mod_mismatch"] = ["錄影與目前 mod 版本清單不符。", "录像与当前 mod 版本列表不符。", "Replay mod versions differ."],
        ["replay_identity_missing"] = ["這筆舊錄影缺少建置或模型識別資訊。", "这笔旧录像缺少构建或模型标识信息。", "This older archive lacks build or model identity."],
        ["replay_archive_missing"] = ["找不到錄影或描述檔。", "找不到录像或描述文件。", "Replay or metadata file is missing."],
        ["replay_archive_unreadable"] = ["無法讀取這筆錄影。", "无法读取这笔录像。", "Replay archive cannot be read."],
        ["title"] = ["Spire Rewind · 關鍵點", "Spire Rewind · 关键点", "Spire Rewind · Checkpoints"],
        ["enabled"] = ["啟用自動保存與回溯", "启用自动保存与回溯", "Enable checkpoints and rewind"],
        ["retention"] = ["保留最近幾局", "保留最近几局", "Retain recent runs"],
        ["retention_hint"] = ["1–100，包含目前這局；調低會刪除最舊的其他局", "1–100，包含当前这局；调低会删除最旧的其他局", "1–100, including the current run; lowering it deletes the oldest other runs"],
        ["language"] = ["介面語言", "界面语言", "Interface language"],
        ["auto"] = ["跟隨遊戲", "跟随游戏", "Follow game"],
        ["mark"] = ["標記為回溯局（只記在本機回溯資料）", "标记为回溯局（仅记录在本机回溯资料）", "Mark as rewound (local rewind records only)"],
        ["save"] = ["儲存設定", "保存设置", "Save settings"],
        ["close"] = ["關閉", "关闭", "Close"],
        ["confirm"] = ["確認回溯", "确认回溯", "Confirm rewind"],
        ["cancel"] = ["取消", "取消", "Cancel"],
        ["warning"] = ["目前進度會被替換，並重新載入這個關鍵點。未存檔的操作會遺失。\n進度、統計、成就與歷史紀錄不會回捲，繼續遊玩仍由遊戲正常記錄。\n會先建立還原前備份；雲端驗證失敗時停止載入。",
            "当前进度将被替换，并重新载入此关键点。未存档的操作会丢失。\n进度、统计、成就与历史记录不会回滚，继续游玩仍由游戏正常记录。\n会先建立还原前备份；云端验证失败时停止载入。",
            "Replace the current run and reload this checkpoint. Unsaved actions will be lost.\nProgress, statistics, achievements and history are not rewound. Further play is recorded normally.\nA recovery checkpoint is saved first. Failed cloud verification stops loading."],
        ["empty"] = ["這局尚無可用關鍵點。啟用後需等遊戲下次自動存檔。", "本局尚无可用关键点。启用后需等待游戏下次自动存档。", "No checkpoints for this run yet. Wait for the next native save after enabling."],
        ["unsupported"] = ["僅支援有存檔的單人一般或自訂局；每日挑戰、多人與回放不支援。", "仅支持有存档的单人普通或自定义局；每日挑战、多人及回放不支持。", "Requires a saved single-player standard or custom run. Daily, multiplayer and replay runs are unsupported."],
        ["disabled"] = ["回溯已停用，可在下方設定啟用。", "回溯已停用，可在下方设置启用。", "Rewind is disabled. Enable it below."],
        ["stats"] = ["進度、成就與統計不隨回溯還原。", "进度、成就与统计不会随回溯还原。", "Progress, achievements and statistics are not restored."],
        ["untested_game_version"] = ["這個版本的 mod 只在遊戲 v{1} 測試過，目前遊戲版本為 {0}。回溯仍可使用，請先備份存檔。", "这个版本的 mod 仅在游戏 v{1} 测试过，当前游戏版本为 {0}。回溯仍可使用，请先备份存档。", "This release was tested on game v{1}; the running game is {0}. Rewind still works; back up your saves first."],
        ["unknown_version"] = ["未知", "未知", "unknown"],
        ["marked"] = ["這局已標記為回溯局", "本局已标记为回溯局", "This run is marked as rewound"],
        ["working"] = ["正在回溯，請稍候……", "正在回溯，请稍候……", "Restoring checkpoint…"],
        ["failed"] = ["回溯未完成。若已返回主選單，請先查看下方結果再繼續。", "回溯未完成。若已返回主菜单，请先查看下方结果再继续。", "Rewind did not finish. If you returned to the menu, review the result below before continuing."],
        ["recovered"] = ["已還原原本的 run 存檔，可從主選單繼續。", "已恢复原有 run 存档，可从主菜单继续。", "The original run save was recovered. Continue from the main menu."],
        ["unrecovered"] = ["原檔或雲端的復原未通過驗證。請停止遊玩並保留 rewind 資料夾供復原。", "原档或云端的恢复未通过验证。请停止游玩并保留 rewind 文件夹以便恢复。", "Recovery of the original save or cloud copy could not be verified. Stop playing and preserve the rewind folder for recovery."],
        ["marker_failed"] = ["關鍵點已載入，但本機回溯紀錄寫入失敗。關閉後會回到已回溯的遊戲。", "关键点已载入，但本机回溯记录写入失败。关闭后会返回已回溯的游戏。", "The checkpoint was loaded, but the local rewind record could not be written. Closing returns to the restored game."],
        ["restart"] = ["原本的 run 存檔已復原，但遊戲狀態清理失敗。請正常退出並重新啟動遊戲後再繼續。", "原有 run 存档已恢复，但游戏状态清理失败。请正常退出并重新启动游戏后再继续。", "The original run save was recovered, but game state cleanup failed. Exit normally and restart before continuing."],
        ["fault"] = ["上一次回溯失敗後尚未重新啟動遊戲，在重啟前不能再回溯。請保留 rewind 資料夾與下列備份 ID。", "上一次回溯失败后尚未重新启动游戏，重启前不能再回溯。请保留 rewind 文件夹与下列备份 ID。", "The game has not been restarted since a failed rewind; no further rewind is possible until then. Keep the rewind folder and the backup id below."],
        ["continue_blocked"] = ["主選單的「繼續」已停用：目前的 run 存檔尚未通過驗證。請正常退出並重新啟動遊戲；需要時依 README 用備份 ID 手動復原。", "主菜单的“继续”已停用：当前 run 存档尚未通过验证。请正常退出并重新启动游戏；需要时按 README 用备份 ID 手动恢复。", "Continue on the main menu is disabled: the current run save is unverified. Exit normally and restart the game; if needed, recover by hand from the backup id (see README)."],
        ["restart_unchanged"] = ["目前存檔尚未替換，但遊戲狀態清理失敗。請正常退出並重新啟動遊戲後再繼續。", "当前存档尚未替换，但游戏状态清理失败。请正常退出并重新启动游戏后再继续。", "The current save was not replaced, but game state cleanup failed. Exit normally and restart before continuing."],
        ["error"] = ["詳細原因", "详细原因", "Details"],
        ["row"] = ["階段{0} · 第{1}層 · {2} · HP {3}/{4} · 金幣{5} · 牌{6} · {7}", "阶段{0} · 第{1}层 · {2} · HP {3}/{4} · 金币{5} · 牌{6} · {7}", "Act {0} · Floor {1} · {2} · HP {3}/{4} · Gold {5} · Cards {6} · {7}"],
        ["row_title"] = ["階段{0} · 第{1}層 · {2}", "阶段{0} · 第{1}层 · {2}", "Act {0} · Floor {1} · {2}"],
        ["row_detail"] = ["HP {0}/{1} · 金幣{2} · 牌組{3}張", "HP {0}/{1} · 金币{2} · 牌组{3}张", "HP {0}/{1} · Gold {2} · {3} cards"],
        ["native_save"] = ["自動存檔", "自动存档", "Auto save"],
        ["room_entry"] = ["即將進入", "即将进入", "Entering room"],
        ["room_completed"] = ["房間完成", "房间完成", "Room completed"],
        ["before_restore"] = ["還原前備份", "还原前备份", "Recovery checkpoint"],
        ["Map"] = ["地圖", "地图", "Map"],
        ["Monster"] = ["戰鬥", "战斗", "Combat"],
        ["Elite"] = ["菁英", "精英", "Elite"],
        ["Boss"] = ["首領", "首领", "Boss"],
        ["Event"] = ["事件", "事件", "Event"],
        ["Treasure"] = ["寶箱", "宝箱", "Treasure"],
        ["Merchant"] = ["商店", "商店", "Merchant"],
        ["Shop"] = ["商店", "商店", "Shop"],
        ["Unknown"] = ["問號房", "问号房", "Unknown room"],
        ["Ancient"] = ["先古事件", "先古事件", "Ancient"],
        ["Unassigned"] = ["未定房間", "未定房间", "Unassigned"],
        ["RestSite"] = ["休息處", "休息处", "Rest site"],
        ["Combat"] = ["戰鬥", "战斗", "Combat"],
        ["None"] = ["地圖", "地图", "Map"]
    };

    public static string Get(string key)
    {
        string language = CurrentLanguage;
        int index = language == "zht" ? 0 : language == "zhs" ? 1 : 2;
        return Strings.TryGetValue(key, out var translations) ? translations[index] : key;
    }

    internal static string CurrentLanguage
    {
        get
        {
            string language;
            try { language = RewindMod.Settings.Language; }
            catch { language = "auto"; }
            if (language == "auto") language = LocManager.Instance?.Language ?? "eng";
            return language is "zht" or "zh_TW" or "zh-Hant" ? "zht" : language is "zhs" or "zh_Hans" or "zh-Hans" ? "zhs" : "eng";
        }
    }
}
