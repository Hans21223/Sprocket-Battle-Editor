using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// The editor's Cinematic tab and the cinematic playing in the battle. Cameras are made of keys (fly the editor's
/// camera where you want it, Add key at the time cursor); cuts say which camera is shown from when; a tank's keys
/// pose its turret and gun, drive it and fire it at their times (force control). Preview flies the cameras over the
/// frozen editor; Play starts the battle and the cinematic with it, with nothing on screen but the shot (Esc stops it).
public sealed partial class BattleEditor
{
    int cam;                 // the camera picked
    int camKey = -1;         // its key picked
    int tankKey = -1;        // the picked tank's key picked
    float scrub;             // the time cursor, battle seconds
    bool previewing;
    float previewAt;
    int previewFrame;

    CinemaData C => file.Cinema ??= new CinemaData();
    CameraTrack? Cam => cam >= 0 && cam < C.Cameras.Count ? C.Cameras[cam] : null;

    /// Where a tank is for a following camera, in the editor: its marker.
    (Vector3 At, float Yaw)? MarkerAnchor(string id) => C.Replay != null ? ReplayAnchor(id, replayTime) :
        file.Units.FirstOrDefault(u => u.Id == id) is { } u ? (Files.Vector(u.Position), u.Yaw) : null;
    (Vector3 At, float Yaw)? EditingAnchor(string id, float at) => C.Replay != null ? ReplayAnchor(id, at) : MarkerAnchor(id);

    static (Vector3 At, float Yaw)? TankAnchor(string id) =>
        Battle.TankOf(id) is { } t ? (t.transform.position, t.transform.eulerAngles.y) : null;

    void CinemaMarks()
    {
        // The picked tank's keys: where it drives (yellow), aims (orange) and shoots (red), from where it starts.
        if (selected != null && C.Tanks.FirstOrDefault(t => t.Unit == selected.Id) is { } keyed)
        {
            var from = Files.Vector(selected.Position);
            foreach (var k in keyed.Keys)
            {
                if (k.DriveTo is { } to)
                {
                    marks.Add(Line(from, Files.Vector(to), 0.5f, 0.4f, Color.yellow));
                    Mark(PrimitiveType.Cylinder, Files.Vector(to) + Vector3.up * 0.6f, new Vector3(1.4f, 0.6f, 1.4f), Color.yellow, null);
                }
                if (k.AimPoint is { } aim) marks.Add(Line(from, Files.Vector(aim), 2.5f, 0.15f, new Color(1f, 0.6f, 0.2f)));
                if ((k.ShootAt ?? k.AimUnit) is { } id && file.Units.FirstOrDefault(u => u.Id == id) is { } target)
                    marks.Add(Line(from, Files.Vector(target.Position), 2.8f, k.ShootAt != null ? 0.35f : 0.15f, k.ShootAt != null ? new Color(1f, 0.15f, 0.1f) : new Color(1f, 0.6f, 0.2f)));
            }
        }
        if (Cam is not { } track) return;
        // The camera's path through its keys, and a camera at each key (yellow: picked).
        var path = new List<Vector3>();
        if (track.Keys.Count > 1)
        {
            float from = track.Keys[0].Time, to = track.Keys[^1].Time;
            for (int i = 0; i <= 80; i++)
            {
                float at = from + (to - from) * i / 80;
                if (Cinema.CameraAt(track, at, id => EditingAnchor(id, at)) is { } p) path.Add(p.Position);
            }
        }
        for (int i = 0; i + 1 < path.Count; i++) marks.Add(Line(path[i], path[i + 1], 0, 0.25f, new Color(0.4f, 0.9f, 1f)));
        for (int camera = 0; camera < C.Cameras.Count; camera++)
        for (int i = 0; i < C.Cameras[camera].Keys.Count; i++)
            if (Cinema.CameraAt(C.Cameras[camera], C.Cameras[camera].Keys[i].Time,
                id => EditingAnchor(id, C.Cameras[camera].Keys[i].Time)) is { } p)
            {
                var o = GameObject.CreatePrimitive(PrimitiveType.Cube);
                o.name = "Battle Editor camera key";
                o.transform.SetPositionAndRotation(p.Position, p.Rotation);
                o.transform.localScale = new Vector3(1.4f, 1f, 2f);
                Colour(o, camera == cam && i == camKey ? Color.yellow : new Color(0.4f, 0.9f, 1f));
                pickable[o.Pointer] = new Hit(null, Part.CamKey, i);
                cameraWidgets[o.Pointer] = (camera, i, o);
                marks.Add(o);
                var lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                lens.transform.SetParent(o.transform, false);
                lens.transform.localPosition = new Vector3(0, 0, 0.7f);
                lens.transform.localRotation = Quaternion.Euler(90, 0, 0);
                lens.transform.localScale = new Vector3(0.6f, 0.3f, 0.6f);
                NoCollider(lens);
                Colour(lens, Color.white);
            }
        foreach (var mark in marks)
            if (mark != null) foreach (var part in mark.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = CameraGizmoLayer;
        if (view != null)
        {
            cameraViewMask ??= view.cullingMask;
            view.cullingMask |= 1 << CameraGizmoLayer;
        }
    }

    /// Mouse on the map in the Cinematic tab: select and drag a camera key without moving the editing view; a tank
    /// picks it for its keys.
    // A tank key's point or target, picked with the next click on the map.
    enum KeyPick { None, AimPoint, DrivePoint, Tank }
    KeyPick keyPick;

    /// The other team's first tank (a target to start with), or null.
    string? Enemy(BattleUnit unit) => file.Units.FirstOrDefault(u => u.Team != unit.Team)?.Id;

    TankKey? PickedTankKey() =>
        selected != null && C.Tanks.FirstOrDefault(t => t.Unit == selected.Id) is { } track && tankKey >= 0 && tankKey < track.Keys.Count ? track.Keys[tankKey] : null;

    void CinemaMap(Mouse? mouse, bool overPanel)
    {
        if (mouse == null || view == null || !mouse.leftButton.wasPressedThisFrame || overPanel) return;
        var ray = view.ScreenPointToRay(mouse.position.ReadValue());
        if (keyPick != KeyPick.None && PickedTankKey() is { } key)
        {
            var what = keyPick;
            keyPick = KeyPick.None;
            if (what == KeyPick.Tank)
            {
                if (Pick(ray) is { Part: Part.Body or Part.Tip, Unit: { } target } && target != selected)
                {
                    if (key.ShootAt != null) key.ShootAt = target.Id; else key.AimUnit = target.Id;
                    Say($"{selected!.Id} {(key.ShootAt != null ? "shoots at" : "aims at")} {target.Id} from {key.Time:0.0} s.");
                }
                else Say("No tank there: click Target again, then a tank.");
            }
            else if (Ground(ray) is { } at)
            {
                if (what == KeyPick.AimPoint) key.AimPoint = Files.Array(at + Vector3.up * 1.5f); else key.DriveTo = Files.Array(at);
                Say(what == KeyPick.AimPoint ? $"{selected!.Id} aims there from {key.Time:0.0} s." : $"{selected!.Id} drives there from {key.Time:0.0} s.");
            }
            Rebuild();
            return;
        }
        if (PickCamera(ray) is { } camera) { BeginCameraMove(camera.Camera, camera.Key, mouse); return; }
        var hit = Pick(ray);
        if (hit is { Part: Part.Body or Part.Tip, Unit: { } unit }) { selected = unit; tankKey = -1; Rebuild(); }
    }

    /// The editor's view put where the picked camera is at `t` (the one being edited), or for `shown`, the camera the
    /// cuts show then (as it will play).
    void ViewAt(float t, bool shown = false)
    {
        ReplayAt(t, sync: !previewing);
        if (view == null || C.Cameras.Count == 0) return;
        var track = !shown && Cam is { Keys.Count: > 0 } picked ? picked : C.Cameras[Cinema.Shown(C, t)];
        if (Cinema.CameraAt(track, t, MarkerAnchor) is not { } p) return;
        view.transform.SetPositionAndRotation(p.Position, p.Rotation);
        view.fieldOfView = p.Fov;
        var e = p.Rotation.eulerAngles;
        pitch = e.x > 180 ? e.x - 360 : e.x; yaw = e.y;
    }

    void AddCamKey()
    {
        if (view == null) return;
        if (Cam is not { } track) { C.Cameras.Add(track = new CameraTrack { Name = $"Camera {C.Cameras.Count + 1}" }); cam = C.Cameras.Count - 1; }
        var key = Cinema.Key(scrub, view.transform.position, view.transform.rotation, view.fieldOfView, track, MarkerAnchor);
        int same = track.Keys.FindIndex(k => Math.Abs(k.Time - scrub) < 0.05f);
        if (same >= 0) track.Keys[same] = key; else track.Keys.Add(key);
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        camKey = track.Keys.IndexOf(key);
        Say($"{track.Name}: key at {scrub:0.0} s ({track.Keys.Count} keys).");
        Rebuild();
    }

    // ---------- preview over the frozen editor ----------

    void StartPreview()
    {
        if (C.Cameras.All(c => c.Keys.Count == 0)) { Say("Add camera keys first."); return; }
        previewing = true; previewAt = scrub < C.Length ? scrub : 0; previewFrame = Time.frameCount;
        StartReplayAudio(previewAt);
        foreach (var o in marks) if (o != null) o.SetActive(false);
    }

    void Preview(Keyboard keys)
    {
        previewAt += Time.unscaledDeltaTime * Math.Max(0.05f, C.Speed);
        ViewAt(previewAt, shown: true);
        ReplayAudioAt(previewAt);
        bool click = Time.frameCount > previewFrame && Mouse.current is { } m && m.leftButton.wasPressedThisFrame; // not the Preview click itself
        if (previewAt > C.Length + 0.5f || keys.escapeKey.wasPressedThisFrame || click)
        {
            StopPreview();
        }
    }

    void StopPreview()
    {
        previewing = false;
        StopReplayAudio();
        scrub = Math.Min(previewAt, C.Length);
        Rebuild();
        Say("Preview stopped. Continue editing cameras or the timeline.");
    }

    void PreviewPanel()
    {
        GUI.Box(new Rect(16, Screen.height - 46, 470, 30), $"Preview {previewAt:0.0} / {C.Length:0.0} s   ({Plugin.EditorKey}, Esc or click stops)");
    }

    // ---------- the panels ----------

    void CinemaPanels()
    {
        var c = C;
        // Keep the timeline usable even if an optional preview/detail panel fails to render.
        Timeline();
        var box = Panel(new Rect(16, 56, 480, 17 * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w), c.Replay != null
            ? $"Replay: {c.Cameras.Count} cameras, {c.Cuts.Count} cuts, {c.Length:0.0} s"
            : $"Battle cinematic: {c.Cameras.Count} cameras, {c.Cuts.Count} cuts, {c.Length:0.0} s");
        y += Row;
        float replayHalf = (w - 4) / 2;
        if (c.Replay == null)
        {
            Button(Line(0, replayHalf), "Record battle", RecordReplay);
            Button(Line(replayHalf + 4, replayHalf), "Replays — Edit", ListReplays);
        }
        else
        {
            Button(Line(0, replayHalf), "Open recording", ListReplays);
            Button(Line(replayHalf + 4, replayHalf), "Load saved replay edits", LoadSaved);
        }
        y += Row;
        float q = (w - 4 * 4) / 5;
        Button(Line(0, q), "-1 s", () => { scrub = Math.Max(0, scrub - 1); ScrubCameraView(scrub); });
        Button(Line(q + 4, q), "-0.1 s", () => { scrub = Math.Max(0, scrub - 0.1f); ScrubCameraView(scrub); });
        Button(Line(2 * (q + 4), q), "+0.1 s", () => { scrub += 0.1f; ScrubCameraView(scrub); });
        Button(Line(3 * (q + 4), q), "+1 s", () => { scrub += 1; ScrubCameraView(scrub); });
        Button(Line(4 * (q + 4), q), "Preview", StartPreview);
        y += Row + 4;

        // Cameras (their keys are on the timeline below).
        GUI.Label(Line(0, w - 210), "Cameras (click to pick):");
        Button(Line(w - 205, 100), "+ New", AddCinemaCamera);
        Button(Line(w - 100, 100), "Delete", () =>
        {
            if (Cam is not { } gone) return;
            c.Cameras.Remove(gone);
            c.Cuts.RemoveAll(k => k.Camera == cam);
            foreach (var k in c.Cuts) if (k.Camera > cam) k.Camera--;
            cam = Math.Max(0, cam - 1); camKey = -1; Rebuild();
        });
        y += Row;
        for (int i = 0; i < Math.Min(c.Cameras.Count, 4); i++)
        {
            int pick = i;
            var t = c.Cameras[i];
            Toggle(Line(0, w), i == cam, $" {t.Name}: {t.Keys.Count} keys{(t.Follow != null ? $", follows {t.Follow}" : "")}{(t.LookAt != null ? $", looks at {t.LookAt}" : "")}", () => { cam = pick; camKey = -1; Rebuild(); });
            y += Row;
        }
        if (c.Cameras.Count > 4) { GUI.Label(Line(0, w), $"(and {c.Cameras.Count - 4} more on the timeline)"); y += Row; }

        // The picked camera.
        if (Cam is { } track)
        {
            float h = (w - 4) / 2;
            Button(Line(0, h), $"Key {track.Name} here at {scrub:0.0} s", AddCamKey);
            string? NextUnit(string? now)
            {
                var ids = file.Units.Select(u => u.Id).ToList();
                int at = now == null ? -1 : ids.IndexOf(now);
                return at + 1 < ids.Count ? ids[at + 1] : null;
            }
            Button(Line(h + 4, h), track.Follow == null ? "Follows: no tank" : $"Follows: {track.Follow}", () =>
            {
                string? was = track.Follow, next = NextUnit(track.Follow);
                Cinema.Refollow(track, next, EditingAnchor); // Each key stays where it was seen at its own replay time.
                if (next != null && (track.LookAt == null || track.LookAt == was)) track.LookAt = next;
                if (next == null && track.LookAt == was) track.LookAt = null;
                Say(next == null ? $"{track.Name} stays where its keys put it." : $"{track.Name} follows {next} and looks at {track.LookAt ?? "where its keys point"}. Its keys move with {next}.");
                Rebuild();
            });
            y += Row;
            Button(Line(0, w), track.LookAt == null ? "Looks: where its keys point   (click: at a tank)" : $"Looks at: {track.LookAt}   (click: next)", () =>
            {
                track.LookAt = NextUnit(track.LookAt);
                Say(track.LookAt == null ? $"{track.Name} points where its keys point." : $"{track.Name} always points at {track.LookAt}.");
                ScrubCameraView(scrub);
                Rebuild();
            });
            y += Row;
            Button(Line(0, q), "FOV -5", () => CameraFov(-5));
            Button(Line(q + 4, q), "FOV +5", () => CameraFov(5));
            GUI.Label(Line(2 * (q + 4), q), $" {CameraFovValue:0}°");
            Button(Line(3 * (q + 4), 2 * q + 4), $"Cut to it at {scrub:0.0} s", AddCut);
            y += Row;
            float quarter = (w - 12) / 4;
            Button(Line(0, quarter), "Height -1 m", () => CameraHeight(-1));
            Button(Line(quarter + 4, quarter), "Height +1 m", () => CameraHeight(1));
            Button(Line(2 * (quarter + 4), quarter), "Use view", CameraUseView);
            Toggle(Line(3 * (quarter + 4), quarter), cameraPovEnabled, " Camera view", () => cameraPovEnabled = !cameraPovEnabled);
            y += Row;
            float sixth = (w - 20) / 6;
            Button(Line(0, sixth), "Yaw -5°", () => CameraRotate(-5, 0, 0));
            Button(Line(sixth + 4, sixth), "Yaw +5°", () => CameraRotate(5, 0, 0));
            Button(Line(2 * (sixth + 4), sixth), "Pitch -5°", () => CameraRotate(0, -5, 0));
            Button(Line(3 * (sixth + 4), sixth), "Pitch +5°", () => CameraRotate(0, 5, 0));
            Button(Line(4 * (sixth + 4), sixth), "Roll -5°", () => CameraRotate(0, 0, -5));
            Button(Line(5 * (sixth + 4), sixth), "Roll +5°", () => CameraRotate(0, 0, 5));
            y += Row;
            GUI.Label(Line(0, w), "Drag: move. Shift-drag: height. Ctrl-drag: rotate.");
            y += Row;
        }

        y = box.y + box.height - Pad - 2 * Row;
        float half = (w - 4) / 2;
        if (c.Replay == null)
        {
            Toggle(Line(0, half), c.PlayOnStart, " Plays when the battle starts", () => c.PlayOnStart = !c.PlayOnStart);
            Button(Line(half + 4, half), $"Battle speed: {c.Speed:0.##}x", () => c.Speed = c.Speed >= 1 ? 0.25f : c.Speed * 2);
            y += Row;
            FileButtons(Line, w);
        }
        else
        {
            Button(Line(0, half), "Top view", ToTopView);
            Button(Line(half + 4, half), $"Replay speed: {c.Speed:0.##}x", () => c.Speed = c.Speed >= 1 ? 0.25f : c.Speed * 2);
            y += Row;
            float third = (w - 8) / 3;
            Button(Line(0, third), "Save replay edits", Save);
            Button(Line(third + 4, third), "Play replay", StartPreview);
            Button(Line(2 * (third + 4), third), CanReturnFromReplay ? "Back to battle" : "Main menu",
                () => { if (CanReturnFromReplay) BackFromReplay(); else ReturnToMainMenu(); });
        }
        Guard.Run("camera view panel", CameraPovPanel);
        if (c.Replay != null) Guard.Run("replay details panel", ReplayInfoPanel);
        else Guard.Run("tank keys panel", TankKeysPanel);
    }

    /// The picked tank's keys: its turret and gun pose, drive and shots at times.
    void TankKeysPanel()
    {
        var unit = selected;
        var box = Panel(new Rect(Screen.width - 16 - 400, CameraDetailsY, 400, (unit == null ? 3 : 14) * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        if (unit == null)
        {
            GUI.Label(Line(0, w), "Tank keys: click a tank on the map to pose it,");
            y += Row;
            GUI.Label(Line(0, w), "drive it and fire it at times (force control).");
            return;
        }
        var track = C.Tanks.FirstOrDefault(t => t.Unit == unit.Id);
        GUI.Label(Line(0, w), $"Tank {unit.Id} keys: {track?.Keys.Count ?? 0}");
        y += Row;
        Button(Line(0, w), $"Add key at {scrub:0.0} s", () => AddTankKey(unit));
        y += Row;
        if (track == null || track.Keys.Count == 0) { GUI.Label(Line(0, w), "No keys: its AI does as it likes."); return; }
        GUI.Label(Line(0, w), string.Join("  ", track.Keys.Select((k, i) => (i == tankKey ? "[" : "") + $"{k.Time:0.0}{(k.Fire ? "!" : "")}" + (i == tankKey ? "]" : ""))));
        y += Row;
        float q = (w - 3 * 4) / 4;
        Button(Line(0, q), "< key", () => tankKey = Math.Max(0, tankKey - 1));
        Button(Line(q + 4, q), "key >", () => tankKey = Math.Min(track.Keys.Count - 1, tankKey + 1));
        if (tankKey < 0 || tankKey >= track.Keys.Count) { y += Row; GUI.Label(Line(0, w), "Pick a key (< key >) to change it."); return; }
        var key = track.Keys[tankKey];
        Button(Line(2 * (q + 4), q), "Time here", () => { key.Time = scrub; track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time)); tankKey = track.Keys.IndexOf(key); });
        Button(Line(3 * (q + 4), q), "Delete", () => { track.Keys.Remove(key); tankKey = -1; Rebuild(); });
        y += Row + 4;
        // Aim: none (its AI), angles, a point, a tank, or a tank to shoot at. Click to change.
        string aimMode = key.ShootAt != null ? "shoot" : key.AimUnit != null ? "tank" : key.AimPoint != null ? "point" : key.Aims ? "angles" : "none";
        string AimText(string m) => m switch
        {
            "angles" => "Aim: turret and gun angles", "point" => "Aim at: a point", "tank" => "Aim at: a tank",
            "shoot" => "Shoot at: a tank", _ => "Aim: its AI's",
        };
        Button(Line(0, w), AimText(aimMode) + "   (click: next)", () =>
        {
            var next = aimMode switch { "none" => "angles", "angles" => "point", "point" => "tank", "tank" => "shoot", _ => "none" };
            key.ClearAim();
            switch (next)
            {
                case "angles": key.Turret = 0; key.Gun = 0; break;
                case "point": key.AimPoint = Files.Array(Files.Vector(unit.Position) + Quaternion.Euler(0, unit.Yaw, 0) * Vector3.forward * 50); break;
                case "tank": case "shoot": key.AimUnit = next == "tank" ? Enemy(unit) : null; key.ShootAt = next == "shoot" ? Enemy(unit) : null; break;
            }
            Rebuild();
        });
        y += Row;
        switch (aimMode)
        {
            case "angles":
                Slider(Line(0, w), key.Turret ?? 0, -180, 180, $"Turret: {key.Turret ?? 0:0}° from the hull's front", v => key.Turret = MathF.Round(v));
                y += Row;
                Slider(Line(0, w), key.Gun ?? 0, -20, 45, $"Gun: {key.Gun ?? 0:0}° up", v => key.Gun = MathF.Round(v));
                y += Row;
                break;
            case "point":
                Button(Line(0, w), keyPick == KeyPick.AimPoint ? "Click the map..." : "Pick the point on the map", () => keyPick = KeyPick.AimPoint);
                y += Row;
                break;
            case "tank":
            case "shoot":
                Button(Line(0, w), keyPick == KeyPick.Tank ? "Click a tank on the map..." : $"Target: {(key.ShootAt ?? key.AimUnit) ?? "none"}   (click, then a tank)", () => keyPick = KeyPick.Tank);
                y += Row;
                break;
        }
        if (aimMode != "none")
        {
            GUI.Label(Line(0, w), TurretTurn(unit, track, key));
            y += Row;
        }
        // Drive: none (its AI), throttle and steering, or a point its AI drives to.
        string driveMode = key.DriveTo != null ? "to" : key.Drives ? "manual" : "none";
        Button(Line(0, w), (driveMode switch { "to" => "Drive to: a point", "manual" => "Drive: throttle and steering", _ => "Drive: its AI's" }) + "   (click: next)", () =>
        {
            key.ClearDrive();
            switch (driveMode)
            {
                case "none": key.Throttle = 0; key.Steer = 0; break;
                case "manual": key.DriveTo = Files.Array(Files.Vector(unit.Position) + Quaternion.Euler(0, unit.Yaw, 0) * Vector3.forward * 60); break;
            }
            Rebuild();
        });
        y += Row;
        if (driveMode == "manual")
        {
            Slider(Line(0, w), key.Throttle ?? 0, -1, 1, $"Throttle: {key.Throttle ?? 0:0.00}", v => key.Throttle = MathF.Round(v * 20) / 20);
            y += Row;
            Slider(Line(0, w), key.Steer ?? 0, -1, 1, $"Steering: {key.Steer ?? 0:0.00}", v => key.Steer = MathF.Round(v * 20) / 20);
            y += Row;
        }
        else if (driveMode == "to")
        {
            Button(Line(0, w), keyPick == KeyPick.DrivePoint ? "Click the map..." : "Pick where it drives on the map", () => keyPick = KeyPick.DrivePoint);
            y += Row;
            GUI.Label(Line(0, w), Arrival(unit, track, key));
            y += Row;
        }
        Toggle(Line(0, w), key.Fire, " Fire at this key", () => key.Fire = !key.Fire);
    }

    /// How long the turret takes to come round to a key's aim (Travel's turret timing).
    string TurretTurn(BattleUnit unit, TankTrack track, TankKey key)
    {
        if (Cinema.AimTurn(unit, track, key, id => file.Units.FirstOrDefault(u => u.Id == id)) is not { } degrees) return "Turret: (pick its target)";
        var (state, rate) = Travel.TurretState(unit.Blueprint);
        return state switch
        {
            "known" => $"Turret turns {degrees:0}°: about {Travel.TurnSeconds(unit.Blueprint, degrees):0.0} s (top {rate:0}°/s)",
            "none" => $"Turns {degrees:0}°: no turret, so the hull has to turn",
            _ => $"Turret turns {degrees:0}°: play the battle once to time it",
        };
    }

    /// When a "drive to" key gets the tank there (Travel's estimate).
    static string Arrival(BattleUnit unit, TankTrack track, TankKey key)
    {
        var leg = Cinema.Legs(unit, track)[key];
        string far = $"{leg.Metres:0} m";
        if (leg.Arrives is not { } at)
            return Travel.State(unit.Blueprint) == "working" ? $"{far}: working out how fast it drives..." : $"{far}: play the battle once to time this design";
        return at <= leg.Ends
            ? $"{far}: there at about {at:0.0} s ({at - key.Time:0.0} s, top {Travel.TopSpeed(unit.Blueprint) * 3.6f:0} km/h" +
              (Travel.DrivesMeasured(unit.Blueprint) is > 0 and var n ? $", from {n} real drives)" : ")")
            : $"{far}: not there by the next key at {leg.Ends:0.0} s (needs {at:0.0} s)";
    }

    // ---------- playing in the battle ----------

    bool cinemaPlaying;
    float cinemaStart, cinemaTimeBefore = 1;
    CinemaData? playingCinema;

    /// The battle's tanks are placed (Battle.PlaceSpawned): its cinematic starts if it plays on start.
    internal static void Started(BattleFile battle, Dictionary<string, VehicleBehaviour> tanks)
    {
        Puppet.ReleaseAll();
        instance?.AllowPlayReturn(battle);
        if (instance?.pendingReplay is { } requested && ReferenceEquals(requested, battle))
        { instance.BeginReplayCapture(battle, tanks); return; }
        if (instance == null || battle.Cinema is not { PlayOnStart: true } cinema || cinema.Cameras.All(c => c.Keys.Count == 0)) return;
        instance.StartCinema(cinema);
    }

    void StartCinema(CinemaData cinema)
    {
        if (editing || commanding) return;
        playingCinema = cinema;
        cinemaPlaying = true;
        cinemaStart = Time.time;
        cinemaTimeBefore = Time.timeScale;
        Time.timeScale = Math.Clamp(cinema.Speed, 0.05f, 4);
        Cinema.Reset();
        OpenView(cinematic: true);
        Trace.Write($"cinematic: playing {cinema.Cameras.Count} cameras, {cinema.Length:0.0} s at {cinema.Speed:0.##}x");
    }

    void StopCinema(string why)
    {
        if (!cinemaPlaying) return;
        cinemaPlaying = false;
        playingCinema = null;
        CloseView();
        Time.timeScale = cinemaTimeBefore > 0 ? cinemaTimeBefore : 1;
        Puppet.ReleaseAll();
        Trace.Write($"cinematic: {why}");
        Say($"Cinematic {why}. {Plugin.CommandKey} opens commands.");
    }

    void CinemaTick()
    {
        if (!cinemaPlaying || playingCinema is not { } c) return;
        if (Battle.Mode == null || view == null) { StopCinema("ended with the battle"); return; }
        float t = Time.time - cinemaStart;
        GameInputOff();
        var track = c.Cameras[Cinema.Shown(c, t)];
        if (Cinema.CameraAt(track, t, TankAnchor) is { } p)
        {
            view.transform.SetPositionAndRotation(p.Position, p.Rotation);
            view.fieldOfView = p.Fov;
        }
        Cinema.Tanks(c, t, Battle.TankOf);
        if (t > c.Length + 1) StopCinema("finished");
    }
}
