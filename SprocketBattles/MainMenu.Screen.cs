using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.SceneManagement;
using Sprocket.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SprocketBattles;

/// The menu as the game's own Scenarios screen: its scene (ScenarioSelectScreen) loaded beside the main menu and filled
/// here instead of by the game. Your battles are listed down the left where the scenarios are (New battle first); the
/// one under the mouse shows behind, its map's picture, with what it is along the bottom (description, objectives,
/// fail conditions, tags). Click a battle for Play / Edit / Duplicate / Delete under it. If the game's screen can't be
/// had, the menu built in MainMenu.cs opens instead.
internal static partial class MainMenu
{
    const string ScreenScene = "ScenarioSelectScreen";
    static Il2CppSystem.Threading.Tasks.Task? loading, unloading;
    static ScenarioSelectScreen? screen;
    static TMP_Text? title;
    static bool screenFailed, firstFill;
    static string? shownKey;
    static int actionsFor = -1; // the battle whose dropdown (its actions) shows under it
    static int renamedFrame = -1; // the frame a typed line was closed in (its Esc isn't the menu's)
    static Action? lastShow;    // what's shown, to show again with a note
    static readonly Dictionary<string, GameObject> items = new();
    static readonly List<UnityAction<BaseEventData>> keepHover = new();

    static bool ScreenOpen => screen != null || loading != null;

    /// Whether the game's screen is still on its way out (a battle waits for it).
    internal static bool Unloading => unloading != null && !unloading.IsCompleted;

    /// The game's own scene manager: the screen is loaded and unloaded through it as the game's Scenarios button does
    /// (loaded past it, the game didn't know the scene, and leaving a battle for the main menu quit the game).
    static ISceneManager? Scenes()
    {
        try { return ISceneManager.Instance ?? UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>()?.context?.SceneManager; }
        catch (Exception) { return null; }
    }

    /// Starts loading the game's screen; false if it can't (the menu built here opens instead).
    static bool OpenScreen()
    {
        if (screenFailed) return false;
        if (screen != null) { shownKey = null; Fill(); return true; }
        if (loading != null) return true;
        if (Unloading) return false; // still closing the last one: the menu built here this once
        try
        {
            if (Scenes() is not { } scenes) { Trace.Write("menu: the game's scene manager wasn't found; using the Battle Editor's own menu"); return false; }
            loading = scenes.Load(ScreenScene, new Il2CppReferenceArray<Il2CppSystem.Object>(0), SceneLoadOptions.None, Il2CppSystem.Threading.CancellationToken.None);
            return loading != null;
        }
        catch (Exception ex) { Trace.Write($"menu: couldn't load the game's {ScreenScene}: {ex.Message}"); screenFailed = true; return false; }
    }

    static void CloseScreen()
    {
        if (!ScreenOpen) return;
        bool loaded = screen != null || loading?.IsCompleted == true;
        screen = null; title = null; loading = null; shownKey = null; lastShow = null;
        items.Clear(); keepHover.Clear();
        if (!loaded) return; // ponytail: closed mid-load, the game's own unloading of the main menu takes it
        try { unloading = Scenes()?.Unload(ScreenScene, SceneLoadOptions.None); }
        catch (Exception ex) { Trace.Write($"menu: couldn't unload {ScreenScene}: {ex.Message}"); }
    }

    /// Every frame while it's open: the loaded screen set up, Esc goes back. True while the game's screen is in use.
    static bool ScreenUpdate()
    {
        if (!ScreenOpen) return false;
        if (loading != null)
        {
            if (!loading.IsCompleted) return true;
            bool failed = loading.IsFaulted || loading.IsCanceled;
            loading = null;
            if (failed || !SetUp())
            {
                screenFailed = true;
                CloseScreen();
                Build();
                return false;
            }
        }
        if (UnityEngine.InputSystem.Keyboard.current is { } keys && keys.escapeKey.wasPressedThisFrame)
        {
            if (typingKey != null) { StopTyping(); Fill(); } // Esc while typing: left as it was
            else if (renamedFrame != Time.frameCount) GoBack(); // (not the Esc the name field just took)
        }
        return true;
    }

    static bool SetUp()
    {
        screen = UnityEngine.Object.FindObjectOfType<ScenarioSelectScreen>();
        if (screen == null || screen.selectButtonPrefab == null || screen.selectContent == null || screen.display == null)
        {
            Trace.Write($"menu: the game's {ScreenScene} isn't what the Battle Editor knows; using its own menu");
            return false;
        }
        if (screen.GetComponent<Canvas>() is { } canvasOf) canvasOf.sortingOrder = 31000; // over the main menu
        title = screen.transform.Find("Content/Text (TMP)")?.GetComponent<TMP_Text>();
        if (title != null)
        {
            // "BATTLE EDITOR" on one line, as long as the line under it (shrunk to fit if it must).
            title.enableWordWrapping = false;
            title.fontSizeMax = title.fontSize; title.fontSizeMin = title.fontSize * 0.6f;
            title.enableAutoSizing = true;
            title.rectTransform.sizeDelta = new Vector2(380, title.rectTransform.sizeDelta.y);
        }
        // The list grows with its lines, so the game's scroll view scrolls a long one (the game's few scenarios fit
        // its fixed height; more battles than that went off the bottom).
        if (screen.selectContent.GetComponent<ContentSizeFitter>() == null)
            screen.selectContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        if (screen.returnButton != null)
        {
            screen.returnButton.onClick = new Button.ButtonClickedEvent();
            screen.returnButton.onClick.AddListener(Action(GoBack));
        }
        for (int i = screen.selectContent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(screen.selectContent.GetChild(i).gameObject);
        firstFill = true;
        Fill();
        Trace.Write("menu: on the game's Scenarios screen");
        return true;
    }

    /// The return arrow and Esc: from the maps back to the battles, else out.
    static void GoBack()
    {
        if (choosingMap && battles.Count > 0) { choosingMap = false; Fill(); }
        else Close();
    }

    // ---------- the list ----------

    static void Fill()
    {
        if (screen == null) return;
        if (title != null) title.text = choosingMap ? "NEW BATTLE" : "BATTLE EDITOR";
        var wanted = new List<(string Key, string Text, Action Click, Action Hover, bool Dim)>();
        if (choosingMap)
        {
            foreach (var map in MapList())
                wanted.Add(("map " + map.Map, map.Map, () => NewBattle(map.Map), () => ShowMap(map), false));
        }
        else
        {
            wanted.Add(("new", "New battle", () => { choosingMap = true; Fill(); }, ShowNew, false));
            wanted.Add(("quick", "Quick battle", () => { quickOpen = !quickOpen; actionsFor = -1; Fill(); ShowQuick(); }, ShowQuick, false));
            if (quickOpen)
            {
                var mapNames = MapList().Select(m => m.Map).ToList();
                wanted.Add(("quick map", "      Map: " + (quickMap < 0 || quickMap >= mapNames.Count ? "any" : mapNames[quickMap]),
                    () => { quickMap = quickMap + 1 >= mapNames.Count ? -1 : quickMap + 1; shownKey = null; Fill(); ShowQuick(); }, ShowQuick, false));
                wanted.Add(("quick size", $"      Tanks a side: {QuickSizes[quickSize]}", () => { quickSize = (quickSize + 1) % QuickSizes.Length; shownKey = null; Fill(); ShowQuick(); }, ShowQuick, false));
                wanted.Add(("quick designs", "      Designs: " + QuickPools[quickPool], () => { quickPool = (quickPool + 1) % QuickPools.Length; shownKey = null; Fill(); ShowQuick(); }, ShowQuick, false));
                var eraNames = Files.EraList().Select(e => e.Name).ToList();
                wanted.Add(("quick era", "      Era: " + (quickEra < 0 || quickEra >= eraNames.Count ? "any" : eraNames[quickEra]),
                    () => { quickEra = quickEra + 1 >= eraNames.Count ? -1 : quickEra + 1; shownKey = null; Fill(); ShowQuick(); }, ShowQuick, false));
                wanted.Add(("quick start", "      Start", QuickBattle, ShowQuick, false));
            }
            wanted.Add(("import", "Import a shared battle", () =>
            {
                importOpen = !importOpen; actionsFor = -1; quickOpen = false;
                sharedZips = importOpen ? Sharing.Find(new[] { Files.Shared, Files.Downloads }) : null;
                Fill(); ShowImport();
            }, ShowImport, false));
            if (importOpen)
            {
                if (sharedZips is not { Count: > 0 })
                    wanted.Add(("import none", "      None found: open the Shared folder", () => OpenFolder(Files.Shared), ShowImport, false));
                else
                    foreach (var zip in sharedZips.Take(10))
                        wanted.Add(("import " + zip, "      " + Path.GetFileNameWithoutExtension(zip).Replace(" (Sprocket battle)", ""), () => Import(zip), ShowImport, false));
            }
            for (int i = 0; i < battles.Count; i++)
            {
                int index = i;
                var b = battles[i].Battle;
                var path = battles[i].Path;
                wanted.Add(("battle " + path, b.Name, () =>
                {
                    if (typingKey == "battle " + path) return;
                    actionsFor = actionsFor == index ? -1 : index;
                    picked = index; deleteArmed = false;
                    StopTyping();
                    Fill(); ShowBattle(index);
                }, () => ShowBattle(index), index == picked));
                if (actionsFor != index) continue;
                wanted.Add(("play", "      Play", () => Start(b, play: true), () => ShowBattle(index), false));
                wanted.Add(("edit", "      Edit", () => Start(b, play: false), () => ShowBattle(index), false));
                wanted.Add(("rename", "      Rename", () => StartTyping("battle " + path, b.Name, v =>
                {
                    if (v.Trim().Length > 0 && v.Trim() != b.Name) Rename(path, b, v); else Fill();
                }), () => ShowBattle(index), false));
                // Typed over in place; what the battle shows behind (description, objectives, fail conditions).
                void Text(string key, string label, string now, string none, Action<string> set) =>
                    wanted.Add((key, $"      {label}: {(now.Length == 0 ? none : now)}", () => StartTyping(key, now, v =>
                    {
                        set(v.Trim()); Save(path, b);
                        shownKey = null; Fill(); ShowBattle(index);
                    }), () => ShowBattle(index), false));
                Text("describe", "Description", b.Description, "(click to write)", v => b.Description = v);
                Text("objective", "Objectives", b.Objective, "(from its rules)", v => b.Objective = v);
                Text("failure", "Fails if", b.Failure, "(from its rules)", v => b.Failure = v);
                wanted.Add(("clouds", $"      Clouds: {b.Clouds}", () => { b.Clouds = Next(CloudNames, b.Clouds); Save(path, b); Fill(); }, () => ShowBattle(index), false));
                wanted.Add(("fog", $"      Fog: {b.Fog}", () => { b.Fog = Next(FogNames, b.Fog); Save(path, b); Fill(); }, () => ShowBattle(index), false));
                wanted.Add(("duplicate", "      Duplicate", () => Duplicate(b), () => ShowBattle(index), false));
                wanted.Add(("share", "      Share", () => Share(b), () => ShowBattle(index), false));
                wanted.Add(("delete", deleteArmed ? "      Sure? Delete" : "      Delete", () =>
                {
                    if (!deleteArmed) { deleteArmed = true; Fill(); return; }
                    try { File.Delete(battles[index].Path); } catch (Exception ex) { Tell($"Couldn't delete it: {ex.Message}"); return; }
                    actionsFor = -1;
                    Open();
                }, () => ShowBattle(index), false));
            }
        }

        // Items kept where they're still wanted (only new ones fade in), the rest taken away.
        var keys = wanted.Select(w => w.Key).ToHashSet();
        foreach (var gone in items.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            if (items[gone] != null) UnityEngine.Object.Destroy(items[gone]);
            items.Remove(gone);
        }
        keep.Clear(); keepHover.Clear();
        for (int i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (!items.TryGetValue(w.Key, out var item) || item == null) items[w.Key] = item = Item(firstFill ? i : 0);
            item.transform.SetSiblingIndex(i);
            if (typingKey == w.Key) TypeInto(item);
            else if (item.GetComponentInChildren<TMP_Text>() is { } label)
            {
                // One line each, cut with "..." where it doesn't fit (a long one wrapped over the line under it).
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.text = w.Text;
                label.alpha = w.Dim ? 0.55f : 1; // the picked battle greyed, as the game shows the scenario picked
            }
            var button = item.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(Action(w.Click));
            }
            var trigger = item.GetComponent<EventTrigger>() ?? item.AddComponent<EventTrigger>();
            trigger.triggers.Clear();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            var hover = DelegateSupport.ConvertDelegate<UnityAction<BaseEventData>>(new Action<BaseEventData>(_ => Guard.Run("Battle Editor menu", w.Hover)))!;
            keepHover.Add(hover);
            enter.callback.AddListener(hover);
            trigger.triggers.Add(enter);
        }
        firstFill = false;
        try { screen.Relayout(); } catch (Exception) { }
        if (shownKey == null)
        {
            if (choosingMap) { if (MapList().FirstOrDefault() is { Map: not null } first) ShowMap(first); }
            else if (battles.Count > 0) ShowBattle(picked);
            else ShowNew();
        }
    }

    /// One of the game's own scenario buttons, fading in after `order` others as the game's do.
    static GameObject Item(int order)
    {
        var item = UnityEngine.Object.Instantiate((UnityEngine.Object)screen!.selectButtonPrefab.gameObject, screen.selectContent).Cast<GameObject>();
        item.name = "Battle Editor item";
        if (item.GetComponent<Animation>() is { } fade)
        {
            try { screen.StartCoroutine(screen.PlayDelayed(fade, order * screen.selectButtonFadeIn)); }
            catch (Exception) { fade.Play(); }
        }
        return item;
    }

    // ---------- quick battle ----------

    // A battle with nothing to set up: so many random designs a side on a map (or any), started where the game spawns
    // them, and not saved (F9 in it opens the editor as on any custom battle).
    static bool quickOpen;
    static int quickMap = -1, quickSize = 2, quickPool;
    static readonly int[] QuickSizes = { 1, 2, 3, 4, 6, 8 };
    static readonly string[] QuickPools = { "yours", "the game's", "yours and the game's" };
    static readonly System.Random dice = new();

    /// The designs a quick battle picks from: yours (not autosaves), the game's tanks (not its AT guns or targets), or both.
    static List<(string Path, string Name)> QuickDesigns() => Files.Designs().Where(d =>
    {
        bool game = d.Path.StartsWith("game:", StringComparison.OrdinalIgnoreCase);
        if (game ? Files.IsATGun(d.Path) || d.Name.Contains("Target") : d.Name.StartsWith("Autosave")) return false;
        if (QuickEra() is { } era && Files.EraOf(d.Path) != era) return false;
        return quickPool == 2 || (quickPool == 0) != game;
    }).ToList();

    static int quickEra = -1; // the era quick battle tanks are picked from (-1: any)
    static string? QuickEra() => Files.EraList().ElementAtOrDefault(quickEra)?.Name;

    static void ShowQuick()
    {
        var maps = MapList();
        bool chosen = quickMap >= 0 && quickMap < maps.Count;
        Show($"quick {quickMap} {quickSize} {quickPool} {quickEra}", "Quick battle",
             $"{QuickSizes[quickSize]} vs {QuickSizes[quickSize]} tanks, each picked at random from {QuickDesigns().Count} designs ({QuickPools[quickPool]}{(QuickEra() is { } e ? ", " + e : "")}), " +
             $"on {(chosen ? maps[quickMap].Map : "a map picked at random")}. They start where the game spawns them, and nothing is saved.",
             new[] { "Destroy every enemy tank" }, new[] { "Lose all vehicles" }, new[] { "QUICK BATTLE" },
             chosen ? SplashPath(maps[quickMap].Map, scenario: true) : maps.Count > 0 ? maps[0].ScenarioSplash : "");
    }

    static void QuickBattle()
    {
        var maps = MapList(); var designs = QuickDesigns();
        if (maps.Count == 0 || designs.Count == 0) { Tell(designs.Count == 0 ? "No designs to pick from: try the other Designs choice." : "No maps to fight on."); return; }
        var map = quickMap >= 0 && quickMap < maps.Count ? maps[quickMap] : maps[dice.Next(maps.Count)];
        int size = map.Spawns > 0 ? Math.Min(QuickSizes[quickSize], map.Spawns) : QuickSizes[quickSize];
        var b = new BattleFile { Name = "Quick battle", Map = map.Map };
        for (int team = 0; team < 2; team++)
            for (int i = 0; i < size; i++)
                b.Units.Add(new BattleUnit { Id = b.NewId(), Team = team, Blueprint = designs[dice.Next(designs.Count)].Path, AtSpawn = true });
        Trace.Write($"menu: quick battle on {map.Map}: {string.Join(", ", b.Units.Select(u => $"{u.Id} {System.IO.Path.GetFileNameWithoutExtension(u.Blueprint)}"))}");
        Start(b, play: true);
    }

    // ---------- the dropdown's settings ----------

    // The Custom Battle screen's weather presets (MapConfigurator's), by name.
    static readonly string[] CloudNames = { "Clear", "Cloudy", "High Clouds", "Overcast" };
    static readonly string[] FogNames = { "None", "Light", "Medium", "Heavy", "Very Heavy" };

    static string Next(string[] all, string now) => all[(Array.IndexOf(all, now) + 1) % all.Length];

    static void Save(string path, BattleFile b)
    {
        try { File.WriteAllText(path, b.ToJson()); }
        catch (Exception ex) { Tell($"Couldn't save it: {ex.Message}"); }
    }

    // Typing into a line of the list (a battle's name, its description): the line's key, its text to start from, and
    // what to do with what was typed.
    static string? typingKey;
    static string typingStart = "";
    static Action<string>? typingDone;

    static void StartTyping(string key, string start, Action<string> done)
    {
        StopTyping();
        typingKey = key; typingStart = start; typingDone = done;
        Fill();
    }

    /// The line typed over in place (the game's input field on the game's label): Enter or clicking away keeps it,
    /// Esc leaves it as it was.
    static void TypeInto(GameObject item)
    {
        if (item.GetComponent<TMP_InputField>() != null) return;
        var label = item.GetComponentInChildren<TMP_Text>();
        if (label == null) return;
        string key = typingKey!;
        var done = typingDone;
        label.alpha = 1;
        // An object takes one clickable part: the line's button goes (the line is made again when typing ends).
        if (item.GetComponent<Button>() is { } button) UnityEngine.Object.DestroyImmediate(button);
        var field = item.AddComponent<TMP_InputField>();
        if (field == null) { Trace.Write("menu: couldn't type on the line"); StopTyping(); return; }
        field.textViewport = label.rectTransform;
        field.textComponent = label;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.text = typingStart;
        var ended = DelegateSupport.ConvertDelegate<UnityAction<string>>(new Action<string>(v => Guard.Run("Battle Editor menu", () =>
        {
            if (typingKey != key) return;
            StopTyping();
            done?.Invoke(v);
        })))!;
        keepText.Add(ended);
        field.onEndEdit.AddListener(ended);
        field.ActivateInputField();
        field.Select();
    }

    /// Typing over: the line it was typed on made again (without its input field).
    static void StopTyping()
    {
        if (typingKey == null) return;
        if (items.TryGetValue(typingKey, out var item))
        {
            if (item != null) UnityEngine.Object.Destroy(item);
            items.Remove(typingKey);
        }
        typingKey = null; typingDone = null;
        renamedFrame = Time.frameCount;
    }

    static UnityAction Action(Action act)
    {
        var callback = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Guard.Run("Battle Editor menu", act)))!;
        keep.Add(callback);
        return callback;
    }

    // ---------- what's shown behind ----------

    static void ShowBattle(int index)
    {
        if (index < 0 || index >= battles.Count) return;
        var (path, b) = battles[index];
        var m = b.Mission;
        int blue = b.Units.Count(u => u.Team == 0), red = b.Units.Count(u => u.Team == 1), reserves = b.Units.Count(u => u.Reserve);
        var parts = new List<string>();
        if (b.Description.Length > 0) parts.Add(b.Description);
        parts.Add($"{blue} vs {red} tanks on {b.Map}.");
        if (reserves > 0) parts.Add($"{reserves} held in reserve.");
        if (m != null && m.Rules.Count > 0) parts.Add($"{m.Rules.Count} mission rules.");
        if (m != null && m.Mines.Count > 0) parts.Add($"{m.Mines.Count} mines.");
        if (b.Cinema?.Cameras.Count > 0) parts.Add($"A {b.Cinema.Length:0} s cinematic plays as it starts.");
        var rules = m?.Rules ?? new();
        var wins = b.Objective.Length > 0 ? BattleFile.Lines(b.Objective) : rules.Where(r => r.Then.Do == "victory").Select(r => Condition(r, m!)).ToArray();
        var losses = b.Failure.Length > 0 ? BattleFile.Lines(b.Failure) : rules.Where(r => r.Then.Do == "defeat").Select(r => Condition(r, m!)).ToArray();
        Show("battle " + path, b.Name, string.Join(" ", parts),
             wins.Length > 0 ? wins : new[] { "Destroy every enemy tank" },
             losses.Length > 0 ? losses : new[] { "Lose all vehicles" },
             new[] { b.Map.ToUpperInvariant(), File.GetLastWriteTime(path).ToString("d MMM yyyy") },
             SplashPath(b.Map, scenario: true));
    }

    static void ShowMap((string Map, string Splash, string ScenarioSplash, int Spawns) map) =>
        Show("map " + map.Map, map.Map, $"A new battle on {map.Map}: up to {map.Spawns} tanks a side. Place them, then add a mission and a cinematic.",
             new[] { "Set by your mission" }, new[] { "Set by your mission" }, new[] { map.Map.ToUpperInvariant() },
             string.IsNullOrEmpty(map.Splash) ? map.ScenarioSplash : map.Splash);

    static void ShowImport() =>
        Show("import", "Import a shared battle",
             "A battle someone shared with you: put its .zip in My Games\\Sprocket\\Battles\\Shared (or leave it in Downloads) and click it here. " +
             $"Its designs go into a faction of their own, {Sharing.Faction}, and the battle into your list. To share one of yours, click it, then Share.",
             new[] { "Set by its mission" }, new[] { "Set by its mission" }, new[] { "SHARED BATTLES" }, MapList().FirstOrDefault().ScenarioSplash ?? "");

    static void ShowNew()
    {
        var first = MapList().FirstOrDefault();
        Show("new", "New battle", "Make a new battle: pick its map, place the tanks, then give it a mission and a cinematic.",
             new[] { "Set by your mission" }, new[] { "Set by your mission" }, new[] { "BATTLE EDITOR" }, first.ScenarioSplash ?? "");
    }

    /// The game's display (its fade, its layout) showing a battle or map, once per change.
    static void Show(string key, string name, string description, string[] objectives, string[] fails, string[] tags, string splash)
    {
        if (screen?.display == null) return;
        string plain = description, plainKey = key;
        lastShow = () => Show(plainKey, name, plain, objectives, fails, tags, splash);
        bool noted = Time.unscaledTime < noteUntil && note.Length > 0;
        if (noted) description = note + "\n" + description;
        key += noted ? "+" + note : "";
        if (key == shownKey) return;
        shownKey = key;
        var info = new MapInfo
        {
            Localization = new MapLocalization
            {
                Identifier = name, Name = name, Description = description,
                Objectives = new Il2CppStringArray(objectives), FailConditions = new Il2CppStringArray(fails), Tags = new Il2CppStringArray(tags),
            },
            Config = new MapConfig { Identifier = name, ScenarioSplashPath = splash, CustomBattleSplashPath = splash, Deathmatch = true, Scenario = true },
        };
        screen.display.DisplayTransitioned(info);
    }

    /// A map's picture as the game's configs name it (its scenario picture, or its custom battle one).
    static string SplashPath(string map, bool scenario)
    {
        var m = MapList().FirstOrDefault(x => x.Map == map);
        return (scenario && !string.IsNullOrEmpty(m.ScenarioSplash) ? m.ScenarioSplash : m.Splash) ?? "";
    }

    static void NewBattle(string map) => Start(new BattleFile { Name = UniqueName($"{map} battle"), Map = map }, play: false);
}
