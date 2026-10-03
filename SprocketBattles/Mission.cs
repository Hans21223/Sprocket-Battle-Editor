using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

/// A battle's mission while it plays: its mines and obstacles laid, its reserve tanks held off the map, and its rules
/// checked four times a second ("when this happens, do that", each once). Started when the battle's tanks are placed.
///
/// Conditions (Rule.When): time (Seconds into the battle), destroyed (Unit out of the fight), wipedOut (all of Team out;
/// reserves still to come count), losses (Count of Team's tanks out), teamEnters / unitEnters (a tank of Team, or Unit,
/// inside Zone), holds (Team inside Zone with no enemy there for Seconds).
/// Actions (RuleAction.Do): message (Text), victory / defeat (a banner with Text; the battle pauses), artillery (Shells
/// over Seconds on Zone, each a blast of Power), reserves (Team's reserves arrive where they were placed), teamTo /
/// unitTo (Team's AI tanks, or Unit, drive to Zone).
internal static class Mission
{
    internal static readonly string[] Conditions = { "time", "destroyed", "wipedOut", "losses", "teamEnters", "unitEnters", "holds" };
    internal static readonly string[] Actions = { "message", "victory", "defeat", "artillery", "reserves", "teamTo", "unitTo" };

    // A shell's calibre and its blast go together: calibre (mm) = 2 √power, so 150 mm is a 5625 blast (the game's mines,
    // 5000, are 141 mm). The artillery rule picks from field guns and howitzers' calibres.
    internal static readonly int[] Calibres = { 75, 88, 105, 122, 152, 203, 240 };
    internal static int Calibre(float power) => (int)MathF.Round(Math.Clamp(MathF.Sqrt(power) * 2, 40, 250));
    internal static float PowerOf(int calibre) => calibre * calibre / 4f;

    static BattleFile? file;
    static Dictionary<string, VehicleBehaviour> tanks = new();
    static float start, nextCheck;
    static readonly List<GameObject> laid = new();
    static readonly List<ATMineRef> mines = new();
    static readonly HashSet<Rule> done = new();
    static readonly Dictionary<Rule, float> holding = new();
    static readonly Dictionary<string, BattleUnit> reserve = new(); // reserve tanks not yet arrived, by id
    static readonly List<Barrage> barrages = new();

    /// What the battle shows: messages (until a time), and the end banner (victory or defeat) once a rule ends it.
    internal static readonly List<(string Text, float Until)> Messages = new();
    internal static (string Title, string Text, bool Won)? Banner;
    static float timeScaleBefore = 1;

    internal static bool Running => file != null;
    internal static float Now => Time.time - start;

    sealed class Barrage { public Vector3 Centre; public float Radius, Power; public List<float> At = new(); }
    sealed class ATMineRef { public Sprocket.Landmines.ATMine Mine = null!; public bool Shown; public float Power; }

    /// The battle's tanks are placed: lay the mission out and start checking its rules.
    internal static void Start(BattleFile battle, Dictionary<string, VehicleBehaviour> byId)
    {
        Stop();
        file = battle;
        tanks = byId;
        start = Time.time;
        nextCheck = 0;
        var m = battle.Mission;
        foreach (var unit in battle.Units.Where(u => u.Reserve))
            if (byId.TryGetValue(unit.Id, out var tank) && tank != null) { Hide(tank); reserve[unit.Id] = unit; }
        if (m != null)
        {
            foreach (var mine in m.Mines) Guard.Run("laying a mine", () => LayMine(Files.Vector(mine.Position), mine.Power));
            foreach (var o in m.Obstacles) Guard.Run("placing an obstacle", () => laid.Add(Build(o, live: true)));
        }
        Trace.Write($"mission: {m?.Rules.Count ?? 0} rules, {m?.Zones.Count ?? 0} zones, {mines.Count} mines, {m?.Obstacles.Count ?? 0} obstacles, {reserve.Count} tanks in reserve");
    }

    /// Everything the mission put in the world taken away (a new battle, Play again, the editor).
    internal static void Stop()
    {
        foreach (var o in laid) if (o != null) UnityEngine.Object.Destroy(o);
        laid.Clear(); mines.Clear(); done.Clear(); holding.Clear(); reserve.Clear(); barrages.Clear(); Messages.Clear();
        if (Banner != null) { Banner = null; Time.timeScale = timeScaleBefore > 0 ? timeScaleBefore : 1; }
        file = null;
    }

    /// The end banner away, the battle going again.
    internal static void Dismiss()
    {
        if (Banner == null) return;
        Banner = null;
        Time.timeScale = timeScaleBefore > 0 ? timeScaleBefore : 1;
    }

    /// Every frame.
    internal static void Tick()
    {
        Effects.Update();
        if (file == null) return;
        if (Battle.Mode == null) { Stop(); return; }
        Messages.RemoveAll(m => Time.unscaledTime > m.Until);
        foreach (var mine in mines)
            if (!mine.Shown && mine.Mine != null && mine.Mine.triggered) { mine.Shown = true; Effects.Explode(mine.Mine.transform.position, mine.Power, 0.6f); }
        foreach (var b in barrages.ToList())
        {
            while (b.At.Count > 0 && Now >= b.At[0])
            {
                b.At.RemoveAt(0);
                var r = UnityEngine.Random.insideUnitCircle * b.Radius;
                var spot = Ground(b.Centre + new Vector3(r.x, 0, r.y));
                Guard.Run("artillery", () => Blast(spot, b.Power));
            }
            if (b.At.Count == 0) barrages.Remove(b);
        }
        if (Time.time < nextCheck || Banner != null) return;
        nextCheck = Time.time + 0.25f;
        foreach (var rule in file.Mission?.Rules ?? new())
            if (!done.Contains(rule) && Holds(rule)) { done.Add(rule); Guard.Run("mission rule", () => Do(rule.Then)); }
    }

    // ---------- conditions ----------

    static bool Holds(Rule r)
    {
        switch (r.When)
        {
            case "time": return Now >= r.Seconds;
            case "destroyed": return r.Unit != null && !reserve.ContainsKey(r.Unit) && Out(Tank(r.Unit));
            case "wipedOut": return Team(r.Team).Any() && Team(r.Team).All(t => !reserve.ContainsKey(t.Id) && Out(t.Tank));
            case "losses": return Team(r.Team).Count(t => !reserve.ContainsKey(t.Id) && Out(t.Tank)) >= r.Count;
            case "teamEnters": return Zone(r.Zone) is { } z && Team(r.Team).Any(t => Alive(t.Id, t.Tank) && In(t.Tank!, z));
            case "unitEnters": return Zone(r.Zone) is { } uz && r.Unit != null && Tank(r.Unit) is { } u && Alive(r.Unit, u) && In(u, uz);
            case "holds":
                if (Zone(r.Zone) is not { } hz) return false;
                bool ours = Team(r.Team).Any(t => Alive(t.Id, t.Tank) && In(t.Tank!, hz));
                bool theirs = file!.Units.Where(u => u.Team != r.Team).Any(u => Tank(u.Id) is { } e && Alive(u.Id, e) && In(e, hz));
                if (!ours || theirs) { holding.Remove(r); return false; }
                if (!holding.ContainsKey(r)) holding[r] = Now;
                return Now - holding[r] >= r.Seconds;
        }
        return false;
    }

    static VehicleBehaviour? Tank(string id) => tanks.TryGetValue(id, out var t) && t != null ? t : null;
    static IEnumerable<(string Id, VehicleBehaviour? Tank)> Team(int team) => file!.Units.Where(u => u.Team == team).Select(u => (u.Id, Tank(u.Id)));
    static bool Out(VehicleBehaviour? tank) => tank == null || (tank.Flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0;
    static bool Alive(string id, VehicleBehaviour? tank) => tank != null && !reserve.ContainsKey(id) && !Out(tank);
    static Zone? Zone(string? id) => file?.Mission?.Zones.FirstOrDefault(z => z.Id == id);
    static bool In(VehicleBehaviour tank, Zone z) => (tank.Position - Files.Vector(z.Center)).Flat().magnitude <= z.Radius;

    // ---------- actions ----------

    static void Do(RuleAction a)
    {
        Trace.Write("mission: " + a.Do switch
        {
            "message" or "victory" or "defeat" => $"{a.Do} \"{a.Text}\"",
            "artillery" => $"artillery, {a.Shells} {Calibre(a.Power)} mm shells on {a.Zone} over {a.Seconds:0} s",
            "reserves" => $"team {a.Team + 1}'s reserves",
            "teamTo" => $"team {a.Team + 1} to {a.Zone}",
            "unitTo" => $"{a.Unit} to {a.Zone}",
            _ => a.Do,
        });
        switch (a.Do)
        {
            case "message": Messages.Add((a.Text, Time.unscaledTime + 8)); break;
            case "victory":
            case "defeat":
                Banner = (a.Do == "victory" ? "VICTORY" : "DEFEAT", a.Text, a.Do == "victory");
                timeScaleBefore = Time.timeScale;
                Time.timeScale = 0;
                break;
            case "artillery":
                if (Zone(a.Zone) is not { } z) break;
                var b = new Barrage { Centre = Files.Vector(z.Center), Radius = z.Radius, Power = a.Power };
                for (int i = 0; i < Math.Max(1, a.Shells); i++) b.At.Add(Now + 1.5f + UnityEngine.Random.value * Math.Max(0.1f, a.Seconds));
                b.At.Sort();
                barrages.Add(b);
                Messages.Add(($"Artillery on {Name(z)}!", Time.unscaledTime + 5));
                break;
            case "reserves":
                foreach (var unit in reserve.Values.Where(u => u.Team == a.Team).ToList())
                    if (Tank(unit.Id) is { } tank) { Arrive(tank, unit); reserve.Remove(unit.Id); }
                break;
            case "teamTo":
                if (Zone(a.Zone) is not { } tz) break;
                var team = Team(a.Team).Where(t => Alive(t.Id, t.Tank)).Select(t => t.Tank!).ToList();
                for (int i = 0; i < team.Count; i++) SendInto(team[i], tz, i, team.Count);
                break;
            case "unitTo":
                if (Zone(a.Zone) is { } uz && a.Unit != null && Tank(a.Unit) is { } u && Alive(a.Unit, u)) SendInto(u, uz, 0, 1);
                break;
        }
    }

    internal static string Name(Zone z) => string.IsNullOrEmpty(z.Name) ? z.Id : z.Name;

    /// A tank to its own spot in the zone (spread around the middle), facing the way it goes.
    static void SendInto(VehicleBehaviour tank, Zone z, int i, int count)
    {
        var centre = Files.Vector(z.Center);
        var spot = count <= 1 ? centre : centre + Quaternion.Euler(0, 360f * i / count, 0) * Vector3.forward * (z.Radius * 0.5f);
        var face = (spot - tank.Position).Flat();
        Battle.SendTo(tank, Ground(spot), face.sqrMagnitude > 1 ? face.normalized : Vector3.forward);
    }

    // ---------- reserves: off the map until they arrive ----------

    static void Hide(VehicleBehaviour tank)
    {
        var root = tank.transform.root;
        root.position += Vector3.down * 1000;
        foreach (var body in root.GetComponentsInChildren<Rigidbody>()) body.isKinematic = true;
    }

    internal static bool Waiting(string id) => reserve.ContainsKey(id);

    static void Arrive(VehicleBehaviour tank, BattleUnit unit)
    {
        var root = tank.transform.root;
        var at = Files.Vector(unit.Position);
        root.SetPositionAndRotation(Ground(at) + Vector3.up * 1.2f, Quaternion.Euler(0, unit.Yaw, 0));
        foreach (var body in root.GetComponentsInChildren<Rigidbody>()) { body.isKinematic = false; body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        Physics.SyncTransforms();
        Trace.Write($"mission: {unit.Id} arrives");
    }

    // ---------- mines, blasts and obstacles ----------

    /// The game's own AT mine (its settings from the game's mines), buried in a disc you can see, set off by a vehicle
    /// rolling into its trigger.
    static void LayMine(Vector3 at, float power)
    {
        var root = new GameObject("Battle Editor mine");
        root.transform.position = Ground(at);
        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.transform.SetParent(root.transform, false);
        disc.transform.localPosition = new Vector3(0, 0.02f, 0);
        disc.transform.localScale = new Vector3(0.6f, 0.04f, 0.6f);
        Look.NoCollider(disc);
        Look.Lit(disc, new Color(0.22f, 0.24f, 0.17f));
        // As the game's mine: on its Soft Obstacle layer (wheels and vehicles touch it, shells don't), a 0.2 m trigger
        // at the ground that a wheel rolling over it reaches.
        root.layer = 14;
        var trigger = root.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.radius = 0.2f;
        trigger.center = new Vector3(0, 0.05f, 0);
        var mine = Arm(root, power, 0.6f);
        laid.Add(root);
        mines.Add(new ATMineRef { Mine = mine, Power = power });
    }

    static Sprocket.Landmines.ATMine Arm(GameObject o, float power, float delay)
    {
        var mine = o.AddComponent<Sprocket.Landmines.ATMine>();
        mine.explosivePower = power;
        mine.delay = delay;
        mine.piercingDamageModifier = 0.25f;
        mine.bluntForceDamageModifier = 0.1f;
        mine.thermalDamageModifier = 0.05f;
        mine.pressureDamageModifier = 0.6f;
        return mine;
    }

    /// An artillery shell landing: the game's mine blast (damage falls off with distance; radius √(power / 10)) and an
    /// explosion to see.
    static void Blast(Vector3 at, float power)
    {
        var o = new GameObject("Battle Editor shell");
        o.transform.position = at + Vector3.up * 0.3f;
        var mine = Arm(o, power, 0);
        mine.Trigger();
        UnityEngine.Object.Destroy(o, 5);
        Effects.Explode(at, power, 0);
    }

    /// An obstacle's model: in the editor a marker, in the battle a solid one (a hedgehog breaks like the game's).
    internal static GameObject Build(Obstacle o, bool live)
    {
        var root = new GameObject("Battle Editor obstacle");
        root.transform.SetPositionAndRotation(live ? Ground(Files.Vector(o.Position)) : Files.Vector(o.Position), Quaternion.Euler(0, o.Yaw, 0));
        if (o.Kind == "block")
        {
            Piece(root, new Vector3(0, 0.6f, 0), Quaternion.identity, new Vector3(1.4f, 1.2f, 1.4f), new Color(0.55f, 0.55f, 0.52f), live);
        }
        else
        {
            // Three steel beams crossed at the middle, each tilted the way the game's Czech hedgehogs are.
            var steel = new Color(0.25f, 0.22f, 0.2f);
            Piece(root, new Vector3(0, 0.75f, 0), Quaternion.Euler(45, 0, 0), new Vector3(0.14f, 0.14f, 2f), steel, live);
            Piece(root, new Vector3(0, 0.75f, 0), Quaternion.Euler(45, 120, 0), new Vector3(0.14f, 0.14f, 2f), steel, live);
            Piece(root, new Vector3(0, 0.75f, 0), Quaternion.Euler(45, 240, 0), new Vector3(0.14f, 0.14f, 2f), steel, live);
            if (live)
            {
                var body = root.AddComponent<Rigidbody>();
                body.mass = 300; body.isKinematic = true;
                root.AddComponent<Sprocket.Gameplay.HardObstacle>().breakForce = 30000;
            }
        }
        return root;
    }

    static void Piece(GameObject root, Vector3 at, Quaternion turn, Vector3 size, Color colour, bool solid)
    {
        var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        p.transform.SetParent(root.transform, false);
        p.transform.localPosition = at; p.transform.localRotation = turn; p.transform.localScale = size;
        if (!solid) Look.NoCollider(p);
        Look.Lit(p, colour);
    }

    /// The ground under `at` (not a vehicle, not the Battle Editor's own objects); `at` if there's none.
    internal static Vector3 Ground(Vector3 at)
    {
        foreach (var h in Physics.RaycastAll(new Vector3(at.x, at.y + 300, at.z), Vector3.down, 1000).OrderBy(h => h.distance))
        {
            if (h.collider == null || h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<VehicleObject>() != null) continue;
            if (h.collider.transform.root.name.StartsWith("Battle Editor")) continue;
            return h.point;
        }
        return at;
    }
}

/// Explosions to see and hear (mines, artillery): the burst the game's own HE shells make on the ground
/// (ProjectileEffectConfig, the shell impact effect with its sound), as big as a shell of a calibre to match the blast;
/// the game's explosion effect if no shell effect is loaded.
internal static class Effects
{
    static Sprocket.Vehicles.Weapons.ProjectileEffectConfig? shells;
    static GameObject? prefab;
    static float lookedAt = -100;
    static readonly List<(Vector3 At, float Power, float When)> pending = new();

    internal static void Explode(Vector3 at, float power, float after)
    {
        if (after > 0) { pending.Add((at, power, Time.time + after)); return; }
        // Looked for again (every 10 s at most) while none is found: a battle loads them as its guns come in.
        if (shells == null && prefab == null && Time.unscaledTime - lookedAt > 10)
        {
            lookedAt = Time.unscaledTime;
            shells = Resources.FindObjectsOfTypeAll<Sprocket.Vehicles.Weapons.ProjectileEffectConfig>().FirstOrDefault(c => c.Effects != null);
            var found = Resources.FindObjectsOfTypeAll<Sprocket.Gameplay.ExplosionEffects>();
            prefab = found.Length > 0 ? found[0].gameObject : null;
            Trace.Write($"effects: {(shells != null ? $"the game's shell bursts ({shells.name})" : prefab != null ? $"the game's explosion {prefab.name}" : "none of the game's loaded, nothing shown")}");
        }
        if (shells != null)
        {
            shells.PlayEffect(new Sprocket.DamageModelling.ProjectileEffectInfo
            {
                Type = Sprocket.DamageModelling.ProjectileEffectType.Explosion,
                Position = at, HitNormal = Vector3.up, HitVelocity = Vector3.down * 300,
                Calibre = (ushort)Mission.Calibre(power),
            });
            return;
        }
        if (prefab != null)
        {
            var o = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            o.name = "Battle Editor explosion";
            o.SetActive(true);
            o.GetComponent<Sprocket.Gameplay.ExplosionEffects>()?.Play();
            UnityEngine.Object.Destroy(o, 15);
        }
    }

    internal static void Update()
    {
        if (pending.Count == 0) return;
        foreach (var p in pending.Where(p => Time.time >= p.When).ToList())
        {
            pending.Remove(p);
            Explode(p.At, p.Power, 0);
        }
    }
}

/// How the Battle Editor's objects look: flat colours the game's renderer can draw (a new object's own material is
/// one it can't: pink), unlit for markers and lit for things in the world.
internal static class Look
{
    static Shader? unlit, lit;
    static readonly Dictionary<(Color, bool), Material> shared = new();

    internal static Material? Material(Color c, bool unique = false, bool shaded = false)
    {
        if (!unique && shared.TryGetValue((c, shaded), out var known) && known != null) return known;
        unlit ??= Shader.Find("HDRP/Unlit");
        lit ??= Shader.Find("HDRP/Lit");
        var shader = shaded ? lit ?? unlit : unlit ?? lit;
        if (shader == null) return null;
        var m = new Material(shader);
        m.SetColor("_UnlitColor", c);
        m.SetColor("_BaseColor", c);
        if (!unique) shared[(c, shaded)] = m;
        return m;
    }

    internal static void Lit(GameObject o, Color c) { if (Material(c, shaded: true) is { } m) o.GetComponent<Renderer>().sharedMaterial = m; }

    /// Off at once (a destroyed object's collider stays in the physics scene until the frame ends), then gone.
    internal static void NoCollider(GameObject o) { var c = o.GetComponent<Collider>(); if (c == null) return; c.enabled = false; UnityEngine.Object.Destroy(c); }

    internal static Vector3 Flat(this Vector3 v) => new(v.x, 0, v.z);
}
