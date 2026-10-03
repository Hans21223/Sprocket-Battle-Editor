using System.Text.Json;
using Sprocket.Powertrains.Transmissions;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Engines;
using Sprocket.Vehicles.Powertrains;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Transmissions;
using UnityEngine;

namespace SprocketBattles;

/// How long a tank takes to drive somewhere. Each design's drive from a standstill at full throttle on level ground is
/// worked out with the game's own drivetrain maths (DriveSim, from Quality of Life) on a spawned tank's engine, gearbox
/// and tracks, once per design (in the background), and kept in BepInEx\config\SprocketBattles-travel.json until the
/// design's file changes. A point more than 80° off the tank's heading costs a turn on the spot first, as the game's
/// driver AI stops and pivots then (DriverAISettings: in past 80°, out under 40°). Straight lines: no way round
/// obstacles, slopes or the AI easing off on arrival; each real drive of a cinematic's "drive to" key corrects the
/// design's times from then on (Measured). The main turret's traverse (its top rate and acceleration) is read from the
/// spawned tank too, for how long an aim takes (TurnSeconds).
internal static class Travel
{
    internal sealed class Profile
    {
        public long Stamp { get; set; }               // the design file's last write time (UTC ticks)
        public float Top { get; set; }                // m/s, where the drive settles
        public float[] Distance { get; set; } = { };  // metres covered by each Step seconds from a standstill
        public float Traverse { get; set; }           // the main turret's top traverse rate, °/s (-1: no turret; 0: not read)
        public float TraverseAccel { get; set; }      // and its acceleration, °/s²
        public float Correction { get; set; } = 1;    // real drives over worked-out times, averaged
        public int Drives { get; set; }               // how many real drives that's from
    }

    const float Step = 0.25f;
    // ponytail: one pivot rate for every tank (neutral steer isn't worked out); measure turns if they're far off.
    const float PivotDegreesPerSecond = 20, PivotIn = 80, PivotOut = 40;

    static readonly string File = Path.Combine(BepInEx.Paths.ConfigPath, "SprocketBattles-travel.json");
    static Dictionary<string, Profile>? known;
    static readonly HashSet<string> working = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, Profile> Known()
    {
        if (known != null) return known;
        try { known = JsonSerializer.Deserialize<Dictionary<string, Profile>>(System.IO.File.ReadAllText(File)); } catch (Exception) { }
        return known = new Dictionary<string, Profile>(known ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    static long StampOf(string path) { try { return System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch (Exception) { return 0; } }

    // Whether a known drive's design file is unchanged, looked at most every 5 s (the panels ask every frame).
    static readonly Dictionary<string, (bool Fresh, float At)> stamps = new(StringComparer.OrdinalIgnoreCase);

    static Profile? Of(string blueprint)
    {
        var path = Files.Absolute(blueprint);
        Profile? p;
        lock (working) if (!Known().TryGetValue(path, out p)) return null;
        if (!stamps.TryGetValue(path, out var s) || Time.unscaledTime - s.At > 5) stamps[path] = s = (p.Stamp == StampOf(path), Time.unscaledTime);
        return s.Fresh ? p : null;
    }

    /// Whether the design's drive is known yet ("working": being worked out).
    internal static string State(string blueprint)
    {
        if (Of(blueprint) != null) return "known";
        lock (working) return working.Contains(Files.Absolute(blueprint)) ? "working" : "unknown";
    }

    internal static float? TopSpeed(string blueprint) => Of(blueprint)?.Top;

    /// The design's real drives measured so far (they correct its times).
    internal static int DrivesMeasured(string blueprint) => Of(blueprint)?.Drives ?? 0;

    static void SaveAll()
    {
        lock (working) try { System.IO.File.WriteAllText(File, JsonSerializer.Serialize(known)); } catch (Exception) { }
    }

    /// A spawned tank of a design: its drive worked out (in the background) unless already known, its turret read.
    internal static void Learn(string blueprint, IVehicleGateway gateway, float mass)
    {
        var path = Files.Absolute(blueprint);
        string name = Path.GetFileNameWithoutExtension(path);
        if (Of(blueprint) is { } have)
        {
            if (have.Traverse == 0) // known from before turrets were read
            {
                (have.Traverse, have.TraverseAccel) = Turret(gateway);
                SaveAll();
                Trace.Write($"travel: {name}'s turret: {(have.Traverse > 0 ? $"{have.Traverse:0.0}°/s" : "none")}");
            }
            return;
        }
        lock (working) if (!working.Add(path)) return;
        DriveSim.Vehicle? vehicle = null;
        (float Rate, float Accel) turret = (0, 0);
        try { vehicle = VehicleOf(gateway, mass); turret = Turret(gateway); }
        catch (Exception ex) { Trace.Write($"travel: couldn't read {name}'s drivetrain: {ex.Message}"); }
        if (vehicle == null) { lock (working) working.Remove(path); return; }
        long stamp = StampOf(path);
        Task.Run(() =>
        {
            try
            {
                var run = DriveSim.Accelerate(vehicle);
                var distance = new List<float> { 0 };
                float covered = 0, speed = 0, next = Step;
                foreach (var p in run)
                {
                    covered += (speed + p.Speed) / 2 * vehicle.FixedDeltaTime;
                    speed = p.Speed;
                    if (p.Time >= next - 1e-4f) { distance.Add(covered); next += Step; }
                }
                var profile = new Profile { Stamp = stamp, Top = Math.Max(0.1f, speed), Distance = distance.ToArray(), Traverse = turret.Rate, TraverseAccel = turret.Accel };
                lock (working) Known()[path] = profile;
                SaveAll();
                Trace.Write($"travel: {name} tops out at {profile.Top * 3.6f:0.0} km/h; 100 m in {Seconds(profile, 100):0.0} s, " +
                            $"500 m in {Seconds(profile, 500):0.0} s; turret {(turret.Rate > 0 ? $"{turret.Rate:0.0}°/s, {turret.Accel:0}°/s²" : "none")}");
            }
            catch (Exception ex) { Trace.Write($"travel: working out {Path.GetFileNameWithoutExtension(path)}'s drive failed: {ex.Message}"); }
            finally { lock (working) working.Remove(path); }
        });
    }

    static float Seconds(Profile p, float distance)
    {
        var d = p.Distance;
        if (distance >= d[^1]) return (d.Length - 1) * Step + (distance - d[^1]) / p.Top;
        int i = Array.FindIndex(d, x => x >= distance);
        if (i <= 0) return 0;
        return (i - 1 + (distance - d[i - 1]) / Math.Max(1e-4f, d[i] - d[i - 1])) * Step;
    }

    static float Metres(Profile p, float seconds)
    {
        var d = p.Distance;
        float at = seconds / Step;
        if (at >= d.Length - 1) return d[^1] + (seconds - (d.Length - 1) * Step) * p.Top;
        int i = (int)at;
        return d[i] + (d[i + 1] - d[i]) * (at - i);
    }

    /// Seconds for a tank of the design to drive from `from`, heading `yaw` (degrees), to `to`: the turn on the spot if
    /// the point is far off its heading, then the drive from a standstill, corrected by its real drives (not if `raw`).
    /// Null while the design's drive isn't known.
    internal static float? Seconds(string blueprint, Vector3 from, float yaw, Vector3 to, bool raw = false)
    {
        if (Of(blueprint) is not { } p) return null;
        var way = to - from; way.y = 0;
        float off = Math.Abs(Mathf.DeltaAngle(yaw, Heading(way)));
        float turn = off > PivotIn ? (off - PivotOut) / PivotDegreesPerSecond : 0;
        return (turn + Seconds(p, way.magnitude)) * (raw ? 1 : p.Correction);
    }

    /// A real drive: `actual` seconds where the uncorrected estimate said `estimate`. Its design's times are corrected
    /// by the average of these (the last ten count; each between half and three times).
    internal static void Measured(string blueprint, float estimate, float actual)
    {
        if (estimate < 2 || Of(blueprint) is not { } p) return;
        int n = Math.Min(p.Drives, 9);
        p.Correction = (p.Correction * n + Math.Clamp(actual / estimate, 0.5f, 3)) / (n + 1);
        p.Drives++;
        SaveAll();
        Trace.Write($"travel: {Path.GetFileNameWithoutExtension(blueprint)}'s drives take {p.Correction:0.00}x the worked-out time ({p.Drives} measured)");
    }

    /// Seconds for the design's main turret to turn `degrees`: speeding up and slowing down at its acceleration, at its
    /// top rate between. Null while not known (or with no turret: TurretState).
    internal static float? TurnSeconds(string blueprint, float degrees)
    {
        if (Of(blueprint) is not { Traverse: > 0 } p) return null;
        float v = p.Traverse, a = p.TraverseAccel;
        if (a <= 0) return degrees / v;
        return degrees >= v * v / a ? degrees / v + v / a : 2 * MathF.Sqrt(degrees / a);
    }

    /// "known" (with its rate), "none" (no turret) or "unknown".
    internal static (string State, float Rate) TurretState(string blueprint) =>
        Of(blueprint) is { } p ? (p.Traverse > 0 ? "known" : p.Traverse < 0 ? "none" : "unknown", p.Traverse) : ("unknown", 0);

    /// How far along from `from` to `to` the tank has got after `seconds` (0 to 1, turning first as Seconds does).
    internal static float Progress(string blueprint, Vector3 from, float yaw, Vector3 to, float seconds)
    {
        if (Of(blueprint) is not { } p) return 0;
        seconds /= p.Correction;
        var way = to - from; way.y = 0;
        if (way.magnitude < 0.01f) return 1;
        float off = Math.Abs(Mathf.DeltaAngle(yaw, Heading(way)));
        float driving = seconds - (off > PivotIn ? (off - PivotOut) / PivotDegreesPerSecond : 0);
        return driving <= 0 ? 0 : Math.Min(1, Metres(p, driving) / way.magnitude);
    }

    internal static float Heading(Vector3 way) => Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg;

    // ---------- the design's drivetrain, as Quality of Life's speed panel reads it ----------

    static float Try(Func<float> read, float fallback = 0) { try { return read(); } catch { return fallback; } }

    const float Rads = MathF.PI / 30; // rpm -> rad/s

    static List<VehicleComponent> Parts(IVehicleGateway gateway)
    {
        var parts = new List<VehicleComponent>();
        var items = gateway.ObjectReader.Items;
        int count = items.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleObject>>().Count;
        for (int i = 0; i < count; i++)
        {
            var components = items[i]?.Components;
            if (components == null) continue;
            int n = components.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleComponent>>().Count;
            for (int k = 0; k < n; k++) if (components[k] != null) parts.Add(components[k]);
        }
        return parts;
    }

    /// The first turret's traverse: top rate and acceleration (°/s, °/s²); (-1, 0) with no turret.
    static (float Rate, float Accel) Turret(IVehicleGateway gateway)
    {
        foreach (var c in Parts(gateway))
            if (c.TryCast<Sprocket.Vehicles.Turrets.TraverseMotor>()?.behaviour?.TryCast<Sprocket.Vehicles.Weapons.TurretBehaviour>() is { } t)
            {
                float rate = t.MaxTraverseRate, accel = t.MaxAcceleration;
                // ponytail: the unit is told from the size (under 2: rad/s, as no turret does 115°/s); the trace shows it.
                if (rate > 0 && rate < 2) { rate *= Mathf.Rad2Deg; accel *= Mathf.Rad2Deg; }
                return rate > 0 ? (rate, accel) : (-1, 0);
            }
        return (-1, 0);
    }

    static DriveSim.Vehicle? VehicleOf(IVehicleGateway gateway, float mass)
    {
        var parts = Parts(gateway);
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        var engine = (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        var gearbox = gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        var tracks = parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).Select(t => t!).ToList();
        if (engine == null || gearbox == null || tracks.Count == 0 || mass <= 0) return null;
        var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        float finalDrive = tracks[0].BlueprintSlot.Blueprint.FinalDriveRatio;
        float radius = Try(() => tracks[0].SprocketAssembly.BeltWrapRadius);
        if (ratios.Length == 0 || radius <= 0 || finalDrive <= 0 || engine.MaxRPM <= 0 || engine.MaxTorque <= 0) return null;

        float revLimit = Try(() => engine.RevLimit);
        if (revLimit <= 0 || revLimit > engine.MaxRPM) revLimit = engine.MaxRPM;
        var e = DriveSim.Engine.Of(engine.MaxTorque, engine.MaxRPM * Rads, Try(() => engine.Inertia), revLimit * Rads,
            Try(() => engine.FrictionCoefficient), Try(() => engine.Downshift) * Rads, engine.IdleRPM * Rads);
        var (disengage, engage) = ShiftTimes(gearbox);
        float Tech(TrackAssembly t, string key) => Try(() => t.TrackTech?.GetFloat(key, 1) ?? 1, 1);
        float Segment(TrackAssembly t) => Try(() => t.Belt.SegmentMass);
        float PerSide(Func<TrackAssembly, float> f) => tracks.Sum(f) / 2;
        var sprocket = new DriveSim.Sprocket(finalDrive,
            PerSide(t => Tech(t, "bendingResistanceCoefficient") * Segment(t) * Segment(t)),
            PerSide(t => Tech(t, "viscousDragCoefficient") * (0.25f * Segment(t) + 3)),
            PerSide(t => Try(() => t.ComputeSprocketInertia())), 0.8f, radius);
        float rolling = Tech(tracks[0], "rollingResistanceCoefficient");
        float upshift = Math.Min(engine.Upshift, revLimit - 25) * Rads;
        float torque = engine.MaxTorque, step = Try(() => Time.fixedDeltaTime, 0.01f);
        return new DriveSim.Vehicle(e, ratios, disengage, engage, 5 * torque, 1.5f * torque, upshift, sprocket, mass, 0.03f * rolling, 0.001f * rolling,
            FixedDeltaTime: step > 0 ? step : 0.01f);
    }

    static (float Disengage, float Engage) ShiftTimes(TransmissionBlock gearbox)
    {
        try
        {
            return TransmissionMeshTypes.ParseMeshType(gearbox.Blueprint.meshType) switch
            {
                TransmissionMeshType.Synchromesh => (TransmissionBehaviour.SynchromeshDisengageTime, TransmissionBehaviour.SynchromeshEngageTime),
                TransmissionMeshType.ConstantMesh => (TransmissionBehaviour.ConstantMeshDisengageTime, TransmissionBehaviour.ConstantMeshEngageTime),
                _ => (TransmissionBehaviour.SlidingMeshDisengageTime, TransmissionBehaviour.SlidingMeshEngageTime),
            };
        }
        catch { return (0, 0); }
    }
}
