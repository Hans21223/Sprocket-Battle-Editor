using HarmonyLib;
using Sprocket.ArtificialIntelligence;
using Sprocket.VehicleControl;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

/// Force control: any AI tank's driving, turret, gun and trigger taken from its AI and set by you (the command view) or
/// the cinematic's tank keys. Its AI keeps seeing and deciding, but its driver, gun layers and gunners skip their turns
/// (hooks below), and every frame the tank gets the drive (throttle, steering) and aim (a turret angle from the hull's
/// front and a gun elevation, or a point) set here, through the same controls the AI uses.
internal static class Puppet
{
    internal sealed class State
    {
        public VehicleBehaviour Tank = null!;
        public bool Drive, Aim, HoldFire;  // what's taken from the AI (HoldFire: its gunners don't fire on their own)
        public float Throttle, Steer;      // -1 to 1
        public float Turret, Gun;          // degrees: turret from the hull's front (+ right), gun elevation (+ up)
        public Vector3? AimAt;             // a point to aim at instead of the angles
        public int Shots;                  // shots asked for, fired as the guns can
        public VehicleBehaviour? ShootAt;  // fired at whenever a gun is loaded and on it (until cleared)
        public readonly Dictionary<int, float> ReadySince = new(); // each gunner: since when it's been loaded, for ShootAt
        public string Name = "";
    }

    static readonly Dictionary<IntPtr, State> puppets = new();
    static readonly Dictionary<IntPtr, VehicleBehaviour> reserves = new();
    static readonly HashSet<IntPtr> drivers = new(), layers = new(), gunners = new();

    internal static IEnumerable<State> All => puppets.Values;

    internal static State? Of(VehicleBehaviour tank) => tank != null && puppets.TryGetValue(tank.Pointer, out var s) ? s : null;

    /// The tank's controls from now on (its turret and gun where they point now, stopped).
    internal static State Take(VehicleBehaviour tank, string name)
    {
        if (Of(tank) is { } known) return known;
        var state = new State { Tank = tank, Name = name, Drive = true, Aim = true, HoldFire = true };
        puppets[tank.Pointer] = state;
        Index();
        Trace.Write($"force control: {name} taken from its AI");
        return state;
    }

    internal static void Release(VehicleBehaviour tank)
    {
        if (tank == null || !puppets.Remove(tank.Pointer, out var s)) return;
        try { if (Commander(tank)?.driver?.target is { } drive) drive.Solution = new DriveSolution(0, 0); }
        catch (Exception) { }
        Index();
        Trace.Write($"force control: {s.Name} back to its AI");
    }

    internal static void ReleaseAll()
    {
        foreach (var s in puppets.Values.ToList()) Release(s.Tank);
        puppets.Clear();
        Index();
    }

    static CommanderAI? Commander(VehicleBehaviour tank) => tank?.OrderReciever?.TryCast<CommanderAI>();

    // A battle's tanks set to keep their turret still or never fire (BattleUnit.NoTurret / NoFire): their gun layers or
    // gunners skip their turns all battle. Force control still aims and fires them (it sets the guns itself).
    static readonly Dictionary<IntPtr, (VehicleBehaviour Tank, bool Turret, bool Fire)> locks = new();

    internal static void Lock(VehicleBehaviour tank, bool turret, bool fire) { locks[tank.Pointer] = (tank, turret, fire); Index(); }
    internal static void Unlock(VehicleBehaviour tank) { if (locks.Remove(tank.Pointer)) Index(); }
    internal static void Unlock() { locks.Clear(); Index(); }

    // Reserves stay constructed at their native pose, but no driver or gun crew may act until arrival.
    internal static void ReserveHold(VehicleBehaviour tank, bool waiting)
    {
        if (waiting) reserves[tank.Pointer] = tank;
        else reserves.Remove(tank.Pointer);
        Index();
    }

    /// Which AI parts skip their turns: the driver if the drive is taken, gun layers if the aim is, gunners if either
    /// the aim is or they hold fire; and the locked tanks' gun layers and gunners.
    static void Index()
    {
        drivers.Clear(); layers.Clear(); gunners.Clear();
        foreach (var tank in reserves.Values)
        {
            var ai = tank == null ? null : Commander(tank);
            if (ai == null) continue;
            if (ai.driver != null) drivers.Add(ai.driver.Pointer);
            if (ai.gunLayers is { } gl) for (int i = 0; i < gl.Length; i++) if (gl[i] != null) layers.Add(gl[i].Pointer);
            if (ai.gunners is { } g) for (int i = 0; i < g.Length; i++) if (g[i] != null) gunners.Add(g[i].Pointer);
        }
        foreach (var (tank, turret, fire) in locks.Values)
        {
            var ai = tank == null ? null : Commander(tank);
            if (ai == null) continue;
            if (turret && ai.gunLayers is { } gl) for (int i = 0; i < gl.Length; i++) if (gl[i] != null) layers.Add(gl[i].Pointer);
            if (fire && ai.gunners is { } g) for (int i = 0; i < g.Length; i++) if (g[i] != null) gunners.Add(g[i].Pointer);
        }
        foreach (var s in puppets.Values)
        {
            var ai = s.Tank == null ? null : Commander(s.Tank);
            if (ai == null) continue;
            if (s.Drive && ai.driver != null) drivers.Add(ai.driver.Pointer);
            if (s.Aim && ai.gunLayers is { } gl) for (int i = 0; i < gl.Length; i++) if (gl[i] != null) layers.Add(gl[i].Pointer);
            if ((s.Aim || s.HoldFire) && ai.gunners is { } g) for (int i = 0; i < g.Length; i++) if (g[i] != null) gunners.Add(g[i].Pointer);
        }
    }

    /// After changing what a puppet takes (Drive, Aim, HoldFire).
    internal static void Changed() => Index();

    internal static bool SkipsDriver(IntPtr ai) => drivers.Contains(ai);
    internal static bool SkipsLayer(IntPtr ai) => layers.Contains(ai);
    internal static bool SkipsGunner(IntPtr ai) => gunners.Contains(ai);

    static void Shoot(State s, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GunnerAI> gunners)
    {
        var target = s.ShootAt!.Position + Vector3.up * 1.5f;
        for (int i = 0; i < gunners.Length; i++)
        {
            var gunner = gunners[i]?.Gunner;
            if (gunner == null || !gunner.Functioning || !gunner.CanFire) { s.ReadySince.Remove(i); continue; }
            if (!s.ReadySince.TryGetValue(i, out var since)) s.ReadySince[i] = since = Time.time;
            bool onTarget = false;
            try
            {
                var barrel = gunner.WeaponGameObject?.transform.forward;
                if (barrel is { } b) onTarget = Vector3.Angle(b, target - gunner.FirePosition) < 2;
            }
            catch (Exception) { }
            if (!onTarget && Time.time - since < 4) continue; // not on it after 4 s loaded: fired anyway
            gunner.Fire();
            s.ReadySince.Remove(i);
            Trace.Write($"force control: {s.Name} fired at its target{(onTarget ? "" : " (aim not confirmed)")}");
        }
    }

    /// Where the guns point for a turret angle and gun elevation on this tank (world direction).
    internal static Vector3 Direction(VehicleBehaviour tank, float turret, float gun) =>
        tank.transform.rotation * (Quaternion.Euler(-gun, turret, 0) * Vector3.forward);

    /// Every frame: each puppet's drive and aim set, shots fired. Gone tanks let go.
    internal static void Update()
    {
        if (puppets.Count == 0) return;
        foreach (var s in puppets.Values.ToList())
        {
            if (s.Tank == null) { puppets.Remove(puppets.First(p => p.Value == s).Key); Index(); continue; }
            var ai = Commander(s.Tank);
            if (ai == null) continue;
            if (s.Drive && ai.driver?.target is { } drive) drive.Solution = new DriveSolution(Math.Clamp(s.Steer, -1, 1), Math.Clamp(s.Throttle, -1, 1));
            if (s.Aim && ai.gunLayers is { } gl)
            {
                var dir = Direction(s.Tank, s.Turret, s.Gun);
                for (int i = 0; i < gl.Length; i++)
                {
                    var layer = gl[i]?.Layer;
                    if (layer == null) continue;
                    if (s.AimAt is { } point) layer.AimAtPosition(point, GunLayerAimFlags.None);
                    else layer.AimAtDirection(dir, GunLayerAimFlags.None);
                }
            }
            if (s.ShootAt != null && ai.gunners is { } sg) Shoot(s, sg);
            else s.ReadySince.Clear();
            if (s.Shots > 0 && ai.gunners is { } g)
            {
                bool fired = false;
                for (int i = 0; i < g.Length; i++)
                {
                    var gunner = g[i]?.Gunner;
                    if (gunner == null || !gunner.Functioning || !gunner.CanFire) continue;
                    gunner.Fire();
                    fired = true;
                }
                if (fired) { s.Shots--; Trace.Write($"force control: {s.Name} fired"); }
            }
        }
    }
}

/// The AI's driver, gun layers and gunners skip their turns on tanks under force control (Puppet). None of these
/// takes a CancellationToken by value (a hook on such a method crashed the game).
[HarmonyPatch]
internal static class PuppetHooks
{
    [HarmonyPrefix, HarmonyPatch(typeof(DriverAI), nameof(DriverAI.Step))]
    static bool Driver(DriverAI __instance)
    {
        try { return !Puppet.SkipsDriver(__instance.Pointer); }
        catch (Exception) { return true; }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GunLayerAI), nameof(GunLayerAI.Step))]
    static bool Layer(GunLayerAI __instance)
    {
        try { return !Puppet.SkipsLayer(__instance.Pointer); }
        catch (Exception) { return true; }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GunnerAI), nameof(GunnerAI.Step))]
    static bool Gunner(GunnerAI __instance)
    {
        try { return !Puppet.SkipsGunner(__instance.Pointer); }
        catch (Exception) { return true; }
    }
}
