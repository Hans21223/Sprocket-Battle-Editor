using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.CustomBattles;
using Sprocket.GameControl;
using Sprocket.Factions;
using Sprocket.SceneManagement;
using Sprocket.TechTrees;
using Sprocket.VehicleDesigner;
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles;
using UnityEngine;
using NativeTask = Il2CppSystem.Threading.Tasks.Task;

namespace SprocketBattles;

/// The full native vehicle designer for player-choice battles, plus the older edit-and-return visit. Native tools and
/// save/load controls remain in charge; battle-only drafts and the lineup stay separate from personal saved designs.
internal static class NativeDesigner
{
    enum Stage { LeavingPicker, OpeningDesigner, LoadingDesign, Editing, Returning, Failed }
    sealed class Visit
    {
        public string Path = "", Status = "Opening the vehicle designer...";
        public Func<NativeTask?> OpenDesigner = null!;
        public string Scene = "";
        public Action<string?> Resume = null!;
        public Stage Stage;
        public float Since, SavingSince, NextSceneCheck;
        public NativeTask? Loading;
        public NativeTask? SceneLoading;
        public VehicleDesignerCore? Core;
        public TechDate PreviousTechnologyLimit;
        public bool TechnologyLimitChanged;
        public VehicleEditor? Editor;
        public Il2CppSystem.EventHandler<VehicleBlueprintSavedEventArgs>? Saved;
        public string? SavedPath;
        public string ExpectedName = "";
        public string FailedName = "";
        public IntPtr FailedTarget;
        public Faction? PreviousFaction;
        public bool FactionChanged;
        public bool Loaded, Saving, ReturnQueued, FailureLogged;
        public Preparation? Battle;
    }

    sealed class Preparation
    {
        public string Map = "", Directory = "", Limits = "", Issue = "", PreviewIssue = "", Lineup = "", LastDesign = "";
        public BattleFile File = null!;
        public BattleFile? Played;
        public readonly List<string> Designs = new();
        public readonly Dictionary<string, string?> Validity = new(StringComparer.OrdinalIgnoreCase);
        public int Active;
        public float NextStatus;
    }

    static Visit? visit;
    internal static bool Busy => visit != null;

    /// Player-choice battles start in the ordinary full vehicle designer. Drafts never replace saved player designs.
    internal static bool BeginBattle(string map, BattleFile file)
    {
        if (visit != null || file.PickCapacity <= 0) return false;
        var scenes = ISceneManager.Instance ?? UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>()?.context?.SceneManager;
        string scene;
        try { scene = ResolveDesignerScene(scenes, map); }
        catch (Exception ex)
        { Trace.Write($"designer: map '{map}' can't open: {ex.Message}"); MainMenu.Tell(ex.Message); return false; }
        string loadName = MapBridge.Call("CustomMapLoadName", map) ?? scene;
        string currentFaction = FactionManager.CurrentFaction?.Name ?? "";
        string path = Gauntlet.InitialDesign(file) ?? Files.Designs().OrderBy(d => Files.FactionOf(d.Path).Equals(currentFaction, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(d => Files.Absolute(d.Path)).FirstOrDefault(p => System.IO.File.Exists(p) && Files.BrokenOf(p) == null)
            ?? file.Slots.Select(u => Files.Absolute(u.Blueprint)).FirstOrDefault(System.IO.File.Exists) ?? "";
        if (path.Length == 0) { MainMenu.Tell("Save or import a tank first, then open this battle to choose and edit it."); return false; }
        var battle = new Preparation
        {
            Map = map, File = BattleFile.FromJson(file.ToJson()),
            Directory = System.IO.Path.Combine(Files.Battles, "Drafts", Guid.NewGuid().ToString("N")),
        };
        battle.Designs.Add(path);
        var next = new Visit { Path = path, Scene = scene, OpenDesigner = () =>
            scenes!.Load(loadName, new Il2CppReferenceArray<Il2CppSystem.Object>(new Il2CppSystem.Object[]
            { new GameSetupContext { Mode = SupportedGameModes.Sandbox } }), SceneLoadOptions.None,
            Il2CppSystem.Threading.CancellationToken.None),
            Battle = battle, Since = Time.unscaledTime, Status = "Opening the vehicle designer on " + map + "..." };
        MatchFaction(next);
        visit = next;
        Trace.Write($"designer: preparing '{file.Name}' with up to {file.PickCapacity} player tanks; map='{map}', scene='{scene}', setupMode=Sandbox");
        return true;
    }

    internal static bool Begin(string path, Action openSandbox, Action<string?> resume)
    {
        if (visit != null) return false;
        var next = new Visit { Path = path, OpenDesigner = () => { openSandbox(); return null; }, Resume = resume, Since = Time.unscaledTime };
        MatchFaction(next);
        visit = next;
        Trace.Write($"designer: editing {path}, preserving the battle's selected lineup");
        return true;
    }

    internal static string ResolveDesignerScene(ISceneManager? scenes, string map)
    {
        if (MapBridge.Call("UnsupportedMapReason", map) is { } unavailable) throw new InvalidOperationException(unavailable);
        if (scenes == null) throw new InvalidOperationException("The game's map loader wasn't found. Return to the main menu and try again.");
        if (scenes.TryCast<MainSceneManager>()?.sceneDatabase is not { } database)
            throw new InvalidOperationException("The game's map list isn't ready. Return to the main menu and try again.");
        if (database.TryGet(map, out var exact) && exact != null) return exact.SceneName;
        // Older shared battles and map mods can store compact identifiers rather than the visible scene name.
        static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        string key = Key(map);
        string? match = null;
        // An Il2Cpp IEnumerable<T>: its generic enumerator has Current only, MoveNext is on the non-generic one.
        var each = database.Scenes.GetEnumerator();
        var step = each.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (step.MoveNext())
        {
            if (each.Current?.SceneName is not { } name || Key(name) != key) continue;
            if (match != null && !match.Equals(name, StringComparison.Ordinal))
                throw new InvalidOperationException($"The map '{map}' matches more than one installed map. Select its full map name again.");
            match = name;
        }
        return match ?? throw new InvalidOperationException($"The map '{map}' isn't installed or its scene can't be found. Install that map or choose another map for this battle.");
    }

    static void MatchFaction(Visit next)
    {
        // Native Save follows the active faction and blueprint name. Match a player design's faction for the visit,
        // then restore the previous one, so editing a design from another faction cannot replace a same-named tank.
        var relative = Files.Relative(next.Path);
        var parts = relative.Split('\\', '/');
        if (parts.Length > 2 && parts[0].Equals("Factions", StringComparison.OrdinalIgnoreCase) && FactionIO.TryGetFaction(parts[1], out var faction))
        {
            next.PreviousFaction = FactionManager.CurrentFaction;
            if (next.PreviousFaction?.Name != faction.Name)
            { FactionManager.CurrentFaction = faction; next.FactionChanged = true; }
        }
    }

    internal static void Step()
    {
        if (visit is not { } v) return;
        try
        {
            // A normal Exit destroys the Sandbox editor. Detect the main menu before reading its old native wrapper.
            if (v.Stage is Stage.Editing or Stage.Returning or Stage.Failed && Time.unscaledTime >= v.NextSceneCheck)
            {
                v.NextSceneCheck = Time.unscaledTime + 0.25f;
                if (UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>() != null &&
                    UnityEngine.Object.FindObjectOfType<CustomBattleCreation>() == null && Menu.HasCurrentCustomBattleButton)
                { Resume(v); return; }
            }
            if (v.Battle != null && v.Core != null && v.Stage is Stage.LoadingDesign or Stage.Editing or Stage.Failed) WrapPlay();
            if (v.Stage == Stage.LeavingPicker)
            {
                if (MainMenu.Unloading || UnityEngine.Object.FindObjectOfType<CustomBattleCreation>() != null || UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>() == null)
                {
                    if (Time.unscaledTime - v.Since > 20)
                    { v.Stage = Stage.Failed; v.Status = "Tank selection hasn't closed yet. Return to the battle and try again."; }
                    Show(v, false);
                    return;
                }
                if (v.ReturnQueued) { Resume(v); return; }
                v.Stage = Stage.OpeningDesigner; v.Since = Time.unscaledTime;
                Trace.Write($"designer: loading scene='{v.Scene}', setupMode={(v.Battle != null ? "Sandbox" : "native Sandbox menu")}");
                v.SceneLoading = v.OpenDesigner();
            }
            else if (v.Stage == Stage.OpeningDesigner)
            {
                if (v.SceneLoading is { IsCompleted: true } sceneLoad && (sceneLoad.IsFaulted || sceneLoad.IsCanceled))
                    throw new InvalidOperationException($"The map '{v.Scene}' couldn't open in the vehicle designer. Return to battles and choose another map");
                var core = UnityEngine.Object.FindObjectOfType<VehicleDesignerCore>();
                if (v.SceneLoading?.IsCompleted != false && core != null && core.HasEditor && core.editorState == VehicleDesignerCore.EditorState.Running)
                {
                    var game = UnityEngine.Object.FindObjectOfType<GameController>();
                    Trace.Write($"designer: scene='{v.Scene}', actualScene='{game?.gameObject.scene.name}' ready; coreState={core.editorState}");
                    v.SceneLoading = null;
                    v.Core = core;
                    if (v.Battle != null)
                    {
                        // The custom battle's era rules govern preparation, rather than this map's stock scenario cap.
                        // Preserve the scene's original value for a visit that ends without destroying the editor.
                        v.PreviousTechnologyLimit = core.technologyLimit;
                        core.technologyLimit = TechDate.MaxValue;
                        v.TechnologyLimitChanged = true;
                    }
                    if (v.ReturnQueued) { v.Stage = Stage.Editing; RequestReturn(v); return; }
                    var blueprint = new VehicleBlueprintSerializer().LoadFromPath(v.Path);
                    v.ExpectedName = blueprint.Header.Name;
                    v.Loading = core.Load(blueprint, Il2CppSystem.Threading.CancellationToken.None);
                    v.Stage = Stage.LoadingDesign; v.Since = Time.unscaledTime;
                    v.Status = "Loading " + System.IO.Path.GetFileNameWithoutExtension(v.Path) + "...";
                }
                else if (Time.unscaledTime - v.Since > 120)
                { v.Stage = Stage.Failed; v.Status = "The vehicle designer hasn't opened on this map. Return to battles and try another map."; }
            }
            else if (v.Stage == Stage.LoadingDesign)
            {
                if (v.Loading?.IsCompleted != true && Time.unscaledTime - v.Since > 120)
                { v.Stage = Stage.Failed; v.Status = "The tank has not finished loading. Return without saving and try another tank."; }
                if (v.Loading?.IsCompleted == true)
                {
                    if (v.Loading.IsFaulted || v.Loading.IsCanceled) { v.Status = "The selected tank couldn't open. Return without saving and choose another tank."; v.Loaded = false; }
                    else
                    {
                        var loaded = v.Core?.Target?.TryCast<IVehicleGateway>() is { } target ? new VehicleBlueprintSerializer().ToBlueprint(target) : null;
                        if (loaded?.Header.Name != v.ExpectedName)
                        { v.Loaded = false; v.Status = "The selected tank didn't become the active design. Return without saving to keep your tank safe."; }
                        else { v.Loaded = true; v.Status = v.Battle == null ? "Edit your tank with the normal tools. Save & return keeps this battle's lineup." : "Choose or edit your tank with the normal Load and design tools."; }
                    }
                    v.Loading = null; v.Stage = Stage.Editing;
                    if (!v.Loaded)
                    {
                        v.FailedTarget = v.Core?.Target?.Pointer ?? IntPtr.Zero;
                        v.FailedName = v.Core?.Target?.TryCast<IVehicleGateway>()?.DesignInfo?.Name ?? "";
                    }
                    if (v.Battle is { } loadedBattle)
                    { UpdateStatus(v, loadedBattle); loadedBattle.NextStatus = Time.unscaledTime + 0.5f; }
                }
            }
            else if (v.Stage == Stage.Editing)
            {
                if (v.ReturnQueued) { RequestReturn(v); return; }
                if (!v.Loaded && v.Battle != null && v.Core is { HasEditor: true, editorState: VehicleDesignerCore.EditorState.Running } recovered &&
                    recovered.Target?.TryCast<IVehicleGateway>() is { } recoveredTank &&
                    (recoveredTank.Pointer != v.FailedTarget || recoveredTank.DesignInfo?.Name != v.FailedName))
                { v.Loaded = true; v.Status = "Choose or edit your tank with the normal Load and design tools."; }
                if (v.Loaded && v.Core?.Editor is { } editor && (v.Editor == null || editor.Pointer != v.Editor.Pointer)) BindSaved(v, editor);
                if (v.Battle is { } preparing && Time.unscaledTime >= preparing.NextStatus)
                { preparing.NextStatus = Time.unscaledTime + 0.5f; UpdateStatus(v, preparing); }
                if (v.Saving && Time.unscaledTime - v.SavingSince > 30)
                { v.Saving = false; v.Status = "Finish or cancel the game's save dialog, then choose Save & return again."; }
            }
            else if (v.Stage == Stage.Returning && Time.unscaledTime - v.Since > 120)
            {
                v.Stage = Stage.Failed; v.Status = "The game hasn't returned to the main menu. Choose Back to battles to try again.";
            }
            Show(v, Ready(v));
        }
        catch (Exception ex)
        {
            v.Stage = Stage.Failed; v.Loaded = false; v.Loading = null; v.Saving = false;
            v.Status = "Couldn't open the designer: " + ex.Message + ". Return without saving to keep your battle.";
            if (!v.FailureLogged) { v.FailureLogged = true; Trace.Write($"designer: {ex}"); }
            Show(v, false);
        }
    }

    static void Show(Visit v, bool ready)
    {
        if (v.Battle is not { } b) { MainMenu.ShowDesignerReturn(SaveAndReturn, ReturnWithoutSaving, v.Status, ready); return; }
        MainMenu.ShowBattleDesigner(b.File.Name, b.Limits, b.Issue.Length > 0 ? b.Issue : b.PreviewIssue.Length > 0 ? b.PreviewIssue : ready ? "" : v.Status,
            b.Lineup, PreviousTank, NextTank, AddTank, RemoveTank, PlayBattle,
            ready && b.Designs.Count > 0, ready && b.Designs.Count < b.File.PickCapacity,
            ready && b.Designs.Count > 0, ready && b.Designs.Count > 1);
    }

    static bool Ready(Visit v) => v is { Stage: Stage.Editing, Loaded: true, Saving: false, ReturnQueued: false, Core: { } core } &&
        core.HasEditor && core.editorState == VehicleDesignerCore.EditorState.Running && core.Target != null && core.Editor?.OperationInProgress != true;

    static void UpdateStatus(Visit v, Preparation b)
    {
        var lim = b.File.Limits ?? new PickLimits();
        long total = 0;
        string? violation = b.Designs.Count == 0 ? "No tanks selected. Use Load to choose a design, then Add tank to include it." : null;
        string active = b.Designs.Count > 0 ? System.IO.Path.GetFileNameWithoutExtension(b.Designs[b.Active]) : "";
        string? activeEra = null;
        for (int i = 0; i < b.Designs.Count; i++)
        {
            if (i == b.Active && Ready(v) && v.Core!.Target?.TryCast<IVehicleGateway>() is { } gateway)
            {
                float nativeCost = gateway.Cost;
                long cost = float.IsFinite(nativeCost) && nativeCost >= 0 ? (long)Math.Ceiling(nativeCost) : -1;
                total += Math.Max(0, cost); active = gateway.DesignInfo?.Name ?? active;
                string? era = LiveEra(gateway.DesignInfo);
                activeEra = era;
                string identity = active + "|" + era + "|" + cost;
                if (identity != b.LastDesign) { b.LastDesign = identity; b.Issue = ""; }
                if (cost < 0) violation ??= "The current tank has an invalid cost. Finish the edit before starting.";
                else if (lim.MaxCost > 0 && cost > lim.MaxCost) violation ??= $"{active} costs {cost:N0}; the maximum per tank is {lim.MaxCost:N0}.";
                if (lim.Eras.Count > 0 && (era == null || !lim.Eras.Contains(era, StringComparer.OrdinalIgnoreCase)))
                    violation ??= $"{active} is {(era ?? "from an unknown era")}; allowed eras: {string.Join(", ", lim.Eras)}.";
            }
            else if (Battle.UnitFor(b.Designs[i]) is { } unit && unit.Cost?.quantities is { Length: > 0 } q)
            {
                total += q[0];
                if (b.Validity.TryGetValue(b.Designs[i], out var nativeIssue) && nativeIssue != null)
                    violation ??= $"Tank {i + 1} ({unit.Name}): {nativeIssue}";
                if (lim.MaxCost > 0 && q[0] > lim.MaxCost) violation ??= $"{unit.Name} costs {q[0]:N0}; the maximum per tank is {lim.MaxCost:N0}.";
                string era = Files.EraOf(b.Designs[i]);
                if (i == b.Active) activeEra = era;
                if (lim.Eras.Count > 0 && !lim.Eras.Contains(era, StringComparer.OrdinalIgnoreCase))
                    violation ??= $"{unit.Name} is {era}; allowed eras: {string.Join(", ", lim.Eras)}.";
            }
            else violation ??= $"Tank {i + 1} couldn't be read. Load or edit that tank again before starting.";
        }
        if (activeEra == null && Ready(v) && v.Core!.Target?.TryCast<IVehicleGateway>() is { } gw)
            activeEra = LiveEra(gw.DesignInfo);
        if (lim.Budget > 0 && total > lim.Budget) violation ??= $"The lineup costs {total:N0}; the battle budget is {lim.Budget:N0}.";
        b.PreviewIssue = violation ?? "";
        b.Limits = $"Selected tanks: {b.Designs.Count} (maximum {b.File.PickCapacity})    Budget: {total:N0}" +
            (lim.Budget > 0 ? $" / {lim.Budget:N0}" : " (unlimited)") +
            (lim.MaxCost > 0 ? $"\nMaximum per tank: {lim.MaxCost:N0}" : "") +
            (lim.Eras.Count > 0 ? $"\nAllowed eras: {string.Join(", ", lim.Eras)}" : "");
        if (b.File.Gauntlet != null) b.Limits = Gauntlet.DesignerFunds(b.File, total, activeEra);
        b.Lineup = b.Designs.Count > 0 ? $"Tank {b.Active + 1} of {b.Designs.Count}: {active}" : "No tanks selected (preview only)";
    }

    static string? LiveEra(IVehicleInfo? info)
    {
        if (info == null) return null;
        var date = info.Date; // Native TechDate is a value type with Year/Month/Day; no blueprint serialization.
        try { return Eras.Of(Files.EraList(), new DateTime(date.Year, date.Month, date.Day)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// Serialize only for an explicit action, never each frame or into a player's personal blueprint file.
    static bool Snapshot(Visit v, Preparation b, bool requireValid = false, bool includePreview = false)
    {
        if (!Ready(v)) return false;
        if (b.Designs.Count == 0 && !includePreview)
        { b.Issue = "Add a tank to the lineup before starting the battle."; return false; }
        try
        {
            string? nativeIssue = ValidationIssue(v);
            if (requireValid && nativeIssue != null) { b.Issue = nativeIssue; return false; }
            var target = v.Core!.Target.TryCast<IVehicleGateway>() ?? throw new InvalidOperationException("No tank is ready in the designer");
            var serializer = new VehicleBlueprintSerializer();
            var blueprint = serializer.ToBlueprint(target);
            string json = serializer.SerializeToJSON(blueprint, true);
            // A freshly assembled blueprint has no loaded file version yet; its transient Valid flag is not an
            // operability check. Validate the serialized format the same way the game reads a saved blueprint.
            var roundTrip = serializer.DeserializeJSON(json);
            if (!roundTrip.Valid || roundTrip.Header?.Name != blueprint.Header?.Name || roundTrip.VehicleObjects is not { Length: > 0 })
                throw new InvalidOperationException("The current tank couldn't be serialized as a readable blueprint");
            System.IO.Directory.CreateDirectory(b.Directory);
            string current = b.Designs.Count > 0 ? b.Designs[b.Active] : "";
            string path = current.StartsWith(b.Directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? current : System.IO.Path.Combine(b.Directory, Guid.NewGuid().ToString("N") + ".blueprint");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                System.IO.File.WriteAllText(temporary, json);
                System.IO.File.Move(temporary, path, true);
            }
            finally
            {
                try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
                catch (Exception) { }
            }
            if (b.Designs.Count == 0) { b.Designs.Add(path); b.Active = 0; }
            else b.Designs[b.Active] = path;
            b.Validity[path] = nativeIssue;
            b.Issue = "";
            return true;
        }
        catch (Exception ex)
        { b.Issue = "Couldn't prepare this tank: " + ex.Message; Trace.Write($"designer: draft failed: {ex.Message}"); return false; }
    }

    static void PreviousTank() => SwitchTank(-1);
    static void NextTank() => SwitchTank(1);
    static void SwitchTank(int by)
    {
        if (visit is not { Battle: { } b } v || b.Designs.Count < 2 || !Snapshot(v, b)) return;
        b.Active = (b.Active + by + b.Designs.Count) % b.Designs.Count;
        LoadTank(v, b.Designs[b.Active]);
    }

    static void AddTank()
    {
        if (visit is not { Battle: { } b } v || b.Designs.Count >= b.File.PickCapacity) return;
        bool first = b.Designs.Count == 0;
        if (!Snapshot(v, b, includePreview: first)) return;
        if (first)
        {
            UpdateStatus(v, b); Show(v, Ready(v));
            Trace.Write("designer: preview added as the first selected tank");
            return;
        }
        // A separate copy avoids two lineup entries sharing a file that the next edit can overwrite.
        string path = System.IO.Path.Combine(b.Directory, Guid.NewGuid().ToString("N") + ".blueprint");
        System.IO.File.Copy(b.Designs[b.Active], path, true);
        if (b.Validity.TryGetValue(b.Designs[b.Active], out var validity)) b.Validity[path] = validity;
        b.Designs.Add(path); b.Active = b.Designs.Count - 1;
        UpdateStatus(v, b); Show(v, Ready(v));
        v.Status = "A second copy is ready. Use the game's Load button to choose another tank, or edit this copy.";
    }

    static void RemoveTank()
    {
        if (visit is not { Battle: { } b } v || b.Designs.Count == 0 || !Ready(v)) return;
        b.Designs.RemoveAt(b.Active);
        b.Active = Math.Clamp(b.Active, 0, Math.Max(0, b.Designs.Count - 1));
        b.Issue = ""; b.LastDesign = "";
        Trace.Write($"designer: tank removed; {b.Designs.Count} selected, maximum {b.File.PickCapacity}");
        if (b.Designs.Count > 0) LoadTank(v, b.Designs[b.Active]);
        // Keep the native designer's working vehicle intact. It is only a preview until explicitly added again.
        // Removing a lineup entry only changes selection data, never the editor's live physics.
        UpdateStatus(v, b); Show(v, Ready(v));
    }

    static void LoadTank(Visit v, string path)
    {
        Unbind(v);
        var blueprint = new VehicleBlueprintSerializer().LoadFromPath(path);
        v.Path = path; v.ExpectedName = blueprint.Header.Name;
        v.Loaded = false; v.Loading = v.Core!.Load(blueprint, Il2CppSystem.Threading.CancellationToken.None);
        v.Stage = Stage.LoadingDesign; v.Since = Time.unscaledTime; v.Status = "Loading the selected lineup tank...";
    }

    internal static void PlayBattle()
    {
        if (visit is not { Battle: { } b } v || !Ready(v)) return;
        if (!Snapshot(v, b, requireValid: true)) return;
        var picks = new List<(string Name, int Cost, string? Era)>();
        foreach (string path in b.Designs)
        {
            if (Battle.UnitFor(path) is not { } unit) { b.Issue = "Couldn't read a tank in this lineup. Choose or edit it again."; return; }
            if (b.Validity.TryGetValue(path, out var nativeIssue) && nativeIssue != null)
            { b.Issue = $"{unit.Name}: {nativeIssue}"; return; }
            if (Files.BrokenOf(path) is { } broken) { b.Issue = $"{unit.Name}: {broken}. Pick a track segment for its tracks, then Play."; return; }
            int cost = unit.Cost?.quantities is { Length: > 0 } q ? q[0] : -1;
            picks.Add((unit.Name, cost, Files.EraOf(path)));
        }
        if (b.File.CheckPicks(picks) is { } error) { b.Issue = error; UpdateStatus(v, b); return; }
        var played = b.File.WithPicks(b.Designs.Select(Files.Relative).ToList());
        if (Gauntlet.CheckPrepared(played) is { } billIssue) { b.Issue = billIssue; UpdateStatus(v, b); return; }
        try { Gauntlet.Prepared(played); }
        catch (Exception ex) { b.Issue = "Couldn't prepare this Gauntlet round: " + ex.Message; return; }
        b.Played = played;
        v.ReturnQueued = true;
        v.Status = "Starting " + b.File.Name + "...";
    }

    static string? ValidationIssue(Visit v)
    {
        // The game's Play checks editor IO state, criteria and operables through this same native collection.
        var validation = new Sprocket.Gameplay.VehicleValidationCollection();
        v.Core!.Validate(validation.Cast<Sprocket.Validation.IValidationIssueCollection>());
        return validation.Valid ? null : validation.issues is { Count: > 0 } issues
            ? issues[0]?.Message ?? "Fix the tank's highlighted issues before starting."
            : "Fix the tank's highlighted issues before starting.";
    }

    // ---------- the designer's own Play arrow and hotkey ----------
    // They raise the designer player state's StateChangeRequested (Exit), which the map's scenario turns into its
    // mission. While a battle is prepared that event's delegate is wrapped: Exit starts the battle, anything else
    // (photo mode) goes on to the game. No game method is hooked: 0.17.3's hook on VehicleEditorScenarioGameState.Update
    // crashed the game in the .NET runtime on its first call, as the designer opened on a map.

    static Sprocket.Gameplay.VehicleDesignerPlayerState? playState;
    static Il2CppSystem.EventHandler<VehicleEditorStateChangeRequestEventArgs>? playOriginal, playWrapper;
    static bool passing;
    internal static bool PlayWrapped => playState != null && playWrapper != null;

    /// Wraps the event's delegate, again if the game has replaced it (each frame while editing; cheap when it hasn't).
    static void WrapPlay()
    {
        var state = playState != null ? playState : UnityEngine.Object.FindObjectOfType<Sprocket.Gameplay.VehicleDesignerPlayerState>();
        if (state == null) return;
        var now = state.StateChangeRequested;
        playWrapper ??= DelegateSupport.ConvertDelegate<Il2CppSystem.EventHandler<VehicleEditorStateChangeRequestEventArgs>>(
            new Action<Il2CppSystem.Object, VehicleEditorStateChangeRequestEventArgs>(PlayRequested))!;
        if (now != null && now.Pointer == playWrapper.Pointer && playState != null && playState.Pointer == state.Pointer) return;
        playState = state; playOriginal = now;
        state.StateChangeRequested = playWrapper;
        Trace.Write($"designer: the Play arrow now starts the battle ({(now == null ? "no" : "had a")} scenario handler)");
    }

    static void UnwrapPlay()
    {
        try
        {
            if (playState != null && playWrapper != null && playState.StateChangeRequested?.Pointer == playWrapper.Pointer)
                playState.StateChangeRequested = playOriginal;
        }
        catch (Exception ex) { Trace.Write($"designer: couldn't give the Play arrow back: {ex.Message}"); }
        playState = null; playOriginal = null;
    }

    static void PlayRequested(Il2CppSystem.Object sender, VehicleEditorStateChangeRequestEventArgs e) => Guard.Run("Battle Editor designer play", () =>
    {
        if (passing) return; // the game's handler list holding our wrapper too
        if (e?.Request == VehicleEditorStateChangeRequest.Exit && visit is { Battle: { } b } v && v.Stage != Stage.Returning)
        {
            Trace.Write("designer: the designer's Play pressed");
            if (!Ready(v)) b.Issue = "Finish the current edit or close the open dialog before starting the battle.";
            else PlayBattle();
            return;
        }
        passing = true;
        try { playOriginal?.Invoke(sender, e!); }
        finally { passing = false; }
    });

    static void BindSaved(Visit v, VehicleEditor editor)
    {
        Unbind(v);
        v.Editor = editor;
        v.Saved = DelegateSupport.ConvertDelegate<Il2CppSystem.EventHandler<VehicleBlueprintSavedEventArgs>>(
            new Action<Il2CppSystem.Object, VehicleBlueprintSavedEventArgs>((_, saved) =>
            {
                if (visit != v) return;
                if (!saved.Completed) { v.Saving = false; v.Status = "Saving was cancelled. Your battle is still waiting."; return; }
                if (string.IsNullOrWhiteSpace(saved.Path)) return;
                var savedPath = Files.Absolute(saved.Path);
                if (!System.IO.File.Exists(savedPath)) return;
                v.SavedPath = savedPath;
                if (v.Saving) { v.Saving = false; v.ReturnQueued = true; }
                Trace.Write($"designer: saved {savedPath}");
            }))!;
        editor.add_Saved(v.Saved);
    }

    static void Unbind(Visit v)
    {
        try { if (v.Editor != null && v.Saved != null) v.Editor.remove_Saved(v.Saved); } catch (Exception) { }
        v.Editor = null; v.Saved = null;
    }

    static void SaveAndReturn()
    {
        if (visit is not { Stage: Stage.Editing, Loaded: true, Saving: false, Core: { } core } v || !core.HasEditor) return;
        if (core.Editor is { } editor && (v.Editor == null || editor.Pointer != v.Editor.Pointer)) BindSaved(v, editor);
        v.Saving = true; v.SavingSince = Time.unscaledTime;
        v.Status = "Save your tank in the game's dialog; the battle picker opens after saving finishes.";
        try { core.RequestSave(); }
        catch (Exception ex)
        {
            v.Saving = false;
            v.Status = "The tank couldn't be saved: " + ex.Message;
            Trace.Write($"designer: couldn't request save: {ex}");
        }
    }

    static void ReturnWithoutSaving()
    {
        if (visit is not { } v) return;
        v.Saving = false;
        if (v.Battle is { } preparing) preparing.Played = null;
        if (v.Stage is Stage.LeavingPicker or Stage.OpeningDesigner or Stage.LoadingDesign)
        { v.ReturnQueued = true; v.Status = "Returning to battle setup once the game finishes opening..."; return; }
        RequestReturn(v);
    }

    static void RequestReturn(Visit v)
    {
        if (v.Stage == Stage.Returning) return;
        UnwrapPlay();
        if (UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>() != null)
        {
            v.Stage = Stage.Returning; v.Since = Time.unscaledTime; v.Status = "Returning to battle setup...";
            UnityEngine.Object.FindObjectOfType<CustomBattleCreation>()?.cancelButton?.Click();
            return;
        }
        var scenes = ISceneManager.Instance ?? throw new InvalidOperationException("The game's scene manager wasn't found");
        if (!scenes.TryGetFirstScene(SceneFlags.MainMenu, out var mainMenu)) throw new InvalidOperationException("The game's main menu scene wasn't found");
        if (UnityEngine.Object.FindObjectOfType<GameController>() is not { } game) throw new InvalidOperationException("The game's scene controller wasn't found");
        v.Stage = Stage.Returning; v.Since = Time.unscaledTime; v.Status = "Returning to battle setup...";
        Unbind(v);
        game.RequestSceneChange(mainMenu);
    }

    static void Resume(Visit v)
    {
        EndVisit(v);
        if (v.Battle is { } battle)
        {
            if (battle.Played != null) Menu.LaunchPrepared(battle.Map, battle.Played);
            else
            {
                Gauntlet.CancelPreparation(battle.File);
                if (v.Stage == Stage.Failed) MainMenu.Tell(v.Status);
                else MainMenu.Open();
            }
            return;
        }
        Trace.Write("designer: restoring the battle and its selected tanks");
        v.Resume(v.SavedPath);
    }

    internal static void AbortMapLoad()
    {
        if (visit is not { } v) return;
        EndVisit(v);
        if (v.Battle is { } battle) Gauntlet.CancelPreparation(battle.File);
        Trace.Write("designer: canceled preparation after the custom map failed, without reopening it");
    }

    static void EndVisit(Visit v)
    {
        Unbind(v);
        UnwrapPlay();
        if (v.TechnologyLimitChanged && v.Core != null)
            try { v.Core.technologyLimit = v.PreviousTechnologyLimit; }
            catch (Exception ex) { Trace.Write($"designer: couldn't restore the scenario's technology limit: {ex.Message}"); }
        if (v.FactionChanged && v.PreviousFaction != null)
            try { FactionManager.CurrentFaction = v.PreviousFaction; }
            catch (Exception ex) { Trace.Write($"designer: couldn't restore the original faction: {ex.Message}"); }
        visit = null;
        MainMenu.HideDesignerReturn();
        MainMenu.HideBattleDesigner();
    }
}
