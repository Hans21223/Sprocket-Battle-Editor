namespace SprocketBattles;

/// Driver-only hold rules, independent of native objects so signed braking and order transitions can be checked offline.
internal static class MovementHoldPolicy
{
    internal static bool AtStart(bool quick, bool forceStopped, int pathPoints) => forceStopped || (!quick && pathPoints == 0);
    internal static bool Idle(bool pathActive, bool attackApproachActive, bool manualMoveActive) =>
        !pathActive && !attackApproachActive && !manualMoveActive;
    internal static bool ManualFinished(float distance, float elapsed, bool hasTask) =>
        distance < 6 || (elapsed >= 3 && !hasTask);
}

/// Opposing input asks the native transmission to brake, then ordinary neutral input holds the tank at rest.
/// The wider re-entry threshold prevents alternating forward/reverse commands around zero speed.
internal sealed class MovementBrake
{
    int direction;
    internal float Throttle(float signedSpeed)
    {
        if (!float.IsFinite(signedSpeed)) { direction = 0; return 0; }
        int current = Math.Sign(signedSpeed);
        if (Math.Abs(signedSpeed) <= 0.35f || (direction != 0 && current != direction)) direction = 0;
        if (direction == 0 && Math.Abs(signedSpeed) > 0.75f) direction = current;
        return -direction;
    }
}
