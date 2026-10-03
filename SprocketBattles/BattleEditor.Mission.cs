using Sprocket;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// The editor's Mission tab: zones (named circles rules refer to), AT mines, obstacles, and the rules ("when this
/// happens, do that") on the map; and, in the battle, the mission's messages and its end banner.
public sealed partial class BattleEditor
{
    enum MissionTool { None, Zone, Mine, Hedgehog, Block, ATGun, Erase }
    MissionTool missionTool;
    object? missionPick;                 // the zone, mine or obstacle picked
    int minefield = 12;                  // mines a "fill the zone" lays
    int atGun, atTeam = 1;               // the AT gun the AT gun tool places, and for which team

    /// The AT guns to place: the game's own (its scenarios' AT guns) and your designs named like one.
    List<(string Path, string Name)> ATGuns() => allDesigns.Where(d => Files.IsATGun(d.Path)).ToList();
    int rulesTop;
    float nextMarks;
    bool marksDirty;
    readonly List<GameObject> marks = new();

    MissionData M => file.Mission ??= new MissionData();

    void ClearMarks()
    {
        foreach (var o in marks) Drop(o);
        marks.Clear();
        foreach (var key in pickable.Where(p => p.Value.Part is Part.Zone or Part.Mine or Part.Obstacle or Part.CamKey).Select(p => p.Key).ToList()) pickable.Remove(key);
    }

    // ---------- on the map ----------

    static readonly Color ZoneColour = new(0.95f, 0.75f, 0.2f), PickedColour = new(1f, 1f, 0.6f);

    void MissionMarks()
    {
        var m = M;
        for (int i = 0; i < m.Zones.Count; i++)
        {
            var z = m.Zones[i];
            bool picked = missionPick == z;
            var c = Files.Vector(z.Center);
            var colour = picked ? PickedColour : ZoneColour;
            // The ring: short posts around it (they follow the ground), a post in the middle to pick it by, and a ball
            // on its edge to drag its size by.
            for (int k = 0; k < 36; k++)
            {
                var a = Quaternion.Euler(0, k * 10, 0) * Vector3.forward * z.Radius;
                var b = Quaternion.Euler(0, (k + 1) * 10, 0) * Vector3.forward * z.Radius;
                var pa = Mission.Ground(c + a); var pb = Mission.Ground(c + b);
                marks.Add(Line(pa, pb, 0.4f, picked ? 0.5f : 0.3f, colour));
            }
            Mark(PrimitiveType.Cylinder, c + Vector3.up * 2, new Vector3(1.2f, 2f, 1.2f), colour, new Hit(null, Part.Zone, i));
            Mark(PrimitiveType.Sphere, Mission.Ground(c + Vector3.right * z.Radius) + Vector3.up, Vector3.one * 2.2f, colour, new Hit(null, Part.Zone, -1 - i));
        }
        for (int i = 0; i < m.Mines.Count; i++)
            Mark(PrimitiveType.Cylinder, Files.Vector(m.Mines[i].Position) + Vector3.up * 0.15f, new Vector3(1.6f, 0.15f, 1.6f),
                 missionPick == m.Mines[i] ? PickedColour : new Color(0.75f, 0.2f, 0.15f), new Hit(null, Part.Mine, i));
        for (int i = 0; i < m.Obstacles.Count; i++)
        {
            var o = Mission.Build(m.Obstacles[i], live: false);
            o.name = "Battle Editor obstacle marker";
            var pick = o.AddComponent<BoxCollider>();
            pick.center = new Vector3(0, 0.8f, 0); pick.size = new Vector3(2.2f, 1.6f, 2.2f);
            pickable[o.Pointer] = new Hit(null, Part.Obstacle, i);
            marks.Add(o);
            if (missionPick == m.Obstacles[i]) Mark(PrimitiveType.Sphere, Files.Vector(m.Obstacles[i].Position) + Vector3.up * 2.5f, Vector3.one * 0.6f, PickedColour, null);
        }
    }

    void Mark(PrimitiveType shape, Vector3 at, Vector3 size, Color colour, Hit? hit)
    {
        var o = GameObject.CreatePrimitive(shape);
        o.name = "Battle Editor mark";
        o.transform.position = at;
        o.transform.localScale = size;
        Colour(o, colour);
        if (hit != null) pickable[o.Pointer] = hit; else NoCollider(o);
        marks.Add(o);
    }

    /// The marks again soon (a drag changes them many times a second).
    void MarksChanged() { marksDirty = true; }

    // What a drag on the Mission tab moves.
    enum MissionDrag { None, Zone, ZoneSize, Mine, Obstacle }
    MissionDrag missionDrag;

    /// Mouse on the map in the Mission tab: with a tool, click the ground to add (a zone, a mine, an obstacle) or a thing
    /// to erase it; without, click a thing to pick it and drag to move it (a zone's edge ball sizes it).
    void MissionMap(Mouse? mouse, bool overPanel)
    {
        var cam = view;
        if (mouse == null || cam == null) return;
        var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        var m = M;
        if (mouse.leftButton.wasPressedThisFrame && !overPanel)
        {
            var hit = Pick(ray);
            if (missionTool == MissionTool.Erase)
            {
                if (hit != null && Thing(hit) is { } gone) { Erase(gone); Rebuild(); }
                return;
            }
            if (hit is { Part: Part.Zone or Part.Mine or Part.Obstacle } h)
            {
                missionPick = Thing(h);
                missionDrag = h.Part switch { Part.Zone => h.Index < 0 ? MissionDrag.ZoneSize : MissionDrag.Zone, Part.Mine => MissionDrag.Mine, _ => MissionDrag.Obstacle };
                Rebuild();
            }
            else if (hit is { Part: Part.Body, Unit: { } unit }) { tab = Tab.Tanks; Select(unit); Say($"{unit.Id} picked (Tanks tab)."); }
            else if (Ground(ray) is { } at)
            {
                var spot = Files.Array(at);
                switch (missionTool)
                {
                    case MissionTool.Zone:
                        var z = new Zone { Id = m.NewZoneId(), Center = spot };
                        z.Name = $"Zone {z.Id[1..]}";
                        m.Zones.Add(z); missionPick = z; missionDrag = MissionDrag.ZoneSize;
                        break;
                    case MissionTool.Mine: var mine = new Mine { Position = spot }; m.Mines.Add(mine); missionPick = mine; break;
                    case MissionTool.Hedgehog:
                    case MissionTool.Block:
                        var o = new Obstacle { Kind = missionTool == MissionTool.Block ? "block" : "hedgehog", Position = spot, Yaw = yaw };
                        m.Obstacles.Add(o); missionPick = o; missionDrag = MissionDrag.Obstacle;
                        break;
                    case MissionTool.ATGun:
                        var guns = ATGuns();
                        if (guns.Count == 0) { Say("No AT guns found (the game's own are in its Blueprints\\Vehicles folder)."); break; }
                        var gun = new BattleUnit { Id = file.NewId(), Team = atTeam, Blueprint = guns[atGun % guns.Count].Path, Position = spot, Yaw = yaw };
                        file.Units.Add(gun);
                        Say($"{gun.Id}: {guns[atGun % guns.Count].Name} for team {atTeam + 1}. It's a unit like the tanks: turn it or give it a target in the Tanks tab.");
                        break;
                    default: missionPick = null; break;
                }
                Rebuild();
            }
        }
        if (!mouse.leftButton.isPressed)
        {
            if (missionDrag != MissionDrag.None) { missionDrag = MissionDrag.None; Rebuild(); }
        }
        else if (missionDrag != MissionDrag.None && Ground(ray) is { } to)
        {
            switch (missionPick)
            {
                case Zone z when missionDrag == MissionDrag.ZoneSize: z.Radius = Math.Clamp((to - Files.Vector(z.Center)).Flat().magnitude, 5, 1000); break;
                case Zone z: z.Center = Files.Array(to); break;
                case Mine mine: mine.Position = Files.Array(to); break;
                case Obstacle o: o.Position = Files.Array(to); break;
            }
            MarksChanged();
        }
        if (marksDirty && Time.unscaledTime >= nextMarks) { marksDirty = false; nextMarks = Time.unscaledTime + 0.1f; Rebuild(); }
    }

    object? Thing(Hit h) => h.Part switch
    {
        Part.Zone => M.Zones.ElementAtOrDefault(h.Index < 0 ? -1 - h.Index : h.Index),
        Part.Mine => M.Mines.ElementAtOrDefault(h.Index),
        Part.Obstacle => M.Obstacles.ElementAtOrDefault(h.Index),
        _ => null,
    };

    void Erase(object thing)
    {
        var m = M;
        switch (thing)
        {
            case Zone z:
                m.Zones.Remove(z);
                m.Rules.RemoveAll(r => r.Zone == z.Id || r.Then.Zone == z.Id);
                Say($"{Mission.Name(z)} and the rules using it removed.");
                break;
            case Mine mine: m.Mines.Remove(mine); break;
            case Obstacle o: m.Obstacles.Remove(o); break;
        }
        if (missionPick == thing) missionPick = null;
    }

    void RemoveMissionPick() { if (missionPick != null) { Erase(missionPick); Rebuild(); } }

    void TurnObstacle(float by)
    {
        if (missionPick is Obstacle o) { o.Yaw = (o.Yaw + by + 360) % 360; Rebuild(); }
    }

    /// Mines spread over the picked zone, none closer than 6 m to another.
    void FillZone(Zone z, int count)
    {
        var c = Files.Vector(z.Center);
        var placed = new List<Vector3>();
        for (int tries = 0; placed.Count < count && tries < count * 40; tries++)
        {
            var r = UnityEngine.Random.insideUnitCircle * z.Radius;
            var at = c + new Vector3(r.x, 0, r.y);
            if (placed.Any(p => (p - at).Flat().magnitude < 6)) continue;
            placed.Add(at);
            M.Mines.Add(new Mine { Position = Files.Array(Mission.Ground(at)) });
        }
        Say($"{placed.Count} mines laid in {Mission.Name(z)}.");
        Rebuild();
    }

    // ---------- the panels ----------

    void MissionPanels()
    {
        var m = M;
        var box = Panel(new Rect(16, 56, 480, 17 * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w), $"Mission: {m.Rules.Count} rules, {m.Zones.Count} zones, {m.Mines.Count} mines, {m.Obstacles.Count} obstacles");
        y += Row;
        GUI.Label(Line(0, w), "Click a tool, then the ground. Without a tool: pick and drag.");
        y += Row;
        float t = (w - 5 * 4) / 6;
        void ToolButton(int i, MissionTool which, string text) =>
            Toggle(Line(i * (t + 4), t), missionTool == which, " " + text, () => missionTool = missionTool == which ? MissionTool.None : which);
        ToolButton(0, MissionTool.Zone, "Zone");
        ToolButton(1, MissionTool.Mine, "Mine");
        ToolButton(2, MissionTool.Hedgehog, "Hedgehog");
        ToolButton(3, MissionTool.Block, "Block");
        ToolButton(4, MissionTool.ATGun, "AT gun");
        ToolButton(5, MissionTool.Erase, "Erase");
        y += Row + 4;

        // The picked thing (or, with the AT gun tool, which AT gun and team it places).
        var guns = missionTool == MissionTool.ATGun ? ATGuns() : new();
        switch (missionTool == MissionTool.ATGun ? "gun" : missionPick)
        {
            case "gun":
                GUI.Label(Line(0, w), "AT gun: click the ground to place one (a unit like the tanks).");
                y += Row;
                if (guns.Count == 0) { GUI.Label(Line(0, w), "No AT guns found."); y += 3 * Row; break; }
                Button(Line(0, w - 124), Short(guns[atGun % guns.Count].Name, 40) + "  (click: next)", () => atGun = (atGun + 1) % guns.Count);
                Button(Line(w - 120, 120), atTeam == 0 ? "Team 1 (blue)" : "Team 2 (red)", () => atTeam = 1 - atTeam);
                y += Row;
                GUI.Label(Line(0, w), "The game's own AT guns are marked (base game); yours count if named ... AT.");
                y += 2 * Row;
                break;
            case Zone z:
                GUI.Label(Line(0, 60), "Zone:");
                TextButton(Line(60, w - 60), "zone", z.Name, "(click to name it)", v => z.Name = v);
                y += Row;
                Slider(Line(0, w), z.Radius, 5, 300, $"Size: {z.Radius:0} m across {z.Radius * 2:0} m", v => { z.Radius = MathF.Round(v); MarksChanged(); });
                y += Row;
                Button(Line(0, 34), "-", () => minefield = Math.Max(1, minefield - 4));
                Button(Line(38, w - 76 - 4), $"Lay a minefield here: {minefield} mines", () => FillZone(z, minefield));
                Button(Line(w - 34, 34), "+", () => minefield = Math.Min(200, minefield + 4));
                y += Row;
                Button(Line(0, w), "Remove this zone (and its rules)", () => { Erase(z); Rebuild(); });
                y += Row;
                break;
            case Mine mine:
                GUI.Label(Line(0, w), "AT mine (the game's own: set off by a vehicle on it)");
                y += Row;
                Slider(Line(0, w), mine.Power, 1000, 20000, $"Blast: {mine.Power:0} (the game's mines: 5000)", v => mine.Power = MathF.Round(v / 250) * 250);
                y += Row;
                Button(Line(0, w), "Remove this mine", () => { Erase(mine); Rebuild(); });
                y += 2 * Row;
                break;
            case Obstacle o:
                GUI.Label(Line(0, w), o.Kind == "block" ? "Concrete block (doesn't break)" : "Czech hedgehog (breaks like the game's, 30 000 N)");
                y += Row;
                float q = (w - 3 * 4) / 4;
                Button(Line(0, q), "-15°", () => TurnObstacle(-15));
                Button(Line(q + 4, q), "+15°", () => TurnObstacle(15));
                Button(Line(2 * (q + 4), q), o.Kind == "block" ? "Make hedgehog" : "Make block", () => { o.Kind = o.Kind == "block" ? "hedgehog" : "block"; Rebuild(); });
                Button(Line(3 * (q + 4), q), "Remove", () => { Erase(o); Rebuild(); });
                y += 3 * Row;
                break;
            default:
                GUI.Label(Line(0, w), "Nothing picked. Zones are the places rules talk about:");
                y += Row;
                GUI.Label(Line(0, w), "enter it, hold it, shell it, send tanks to it.");
                y += 3 * Row;
                break;
        }

        // The zones, to fly to.
        GUI.Label(Line(0, w), "Zones (click to fly there):");
        y += Row;
        for (int i = 0; i < Math.Min(m.Zones.Count, 5); i++)
        {
            var z = m.Zones[i];
            Button(Line(0, w), $"{z.Id}  {Short(z.Name, 40)}  ({z.Radius:0} m)", () => { missionPick = z; FlyTo(Files.Vector(z.Center), z.Radius * 3); Rebuild(); });
            y += Row;
        }
        y = box.y + box.height - Pad - 2 * Row;
        FileButtons(Line, w);
        RulesPanel();
    }

    /// The camera over `at`, looking down at an angle from `height` up.
    void FlyTo(Vector3 at, float height)
    {
        if (view == null) return;
        pitch = 60;
        height = Math.Clamp(height, 40, 900);
        var back = Quaternion.Euler(0, yaw, 0) * Vector3.back * (height / MathF.Tan(60 * Mathf.Deg2Rad));
        view.transform.SetPositionAndRotation(at + Vector3.up * height + back, Quaternion.Euler(pitch, yaw, 0));
    }

    /// Top view, Save, Load saved, Play, Leave: the bottom of every tab's main panel.
    void FileButtons(Func<float, float, Rect> line, float w)
    {
        float b = (w - 4 * 4) / 5;
        Button(line(0, b), "Top view", ToTopView);
        Button(line(b + 4, b), "Save", Save);
        Button(line(2 * (b + 4), b), "Load saved", LoadSaved);
        Button(line(3 * (b + 4), b), "Play", Play);
        Button(line(4 * (b + 4), b), "Leave", () => Leave());
    }

    static string Describe(string when) => when switch
    {
        "time" => "time passed", "destroyed" => "tank destroyed", "wipedOut" => "team wiped out", "losses" => "team lost",
        "teamEnters" => "team enters", "unitEnters" => "tank enters", "holds" => "team holds", _ => when,
    };

    static string DescribeAction(string what) => what switch
    {
        "message" => "message", "victory" => "VICTORY", "defeat" => "DEFEAT", "artillery" => "artillery on", "reserves" => "reserves arrive",
        "teamTo" => "send team to", "unitTo" => "send tank to", _ => what,
    };

    static string Next(string[] all, string now) => all[(Array.IndexOf(all, now) + 1) % all.Length];

    /// The rules, two lines each: "When <condition>" and "then <action>", every part a button (click to change it).
    void RulesPanel()
    {
        var m = M;
        float width = Math.Min(640, Screen.width - 16 - 512);
        var box = Panel(new Rect(Screen.width - 16 - width, 56, width, Math.Min(Screen.height - 120, 20 * Row + 2 * Pad)));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float wide) => new(x + left, y, wide, Row - 3);
        GUI.Label(Line(0, w - 130), $"Rules ({m.Rules.Count}): when this happens, do that (once)");
        Button(Line(w - 125, 125), "+ Add rule", () =>
        {
            m.Rules.Add(new Rule { When = "time", Seconds = 10, Then = new RuleAction { Do = "message", Text = "The battle begins." } });
            rulesTop = Math.Max(0, m.Rules.Count - RulesShown(box));
        });
        y += Row + 4;
        int shown = RulesShown(box);
        scrollers.Add((box, by => rulesTop = Math.Clamp(rulesTop - by, 0, Math.Max(0, m.Rules.Count - shown))));
        var units = file.Units.Select(u => u.Id).ToArray();
        var zones = m.Zones.Select(z => z.Id).ToArray();
        string ZoneName(string? id) => m.Zones.FirstOrDefault(z => z.Id == id) is { } z ? Short(Mission.Name(z), 14) : "pick zone";
        string Cycle(string[] all, string? now) => all.Length == 0 ? "" : all[(Array.IndexOf(all, now ?? "") + 1) % all.Length];
        for (int i = rulesTop; i < Math.Min(m.Rules.Count, rulesTop + shown); i++)
        {
            var r = m.Rules[i];
            var a = r.Then;
            float c = 0;
            Rect Next2(float wide) { var rect = Line(c, wide); c += wide + 4; return rect; }
            GUI.Label(Next2(70), $"{i + 1}. When");
            Button(Next2(120), Describe(r.When), () => { r.When = Next(Mission.Conditions, r.When); if (r.When == "time" && r.Seconds < 1) r.Seconds = 30; });
            void Team(Action<int> set, int now) => Button(Next2(70), $"Team {now + 1}", () => set(1 - now));
            void Seconds(float now, Action<float> set) { Button(Next2(26), "-", () => set(Math.Max(0, now - (now > 60 ? 10 : 5)))); GUI.Label(Next2(56), $"{now:0} s"); Button(Next2(26), "+", () => set(now + (now >= 60 ? 10 : 5))); }
            switch (r.When)
            {
                case "time": Seconds(r.Seconds, v => r.Seconds = v); break;
                case "destroyed": Button(Next2(110), r.Unit ?? "pick tank", () => r.Unit = Cycle(units, r.Unit)); break;
                case "wipedOut": Team(v => r.Team = v, r.Team); break;
                case "losses":
                    Team(v => r.Team = v, r.Team);
                    Button(Next2(26), "-", () => r.Count = Math.Max(1, r.Count - 1)); GUI.Label(Next2(56), $"{r.Count} tanks"); Button(Next2(26), "+", () => r.Count++);
                    break;
                case "teamEnters": Team(v => r.Team = v, r.Team); Button(Next2(110), ZoneName(r.Zone), () => r.Zone = Cycle(zones, r.Zone)); break;
                case "unitEnters": Button(Next2(80), r.Unit ?? "pick tank", () => r.Unit = Cycle(units, r.Unit)); Button(Next2(110), ZoneName(r.Zone), () => r.Zone = Cycle(zones, r.Zone)); break;
                case "holds": Team(v => r.Team = v, r.Team); Button(Next2(100), ZoneName(r.Zone), () => r.Zone = Cycle(zones, r.Zone)); Seconds(r.Seconds, v => r.Seconds = v); break;
            }
            Button(new Rect(x + w - 26, y, 26, Row - 3), "x", () => m.Rules.Remove(r));
            y += Row;
            c = 0;
            GUI.Label(Next2(50), "    then");
            Button(Next2(120), DescribeAction(a.Do), () => a.Do = Next(Mission.Actions, a.Do));
            switch (a.Do)
            {
                case "message":
                case "victory":
                case "defeat":
                    TextButton(new Rect(x + c, y, w - c, Row - 3), $"rule{i}", a.Text, a.Do == "message" ? "(click to type the message)" : "(click to type a line under the banner)", v => a.Text = v);
                    break;
                case "artillery":
                    Button(Next2(90), ZoneName(r.ActionZone(m)?.Id), () => a.Zone = Cycle(zones, r.ActionZone(m)?.Id));
                    Button(Next2(26), "-", () => a.Shells = Math.Max(1, a.Shells - 4)); GUI.Label(Next2(64), $"{a.Shells} shells"); Button(Next2(26), "+", () => a.Shells += 4);
                    Button(Next2(66), $"{Mission.Calibre(a.Power)} mm", () => a.Power = Mission.PowerOf(Mission.Calibres.FirstOrDefault(k => k > Mission.Calibre(a.Power), Mission.Calibres[0])));
                    Button(Next2(26), "-", () => a.Seconds = Math.Max(1, a.Seconds - 5)); GUI.Label(Next2(40), $"{a.Seconds:0} s"); Button(Next2(26), "+", () => a.Seconds += 5);
                    break;
                case "reserves": Team(v => a.Team = v, a.Team); break;
                case "teamTo": Team(v => a.Team = v, a.Team); Button(Next2(110), ZoneName(r.ActionZone(m)?.Id), () => a.Zone = Cycle(zones, r.ActionZone(m)?.Id)); break;
                case "unitTo": Button(Next2(80), a.Unit ?? "pick tank", () => a.Unit = Cycle(units, a.Unit)); Button(Next2(110), ZoneName(r.ActionZone(m)?.Id), () => a.Zone = Cycle(zones, r.ActionZone(m)?.Id)); break;
            }
            y += Row + 6;
        }
        if (m.Rules.Count > shown) GUI.Label(new Rect(x, box.y + box.height - Row - Pad, w, Row - 3), $"Rules {rulesTop + 1}-{Math.Min(m.Rules.Count, rulesTop + shown)} of {m.Rules.Count} (the wheel scrolls)");
        else if (m.Rules.Count == 0) GUI.Label(Line(0, w), "No rules: the battle is the game's own (fought to the last tank).");
    }

    static int RulesShown(Rect box) => Math.Max(1, (int)((box.height - 2 * Pad - 2 * Row) / (2 * Row + 6)));

    // ---------- in the battle ----------

    /// The mission's messages, and its end banner (the battle paused) with what to do next.
    void MissionOverlay()
    {
        if (!commanding) buttons.Clear();
        float y = 56;
        foreach (var (text, _) in Mission.Messages.ToList())
        {
            GUI.Box(new Rect((Screen.width - 640) / 2f, y, 640, 30), text);
            y += 34;
        }
        if (Mission.Banner is not { } banner) return;
        ShowCursor();
        var box = new Rect((Screen.width - 560) / 2f, Screen.height * 0.3f, 560, 190);
        GUI.Box(box, "");
        GUI.Box(box, ""); // twice, darker: GUI.color (for a tint) is stripped in this build
        GUI.Box(new Rect(box.x + 20, box.y + 20, box.width - 40, 60), banner.Won ? $"\n***   {banner.Title}   ***" : $"\n{banner.Title}");
        GUI.Label(new Rect(box.x + 24, box.y + 92, box.width - 48, 40), banner.Text);
        float b = (box.width - 40 - 8) / 3;
        Button(new Rect(box.x + 20, box.y + 140, b, 30), "Keep playing", () => Mission.Dismiss());
        Button(new Rect(box.x + 24 + b, box.y + 140, b, 30), "Play again", () =>
        {
            Mission.Dismiss();
            if (Battle.Mode is { } mode && Battle.Playing is { } again) Say(Battle.Play(mode, again) ?? "Playing again.");
        });
        Button(new Rect(box.x + 28 + 2 * b, box.y + 140, b, 30), "Edit (F9)", () =>
        {
            Mission.Dismiss();
            if (Battle.Mode is { } mode) Enter(mode);
        });
    }
}
