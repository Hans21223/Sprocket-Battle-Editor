using Sprocket;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// The command view (F10 in a battle): the battle from above while it plays. Any AI tank of either team is selected and
/// ordered with the mouse; the lines show what each selected tank's AI plans: the route its path-finding computed (cyan),
/// the rest of its editor path (yellow) and its target (red). Space pauses. The tank you drive takes no orders, but
/// Drive it puts you in any selected tank.
public sealed partial class BattleEditor
{
    bool commanding;
    bool paused;
    bool allRoutes;                                       // every tank's lines, not just the selected ones
    int showTeam = -1;                                    // which team's tanks a click or box selects: -1 both
    readonly List<VehicleBehaviour> chosen = new();       // the selected tanks
    List<VehicleBehaviour> seen = new();                  // the battle's tanks, as last looked up
    readonly List<(VehicleBehaviour Tank, string Name)> names = new(); // their labels, as last looked up
    readonly Dictionary<IntPtr, VehicleBehaviour> attacking = new(); // targets given here, by tank
    readonly List<LineRenderer> drawn = new();            // the route lines and rings, reused
    int drawnUsed;
    Vector2? boxFrom, orderFrom;                          // where a left / right mouse press began (from the bottom)
    float nextDraw, nextLook;

    void StartCommand(DeathmatchGameMode mode)
    {
        commanding = true;
        paused = false;
        timeScaleBefore = Time.timeScale;
        OpenView();
        seen = Battle.Behaviours(mode);
        topView = TopView(seen.Select(t => t.Position).ToList(), view!.transform.position);
        pitch = 89;
        view.transform.SetPositionAndRotation(topView.At + Vector3.up * topView.Height, Quaternion.Euler(pitch, yaw, 0));
        nextLook = 0;
        Trace.Write($"command view open: {seen.Count} tanks, {seen.Count(Commandable)} take orders, {Battle.AICount} AIs");
        Say($"Command view: select any tanks with the left mouse button, right click to move or attack. Space pauses, {Plugin.CommandKey} goes back.");
    }

    void StopCommand(bool forPause = false)
    {
        if (puppet != null) { Puppet.Release(puppet); puppet = null; }
        commanding = false;
        foreach (var l in drawn) if (l != null) Drop(l.gameObject);
        drawn.Clear();
        chosen.Clear();
        boxFrom = orderFrom = null;
        CloseView();
        if (forPause) pausing = Time.unscaledTime; // the pause menu keeps the battle still and starts it again itself
        else Time.timeScale = timeScaleBefore;
        paused = false;
        Trace.Write("command view closed");
    }

    void Pause(bool on)
    {
        paused = on;
        Time.timeScale = on ? 0 : timeScaleBefore > 0 ? timeScaleBefore : 1;
    }

    /// A tank the AI drives (either team): one you can give orders to.
    static bool Commandable(VehicleBehaviour t) => t != null && t.ControlType != Sprocket.Vehicles.Control.ControlType.Player;

    /// Commandable, and of the team a click or box picks from.
    bool Pickable(VehicleBehaviour t) => Commandable(t) && (showTeam < 0 || GroupOf(t) == showTeam);

    static string TeamName(int team) => team == 0 ? "Blue" : team == 1 ? "Red" : $"Team {team + 1}";
    static int GroupOf(VehicleBehaviour tank) => Battle.UnitOf(tank)?.Team ?? (int)tank.ID.TeamID - 1;
    static string TankSide(VehicleBehaviour tank) => Battle.Playing?.FreeForAll == true ? "FFA" : TeamName(GroupOf(tank));

    bool OnScreen(VehicleBehaviour t, out Vector2 at)
    {
        var p = view!.WorldToScreenPoint(t.Position + Vector3.up * 2);
        at = new Vector2(p.x, p.y);
        return p.z > 0;
    }

    /// The tank drawn nearest the mouse, within 40 pixels.
    VehicleBehaviour? Nearest(IEnumerable<VehicleBehaviour> tanks, Vector2 mouse)
    {
        VehicleBehaviour? best = null;
        float bestDistance = 40;
        foreach (var t in tanks)
            if (OnScreen(t, out var at) && Vector2.Distance(at, mouse) < bestDistance) { best = t; bestDistance = Vector2.Distance(at, mouse); }
        return best;
    }

    bool Chosen(VehicleBehaviour t) => chosen.Any(c => c.Pointer == t.Pointer);

    void CommandUpdate(Keyboard keys)
    {
        var mode = Battle.Mode;
        if (mode == null || view == null) { StopCommand(); return; } // the battle ended
        var mouse = Mouse.current;
        var at = mouse?.position.ReadValue() ?? Vector2.zero;
        bool overPanel = mouse != null && OverPanel(at);
        if (mouse != null) Sliding(mouse);
        if (overPanel && mouse!.leftButton.wasPressedThisFrame)
        {
            Click(at);
            if (!commanding) return; // Back to driving
        }
        FlyCamera(keys, mouse, overPanel);
        if (puppet != null) PuppetKeys(keys, mouse, overPanel);
        if (keys.spaceKey.wasPressedThisFrame) Pause(!paused);
        if (keys.pKey.wasPressedThisFrame) { allRoutes = !allRoutes; nextDraw = 0; }
        // Esc: the selection let go; with none (and no tank under your control), out and into the game's pause menu.
        if (keys.escapeKey.wasPressedThisFrame)
        {
            if (chosen.Count > 0 || puppet != null) { chosen.Clear(); nextDraw = 0; }
            else StopCommand(forPause: true);
        }
        if (keys.tKey.wasPressedThisFrame) ToChosen();
        // The battle's tanks and their labels, four times a second (with many tanks, every frame cost too much).
        if (Time.unscaledTime >= nextLook)
        {
            nextLook = Time.unscaledTime + 0.25f;
            seen = Battle.Behaviours(mode);
            chosen.RemoveAll(t => t == null || !seen.Any(s => s.Pointer == t.Pointer) || !Commandable(t));
            Label();
        }
        if (mouse != null)
        {
            Selecting(mouse, at, keys.shiftKey.isPressed, overPanel);
            Ordering(mouse, at, overPanel);
        }
        if (Time.unscaledTime >= nextDraw) { nextDraw = Time.unscaledTime + 0.2f; DrawRoutes(); }
    }

    /// Each tank's label: its battle id (or team), "(you)", its team, and what its AI does when selected.
    void Label()
    {
        names.Clear();
        foreach (var t in seen)
        {
            if (t == null) continue;
            string name = (Battle.UnitOf(t)?.Id ?? "tank") + " " + TankSide(t);
            if (t.ControlType == Sprocket.Vehicles.Control.ControlType.Player) name += " (you)";
            else if (Battle.Commanded(t)) name += " *";
            if (Chosen(t) || allRoutes) { var task = Battle.Task(t); if (task != "") name += ": " + task.Replace("Task", ""); }
            names.Add((t, name));
        }
    }

    /// Left click: the tank under the mouse (Shift: add or take it away). Left drag: every tank in the box (Shift:
    /// added). Only tanks of the team shown (both by default). A click on nothing clears the selection.
    void Selecting(Mouse mouse, Vector2 at, bool shift, bool overPanel)
    {
        if (mouse.leftButton.wasPressedThisFrame && !overPanel) boxFrom = at;
        if (boxFrom is not { } from || !mouse.leftButton.wasReleasedThisFrame) return;
        boxFrom = null;
        var pickable = seen.Where(Pickable).ToList();
        var hit = new List<VehicleBehaviour>();
        if (Vector2.Distance(from, at) > 6)
        {
            float x0 = Math.Min(from.x, at.x), x1 = Math.Max(from.x, at.x), y0 = Math.Min(from.y, at.y), y1 = Math.Max(from.y, at.y);
            hit = pickable.Where(t => OnScreen(t, out var s) && s.x >= x0 && s.x <= x1 && s.y >= y0 && s.y <= y1).ToList();
        }
        else if (Nearest(pickable, at) is { } one) hit.Add(one);
        if (shift && hit.Count == 1 && Chosen(hit[0])) chosen.RemoveAll(c => c.Pointer == hit[0].Pointer);
        else
        {
            if (!shift) chosen.Clear();
            foreach (var t in hit) if (!Chosen(t)) chosen.Add(t);
        }
        nextDraw = nextLook = 0;
    }

    /// Right click (a right drag turns the camera instead): on a tank, each selected tank of another team attacks it;
    /// on the ground, they drive there side by side, facing the way they're going.
    void Ordering(Mouse mouse, Vector2 at, bool overPanel)
    {
        if (mouse.rightButton.wasPressedThisFrame && !overPanel) orderFrom = at;
        if (orderFrom is not { } from || !mouse.rightButton.wasReleasedThisFrame) return;
        orderFrom = null;
        if (Vector2.Distance(from, at) > 6 || chosen.Count == 0) return;
        if (Nearest(seen.Where(t => t != null && !Chosen(t)), at) is { } enemy)
        {
            int sent = 0, sameTeam = 0;
            foreach (var t in chosen)
            {
                if (Battle.Playing?.FreeForAll != true && t.ID.TeamID == enemy.ID.TeamID) { sameTeam++; continue; }
                if (Battle.SendAgainst(t, enemy)) { attacking[t.Pointer] = enemy; sent++; }
            }
            Say(sameTeam == chosen.Count ? $"That tank is on the same team ({TankSide(enemy)}): right click a tank of the other team to attack."
                : $"{sent} of {chosen.Count} tanks attack{(sameTeam > 0 ? $" ({sameTeam} are on its team)" : "")}.");
        }
        else if (Ground(view!.ScreenPointToRay(at)) is { } spot) MoveChosen(spot);
        nextDraw = nextLook = 0;
    }

    void MoveChosen(Vector3 to)
    {
        var centre = chosen.Aggregate(Vector3.zero, (s, t) => s + t.Position) / chosen.Count;
        var ahead = to - centre;
        ahead.y = 0;
        ahead = ahead.sqrMagnitude > 1 ? ahead.normalized : Vector3.forward;
        var side = new Vector3(ahead.z, 0, -ahead.x);
        var line = chosen.OrderBy(t => Vector3.Dot(t.Position - centre, side)).ToList(); // keeps their order, so paths don't cross
        int sent = 0;
        for (int i = 0; i < line.Count; i++)
        {
            var spot = to + side * ((i - (line.Count - 1) / 2f) * 12f);
            spot = Ground(new Ray(spot + Vector3.up * 300, Vector3.down)) ?? spot;
            attacking.Remove(line[i].Pointer);
            if (Battle.SendTo(line[i], spot, ahead)) sent++;
        }
        Say(sent == chosen.Count ? $"{sent} tanks on the move." : $"{sent} of {chosen.Count} tanks on the move (the rest can't take orders).");
    }

    /// The selected tanks back to the game's own AI: no orders of yours, its own orders and formations again.
    void FreeChosen()
    {
        foreach (var t in chosen) { Battle.Free(t); attacking.Remove(t.Pointer); }
        Say($"{chosen.Count} tanks back to the game's AI.");
        nextDraw = nextLook = 0;
    }

    void ForceStopChosen()
    {
        if (chosen.Count == 0) { Say("Select one or more AI tanks to force stop."); return; }
        if (puppet != null && Chosen(puppet)) ReleasePuppet();
        int stopped = 0;
        foreach (var tank in chosen)
            if (Battle.ForceStop(tank)) { attacking.Remove(tank.Pointer); stopped++; }
        Say($"{stopped} of {chosen.Count} tanks held in place. They can still aim and fire; a new move or attack order releases them.");
        nextDraw = nextLook = 0;
    }

    void DriveChosen()
    {
        if (chosen.Count != 1) { Say("Select one tank to drive."); return; }
        var tank = chosen[0];
        if (!Battle.Drive(tank)) { Say("Couldn't put you in that tank."); return; }
        chosen.Clear();
        attacking.Remove(tank.Pointer);
        StopCommand();
    }

    void ToChosen()
    {
        var spots = (chosen.Count > 0 ? chosen : seen).Select(t => t.Position).ToList();
        if (spots.Count == 0 || view == null) return;
        var over = TopView(spots, view.transform.position);
        pitch = 89;
        view.transform.SetPositionAndRotation(over.At + Vector3.up * over.Height, Quaternion.Euler(pitch, yaw, 0));
    }

    /// For each selected tank (every tank with P): its planned route, the rest of its editor path, the line to its
    /// target, and a ring around it. The lines are kept and reused, not made anew each time.
    void DrawRoutes()
    {
        drawnUsed = 0;
        foreach (var t in seen)
        {
            if (t == null) continue;
            bool picked = Chosen(t);
            if (!picked && !allRoutes) continue;
            var route = Battle.Route(t);
            if (route.Count > 1) Trail(route, new Color(0.2f, 0.9f, 1f), picked ? 0.7f : 0.4f);
            var (path, target) = Battle.Plan(t);
            if (path.Count > 0) Trail(path.Prepend(t.Position).ToList(), Color.yellow, 0.4f);
            var enemy = attacking.TryGetValue(t.Pointer, out var given) && given != null ? given : target;
            if (enemy != null) Trail(new List<Vector3> { t.Position, enemy.Position }, new Color(1f, 0.2f, 0.1f), 0.3f);
            if (picked)
                Trail(Enumerable.Range(0, 25).Select(i => t.Position + Quaternion.Euler(0, i * 15, 0) * Vector3.forward * 5).ToList(),
                      new Color(0.3f, 1f, 0.4f), 0.3f);
        }
        for (int i = drawnUsed; i < drawn.Count; i++) if (drawn[i] != null && drawn[i].gameObject.activeSelf) drawn[i].gameObject.SetActive(false);
    }

    void Trail(List<Vector3> points, Color colour, float width)
    {
        if (drawnUsed == drawn.Count || drawn[drawnUsed] == null)
        {
            var line = new GameObject("Battle Editor route").AddComponent<LineRenderer>();
            if (drawnUsed == drawn.Count) drawn.Add(line); else drawn[drawnUsed] = line;
        }
        var l = drawn[drawnUsed++];
        if (!l.gameObject.activeSelf) l.gameObject.SetActive(true);
        l.widthMultiplier = width;
        l.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) l.SetPosition(i, points[i] + Vector3.up * 1.5f);
        Colour(l.gameObject, colour);
    }

    // ---------- on screen ----------

    static Rect CommandPanel => new(16, 56, 560, 10 * Row + 2 * Pad);

    // ---------- force control: one tank's drive, guns and trigger yours ----------

    VehicleBehaviour? puppet;
    bool aimAtMouse = true;
    float cruise;

    void TakeChosen()
    {
        if (chosen.Count != 1) { Say("Select one tank to take control of."); return; }
        if (puppet != null) Puppet.Release(puppet);
        puppet = chosen[0];
        var state = Puppet.Take(puppet, Battle.UnitOf(puppet)?.Id ?? "tank");
        cruise = 0;
        state.Turret = 0; state.Gun = 0;
        Battle.Free(puppet); // no orders of yours or the game's on it while you drive it
        Say("Force control: arrow keys drive, the guns follow the mouse (or the sliders), G fires. Release gives it back to its AI.");
    }

    void ReleasePuppet()
    {
        if (puppet != null) Puppet.Release(puppet);
        puppet = null;
    }

    /// Arrow keys: throttle and steering while held (else the cruise throttle, straight). The mouse: where the guns aim,
    /// over the map. G: fire.
    void PuppetKeys(Keyboard keys, Mouse? mouse, bool overPanel)
    {
        if (puppet == null || Puppet.Of(puppet) is not { } s) { puppet = null; return; }
        s.Throttle = keys.upArrowKey.isPressed ? 1 : keys.downArrowKey.isPressed ? -1 : cruise;
        s.Steer = keys.rightArrowKey.isPressed ? 1 : keys.leftArrowKey.isPressed ? -1 : 0;
        if (aimAtMouse && mouse != null && view != null && !overPanel)
        {
            var ray = view.ScreenPointToRay(mouse.position.ReadValue());
            foreach (var h in Physics.RaycastAll(ray, 5000).OrderBy(h => h.distance))
            {
                if (h.collider == null || h.collider.isTrigger || h.collider.transform.IsChildOf(puppet.transform.root)) continue;
                s.AimAt = h.point;
                break;
            }
        }
        else if (!aimAtMouse) s.AimAt = null;
        if (keys.gKey.wasPressedThisFrame) s.Shots++; // (F flies the camera down)
    }

    void PuppetPanel()
    {
        if (puppet == null || Puppet.Of(puppet) is not { } s) return;
        var box = Panel(new Rect(16, CommandPanel.yMax + 8, 560, 8 * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w), $"Force control: {s.Name} ({TankSide(puppet)}). Arrows drive, G fires.");
        y += Row;
        float t = (w - 3 * 4) / 4;
        Toggle(Line(0, t), s.Drive, " Drive", () => { s.Drive = !s.Drive; Puppet.Changed(); });
        Toggle(Line(t + 4, t), s.Aim, " Aim", () => { s.Aim = !s.Aim; Puppet.Changed(); });
        Toggle(Line(2 * (t + 4), t), s.HoldFire, " Hold fire", () => { s.HoldFire = !s.HoldFire; Puppet.Changed(); });
        Toggle(Line(3 * (t + 4), t), aimAtMouse, " Aim at mouse", () => { aimAtMouse = !aimAtMouse; if (!aimAtMouse) s.AimAt = null; });
        y += Row;
        Slider(Line(0, w), cruise, -1, 1, $"Cruise throttle: {cruise:0.00} (arrows override)", v => cruise = MathF.Round(v * 20) / 20);
        y += Row;
        if (!aimAtMouse)
        {
            Slider(Line(0, w), s.Turret, -180, 180, $"Turret: {s.Turret:0}° from the hull's front", v => s.Turret = MathF.Round(v));
            y += Row;
            Slider(Line(0, w), s.Gun, -20, 45, $"Gun elevation: {s.Gun:0}°", v => s.Gun = MathF.Round(v));
            y += Row;
        }
        else { GUI.Label(Line(0, w), "The guns aim where the mouse points on the map."); y += 2 * Row; }
        Button(Line(0, t), "Fire", () => s.Shots++);
        Button(Line(t + 4, t), "Stop", () => { cruise = 0; s.Throttle = 0; s.Steer = 0; });
        Button(Line(2 * (t + 4), t), "Look at it", () => { if (view != null) { var p = puppet.Position; pitch = 35; view.transform.SetPositionAndRotation(p + Quaternion.Euler(0, yaw, 0) * new Vector3(0, 12, -22), Quaternion.Euler(pitch, yaw, 0)); } });
        Button(Line(3 * (t + 4), t), "Release", ReleasePuppet);
    }

    void CommandPanels()
    {
        buttons.Clear();
        if (Battle.Mode == null || view == null) return;
        foreach (var (t, name) in names)
            if (t != null && OnScreen(t, out var s)) GUI.Label(new Rect(s.x - 60, Screen.height - s.y - 24, 260, 22), name);
        // The box being dragged.
        var mouse = Mouse.current;
        if (boxFrom is { } from && mouse != null && Vector2.Distance(from, mouse.position.ReadValue()) > 6)
        {
            var to = mouse.position.ReadValue();
            GUI.Box(new Rect(Math.Min(from.x, to.x), Screen.height - Math.Max(from.y, to.y), Math.Abs(to.x - from.x), Math.Abs(to.y - from.y)), "");
        }

        var box = Panel(CommandPanel);
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w), $"Command view: {chosen.Count} selected{(paused ? "      PAUSED" : "")}");
        y += Row;
        float b = (w - 3 * 4) / 4;
        Button(Line(0, b), paused ? "Resume" : "Pause", () => Pause(!paused));
        Button(Line(b + 4, b), "Select all", () => { chosen.Clear(); chosen.AddRange(seen.Where(Pickable)); nextDraw = nextLook = 0; });
        Button(Line(2 * (b + 4), b), allRoutes ? "Lines: all" : "Lines: selected", () => { allRoutes = !allRoutes; nextDraw = nextLook = 0; });
        Button(Line(3 * (b + 4), b), "Back to driving", () => StopCommand());
        y += Row;
        string group = TeamName(showTeam) + (Battle.Playing?.FreeForAll == true ? " group" : " only");
        Button(Line(0, b), showTeam < 0 ? "Pick: all tanks" : $"Pick: {group}", () => { showTeam = showTeam < 1 ? showTeam + 1 : -1; });
        Button(Line(b + 4, b), "Drive it", DriveChosen);
        Button(Line(2 * (b + 4), b), "Force stop", ForceStopChosen);
        Button(Line(3 * (b + 4), b), Battle.OverrideGame ? "Override: on" : "Override: off", () => Battle.OverrideGame = !Battle.OverrideGame);
        y += Row;
        Button(Line(0, b), "Release to AI", FreeChosen);
        GUI.Label(Line(b + 4, w - b - 4), "Force stop: holds position; new move / attack releases it.");
        y += Row;
        string sight = Battle.SightEvery == 0 ? $"auto (now 1 in {Battle.SightNow})" : Battle.SightEvery == 1 ? "game rate" : $"1 in {Battle.SightEvery}";
        Button(Line(0, b), "AI sight: " + sight.Replace("auto (now ", "auto (").Replace("game rate", "full"), () => Battle.SightEvery = (Battle.SightEvery + 1) % 4);
        Button(Line(b + 4, b), puppet != null && chosen.Count == 1 && chosen[0].Pointer == puppet.Pointer ? "Controlling" : "Take control", TakeChosen);
        GUI.Label(Line(2 * (b + 4), 2 * b + 4), $"{1000 / Math.Max(1, frameMs):0} fps, {frameMs:0.0} ms, {physicsPerFrame:0.0} physics steps/frame");
        y += Row;
        GUI.Label(Line(0, w), $"{seen.Count} tanks, {Battle.AICount} AIs, mod {modMs:0.00} ms/frame. * = your orders only (Override).");
        y += Row;
        if (Battle.Playing != null)
        {
            Button(Line(0, 220), "Record current battle", RecordRunningBattle);
            GUI.Label(Line(230, w - 230), "Starts now, without restarting.");
        }
        y += Row;
        GUI.Label(Line(0, w), "Left click / drag a box: select tanks (Shift: add). Esc: none.");
        y += Row;
        GUI.Label(Line(0, w), Battle.Playing?.FreeForAll == true ? "Right click the ground: move. Right click any other tank: attack." : "Right click the ground: move. Right click an enemy tank: attack.");
        y += Row;
        GUI.Label(Line(0, w), $"Right drag: look. Middle drag: pan. Wheel: zoom. Space: pause. P: all lines. {Plugin.CommandKey}: back.");
        PuppetPanel();
    }
}
