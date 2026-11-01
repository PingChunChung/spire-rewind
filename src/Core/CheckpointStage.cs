namespace SpireRewind.Core;

public static class CheckpointStage
{
    public static (int Floor, string RoomType, string CaptureKind) Describe(
        int completedFloorCount, int currentActHistoryCount, int visitedCoordinateCount,
        bool hasPreFinishedRoom, string previousRoom, string destinationPointType, string captureKind)
    {
        bool pending = (captureKind == "native_save" || captureKind == "before_restore")
            && !hasPreFinishedRoom && visitedCoordinateCount > currentActHistoryCount;
        if (pending)
            return (checked(completedFloorCount + 1), destinationPointType,
                captureKind == "native_save" ? "room_entry" : captureKind);
        return (completedFloorCount, previousRoom, captureKind);
    }
}
