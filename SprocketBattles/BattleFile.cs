using System.Text.Json;
using System.Text.Json.Serialization;

namespace SprocketBattles;

/// A battle made in the Battle Editor: its name, the map, every tank on it, its mission (zones, mines, obstacles and
/// rules) and its cinematic (cameras and tank poses over time). Plain data, saved as JSON in
/// Documents\My Games\Sprocket\Battles\<name>.json, so it can be shared, and later sent to other players as it is: each
/// tank has a stable id (orders name tanks by it) and says who drives it ("player", "ai", or "slot:N" for the Nth human
/// player).
public sealed class BattleFile
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "";
    /// A line or two about it, shown on the Battle Editor's menu.
    public string Description { get; set; } = "";
    public string Map { get; set; } = "";
    /// The weather, by the Custom Battle screen's names (maps whose weather is fixed ignore it).
    public string Clouds { get; set; } = "Clear";
    public string Fog { get; set; } = "None";
    public List<BattleUnit> Units { get; set; } = new();
    public MissionData? Mission { get; set; }
    public CinemaData? Cinema { get; set; }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BattleFile FromJson(string json) =>
        JsonSerializer.Deserialize<BattleFile>(json, Options) ?? throw new Exception("Not a battle file.");

    /// An id no tank in the battle has yet ("u1", "u2", ...).
    public string NewId()
    {
        int n = 1;
        while (Units.Any(u => u.Id == $"u{n}")) n++;
        return $"u{n}";
    }

    /// The tanks of `team` in spawning order: grouped by design, first design first, in the order they were added.
    /// The game spawns a team one design at a time, so this is the order its spawn calls ask for positions in.
    public List<List<BattleUnit>> SpawnGroups(int team) =>
        Units.Where(u => u.Team == team).GroupBy(u => u.Blueprint).Select(g => g.ToList()).ToList();

    /// Takes `unit` out, and out of every other tank's orders, the mission's rules and the cinematic.
    public void Remove(BattleUnit unit)
    {
        Units.Remove(unit);
        foreach (var u in Units) if (u.Attack?.Target == unit.Id) u.SetTarget(null);
        Mission?.Rules.RemoveAll(r => r.Unit == unit.Id || r.Then.Unit == unit.Id);
        if (Cinema == null) return;
        Cinema.Tanks.RemoveAll(t => t.Unit == unit.Id);
        foreach (var k in Cinema.Tanks.SelectMany(t => t.Keys))
        {
            if (k.AimUnit == unit.Id) k.AimUnit = null;
            if (k.ShootAt == unit.Id) k.ShootAt = null;
        }
        // (A camera following it is moved off it by the editor first, its keys kept where they were: BattleEditor.Remove.)
        foreach (var c in Cinema.Cameras)
        {
            if (c.Follow == unit.Id) c.Follow = null;
            if (c.LookAt == unit.Id) c.LookAt = null;
        }
    }
}

public sealed class BattleUnit
{
    public string Id { get; set; } = "";
    public int Team { get; set; }                 // 0: the first team (attackers), 1: the second (defenders)
    public string Blueprint { get; set; } = "";   // relative to Documents\My Games\Sprocket, so the file travels
    public float[] Position { get; set; } = new float[3];
    public float Yaw { get; set; }                // degrees, 0 = facing +z (north)
    public string Control { get; set; } = "ai";   // "player", "ai", or "slot:N"
    /// Held off the map until a mission rule brings its team's reserves in.
    public bool Reserve { get; set; }
    /// Left where the game spawns it instead of moved to Position (a quick battle's tanks).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AtSpawn { get; set; }
    /// What the AI does, in order: drive through each "move" point (the path), then go for the "attack" target.
    public List<BattleOrder>? Orders { get; set; }

    /// The path: each "move" order's point, in order (the same arrays, so changing one moves the point).
    [JsonIgnore] public List<float[]> Path => Orders?.Where(o => o.Type == "move" && o.To != null).Select(o => o.To!).ToList() ?? new();

    /// The main target's order, if the tank has one.
    [JsonIgnore] public BattleOrder? Attack => Orders?.FirstOrDefault(o => o.Type == "attack");

    /// A point on the end of the path (before the target, which comes last).
    public void AddPoint(float[] at)
    {
        Orders ??= new();
        Orders.Insert(Orders.FindLastIndex(o => o.Type == "move") + 1, new BattleOrder { Type = "move", To = at });
    }

    public void RemoveLastPoint()
    {
        int last = Orders?.FindLastIndex(o => o.Type == "move") ?? -1;
        if (last >= 0) Orders!.RemoveAt(last);
        Tidy();
    }

    public void ClearPath() { Orders?.RemoveAll(o => o.Type == "move"); Tidy(); }

    /// The main target: another tank's id (null: none), and whether the tank stops to fire at it.
    public void SetTarget(string? id, string engage = "stopToFire")
    {
        Orders?.RemoveAll(o => o.Type == "attack");
        if (id != null) (Orders ??= new()).Add(new BattleOrder { Type = "attack", Target = id, Engage = engage });
        Tidy();
    }

    void Tidy() { if (Orders?.Count == 0) Orders = null; }
}

public sealed class BattleOrder
{
    public string Type { get; set; } = "hold";    // "hold", "move", "attack"
    public float[]? To { get; set; }              // move: where
    public float? Face { get; set; }              // move: facing when there, degrees
    public string? Target { get; set; }           // attack: the target tank's id
    public string? Engage { get; set; }           // attack: "stopToFire" or "fireOnTheMove"
}

// ---------- the mission ----------

/// What happens in the battle besides the tanks fighting: named zones on the map, AT mines, obstacles, and rules
/// ("when this happens, do that") that the Battle Editor checks while the battle plays.
public sealed class MissionData
{
    public List<Zone> Zones { get; set; } = new();
    public List<Mine> Mines { get; set; } = new();
    public List<Obstacle> Obstacles { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();

    public string NewZoneId()
    {
        int n = 1;
        while (Zones.Any(z => z.Id == $"z{n}")) n++;
        return $"z{n}";
    }
}

public sealed class Zone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float[] Center { get; set; } = new float[3];
    public float Radius { get; set; } = 30;
}

/// An anti-tank mine, as the game's own (Sprocket.Landmines.ATMine): set off by a vehicle driving over it.
public sealed class Mine
{
    public float[] Position { get; set; } = new float[3];
    public float Power { get; set; } = 5000; // the game's mines: 5000
}

/// "hedgehog": a steel Czech hedgehog (the game's break force, 30 000 N); "block": a concrete block that never breaks.
public sealed class Obstacle
{
    public string Kind { get; set; } = "hedgehog";
    public float[] Position { get; set; } = new float[3];
    public float Yaw { get; set; }
}

/// When `When` happens, do `Then` (once). The fields each kind of condition and action uses are listed by Mission.
public sealed class Rule
{
    public string When { get; set; } = "time";    // time, destroyed, wipedOut, losses, teamEnters, unitEnters, holds
    public float Seconds { get; set; } = 30;      // time: battle seconds; holds: seconds held
    public string? Unit { get; set; }             // destroyed, unitEnters
    public int Team { get; set; }                 // wipedOut, losses, teamEnters, holds
    public string? Zone { get; set; }             // teamEnters, unitEnters, holds
    public int Count { get; set; } = 1;           // losses: how many lost
    public RuleAction Then { get; set; } = new();

    /// The zone its action goes to: the one picked for it, else the zone its condition watches, else the first zone.
    /// An action left on "pick zone" used to do nothing at all.
    public Zone? ActionZone(MissionData mission) =>
        mission.Zones.FirstOrDefault(z => z.Id == Then.Zone) ?? mission.Zones.FirstOrDefault(z => z.Id == Zone) ?? mission.Zones.FirstOrDefault();
}

public sealed class RuleAction
{
    public string Do { get; set; } = "message";   // message, victory, defeat, artillery, reserves, teamTo, unitTo
    public string Text { get; set; } = "";        // message, victory, defeat
    public string? Zone { get; set; }             // artillery, teamTo, unitTo
    public string? Unit { get; set; }             // unitTo
    public int Team { get; set; }                 // reserves, teamTo
    public int Shells { get; set; } = 12;         // artillery: how many shells
    public float Seconds { get; set; } = 15;      // artillery: over how long
    public float Power { get; set; } = 2756.25f;  // artillery: each shell's blast (a 105 mm shell: calibre = 2 √power)
}

// ---------- the cinematic ----------

/// Cameras that fly along keyframes, cuts between them, and tanks' turret and gun poses (and shots) over time. Times are
/// battle seconds from the moment the battle's tanks are placed.
public sealed class CinemaData
{
    public List<CameraTrack> Cameras { get; set; } = new();
    public List<Cut> Cuts { get; set; } = new();
    public List<TankTrack> Tanks { get; set; } = new();
    public bool PlayOnStart { get; set; } = true;
    public float Speed { get; set; } = 1;         // the battle's time scale while it plays (0.25: slow motion)
    /// When it ends (the End mark on the timeline); null: at its last key.
    public float? End { get; set; }

    [JsonIgnore] public float Length => End ?? Math.Max(Cameras.SelectMany(c => c.Keys).Select(k => k.Time).DefaultIfEmpty(0).Max(),
                                                 Tanks.SelectMany(t => t.Keys).Select(k => k.Time).DefaultIfEmpty(0).Max());
}

public sealed class CameraTrack
{
    public string Name { get; set; } = "";
    /// A tank's id: the camera's keys are then where it is seen from that tank (moving and turning with it).
    public string? Follow { get; set; }
    /// A tank's id: the camera always points at it (its keys then only place the camera).
    public string? LookAt { get; set; }
    public List<CamKey> Keys { get; set; } = new();
}

public sealed class CamKey
{
    public float Time { get; set; }
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new float[4]; // quaternion x, y, z, w
    public float Fov { get; set; } = 60;
}

/// From `Time` on, camera `Camera` is the one shown.
public sealed class Cut
{
    public float Time { get; set; }
    public int Camera { get; set; }
}

public sealed class TankTrack
{
    public string Unit { get; set; } = "";
    public List<TankKey> Keys { get; set; } = new();
}

/// What a tank does from a time, until a later key changes it. Its aim: angles (turret from the hull's front, gun
/// elevation), a point, a tank, or a tank to shoot at (fired at whenever loaded and on target). Its drive: throttle and
/// steering (-1 to 1), or a point its AI drives to. Fire: one shot at this key. Null: left to its AI.
public sealed class TankKey
{
    public float Time { get; set; }
    public float? Turret { get; set; }
    public float? Gun { get; set; }
    public float[]? AimPoint { get; set; }
    public string? AimUnit { get; set; }
    public string? ShootAt { get; set; }
    public float? Throttle { get; set; }
    public float? Steer { get; set; }
    public float[]? DriveTo { get; set; }
    public bool Fire { get; set; }

    [JsonIgnore] public bool Aims => Turret != null || Gun != null || AimPoint != null || AimUnit != null || ShootAt != null;
    [JsonIgnore] public bool Drives => Throttle != null || Steer != null || DriveTo != null;

    /// This key's aim set to nothing (left to the AI), and its drive likewise.
    public void ClearAim() { Turret = Gun = null; AimPoint = null; AimUnit = ShootAt = null; }
    public void ClearDrive() { Throttle = Steer = null; DriveTo = null; }
}
