using HarmonyLib;
using Sprocket;
using Sprocket.GameControl;
using Sprocket.SceneManagement;
using Sprocket.Vehicles;
using UnityEngine;
using NativeMission = Sprocket.MissionFramework.Mission;

namespace SprocketBattles;

/// A run uses the game's ordinary scene/designer/loading lifecycle between rounds. No live tank is rebuilt,
/// teleported or repaired; the quoted repair is paid before the next normally constructed tank fights.
internal static class Gauntlet
{
    enum Phase { Preparing, Loading, Fighting, Intermission, Returning, Finished }
    sealed class Run
    {
        internal string Id = Guid.NewGuid().ToString("N");
        internal GauntletSettings Settings = null!;
        internal GauntletLedger Ledger = null!;
        internal List<(string Path, string Name, int Cost, int EraRank)> Opponents = new();
        internal int Capacity;
        internal Phase Phase;
        internal string HeroPath = "", PendingPath = "", PendingFingerprint = "";
        internal int PendingCost, PendingEraRank;
        internal bool HealthKnown;
        internal DeathmatchGameMode? Mode;
        internal VehicleBehaviour? Hero;
        internal readonly List<VehicleBehaviour> Enemies = new();
        internal float Since, NextCheck;
        internal int LastRemaining = -1;
    }
    static Run? run;
    internal static bool Attached { get; private set; }
    internal static bool CanRefit => run is { Phase: Phase.Intermission } r && r.Ledger.Round < r.Settings.Rounds;
    internal static bool IsCurrent(BattleFile? battle) => run != null && battle?.Gauntlet is { } info
        && info.RunId == run.Id && info.Round == run.Ledger.Round;

    static string SavePath => Path.Combine(Files.Root, "GauntletSave.json");

    internal static void Attach(Harmony harmony)
    {
        harmony.PatchAll(typeof(GauntletEndHooks));
        harmony.PatchAll(typeof(GauntletMissionHook));
        Attached = true;
    }

    internal static bool Owns(DeathmatchGameMode mode) => Attached && IsCurrent(Battle.Playing)
        && Battle.Mode?.Pointer == mode.Pointer;
    internal static bool Owns(NativeMission mission)
    {
        var mode = Battle.Mode;
        if (mode == null || !Owns(mode) || mode.missions == null) return false;
        for (int i = 0; i < mode.missions.Length; i++)
            if (mode.missions[i]?.Pointer == mission.Pointer) return true;
        return false;
    }

    internal static int EraRankOf(string blueprintPath) => EraRank(Files.EraOf(blueprintPath));

    internal static int EraRank(string? eraName)
    {
        if (string.IsNullOrEmpty(eraName) || eraName.Equals("No era", StringComparison.OrdinalIgnoreCase)) return 0;
        var eras = Files.EraList();
        for (int i = 0; i < eras.Count; i++)
            if (string.Equals(eras[i].Name, eraName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    internal static string EraName(int eraRank)
    {
        var eras = Files.EraList();
        if (eraRank >= 0 && eraRank < eras.Count) return eras[eraRank].Name;
        return "Unknown era";
    }

    internal static List<(string Path, string Name, int Cost, int EraRank)> CollectOpponents(GauntletSettings settings)
    {
        var opponents = new List<(string Path, string Name, int Cost, int EraRank)>();
        foreach (var design in MainMenu.GauntletDesigns(settings))
        {
            if (Battle.UnitFor(design.Path) is not { } unit || unit.Cost?.quantities is not { Length: > 0 } cost || cost[0] <= 0) continue;
            int eraRank = EraRankOf(design.Path);
            opponents.Add((design.Path, unit.Name, cost[0], eraRank));
        }
        return GauntletProgression.SortOpponents(opponents);
    }

    internal static void Start(GauntletSettings settings)
    {
        if (Menu.Busy) return;
        if (!Attached) { MainMenu.Tell("Gauntlet couldn't attach to this game build. Check SprocketBattles-trace.log."); return; }
        if (settings.Check() is { } issue) { MainMenu.Tell(issue); return; }
        var opponents = CollectOpponents(settings);
        if (opponents.Count == 0) { MainMenu.Tell("No usable enemy tanks match this faction. Choose another faction."); return; }
        int capacity = MainMenu.EnemySpawnCapacity(settings.Map);
        if (capacity < 1) { MainMenu.Tell("This map has no known enemy spawn positions. Choose another map."); return; }
        int startingEra = 0; // Player always starts in WWI era (rank 0)
        var eraDesigns = Files.Designs().Select(d => {
            string era = Files.EraOf(d.Path);
            int cost = Battle.UnitFor(d.Path)?.Cost?.quantities is { Length: > 0 } q ? q[0] : 0;
            return (Era: era, Cost: cost);
        }).Where(d => d.Cost > 0);
        var eraCosts = GauntletProgression.CalculateEraCosts(Files.EraList(), eraDesigns);
        run = new Run {
            Settings = settings,
            Ledger = new GauntletLedger(settings.Budget, startingEraRank: startingEra, eraCosts: eraCosts),
            Opponents = opponents,
            Capacity = GauntletProgression.SpawnCapacity(capacity)
        };
        Trace.Write($"gauntlet: new run {run.Id}, {settings.Rounds} rounds on {settings.Map}, funds {settings.Budget}, {opponents.Count} ranked designs across eras, starting era {EraName(startingEra)}, enemy source {settings.Pool}, faction {settings.EnemyFaction ?? "all"}, map capacity {run.Capacity}");
        SaveRun(run);
        OpenDesigner(run);
    }

    static string StarterBlueprint(Run r)
    {
        if (r.HeroPath.Length > 0 && File.Exists(r.HeroPath)) return r.HeroPath;
        // 1. Check if there is a WWI design among the opponents in this run
        var ww1Opponent = r.Opponents.FirstOrDefault(o => o.EraRank == 0);
        if (!string.IsNullOrEmpty(ww1Opponent.Path))
        {
            string abs = Files.Absolute(ww1Opponent.Path);
            if (File.Exists(abs) && Files.BrokenOf(abs) == null) return abs;
        }
        // 2. Check if there is any usable WWI design in the game's designs
        var ww1Design = Files.Designs().FirstOrDefault(d => EraRankOf(d.Path) == 0 && File.Exists(Files.Absolute(d.Path)) && Files.BrokenOf(Files.Absolute(d.Path)) == null);
        if (!string.IsNullOrEmpty(ww1Design.Path))
        {
            return Files.Absolute(ww1Design.Path);
        }
        // 3. Fallback: if no WWI design exists, use first opponent or any available design
        if (r.Opponents.Count > 0 && !string.IsNullOrEmpty(r.Opponents[0].Path))
            return Files.Absolute(r.Opponents[0].Path);
        var anyDesign = Files.Designs().FirstOrDefault(d => File.Exists(Files.Absolute(d.Path)) && Files.BrokenOf(Files.Absolute(d.Path)) == null);
        return anyDesign.Path != null ? Files.Absolute(anyDesign.Path) : "";
    }

    static void OpenDesigner(Run r)
    {
        var wave = GauntletProgression.Wave(r.Ledger.Round, r.Settings.Rounds, r.Capacity, r.Opponents.Count);
        var enemy = r.Opponents[wave.Tier];
        var file = new BattleFile { Name = $"Gauntlet — round {r.Ledger.Round} of {r.Settings.Rounds}", Map = r.Settings.Map,
            Description = "Repair and refit within your remaining funds, then defeat the next round.",
            Objective = "Defeat every opponent", Failure = "Lose your tank",
            Gauntlet = new GauntletBattleInfo { RunId = r.Id, Round = r.Ledger.Round },
            Limits = new PickLimits { MaxTanks = 1, Budget = r.Ledger.DesignBudget },
            FreeForAllSpawns = r.Settings.Spawns != null && r.Settings.Spawns.Randomize ? r.Settings.Spawns : null };
        string starter = StarterBlueprint(r);
        string heroBlueprint = starter.Length > 0 ? Files.Relative(starter) : enemy.Path;
        file.Units.Add(new BattleUnit { Id = "player", Team = 0, Blueprint = heroBlueprint,
            AtSpawn = true, Control = "player", Pick = true });
        for (int i = 0; i < wave.Count; i++)
            file.Units.Add(new BattleUnit { Id = "opponent" + (i + 1), Team = 1, Blueprint = enemy.Path, AtSpawn = true });
        r.Phase = Phase.Preparing; r.Since = Time.unscaledTime;
        Trace.Write($"gauntlet: round {r.Ledger.Round} prepares {wave.Count} opponents, {enemy.Name}, era {EraName(enemy.EraRank)}, cost tier {enemy.Cost}; funds {r.Ledger.Credits}, repair quote {r.Ledger.Repair}");
        SaveRun(r);
        Menu.Launch(r.Settings.Map, file, play: true);
    }

    internal static string? InitialDesign(BattleFile file)
    {
        if (!IsCurrent(file) || run == null) return null;
        string starter = StarterBlueprint(run);
        return string.IsNullOrEmpty(starter) ? null : starter;
    }

    internal static string DesignerFunds(BattleFile file, long currentCost, string? currentEra = null)
    {
        if (!IsCurrent(file)) return "";
        var r = run!;
        int targetEra = EraRank(currentEra);
        int cost = (int)Math.Clamp(currentCost, 1, int.MaxValue);
        var bill = r.Ledger.Quote(cost, r.HeroPath.Length > 0 ? "" : "initial", targetEra);
        if (r.Ledger.Committed == 0)
        {
            string line1 = $"Funds: {r.Ledger.Credits:N0}   Purchase: {currentCost:N0}";
            string line2 = bill.EraFee > 0
                ? $"Era advance ({EraName(targetEra)}): {bill.EraFee:N0}   Total: {bill.Total:N0}"
                : $"Era: {EraName(targetEra)} (unlocked)";
            return $"{line1}\n{line2}";
        }
        string l1 = $"Funds: {r.Ledger.Credits:N0}   Repair: {r.Ledger.Repair:N0}";
        // This live panel only knows price and era, not the designer's unsaved blueprint. The start check
        // compares the complete design fingerprint and waives labour when no refit was made.
        string l2 = $"Refit estimate: +{bill.Upgrade:N0}   Labour up to: {bill.Labour:N0}"
            + (bill.EraFee > 0 ? $"   Era advance: {bill.EraFee:N0}" : "")
            + $"   Total up to: {bill.Total:N0}";
        return $"{l1}\n{l2}";
    }

    internal static string? CheckPrepared(BattleFile file)
    {
        if (file.Gauntlet == null) return null;
        if (!IsCurrent(file)) return "This Gauntlet run has ended. Start a new run from Playing → Gauntlet.";
        var hero = file.Units.SingleOrDefault(u => u.Team == 0);
        if (hero == null || Battle.UnitFor(hero.Blueprint) is not { } unit || unit.Cost?.quantities is not { Length: > 0 } q || q[0] <= 0)
            return "Choose one usable tank for this Gauntlet round.";
        string blueprintPath = Files.Absolute(hero.Blueprint);
        string fingerprint;
        try { fingerprint = GauntletLedger.DesignFingerprint(File.ReadAllText(blueprintPath)); }
        catch (Exception ex) { return "Couldn't quote the refit: " + ex.Message; }
        int targetEra = EraRankOf(blueprintPath);
        var bill = run!.Ledger.Quote(q[0], fingerprint, targetEra);
        if (bill.Total > run.Ledger.Credits)
        {
            if (bill.EraFee > 0)
                return $"Advancing to {EraName(targetEra)} costs {bill.EraFee:N0} in era fees (total needed: {bill.Total:N0}; you have {run.Ledger.Credits:N0}). Start with WWI or an era you can afford.";
            return $"This round needs {bill.Total:N0} (repair {bill.Repair:N0}, upgrade {bill.Upgrade:N0}, labour {bill.Labour:N0}); you have {run.Ledger.Credits:N0}. Reduce the refit or end the run.";
        }
        return null;
    }

    internal static void Prepared(BattleFile file)
    {
        if (!IsCurrent(file)) return;
        if (CheckPrepared(file) is { } issue) throw new InvalidOperationException(issue);
        var r = run!;
        var hero = file.Units.Single(u => u.Team == 0);
        r.PendingPath = Files.Absolute(hero.Blueprint);
        r.PendingCost = Battle.UnitFor(hero.Blueprint)!.Cost.quantities[0];
        r.PendingFingerprint = GauntletLedger.DesignFingerprint(File.ReadAllText(r.PendingPath));
        r.PendingEraRank = EraRankOf(r.PendingPath);
        r.Phase = Phase.Loading; r.Since = Time.unscaledTime;
    }

    internal static void Started(BattleFile file, IReadOnlyDictionary<string, VehicleBehaviour> tanks)
    {
        if (!IsCurrent(file)) return;
        var r = run!;
        if (r.Phase != Phase.Loading) throw new InvalidOperationException("Gauntlet isn't waiting for this round to spawn.");
        if (!tanks.TryGetValue("player", out var hero) || hero == null)
            throw new InvalidOperationException("The Gauntlet tank didn't spawn.");
        var enemies = file.Units.Where(u => u.Team == 1).Select(u =>
            tanks.TryGetValue(u.Id, out var tank) && tank != null ? tank :
                throw new InvalidOperationException("A Gauntlet opponent didn't spawn.")).ToList();
        if (enemies.Count == 0) throw new InvalidOperationException("Gauntlet has no spawned opponents.");
        if (!r.Ledger.Commit(file.Gauntlet!.Round, r.PendingCost, r.PendingFingerprint, r.PendingEraRank))
            throw new InvalidOperationException("Gauntlet couldn't settle the round's purchase/repair/refit bill.");
        r.HeroPath = r.PendingPath;
        r.Hero = hero;
        r.Enemies.Clear();
        r.Enemies.AddRange(enemies);
        r.Mode = Battle.Mode; r.Phase = Phase.Fighting; r.NextCheck = Time.time + 2;
        SaveRun(r);
        Trace.Write($"gauntlet: round {r.Ledger.Round} started, bill settled once, funds {r.Ledger.Credits}; {r.Enemies.Count} opponents");
    }

    internal static void Tick()
    {
        if (run is not { } r) return;
        if (r.Phase is Phase.Fighting or Phase.Intermission or Phase.Finished
            && (r.Mode == null || !r.Mode.gameObject.scene.isLoaded || Battle.Mode?.Pointer != r.Mode.Pointer
                || !IsCurrent(Battle.Playing)))
        {
            Trace.Write($"gauntlet: run ended/aborted: phase {r.Phase}, mode {r.Mode != null}, scene loaded {r.Mode?.gameObject.scene.isLoaded}, mode match {Battle.Mode?.Pointer == r.Mode?.Pointer}, is current {IsCurrent(Battle.Playing)}");
            run = null;
            return;
        }
        if (r.Phase == Phase.Returning)
        {
            if (UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>() != null && Menu.HasCurrentCustomBattleButton && !Menu.Busy && !MainMenu.Unloading)
            { OpenDesigner(r); return; }
            if (Time.unscaledTime - r.Since > 120)
            { r.Phase = Phase.Finished; MainMenu.Tell("Couldn't return to the Gauntlet designer. Return to the main menu and start a new run."); }
            return;
        }
        if (r.Phase != Phase.Fighting) return;
        if (Mission.Banner != null || Time.timeScale <= 0 || Time.time < r.NextCheck) return;
        r.NextCheck = Time.time + 0.5f;
        if (Out(r.Hero))
        {
            r.Phase = Phase.Finished;
            DeleteSave();
            Mission.End(false, "GAUNTLET ENDED", $"Rounds cleared: {r.Ledger.Completed}/{r.Settings.Rounds}. Funds left: {r.Ledger.Credits:N0}.");
            return;
        }
        int remaining = r.Enemies.Count(t => !Out(t));
        if (remaining != r.LastRemaining)
        {
            r.LastRemaining = remaining;
            Trace.Write($"gauntlet: round {r.Ledger.Round} opponents remaining: {remaining}/{r.Enemies.Count}");
        }
        if (remaining == 0)
        {
            double damage = DamageFraction(r.Hero, out bool known); r.HealthKnown = known;
            long reward = r.Ledger.Complete(damage);
            bool final = r.Ledger.Round >= r.Settings.Rounds;
            r.Phase = final ? Phase.Finished : Phase.Intermission;
            Mission.End(true, final ? "GAUNTLET COMPLETE" : $"ROUND {r.Ledger.Round} CLEARED",
                $"Reward {reward:N0} · Funds {r.Ledger.Credits:N0}\n" + (final ? $"Completed all {r.Settings.Rounds} rounds." : $"Repair quote {r.Ledger.Repair:N0}. Repair and refit before the next round."));
            Trace.Write($"gauntlet: round cleared; damage {damage:0.000}, health {(known ? "measured" : "unavailable")}, reward {reward}, credits {r.Ledger.Credits}, repair {r.Ledger.Repair}");
            if (final) DeleteSave(); else SaveRun(r);
            return;
        }
        foreach (var tank in r.Enemies)
            if (!Out(tank) && Battle.CanAutoEngage(tank) && Battle.AutomaticTarget(tank)?.Pointer != r.Hero!.Pointer)
                Battle.SendAutoAgainst(tank, r.Hero!);
    }

    static bool Out(VehicleBehaviour? tank) => Battle.Out(tank);

    static double DamageFraction(VehicleBehaviour? tank, out bool known)
    {
        known = false;
        try
        {
            var entries = tank?.healthRegister?.entries;
            if (entries == null) return 1;
            int count = entries.Count;
            var seen = new HashSet<IntPtr>(); double current = 0, maximum = 0;
            for (int i = 0; i < count; i++)
            {
                var durable = entries[i]?.HealthPool;
                if (durable == null || durable.Pointer == IntPtr.Zero || durable.WasCollected) continue;
                if (!seen.Add(durable.Pointer)) continue;
                var health = durable.HealthInfo;
                if (!float.IsFinite(health.Current) || !float.IsFinite(health.Max) || health.Max <= 0) continue;
                current += Math.Clamp(health.Current, 0, health.Max); maximum += health.Max;
            }
            if (maximum <= 0) return 1;
            known = true; return Math.Clamp(1 - current / maximum, 0, 1);
        }
        catch (Exception ex) { Trace.Write($"gauntlet: health quote unavailable: {ex.Message}"); return 1; }
    }

    internal static void Refit()
    {
        if (!CanRefit || run is not { } r) return;
        if (!BattleEditor.ReturnToMainMenu()) return;
        if (!r.Ledger.Advance()) return;
        r.Mode = null; r.Hero = null; r.Enemies.Clear(); r.Phase = Phase.Returning; r.Since = Time.unscaledTime;
        SaveRun(r);
    }

    internal static void EndRun()
    {
        if (!BattleEditor.ReturnToMainMenu()) return;
        run = null;
    }

    internal static void CancelPreparation(BattleFile file)
    { if (IsCurrent(file) && run!.Phase == Phase.Preparing) run = null; }

    internal static void CancelForOtherBattle(BattleFile? file)
    { if (file?.Gauntlet == null) run = null; }

    static void SaveRun(Run r)
    {
        try
        {
            var data = new GauntletSaveData
            {
                EconomyVersion = GauntletProgression.EconomyVersion,
                Id = r.Id,
                Settings = r.Settings,
                Credits = r.Ledger.Credits,
                Round = r.Ledger.Round,
                Completed = r.Ledger.Completed,
                TankCost = r.Ledger.TankCost,
                Repair = r.Ledger.Repair,
                Fingerprint = r.Ledger.Fingerprint,
                Committed = r.Ledger.Committed,
                EraRank = r.Ledger.EraRank,
                EraCosts = r.Ledger.EraCosts.ToDictionary(kv => kv.Key, kv => kv.Value),
                HeroPath = r.HeroPath,
                Capacity = r.Capacity,
                SavedAtUtc = DateTime.UtcNow
            };
            string path = SavePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SavedFiles.Write(path, data.ToJson());
            Trace.Write($"gauntlet: saved run {r.Id}, round {r.Ledger.Round}, credits {r.Ledger.Credits}");
        }
        catch (Exception ex) { Trace.Write($"gauntlet: couldn't save run: {ex.Message}"); }
    }

    internal static void DeleteSave()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                File.Delete(SavePath);
                Trace.Write("gauntlet: deleted save file");
            }
        }
        catch (Exception ex) { Trace.Write($"gauntlet: couldn't delete save: {ex.Message}"); }
    }

    internal static bool TryGetSavedRun(out GauntletSaveData? data)
    {
        data = null;
        try
        {
            if (!File.Exists(SavePath)) return false;
            string json = File.ReadAllText(SavePath);
            data = GauntletSaveData.FromJson(json);
            return data != null && data.Completed < data.Settings.Rounds;
        }
        catch { return false; }
    }

    internal static void Resume()
    {
        if (Menu.Busy) return;
        if (!Attached) { MainMenu.Tell("Gauntlet couldn't attach to this game build. Check SprocketBattles-trace.log."); return; }
        if (!TryGetSavedRun(out var save) || save == null)
        {
            MainMenu.Tell("No saved Gauntlet run found.");
            return;
        }
        var opponents = CollectOpponents(save.Settings);
        if (opponents.Count == 0)
        {
            MainMenu.Tell("No usable enemy tanks match this faction. Choose another faction or start a new run.");
            return;
        }
        // Saved capacities may come from older builds which forced at least 12 tanks onto every map.
        int capacity = MainMenu.EnemySpawnCapacity(save.Settings.Map);
        if (capacity < 1) { MainMenu.Tell("The saved run's map has no known enemy spawn positions. Restore that map or start a new run."); return; }
        if (save.Capacity > 0 && capacity > 0) capacity = Math.Min(save.Capacity, capacity);
        capacity = GauntletProgression.SpawnCapacity(capacity);

        save.UpdateEconomy(Files.EraList(), Files.Designs().Select(d => (Files.EraOf(d.Path),
            Battle.UnitFor(d.Path)?.Cost?.quantities is { Length: > 0 } q ? q[0] : 0)).Where(d => d.Item2 > 0));
        var ledger = save.ResumeLedger();

        run = new Run
        {
            Id = save.Id,
            Settings = save.Settings,
            Ledger = ledger,
            Opponents = opponents,
            Capacity = capacity,
            HeroPath = save.HeroPath
        };
        Trace.Write($"gauntlet: resumed run {run.Id}, round {run.Ledger.Round}/{run.Settings.Rounds}, credits {run.Ledger.Credits}, opponents {run.Opponents.Count}");
        OpenDesigner(run);
    }
}

[HarmonyPatch(typeof(DeathmatchGameMode))]
internal static class GauntletEndHooks
{
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.RetryAsyncVoid))]
    static bool Retry(DeathmatchGameMode __instance)
    {
        if (!Gauntlet.Owns(__instance) || Battle.PreparedNativeRetry) return true;
        BattleEditor.Tell("Gauntlet uses Repair & refit after each round. To restart the run, return to the main menu.");
        Trace.Write("gauntlet: raw Retry blocked; repair/refit uses the normal paid round transition");
        return false;
    }
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.CompleteMission))]
    static bool Complete(DeathmatchGameMode __instance) => !Gauntlet.Owns(__instance);
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.AdvanceMission))]
    static bool Advance(DeathmatchGameMode __instance) => !Gauntlet.Owns(__instance);
    [HarmonyPrefix, HarmonyPatch(nameof(DeathmatchGameMode.FailMission))]
    static bool Fail(DeathmatchGameMode __instance) => !Gauntlet.Owns(__instance);
}

[HarmonyPatch(typeof(NativeMission), nameof(NativeMission.MissionUpdate))]
internal static class GauntletMissionHook
{
    [HarmonyPrefix]
    static bool Running(NativeMission __instance, ref Sprocket.MissionFramework.MissionUpdateResult __result)
    {
        if (!Gauntlet.Owns(__instance)) return true;
        __result = Sprocket.MissionFramework.MissionUpdateResult.Running;
        return false;
    }
}
