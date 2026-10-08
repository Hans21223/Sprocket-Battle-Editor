namespace SprocketBattles;

/// Wait for editor cleanup and a normal physics step before releasing the current native vehicles.
internal sealed class RestartGate
{
    int baselineFrame;
    float baselineFixedTime;
    bool begun;

    internal RestartGate(int frame, float fixedTime)
    {
        baselineFrame = frame;
        baselineFixedTime = fixedTime;
    }

    internal bool TryBegin(int currentFrame, float currentFixedTime, bool editorBusy, bool paused)
    {
        if (begun) return false;
        if (editorBusy || paused)
        {
            // Cleanup can take another frame, or the player can pause again while waiting.
            // Each hold starts a fresh wait; paused frames never count toward safe native teardown.
            baselineFrame = currentFrame;
            baselineFixedTime = currentFixedTime;
            return false;
        }
        if ((long)currentFrame - baselineFrame < 2 || !float.IsFinite(baselineFixedTime) || !float.IsFinite(currentFixedTime)
            || currentFixedTime <= baselineFixedTime) return false;
        begun = true;
        return true;
    }
}
