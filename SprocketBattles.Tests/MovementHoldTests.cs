using SprocketBattles;

static class MovementHoldTests
{
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("movement hold: " + description);
    }

    public static void Run()
    {
        Check(MovementHoldPolicy.AtStart(false, false, 0), "authored idle tanks hold without driving back to a coordinate");
        Check(!MovementHoldPolicy.AtStart(false, false, 2), "an authored path begins normally");
        Check(!MovementHoldPolicy.AtStart(true, false, 0), "quick battles keep autonomous movement");
        Check(MovementHoldPolicy.AtStart(true, true, 0), "an explicit stop also applies in a quick battle");
        Check(MovementHoldPolicy.AtStart(false, true, 3), "a saved force stop overrides a saved path");
        Check(!MovementHoldPolicy.Idle(true, false, false), "an unfinished path keeps its movement controls");
        Check(!MovementHoldPolicy.Idle(false, true, false), "an explicit attack approach keeps its movement controls");
        Check(!MovementHoldPolicy.Idle(false, false, true), "a command-view move keeps its movement controls");
        Check(MovementHoldPolicy.Idle(false, false, false), "completed or absent movement orders return to holding");
        Check(!MovementHoldPolicy.ManualFinished(100, 0.5f, false), "a pending native path has time to start without losing a manual order");
        Check(!MovementHoldPolicy.ManualFinished(100, 5, true), "an active native path stays authoritative");
        Check(MovementHoldPolicy.ManualFinished(100, 3, false), "a failed or finished path eventually releases its manual order");
        Check(MovementHoldPolicy.ManualFinished(5, 0.5f, true), "arrival completes a move even while the native task winds down");

        var forward = new MovementBrake();
        Check(forward.Throttle(12) == -1, "a tank moving forward brakes with reverse input");
        Check(forward.Throttle(0.5f) == -1, "braking continues through the slowing band");
        Check(forward.Throttle(0.3f) == 0, "braking changes to neutral near rest");
        Check(forward.Throttle(0.5f) == 0, "small motion near rest does not reapply reverse input");
        Check(forward.Throttle(-0.5f) == 0, "a small speed sign change cannot alternate drive directions");
        Check(forward.Throttle(-2) == 1, "a backwards push gets forward braking");
        Check(forward.Throttle(-0.2f) == 0, "backward braking also stops at neutral");
        Check(forward.Throttle(0) == 0, "rest stays neutral");
        Check(forward.Throttle(float.NaN) == 0 && forward.Throttle(float.PositiveInfinity) == 0,
            "invalid physics speed never produces drive input");
        Check(new MovementBrake().Throttle(-8) == 1, "a tank initially reversing uses forward braking");
        Console.WriteLine("MOVEMENT_HOLD_TESTS_OK: authored idle and explicit stop, movement transitions, signed braking and rest hysteresis");
    }
}
