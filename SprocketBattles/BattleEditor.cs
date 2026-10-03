using Sprocket;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketBattles;

/// The Battle Editor, over a running custom battle (F9): the battle freezes, a free camera flies over the map, and
/// tanks are placed as markers (a box the size of a tank, its team's colour, a light block at its front). Keys and the
/// mouse are read directly (the game's on-screen GUI doesn't pass its own events to mods).
public sealed partial class BattleEditor : MonoBehaviour
{
    public BattleEditor(IntPtr pointer) : base(pointer) { instance = this; }

    static BattleEditor? instance;

    /// A message on screen from outside the editor (the battle's order runner), open or not.
    internal static void Tell(string text) => instance?.Say(text);

    bool editing;
    BattleFile file = new();
    BattleUnit? selected;
    List<(string Path, string Name)> designs = new();    // the picked faction's (every one's for "All")
    List<(string Path, string Name)> allDesigns = new();
    static string faction = "All";
    int design;
    int team;
    string status = "";
    float statusUntil;

    // What editing changed, put back on leaving.
    float timeScaleBefore = 1;
    Camera? gameCamera;
    readonly List<(Transform Tank, Vector3 At, Quaternion Turn)> movedTanks = new();
    readonly List<InputAction> gameInput = new();
    readonly List<Canvas> hud = new();
    readonly List<UnityEngine.Rendering.VolumeComponent> fogs = new();
    CursorLockMode lockBefore;
    bool cursorBefore;
    // The editor's own camera: the game's follows the player's tank from a parent object, putting itself back each frame.
    Camera? view;
    float yaw, pitch;
    string map = "";
    (Vector3 At, float Height) topView;
    float nextFill;
    int editedBattle; // the battle (game mode) the placed tanks were placed in

    static readonly Color[] TeamColours = { new(0.25f, 0.45f, 0.95f), new(0.9f, 0.3f, 0.25f) };

    void Say(string text) { status = text; statusUntil = Time.unscaledTime + 5; }

    // Performance, shown in the command view: frame time and physics steps per frame (smoothed), and the mod's own time.
    float frameMs = 16, physicsPerFrame, modMs;
    int fixedSteps;
    long modTicks;
    readonly System.Diagnostics.Stopwatch modClock = new();

    public void FixedUpdate() => fixedSteps++;

    public void Update()
    {
        frameMs += (Time.unscaledDeltaTime * 1000 - frameMs) * 0.05f;
        physicsPerFrame += (fixedSteps - physicsPerFrame) * 0.05f;
        modMs += (modTicks * 1000f / System.Diagnostics.Stopwatch.Frequency - modMs) * 0.05f;
        fixedSteps = 0;
        modTicks = 0;
        modClock.Restart();
        Guard.Run("Battle editor", Tick);
        modTicks += modClock.ElapsedTicks;
    }

    void Tick()
    {
        Battle.PlaceSpawned();
        Battle.RunOrders();
        if (Battle.Mode != null) Battle.ThrottleSight();
        Guard.Run("mission", Mission.Tick);
        Guard.Run("force control", Puppet.Update);
        Guard.Run("cinematic", CinemaTick);
        Guard.Run("menu", MenuLaunch);
        if (pausing >= 0 && !editing && !commanding)
        {
            // Left with Esc: the battle stays still under the pause menu; if no pause menu came, it goes on as before.
            if (UnityEngine.SceneManagement.SceneManager.GetSceneByName("PauseMenu").isLoaded) pausing = -1;
            else if (Time.unscaledTime - pausing > 0.6f) { pausing = -1; Time.timeScale = timeScaleBefore; }
        }
        // Started from the Battle Editor's menu: open once the battle's tanks are in (with the battle picked there).
        if (Menu.EditNext && !editing)
        {
            if (Battle.Mode is { } started)
            {
                if (Battle.Spawned(started))
                {
                    Menu.EditNext = false;
                    Enter(started);
                    if (Menu.Opening is { } picked)
                    {
                        Menu.Opening = null;
                        file = picked;
                        Select(null); ToTopView();
                        Say(string.Equals(picked.Map, map, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(picked.Map)
                            ? $"Editing {picked.Name} on {map}." : $"{picked.Name} was made on {picked.Map}, but the game started {map}. Saving keeps it on {map}.");
                    }
                }
            }
            else FillTeams();
        }
        if (editing || commanding || Mission.Banner != null) { GameInputOff(); ShowCursor(); bannerHeld = !editing && !commanding; }
        else
        {
            if (bannerHeld && !cinemaPlaying) { foreach (var a in gameInput) a?.Enable(); gameInput.Clear(); } // the banner's gone
            bannerHeld = false;
            if (!cinemaPlaying) PhotoPointer();
        }
        var keys = Keyboard.current;
        if (keys == null) return;
        if (Mission.Banner != null && !editing)
        {
            if (Mouse.current is { } m && m.leftButton.wasPressedThisFrame) Click(m.position.ReadValue());
            return;
        }
        if (cinemaPlaying)
        {
            if (keys.escapeKey.wasPressedThisFrame || keys.f9Key.wasPressedThisFrame || keys.f10Key.wasPressedThisFrame) StopCinema("stopped");
            return;
        }
        if (keys.f9Key.wasPressedThisFrame && !commanding)
        {
            if (editing) Leave();
            else if (Battle.Mode is { } mode) Enter(mode);
        }
        if (keys.f10Key.wasPressedThisFrame && !editing)
        {
            if (commanding) StopCommand();
            else if (Battle.Mode is { } battle) StartCommand(battle);
        }
        if (commanding) { CommandUpdate(keys); return; }
        if (!editing) return;
        var mouse = Mouse.current;
        bool overPanel = mouse != null && OverPanel(mouse.position.ReadValue());
        if (overPanel && mouse!.scroll.ReadValue().y is var wheel && wheel != 0) Scroll(mouse.position.ReadValue(), Math.Sign(wheel));
        if (mouse != null) Sliding(mouse);
        if (overPanel && mouse!.leftButton.wasPressedThisFrame)
        {
            Click(mouse.position.ReadValue());
            if (!editing) return; // Play or Leave
        }
        if (previewing) { Preview(keys); return; }
        if (typing != null) { Type(keys); return; }
        if (savedList != null) { if (keys.escapeKey.wasPressedThisFrame) savedList = null; return; } // waits for a pick
        FlyCamera(keys, mouse, overPanel);
        EditKeys(keys);
        switch (tab)
        {
            case Tab.Mission: MissionMap(mouse, overPanel); break;
            case Tab.Cinema: TimelineInput(mouse); CinemaMap(mouse, overPanel); break;
            default: EditTanks(mouse, overPanel); break;
        }
    }

    bool bannerHeld; // the game's controls are off for the mission's end banner (not the editor's or command view's)

    // The editor's three parts: placing tanks and their orders, the mission, the cinematic.
    enum Tab { Tanks, Mission, Cinema }
    Tab tab;

    /// The Battle Editor's menu and the battle it's starting.
    static void MenuLaunch()
    {
        Menu.Step();
        MainMenu.Update();
    }

    void SwitchTab(Tab to)
    {
        tab = to; tool = Tool.Tanks; missionTool = MissionTool.None; drag = Drag.None; typing = null;
        Rebuild();
    }

    void Enter(DeathmatchGameMode mode)
    {
        if (cinemaPlaying) StopCinema("the editor opened");
        Mission.Stop();
        Puppet.ReleaseAll();
        editing = true;
        map = Battle.Map(mode);
        timeScaleBefore = Time.timeScale;
        Time.timeScale = 0; // the battle waits
        OpenView();
        // The battle's own tanks out of the way, deep under the map (switched off, the game still draws their track
        // belts and plays their sound): the map starts empty, only the markers show. Looking down on where they were,
        // high enough to see both teams' ground.
        var tanks = Battle.Tanks(mode);
        topView = TopView(tanks.Select(t => t.transform.position).ToList(), view!.transform.position);
        movedTanks.Clear();
        foreach (var t in tanks)
        {
            movedTanks.Add((t.transform, t.transform.position, t.transform.rotation));
            Put(t.transform, t.transform.position + Vector3.down * 1000, t.transform.rotation);
        }
        Physics.SyncTransforms();
        allDesigns = Files.Designs();
        ShowFaction();
        // A new battle starts empty (Load saved brings the saved one back); back in the same one (after Play, or F9 and
        // F9 again), what's placed stays.
        if (file.Map != map || mode.GetInstanceID() != editedBattle) file = new BattleFile { Map = map };
        editedBattle = mode.GetInstanceID();
        selected = null;
        Rebuild();
        ToTopView();
        Trace.Write($"editor open on '{map}': {file.Units.Count} tanks placed, {movedTanks.Count} of the battle's moved away, {designs.Count} designs, " +
                    $"{gameInput.Count} game controls, {hud.Count} HUD canvases, {fogs.Count} fogs off, camera over {topView.At} at {topView.Height:0} m");
        Say($"Battle Editor on {map}. Click the ground to place a tank, F9 to leave.");
    }

    /// The view from above, for the editor and the command view: its own camera instead of the game's (set up as the
    /// game's is, HDRP settings too; the game's follows the player's tank), the mouse pointer free, and the game's
    /// controls (the right mouse button would aim and open the command wheel), tank HUD and fog off. CloseView puts
    /// it all back.
    void OpenView(bool cinematic = false)
    {
        gameCamera = Camera.main;
        view = new GameObject("Battle Editor camera").AddComponent<Camera>();
        if (gameCamera != null)
        {
            view.CopyFrom(gameCamera);
            if (gameCamera.GetComponent<HDAdditionalCameraData>() is { } hd) hd.CopyTo(view.gameObject.AddComponent<HDAdditionalCameraData>());
            yaw = gameCamera.transform.eulerAngles.y;
            gameCamera.enabled = false;
        }
        view.fieldOfView = 60; // not the gunner's zoom
        lockBefore = Cursor.lockState; cursorBefore = Cursor.visible;
        if (cinematic) Cursor.visible = false; // a cinematic keeps the game's fog and shows nothing but the shot
        else { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        GameInputOff();
        var ui = UnityEngine.SceneManagement.SceneManager.GetSceneByName("VehicleControlUI");
        if (ui.IsValid() && ui.isLoaded)
            foreach (var root in ui.GetRootGameObjects())
                foreach (var c in root.GetComponentsInChildren<Canvas>())
                    if (c.enabled) { c.enabled = false; hud.Add(c); }
        if (!cinematic) foreach (var v in FindObjectsOfType<UnityEngine.Rendering.Volume>())
        {
            var profile = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile;
            var parts = profile?.components;
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] != null && parts[i].active && parts[i].TryCast<UnityEngine.Rendering.HighDefinition.Fog>() != null) { parts[i].active = false; fogs.Add(parts[i]); }
        }
    }

    void CloseView()
    {
        foreach (var a in gameInput) a?.Enable();
        gameInput.Clear();
        foreach (var c in hud) if (c != null) c.enabled = true;
        hud.Clear();
        foreach (var f in fogs) if (f != null) f.active = true;
        fogs.Clear();
        if (gameCamera != null) gameCamera.enabled = true;
        if (view != null) Destroy(view.gameObject);
        view = null;
        Cursor.lockState = lockBefore; Cursor.visible = cursorBefore;
    }

    static void Put(Transform tank, Vector3 at, Quaternion turn)
    {
        tank.SetPositionAndRotation(at, turn);
        foreach (var body in tank.GetComponentsInChildren<Rigidbody>()) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
    }

    /// The mouse pointer, which the game hides and locks every frame while you're in a tank: shown again every frame,
    /// and last thing in the frame (from OnGUI) too.
    static void ShowCursor()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    // Photo mode leaves the mouse pointer on screen, in the way of the shot: it hides after 1.5 s without the mouse
    // moving (and while a button turns the camera), and comes back when the mouse moves, for the photo mode panel.
    bool pointerHidden;
    float pointerMoved;

    void PhotoPointer()
    {
        bool photo = UnityEngine.SceneManagement.SceneManager.GetSceneByName("PhotomodeOverlay").isLoaded;
        var mouse = Mouse.current;
        if (!photo || mouse == null)
        {
            if (pointerHidden) { pointerHidden = false; Cursor.visible = true; }
            return;
        }
        bool turning = mouse.rightButton.isPressed || mouse.middleButton.isPressed;
        bool moved = mouse.delta.ReadValue().sqrMagnitude > 0.5f || mouse.leftButton.isPressed || mouse.scroll.ReadValue().y != 0;
        if (moved && !turning) pointerMoved = Time.unscaledTime;
        pointerHidden = turning || Time.unscaledTime - pointerMoved > 1.5f;
        if (Cursor.visible == pointerHidden) Cursor.visible = !pointerHidden;
    }

    /// Every game control that's on, off until leaving (the game may switch some back on, so every frame).
    void GameInputOff()
    {
        // The game's pause key is left on whenever Esc has nothing else to do, so Esc brings up the game's pause menu from
        // the editor (EditKeys), the command view (CommandUpdate) and under the mission's end banner. Off while Esc puts
        // down a tool, a pick, a name being typed, a preview, a selection or a tank under your control. (Switching the
        // game's pause key off and on again is the likely cause of a crash on leaving a battle for the main menu.)
        bool escPauses = editing ? typing == null && !previewing && savedList == null && tool == Tool.Tanks && missionTool == MissionTool.None && keyPick == KeyPick.None
                       : commanding ? chosen.Count == 0 && puppet == null
                       : true;
        var on = InputSystem.ListEnabledActions();
        for (int i = 0; i < on.Count; i++)
        {
            if (escPauses && on[i].name == "TogglePause") continue;
            on[i].Disable(); gameInput.Add(on[i]);
        }
        if (escPauses) foreach (var a in gameInput) if (a != null && a.name == "TogglePause" && !a.enabled) a.Enable();
    }

    /// On the Custom Battle screen opened by the Battle Editor button: a team with no tanks gets one of your designs
    /// (the screen won't start without), written into the battle setup the screen checks. Checked twice a second while
    /// the screen is up, as the screen sets its teams up after it shows. The tank is moved out of the way in the editor.
    void FillTeams()
    {
        if (Time.unscaledTime < nextFill) return;
        nextFill = Time.unscaledTime + 0.5f;
        var edit = FindObjectOfType<Sprocket.CustomBattles.CustomBattleCreation>()?.ConfigEdit;
        var teams = edit?.Config?.Teams;
        if (edit == null || teams == null) return;
        if (allDesigns.Count == 0) allDesigns = Files.Designs();
        if (allDesigns.Count == 0) return;
        int filled = 0;
        for (int t = 0; t < teams.Length; t++)
            if (teams[t] != null && teams[t].UnitCount == 0 && Battle.UnitFor(allDesigns[0].Path) is { } unit)
            {
                teams[t].Units.Add(unit, new Sprocket.CustomBattles.UnitInstanceInfo { Count = 1 });
                filled++;
            }
        if (filled == 0) return;
        edit.RaiseDirtyFlags(Sprocket.CustomBattles.BattleConfigDirtyFlags.Everything);
        Trace.Write($"setup screen: {filled} empty teams given a {allDesigns[0].Name} so the battle can start");
        Say($"Battle Editor: empty teams got a {allDesigns[0].Name} so the battle can start. Pick the map and start; it won't be in your battle.");
    }

    /// Straight down over the middle of `spots`, high enough to see them all (over `fallback` if there are none).
    static (Vector3 At, float Height) TopView(List<Vector3> spots, Vector3 fallback)
    {
        if (spots.Count == 0) return (fallback, 150);
        var middle = spots.Aggregate(Vector3.zero, (s, p) => s + p) / spots.Count;
        float spread = spots.Max(p => Vector3.Distance(new Vector3(p.x, middle.y, p.z), middle));
        return (middle, Math.Clamp(spread * 1.6f, 120, 800));
    }

    /// Looking straight down on the placed tanks, all of them; with none placed, on where the battle's own spawned.
    void ToTopView()
    {
        if (view == null) return;
        var over = file.Units.Count > 0 ? TopView(file.Units.Select(u => Files.Vector(u.Position)).ToList(), topView.At) : topView;
        pitch = 89;
        view.transform.SetPositionAndRotation(over.At + Vector3.up * over.Height, Quaternion.Euler(pitch, yaw, 0));
    }

    void ClearAll()
    {
        if (file.Units.Count == 0) return;
        Say($"Cleared {file.Units.Count} tanks (Load saved brings back the last saved battle).");
        file.Units.Clear();
        Select(null);
    }

    float pausing = -1; // left for the pause menu: when, until it shows (the battle stays still meanwhile)

    void Leave(bool forPause = false)
    {
        editing = false;
        tool = Tool.Tanks;
        drag = Drag.None;
        foreach (var (_, root) in markers) Drop(root);
        markers.Clear();
        foreach (var o in lines) Drop(o);
        lines.Clear();
        ClearMarks();
        pickable.Clear();
        previewing = false; typing = null;
        foreach (var (tank, at, turn) in movedTanks) if (tank != null) Put(tank, at, turn);
        movedTanks.Clear();
        Physics.SyncTransforms();
        CloseView();
        if (forPause) pausing = Time.unscaledTime; // the pause menu keeps the battle still and starts it again itself
        else Time.timeScale = timeScaleBefore;
        Plugin.ModLog.LogInfo("EDITOR closed");
    }

    // ---------- camera ----------

    Vector3? grab; // the ground point the middle mouse button holds while panning

    /// Mouse: right button drag looks around, middle button drag pans (the ground stays under the mouse), the wheel
    /// zooms toward the mouse. Keys: W A S D, R / F fly, Shift faster. All faster the higher up.
    void FlyCamera(Keyboard keys, Mouse? mouse, bool overPanel)
    {
        var cam = view;
        if (cam == null) return;
        var at = cam.transform.position;
        float speed = Math.Max(20, Math.Abs(at.y - topView.At.y)) * (keys.shiftKey.isPressed ? 2f : 0.7f) * Time.unscaledDeltaTime;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            var d = mouse.delta.ReadValue() * 0.15f;
            yaw += d.x; pitch = Math.Clamp(pitch - d.y, -89, 89);
        }
        var flat = Quaternion.Euler(0, yaw, 0);
        var move = Vector3.zero;
        if (keys.wKey.isPressed) move += flat * Vector3.forward;
        if (keys.sKey.isPressed) move -= flat * Vector3.forward;
        if (keys.dKey.isPressed) move += flat * Vector3.right;
        if (keys.aKey.isPressed) move -= flat * Vector3.right;
        if (keys.rKey.isPressed) move += Vector3.up;
        if (keys.fKey.isPressed) move -= Vector3.up;
        at += move * speed;
        if (mouse != null)
        {
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (mouse.middleButton.wasPressedThisFrame && !overPanel) grab = Ground(ray) ?? Flat(ray, topView.At.y);
            if (!mouse.middleButton.isPressed) grab = null;
            if (grab is { } held && Flat(ray, held.y) is { } under) at += new Vector3(held.x - under.x, 0, held.z - under.z);
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel != 0 && !overPanel && (Ground(ray) ?? Flat(ray, topView.At.y)) is { } target)
            {
                float d = Vector3.Distance(at, target);
                at += ray.direction * Math.Min(d * 0.2f * Math.Sign(wheel), d - 5); // never through the ground
            }
        }
        cam.transform.SetPositionAndRotation(at, Quaternion.Euler(pitch, yaw, 0));
    }

    /// Where `ray` meets the level `y`, if it goes that way.
    static Vector3? Flat(Ray ray, float y)
    {
        if (Math.Abs(ray.direction.y) < 1e-4f) return null;
        float t = (y - ray.origin.y) / ray.direction.y;
        return t > 0 ? ray.origin + ray.direction * t : null;
    }

    // ---------- tanks ----------

    /// Keys for what the panels' buttons do. Tab and 1 / 2 pick the design and team of the next tank placed.
    void EditKeys(Keyboard keys)
    {
        bool shift = keys.shiftKey.isPressed, ctrl = keys.ctrlKey.isPressed;
        if (keys.tabKey.wasPressedThisFrame && designs.Count > 0) PickDesign((design + (shift ? designs.Count - 1 : 1)) % designs.Count);
        if (keys.digit1Key.wasPressedThisFrame) team = 0;
        if (keys.digit2Key.wasPressedThisFrame) team = 1;
        if (keys.qKey.wasPressedThisFrame) { if (tab == Tab.Mission) TurnObstacle(shift ? -1 : -15); else Turn(shift ? -1 : -15); }
        if (keys.eKey.wasPressedThisFrame) { if (tab == Tab.Mission) TurnObstacle(shift ? 1 : 15); else Turn(shift ? 1 : 15); }
        if (keys.deleteKey.wasPressedThisFrame || keys.backspaceKey.wasPressedThisFrame) { if (tab == Tab.Mission) RemoveMissionPick(); else if (tab == Tab.Cinema) DeleteKey(); else Remove(); }
        if (keys.pKey.wasPressedThisFrame) Drive();
        if (ctrl && keys.sKey.wasPressedThisFrame) Save();
        if (ctrl && keys.lKey.wasPressedThisFrame) LoadSaved();
        if (keys.tKey.wasPressedThisFrame) ToTopView();
        if (keys.escapeKey.wasPressedThisFrame)
        {
            // Esc: a tool or a pick in hand put down; with none, out of the editor and into the game's pause menu (the game
            // opens it on the same Esc, its pause key left on while editing).
            if (tool != Tool.Tanks || missionTool != MissionTool.None || keyPick != KeyPick.None) { tool = Tool.Tanks; missionTool = MissionTool.None; keyPick = KeyPick.None; }
            else Leave(forPause: true);
        }
        if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) Play();
    }

    // What the left mouse button does on the map: work on tanks; add points to the selected tank's path; or pick the
    // selected tank's main target.
    enum Tool { Tanks, Path, Target }
    Tool tool;
    enum Drag { None, Move, Turn, Point }
    Drag drag;
    int dragPoint;

    /// Mouse on the map. Click the ground to place a tank (keep holding and drag to point it), click a tank to pick it
    /// (drag to move it), drag the tip of a tank's arrow to turn it, drag a path point to move it.
    void EditTanks(Mouse? mouse, bool overPanel)
    {
        var cam = view;
        if (mouse == null || cam == null) return;
        var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        if (mouse.leftButton.wasPressedThisFrame && !overPanel) Press(ray);
        if (!mouse.leftButton.isPressed) drag = Drag.None;
        if (drag == Drag.None || selected == null || Ground(ray) is not { } to) return;
        var at = Files.Vector(selected.Position);
        switch (drag)
        {
            case Drag.Move when Vector3.Distance(to, at) > 0.2f:
                selected.Position = Files.Array(to);
                Place(selected);
                DrawLines();
                break;
            case Drag.Turn when Vector2.Distance(new Vector2(to.x, to.z), new Vector2(at.x, at.z)) > 1.5f:
                selected.Yaw = (Mathf.Atan2(to.x - at.x, to.z - at.z) * Mathf.Rad2Deg + 360) % 360;
                Place(selected);
                break;
            case Drag.Point when dragPoint < selected.Path.Count && Vector3.Distance(to, Files.Vector(selected.Path[dragPoint])) > 0.2f:
                var point = selected.Path[dragPoint];
                (point[0], point[1], point[2]) = (to.x, to.y, to.z);
                DrawLines();
                break;
        }
    }

    void Press(Ray ray)
    {
        var hit = Pick(ray);
        if (tool == Tool.Path && selected != null)
        {
            if (hit is { Part: Part.Point } p && p.Unit == selected) { drag = Drag.Point; dragPoint = p.Index; }
            else if (hit is { Part: Part.Body, Unit: { } other }) { tool = Tool.Tanks; Select(other); }
            else if (Ground(ray) is { } at) { selected.AddPoint(Files.Array(at)); DrawLines(); }
            return;
        }
        if (tool == Tool.Target && selected != null)
        {
            tool = Tool.Tanks;
            if (hit is not { Part: Part.Body, Unit: { } enemy } || enemy == selected) Say("No target picked.");
            else if (enemy.Team == selected.Team) Say($"{enemy.Id} is on the same team: pick a tank of the other team.");
            else
            {
                selected.SetTarget(enemy.Id, selected.Attack?.Engage ?? "stopToFire");
                DrawLines();
                Say($"{selected.Id} goes for {enemy.Id} once its path is done.");
            }
            return;
        }
        switch (hit?.Part)
        {
            case Part.Body: Select(hit.Unit); drag = Drag.Move; break;
            case Part.Tip: Select(hit.Unit); drag = Drag.Turn; break;
            case Part.Point: Select(hit.Unit); drag = Drag.Point; dragPoint = hit.Index; break;
            case Part.Zone or Part.Mine or Part.Obstacle or Part.CamKey: break;
            default:
                if (Ground(ray) is not { } spot || designs.Count == 0) break;
                var placed = new BattleUnit { Id = file.NewId(), Team = team, Blueprint = designs[design].Path, Position = Files.Array(spot), Yaw = selected?.Yaw ?? yaw };
                file.Units.Add(placed);
                Select(placed);
                drag = Drag.Turn; // keep holding and drag to point it
                break;
        }
    }

    void Select(BattleUnit? unit)
    {
        if (unit != selected) tool = Tool.Tanks;
        selected = unit;
        Rebuild();
    }

    void StartPath()
    {
        if (selected == null) return;
        tool = tool == Tool.Path ? Tool.Tanks : Tool.Path;
        if (tool == Tool.Path) Say($"Path for {selected.Id}: click the ground to add points, drag a point to move it. Done (or Esc) to finish.");
    }

    void StartTarget()
    {
        if (selected == null) return;
        tool = tool == Tool.Target ? Tool.Tanks : Tool.Target;
        if (tool == Tool.Target) Say($"Main target for {selected.Id}: click a tank of the other team.");
    }

    /// The factions to pick designs from: all of them, then each in the list's order (yours first, the game's last).
    string[] Factions() => new[] { "All" }.Concat(allDesigns.Select(d => Files.FactionOf(d.Path)).Distinct()).ToArray();

    /// The design list cut to the picked faction (the design in hand kept if it's in it).
    void ShowFaction()
    {
        var holding = designs.ElementAtOrDefault(design).Path;
        if (faction != "All" && !allDesigns.Any(d => Files.FactionOf(d.Path) == faction)) faction = "All";
        designs = faction == "All" ? allDesigns : allDesigns.Where(d => Files.FactionOf(d.Path) == faction).ToList();
        design = Math.Max(0, designs.FindIndex(d => d.Path == holding));
        listTop = 0;
        PickDesign(design);
    }

    void StepFaction(int by)
    {
        var all = Factions();
        faction = all[(Array.IndexOf(all, faction) + by + all.Length) % all.Length];
        ShowFaction();
    }

    void PickDesign(int index)
    {
        design = index;
        if (design < listTop || design >= listTop + ListRows) listTop = Math.Max(0, design - ListRows / 2);
    }

    void Turn(float by)
    {
        if (selected == null) return;
        selected.Yaw = (selected.Yaw + by + 360) % 360;
        Place(selected);
    }

    void Remove()
    {
        if (selected == null) return;
        // A camera following it keeps its keys where they were, around where the tank stood.
        var gone = selected;
        foreach (var c in file.Cinema?.Cameras ?? new())
            if (c.Follow == gone.Id) Cinema.Refollow(c, null, id => id == gone.Id ? (Files.Vector(gone.Position), gone.Yaw) : null);
        file.Remove(selected);
        Select(null);
    }

    /// The selected tank is the one you drive, or no longer.
    void Drive()
    {
        if (selected == null) return;
        bool mine = selected.Control != "player";
        foreach (var u in file.Units) if (u.Control == "player") u.Control = "ai";
        selected.Control = mine ? "player" : "ai";
        Rebuild();
    }

    // Load saved: the battles saved on this map, to pick one from (null: the list is closed).
    List<(BattleFile Battle, string When)>? savedList;
    int savedTop;

    void LoadSaved()
    {
        savedList = Files.SavedBattles().Where(b => string.Equals(b.Battle.Map, map, StringComparison.OrdinalIgnoreCase))
            .Select(b => (b.Battle, File.GetLastWriteTime(b.Path).ToString("d MMM HH:mm"))).ToList();
        savedTop = 0;
        if (savedList.Count == 0) { savedList = null; Say($"No battles saved on {map} yet (Save makes one)."); }
    }

    void LoadBattle(BattleFile saved)
    {
        savedList = null;
        file = saved;
        Select(null);
        ToTopView();
        Say($"Loaded {BattleName}: {file.Units.Count} tanks");
    }

    /// The saved battles on this map, newest first, over everything else until one is picked or it's closed.
    void SavedPanel()
    {
        var list = savedList!;
        int rows = Math.Clamp((int)((Screen.height - 220) / Row) - 3, 3, 14);
        var box = Panel(new Rect((Screen.width - 560) / 2f, 90, 560, (rows + 3) * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w - 90), $"Battles saved on {map} ({list.Count}): click one to load it");
        Button(Line(w - 85, 85), "Close", () => savedList = null);
        y += Row + 4;
        for (int i = savedTop; i < Math.Min(list.Count, savedTop + rows); i++, y += Row)
        {
            var (b, when) = list[i];
            int rules = b.Mission?.Rules.Count ?? 0;
            string extra = (rules > 0 ? $", {rules} rules" : "") + (b.Cinema?.Cameras.Count > 0 ? ", cinematic" : "");
            Button(Line(0, w), $"{Short(b.Name, 34)}    {b.Units.Count(u => u.Team == 0)} vs {b.Units.Count(u => u.Team == 1)}{extra}    {when}", () => LoadBattle(b));
        }
        scrollers.Add((box, by => savedTop = Math.Clamp(savedTop - by, 0, Math.Max(0, list.Count - rows))));
        y = box.y + box.height - Row - Pad;
        GUI.Label(Line(0, w), (list.Count > rows ? $"{savedTop + 1}-{Math.Min(list.Count, savedTop + rows)} of {list.Count}, the wheel scrolls. " : "") + "Esc closes.");
    }

    // ---------- markers ----------

    // What a click can land on: a tank's body, the tip of its arrow (turns it), or one of its path points.
    enum Part { Body, Tip, Point, Zone, Mine, Obstacle, CamKey }
    sealed record Hit(BattleUnit? Unit, Part Part, int Index);
    readonly Dictionary<IntPtr, Hit> pickable = new();
    readonly List<(BattleUnit Unit, GameObject Root)> markers = new();
    readonly List<GameObject> lines = new();

    /// The marker, arrow tip or path point under the mouse, if any.
    Hit? Pick(Ray ray)
    {
        foreach (var h in Physics.RaycastAll(ray, 5000).OrderBy(h => h.distance))
            if (h.collider != null && pickable.TryGetValue(h.collider.gameObject.Pointer, out var hit)) return hit;
        return null;
    }

    /// The ground under the mouse: the first thing hit that isn't the editor's or a tank.
    Vector3? Ground(Ray ray)
    {
        foreach (var h in Physics.RaycastAll(ray, 5000).OrderBy(h => h.distance))
        {
            if (h.collider == null || pickable.ContainsKey(h.collider.gameObject.Pointer)) continue;
            if (h.collider.GetComponentInParent<Sprocket.Vehicles.VehicleObject>() != null) continue;
            return h.point;
        }
        return null;
    }

    void Rebuild()
    {
        foreach (var (_, root) in markers) Drop(root);
        markers.Clear();
        pickable.Clear();
        foreach (var unit in file.Units) markers.Add((unit, Marker(unit)));
        DrawLines();
        ClearMarks();
        if (tab == Tab.Mission) MissionMarks();
        if (tab == Tab.Cinema) CinemaMarks();
        Physics.SyncTransforms();
    }

    /// A tank's marker: the design's shape (Shapes: once a tank of it has been in a battle; a box the size of a tank
    /// till then) in its team's colour (lighter when picked), a yellow plate (on the box's front, or over the shape)
    /// when you drive it, and an arrow over it showing where it points, with a ball at the tip to turn it by.
    GameObject Marker(BattleUnit unit)
    {
        bool picked = unit == selected;
        var colour = unit.Reserve ? Color.Lerp(TeamColours[unit.Team % 2], Color.black, 0.55f) : TeamColours[unit.Team % 2];
        var body = picked ? Color.Lerp(colour, Color.white, 0.5f) : colour;
        var root = new GameObject("Battle Editor tank " + unit.Id);
        float front = 3.25f, top = 2.2f;
        if (Shapes.Of(unit.Blueprint) is { } shape)
        {
            var o = new GameObject("Battle Editor tank shape");
            o.transform.SetParent(root.transform, false);
            o.AddComponent<MeshFilter>().sharedMesh = shape;
            o.AddComponent<MeshRenderer>();
            Colour(o, body);
            var bounds = shape.bounds;
            var box = o.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            pickable[o.Pointer] = new Hit(unit, Part.Body, 0);
            front = Math.Max(1, bounds.max.z); top = bounds.max.y;
            if (unit.Control == "player") Block(root, new Vector3(bounds.center.x, top + 0.1f, bounds.center.z), new Vector3(1.6f, 0.15f, 1.6f), Color.yellow);
        }
        else
        {
            Block(root, new Vector3(0, 1.1f, 0), new Vector3(3.2f, 2.2f, 6.5f), body, new Hit(unit, Part.Body, 0));
            Block(root, new Vector3(0, 1.4f, 3.26f), new Vector3(1.6f, 0.6f, 0.15f), unit.Control == "player" ? Color.yellow : Color.white);
        }
        // From the middle to past the front, over the top.
        var arrow = picked ? Color.white : Color.Lerp(colour, Color.white, 0.6f);
        float y = top + 0.25f;
        Block(root, new Vector3(0, y, (front + 3.75f) / 2), new Vector3(0.45f, 0.12f, front + 3.75f), arrow);
        Block(root, new Vector3(-0.77f, y, front + 2.83f), new Vector3(0.45f, 0.12f, 2.4f), arrow, turn: 40);
        Block(root, new Vector3(0.77f, y, front + 2.83f), new Vector3(0.45f, 0.12f, 2.4f), arrow, turn: -40);
        Block(root, new Vector3(0, y, front + 4.35f), Vector3.one * 1.3f, picked ? Color.yellow : Color.white, new Hit(unit, Part.Tip, 0), PrimitiveType.Sphere);
        root.transform.SetPositionAndRotation(Files.Vector(unit.Position), Quaternion.Euler(0, unit.Yaw, 0));
        return root;
    }

    void Block(GameObject root, Vector3 at, Vector3 size, Color colour, Hit? hit = null, PrimitiveType shape = PrimitiveType.Cube, float turn = 0)
    {
        var o = GameObject.CreatePrimitive(shape);
        o.transform.SetParent(root.transform, false);
        o.transform.localPosition = at;
        o.transform.localRotation = Quaternion.Euler(0, turn, 0);
        o.transform.localScale = size;
        Colour(o, colour);
        if (hit != null) pickable[o.Pointer] = hit;
        else NoCollider(o);
    }

    void Place(BattleUnit unit)
    {
        foreach (var (u, root) in markers)
            if (u == unit) root.transform.SetPositionAndRotation(Files.Vector(unit.Position), Quaternion.Euler(0, unit.Yaw, 0));
        // The battle is frozen, so physics never catches up by itself: without this, clicks find the markers where they were made.
        Physics.SyncTransforms();
    }

    /// Every tank's path (a line through its points, a post at each) and its main target (a red line from the end of
    /// its path to the target). The picked tank's are brighter.
    void DrawLines()
    {
        foreach (var o in lines) Drop(o);
        lines.Clear();
        foreach (var key in pickable.Where(p => p.Value.Part == Part.Point).Select(p => p.Key).ToList()) pickable.Remove(key);
        foreach (var unit in file.Units)
        {
            bool picked = unit == selected;
            var colour = Color.Lerp(TeamColours[unit.Team % 2], picked ? Color.white : Color.black, picked ? 0.35f : 0.25f);
            var from = Files.Vector(unit.Position);
            var path = unit.Path;
            for (int i = 0; i < path.Count; i++)
            {
                var to = Files.Vector(path[i]);
                lines.Add(Line(from, to, 0.6f, picked ? 0.5f : 0.3f, colour));
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = "Battle Editor path point";
                post.transform.SetPositionAndRotation(to + Vector3.up * 0.6f, Quaternion.identity);
                post.transform.localScale = new Vector3(1.6f, 0.6f, 1.6f);
                Colour(post, picked ? Color.white : colour);
                pickable[post.Pointer] = new Hit(unit, Part.Point, i);
                lines.Add(post);
                from = to;
            }
            if (unit.Attack?.Target is { } id && file.Units.FirstOrDefault(u => u.Id == id) is { } enemy)
                lines.Add(Line(from, Files.Vector(enemy.Position), 2.8f, picked ? 0.4f : 0.2f, new Color(1f, 0.2f, 0.1f)));
        }
        Physics.SyncTransforms();
    }

    GameObject Line(Vector3 a, Vector3 b, float up, float width, Color colour)
    {
        var o = GameObject.CreatePrimitive(PrimitiveType.Cube);
        o.name = "Battle Editor line";
        var d = b - a;
        o.transform.SetPositionAndRotation((a + b) / 2 + Vector3.up * up, d.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(d) : Quaternion.identity);
        o.transform.localScale = new Vector3(width, width * 0.4f, d.magnitude);
        NoCollider(o);
        Colour(o, colour);
        return o;
    }

    /// Off at once (a destroyed object's collider stays in the physics scene until the frame ends), then gone.
    static void NoCollider(GameObject o) { var c = o.GetComponent<Collider>(); c.enabled = false; Destroy(c); }

    static void Drop(GameObject? o) { if (o == null) return; o.SetActive(false); Destroy(o); }

    /// A flat colour the game's renderer can draw (a new object's own material is one it can't: pink), one per colour.
    static void Colour(GameObject o, Color c)
    {
        var r = o.GetComponent<Renderer>();
        if (!Materials.TryGetValue(c, out var m) || m == null)
        {
            markerShader ??= Shader.Find("HDRP/Unlit") ?? Shader.Find("HDRP/Lit");
            if (markerShader == null) return;
            m = new Material(markerShader);
            m.SetColor("_UnlitColor", c);
            m.SetColor("_BaseColor", c);
            Materials[c] = m;
        }
        r.sharedMaterial = m;
    }

    static Shader? markerShader;
    static readonly Dictionary<Color, Material> Materials = new();

    // ---------- files ----------

    /// The battle's name: as named, or the map's.
    string BattleName => string.IsNullOrWhiteSpace(file.Name) ? map : file.Name;

    static string PathFor(string name) => Files.BattlePath(name);

    void Save()
    {
        file.Map = map;
        if (string.IsNullOrWhiteSpace(file.Name)) file.Name = map;
        Directory.CreateDirectory(Files.Battles);
        File.WriteAllText(PathFor(file.Name), file.ToJson());
        Trace.Write($"editor saved {PathFor(file.Name)}: {file.Units.Count} tanks");
        Say("Saved: " + PathFor(file.Name));
    }

    /// Save, then play: the battle restarts with these tanks where they were placed.
    void Play()
    {
        if (file.Units.Count == 0) { Say("Place some tanks first."); return; }
        if (file.Units.All(u => u.Reserve)) { Say("Every tank is a reserve: at least one has to start on the map."); return; }
        var mode = Battle.Mode;
        if (mode == null) { Say("The battle is gone."); return; }
        Save();
        Leave();
        Say(Battle.Play(mode, file) ?? "Playing: the tanks go where you placed them once they're in. F9 edits again.");
    }

    // ---------- on screen ----------

    // The panels: the editor's own (left), the selected tank's or the tab's (right). Clicks on them don't reach the map.
    const float Row = 26, Pad = 8;
    int listTop;
    static int ListRows => Math.Clamp((int)((Screen.height - 56 - 9 * Row - 40) / Row), 4, 16);
    static Rect MainPanel => new(16, 56, 480, (7 + ListRows) * Row + 2 * Pad);
    static Rect ListArea => new(16 + Pad, 56 + Pad + 4 * Row, 480 - 2 * Pad, ListRows * Row);
    static Rect SelectedPanel => new(Screen.width - 16 - 360, 56, 360, 10 * Row + 2 * Pad);

    static bool Inside(Rect r, Vector2 p) => p.x >= r.x && p.x <= r.x + r.width && p.y >= r.y && p.y <= r.y + r.height;

    // Every panel as last drawn, and the lists the mouse wheel scrolls.
    readonly List<Rect> panelsDrawn = new();
    readonly List<(Rect Area, Action<int> By)> scrollers = new();

    Rect Panel(Rect r) { GUI.Box(r, ""); panelsDrawn.Add(r); return r; }

    /// The mouse (Unity counts it up from the bottom of the screen, the panels down from the top) over a panel.
    bool OverPanel(Vector2 mouse)
    {
        var p = new Vector2(mouse.x, Screen.height - mouse.y);
        return panelsDrawn.Any(r => Inside(r, p));
    }

    void Scroll(Vector2 mouse, int by)
    {
        var p = new Vector2(mouse.x, Screen.height - mouse.y);
        foreach (var (area, act) in scrollers) if (Inside(area, p)) { act(by); return; }
    }

    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    public void OnGUI()
    {
        if (pointerHidden && Cursor.visible) Cursor.visible = false; // photo mode: last thing in the frame
        if (cinemaPlaying) { if (Cursor.visible) Cursor.visible = false; return; } // nothing over the shot
        if (!editing && !commanding && Time.unscaledTime > statusUntil && Mission.Messages.Count == 0 && Mission.Banner == null) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        panelsDrawn.Clear(); scrollers.Clear(); sliders.Clear();
        if (editing) { ShowCursor(); Guard.Run("Battle editor panels", Panels); }
        if (commanding) { ShowCursor(); Guard.Run("Command view panels", CommandPanels); }
        if (!editing) Guard.Run("mission overlay", MissionOverlay);
        if (Time.unscaledTime < statusUntil) GUI.Box(new Rect((Screen.width - 700) / 2f, 20, 700, 28), status);
        modTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
    }

    // The panels' buttons as last drawn, clicked from Update: the game keeps the mouse from the on-screen GUI (its
    // buttons draw, but never hear a click), while the mouse read directly works.
    readonly List<(Rect Area, Action Act)> buttons = new();

    void Button(Rect area, string text, Action act) { GUI.Button(area, text); buttons.Add((area, act)); }
    void Toggle(Rect area, bool on, string text, Action act) { GUI.Toggle(area, on, text); buttons.Add((area, act)); }

    /// A left click on a panel: the button under it, if any.
    void Click(Vector2 mouse)
    {
        var p = new Vector2(mouse.x, Screen.height - mouse.y);
        foreach (var (area, act) in buttons.ToList())
            if (Inside(area, p)) { act(); return; }
    }

    void Panels()
    {
        buttons.Clear();
        if (previewing) { PreviewPanel(); return; }
        if (savedList != null) { SavedPanel(); return; }
        var tabs = new Rect(16, 56 - Row - 4, 480, Row);
        float third = (tabs.width - 8) / 3;
        Toggle(new Rect(tabs.x, tabs.y, third, Row - 3), tab == Tab.Tanks, " Tanks", () => SwitchTab(Tab.Tanks));
        Toggle(new Rect(tabs.x + third + 4, tabs.y, third, Row - 3), tab == Tab.Mission, " Mission", () => SwitchTab(Tab.Mission));
        Toggle(new Rect(tabs.x + 2 * (third + 4), tabs.y, third, Row - 3), tab == Tab.Cinema, " Cinematic", () => SwitchTab(Tab.Cinema));
        panelsDrawn.Add(tabs);
        if (tab == Tab.Mission) { MissionPanels(); return; }
        if (tab == Tab.Cinema) { CinemaPanels(); return; }
        // Names over the tanks, and the picked tank's path points numbered.
        void Tag(Vector3 at, string text)
        {
            var s = view!.WorldToScreenPoint(at);
            if (s.z > 0) GUI.Label(new Rect(s.x - 60, Screen.height - s.y - 10, 220, 22), text);
        }
        if (view != null)
        {
            foreach (var (unit, root) in markers)
                Tag(root.transform.position + Vector3.up * 4, $"{unit.Id} {System.IO.Path.GetFileNameWithoutExtension(unit.Blueprint)}{(unit.Control == "player" ? " (you)" : "")}");
            if (selected != null)
            {
                // Each point numbered, with about when the tank gets there (its orders start 2 s in: Battle.StartOrders).
                var path = selected.Path;
                var from = Files.Vector(selected.Position);
                float heading = selected.Yaw, when = 2;
                for (int i = 0; i < path.Count; i++)
                {
                    var to = Files.Vector(path[i]);
                    var takes = Travel.Seconds(selected.Blueprint, from, heading, to);
                    when += takes ?? 0;
                    Tag(to + Vector3.up * 2.5f, takes == null ? $"  {i + 1}" : $"  {i + 1}  ~{when:0} s");
                    var way = to - from; way.y = 0;
                    if (way.sqrMagnitude > 0.01f) heading = Travel.Heading(way);
                    from = to;
                }
            }
        }

        var box = Panel(MainPanel);
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w), $"Battle Editor: {Short(BattleName, 30)} on {map}        tanks {file.Units.Count(u => u.Team == 0)} vs {file.Units.Count(u => u.Team == 1)}");
        y += Row;
        GUI.Label(Line(0, 60), "Name:");
        Button(Line(60, w - 60), typing == "name" ? file.Name + "_" : (string.IsNullOrEmpty(file.Name) ? "(click to name this battle)" : file.Name), () => StartTyping("name", file.Name, v => file.Name = v));
        y += Row;
        GUI.Label(Line(0, 90), "Next tank:");
        Toggle(Line(90, 150), team == 0, " Team 1 (blue)", () => team = 0);
        Toggle(Line(250, 150), team == 1, " Team 2 (red)", () => team = 1);
        y += Row;
        if (allDesigns.Count == 0) GUI.Label(Line(0, w - 130), "No designs in My Games\\Sprocket\\Factions");
        else
        {
            // The faction: click its name (or >) for the next, < for the one before.
            GUI.Label(Line(0, 58), "Faction:");
            Button(Line(60, 26), "<", () => StepFaction(-1));
            Button(Line(90, w - 250), $"{Short(faction, 22)} ({designs.Count})", () => StepFaction(1));
            Button(Line(w - 156, 26), ">", () => StepFaction(1));
        }
        Button(Line(w - 125, 60), "Up", () => listTop = Math.Max(0, listTop - ListRows));
        Button(Line(w - 60, 60), "Down", () => listTop = Math.Min(Math.Max(0, designs.Count - ListRows), listTop + ListRows));
        y += Row;
        for (int i = listTop; i < Math.Min(designs.Count, listTop + ListRows); i++, y += Row)
        {
            int pick = i;
            Button(Line(0, w), (i == design ? ">  " : "    ") + Short(designs[i].Name, 64), () => design = pick);
        }
        scrollers.Add((ListArea, by => listTop = Math.Clamp(listTop - by * 3, 0, Math.Max(0, designs.Count - ListRows))));
        y = ListArea.y + ListArea.height;
        float b = (w - 5 * 4) / 6;
        Button(Line(0, b), "Top view", ToTopView);
        Button(Line(b + 4, b), "Save", Save);
        Button(Line(2 * (b + 4), b), "Load saved", LoadSaved);
        Button(Line(3 * (b + 4), b), "Clear all", ClearAll);
        Button(Line(4 * (b + 4), b), "Play", Play);
        Button(Line(5 * (b + 4), b), "Leave", () => Leave());
        y += Row;
        GUI.Label(Line(0, w), "Left: place (drag to point it), pick, drag to move. Arrow tip: turn.");
        y += Row;
        GUI.Label(Line(0, w), "Right drag: look.  Middle drag: pan.  Wheel: zoom.  W A S D, R / F: fly.");

        var chosen = selected;
        if (chosen == null) return;
        box = Panel(SelectedPanel);
        x = box.x + Pad; w = box.width - 2 * Pad; y = box.y + Pad;
        GUI.Label(Line(0, w), $"Selected {chosen.Id}: {Short(System.IO.Path.GetFileNameWithoutExtension(chosen.Blueprint), 38)}");
        y += Row;
        GUI.Label(Line(0, w), $"Team {chosen.Team + 1}, facing {chosen.Yaw:0}°, {(chosen.Control == "player" ? "you drive it" : "the AI drives it")}");
        y += Row;
        float q = (w - 3 * 4) / 4;
        Button(Line(0, q), "-15°", () => Turn(-15));
        Button(Line(q + 4, q), "-1°", () => Turn(-1));
        Button(Line(2 * (q + 4), q), "+1°", () => Turn(1));
        Button(Line(3 * (q + 4), q), "+15°", () => Turn(15));
        y += Row;
        float h = (w - 4) / 2;
        Button(Line(0, h), "Team 1 (blue)", () => { chosen.Team = 0; Rebuild(); });
        Button(Line(h + 4, h), "Team 2 (red)", () => { chosen.Team = 1; Rebuild(); });
        y += Row;
        if (designs.Count > 0) Button(Line(0, w), "Make it: " + Short(designs[design].Name, 42), () => { chosen.Blueprint = designs[design].Path; Rebuild(); });
        y += Row;
        float t = (w - 2 * 4) / 3;
        Button(Line(0, t), chosen.Control == "player" ? "AI drives it" : "You drive it", Drive);
        Button(Line(t + 4, t), "Remove", Remove);
        Button(Line(2 * (t + 4), t), "Done", () => Select(null));
        y += Row;
        Toggle(Line(0, w), chosen.Reserve, " Reserve: off the map until a mission rule brings its team's reserves", () => { chosen.Reserve = !chosen.Reserve; Rebuild(); });
        y += Row;
        // Orders: the path it drives, then its main target.
        GUI.Label(Line(0, 110), $"Path: {chosen.Path.Count} points");
        Button(Line(110, 90), tool == Tool.Path ? "Done" : "Add points", StartPath);
        Button(Line(204, 64), "Undo", () => { chosen.RemoveLastPoint(); DrawLines(); });
        Button(Line(272, w - 272), "Clear", () => { chosen.ClearPath(); DrawLines(); });
        y += Row;
        GUI.Label(Line(0, 110), $"Target: {chosen.Attack?.Target ?? "none"}");
        Button(Line(110, 158), tool == Tool.Target ? "Cancel" : "Pick target", StartTarget);
        Button(Line(272, w - 272), "Clear", () => { chosen.SetTarget(null); DrawLines(); });
        y += Row;
        if (chosen.Attack is { } attack)
        {
            GUI.Label(Line(0, 110), "Attacking:");
            Toggle(Line(110, 110), attack.Engage != "fireOnTheMove", " Stops to fire", () => attack.Engage = "stopToFire");
            Toggle(Line(224, w - 224), attack.Engage == "fireOnTheMove", " Fires moving", () => attack.Engage = "fireOnTheMove");
        }
        else GUI.Label(Line(0, w), "Without a target it fights whatever it meets.");
    }
}
