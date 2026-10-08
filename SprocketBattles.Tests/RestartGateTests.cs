using SprocketBattles;

static class RestartGateTests
{
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("restart gate: " + description);
    }

    public static void Run()
    {
        var cleanup = new RestartGate(100, 5);
        Check(!cleanup.TryBegin(101, 5.02f, true, false), "an editor view still owns native references");
        Check(!cleanup.TryBegin(105, 5.10f, true, false), "long editor cleanup still blocks native teardown");
        Check(!cleanup.TryBegin(106, 5.12f, false, false), "first resumed frame cannot skip the cleanup interval");
        Check(cleanup.TryBegin(107, 5.14f, false, false), "two resumed frames with physics release the wait");
        Check(!cleanup.TryBegin(107, 5.14f, false, false), "a repeated callback in the same frame cannot restart twice");
        Check(!cleanup.TryBegin(108, 5.16f, false, false), "later callbacks cannot restart twice");
        Check(!cleanup.TryBegin(109, 5.18f, true, true), "a completed gate stays consumed when the editor reopens");
        Check(!cleanup.TryBegin(112, 5.22f, false, false), "reopening the editor cannot rearm a completed request");

        var paused = new RestartGate(200, 8);
        Check(!paused.TryBegin(210, 8, false, true), "paused frames do not count toward the cleanup wait");
        Check(!paused.TryBegin(211, 8.02f, false, false), "resume after a long pause still waits a full frame");
        Check(paused.TryBegin(212, 8.04f, false, false), "normal physics and a full resumed frame finish a paused request");

        var noPhysics = new RestartGate(300, 12);
        Check(!noPhysics.TryBegin(301, 12, false, false), "a render frame without physics cannot begin");
        Check(!noPhysics.TryBegin(310, 12, false, false), "many render frames cannot replace a physics step");
        Check(!noPhysics.TryBegin(311, float.NaN, false, false), "an invalid clock cannot prove physics completed");
        Check(noPhysics.TryBegin(312, 12.02f, false, false), "the first real physics step releases an otherwise ready wait");

        var interrupted = new RestartGate(400, 15);
        Check(!interrupted.TryBegin(401, 15.02f, false, false), "initial resumed frame waits");
        Check(!interrupted.TryBegin(402, 15.02f, false, true), "pausing again cancels elapsed resumed frames");
        Check(!interrupted.TryBegin(403, 15.04f, false, false), "the first frame after another pause waits again");
        Check(interrupted.TryBegin(404, 15.06f, false, false), "a fresh cleanup interval and physics step finish the request");

        var combinedHold = new RestartGate(500, 20);
        Check(!combinedHold.TryBegin(501, 20.02f, true, true), "combined editor and pause holds block");
        Check(!combinedHold.TryBegin(502, 20.04f, true, false), "leaving pause does not bypass an active editor");
        Check(!combinedHold.TryBegin(503, 20.06f, false, false), "final editor cleanup resets the safe interval");
        Check(combinedHold.TryBegin(504, 20.08f, false, false), "both holds must be gone before the safe interval passes");
        Console.WriteLine("RESTART_GATE_TESTS_OK: editor cleanup, pause and resume, physics step, interrupted wait, exactly one restart");
    }
}
