using SprocketMaps;

static class CustomMapLifetimeTests
{
    static void Check(bool value, string reason)
    { if (!value) throw new Exception("custom map lifetime: " + reason); }

    internal static void Run()
    {
        var abandoned = new CustomMapLifetime();
        abandoned.SceneLoadStarted();
        Check(abandoned.Abandon(), "leaving during a load settles the native awaited task");
        Check(!abandoned.Abandon() && !abandoned.TrySettle(CustomMapLifetime.Outcome.Succeeded),
            "late callbacks cannot settle the same task twice or turn cancellation into success");
        Check(!abandoned.FinishCleanup() && !abandoned.CanResumeNative,
            "an uncancelable additive load keeps ownership until its late completion");
        abandoned.SceneLoadFinished();
        Check(abandoned.FinishCleanup() && !abandoned.CanResumeNative,
            "a disposed abandoned session never spawns native vehicles");

        var rejected = new CustomMapLifetime();
        rejected.SceneLoadStarted();
        Check(rejected.BeginRecovery() && !rejected.BeginRecovery(), "environment rejection enters recovery once");
        Check(!rejected.FinishCleanup() && !rejected.CanResumeNative, "fallback waits for the scene load to finish");
        rejected.SceneLoadFinished();
        rejected.NavigationStarted();
        Check(!rejected.FinishCleanup() && !rejected.CanResumeNative, "fallback also waits for navigation cancellation");
        rejected.NavigationFinished();
        Check(rejected.FinishCleanup() && rejected.CanResumeNative, "fallback starts only after owned work is gone");
        Check(rejected.TrySettle(CustomMapLifetime.Outcome.Succeeded) && !rejected.CanResumeNative,
            "completed fallback cannot run native initiation again");
        Check(!rejected.CanEnterMap, "a running recovery carrier cannot open the editor before returning to the menu");
        Check(!abandoned.CanEnterMap, "an abandoned scene never becomes an editable custom map");

        var ready = new CustomMapLifetime();
        Check(ready.TrySettle(CustomMapLifetime.Outcome.Succeeded) && !ready.BeginRecovery(),
            "a successful session is terminal and may be replaced by a later custom load");
        Check(ready.CanEnterMap, "only completed custom initialization enables editor entry");
        ready.Abandon();
        Check(!ready.CanEnterMap, "scene exit revokes readiness even after successful initialization");
        var surface = new CustomMapNavigation(new[]
        {
            new CustomMapNavigation.Vertex(-100, 0, -100), new CustomMapNavigation.Vertex(100, 20, -100),
            new CustomMapNavigation.Vertex(100, 20, 100), new CustomMapNavigation.Vertex(-100, 0, 100),
            new CustomMapNavigation.Vertex(-100, 100, -100), new CustomMapNavigation.Vertex(100, 100, -100),
            new CustomMapNavigation.Vertex(100, 100, 100), new CustomMapNavigation.Vertex(-100, 100, 100),
        }, new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, -1, 8, 2 });
        Check(surface.Any && Math.Abs(surface.Height(0, 0, 0)!.Value - 10) < 0.0001,
            "native triangles interpolate actual sloped ground height");
        Check(surface.Height(0, 0, 90) == 100 && surface.Height(200, 0, 0) == null,
            "stacked navigation layers follow collider height and missing ground remains unsupported");
        Check(surface.Height(float.NaN, 0, 0) == null && surface.Height(100, -100, 0) == 20,
            "invalid coordinates are rejected while shared triangle edges remain supported");
        Console.WriteLine("CUSTOM_MAP_LIFETIME_TESTS_OK: cancellation, delayed cleanup, recovery, exactly-once completion");
    }
}
