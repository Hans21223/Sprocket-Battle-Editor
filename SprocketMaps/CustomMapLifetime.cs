namespace SprocketMaps;

/// A canceled Unity load still owns work until Unity finishes it. Keep that ownership separate from the
/// task awaited by Sprocket: abandon settles the task immediately, while recovery waits for cleanup.
internal sealed class CustomMapLifetime
{
    internal enum Outcome { Pending, Succeeded, Faulted, Canceled }
    internal Outcome Result { get; private set; }
    internal bool Abandoned { get; private set; }
    internal bool Recovering { get; private set; }
    internal bool SceneLoading { get; private set; }
    internal bool NavigationBuilding { get; private set; }
    internal bool CleanupFinished { get; private set; }
    internal bool CanDisposeResources => !SceneLoading && !NavigationBuilding;
    internal bool CanResumeNative => Recovering && CleanupFinished && !Abandoned && Result == Outcome.Pending;
    internal bool CanEnterMap => Result == Outcome.Succeeded && !Recovering && !Abandoned
        && !SceneLoading && !NavigationBuilding;

    internal void SceneLoadStarted() { SceneLoading = true; CleanupFinished = false; }
    internal void SceneLoadFinished() => SceneLoading = false;
    internal void NavigationStarted() { NavigationBuilding = true; CleanupFinished = false; }
    internal void NavigationFinished() => NavigationBuilding = false;
    internal bool BeginRecovery()
    {
        if (Result != Outcome.Pending || Recovering || Abandoned) return false;
        Recovering = true;
        return true;
    }
    internal bool Abandon()
    {
        Abandoned = true;
        return TrySettle(Outcome.Canceled);
    }
    internal bool FinishCleanup()
    {
        if (!CanDisposeResources) return false;
        CleanupFinished = true;
        return true;
    }
    internal bool TrySettle(Outcome result)
    {
        if (result == Outcome.Pending || Result != Outcome.Pending) return false;
        Result = result;
        return true;
    }
}
