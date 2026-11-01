using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireRewind.Core;

namespace SpireRewind;

// Observes only the current run's newly appended history; never reconstructs older choices.
internal static class RunChoiceRecorder
{
    private static RunState? lastRun;
    private static PlayerMapPointHistoryEntry? lastRoom;
    private static readonly RoomHistoryCursor cursor = new();
    private static int profile;
    private static readonly Dictionary<MerchantEntry, (RunState state, int profile, string? model)> pendingPurchases = [];
    internal static void Clear() { lastRun = null; lastRoom = null; cursor.Detach(); pendingPurchases.Clear(); }
    private static bool Allowed(RunState? state)
    {
        var m = RunManager.Instance;
        // While a saved run is being continued, State is set before NetService (the run manager awaits the reload
        // counter save in between), so IsInProgress alone does not mean the run is initialised.
        return state != null && m.IsInProgress && m.NetService is { Type: NetGameType.Singleplayer }
            && !m.DailyTime.HasValue && state.GameMode is GameMode.Standard or GameMode.Custom
            && state.Players.Count == 1 && RewindMod.Settings.Enabled && !RewindMod.Busy && !ReplayPlaybackService.Active;
    }
    private static PlayerMapPointHistoryEntry? CurrentRoom(RunState state)
    {
        ulong id = state.Players[0].NetId;
        return state.CurrentMapPointHistoryEntry?.PlayerStats?.FirstOrDefault(e => e.PlayerId == id);
    }
    private static void Add(RunState state, string kind, string? model = null, string? text = null, string? value = null)
    {
        if (!Allowed(state)) return;
        int p = SaveManager.Instance.CurrentProfileId;
        long start = (long)AccessTools.Field(typeof(RunManager), "_startTime").GetValue(RunManager.Instance)!;
        string run = RunIdentity.Create(p, state.Rng.StringSeed, start, state.Players[0].Character.Id.ToString());
        var repo = new RunChoiceRepository(ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("rewind")));
        repo.Append(run, new RunChoiceEntry { EntryId = Guid.NewGuid().ToString("N"), Act = state.CurrentActIndex + 1,
            Floor = state.TotalFloor, Kind = kind, ModelId = model, TextKey = text, Value = value, CapturedAtUtc = DateTimeOffset.UtcNow });
    }
    internal static void Observe()
    {
        try
        {
            var state = RunManager.Instance.DebugOnlyGetState();
            if (!Allowed(state)) { Clear(); return; }
            var current = CurrentRoom(state!);
            bool reset = state != lastRun || profile != SaveManager.Instance.CurrentProfileId;
            bool newRoom = current != lastRoom;
            lastRun = state; lastRoom = current; profile = SaveManager.Instance.CurrentProfileId;
            if (current == null) { cursor.Detach(); return; }
            // Existing entries on attachment are historical. On subsequent room changes, only new in-memory entries are observed.
            var counts = new RoomHistoryCounts(current.CardChoices.Count, current.CardsRemoved.Count, current.BoughtRelics.Count,
                current.BoughtPotions.Count, current.BoughtColorless.Count);
            var added = cursor.Observe(counts, reset, newRoom);
            if (added == null) { Clear(); return; }
            foreach (int index in added)
            {
                var c = current.CardChoices[index];
                if (c.wasPicked && state!.CurrentRoom is not MegaCrit.Sts2.Core.Rooms.MerchantRoom) Add(state, "card_reward", c.Card.Id?.ToString());
            }
            // Removal is recorded by RemoveFromState after the card is actually removed. Card purchases are already
            // recorded through CardChoices; relic, potion and colorless purchases through the merchant hooks.
        }
        catch (Exception ex) { Clear(); RewindMod.Error(ex); }
    }
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.RemoveFromState))]
    private static class RemovedPatch
    {
        private static void Postfix(CardModel __instance)
        {
            try
            {
                var state = RunManager.Instance.DebugOnlyGetState();
                if (!Allowed(state) || state != lastRun || profile != SaveManager.Instance.CurrentProfileId) return;
                var room = CurrentRoom(state!);
                if (room == null || room != lastRoom) return;
                int index = cursor.NextRemoval(room.CardsRemoved.Count);
                if (index < 0) return;
                var id = room.CardsRemoved[index].Id?.ToString();
                if (id != __instance.Id.ToString()) return;
                cursor.AdvanceRemoval();
                Add(state!, "card_removal", id);
            }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
    }
    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
    private static class PurchaseStartPatch
    {
        private static void Prefix(MerchantEntry __instance)
        {
            try
            {
                var state = RunManager.Instance.DebugOnlyGetState();
                // The removal service is recorded as card_removal by RemoveFromState; a model-less purchase would duplicate it.
                if (!Allowed(state) || __instance is MerchantCardRemovalEntry) return;
                string? model = __instance switch
                {
                    MerchantCardEntry c => c.CreationResult?.Card.Id.ToString(),
                    MerchantRelicEntry r => r.Model?.Id.ToString(),
                    MerchantPotionEntry p => p.Model?.Id.ToString(),
                    _ => null
                };
                pendingPurchases[__instance] = (state!, SaveManager.Instance.CurrentProfileId, model);
            }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
    }
    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
    private static class PurchaseDonePatch
    {
        private static void Postfix(MerchantEntry __instance)
        {
            try
            {
                if (pendingPurchases.Remove(__instance, out var item) && item.profile == SaveManager.Instance.CurrentProfileId
                    && ReferenceEquals(item.state, RunManager.Instance.DebugOnlyGetState())) Add(item.state, "shop_purchase", item.model);
            }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
    }
    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
    private static class PurchaseFailedPatch { private static void Postfix(MerchantEntry __instance) { pendingPurchases.Remove(__instance); } }
    [HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
    private static class EventPatch
    {
        private static void Prefix(EventSynchronizer __instance, MegaCrit.Sts2.Core.Entities.Players.Player player, int optionIndex, out string? __state)
        {
            __state = null;
            try { var e = __instance.GetEventForPlayer(player); if (optionIndex >= 0 && optionIndex < e.CurrentOptions.Count) __state = e.CurrentOptions[optionIndex].TextKey; }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
        private static void Postfix(MegaCrit.Sts2.Core.Entities.Players.Player player, string? __state)
        {
            try { if (__state != null && player.RunState is RunState state) Add(state, "event_option", text: __state); }
            catch (Exception ex) { RewindMod.Error(ex); }
        }
    }
}
