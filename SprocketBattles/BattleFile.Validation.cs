namespace SprocketBattles;

public sealed partial class BattleFile
{
    /// Reject broken save data before menu rendering or native spawning can consume it. Missing fields retain
    /// their legacy defaults; explicit null entries and invalid coordinates are not usable defaults.
    public string? CheckData(bool forPlay = false)
    {
        static bool Items<T>(List<T>? items) where T : class => items != null && items.All(x => x != null);
        static bool Point(float[]? p, int size = 3) => p != null && p.Length == size && p.All(float.IsFinite);
        static bool OptionalPoint(float[]? p) => p == null || Point(p);
        static bool Number(float? n) => !n.HasValue || float.IsFinite(n.Value);
        static bool Unique(IEnumerable<string> ids) => ids.Where(id => !string.IsNullOrEmpty(id))
            .GroupBy(id => id, StringComparer.Ordinal).All(g => g.Count() == 1);
        const string invalid = "The battle file is invalid: ";
        if (Name == null || Map == null || Description == null || Objective == null || Failure == null || Clouds == null || Fog == null)
            return invalid + "a text field is null.";
        if (!Items(Units)) return invalid + "the tank list contains a null entry.";
        if (!Unique(Units.Select(u => u.Id))) return invalid + "two tanks have the same ID.";
        foreach (var u in Units)
        {
            if (u.Id == null || u.Blueprint == null || u.Control == null || u.Team < 0 || u.Team > 1)
                return invalid + "a tank's ID, blueprint, control or team is invalid.";
            if (forPlay && (string.IsNullOrWhiteSpace(u.Id) || string.IsNullOrWhiteSpace(u.Blueprint)))
                return invalid + "each tank needs an ID and a blueprint.";
            if (!Point(u.Position) || !float.IsFinite(u.Yaw)) return invalid + $"tank '{u.Id}' has an invalid position or facing.";
            if (u.Orders == null) continue;
            if (!Items(u.Orders)) return invalid + $"tank '{u.Id}' has a null order.";
            foreach (var order in u.Orders)
                if (order.Type == null || !Number(order.Face) || !OptionalPoint(order.To) || (order.Type == "move" && order.To == null))
                    return invalid + $"tank '{u.Id}' has an invalid movement order.";
        }
        if (Mission is { } m)
        {
            if (!Items(m.Zones) || !Items(m.Mines) || !Items(m.Obstacles) || !Items(m.Rules))
                return invalid + "a mission list contains a null entry.";
            if (!Unique(m.Zones.Select(z => z.Id))) return invalid + "two zones have the same ID.";
            foreach (var z in m.Zones)
                if (z.Id == null || z.Name == null || !Point(z.Center) || !float.IsFinite(z.Radius) || z.Radius <= 0)
                    return invalid + "a zone has an invalid centre or radius.";
            foreach (var mine in m.Mines)
                if (!Point(mine.Position) || !float.IsFinite(mine.Power) || mine.Power < 0)
                    return invalid + "a mine has an invalid position or power.";
            foreach (var obstacle in m.Obstacles)
                if (obstacle.Kind == null || !Point(obstacle.Position) || !float.IsFinite(obstacle.Yaw))
                    return invalid + "an obstacle has an invalid position or facing.";
            foreach (var r in m.Rules)
                if (r.When == null || !float.IsFinite(r.Seconds) || r.Seconds < 0 || r.Team < 0 || r.Team > 1 || r.Count < 0
                    || r.Then is not { } a || a.Do == null || a.Text == null || a.Team < 0 || a.Team > 1 || a.Shells < 0
                    || !float.IsFinite(a.Seconds) || a.Seconds < 0 || !float.IsFinite(a.Power) || a.Power < 0)
                    return invalid + "a mission rule has an invalid condition or action.";
        }
        if (Cinema is { } c)
        {
            if (c.Replay?.Check(Units.Select(u => u.Id)) is { } replayError) return replayError;
            if (!Items(c.Cameras) || !Items(c.Tanks) || !Items(c.Cuts)) return invalid + "a cinematic list contains a null entry.";
            if (!float.IsFinite(c.Speed) || c.Speed <= 0 || !Number(c.End) || c.End < 0)
                return invalid + "the cinematic has an invalid speed or end time.";
            foreach (var camera in c.Cameras)
            {
                if (camera.Name == null || !Items(camera.Keys)) return invalid + "a camera track has a null key.";
                foreach (var k in camera.Keys)
                    if (!float.IsFinite(k.Time) || k.Time < 0 || !Point(k.Position) || !Point(k.Rotation, 4)
                        || !float.IsFinite(k.Fov) || k.Fov <= 0 || k.Fov >= 180)
                        return invalid + "a camera key has an invalid pose, time or field of view.";
            }
            foreach (var track in c.Tanks)
            {
                if (track.Unit == null || !Items(track.Keys)) return invalid + "a tank track has a null key.";
                foreach (var k in track.Keys)
                    if (!float.IsFinite(k.Time) || k.Time < 0 || !Number(k.Turret) || !Number(k.Gun)
                        || !Number(k.Throttle) || !Number(k.Steer) || !OptionalPoint(k.DriveTo) || !OptionalPoint(k.AimPoint))
                        return invalid + "a tank key has invalid controls, coordinates or time.";
            }
            if (c.Cuts.Any(k => !float.IsFinite(k.Time) || k.Time < 0)) return invalid + "a camera cut has an invalid time.";
        }
        return null;
    }
}
