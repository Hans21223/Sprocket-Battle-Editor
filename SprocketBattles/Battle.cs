using Sprocket;
using Sprocket.CustomBattles;
using Sprocket.Orders;
using Sprocket.Vehicles;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles.Spawning;
using UnityEngine;

namespace SprocketBattles;

/// The battle side: the running custom battle's map and tanks, and playing an edited battle in it. Playing rewrites the
/// custom battle's own teams to the battle file's tanks and restarts it (the game's Retry), then moves each tank to
/// where it was placed once the game has spawned it. Nothing of the game's is hooked: hooks on its loading never ran,
/// one on its spawning crashed it, and hooks on its win and loss (for a team left empty) most likely made Reset in the
/// pause menu crash it; a battle with an empty team doesn't end anyway.
internal static class Battle
{
    /// The running battle, kept once found (searching every object each frame cost a lot with many tanks); looked
    /// for again at most once a second while there is none.
    internal static DeathmatchGameMode? Mode
    {
        get
        {
            if (mode != null) return mode;
            if (Time.unscaledTime - lookedAt < 1) return null;
            lookedAt = Time.unscaledTime;
            return mode = UnityEngine.Object.FindObjectOfType<DeathmatchGameMode>();
        }
    }

    static DeathmatchGameMode? mode;
    static float lookedAt = -10;

    /// The running battle's map, as the game names it ("Defence", ...).
    internal static string Map(DeathmatchGameMode mode)
    {
        var name = mode.context?.SetupContext?.SetupConfig?.MapName;
        return string.IsNullOrEmpty(name) ? UnityEngine.SceneManagement.SceneManager.GetActiveScene().name : name;
    }

    /// The battle's tanks: the game object of each, spawned or not yet.
    internal static List<GameObject> Tanks(DeathmatchGameMode mode)
    {
        var tanks = new List<GameObject>();
        var spawns = mode.vehicleSpawns;
        if (spawns == null) return tanks;
        for (int i = 0; i < spawns.Count; i++)
            if (spawns[i]?.Vehicle?.TryCast<IVehicleGateway>()?.gameObject is { } o) tanks.Add(o);
        return tanks;
    }

    /// Whether every tank the battle started with is in.
    internal static bool Spawned(DeathmatchGameMode mode)
    {
        var spawns = mode.vehicleSpawns;
        if (spawns == null || spawns.Count == 0) return false;
        for (int i = 0; i < spawns.Count; i++) if (spawns[i] == null || !spawns[i].Spawned) return false;
        return true;
    }

    /// Restart the battle with the file's tanks: the custom battle's teams get them, one unit per design in the order the
    /// file spawns them, and they're moved to their places once spawned. A reason it can't, or null.
    internal static string? Play(DeathmatchGameMode mode, BattleFile file)
    {
        var teams = mode.context?.SetupContext?.SetupConfig?.TryCast<BattleConfig>()?.Teams;
        if (teams == null) return "This battle isn't a custom battle, so it can't be restarted with these tanks.";
        if (Fill(teams, file) is { } error) return error;
        var spawns = mode.vehicleSpawns;
        if (spawns != null) for (int i = 0; i < spawns.Count; i++) if (spawns[i] != null) before.Add(spawns[i].Pointer);
        Trace.Write($"play '{file.Name}' on {file.Map}: {toPlace!.Count} tanks, restarting");
        mode.RetryAsyncVoid();
        return null;
    }

    /// A battle about to start from the Custom Battle screen (the Battle Editor menu's Play): its teams are the file's.
    internal static string? Prepare(Il2CppReferenceArray<TeamDefinition> teams, BattleFile file)
    {
        if (Fill(teams, file) is { } error) return error;
        Trace.Write($"menu: '{file.Name}' on {file.Map}: {toPlace!.Count} tanks set up");
        return null;
    }

    /// The custom battle's teams made the file's tanks, one unit per design in the order the file spawns them, and
    /// the tanks to place once spawned. A reason it can't, or null.
    static string? Fill(Il2CppReferenceArray<TeamDefinition> teams, BattleFile file)
    {
        // Every design read first: the teams only change once they all can be.
        foreach (var u in file.Units)
            if (UnitFor(u.Blueprint) == null) return $"Couldn't read the design {System.IO.Path.GetFileNameWithoutExtension(u.Blueprint)}.";
        var order = new List<BattleUnit>();
        for (int t = 0; t < teams.Length; t++)
        {
            var units = teams[t].Units;
            units.Clear();
            foreach (var group in file.SpawnGroups(t))
            {
                units.Add(UnitFor(group[0].Blueprint)!, new UnitInstanceInfo { Count = group.Count });
                order.AddRange(group);
            }
        }
        StopOrders();
        Mission.Stop();
        before.Clear();
        toPlace = order;
        playing = file;
        placeSince = Time.unscaledTime;
        return null;
    }

    /// A battle about to start from the Custom Battle screen to be edited: a team with no tanks gets one of your designs
    /// (the screen won't start without; the editor moves the battle's own tanks away anyway).
    internal static string? FillEmpty(Il2CppReferenceArray<TeamDefinition> teams)
    {
        var designs = Files.Designs();
        if (designs.Count == 0) return "No vehicle designs in My Games\\Sprocket\\Factions to start a battle with.";
        var unit = UnitFor(designs[0].Path);
        if (unit == null) return $"Couldn't read the design {designs[0].Name}.";
        for (int t = 0; t < teams.Length; t++)
            if (teams[t] != null && teams[t].UnitCount == 0) teams[t].Units.Add(unit, new UnitInstanceInfo { Count = 1 });
        StopOrders();
        Mission.Stop();
        toPlace = null;
        playing = null;
        return null;
    }

    /// The battle file being played (its tanks placed, its mission and cinematic running), if any.
    internal static BattleFile? Playing => playing;
    static BattleFile? playing;

    /// A custom battle unit for a design, made the way the Custom Battle screen makes its own (name, cost, icon): the
    /// screen prices every unit, and one without a cost broke it (an error every frame, and a crash going back to the
    /// menu). Null if the game can't read the design.
    internal static UnitDefinition? UnitFor(string blueprint)
    {
        var path = Files.Absolute(blueprint);
        if (Units.TryGetValue(path, out var known)) return known;
        try
        {
            var card = Sprocket.Vehicles.Serialization.VehicleCard.LoadVehicleCard(path);
            var unit = card == null ? null : UnitSelectionLoader.NewUnit(card);
            if (unit != null) Units[path] = unit;
            else Trace.Write($"couldn't read the design {path}");
            return unit;
        }
        catch (Exception ex) { Trace.Write($"couldn't read the design {path}: {ex.Message}"); return null; }
    }

    static readonly Dictionary<string, UnitDefinition> Units = new(StringComparer.OrdinalIgnoreCase);

    /// Once the restarted battle's tanks are all in: each moved to where it was placed, turned as placed, at the height
    /// above the ground the game gave it. Called every frame; does nothing when there's nothing to place.
    internal static void PlaceSpawned()
    {
        if (toPlace == null) return;
        var spawns = Mode?.vehicleSpawns;
        var fresh = new List<VehicleSpawn>();
        if (spawns != null)
            for (int i = 0; i < spawns.Count; i++)
                if (spawns[i] != null && !before.Contains(spawns[i].Pointer)) fresh.Add(spawns[i]);
        if (fresh.Count < toPlace.Count || fresh.Any(s => !s.Spawned))
        {
            if (Time.unscaledTime - placeSince > 240) { Trace.Write($"placing: gave up, {fresh.Count} of {toPlace.Count} tanks spawned in 4 minutes"); toPlace = null; }
            return;
        }
        var order = toPlace;
        toPlace = null;
        var tanks = new Dictionary<string, VehicleBehaviour>();
        for (int i = 0; i < order.Count; i++)
        {
            var gateway = fresh[i].Vehicle?.TryCast<IVehicleGateway>();
            var root = gateway?.transform;
            if (root == null) { Trace.Write($"placing: tank {i} has no transform"); continue; }
            if (!order[i].AtSpawn)
            {
                var target = Files.Vector(order[i].Position);
                float above = root.position.y - GroundBelow(root, root.position);
                root.SetPositionAndRotation(new Vector3(target.x, target.y + Math.Max(0.1f, above), target.z), Quaternion.Euler(0, order[i].Yaw, 0));
                foreach (var body in root.GetComponentsInChildren<Rigidbody>()) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
            if ((gateway!.Behaviour?.TryCast<VehicleBehaviour>() ?? root.GetComponentInChildren<VehicleBehaviour>()) is { } tank)
            {
                tanks[order[i].Id] = tank;
                var unit = order[i];
                Guard.Run("travel", () => Travel.Learn(unit.Blueprint, gateway, tank.Mass));
                Guard.Run("shapes", () => Shapes.Learn(unit.Blueprint, root));
            }
        }
        Physics.SyncTransforms();
        Trace.Write($"placing: moved {order.Count} tanks to where they were placed");
        StartOrders(order, tanks);
        if (playing != null)
        {
            if (Mode is { } on && !string.Equals(Map(on), playing.Map, StringComparison.OrdinalIgnoreCase))
            {
                Trace.Write($"placing: '{playing.Name}' was made on {playing.Map} but this is {Map(on)}");
                BattleEditor.Tell($"{playing.Name} was made on {playing.Map}, but the game started {Map(on)}: the tanks may be off the map.");
            }
            Guard.Run("mission", () => Mission.Start(playing, tanks));
            BattleEditor.Started(playing, tanks);
        }
    }

    /// A placed tank of the battle being played, by its id (null once destroyed or if there's none).
    internal static VehicleBehaviour? TankOf(string id) => tanksById.TryGetValue(id, out var t) && t != null ? t : null;

    // ---------- orders: each AI tank drives its path, point by point, then goes for its main target ----------

    sealed class Runner
    {
        public BattleUnit Unit = null!;
        public VehicleBehaviour Tank = null!;
        public int Step;       // the path point it's driving to; Path.Count: the target is next; past that: done
        public bool Given;     // the order for this step went out
        public float Since;    // when this step started
        public float Best;     // the closest it has come to this step's point, and when
        public float BestAt;
        public bool Driven;    // the player was found driving it (said once)
        public bool Attacking; // its target went out while on the way
    }

    static List<Runner>? runners;
    static Dictionary<string, VehicleBehaviour> tanksById = new();
    static float nextTick;

    static void StartOrders(List<BattleUnit> units, Dictionary<string, VehicleBehaviour> tanks)
    {
        tanksById = tanks;
        idByTank = tanks.Where(p => p.Value != null).ToDictionary(p => p.Value.Pointer, p => p.Key);
        held.Clear();
        // Every tank with orders runs them whenever the AI drives it: the tank the player drives waits (see Step).
        runners = units.Where(u => u.Orders != null && tanks.ContainsKey(u.Id))
                       .Select(u => new Runner { Unit = u, Tank = tanks[u.Id], Since = Time.time }).ToList();
        foreach (var r in runners) Hold(r.Tank);
        driver = units.FirstOrDefault(u => u.Control == "player" && tanks.ContainsKey(u.Id));
        nextTick = Time.time + 2; // the battle settles first
        Trace.Write($"orders: {runners.Count} tanks have a path or a target, {tanks.Count} tanks found{(driver != null ? $", you drive {driver.Id}" : "")}");
        foreach (var r in runners)
            Trace.Write($"orders: {r.Unit.Id} has {r.Unit.Path.Count} path points, its AI brakes within {BrakingDistance(r.Tank):0} m of where it's going");
        // A battle restarted by Play leaves every AI holding fire (the game lets them fire when a battle starts its own
        // way): they'd drive up to their targets and never shoot. Everyone fires at will.
        var modes = tanks.Values.Where(t => t != null).Select(t => Commander(t)?.FireMode.ToString() ?? "no AI").GroupBy(m => m).Select(g => $"{g.Key} x{g.Count()}").ToList();
        foreach (var t in tanks.Values) if (t != null) FireAtWill(t);
        Trace.Write($"orders: fire modes were {string.Join(", ", modes)}; all set to fire at will");
    }

    static Sprocket.ArtificialIntelligence.CommanderAI? Commander(VehicleBehaviour tank) =>
        tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>();

    static void FireAtWill(VehicleBehaviour tank)
    {
        if (Commander(tank) is { } commander && commander.FireMode != Sprocket.ArtificialIntelligence.FireMode.FireAtWill)
            commander.FireMode = Sprocket.ArtificialIntelligence.FireMode.FireAtWill;
    }

    // The tank marked "you drive it", handed to the player on the first tick (the game gives the player its own pick at spawn).
    static BattleUnit? driver;

    internal static void StopOrders() { runners = null; attacks.Clear(); held.Clear(); blocked.Clear(); }

    // ---------- your orders over the game's ----------

    /// Your orders override the game's: a tank you command (its battle path and target, or the command view's orders)
    /// takes orders from the Battle Editor only (a mission's scripted orders and the command wheel are dropped) and
    /// leaves its formation (a formation leader drags its followers to their places). Off: the game's own orders mix in.
    internal static bool OverrideGame = true;

    // The AI (CommanderAI) of each tank you command, and which game orders were dropped for which tank (said once each).
    static readonly Dictionary<IntPtr, string> held = new();
    static readonly HashSet<(IntPtr, string)> blocked = new();
    static bool sending;

    internal static bool Holds(Sprocket.ArtificialIntelligence.CommanderAI ai) => OverrideGame && held.ContainsKey(ai.Pointer);

    internal static bool Commanded(VehicleBehaviour tank) => Commander(tank) is { } ai && held.ContainsKey(ai.Pointer);

    /// From the hook on the AI's Order: whether this order goes through. Ours always; the game's only to tanks you don't command.
    internal static bool Accept(Sprocket.ArtificialIntelligence.CommanderAI ai, Order order)
    {
        if (sending || !Holds(ai)) return true;
        try
        {
            string kind = order?.GetIl2CppType()?.Name ?? "order";
            if (blocked.Add((ai.Pointer, kind))) Trace.Write($"override: dropped the game's {kind} for {held[ai.Pointer]}");
        }
        catch (Exception) { }
        return false;
    }

    /// Give an order as the Battle Editor (the hook lets it through).
    static void Send(IOrderReceiver receiver, Order order)
    {
        sending = true;
        try { receiver.Order(order); }
        finally { sending = false; }
    }

    /// From now on the tank takes your orders only: out of its formation, its formation tasks cancelled.
    static void Hold(VehicleBehaviour tank)
    {
        if (Commander(tank) is not { } ai || !held.TryAdd(ai.Pointer, NameOf(tank))) return;
        try
        {
            ai.formationTasksCts?.Cancel();
            ai.formation?.Exit();
        }
        catch (Exception ex) { Trace.Write($"override: couldn't take {NameOf(tank)} out of its formation: {ex.Message}"); }
    }

    /// Back to the game's AI: no path, target or hold of yours.
    internal static void Free(VehicleBehaviour tank)
    {
        runners?.RemoveAll(r => r.Tank != null && r.Tank.Pointer == tank.Pointer);
        if (attacks.RemoveAll(a => a.Tank == null || a.Tank.Pointer == tank.Pointer) > 0) Commander(tank)?.attackOrderCts?.Cancel();
        if (Commander(tank) is { } ai) held.Remove(ai.Pointer);
    }

    /// You drive `tank` from now on (any team's); the one you drove goes back to its AI.
    internal static bool Drive(VehicleBehaviour tank)
    {
        if (Mode?.vehicleControlState is not { } control || tank.TryCast<IVehicleBehaviour>() is not { } target) return false;
        Free(tank);
        control.SetControlTarget(target);
        Trace.Write($"you drive {NameOf(tank)} (team {(int)tank.ID.TeamID}) now");
        return true;
    }

    // ---------- performance: the AI's sight checks less often in a big battle ----------

    /// How often each AI's slow tick runs, 1 in this many of its turns: the slow tick checks what it sees, picks targets and
    /// plans its moves (its cost grows with tanks times tanks); its fast tick (driving, aiming, firing) is left at the
    /// game's rate. 0: by the number of tanks (every turn up to 40, every 2nd up to 80, then every 3rd).
    internal static int SightEvery = 0;
    internal static int SightNow { get; private set; } = 1;
    static int frame;

    /// Every frame: an AI's slow tick runs once enough battle time has added up on its timer, so on the frames it should
    /// skip, its timer is set back to where it was a frame ago (unless it has just ticked, which lowers it). Staggered
    /// across AIs so the work spreads over frames; whichever of the game and this runs first in a frame, the timers then
    /// gain time on 1 frame in n.
    internal static void ThrottleSight()
    {
        frame++;
        var ais = Sprocket.ArtificialIntelligence.AIRegister.register;
        int count = ais?.Count ?? 0;
        SightNow = SightEvery > 0 ? SightEvery : count > 80 ? 3 : count > 40 ? 2 : 1;
        if (ais == null || SightNow <= 1 || slowBefore.Count > 4 * count + 16) slowBefore.Clear();
        if (ais == null || SightNow <= 1) return;
        for (int i = 0; i < count; i++)
        {
            var ai = ais[i];
            if (ai == null) continue;
            float now = ai.slowTickDeltaTime;
            if ((i + frame) % SightNow != 0 && slowBefore.TryGetValue(ai.Pointer, out var before) && now > before) ai.slowTickDeltaTime = now = before;
            slowBefore[ai.Pointer] = now;
        }
    }

    static readonly Dictionary<IntPtr, float> slowBefore = new();

    internal static int AICount => Sprocket.ArtificialIntelligence.AIRegister.register?.Count ?? 0;

    /// Twice a second of battle time (so nothing moves on while the battle is frozen).
    internal static void RunOrders()
    {
        if ((runners == null && attacks.Count == 0) || Time.time < nextTick) return;
        nextTick = Time.time + 0.5f;
        Guard.Run("attacks", WatchAttacks);
        if (driver != null)
        {
            var mine = tanksById[driver.Id];
            Guard.Run("you drive", () =>
            {
                if (mine != null && Mode?.vehicleControlState is { } control && mine.TryCast<IVehicleBehaviour>() is { } tank)
                {
                    control.SetControlTarget(tank);
                    Trace.Write($"you drive {driver.Id} now");
                }
            });
            driver = null;
        }
        if (runners != null) foreach (var r in runners) Guard.Run($"orders for {r.Unit.Id}", () => Step(r));
    }

    static void Step(Runner r)
    {
        if (r.Tank == null || Mission.Waiting(r.Unit.Id)) return; // destroyed, or a reserve still to come
        if (r.Tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player)
        {
            // The player drives it: an AI order would do nothing. It goes on when the player switches to another tank.
            if (!r.Driven)
            {
                r.Driven = true;
                Trace.Write($"orders: you're driving {r.Unit.Id}, so it doesn't follow its path");
                BattleEditor.Tell($"You're driving {r.Unit.Id}, so it won't follow its path. In the editor, pick another tank and click You drive it.");
            }
            r.Given = false;
            return;
        }
        var receiver = r.Tank.OrderReciever;
        if (receiver == null || !receiver.CanReceiveOrder) return;
        var path = r.Unit.Path;
        VehicleBehaviour? foe = r.Unit.Attack?.Target is { } tid && tanksById.TryGetValue(tid, out var t) && t != null ? t : null;
        // The target from the start: while it drives its path it holds course and shoots the target when it can (an
        // order no later order cancels, so its path points don't); once there, it goes for the target (below).
        if (foe != null && !r.Attacking && r.Step < path.Count)
        {
            r.Attacking = true;
            StartAttack(r.Tank, foe, $"{r.Unit.Id} on {r.Unit.Attack!.Target} (on the way)", r.Unit.Attack.Engage != "fireOnTheMove", AttackApproachType.Passive);
        }
        if (r.Step < path.Count)
        {
            var to = Files.Vector(path[r.Step]);
            bool last = r.Step == path.Count - 1;
            float distance = Flat(r.Tank.Position, to);
            // The AI slows down as it nears its destination: a point on the way is passed on while the tank is still
            // at full speed (as far out as its driver starts braking), so it drives the path without stopping at each
            // point. The last point is driven to, and faced.
            float reach = last ? 10 : Math.Clamp(BrakingDistance(r.Tank), 25, 60);
            if (distance < reach || Time.time - r.Since > 180)
            {
                Trace.Write($"orders: {r.Unit.Id} {(distance < reach ? "reached" : "gave up on")} point {r.Step + 1} of {path.Count} after {Time.time - r.Since:0} s");
                r.Step++; r.Given = false; r.Since = Time.time;
                return;
            }
            // Closer, or stopped to fight its target: not stuck.
            if (distance < r.Best - 3 || (foe != null && !Out(foe) && InSight(r.Tank, foe))) { r.Best = Math.Min(r.Best, distance); r.BestAt = Time.time; }
            // Stuck, or the game's own AI (its platoon's formation, a fight) took over: no closer for 10 s, so again.
            if (!r.Given || Time.time - r.BestAt > 10)
            {
                if (r.Given) Trace.Write($"orders: {r.Unit.Id} no closer to point {r.Step + 1} for 10 s ({distance:0} m away), ordered again");
                Give(r, receiver, MoveOrder(to, last ? Facing(r.Unit, path, r.Step) : null));
                r.Best = distance; r.BestAt = Time.time;
            }
            return;
        }
        if (r.Step++ > path.Count) return;
        if (foe != null) StartAttack(r.Tank, foe, $"{r.Unit.Id} on {r.Unit.Attack!.Target}", r.Unit.Attack.Engage != "fireOnTheMove", AttackApproachType.Aggressive);
    }

    /// Drive to `to`; with `face`, stop there facing that way (a point on the way has none, so it isn't braked for).
    static Order MoveOrder(Vector3 to, Vector3? face)
    {
        var target = face is { } f ? new PositionTarget(to, f, 6, 0) : new PositionTarget(to, 12, 0);
        return new MoveToPositionOrder(ref target, Order.HighestPriority, OrderCancellationType.CancelOnNextOrder);
    }

    /// How far from its destination the tank's AI driver starts braking (its "full throttle" distance), or 0.
    static float BrakingDistance(VehicleBehaviour tank)
    {
        try { return tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>()?.driver?.maxThrottleDist ?? 0; }
        catch (Exception) { return 0; }
    }

    /// Attack `enemy` (its id is the order's target filter). Passive: keep to the tank's own course and shoot when it
    /// can; Aggressive: go for it. Explicit orders stay until cancelled, not by the next order (a path point).
    static Order AttackOrderOn(VehicleBehaviour enemy, bool stopToFire, AttackApproachType approach = AttackApproachType.Aggressive)
    {
        var info = new AttackOrderInfo(enemy.ID, approach, stopToFire ? AttackEngageMode.StopToFire : AttackEngageMode.FireOnTheMove, 1);
        var cancel = approach == AttackApproachType.Passive ? OrderCancellationType.Explicit : OrderCancellationType.CancelOnNextOrder;
        return new AttackOrder(ref info, Order.HighestPriority, cancel);
    }

    // ---------- orders from the command view: they replace the tank's own path and target ----------

    /// Every tank in the battle, as the game's vehicle behaviours (id, team, position, AI).
    internal static List<VehicleBehaviour> Behaviours(DeathmatchGameMode mode)
    {
        var tanks = new List<VehicleBehaviour>();
        var spawns = mode.vehicleSpawns;
        if (spawns == null) return tanks;
        for (int i = 0; i < spawns.Count; i++)
        {
            var gateway = spawns[i]?.Vehicle?.TryCast<IVehicleGateway>();
            if (gateway == null) continue;
            if ((gateway.Behaviour?.TryCast<VehicleBehaviour>() ?? gateway.transform?.GetComponentInChildren<VehicleBehaviour>()) is { } tank) tanks.Add(tank);
        }
        return tanks;
    }

    internal static bool SendTo(VehicleBehaviour tank, Vector3 to, Vector3 face)
    {
        if (!TakeOver(tank)) return false;
        FireAtWill(tank);
        Send(tank.OrderReciever, MoveOrder(to, face));
        return true;
    }

    internal static bool SendAgainst(VehicleBehaviour tank, VehicleBehaviour enemy)
    {
        if (!TakeOver(tank)) return false;
        StartAttack(tank, enemy, $"{NameOf(tank)} on {NameOf(enemy)}", true);
        return true;
    }

    /// A command-view order for `tank`: its own path, target and earlier attack end. False if it can't take orders.
    static bool TakeOver(VehicleBehaviour tank)
    {
        Free(tank);
        var receiver = tank.OrderReciever;
        if (receiver == null || !receiver.CanReceiveOrder || tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player) return false;
        Hold(tank);
        return true;
    }

    static string NameOf(VehicleBehaviour tank) => UnitOf(tank)?.Id ?? tank.ID.ToString();

    // ---------- attacks: watched while the target fights, given again if the AI drops them ----------

    sealed class Attack
    {
        public VehicleBehaviour Tank = null!, Enemy = null!;
        public string Name = "";
        public bool StopToFire;
        public AttackApproachType Approach;
        public float Since, GivenAt, LoggedAt;
    }

    static readonly List<Attack> attacks = new();

    static void StartAttack(VehicleBehaviour tank, VehicleBehaviour enemy, string name, bool stopToFire,
                            AttackApproachType approach = AttackApproachType.Aggressive)
    {
        attacks.RemoveAll(a => a.Tank == null || a.Tank.Pointer == tank.Pointer);
        var attack = new Attack { Tank = tank, Enemy = enemy, Name = name, StopToFire = stopToFire, Approach = approach, Since = Time.time, LoggedAt = Time.time };
        attacks.Add(attack);
        GiveAttack(attack, "given");
    }

    static void GiveAttack(Attack a, string how)
    {
        var receiver = a.Tank.OrderReciever;
        if (receiver == null || !receiver.CanReceiveOrder) return;
        FireAtWill(a.Tank);
        var order = AttackOrderOn(a.Enemy, a.StopToFire, a.Approach);
        Send(receiver, order);
        a.GivenAt = Time.time;
        Trace.Write($"attack {a.Name} {how}: {order.GetLog(receiver)}; {Sight(a.Tank, a.Enemy)}");
    }

    /// Out of the fight: can neither move nor shoot (or gone).
    static bool Out([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] VehicleBehaviour? tank) =>
        tank == null || (tank.Flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0;

    static void WatchAttacks()
    {
        foreach (var a in attacks.ToList())
        {
            if (a.Tank == null || Out(a.Enemy))
            {
                attacks.Remove(a);
                Trace.Write($"attack {a.Name}: over, the target {(a.Enemy == null ? "is gone" : $"is out of the fight (flags {a.Enemy.Flags})")}");
                continue;
            }
            var commander = a.Tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>();
            var cts = commander?.attackOrderCts;
            bool active = cts != null && !cts.IsCancellationRequested;
            // What the AI makes of it, every 5 s for the first minute: the trace shows why a tank doesn't shoot.
            if (Time.time - a.LoggedAt >= 5 && Time.time - a.Since < 60)
            {
                a.LoggedAt = Time.time;
                Trace.Write($"attack {a.Name}: order {(active ? "active" : "not active")}, {Sight(a.Tank, a.Enemy)}");
            }
            if (!active && Time.time - a.GivenAt > 10) GiveAttack(a, "given again (the AI had dropped it)");
        }
    }

    /// What the attacker's AI sees: each visible target's id, whether the target is among them (matched the way the
    /// attack order's filter is), and its fire mode.
    static bool InSight(VehicleBehaviour tank, VehicleBehaviour enemy)
    {
        var buffer = Commander(tank)?.visibleTargets;
        var visible = buffer?.visible;
        for (int i = 0; buffer != null && visible != null && i < buffer.visibleCount && i < visible.Length; i++)
            if (visible[i] != null && EntityID.FilterMatch(visible[i].EntityID, enemy.ID)) return true;
        return false;
    }

    static string Sight(VehicleBehaviour tank, VehicleBehaviour enemy)
    {
        var commander = tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>();
        var buffer = commander?.visibleTargets;
        if (commander == null || buffer == null) return "no AI to ask";
        var ids = new List<string>();
        bool inSight = false;
        var visible = buffer.visible;
        for (int i = 0; visible != null && i < buffer.visibleCount && i < visible.Length; i++)
        {
            if (visible[i] == null) continue;
            var id = visible[i].EntityID;
            ids.Add(id.ToString());
            if (EntityID.FilterMatch(id, enemy.ID)) inSight = true;
        }
        return $"it sees {ids.Count} [{string.Join(", ", ids)}], target {enemy.ID} {(inSight ? "IN SIGHT" : "not in sight")}, " +
               $"{Vector3.Distance(tank.Position, enemy.Position):0} m away, fire mode {commander.FireMode}";
    }

    /// What a tank's AI plans: the path-finding route still ahead (from the tank), and where it ends. Empty if none.
    internal static List<Vector3> Route(VehicleBehaviour tank)
    {
        var route = new List<Vector3>();
        var driver = tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>()?.driver;
        var corners = driver?.pathCorners;
        if (driver == null || corners == null || corners.Length == 0) return route;
        route.Add(tank.Position);
        for (int i = Math.Max(0, driver.nextCornerIndex); i < corners.Length; i++) route.Add(corners[i]);
        return route;
    }

    /// What a tank's AI is doing now, as the game names the task ("MoveToPositionTask", ...), or "" if nothing.
    internal static string Task(VehicleBehaviour tank)
    {
        try { return tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>()?.driver?.CurrentTask?.GetIl2CppType()?.Name ?? ""; }
        catch (Exception) { return ""; }
    }

    /// The battle file's tank for a game tank, if it was placed in the editor.
    internal static BattleUnit? UnitOf(VehicleBehaviour tank) =>
        idByTank.TryGetValue(tank.Pointer, out var id) ? runners?.FirstOrDefault(r => r.Unit.Id == id)?.Unit ?? new BattleUnit { Id = id } : null;

    static Dictionary<IntPtr, string> idByTank = new();

    /// The tank's remaining editor path and main target, if it still follows them.
    internal static (List<Vector3> Path, VehicleBehaviour? Target) Plan(VehicleBehaviour tank)
    {
        var r = runners?.FirstOrDefault(x => x.Tank != null && x.Tank.Pointer == tank.Pointer);
        if (r == null) return (new(), null);
        var path = r.Unit.Path.Skip(r.Step).Select(Files.Vector).ToList();
        VehicleBehaviour? target = r.Unit.Attack?.Target is { } id && tanksById.TryGetValue(id, out var t) && t != null ? t : null;
        return (path, target);
    }

    static void Give(Runner r, IOrderReceiver receiver, Order order)
    {
        Send(receiver, order);
        r.Given = true;
        Trace.Write($"order {r.Unit.Id}: {order.GetLog(receiver)}");
    }

    /// Which way to face at path point `i`: the way it came, or at the last point toward the target if it has one.
    static Vector3 Facing(BattleUnit unit, List<float[]> path, int i)
    {
        var at = Files.Vector(path[i]);
        if (i == path.Count - 1 && unit.Attack?.Target is { } id && tanksById.TryGetValue(id, out var enemy) && enemy != null)
            return Level(enemy.Position - at);
        var from = i > 0 ? Files.Vector(path[i - 1]) : Files.Vector(unit.Position);
        return Level(at - from);
    }

    static Vector3 Level(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward; }
    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

    static List<BattleUnit>? toPlace;
    static float placeSince;
    // The battle's spawns from before the restart, to tell the new ones apart if the game keeps the old in its list.
    static readonly HashSet<IntPtr> before = new();

    /// The height of the ground under `at`, not counting the tank itself (or `at` if nothing's there).
    static float GroundBelow(Transform tank, Vector3 at)
    {
        foreach (var hit in Physics.RaycastAll(at + Vector3.up * 50, Vector3.down, 500).OrderBy(h => h.distance))
            if (hit.collider != null && !hit.collider.transform.IsChildOf(tank)) return hit.point.y;
        return at.y;
    }
}

/// A line straight to BepInEx\SprocketBattles-trace.log, written at once: the game's own log is written in batches,
/// and a crash loses the last of it.
internal static class Trace
{
    static readonly string File = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "SprocketBattles-trace.log");
    // Kept open and flushed per line: opening the file for every line cost more than the line, with many tanks ordered.
    static StreamWriter? writer;

    internal static void Write(string line)
    {
        Plugin.ModLog.LogInfo(line);
        try
        {
            writer ??= new StreamWriter(new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch (Exception) { writer = null; }
    }
}

/// Where battle files and blueprints live.
internal static class Files
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket");
    internal static string Battles => Path.Combine(Root, "Battles");

    /// A battle's file: its name, with what a file name can't have taken out.
    internal static string BattlePath(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((string.IsNullOrWhiteSpace(name) ? "Unnamed battle" : name.Trim()).Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(Battles, clean + ".json");
    }

    /// A saved battle (named after its file if it has no name: battles from before names, saved per map), or null.
    internal static BattleFile? ReadBattle(string path)
    {
        try
        {
            var battle = BattleFile.FromJson(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(battle.Name)) battle.Name = Path.GetFileNameWithoutExtension(path);
            return battle;
        }
        catch (Exception ex) { Plugin.ModLog.LogError($"couldn't read the battle {path}: {ex.Message}"); return null; }
    }

    /// Every saved battle, newest first.
    internal static List<(string Path, BattleFile Battle)> SavedBattles()
    {
        if (!Directory.Exists(Battles)) return new();
        return Directory.GetFiles(Battles, "*.json").OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(p => (p, ReadBattle(p))).Where(b => b.Item2 != null).Select(b => (b.p, b.Item2!)).ToList();
    }

    /// A blueprint path as saved in a battle file: relative to Documents\My Games\Sprocket when it's in there.
    /// Or "game:" and its path in the game's own files (its scenario vehicles: AT guns, enemy tanks, targets).
    internal static string Relative(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(Root, full);
        var game = Path.GetFullPath(Application.streamingAssetsPath);
        return full.StartsWith(game, StringComparison.OrdinalIgnoreCase) ? "game:" + Path.GetRelativePath(game, full) : path;
    }

    internal static string Absolute(string path) =>
        path.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Application.streamingAssetsPath, path[5..])
        : Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

    /// The vehicles that come with the game (its scenarios' AT guns and tanks, historical tanks, targets), AT guns first.
    internal static List<(string Path, string Name)> GameDesigns()
    {
        var dir = Path.Combine(Application.streamingAssetsPath, "Blueprints", "Vehicles");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.blueprint")
            .OrderBy(p => IsATGun(p) ? 0 : 1).ThenBy(p => p)
            .Select(p => (Relative(p), $"{Path.GetFileNameWithoutExtension(p)} (base game)")).ToList();
    }

    /// An anti-tank gun by its name ("NTL AT Gun", "TaigaATGun", "FieldsLightAT").
    internal static bool IsATGun(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"AT( ?Gun)?$|ATGun", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static Vector3 Vector(float[] v) => new(v[0], v[1], v[2]);
    internal static float[] Array(Vector3 v) => new[] { v.x, v.y, v.z };

    /// Every vehicle design in every faction, as (relative path, name), the player's own faction first, then the
    /// game's own vehicles (AT guns first).
    internal static List<(string Path, string Name)> Designs() => FactionDesigns().Concat(GameDesigns()).ToList();

    static List<(string Path, string Name)> FactionDesigns()
    {
        var factions = Path.Combine(Root, "Factions");
        if (!Directory.Exists(factions)) return new();
        string current = "";
        try { current = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "HD", "Sprocket", "CurrentFaction")).Trim(); }
        catch (Exception) { }
        return Directory.GetDirectories(factions)
            .OrderBy(f => Path.GetFileName(f) == current ? 0 : 1).ThenBy(f => f)
            .SelectMany(f => Directory.Exists(Path.Combine(f, "Blueprints", "Vehicles"))
                ? Directory.GetFiles(Path.Combine(f, "Blueprints", "Vehicles"), "*.blueprint").OrderBy(p => p) : Enumerable.Empty<string>())
            .Select(p => (Relative(p), $"{Path.GetFileNameWithoutExtension(p)} ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(p))))})"))
            .ToList();
    }
}

internal static class Guard
{
    /// An exception thrown back into the game could take it down: written to the trace instead.
    internal static void Run(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // The same error again (a panel drawn every frame) is logged once, not a wall of it.
            string text = ex.ToString();
            if (last.TryGetValue(what, out var seen) && seen == text) return;
            last[what] = text;
            Plugin.ModLog.LogError($"{what}: {ex}");
            Trace.Write($"{what} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static readonly Dictionary<string, string> last = new();
}
