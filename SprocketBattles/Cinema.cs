using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

/// The cinematic's maths: where a camera is at a time (a smooth curve through its keys: Catmull-Rom for the path,
/// turning and zooming evenly between keys), which camera is shown (the last cut before then), and the tanks' poses
/// (turret, gun, drive, shots) given to force control. A camera that follows a tank keeps its keys around that tank,
/// turning with the tank's heading only (not its rocking).
internal static class Cinema
{
    internal readonly record struct Pose(Vector3 Position, Quaternion Rotation, float Fov);

    /// Where a tank is for a following camera: its position and heading (from the battle, or the editor's marker).
    internal delegate (Vector3 At, float Yaw)? Anchor(string unit);

    internal static Pose? CameraAt(CameraTrack track, float t, Anchor anchor)
    {
        var keys = track.Keys;
        if (keys.Count == 0) return null;
        Pose local;
        if (keys.Count == 1 || t <= keys[0].Time) local = Of(keys[0]);
        else if (t >= keys[^1].Time) local = Of(keys[^1]);
        else
        {
            int i = keys.FindLastIndex(k => k.Time <= t);
            var a = keys[i]; var b = keys[i + 1];
            float u = (t - a.Time) / Math.Max(1e-4f, b.Time - a.Time);
            var p0 = Files.Vector(keys[Math.Max(0, i - 1)].Position);
            var p3 = Files.Vector(keys[Math.Min(keys.Count - 1, i + 2)].Position);
            var position = CatmullRom(p0, Files.Vector(a.Position), Files.Vector(b.Position), p3, u);
            local = new Pose(position, Quaternion.Slerp(Rotation(a), Rotation(b), Smooth(u)), Mathf.Lerp(a.Fov, b.Fov, Smooth(u)));
        }
        var pose = track.Follow != null && anchor(track.Follow) is { } frame
            ? new Pose(frame.At + Quaternion.Euler(0, frame.Yaw, 0) * local.Position, Quaternion.Euler(0, frame.Yaw, 0) * local.Rotation, local.Fov)
            : local;
        // Looking at a tank: pointed at its middle (about the height of a hull), level (no roll).
        if (track.LookAt != null && anchor(track.LookAt) is { } target)
        {
            var toward = target.At + Vector3.up * LookHeight - pose.Position;
            if (toward.sqrMagnitude > 0.01f) pose = pose with { Rotation = Quaternion.LookRotation(toward, Vector3.up) };
        }
        return pose;
    }

    const float LookHeight = 1.5f;

    /// A key's pose in the world, for the camera following `follow` (or none).
    internal static Pose World(CamKey k, string? follow, Anchor anchor)
    {
        var local = Of(k);
        if (follow == null || anchor(follow) is not { } frame) return local;
        var turn = Quaternion.Euler(0, frame.Yaw, 0);
        return new Pose(frame.At + turn * local.Position, turn * local.Rotation, local.Fov);
    }

    /// The camera now follows `follow` (or nothing): its keys are moved into the new frame, so each stays where it was
    /// seen (in the editor, against the tanks' markers).
    internal static void Refollow(CameraTrack track, string? follow, Anchor anchor)
        => Refollow(track, follow, (id, time) => anchor(id));

    internal static void Refollow(CameraTrack track, string? follow, Func<string, float, (Vector3 At, float Yaw)?> anchor)
    {
        var world = track.Keys.Select(k => World(k, track.Follow, id => anchor(id, k.Time))).ToList();
        track.Follow = follow;
        for (int i = 0; i < track.Keys.Count; i++)
        {
            float time = track.Keys[i].Time;
            track.Keys[i] = Key(time, world[i].Position, world[i].Rotation, world[i].Fov, track, id => anchor(id, time));
        }
    }

    /// The key for a view: in the world, or around the followed tank.
    internal static CamKey Key(float time, Vector3 position, Quaternion rotation, float fov, CameraTrack track, Anchor anchor)
    {
        if (track.Follow != null && anchor(track.Follow) is { } frame)
        {
            var back = Quaternion.Inverse(Quaternion.Euler(0, frame.Yaw, 0));
            position = back * (position - frame.At);
            rotation = back * rotation;
        }
        return new CamKey { Time = time, Position = Files.Array(position), Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, Fov = fov };
    }

    static Pose Of(CamKey k) => new(Files.Vector(k.Position), Rotation(k), k.Fov);

    internal static Quaternion Rotation(CamKey k) =>
        k.Rotation is { Length: 4 } r && r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3] > 0.5f ? new Quaternion(r[0], r[1], r[2], r[3]).normalized : Quaternion.identity;

    static float Smooth(float u) => u * u * (3 - 2 * u);

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return 0.5f * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 + (3 * p1 - p0 - 3 * p2 + p3) * u3);
    }

    /// The camera shown at `t`: the last cut at or before it (the first camera before any cut).
    internal static int Shown(CinemaData data, float t)
    {
        int camera = 0;
        foreach (var cut in data.Cuts.OrderBy(c => c.Time)) if (cut.Time <= t) camera = cut.Camera;
        return Math.Clamp(camera, 0, Math.Max(0, data.Cameras.Count - 1));
    }

    // ---------- tanks ----------

    /// A tank's pose at `t`: the aim and drive of the last key at or before then (a new key starts as a copy of the one
    /// before, so a key left to the AI hands it back), angles eased toward the next key if that one sets angles too.
    /// Null: its AI's.
    internal sealed record TankPose(TankKey? Aim, float? Turret, float? Gun, TankKey? Drive);

    internal static TankPose TankAt(TankTrack track, float t)
    {
        int i = track.Keys.FindLastIndex(k => k.Time <= t);
        if (i < 0) return new(null, null, null, null);
        var key = track.Keys[i];
        var nextAim = i + 1 < track.Keys.Count && track.Keys[i + 1].Aims ? track.Keys[i + 1] : null;
        TankKey? aim = key.Aims ? key : null, drive = key.Drives ? key : null;
        float? turret = null, gun = null;
        if (aim != null && aim.AimPoint == null && aim.AimUnit == null && aim.ShootAt == null)
        {
            turret = aim.Turret ?? 0; gun = aim.Gun ?? 0;
            if (nextAim is { AimPoint: null, AimUnit: null, ShootAt: null })
            {
                float u = Smooth((t - aim.Time) / Math.Max(1e-4f, nextAim.Time - aim.Time));
                turret = Mathf.LerpAngle(turret.Value, nextAim.Turret ?? 0, u);
                gun = Mathf.Lerp(gun.Value, nextAim.Gun ?? 0, u);
            }
        }
        return new(aim, turret, gun, drive);
    }

    // While a cinematic plays in a battle: each keyed tank under force control (its aim and manual drive), its "drive
    // to" points given to its AI, its shots fired as their keys pass.
    static readonly Dictionary<string, float> firedUpTo = new();
    static readonly Dictionary<string, TankKey> driveGiven = new();

    internal static void Tanks(CinemaData data, float t, Func<string, VehicleBehaviour?> tank)
    {
        foreach (var track in data.Tanks)
        {
            if (track.Keys.Count == 0 || t < track.Keys[0].Time || tank(track.Unit) is not { } v) continue;
            var pose = TankAt(track, t);
            var p = Puppet.Take(v, track.Unit);
            var a = pose.Aim;
            string? targetId = a?.ShootAt ?? a?.AimUnit;
            var target = targetId != null ? tank(targetId) : null;
            bool aim = a != null, manualDrive = pose.Drive is { DriveTo: null }, holdFire = track.Keys.Any(k => k.Fire || k.ShootAt != null);
            if (p.Drive != manualDrive || p.Aim != aim || p.HoldFire != holdFire) { p.Drive = manualDrive; p.Aim = aim; p.HoldFire = holdFire; Puppet.Changed(); }
            p.Throttle = pose.Drive?.Throttle ?? 0; p.Steer = pose.Drive?.Steer ?? 0;
            p.Turret = pose.Turret ?? 0; p.Gun = pose.Gun ?? 0;
            p.AimAt = a?.AimPoint is { } point ? Files.Vector(point) : target != null ? target.Position + Vector3.up * 1.5f : null;
            var shootAt = a?.ShootAt != null && target != null && !Battle.Out(target) ? target : null;
            if (shootAt?.Pointer != p.ShootAt?.Pointer) p.ReadySince.Clear(); // a new target: each gun waits for its aim again
            p.ShootAt = shootAt;
            // Drive to a point: the AI drives there (given once per key).
            if (pose.Drive is { DriveTo: { } to } key && (!driveGiven.TryGetValue(track.Unit, out var given) || given != key))
            {
                driveGiven[track.Unit] = key;
                var face = Files.Vector(to) - v.Position; face.y = 0;
                Battle.SendTo(v, Files.Vector(to), face.sqrMagnitude > 1 ? face.normalized : v.transform.forward);
                var blueprint = Battle.Playing?.Units.FirstOrDefault(u => u.Id == track.Unit)?.Blueprint;
                if (!driving.TryGetValue(track.Unit, out var under) || under.To != Files.Vector(to)) // not a copy of the key before
                    driving[track.Unit] = (Files.Vector(to), t, blueprint, blueprint == null ? null : Travel.Seconds(blueprint, v.Position, v.transform.eulerAngles.y, Files.Vector(to), raw: true));
            }
            else if (pose.Drive is not { DriveTo: not null }) driving.Remove(track.Unit);
            // How long it took against the estimate (Travel), in the trace.
            if (driving.TryGetValue(track.Unit, out var d))
            {
                var gap = d.To - v.Position; gap.y = 0;
                if (gap.magnitude < 3)
                {
                    driving.Remove(track.Unit);
                    Trace.Write($"cinematic: {track.Unit} got to its drive-to point in {t - d.Since:0.0} s{(d.Estimate is { } e ? $" (worked out {e:0.0} s)" : "")}");
                    if (d.Blueprint != null && d.Estimate is { } worked) Travel.Measured(d.Blueprint, worked, t - d.Since); // corrects its times
                }
            }
            float from = firedUpTo.TryGetValue(track.Unit, out var f) ? f : -1;
            p.Shots += track.Keys.Count(k => k.Fire && k.Time > from && k.Time <= t);
            firedUpTo[track.Unit] = t;
        }
    }

    static readonly Dictionary<string, (Vector3 To, float Since, string? Blueprint, float? Estimate)> driving = new();

    /// How far the turret turns for a key's aim (degrees, from where the tank was placed): from where the key before it
    /// with an aim left the turret (or the hull's front) to where this key aims. Null if the key leaves the aim to its AI.
    internal static float? AimTurn(BattleUnit unit, TankTrack track, TankKey key, Func<string, BattleUnit?> unitOf)
    {
        var at = Files.Vector(unit.Position);
        float? Yaw(TankKey k)
        {
            if (k.AimPoint is { } point) return Travel.Heading(Files.Vector(point) - at);
            if ((k.ShootAt ?? k.AimUnit) is { } id) return unitOf(id) is { } target ? Travel.Heading(Files.Vector(target.Position) - at) : null;
            if (k.Turret != null || k.Gun != null) return unit.Yaw + (k.Turret ?? 0);
            return null;
        }
        if (Yaw(key) is not { } to) return null;
        float from = unit.Yaw;
        for (int i = track.Keys.IndexOf(key) - 1; i >= 0; i--) if (Yaw(track.Keys[i]) is { } before) { from = before; break; }
        return Math.Abs(Mathf.DeltaAngle(from, to));
    }

    internal static void Reset() { firedUpTo.Clear(); driveGiven.Clear(); driving.Clear(); }

    /// A tank's "drive to" keys, estimated (Travel): when it gets there (null while its design's drive isn't known),
    /// how far it is, and when a later key ends the drive (a copy of the key carries it on).
    internal readonly record struct Leg(float? Arrives, float Metres, float Ends);

    internal static Dictionary<TankKey, Leg> Legs(BattleUnit unit, TankTrack track)
    {
        var legs = new Dictionary<TankKey, Leg>();
        Vector3 at = Files.Vector(unit.Position);
        float yaw = unit.Yaw;
        var under = new List<TankKey>(); // the keys of the drive under way (the first starts it)
        void End(float time)
        {
            if (under.Count == 0) return;
            var first = under[0];
            foreach (var k in under) legs[k] = legs[first] with { Ends = time };
            // Where it has got to by then, facing the way it went.
            var to = Files.Vector(first.DriveTo!);
            var way = to - at;
            float done = Travel.Progress(unit.Blueprint, at, yaw, to, time - first.Time);
            at += way * done;
            if (done > 0 && way.sqrMagnitude > 0.01f) yaw = Travel.Heading(way);
            under.Clear();
        }
        foreach (var k in track.Keys)
        {
            if (under.Count > 0 && k.DriveTo is { } same && Files.Vector(same) == Files.Vector(under[0].DriveTo!)) { under.Add(k); continue; }
            End(k.Time);
            if (k.DriveTo is not { } point) continue;
            var dest = Files.Vector(point);
            float metres = Vector3.Distance(new Vector3(at.x, 0, at.z), new Vector3(dest.x, 0, dest.z));
            legs[k] = new Leg(Travel.Seconds(unit.Blueprint, at, yaw, dest) is { } s ? k.Time + s : null, metres, float.PositiveInfinity);
            under.Add(k);
        }
        foreach (var k in under) legs[k] = legs[under[0]];
        return legs;
    }
}
