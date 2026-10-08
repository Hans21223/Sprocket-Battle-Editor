using Sprocket;
using Sprocket.CustomBattles;
using Sprocket.Orders;
using Sprocket.Vehicles;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles.Spawning;
using UnityEngine;
using Sprocket.ArtificialIntelligence;
using Sprocket.VehicleControl;

namespace SprocketBattles;

/// The battle side: the running custom battle's map and tanks, and playing an edited battle in it. Playing rewrites the
/// custom battle's own teams and native spawn locators to the battle file's tanks and poses, then restarts it
/// (the game's Retry). Tanks are registered after spawning, without changing live physics transforms.
/// Async loading/spawning methods with value-type arguments remain unhooked.
internal static partial class Battle
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
        // VehicleSpawn records appear one design at a time; a ready first record doesn't finish native setup.
        if (mode.gameState != DeathmatchGameMode.ModeState.Running) return false;
        var spawns = mode.vehicleSpawns;
        if (spawns == null || spawns.Count == 0) return false;
        for (int i = 0; i < spawns.Count; i++) if (spawns[i] == null || !spawns[i].Spawned) return false;
        return true;
    }

    /// Restart the battle with the file's tanks: the custom battle's teams get them, one unit per design in the order the
    /// file spawns them, using their authored positions before native physics setup. A reason it can't, or null.
    internal static string? Play(DeathmatchGameMode mode, BattleFile file)
    {
        if (restart != null) return "The battle is already preparing to restart. Please wait.";
        if (!Spawned(mode)) return "The map's tanks are still loading. Wait for setup to finish before playing.";
        var teams = mode.context?.SetupContext?.SetupConfig?.TryCast<BattleConfig>()?.Teams;
        if (teams == null) return "This battle isn't a custom battle, so it can't be restarted with these tanks.";
        if (teams.Any(t => t == null)) return "The battle's team setup isn't ready yet.";
        var previous = Enumerable.Range(0, teams.Length).Select(t => teams[t].Units).ToArray();
        if (Fill(teams, file) is { } error) return error;
        var spawns = mode.vehicleSpawns;
        if (spawns != null) for (int i = 0; i < spawns.Count; i++) if (spawns[i] != null) before.Add(spawns[i].Pointer);
        try { SpawnPlan.Apply(mode); }
        catch (Exception ex)
        {
            Trace.Write($"play '{file.Name}': native spawn preparation failed: {ex}");
            for (int t = 0; t < teams.Length; t++) teams[t].Units = previous[t];
            toPlace = null; playing = null; before.Clear();
            return $"Couldn't prepare native tank spawn positions: {ex.Message}";
        }
        // Retry releases the current tanks synchronously before its first await. Leave must restore the
        // editor's renderer/input/camera references first, and Unity must flush destroyed markers this frame.
        restart = new RestartRequest(mode, teams, previous, new RestartGate(Time.frameCount, Time.fixedTime), file.Name);
        Trace.Write($"play '{file.Name}' on {file.Map}: {toPlace!.Count} tanks, restart queued at frame {Time.frameCount}");
        return null;
    }

    sealed record RestartRequest(DeathmatchGameMode Mode, Il2CppReferenceArray<TeamDefinition> Teams,
        Il2CppSystem.Collections.Generic.Dictionary<UnitDefinition, UnitInstanceInfo>[] Previous,
        RestartGate Gate, string Name);

    static RestartRequest? restart;
    internal static bool RestartQueued => restart != null;
    internal static bool PreparedNativeRetry => toPlace != null || restart != null;

    /// Called at the end of the frame, after native Update/LateUpdate and editor cleanup, before releasing tanks.
    internal static void StepRestart(bool editorViewActive)
    {
        var request = restart;
        if (request == null || !request.Gate.TryBegin(Time.frameCount, Time.fixedTime, editorViewActive, Time.timeScale <= 0)) return;
        // Clear before native code: any callbacks/repeated button input must not launch the same retry twice.
        restart = null;
        try
        {
            if (request.Mode == null || !request.Mode.gameObject.scene.isLoaded)
                throw new InvalidOperationException("The battle's map closed before the restart");
            if (!Spawned(request.Mode))
                throw new InvalidOperationException("The battle's native setup changed before the restart");
            if (!SpawnPlan.ReadyFor(request.Mode) || toPlace == null || toPlace.Count == 0)
                throw new InvalidOperationException("The prepared tank spawn positions are no longer available");
            Trace.Write($"play '{request.Name}': native restart starting at frame {Time.frameCount}, physics time {Time.fixedTime:0.000}, editor closed");
            request.Mode.RetryAsyncVoid();
            Trace.Write($"play '{request.Name}': native restart call returned");
        }
        catch (Exception ex)
        {
            Trace.Write($"play '{request.Name}': native restart failed: {ex}");
            for (int t = 0; t < request.Teams.Length; t++)
                if (request.Teams[t] != null) request.Teams[t].Units = request.Previous[t];
            StopOrders();
            toPlace = null; playing = null; before.Clear();
            BattleEditor.Tell($"Couldn't restart the battle: {ex.Message}. See SprocketBattles-trace.log.");
        }
    }

    /// A battle about to start from the Custom Battle screen (the Battle Editor menu's Play): its teams are the file's.
    internal static string? Prepare(Il2CppReferenceArray<TeamDefinition> teams, BattleFile file)
    {
        if (Fill(teams, file) is { } error) return error;
        Trace.Write($"menu: '{file.Name}' on {file.Map}: {toPlace!.Count} tanks set up");
        return null;
    }

    /// A battle the player picks their own tanks for (the menu's Play, the battle having Pick tanks), on the game's own
    /// screen: the other team is the battle's; the player's team starts empty, with the battle's budget and as many
    /// places as Pick tanks. Prepare (with the picks put in) takes over once they press Start battle. A reason, or null.
    internal static string? PreparePick(Il2CppReferenceArray<TeamDefinition> teams, BattleFile file)
    {
        if (file.CheckData() is { } dataIssue) return dataIssue;
        if (teams.Length == 0 || teams.Any(t => t == null)) return "The game's teams aren't ready yet.";
        if (file.PickCapacity == 0) return "This battle has no player tank positions. Mark Team 1 tanks as player choices in the editor first.";
        foreach (var u in file.Units.Where(u => u.Team != 0))
            if (UnitFor(u.Blueprint) == null) return $"Couldn't read the design {System.IO.Path.GetFileNameWithoutExtension(u.Blueprint)}.";
        RestorePickTeam();
        pickedTeam = (teams[0], teams[0].Budget, teams[0].maxUnits);
        for (int t = 0; t < teams.Length; t++)
        {
            teams[t].Units.Clear();
            if (t == 0) continue;
            foreach (var group in file.SpawnGroups(t, Files.Absolute)) teams[t].Units.Add(UnitFor(group[0].Blueprint)!, new UnitInstanceInfo { Count = group.Count });
        }
        teams[0].Budget = Budget(file.Limits?.Budget > 0 ? file.Limits.Budget : -1);
        teams[0].maxUnits = file.PickCapacity;
        return null;
    }

    /// The player's team's own budget and places back (unlimited), before its tanks are made the battle's: Team 1's
    /// own tanks join the player's picks, which the game would otherwise turn away.
    internal static void UnpickTeam(Il2CppReferenceArray<TeamDefinition> teams)
    {
        if (pickedTeam is not { } was) return;
        teams[0].Budget = Budget(-1);
        teams[0].maxUnits = Math.Max(was.MaxUnits, teams[0].UnitCount);
        pickedTeam = null;
    }

    /// Leaving the picker restores the normal Custom Battle budget and tank limit, even when its screen has closed.
    internal static void RestorePickTeam()
    {
        if (pickedTeam is not { } was) return;
        pickedTeam = null;
        was.Team.Budget = was.Budget;
        was.Team.maxUnits = was.MaxUnits;
    }

    static (TeamDefinition Team, Cost Budget, int MaxUnits)? pickedTeam;

    static Cost Budget(int amount) => new(new Il2CppStructArray<int>(new[] { amount }));

    /// Restore a lineup after visiting the native vehicle designer, reading fresh prices from the saved designs.
    internal static string? RestorePicks(TeamDefinition team, IReadOnlyList<string> blueprints)
    {
        var definitions = new List<(UnitDefinition Unit, int Count)>();
        foreach (var group in blueprints.GroupBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (UnitFor(group.Key) is not { } unit) return $"Couldn't restore the selected design {Path.GetFileNameWithoutExtension(group.Key)}. Choose it again on this screen.";
            definitions.Add((unit, group.Count()));
        }
        team.Units.Clear();
        foreach (var entry in definitions) team.Units.Add(entry.Unit, new UnitInstanceInfo { Count = entry.Count });
        return null;
    }

    /// The tanks the player has picked on the screen (each as many times as its count): name, design file, cost.
    internal static List<(string Name, string Path, int Cost)> Picked(TeamDefinition team)
    {
        var picked = new List<(string, string, int)>();
        foreach (var unit in team.Units)
        {
            var def = unit.Key;
            if (def == null) continue;
            int cost = def.Cost?.quantities is { Length: > 0 } q ? q[0] : 0;
            for (int i = 0; i < Math.Max(0, unit.Value?.Count ?? 0); i++) picked.Add((def.Name, def.Path, cost));
        }
        return picked;
    }

    /// The custom battle's teams made the file's tanks, one unit per design in the order the file spawns them, and
    /// the tanks to place once spawned. A reason it can't, or null.
    /// Why the battle can't be played as it is, or null. The game puts the player in one of Team 1's tanks on the map:
    /// with none its setup fails and the screen stays black. A broken design (Blueprints) can crash the game.
    internal static string? CannotPlay(BattleFile file)
    {
        if (file.CheckData(forPlay: true) is { } dataIssue) return dataIssue;
        if (file.FreeForAll && file.Gauntlet != null) return "Choose either Free-for-All or Gauntlet for this battle.";
        if (file.FreeForAllSpawns?.Check() is { } spacingIssue) return spacingIssue;
        if (file.Gauntlet != null && !Gauntlet.IsCurrent(file)) return "This Gauntlet run has ended. Start a new one from Playing → Gauntlet.";
        if (file.FreeForAll && !FreeForAll.Attached)
            return "Free for all couldn't attach to this game build. Check SprocketBattles-trace.log.";
        if (file.FreeForAll && (file.Units.Count < 2 || file.Units.Count > BattleFile.MaxFreeForAllTanks))
            return $"Free for all needs between 2 and {BattleFile.MaxFreeForAllTanks} tanks (including reserves).";
        if (file.FreeForAll && !file.Units.Any(u => u.Team == 1 && !u.Reserve))
            return "Spawn group 2 needs a starting tank to load Free for all; each tank still fights alone.";
        if (!file.Units.Any(u => u.Team == 0 && !u.Reserve)) return "Team 1 needs a tank on the map at the start: the game puts you in one of them.";
        foreach (var path in file.Units.Select(u => u.Blueprint).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Files.BrokenOf(path) is { } why)
                return $"{Path.GetFileNameWithoutExtension(path)} can't be used: {why}. Open it in the vehicle designer, fix its tracks and save it again.";
        return null;
    }

    static string? Fill(Il2CppReferenceArray<TeamDefinition> teams, BattleFile file)
    {
        if (CannotPlay(file) is { } why) return why;
        if (file.Units.Any(u => u.Team >= teams.Length)) return "A tank refers to a team that isn't in this battle.";
        // Every design read first: the teams only change once they all can be.
        foreach (var u in file.Units)
            if (UnitFor(u.Blueprint) == null) return $"Couldn't read the design {System.IO.Path.GetFileNameWithoutExtension(u.Blueprint)}.";
        var order = new List<BattleUnit>();
        var lineups = new Il2CppSystem.Collections.Generic.Dictionary<UnitDefinition, UnitInstanceInfo>[teams.Length];
        for (int t = 0; t < teams.Length; t++)
        {
            if (teams[t] == null) return $"Team {t + 1} isn't ready yet.";
            var units = lineups[t] = new();
            foreach (var group in file.SpawnGroups(t, Files.Absolute))
            {
                units.Add(UnitFor(group[0].Blueprint)!, new UnitInstanceInfo { Count = group.Count });
                order.AddRange(group);
            }
        }
        // Replace dictionaries instead of clearing the originals so failed spawn planning can restore the lineup.
        for (int t = 0; t < teams.Length; t++) teams[t].Units = lineups[t];
        StopOrders();
        Mission.Stop();
        before.Clear();
        toPlace = order;
        playing = file;
        placeSince = Time.unscaledTime;
        return null;
    }

    /// A battle about to start for editing uses one hidden placeholder per team, without retaining the native
    /// screen's previous lineup. These are only loading vehicles; authored tanks are installed when Play is requested.
    internal static string? FillEmpty(Il2CppReferenceArray<TeamDefinition> teams)
    {
        if (teams.Length == 0 || teams.Any(t => t == null)) return "The battle's team setup isn't ready yet.";
        if (LoadingVehicle() is not { } loading)
            return "No usable loading tank was found. Check the game files or fix a design's tracks in the vehicle designer.";
        for (int t = 0; t < teams.Length; t++)
        {
            teams[t].Units = new();
            teams[t].Units.Add(loading.Unit, new UnitInstanceInfo { Count = 1 });
        }
        Trace.Write($"editor loading vehicle: {loading.Path}; track validation passed; one per team");
        StopOrders();
        Mission.Stop();
        toPlace = null;
        playing = null;
        return null;
    }

    /// Both editor launch paths use the same validated loading tank, preferring the game's shipped designs.
    internal static (string Path, UnitDefinition Unit)? LoadingVehicle()
    {
        // These vehicles also go through native track teardown on Play. Never use an unchecked faction design:
        // an old save with missing track segments can load far enough to open the editor, then crash on Retry.
        var candidates = Files.GameDesigns().Where(d => !Files.IsATGun(d.Path)).Concat(Files.Designs())
            .Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase);
        UnitDefinition? unit = null;
        var path = Blueprints.ChooseLoadingVehicle(candidates, Files.BrokenOf, p => (unit = UnitFor(p)) != null,
            (p, why) => Trace.Write($"editor loading vehicle skipped: {p}: {why}"));
        return path == null || unit == null ? null : (path, unit);
    }

    /// The battle file being played (its tanks placed, its mission and cinematic running), if any.
    internal static BattleFile? Playing => playing;
    static BattleFile? playing;
    static UnityEngine.SceneManagement.Scene? runningScene;

    internal static void OwnScene(DeathmatchGameMode on) => runningScene = on.gameObject.scene;

    internal static void StepSceneLifetime()
    {
        if (runningScene is { } owned && (!owned.IsValid() || !owned.isLoaded)) EndScene();
    }

    internal static void EndScene()
    {
        StopOrders();
        Mission.Stop();
        Puppet.ReleaseAll();
        Puppet.Unlock();
        toPlace = null; playing = null; before.Clear();
        mode = null; lookedAt = -10;
        slowBefore.Clear();
    }

    /// A custom battle unit for a design, made the way the Custom Battle screen makes its own (name, cost, icon): the
    /// screen prices every unit, and one without a cost broke it (an error every frame, and a crash going back to the
    /// menu). Null if the game can't read the design.
    internal static UnitDefinition? UnitFor(string blueprint)
    {
        try
        {
            var path = Files.Absolute(blueprint);
            var info = new FileInfo(path);
            if (!info.Exists) { Units.Remove(path); Trace.Write($"couldn't find the design {path}"); return null; }
            long stamp = info.LastWriteTimeUtc.Ticks, size = info.Length;
            if (Units.TryGetValue(path, out var known) && known.Stamp == stamp && known.Size == size) return known.Unit;
            var card = Sprocket.Vehicles.Serialization.VehicleCard.LoadVehicleCard(path);
            var unit = card == null ? null : UnitSelectionLoader.NewUnit(card);
            if (unit != null) Units[path] = (unit, stamp, size);
            else Units.Remove(path);
            if (unit == null) Trace.Write($"couldn't read the design {path}");
            return unit;
        }
        catch (Exception ex) { Trace.Write($"couldn't read the design {blueprint}: {ex.Message}"); return null; }
    }

    static readonly Dictionary<string, (UnitDefinition Unit, long Stamp, long Size)> Units = new(StringComparer.OrdinalIgnoreCase);

    /// Register each completed native spawn without relocating an enabled physics assembly.
    /// Start the mission once all of the battle's tanks have spawned.
    internal static void PlaceSpawned()
    {
        if (restart != null || toPlace == null) return;
        if (!SpawnPlan.ReadyFor(Mode))
        {
            if (SpawnPlan.Failure is { } failure)
            {
                Trace.Write($"spawn plan: registration cancelled: {failure}");
                BattleEditor.Tell($"Couldn't prepare the battle's spawn positions: {failure}");
                toPlace = null;
                return;
            }
            // Initial scene loading hasn't installed the plan yet. Never silently start orders at default spawns.
            if (Time.unscaledTime - placeSince > 240)
            {
                Trace.Write("spawn plan: no active native spawn plan after 4 minutes; battle registration stopped");
                BattleEditor.Tell("The battle's spawn positions couldn't be prepared. See SprocketBattles-trace.log.");
                toPlace = null;
            }
            return;
        }
        var spawns = Mode?.vehicleSpawns;
        var fresh = new List<VehicleSpawn>();
        if (spawns != null)
            for (int i = 0; i < spawns.Count; i++)
                if (spawns[i] != null && !before.Contains(spawns[i].Pointer)) fresh.Add(spawns[i]);
        var order = toPlace;
        var pending = Enumerable.Range(0, Math.Min(order.Count, fresh.Count))
            .Where(i => fresh[i].Spawned && !placed.ContainsKey(order[i].Id)).ToList();
        var joints = pending.Count > 0 ? UnityEngine.Object.FindObjectsOfType<Joint>() : null;
        if (pending.Count > 0) Physics.SyncTransforms();
        foreach (int i in pending)
        {
            var gateway = fresh[i].Vehicle?.TryCast<IVehicleGateway>();
            var root = gateway?.transform;
            if (root == null) { Trace.Write($"placing: tank {i} has no transform"); continue; }
            var spawn = root.position;
            var assembly = SpawnAssembly.Capture(root, joints!, order[i].Id);
            if (!assembly.HasHealthyPose)
            {
                Trace.Write($"placing: {order[i].Id} already has an invalid native spawn pose at {spawn}; registration stopped");
                BattleEditor.Tell($"{order[i].Id} has an invalid native spawn pose. See SprocketBattles-trace.log.");
                toPlace = null;
                return;
            }
            // Native physics jobs maintain their own body state. The spawn plan already supplies the authored
            // pose before component setup; changing it here can throw a tank hundreds of metres upward.
            Trace.Write($"placing: {order[i].Id} registered native pose {spawn}; no live relocation");
            placed[order[i].Id] = new PlacedTank { Assembly = assembly, At = root.position, Since = Time.time };
            if ((gateway!.Behaviour?.TryCast<VehicleBehaviour>() ?? root.GetComponentInChildren<VehicleBehaviour>()) is { } tank)
            {
                tanksById[order[i].Id] = tank;
                var unit = order[i];
                // A designer draft is a one-off file (new each Play): nothing to keep its shape or speed for.
                if (!Files.Absolute(unit.Blueprint).StartsWith(Path.Combine(Files.Battles, "Drafts"), StringComparison.OrdinalIgnoreCase))
                {
                    Guard.Run("travel", () => Travel.Learn(unit.Blueprint, gateway, tank.Mass));
                    Guard.Run("shapes", () => Shapes.Learn(unit.Blueprint, root));
                }
            }
        }
        if (pending.Count > 0) Physics.SyncTransforms();
        if (placed.Count < order.Count)
        {
            if (Time.unscaledTime - placeSince > 240)
            {
                Trace.Write($"placing: gave up, {placed.Count} of {order.Count} tanks placed in 4 minutes");
                toPlace = null;
                BattleEditor.Tell("Some tanks didn't finish spawning. See SprocketBattles-trace.log.");
            }
            return;
        }
        toPlace = null;
        var tanks = new Dictionary<string, VehicleBehaviour>(tanksById);
        Trace.Write($"placing: registered {order.Count} tanks spawned at the planned native positions");
        foreach (var (id, t) in tanks)
            if (t != null && t.ControlType == Sprocket.Vehicles.Control.ControlType.Player && placed.TryGetValue(id, out var p))
            {
                var bodies = p.Root.GetComponentsInChildren<Rigidbody>();
                Trace.Write($"placing: you drive {id}: {bodies.Length} bodies ({bodies.Count(b => b.isKinematic)} kinematic), {p.Root.GetComponentsInChildren<Joint>().Length} joints");
            }
        StartOrders(order, tanks);
        if (playing != null)
        {
            if (Mode is { } on && !string.Equals(Map(on), playing.Map, StringComparison.OrdinalIgnoreCase))
            {
                Trace.Write($"placing: '{playing.Name}' was made on {playing.Map} but this is {Map(on)}");
                BattleEditor.Tell($"{playing.Name} was made on {playing.Map}, but the game started {Map(on)}: the tanks may be off the map.");
            }
            Guard.Run("mission", () => Mission.Start(playing, tanks));
            try { FreeForAll.Start(playing, tanks); }
            catch (Exception ex)
            {
                Trace.Write($"free-for-all: setup failed: {ex}");
                StopOrders();
                playing = null;
                Mission.Stop();
                Mission.End(false, "BATTLE SETUP FAILED", $"Couldn't start Free-for-All: {ex.Message}. See SprocketBattles-trace.log.");
                BattleEditor.Tell($"Couldn't start Free-for-All: {ex.Message}");
                return;
            }
            try { Gauntlet.Started(playing, tanks); }
            catch (Exception ex)
            {
                Trace.Write($"gauntlet: setup failed: {ex}");
                Mission.End(false, "BATTLE SETUP FAILED", ex.Message);
                return;
            }
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
    static Dictionary<string, BattleUnit> unitsById = new();
    static float nextTick;

    static void StartOrders(List<BattleUnit> units, Dictionary<string, VehicleBehaviour> tanks)
    {
        tanksById = tanks;
        unitsById = units.ToDictionary(u => u.Id);
        idByTank = tanks.Where(p => p.Value != null).ToDictionary(p => p.Value.Pointer, p => p.Key);
        held.Clear();
        // Every tank with orders runs them whenever the AI drives it: the tank the player drives waits (see Step).
        runners = units.Where(u => !u.ForceStopped && u.Orders != null && tanks.ContainsKey(u.Id))
                       .Select(u => new Runner { Unit = u, Tank = tanks[u.Id], Since = Time.time }).ToList();
        foreach (var r in runners) Hold(r.Tank);
        // A tank with no path holds its ground and only shoots: the game's orders (a mission's advance, its formation)
        // are dropped. A quick battle's tanks (all left at the game's spawns) keep the game's AI.
        bool quick = units.All(u => u.AtSpawn);
        int holding = 0, locked = 0;
        ClearMovementHolds(); thrown.Clear();
        Puppet.Unlock();
        foreach (var u in units)
        {
            if (!tanks.TryGetValue(u.Id, out var tank) || tank == null) continue;
            // Track the current player too: if control later passes to another tank, its idle AI must hold.
            if (!quick || u.ForceStopped) idleCandidates[tank.Pointer] = tank;
            if (u.ForceStopped) explicitStops.Add(tank.Pointer);
            // Not the tank you drive (the game picks one when none is marked): 0.17.6 held it too and the view went blank.
            if (u.Control == "player" || tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player) continue;
            // Holding applies only to the driver: the commander keeps seeing targets and the guns keep firing.
            if (MovementHoldPolicy.AtStart(quick, u.ForceStopped, u.Path.Count))
            {
                Hold(tank);
                SetMovementHold(tank);
                holding++;
            }
            if (u.NoTurret || u.NoFire) { Puppet.Lock(tank, u.NoTurret, u.NoFire); locked++; }
        }
        driver = units.FirstOrDefault(u => !u.Reserve && u.Control == "player" && tanks.ContainsKey(u.Id));
        Trace.Write($"orders: {holding} tanks with no path hold their ground, {locked} with their turret or fire kept still");
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

    internal static void StopOrders()
    {
        FreeForAll.Stop();
        runningScene = null;
        if (restart != null) Trace.Write($"play '{restart.Name}': queued restart cancelled");
        restart = null;
        runners = null; attacks.Clear(); held.Clear(); blocked.Clear(); ClearMovementHolds();
        tanksById.Clear(); unitsById.Clear(); idByTank.Clear(); driver = null;
        placed.Clear(); thrown.Clear();
        SpawnSafety.Clear();
        SpawnPlan.Clear();
    }

    // ---------- driver-only stopping ----------
    // Do not send a move back to the original position: it made a stopped tank reposition itself after a push.
    // Native movement tasks are cleared and the crew's ordinary controls brake it; physics and gun AI stay active.
    static readonly Dictionary<IntPtr, VehicleBehaviour> idleCandidates = new();
    static readonly Dictionary<IntPtr, VehicleBehaviour> movementHolds = new();
    static readonly Dictionary<IntPtr, VehicleBehaviour> heldDrivers = new();
    static readonly Dictionary<IntPtr, Vector3> manualMoves = new();
    static readonly Dictionary<IntPtr, float> manualMoveSince = new();
    static readonly Dictionary<IntPtr, MovementBrake> brakes = new();
    static readonly HashSet<IntPtr> stoppedCommanders = new();
    // An idle tank can receive an automatic FFA goal; an explicit Stop must remain until a new player order.
    static readonly HashSet<IntPtr> explicitStops = new();

    static void ClearMovementHolds()
    {
        foreach (var tank in movementHolds.Values)
            if (tank != null && tank.ControlType != Sprocket.Vehicles.Control.ControlType.Player)
                Guard.Run("release stopped driver", () => { if (Commander(tank)?.driver?.target is { } drive) drive.Solution = default; });
        movementHolds.Clear(); heldDrivers.Clear(); idleCandidates.Clear(); manualMoves.Clear(); brakes.Clear(); stoppedCommanders.Clear(); explicitStops.Clear();
        manualMoveSince.Clear();
    }

    static void ReleaseMovementHold(VehicleBehaviour tank)
    {
        explicitStops.Remove(tank.Pointer);
        bool stopped = movementHolds.Remove(tank.Pointer);
        if (Commander(tank) is { } ai) stoppedCommanders.Remove(ai.Pointer);
        foreach (var key in heldDrivers.Where(p => p.Value == null || p.Value.Pointer == tank.Pointer).Select(p => p.Key).ToList())
        { heldDrivers.Remove(key); brakes.Remove(key); }
        if (stopped && tank.ControlType != Sprocket.Vehicles.Control.ControlType.Player && Commander(tank)?.driver?.target is { } drive)
            drive.Solution = default;
    }

    static void SetMovementHold(VehicleBehaviour tank)
    {
        var ai = Commander(tank);
        if (ai?.driver is not { } drive || tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player) return;
        if (UnitOf(tank) is { } unit && Mission.Waiting(unit.Id)) return;
        if (movementHolds.TryAdd(tank.Pointer, tank))
        {
            ai.oneOffMoveOrderCts?.Cancel();
            ai.formationTasksCts?.Cancel();
            ai.formation?.Exit();
            Trace.Write($"order {NameOf(tank)}: force stop; aiming and firing remain active");
        }
        stoppedCommanders.Add(ai.Pointer);
        heldDrivers[drive.Pointer] = tank;
        Brake(drive);
    }

    static void Brake(DriverAI driver)
    {
        driver.ClearTasks();
        driver.ClearPath();
        if (driver.target is not { } target) return;
        float speed = driver.body != null ? Vector3.Dot(driver.body.linearVelocity, driver.body.transform.forward) : 0;
        if (!brakes.TryGetValue(driver.Pointer, out var brake)) brakes[driver.Pointer] = brake = new MovementBrake();
        target.Solution = new DriveSolution { Steer = 0, Throttle = brake.Throttle(speed) };
    }

    /// Called by the driver hook only: manual controls and unrelated AI tanks always retain their ordinary input.
    internal static bool StopDriver(DriverAI driver)
    {
        if (!heldDrivers.TryGetValue(driver.Pointer, out var tank)) return false;
        if (tank == null) { heldDrivers.Remove(driver.Pointer); return false; }
        if (tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player || Puppet.Of(tank)?.Drive == true) return false;
        Brake(driver);
        return true;
    }

    internal static bool IsForceStopped(VehicleBehaviour tank) => tank != null && movementHolds.ContainsKey(tank.Pointer);

    /// Replace all movement with braking. This affects the AI driver, leaving its gun crews and physics running.
    internal static bool ForceStop(VehicleBehaviour tank)
    {
        if (tank == null || tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player || Commander(tank)?.driver == null) return false;
        Free(tank);
        Hold(tank);
        explicitStops.Add(tank.Pointer);
        idleCandidates[tank.Pointer] = tank;
        Commander(tank)?.attackOrderCts?.Cancel();
        FireAtWill(tank);
        SetMovementHold(tank);
        return IsForceStopped(tank);
    }

    static void HoldIdleDrivers()
    {
        foreach (var (key, tank) in idleCandidates.ToList())
        {
            if (tank == null) { idleCandidates.Remove(key); movementHolds.Remove(key); continue; }
            if (tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player || Puppet.Of(tank)?.Drive == true) continue;
            var unit = UnitOf(tank);
            if (unit != null && Mission.Waiting(unit.Id)) continue;
            if (IsForceStopped(tank)) { SetMovementHold(tank); continue; }
            var runner = runners?.FirstOrDefault(r => r.Tank != null && r.Tank.Pointer == tank.Pointer);
            bool path = runner != null && runner.Step < runner.Unit.Path.Count;
            bool attacking = attacks.Any(a => a.Tank != null && a.Tank.Pointer == tank.Pointer && !Out(a.Enemy)
                && (a.Automatic || a.Approach != AttackApproachType.Passive));
            bool moving = manualMoves.TryGetValue(tank.Pointer, out var destination);
            if (moving && MovementHoldPolicy.ManualFinished(Flat(tank.Position, destination),
                    Time.time - manualMoveSince.GetValueOrDefault(tank.Pointer), Commander(tank)?.driver?.CurrentTask != null))
            { manualMoves.Remove(tank.Pointer); manualMoveSince.Remove(tank.Pointer); moving = false; }
            if (MovementHoldPolicy.Idle(path, attacking, moving)) { Hold(tank); SetMovementHold(tank); }
        }
    }

    // ---------- startup physics diagnostics ----------

    // Watch the assembly only through the initial spawn window; native job buffers cannot be repaired by teleporting.
    sealed class PlacedTank
    {
        internal SpawnAssembly Assembly = null!;
        internal Transform Root => Assembly.Root;
        internal Vector3 At;
        internal float Since;
    }
    static readonly Dictionary<string, PlacedTank> placed = new();
    static readonly HashSet<string> thrown = new();

    // Run every frame during startup, independently of AI orders and their initial two-second delay.
    static void WatchThrown()
    {
        foreach (var (id, pose) in placed)
        {
            if (pose.Root == null || thrown.Contains(id) || Time.time - pose.Since > 8) continue;
            var position = pose.Root.position;
            bool invalid = pose.Assembly.HasInvalidPhysics;
            bool launched = false;
            // No ground queries while the tank stays near its placement height. Unknown ground isn't proof of a launch.
            if (!invalid && position.y - pose.At.y > 30)
                launched = SpawnGround.Height(position.x, position.z, pose.At.y) is { } floor && position.y - floor > 30;
            if (!invalid && !launched) continue;
            thrown.Add(id);
            bool player = tanksById.TryGetValue(id, out var tank) && tank != null
                && tank.ControlType == Sprocket.Vehicles.Control.ControlType.Player;
            Trace.Write($"thrown: {id} is at {position} ({(invalid ? "invalid physics" : "above the ground")}, {(player ? "you drive it" : "AI")})");
            // Moving an enabled track cannot reset its private solver state; keep the evidence without repeating it.
            BattleEditor.Tell($"{id} developed invalid spawn physics. See SprocketBattles-trace.log.");
        }
        if (toPlace == null)
            foreach (var id in placed.Where(p => p.Value.Root == null || Time.time - p.Value.Since > 8).Select(p => p.Key).ToList())
            { placed.Remove(id); thrown.Remove(id); }
    }
    // ---------- your orders over the game's ----------

    /// Your orders override the game's: a tank you command (its battle path and target, or the command view's orders)
    /// takes orders from the Battle Editor only (a mission's scripted orders and the command wheel are dropped) and
    /// leaves its formation (a formation leader drags its followers to their places). Off: the game's own orders mix in.
    internal static bool OverrideGame = true;

    // The AI (CommanderAI) of each tank you command, and which game orders were dropped for which tank (said once each).
    static readonly Dictionary<IntPtr, string> held = new();
    static readonly HashSet<(IntPtr, string)> blocked = new();
    static bool sending;

    internal static bool Holds(Sprocket.ArtificialIntelligence.CommanderAI ai) => held.ContainsKey(ai.Pointer)
        && (OverrideGame || stoppedCommanders.Contains(ai.Pointer));

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

    /// From now on the tank takes your orders only: out of its formation (if asked), its formation tasks cancelled.
    static void Hold(VehicleBehaviour tank, bool leaveFormation = true)
    {
        if (Commander(tank) is not { } ai || !held.TryAdd(ai.Pointer, NameOf(tank)) || !leaveFormation) return;
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
        ReleaseMovementHold(tank);
        idleCandidates.Remove(tank.Pointer);
        manualMoves.Remove(tank.Pointer);
        manualMoveSince.Remove(tank.Pointer);
        runners?.RemoveAll(r => r.Tank != null && r.Tank.Pointer == tank.Pointer);
        if (attacks.RemoveAll(a => a.Tank == null || a.Tank.Pointer == tank.Pointer) > 0) Commander(tank)?.attackOrderCts?.Cancel();
        if (Commander(tank) is { } ai) held.Remove(ai.Pointer);
    }

    /// You drive `tank` from now on (any team's); the one you drove goes back to its AI.
    internal static bool Drive(VehicleBehaviour tank)
    {
        if (Mode?.vehicleControlState is not { } control || tank.TryCast<IVehicleBehaviour>() is not { } target) return false;
        Free(tank);
        Puppet.Unlock(tank); // your own aim and trigger
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
        if (placed.Count > 0) Guard.Run("spawn safety", WatchThrown);
        Guard.Run("idle driver holds", HoldIdleDrivers);
        if (Time.time < nextTick) return;
        nextTick = Time.time + 0.5f;
        Guard.Run("team auto engagement", AutoEngageTeams);
        Guard.Run("attacks", WatchAttacks);
        if (driver != null)
        {
            var mine = tanksById[driver.Id];
            Guard.Run("you drive", () =>
            {
                if (mine != null && Mode?.vehicleControlState is { } control && mine.TryCast<IVehicleBehaviour>() is { } tank)
                {
                    control.SetControlTarget(tank);
                    ReleaseMovementHold(mine);
                    Trace.Write($"you drive {driver.Id} now");
                }
            });
            driver = null;
        }
        if (runners != null) foreach (var r in runners) Guard.Run($"orders for {r.Unit.Id}", () => Step(r));
        Guard.Run("idle driver holds", HoldIdleDrivers);
    }

    static void AutoEngageTeams()
    {
        if (playing == null || playing.FreeForAll || Gauntlet.IsCurrent(playing)) return;
        foreach (var tank in tanksById.Values.Distinct())
        {
            if (tank == null || Out(tank) || !CanAutoEngage(tank)) continue;
            var currentTarget = AutomaticTarget(tank);
            if (currentTarget != null && !Out(currentTarget)) continue;
            var opponent = tanksById.Values
                .Where(o => o != null && !Out(o) && (UnitOf(tank) is { } ut && UnitOf(o) is { } uo ? ut.Team != uo.Team : (tank.ID.TeamID & o.ID.TeamID) == 0))
                .OrderBy(o => Flat(tank.Position, o.Position))
                .FirstOrDefault();
            if (opponent != null)
            {
                SendAutoAgainst(tank, opponent);
                FireAtWill(tank);
            }
        }
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
        // After its path it goes for the target; with no path it shoots it from where it stands.
        if (foe != null) StartAttack(r.Tank, foe, $"{r.Unit.Id} on {r.Unit.Attack!.Target}", r.Unit.Attack.Engage != "fireOnTheMove",
                                     path.Count > 0 ? AttackApproachType.Aggressive : AttackApproachType.Passive);
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
        manualMoves[tank.Pointer] = to;
        manualMoveSince[tank.Pointer] = Time.time;
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
        idleCandidates[tank.Pointer] = tank;
        return true;
    }

    static string NameOf(VehicleBehaviour tank) => UnitOf(tank)?.Id ?? tank.ID.ToString();

    // ---------- attacks: watched while the target fights, given again if the AI drops them ----------

    sealed class Attack
    {
        public VehicleBehaviour Tank = null!, Enemy = null!;
        public string Name = "";
        public bool StopToFire;
        public bool Automatic;
        public bool Closing;
        public float LastSeenAt;
        public AttackApproachType Approach;
        public float Since, GivenAt, LoggedAt;
    }

    static readonly List<Attack> attacks = new();

    static void StartAttack(VehicleBehaviour tank, VehicleBehaviour enemy, string name, bool stopToFire,
                            AttackApproachType approach = AttackApproachType.Aggressive, bool automatic = false)
    {
        attacks.RemoveAll(a => a.Tank == null || a.Tank.Pointer == tank.Pointer);
        var attack = new Attack { Tank = tank, Enemy = enemy, Name = name, StopToFire = stopToFire, Approach = approach, Automatic = automatic, Since = Time.time, LoggedAt = Time.time };
        attacks.Add(attack);
        GiveAttack(attack, "given");
    }

    static void GiveAttack(Attack a, string how)
    {
        var receiver = a.Tank.OrderReciever;
        if (receiver == null || !receiver.CanReceiveOrder) return;
        if (a.Automatic || a.Approach != AttackApproachType.Passive) ReleaseMovementHold(a.Tank);
        FireAtWill(a.Tank);
        if (Commander(a.Tank) is { } cmd)
        {
            cmd.FireMode = Sprocket.ArtificialIntelligence.FireMode.FireAtWill;
        }
        var order = AttackOrderOn(a.Enemy, a.StopToFire, a.Approach);
        Send(receiver, order);
        a.GivenAt = Time.time;
        Trace.Write($"attack {a.Name} {how}: {order.GetLog(receiver)}; {Sight(a.Tank, a.Enemy)}");
    }

    /// Out of the fight: can neither move nor shoot, or all crew knocked out, or hull/components destroyed (or gone).
    internal static bool Out([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] VehicleBehaviour? tank)
    {
        if (tank == null || tank.Pointer == IntPtr.Zero || tank.WasCollected) return true;
        try
        {
            if (!tank.gameObject.activeInHierarchy || !tank.enabled) return true;
            var flags = tank.Flags;
            if ((flags & VehicleFlags.Enabled) == 0) return true;
            if ((flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0) return true;

            int totalCrew = 0;
            int aliveCrew = 0;
            double currentHp = 0;
            double maxHp = 0;

            var entries = tank.healthRegister?.entries;
            int count = entries?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                var comp = entries![i];
                if (comp == null || comp.Pointer == IntPtr.Zero || comp.WasCollected) continue;
                var durable = comp.HealthPool;
                if (durable != null)
                {
                    var h = durable.HealthInfo;
                    if (float.IsFinite(h.Current) && float.IsFinite(h.Max) && h.Max > 0)
                    {
                        currentHp += Math.Clamp(h.Current, 0, h.Max);
                        maxHp += h.Max;
                    }
                }

                var owner = comp.Owner;
                if (owner == null || owner.Pointer == IntPtr.Zero || owner.WasCollected) continue;
                if (owner.TryCast<Sprocket.Vehicles.CrewSystems.CrewSeat>() is { } seat)
                {
                    totalCrew++;
                    float cur = durable?.HealthInfo.Current ?? 1f;
                    if (seat.HealthFraction > 0.001f && cur > 0.001f)
                        aliveCrew++;
                }
                else if (owner.GetIl2CppType()?.Name?.IndexOf("Crew", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    totalCrew++;
                    float cur = durable?.HealthInfo.Current ?? 1f;
                    if (owner.HealthFraction > 0.001f && cur > 0.001f)
                        aliveCrew++;
                }
            }

            if (totalCrew == 0)
            {
                var seats = tank.GetComponentsInChildren<Sprocket.Vehicles.CrewSystems.CrewSeat>(true);
                if (seats != null && seats.Length > 0)
                {
                    for (int i = 0; i < seats.Length; i++)
                    {
                        var s = seats[i];
                        if (s != null && s.Pointer != IntPtr.Zero && !s.WasCollected)
                        {
                            totalCrew++;
                            float cur = s.healthPool?.HealthInfo.Current ?? 1f;
                            if (s.HealthFraction > 0.001f && cur > 0.001f)
                                aliveCrew++;
                        }
                    }
                }
            }

            if (totalCrew > 0 && aliveCrew == 0) return true;
            if (maxHp > 0 && currentHp <= 0.001) return true;
        }
        catch
        {
            try
            {
                return (tank.Flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

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
            if (a.Automatic) Guard.Run($"automatic approach {a.Name}", () => WatchAutoApproach(a));
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
        if (IsForceStopped(tank)) return route;
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
        if (IsForceStopped(tank)) return "Force stopped";
        try { return tank.OrderReciever?.TryCast<Sprocket.ArtificialIntelligence.CommanderAI>()?.driver?.CurrentTask?.GetIl2CppType()?.Name ?? ""; }
        catch (Exception) { return ""; }
    }

    /// The battle file's tank for a game tank, if it was placed in the editor.
    internal static BattleUnit? UnitOf(VehicleBehaviour tank) =>
        idByTank.TryGetValue(tank.Pointer, out var id) && unitsById.TryGetValue(id, out var unit) ? unit : null;

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
        ReleaseMovementHold(r.Tank);
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
    internal static bool PendingPlacement => toPlace != null;
    internal static IReadOnlyList<BattleUnit>? PendingUnits => toPlace;
    static float placeSince;
    // The battle's spawns from before the restart, to tell the new ones apart if the game keeps the old in its list.
    static readonly HashSet<IntPtr> before = new();

}

/// A line straight to BepInEx\SprocketBattles-trace.log, written at once: the game's own log is written in batches,
/// and a crash loses the last of it.
internal static class Trace
{
    static readonly string File = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "SprocketBattles-trace.log");
    // Kept open and flushed per line: opening the file for every line cost more than the line, with many tanks ordered.
    static StreamWriter? writer;
    static readonly object writerLock = new();

    internal static void Write(string line)
    {
        Plugin.ModLog.LogInfo(line);
        lock (writerLock) try
        {
            writer ??= new StreamWriter(new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch (Exception) { try { writer?.Dispose(); } catch { } writer = null; }
    }
}

/// Where battle files and blueprints live.
internal static class Files
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket");
    internal static string Battles => Path.Combine(Root, "Battles");
    /// Where a shared battle's zip is written, and looked for (with Downloads) to put one in.
    internal static string Shared => Path.Combine(Battles, "Shared");
    internal static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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
        Path.GetFullPath(path.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Application.streamingAssetsPath, path[5..])
        : Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

    /// The vehicles that come with the game (its scenarios' AT guns and tanks, historical tanks, targets), AT guns first.
    internal static List<(string Path, string Name)> GameDesigns()
    {
        var dir = Path.Combine(Application.streamingAssetsPath, "Blueprints", "Vehicles");
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.blueprint")
            .OrderBy(p => IsATGun(p) ? 0 : 1).ThenBy(p => p)
            .Select(p => (Relative(p), $"{Path.GetFileNameWithoutExtension(p)} (base game)")).ToList();
    }

    /// An anti-tank gun by its name ("NTL AT Gun", "TaigaATGun"). The game's "FieldsLightAT" is a whole tank, not one.
    internal static bool IsATGun(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"AT ?Gun", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// The faction a design is from (its folder in Factions), or "Base game" for the game's own vehicles.
    internal static string FactionOf(string path)
    {
        if (path.StartsWith("game:", StringComparison.OrdinalIgnoreCase)) return "Base game";
        var parts = path.Split('\\', '/');
        return parts.Length > 2 && parts[0].Equals("Factions", StringComparison.OrdinalIgnoreCase) ? parts[1] : "Other";
    }

    /// The game's eras (StreamingAssets\Eras, custom ones dropped in there too, and custom eras from blueprints), oldest first; read once.
    internal static List<Eras.Era> EraList() => eras ??= LoadEras();
    static List<Eras.Era>? eras;

    static List<Eras.Era> LoadEras()
    {
        var list = Eras.Read(Path.Combine(Application.streamingAssetsPath, "Eras"));
        eras = list;
        try
        {
            var known = new HashSet<string>(list.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            DateTime latest = list.Count > 0 ? list[^1].Start : new DateTime(1945, 9, 3);
            int extraYears = 5;
            foreach (var d in Files.Designs())
            {
                string era = Files.EraOf(d.Path);
                if (!string.IsNullOrEmpty(era) && !era.Equals("No era", StringComparison.OrdinalIgnoreCase) && known.Add(era))
                {
                    latest = latest.AddYears(extraYears);
                    list.Add(new Eras.Era(era, latest));
                    Trace.Write($"eras: discovered custom blueprint era '{era}', ranked at {latest:yyyy.MM.dd}");
                }
            }
        }
        catch (Exception ex) { Trace.Write($"eras: custom era scan exception: {ex.Message}"); }
        return list;
    }

    /// The era a design is from (by the date in its header, or the era it names), or "No era"; looked up again when
    /// its file changes.
    internal static string EraOf(string path)
    {
        var file = Absolute(path);
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(file).Ticks; } catch (Exception) { }
        if (designEras.TryGetValue(file, out var known) && known.Stamp == stamp) return known.Era;
        var era = Eras.Of(EraList(), file) ?? "No era";
        designEras[file] = (stamp, era);
        return era;
    }

    static readonly Dictionary<string, (long Stamp, string Era)> designEras = new(StringComparer.OrdinalIgnoreCase);

    /// What's wrong with a design the game can't build properly (Blueprints.Broken), or null; looked up again when its
    /// file changes.
    internal static string? BrokenOf(string path)
    {
        var file = Absolute(path);
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(file).Ticks; } catch (Exception) { }
        if (broken.TryGetValue(file, out var known) && known.Stamp == stamp) return known.Why;
        segments ??= Blueprints.Segments(Path.Combine(Application.streamingAssetsPath, "Parts"));
        var why = Blueprints.Broken(file, segments);
        broken[file] = (stamp, why);
        if (why != null) Trace.Write($"designs: {Path.GetFileNameWithoutExtension(file)} is broken: {why}");
        return why;
    }

    static readonly Dictionary<string, (long Stamp, string? Why)> broken = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string>? segments;

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
