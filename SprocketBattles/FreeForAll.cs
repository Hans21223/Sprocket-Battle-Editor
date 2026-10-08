using HarmonyLib;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DetectionSystems;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Control;
using UnityEngine;
using NativeMission = Sprocket.MissionFramework.Mission;

namespace SprocketBattles;

/// Separates the finished native spawns into individual combatants. It changes team metadata only;
/// native tank creation, transforms, physics jobs and buffer ownership remain the game's responsibility.
internal static class FreeForAll
{
    internal const int MaxContenders = BattleFile.MaxFreeForAllTanks;
    internal static bool Attached { get; private set; }
    static DeathmatchGameMode? mode;
    static DetectionRegister? detection;
    static TeamID playerTeam;
    static string? initialPlayer;
    static float nextCheck;
    static float nextAiCheck;
    static bool finished;
    static readonly List<Entry> entries = new();
    static readonly HashSet<IntPtr> commanders = new();
    static readonly Dictionary<string, float> chosenAt = new();

    sealed record Entry(string Id, VehicleBehaviour Tank, EntityID Original, EntityID Contender,
        DetectionRegister.DetectableObject Detectable, CommanderAI? Commander, EntityID EnemyFilter, EntityID AllyFilter);

    internal static void Attach(Harmony harmony)
    {
        harmony.PatchAll(typeof(FreeForAllEndHooks));
        harmony.PatchAll(typeof(FreeForAllMissionHook));
        harmony.PatchAll(typeof(FreeForAllFormationHook));
        Attached = true;
    }

    /// Active during native loading as well as combat: the two spawn groups must not decide the FFA result.
    internal static bool Owns(DeathmatchGameMode on) => Attached && Battle.Playing?.FreeForAll == true
        && (mode != null ? mode.Pointer == on.Pointer : Battle.Mode?.Pointer == on.Pointer);

    internal static bool Owns(NativeMission mission)
    {
        var on = mode ?? Battle.Mode;
        if (on == null || !Owns(on) || on.missions == null) return false;
        for (int i = 0; i < on.missions.Length; i++)
            if (on.missions[i]?.Pointer == mission.Pointer) return true;
        return false;
    }

    internal static bool Owns(CommanderAI commander) => mode != null && commanders.Contains(commander.Pointer);

    /// Call after Mission.Start has installed its reserve state, before the AI's first battle order.
    internal static void Start(BattleFile battle, IReadOnlyDictionary<string, VehicleBehaviour> tanks)
    {
        Stop();
        if (!battle.FreeForAll) return;
        if (!Attached) throw new InvalidOperationException("Free-for-all could not attach to this game's mission logic");
        var on = Battle.Mode ?? throw new InvalidOperationException("The free-for-all's native battle is missing");
        var register = on.detectionRegister?.TryCast<DetectionRegister>()
            ?? throw new InvalidOperationException("The free-for-all's native detection register is unavailable");
        if (battle.Units.Count < 2 || battle.Units.Count > MaxContenders)
            throw new InvalidOperationException($"Free-for-all requires 2 to {MaxContenders} tanks");

        // Validate all records before changing any: a partial setup must never leave teammates mismatched.
        string? player = battle.Units.FirstOrDefault(u => u.Control == "player" && !u.Reserve)?.Id
            ?? battle.Units.FirstOrDefault(u => tanks.TryGetValue(u.Id, out var t)
                && t != null && t.ControlType == ControlType.Player)?.Id;
        var ordered = battle.Units.OrderByDescending(u => u.Id == player).ToArray();
        var prepared = new List<Entry>();
        var instanceIds = new HashSet<ushort>();
        for (int n = 0; n < ordered.Length; n++)
        {
            var unit = ordered[n];
            if (!tanks.TryGetValue(unit.Id, out var tank) || tank == null)
                throw new InvalidOperationException($"Free-for-all tank {unit.Id} did not finish spawning");
            var original = tank.ID;
            // Native detection excludes the observer using the low two ID bytes. The loader assigns a
            // different SpawnerID to each spawn group, and BuildNumber advances within that spawner.
            ushort instance = (ushort)(original.BuildNumber | ((byte)original.SpawnerID << 8));
            if (!instanceIds.Add(instance))
                throw new InvalidOperationException($"Free-for-all tank {unit.Id} has a duplicate native instance ID ({instance}); see the spawn setup");
            var contender = new EntityID(original.BuildNumber, original.SpawnerID, original.GroupID,
                (TeamID)FreeForAllRules.TeamMask(n));
            DetectionRegister.DetectableObject? record = null;
            for (int i = 0; i < register.register.Length; i++)
            {
                var item = register.register[i];
                if (item?.transform != null && item.entityID.Equals(original)
                    && item.transform.root == tank.transform.root) { record = item; break; }
            }
            if (record == null) throw new InvalidOperationException($"Free-for-all tank {unit.Id} has no native detection record");
            var ai = tank.OrderReciever?.TryCast<CommanderAI>();
            prepared.Add(new Entry(unit.Id, tank, original, contender, record, ai,
                ai?.visibleTargets?.Filter ?? EntityID.Default, ai?.localAllies?.Filter ?? EntityID.Default));
        }

        mode = on; detection = register; playerTeam = on.playerTeamID;
        initialPlayer = player;
        entries.AddRange(prepared);
        try
        {
            foreach (var entry in entries)
            {
                SetIdentity(entry, entry.Contender);
                if (entry.Commander is { } ai)
                {
                    commanders.Add(ai.Pointer);
                    ai.formationTasksCts?.Cancel();
                    ai.formation?.Exit();
                    SetFilters(ai, entry.Contender.TeamID);
                    ClearTargets(ai);
                    ai.FireMode = FireMode.FireAtWill;
                }
                if (entry.Id == initialPlayer) on.playerTeamID = entry.Contender.TeamID;
                Trace.Write($"free-for-all: {entry.Id} is contender {(byte)entry.Contender.TeamID}, original team {(byte)entry.Original.TeamID}, spawner {entry.Original.SpawnerID}, build {entry.Original.BuildNumber}");
            }
            nextCheck = Time.time + 2;
            nextAiCheck = nextCheck;
            finished = false;
        }
        catch { Stop(); throw; }
    }

    static void SetIdentity(Entry entry, EntityID identity)
    {
        entry.Tank.ID = identity;
        // Detection stores a value copy of the ID. Its public registration API refreshes that copy without
        // replacing the shared register, clearing other objects or touching any tank physics buffers.
        var item = entry.Detectable;
        detection!.Deregister(item.transform);
        detection.Register(identity, item.transform, item.structures);
    }

    static void SetFilters(CommanderAI ai, TeamID team)
    {
        if (ai.visibleTargets != null) ai.visibleTargets.Filter = new EntityID((TeamID)(byte)~(byte)team);
        if (ai.localAllies != null) ai.localAllies.Filter = new EntityID(team);
    }

    static void ClearTargets(CommanderAI ai)
    {
        ai.combat?.CancelPlan();
        ai.attackOrderCts?.Cancel();
        if (ai.visibleTargets != null) { ai.visibleTargets.availableCount = 0; ai.visibleTargets.visibleCount = 0; }
        if (ai.localAllies != null) { ai.localAllies.availableCount = 0; ai.localAllies.visibleCount = 0; }
        if (ai.fieldOfVisionTargets != null) ai.fieldOfVisionTargets.bufferCount = 0;
        if (ai.recognizedTargets != null) ai.recognizedTargets.validTargetCount = 0;
        if (ai.prioritizedTargets != null) ai.prioritizedTargets.validTargetCount = 0;
        ClearLongTargets(ai.longtermTargetBuffer);
        ClearLongTargets(ai.allyLongTermInfo);
    }

    static void ClearLongTargets(LongtermTargetBuffer? targets)
    {
        if (targets == null) return;
        targets.indexMap?.Clear();
        if (targets.buffer == null) return;
        for (int i = 0; i < targets.buffer.Length; i++)
        {
            var item = targets.buffer[i];
            if (item == null) continue;
            item.entityID = EntityID.Default;
            item.features = 0;
            targets.buffer[i] = item;
        }
    }

    internal static void Tick()
    {
        if (entries.Count == 0) return;
        // Scene exit also happens while paused. Release our wrappers then, rather than retaining a finished map.
        if (mode == null || !mode.gameObject.scene.isLoaded || Battle.Playing?.FreeForAll != true || Battle.Mode?.Pointer != mode.Pointer) { Stop(); return; }
        if (finished || Mission.Banner != null || Time.timeScale <= 0 || Time.time < nextCheck) return;
        nextCheck = Time.time + 0.25f;
        if (Time.time >= nextAiCheck)
        {
            nextAiCheck = Time.time + 0.5f;
            foreach (var entry in entries) Guard.Run($"free-for-all AI {entry.Id}", () => Engage(entry));
        }
        var currentPlayer = entries.FirstOrDefault(e => e.Tank != null && e.Tank.ControlType == ControlType.Player)?.Id
            ?? initialPlayer;
        var states = entries.Select(e => new FreeForAllRules.Contender(e.Id,
            e.Tank != null && !Battle.Out(e.Tank),
            Mission.Waiting(e.Id), e.Id == currentPlayer)).ToArray();
        if (FreeForAllRules.Outcome(states) is not { } result) return;
        finished = true;
        string text = result.Draw ? "No tank remains mobile." : result.Won ? "Your tank is the last contender."
            : $"{result.Winner} is the last contender.";
        Trace.Write($"free-for-all: {(result.Draw ? "draw" : $"winner {result.Winner}")}");
        Mission.End(result.Won, result.Draw ? "DRAW" : result.Won ? "VICTORY" : "DEFEAT", text);
    }

    static void Engage(Entry entry)
    {
        var tank = entry.Tank;
        if (!Battle.CanAutoEngage(tank)) return;
        var current = Battle.AutomaticTarget(tank);
        string? currentId = current == null ? null : entries.FirstOrDefault(e => e.Tank != null && e.Tank.Pointer == current.Pointer)?.Id;
        var targets = entries.Select(e => new FreeForAllRules.Target(e.Id,
            e.Tank == null ? float.PositiveInfinity : (e.Tank.Position - tank.Position).sqrMagnitude,
            e.Tank != null && !Mission.Waiting(e.Id) && !Battle.Out(e.Tank))).ToArray();
        bool mayRetarget = !chosenAt.TryGetValue(entry.Id, out var since) || Time.time - since >= 4;
        string? selected = FreeForAllRules.SelectTarget(entry.Id, currentId, targets, mayRetarget);
        if (selected == null || selected == currentId) return;
        var enemy = entries.First(e => e.Id == selected).Tank;
        if (Battle.SendAutoAgainst(tank, enemy)) chosenAt[entry.Id] = Time.time;
    }

    internal static void Stop()
    {
        commanders.Clear();
        foreach (var entry in entries)
        {
            try
            {
                if (entry.Tank == null) continue;
                SetIdentity(entry, entry.Original);
                if (entry.Commander is { } ai)
                {
                    if (ai.visibleTargets != null) ai.visibleTargets.Filter = entry.EnemyFilter;
                    if (ai.localAllies != null) ai.localAllies.Filter = entry.AllyFilter;
                    ClearTargets(ai);
                }
            }
            catch (Exception ex) { Trace.Write($"free-for-all: couldn't restore {entry.Id}: {ex.Message}"); }
        }
        if (mode != null) mode.playerTeamID = playerTeam;
        entries.Clear(); chosenAt.Clear(); mode = null; detection = null; initialPlayer = null; finished = false;
    }
}

/// These native endpoints take only reference arguments. No loading coroutine or CancellationToken value is hooked.
[HarmonyPatch(typeof(DeathmatchGameMode))]
internal static class FreeForAllEndHooks
{
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.RetryAsyncVoid))]
    static bool Retry(DeathmatchGameMode __instance)
    {
        if (!FreeForAll.Owns(__instance) || Battle.PreparedNativeRetry) return true;
        try
        {
            // Raw Retry replaces native tanks without re-registering contender IDs. Queue the usual
            // plan/registration lifecycle instead; its prepared Retry is allowed through next frame.
            string? issue = Battle.Play(__instance, BattleFile.FromJson(Battle.Playing!.ToJson()));
            BattleEditor.Tell(issue ?? "Free-for-All restart prepared. Resume the battle to restart.");
            if (issue != null) Trace.Write("free-for-all: Retry couldn't prepare: " + issue);
        }
        catch (Exception ex) { Trace.Write($"free-for-all: Retry preparation failed: {ex}"); BattleEditor.Tell(ex.Message); }
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.CompleteMission))]
    static bool KeepContendersPlaying(DeathmatchGameMode __instance) => !FreeForAll.Owns(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.AdvanceMission))]
    static bool KeepStagePlaying(DeathmatchGameMode __instance) => !FreeForAll.Owns(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.FailMission))]
    static bool KeepLastContenderPlaying(DeathmatchGameMode __instance) => !FreeForAll.Owns(__instance);
}

[HarmonyPatch(typeof(NativeMission), nameof(NativeMission.MissionUpdate))]
internal static class FreeForAllMissionHook
{
    [HarmonyPrefix]
    static bool KeepSpawnGroupsPlaying(NativeMission __instance, ref Sprocket.MissionFramework.MissionUpdateResult __result)
    {
        if (!FreeForAll.Owns(__instance)) return true;
        __result = Sprocket.MissionFramework.MissionUpdateResult.Running;
        return false;
    }
}

[HarmonyPatch(typeof(CommanderAI), nameof(CommanderAI.UpdateFormationTasks))]
internal static class FreeForAllFormationHook
{
    [HarmonyPrefix]
    static bool NoAlliedFormation(CommanderAI __instance) => !FreeForAll.Owns(__instance);
}
