using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketBattles;

/// The Battle Editor's own menu, over the game's main menu: your saved battles as cards on the left (wheel scrolls),
/// the picked one on the right with its map, tanks, mission and cinematic, and Edit / Play / Duplicate / Delete; New
/// battle picks a map from the game's custom battle maps. Built from the game's own UI parts (uGUI, its font) in a look
/// of its own: a dark map table with a faint grid and amber markings.
internal static partial class MainMenu
{
    static GameObject? canvas;
    static TMP_FontAsset? font;
    static readonly List<UnityAction> keep = new();
    static readonly List<UnityAction<string>> keepText = new();
    static List<(string Path, BattleFile Battle)> battles = new();
    static int picked, top;
    static bool choosingMap, deleteArmed, rebuild;
    static string newName = "", note = "";
    static float noteUntil;
    static RectTransform? listArea;
    static readonly Dictionary<string, Sprite?> splashes = new();

    // The look.
    static readonly Color Back = new(0.05f, 0.055f, 0.045f, 0.985f), Grid = new(1, 1, 1, 0.03f), Bar = new(0.1f, 0.105f, 0.08f, 1),
        Card = new(0.12f, 0.125f, 0.1f, 1), CardPicked = new(0.2f, 0.18f, 0.1f, 1), Amber = new(0.96f, 0.68f, 0.22f, 1),
        Ink = new(0.92f, 0.91f, 0.86f, 1), Dim = new(0.6f, 0.61f, 0.56f, 1), Dark = new(0.08f, 0.07f, 0.04f, 1),
        ButtonColour = new(0.17f, 0.175f, 0.14f, 1), Blue = new(0.3f, 0.5f, 0.95f, 1), Red = new(0.9f, 0.32f, 0.27f, 1);

    const int Cards = 7;

    internal static bool IsOpen => canvas != null || ScreenOpen;

    internal static void Open()
    {
        font ??= TMP_Settings.defaultFontAsset ?? UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()?.font;
        battles = Files.SavedBattles();
        picked = Math.Clamp(picked, 0, Math.Max(0, battles.Count - 1));
        top = Math.Clamp(top, 0, Math.Max(0, battles.Count - Cards));
        choosingMap = battles.Count == 0;
        deleteArmed = false;
        if (OpenScreen()) return; // the game's own Scenarios screen
        Build();
    }

    internal static void Close()
    {
        Menu.Cancel();
        CloseMenu();
    }

    static void CloseMenu()
    {
        actionsFor = -1; quickOpen = false; importOpen = false; typingKey = null; typingDone = null; // the dropdowns closed for next time
        CloseScreen();
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        canvas = null;
        listArea = null;
        keep.Clear(); keepText.Clear();
    }

    // ---------- while a battle starts ----------

    static GameObject? cover;

    /// A battle starting behind the menu: the menu closed (its scene gone before the battle loads) and a plain cover
    /// over the game's Custom Battle screen while it's filled in and started.
    internal static void Cover(string text)
    {
        CloseMenu();
        Uncover();
        cover = new GameObject("Battle Editor cover", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var c = cover.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 31000;
        var scaler = cover.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
        cover.AddComponent<GraphicRaycaster>();
        var back = Node("Back", cover.transform, 0, 0, 0, 0);
        back.anchorMin = Vector2.zero; back.anchorMax = Vector2.one; back.sizeDelta = Vector2.zero;
        back.gameObject.AddComponent<Image>().color = Color.black; // and it takes the clicks
        Text(cover.transform, text, 0, 500, 1920, 60, 30, Ink, TextAlignmentOptions.Center);
    }

    internal static void Uncover()
    {
        if (cover != null) UnityEngine.Object.Destroy(cover);
        cover = null;
    }

    /// A message on the menu (opened again if it was closed: a battle that couldn't start).
    internal static void Tell(string text)
    {
        note = text; noteUntil = Time.unscaledTime + 8;
        if (!IsOpen) Open();
        else if (screen != null) { shownKey = null; lastShow?.Invoke(); }
        else rebuild = true;
    }

    /// Every frame: Esc goes back, the wheel scrolls the battles.
    internal static void Update()
    {
        if (!IsOpen || ScreenUpdate()) return;
        if (rebuild) { rebuild = false; Build(); }
        if (Keyboard.current is { } keys && keys.escapeKey.wasPressedThisFrame)
        {
            if (choosingMap && battles.Count > 0) { choosingMap = false; Build(); }
            else Close();
            return;
        }
        if (Mouse.current is { } mouse && listArea != null && !choosingMap && mouse.scroll.ReadValue().y is var wheel && wheel != 0
            && RectTransformUtility.RectangleContainsScreenPoint(listArea, mouse.position.ReadValue()))
        {
            int to = Math.Clamp(top - Math.Sign(wheel), 0, Math.Max(0, battles.Count - Cards));
            if (to != top) { top = to; Build(); }
        }
    }

    // ---------- building ----------

    static void Build()
    {
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        keep.Clear(); keepText.Clear();
        canvas = new GameObject("Battle Editor menu", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var c = canvas.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 31000;
        var scaler = canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvas.AddComponent<GraphicRaycaster>();
        var root = canvas.transform;

        // The table: dark, a faint grid, a bar across the top with an amber line under it.
        var back = Node("Back", root, 0, 0, 0, 0);
        back.anchorMin = Vector2.zero; back.anchorMax = Vector2.one; back.sizeDelta = Vector2.zero;
        back.gameObject.AddComponent<Image>().color = Back;
        for (int x = 0; x < 1920; x += 64) Fill(root, x, 0, 1, 1080, Grid);
        for (int y = 0; y < 1080; y += 64) Fill(root, 0, y, 1920, 1, Grid);
        Fill(root, 0, 0, 1920, 118, Bar);
        Fill(root, 0, 118, 1920, 4, Amber);
        for (int i = 0; i < 14; i++) Fill(root, 1920 - 300 + i * 22, 0, 9, 118, new Color(Amber.r, Amber.g, Amber.b, 0.10f)); // hazard stripes
        var title = Text(root, "BATTLE EDITOR", 60, 18, 900, 64, 54, Amber, TextAlignmentOptions.Left, bold: true);
        title.characterSpacing = 14;
        Text(root, "MISSIONS  ·  CINEMATICS  ·  COMMAND", 64, 78, 900, 30, 18, Dim, TextAlignmentOptions.Left).characterSpacing = 8;

        // Left: the saved battles.
        Text(root, $"YOUR BATTLES  ({battles.Count})", 60, 146, 420, 34, 22, Ink, TextAlignmentOptions.Left, bold: true).characterSpacing = 4;
        Btn(root, "+  NEW BATTLE", 470, 142, 230, 42, () => { choosingMap = true; newName = ""; Build(); }, primary: choosingMap);
        listArea = Node("Battles", root, 60, 196, 640, Cards * 104);
        for (int i = top; i < Math.Min(battles.Count, top + Cards); i++) BattleCard(listArea, i, (i - top) * 104);
        if (battles.Count == 0) Text(listArea, "No battles yet.\nPick NEW BATTLE, choose a map, and place your tanks.", 20, 20, 600, 120, 20, Dim, TextAlignmentOptions.TopLeft);
        if (battles.Count > Cards) Text(root, $"{top + 1}-{Math.Min(battles.Count, top + Cards)} of {battles.Count}   (mouse wheel)", 60, 196 + Cards * 104 + 4, 640, 26, 16, Dim, TextAlignmentOptions.Left);

        // Right: the battle picked, or the maps for a new one.
        var right = Node("Picked", root, 760, 146, 1100, 860);
        if (choosingMap) Maps(right);
        else if (picked < battles.Count) Details(right, battles[picked]);

        // Bottom: back, and what the keys do in a battle.
        Btn(root, "BACK", 60, 1006, 200, 48, Close);
        Text(root, "In a battle:  F9 editor (Tanks · Mission · Cinematic)   F10 command view (orders, force control)   Esc closes this menu",
             300, 1016, 1560, 30, 17, Dim, TextAlignmentOptions.Right);
        if (Time.unscaledTime < noteUntil && note.Length > 0)
        {
            Fill(root, 560, 950, 800, 44, new Color(0.35f, 0.12f, 0.08f, 0.95f));
            Text(root, note, 576, 956, 770, 32, 18, Ink, TextAlignmentOptions.Center);
        }
    }

    static void BattleCard(Transform parent, int index, float y)
    {
        var (path, b) = battles[index];
        bool on = index == picked;
        var card = Node("Battle card", parent, 0, y, 640, 96);
        var image = card.gameObject.AddComponent<Image>();
        image.color = on ? CardPicked : Card;
        Click(card.gameObject, image, () => { picked = index; deleteArmed = false; choosingMap = false; Build(); });
        if (on) { var o = card.gameObject.AddComponent<Outline>(); o.effectColor = Amber; o.effectDistance = new Vector2(2, -2); }
        int blue = b.Units.Count(u => u.Team == 0), red = b.Units.Count(u => u.Team == 1);
        // A stripe down its left side: the teams' share of the tanks.
        float share = blue + red == 0 ? 0.5f : (float)blue / (blue + red);
        Fill(card, 0, 0, 8, 96 * share, Blue);
        Fill(card, 0, 96 * share, 8, 96 * (1 - share), Red);
        Text(card, b.Name, 24, 10, 420, 34, 25, on ? Amber : Ink, TextAlignmentOptions.Left, bold: true);
        Text(card, $"{b.Map}   ·   {blue} vs {red} tanks{(b.Mission?.Rules.Count > 0 ? $"   ·   {b.Mission.Rules.Count} rules" : "")}{(b.Cinema?.Cameras.Count > 0 ? "   ·   cinematic" : "")}",
             24, 50, 600, 28, 17, Dim, TextAlignmentOptions.Left);
        Text(card, File.GetLastWriteTime(path).ToString("d MMM yyyy"), 440, 14, 180, 26, 15, Dim, TextAlignmentOptions.Right);
    }

    static void Details(Transform parent, (string Path, BattleFile Battle) entry)
    {
        var (path, b) = entry;
        // The map, big, with the battle's name over its foot.
        var frame = Node("Map", parent, 0, 0, 1100, 470);
        var shot = frame.gameObject.AddComponent<Image>();
        shot.color = new Color(0.15f, 0.16f, 0.13f, 1);
        if (Splash(b.Map) is { } sprite) { shot.sprite = sprite; shot.color = Color.white; shot.preserveAspect = false; }
        Fill(frame, 0, 330, 1100, 140, new Color(0, 0, 0, 0.62f));
        Fill(frame, 0, 466, 1100, 4, Amber);
        Text(frame, b.Name.ToUpperInvariant(), 28, 346, 1040, 56, 44, Ink, TextAlignmentOptions.Left, bold: true).characterSpacing = 3;
        Text(frame, $"MAP  {b.Map.ToUpperInvariant()}", 30, 408, 1040, 34, 20, Amber, TextAlignmentOptions.Left).characterSpacing = 6;

        // What's in it.
        var m = b.Mission;
        int blue = b.Units.Count(u => u.Team == 0), red = b.Units.Count(u => u.Team == 1), reserves = b.Units.Count(u => u.Reserve);
        float x = 0;
        void Chip(string text, Color colour)
        {
            float w = 26 + text.Length * 11;
            Fill(parent, x, 490, w, 40, new Color(colour.r * 0.35f, colour.g * 0.35f, colour.b * 0.35f, 1));
            Fill(parent, x, 490, 4, 40, colour);
            Text(parent, text, x + 14, 496, w - 16, 30, 18, Ink, TextAlignmentOptions.Left);
            x += w + 10;
        }
        Chip($"BLUE {blue}", Blue);
        Chip($"RED {red}", Red);
        if (reserves > 0) Chip($"RESERVES {reserves}", Dim);
        if (m != null && m.Rules.Count > 0) Chip($"RULES {m.Rules.Count}", Amber);
        if (m != null && m.Mines.Count > 0) Chip($"MINES {m.Mines.Count}", new Color(0.75f, 0.3f, 0.2f));
        if (m != null && m.Zones.Count > 0) Chip($"ZONES {m.Zones.Count}", new Color(0.95f, 0.75f, 0.2f));
        if (b.Cinema?.Cameras.Count > 0) Chip($"CAMERAS {b.Cinema.Cameras.Count}  {b.Cinema.Length:0} s", new Color(0.4f, 0.85f, 1f));

        // The mission's rules, as sentences.
        var lines = (m?.Rules ?? new()).Take(5).Select(r => "•  " + Sentence(r, m!)).ToList();
        if (lines.Count == 0) lines.Add("No mission rules: the battle is fought to the last tank.");
        Text(parent, string.Join("\n", lines), 0, 548, 1100, 150, 18, Dim, TextAlignmentOptions.TopLeft);

        // Its name, and what to do with it.
        Text(parent, "NAME", 0, 712, 120, 40, 18, Dim, TextAlignmentOptions.Left).characterSpacing = 4;
        TextInput(parent, b.Name, 90, 708, 600, 44, v => Rename(path, b, v));
        float bx = 0;
        void ActionButton(string text, Action act, bool primary = false, float w = 260) { Btn(parent, text, bx, 776, w, 64, act, primary, 24); bx += w + 20; }
        ActionButton("PLAY", () => Start(b, play: true), primary: true);
        ActionButton("EDIT", () => Start(b, play: false));
        ActionButton("DUPLICATE", () => Duplicate(b), w: 250);
        ActionButton(deleteArmed ? "SURE? DELETE" : "DELETE", () =>
        {
            if (!deleteArmed) { deleteArmed = true; Build(); return; }
            try { File.Delete(path); } catch (Exception ex) { Tell($"Couldn't delete it: {ex.Message}"); }
            Open();
        }, w: 270);
    }

    /// A rule as a sentence: "After 30 s: message "The battle begins."".
    static string Sentence(Rule r, MissionData m)
    {
        string Z(string? id) => m.Zones.FirstOrDefault(z => z.Id == id) is { } z ? Mission.Name(z) : "?";
        string when = When(r, m);
        var a = r.Then;
        string then = a.Do switch
        {
            "message" => $"\"{a.Text}\"",
            "victory" => "VICTORY" + (a.Text.Length > 0 ? $": {a.Text}" : ""),
            "defeat" => "DEFEAT" + (a.Text.Length > 0 ? $": {a.Text}" : ""),
            "artillery" => $"{a.Shells} {Mission.Calibre(a.Power)} mm shells on {Z(a.Zone)} over {a.Seconds:0} s",
            "reserves" => $"team {a.Team + 1}'s reserves arrive",
            "teamTo" => $"team {a.Team + 1} drives to {Z(a.Zone)}",
            "unitTo" => $"{a.Unit} drives to {Z(a.Zone)}",
            _ => a.Do,
        };
        return $"{when}:  {then}";
    }

    static string When(Rule r, MissionData m)
    {
        string Z(string? id) => m.Zones.FirstOrDefault(z => z.Id == id) is { } z ? Mission.Name(z) : "?";
        return r.When switch
        {
            "time" => $"After {r.Seconds:0} s",
            "destroyed" => $"When {r.Unit} is destroyed",
            "wipedOut" => $"When team {r.Team + 1} is wiped out",
            "losses" => $"When team {r.Team + 1} has lost {r.Count}",
            "teamEnters" => $"When team {r.Team + 1} enters {Z(r.Zone)}",
            "unitEnters" => $"When {r.Unit} enters {Z(r.Zone)}",
            "holds" => $"When team {r.Team + 1} holds {Z(r.Zone)} for {r.Seconds:0} s",
            _ => r.When,
        };
    }

    /// A rule's condition as an objective: "Team 1 holds the hill for 30 s".
    static string Condition(Rule r, MissionData m)
    {
        var when = When(r, m);
        if (when.StartsWith("When ")) when = when[5..];
        return when.Length > 0 ? char.ToUpperInvariant(when[0]) + when[1..] : when;
    }

    /// The game's custom battle maps (its scenario configs that are deathmatches), in its order, with their pictures
    /// (custom battle and scenario) and how many tanks a side they take.
    static List<(string Map, string Splash, string ScenarioSplash, int Spawns)> MapList()
    {
        if (maps != null) return maps;
        var list = new List<(int Order, string Map, string Splash, string ScenarioSplash, int Spawns)>();
        // The game's own list (so maps a mod like the Map Framework adds are in it); its config files if that fails. Only
        // the custom battle maps: Ambush has a setup in its scene, but without the Map Framework's repair the game's start
        // throws on it and the battle never leaves the loading screen.
        try
        {
            var defined = Sprocket.UI.ScenarioLoader.GetScenarioDefinitions(System.IO.Path.Combine(Application.streamingAssetsPath, "Scenarios", "Configs"));
            for (int i = 0; i < defined.Length; i++)
                if (defined[i]?.Config is { Deathmatch: true } c)
                    list.Add((c.Order, c.Identifier, c.CustomBattleSplashPath ?? "", c.ScenarioSplashPath ?? "",
                              c.TeamSpawnsSupported is { Length: > 0 } s ? s[0] : 0));
        }
        catch (Exception ex) { Trace.Write($"menu: the game's map list couldn't be read ({ex.Message}); reading its files"); list.Clear(); }
        if (list.Count == 0)
            try
            {
                var dir = System.IO.Path.Combine(Application.streamingAssetsPath, "Scenarios", "Configs");
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("Deathmatch", out var d) || !d.GetBoolean()) continue;
                    string Text(string key) => r.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
                    int spawns = r.TryGetProperty("TeamSpawnsSupported", out var s) && s.GetArrayLength() > 0 ? s[0].GetInt32() : 0;
                    list.Add((r.GetProperty("Order").GetInt32(), Text("Identifier"), Text("CustomBattleSplashPath"), Text("ScenarioSplashPath"), spawns));
                }
            }
            catch (Exception ex) { Trace.Write($"menu: couldn't read the maps: {ex.Message}"); return new(); }
        return maps = list.OrderBy(m => m.Order).Select(m => (m.Map, m.Splash, m.ScenarioSplash, m.Spawns)).ToList();
    }

    static List<(string Map, string Splash, string ScenarioSplash, int Spawns)>? maps;

    static void Maps(Transform parent)
    {
        Text(parent, "NEW BATTLE: PICK A MAP", 0, 0, 800, 40, 28, Amber, TextAlignmentOptions.Left, bold: true).characterSpacing = 5;
        Text(parent, "NAME", 0, 58, 120, 40, 18, Dim, TextAlignmentOptions.Left).characterSpacing = 4;
        TextInput(parent, newName, 90, 52, 600, 44, v => newName = v, keepOnChange: true);
        var maps = MapList();
        for (int i = 0; i < maps.Count; i++)
        {
            var (map, splash, _, _) = maps[i];
            float x = (i % 3) * 370, y = 120 + (i / 3) * 330;
            var card = Node("Map card", parent, x, y, 350, 310);
            var image = card.gameObject.AddComponent<Image>();
            image.color = Card;
            Click(card.gameObject, image, () => Start(new BattleFile { Name = string.IsNullOrWhiteSpace(newName) ? UniqueName($"{map} battle") : newName.Trim(), Map = map }, play: false));
            var picture = Node("Picture", card, 8, 8, 334, 236);
            var shot = picture.gameObject.AddComponent<Image>();
            shot.color = new Color(0.16f, 0.17f, 0.14f, 1);
            shot.raycastTarget = false;
            if (Splash(map, splash) is { } sprite) { shot.sprite = sprite; shot.color = Color.white; }
            Text(card, map.ToUpperInvariant(), 14, 254, 330, 40, 24, Ink, TextAlignmentOptions.Left, bold: true).characterSpacing = 3;
        }
        if (maps.Count == 0) Text(parent, "Couldn't find the game's maps.", 0, 120, 800, 40, 20, Dim, TextAlignmentOptions.Left);
    }

    // ---------- doing ----------

    static void Start(BattleFile b, bool play)
    {
        if (Menu.Busy) return; // already starting one
        if (play)
        {
            for (int t = 0; t < 2; t++)
                if (!b.Units.Any(u => u.Team == t && !u.Reserve)) { Tell($"Team {t + 1} has no tanks on the map at the start: Edit it first (or Play from inside the editor)."); return; }
        }
        if (string.IsNullOrEmpty(b.Map)) { Tell("This battle has no map."); return; }
        if (MapList().Count > 0 && !MapList().Any(m => string.Equals(m.Map, b.Map, StringComparison.OrdinalIgnoreCase)))
        { Tell($"{b.Map} isn't one of the game's custom battle maps, so a battle can't start there."); return; }
        Menu.Launch(b.Map, b, play);
    }

    static string UniqueName(string name)
    {
        string candidate = name;
        for (int n = 2; File.Exists(Files.BattlePath(candidate)); n++) candidate = $"{name} {n}";
        return candidate;
    }

    static void Duplicate(BattleFile b)
    {
        var copy = BattleFile.FromJson(b.ToJson());
        copy.Name = UniqueName(b.Name + " copy");
        Directory.CreateDirectory(Files.Battles);
        File.WriteAllText(Files.BattlePath(copy.Name), copy.ToJson());
        picked = 0; top = 0;
        Open();
    }

    // Import a shared battle's dropdown: the shared battles found when it opened.
    static bool importOpen;
    static List<string>? sharedZips;

    /// A battle written as one zip to send (its designs and decal pictures in it), its folder opened.
    static void Share(BattleFile b)
    {
        try
        {
            var zip = Sharing.Export(b, Files.Root, Files.Shared);
            Trace.Write($"menu: shared '{b.Name}' as {zip} ({new FileInfo(zip).Length / 1024} KB)");
            Tell($"Shared as \"{Path.GetFileName(zip)}\" in My Games\\Sprocket\\Battles\\Shared (opened): send that file.");
            OpenFolder(Files.Shared);
        }
        catch (Exception ex) { Trace.Write($"menu: sharing '{b.Name}' failed: {ex}"); Tell($"Couldn't share it: {ex.Message}"); }
    }

    /// A shared battle put in (Sharing.Import), then the list shown with it at the top.
    static void Import(string zip)
    {
        try
        {
            var (battle, path, added, reused) = Sharing.Import(zip, Files.Root, Files.BattlePath);
            Trace.Write($"menu: put in {zip} as {path}: {added} designs added, {reused} already there");
            importOpen = false; sharedZips = null; picked = 0; top = 0;
            Open();
            Tell($"{battle.Name} is in your battles. " +
                 (added > 0 ? $"{added} of its designs went into the {Sharing.Faction} faction." : "Its designs were already there."));
        }
        catch (Exception ex) { Trace.Write($"menu: putting in {zip} failed: {ex}"); Tell($"Couldn't put it in: {ex.Message}"); }
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Application.OpenURL(new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri);
    }

    static void Rename(string path, BattleFile b, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == b.Name) return;
        var to = Files.BattlePath(name);
        if (File.Exists(to)) { Tell($"There's already a battle named {name}."); return; }
        b.Name = name;
        try { File.WriteAllText(to, b.ToJson()); File.Delete(path); }
        catch (Exception ex) { Tell($"Couldn't rename it: {ex.Message}"); return; }
        picked = 0; top = 0;
        Open();
    }

    /// A map's picture from the game's own files (its custom battle splash), kept once loaded.
    static Sprite? Splash(string map, string? relative = null)
    {
        if (splashes.TryGetValue(map, out var known)) return known;
        Sprite? sprite = null;
        try
        {
            relative ??= MapList().FirstOrDefault(m => m.Map == map).Splash;
            var file = string.IsNullOrEmpty(relative) ? null : System.IO.Path.Combine(Application.streamingAssetsPath, relative.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            if (file != null && File.Exists(file))
            {
                var texture = new Texture2D(2, 2);
                if (ImageConversion.LoadImage(texture, File.ReadAllBytes(file)))
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            }
        }
        catch (Exception ex) { Trace.Write($"menu: no picture for {map}: {ex.Message}"); }
        return splashes[map] = sprite;
    }

    // ---------- parts ----------

    static RectTransform Node(string name, Transform parent, float x, float y, float width, float height)
    {
        var go = new GameObject(name, new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var rect = go.GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    static void Fill(Transform parent, float x, float y, float width, float height, Color colour)
    {
        var image = Node("Fill", parent, x, y, width, height).gameObject.AddComponent<Image>();
        image.color = colour;
        image.raycastTarget = false;
    }

    static TextMeshProUGUI Text(Transform parent, string text, float x, float y, float width, float height, float size, Color colour,
                                TextAlignmentOptions align, bool bold = false)
    {
        var label = Node("Text", parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.fontSize = size; label.color = colour; label.raycastTarget = false;
        label.richText = false; label.enableWordWrapping = true; label.overflowMode = TextOverflowModes.Ellipsis;
        label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        label.alignment = align;
        label.text = text;
        return label;
    }

    static void Click(GameObject o, Graphic graphic, Action act)
    {
        var button = o.AddComponent<Button>();
        button.targetGraphic = graphic;
        var colours = button.colors;
        colours.highlightedColor = new Color(1.18f, 1.15f, 1.05f, 1); colours.pressedColor = new Color(0.8f, 0.78f, 0.7f, 1);
        colours.colorMultiplier = 1.2f;
        button.colors = colours;
        var callback = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Guard.Run("Battle Editor menu", act)))!;
        keep.Add(callback);
        button.onClick.AddListener(callback);
    }

    static void Btn(Transform parent, string text, float x, float y, float width, float height, Action act, bool primary = false, float size = 20)
    {
        var rect = Node("Button " + text, parent, x, y, width, height);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = primary ? Amber : ButtonColour;
        Click(rect.gameObject, image, act);
        if (!primary) Fill(rect, 0, height - 3, width, 3, new Color(Amber.r, Amber.g, Amber.b, 0.6f));
        Text(rect, text, 0, 0, width, height, size, primary ? Dark : Ink, TextAlignmentOptions.Center, bold: true).characterSpacing = 4;
    }

    /// A text field (the game's own TextMeshPro input): `done` on Enter or leaving it (or on each change with keepOnChange).
    static void TextInput(Transform parent, string text, float x, float y, float width, float height, Action<string> done, bool keepOnChange = false)
    {
        var rect = Node("Text input", parent, x, y, width, height);
        var background = rect.gameObject.AddComponent<Image>();
        background.color = new Color(0.09f, 0.095f, 0.08f, 1);
        var field = rect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = background;
        var viewport = Node("Viewport", rect, 12, 6, width - 24, height - 12);
        viewport.gameObject.AddComponent<RectMask2D>();
        field.textViewport = viewport;
        var value = Text(viewport, "", 0, 0, width - 24, height - 12, 22, Ink, TextAlignmentOptions.Left);
        value.enableWordWrapping = false; value.overflowMode = TextOverflowModes.Overflow;
        field.textComponent = value;
        var hint = Text(viewport, "type a name", 0, 0, width - 24, height - 12, 22, Dim, TextAlignmentOptions.Left);
        field.placeholder = hint;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.SetTextWithoutNotify(text);
        Fill(rect, 0, height - 2, width, 2, Amber);
        var callback = DelegateSupport.ConvertDelegate<UnityAction<string>>(new Action<string>(v => Guard.Run("Battle Editor menu", () => done(v))))!;
        keepText.Add(callback);
        if (keepOnChange) field.onValueChanged.AddListener(callback);
        else field.onEndEdit.AddListener(callback);
    }
}
