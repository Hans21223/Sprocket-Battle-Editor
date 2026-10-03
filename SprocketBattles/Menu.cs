using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.CustomBattles;
using UnityEngine;
using UnityEngine.Events;

namespace SprocketBattles;

/// A Battle Editor button under the game's own Custom Battle button, wherever a menu adds that one. It opens the Battle
/// Editor's own menu (MainMenu: your battles, new ones on any map). Starting one goes through the game's Custom Battle
/// screen, under a plain cover, filled in and started for you (the map, the weather, and for Play the battle's tanks);
/// a battle to edit opens in the editor once its tanks are in.
[HarmonyPatch]
internal static class Menu
{
    /// The next battle to start opens in the editor (with Opening, if set).
    internal static bool EditNext;
    internal static BattleFile? Opening;

    static UnityAction? customBattle;
    static UnityAction? ours; // one for good: the game holds on to it

    [HarmonyPostfix, HarmonyPatch(typeof(MenuPanel), nameof(MenuPanel.Button))]
    static void Added(MenuPanel __instance, string label, UnityAction onClick, bool interactable) => Guard.Run("Battle Editor button", () =>
    {
        if (label == null || label.IndexOf("custom", StringComparison.OrdinalIgnoreCase) < 0 || label.IndexOf("battle", StringComparison.OrdinalIgnoreCase) < 0) return;
        customBattle = onClick;
        ours ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(Open))!;
        __instance.Button("Battle Editor", ours, interactable);
    });

    static void Open() => Guard.Run("Battle Editor menu", () =>
    {
        Trace.Write("menu: Battle Editor");
        MainMenu.Open();
    });

    // ---------- starting a battle through the Custom Battle screen ----------

    sealed class Launching { public string Map = ""; public BattleFile? File; public bool Play, Pick; public int Stage; public float Since; public string? Wrong; }
    static Launching? launch;

    internal static bool Busy => launch != null;

    /// Start a battle on `map`: to edit (`file`, or a new one) or to play `file` as made. A battle with Pick tanks to
    /// play opens the screen as it is, for the player to pick theirs (Picking).
    internal static void Launch(string map, BattleFile? file, bool play)
    {
        if (customBattle == null) { Trace.Write("menu: no Custom Battle button seen, can't start"); MainMenu.Tell("The game's Custom Battle button wasn't found, so battles can't be started."); return; }
        bool pick = play && file != null && file.Slots.Count > 0;
        launch = new Launching { Map = map, File = file, Play = play, Pick = pick, Since = Time.unscaledTime };
        Trace.Write($"menu: {(play ? "playing" : "editing")} '{file?.Name ?? "a new battle"}' on {map}, opening the Custom Battle screen{(pick ? $" for {file!.Slots.Count} picks" : "")}");
        if (pick) MainMenu.Hide();
        else MainMenu.Cover($"{(play ? "Starting" : "Opening")} {file?.Name ?? map}...");
        customBattle.Invoke();
    }

    internal static void Cancel() { launch = null; MainMenu.HidePicker(); }

    /// Every frame while starting: once the Custom Battle screen is up, its map, weather (and for Play, its teams) set;
    /// once it's waiting for its start button, that pressed; pressed again if the screen is still there 2.5 s later.
    /// The Battle Editor's menu covers it all and closes when the screen goes (the game took the battle).
    internal static void Step()
    {
        if (launch is not { } l) return;
        var screen = UnityEngine.Object.FindObjectOfType<CustomBattleCreation>();
        var edit = screen?.ConfigEdit;
        var config = edit?.Config;
        if (l.Stage >= 2)
        {
            if (screen == null) { launch = null; Trace.Write("menu: the game took the battle"); MainMenu.Uncover(); return; }
            if (Time.unscaledTime - l.Since < 2.5f) return;
            if (l.Stage >= 4)
            {
                Trace.Write("menu: the Custom Battle screen didn't take the start after 3 presses, left to you");
                launch = null;
                MainMenu.Uncover(); // its start button (bottom right) is then yours to press
                return;
            }
            Trace.Write($"menu: the Custom Battle screen is still up, pressing start again ({l.Stage - 1})");
            if (config != null) Press(l, screen!, config);
            return;
        }
        if (l.Pick && l.Stage == 1) { Picking(l, screen, config); return; }
        if (Time.unscaledTime - l.Since > 20)
        {
            Trace.Write($"menu: the Custom Battle screen didn't come up in 20 s (stage {l.Stage}), gave up");
            launch = null;
            MainMenu.Uncover();
            MainMenu.Tell("The game's Custom Battle screen didn't come up, so the battle couldn't start.");
            return;
        }
        if (screen == null || config?.Teams == null) return;
        if (l.Stage == 0)
        {
            config.MapName = l.Map;
            string? error = l.Pick ? Battle.PreparePick(config.Teams, l.File!)
                          : l.Play && l.File != null ? Battle.Prepare(config.Teams, l.File) : Battle.FillEmpty(config.Teams);
            if (error != null) { Trace.Write($"menu: {error}"); launch = null; MainMenu.Uncover(); MainMenu.Tell(error); return; }
            Weather(screen, config, l.File);
            edit!.RaiseDirtyFlags(BattleConfigDirtyFlags.Everything);
            l.Stage = 1; l.Since = Time.unscaledTime;
            return;
        }
        // The screen hears its start button once it's waiting for it (linkedCts is made then); 3 s at most. The menu's
        // own scene is gone first, so nothing of it is left when the battle loads.
        bool waiting = screen.linkedCts != null;
        if (Time.unscaledTime - l.Since < (waiting ? 0.8f : 3) || MainMenu.Unloading) return;
        if (!l.Play) { EditNext = true; Opening = l.File; }
        Press(l, screen, config);
    }

    /// The player picking their tanks on the game's screen (Team 1, its budget and places the battle's): the Battle
    /// Editor's Start battle button over the screen's own start, with what's picked above it. Leaving the screen (its
    /// Exit) leaves the picking, back to the Battle Editor's menu.
    static void Picking(Launching l, CustomBattleCreation? screen, BattleConfig? config)
    {
        if (screen == null || config?.Teams == null)
        {
            if (Time.unscaledTime - l.Since < 1) return; // the screen coming up again after a change
            Trace.Write("menu: the Custom Battle screen closed while picking");
            Cancel();
            MainMenu.Open();
            return;
        }
        l.Since = Time.unscaledTime;
        var file = l.File!;
        var picked = Battle.Picked(config.Teams[0]);
        MainMenu.ShowPicker(() => StartPicked(l));
        if (screen.confirmButton?.GetComponent<RectTransform>() is { } start)
        {
            var corners = new Vector3[4];
            start.GetWorldCorners(corners);
            var lim = file.Limits ?? new PickLimits();
            string status = l.Wrong ?? $"{file.Name}: pick up to {file.Slots.Count} of your tanks for Team 1 ({picked.Count} picked" +
                (lim.Budget > 0 ? $", {picked.Sum(p => p.Cost):N0} of {lim.Budget:N0}" : "") + ")" +
                (lim.MaxCost > 0 ? $"\nEach at most {lim.MaxCost:N0}" : "\n") + (lim.Eras.Count > 0 ? $"{(lim.MaxCost > 0 ? ", " : "")}{PickLimits.Describe(lim.Eras)}" : "");
            MainMenu.PlacePicker(new Vector2(corners[0].x, corners[0].y), new Vector2(corners[2].x, corners[2].y), status, l.Wrong != null);
        }
        if (l.Wrong != null && Time.unscaledTime > wrongUntil) l.Wrong = null;
    }

    static float wrongUntil;

    /// Start battle: the picks checked against the battle's limits, then put into its Pick tanks (their places, orders
    /// and parts in the mission kept), the teams made the battle's, and the game's start pressed as for any battle.
    static void StartPicked(Launching l)
    {
        var screen = UnityEngine.Object.FindObjectOfType<CustomBattleCreation>();
        var config = screen?.ConfigEdit?.Config;
        if (screen == null || config?.Teams == null || launch != l) return;
        var picked = Battle.Picked(config.Teams[0]);
        var file = l.File!;
        string? error = file.CheckPicks(picked.Select(p => (p.Name, p.Cost, (string?)Files.EraOf(p.Path))).ToList());
        if (error == null)
        {
            var played = file.WithPicks(picked.Select(p => Files.Relative(p.Path)).ToList());
            Battle.UnpickTeam(config.Teams);
            error = Battle.Prepare(config.Teams, played);
            if (error == null)
            {
                Trace.Write($"menu: picked {string.Join(", ", picked.Select(p => p.Name))} for '{file.Name}'");
                l.File = played; l.Pick = false;
                MainMenu.HidePicker();
                MainMenu.Cover($"Starting {file.Name}...");
                screen.ConfigEdit!.RaiseDirtyFlags(BattleConfigDirtyFlags.Everything);
                Press(l, screen, config);
                return;
            }
        }
        Trace.Write($"menu: picks turned down: {error}");
        l.Wrong = error;
        wrongUntil = Time.unscaledTime + 6;
    }

    static void Press(Launching l, CustomBattleCreation screen, BattleConfig config)
    {
        // Set again just before: the screen's own modules may redo the map and the weather.
        config.MapName = l.Map;
        Weather(screen, config, l.File);
        var scenes = Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);
        Trace.Write($"menu: starting {config.MapName} ({string.Join(" vs ", Enumerable.Range(0, config.Teams.Length).Select(t => config.Teams[t].UnitCount))} units; scenes loaded: {string.Join(", ", scenes)})");
        l.Stage++; l.Since = Time.unscaledTime;
        screen.confirmButton?.Click();
    }

    /// The battle's clouds and fog, by the screen's own presets (its map module's), into the config.
    static void Weather(CustomBattleCreation screen, BattleConfig config, BattleFile? file)
    {
        if (file == null || screen.modules == null) return;
        MapConfigurator? map = null;
        for (int i = 0; i < screen.modules.Length; i++) map ??= screen.modules[i]?.TryCast<MapConfigurator>();
        if (map == null) return;
        config.Environment ??= new EnvironmentConfig();
        if (map.cloudPresets is { } clouds)
            for (int i = 0; i < clouds.Length; i++)
                if (clouds[i]?.Name == file.Clouds) config.Environment.CloudChannelMapping = clouds[i].CloudChannelMapping;
        if (map.fogPresets is { } fogs)
            for (int i = 0; i < fogs.Length; i++)
                if (fogs[i]?.Name == file.Fog) config.Environment.FogDistance = fogs[i].AttenuationDistance;
    }
}
