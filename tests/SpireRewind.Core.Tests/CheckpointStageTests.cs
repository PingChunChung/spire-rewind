using SpireRewind.Core;

internal static class CheckpointStageTests
{
    internal static void Run()
    {
        void Check((int Floor, string RoomType, string CaptureKind) actual, (int, string, string) expected)
        {
            if (actual != expected) throw new Exception($"Unexpected checkpoint stage: {actual}");
        }
        Check(CheckpointStage.Describe(5, 5, 6, false, "Monster", "Shop", "native_save"), (6, "Shop", "room_entry"));
        Check(CheckpointStage.Describe(5, 5, 6, false, "Monster", "Unknown", "native_save"), (6, "Unknown", "room_entry"));
        Check(CheckpointStage.Describe(5, 5, 6, true, "Monster", "Shop", "native_save"), (5, "Monster", "native_save"));
        Check(CheckpointStage.Describe(17, 0, 1, false, "Boss", "Shop", "native_save"), (18, "Shop", "room_entry"));
        Check(CheckpointStage.Describe(5, 5, 6, false, "Monster", "Shop", "before_restore"), (6, "Shop", "before_restore"));
        Check(CheckpointStage.Describe(5, 5, 5, false, "Monster", "Shop", "before_restore"), (5, "Monster", "before_restore"));
        Check(CheckpointStage.Describe(5, 5, 6, false, "Monster", "Shop", "combat_replay"), (5, "Monster", "combat_replay"));
    }
}
