using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// The Cinematic tab's timeline along the bottom of the screen, as in video editing software: a time ruler with the
/// playhead (the time cursor), the cuts (which camera is shown when), a track of keys for each camera and each keyed
/// tank. Click or drag on the ruler or a track to move the playhead (the view follows the picked camera), drag a key to
/// retime it, drag a cut's edge to move it, click a track's name to pick it, + adds a key (or a cut) at the playhead.
/// The wheel zooms, Shift + wheel scrolls through time. Drawn with boxes and labels only (the game strips the rest).
public sealed partial class BattleEditor
{
    float tlStart, tlSpan = 30;                   // the seconds the timeline shows
    float tlX0, tlX1;                             // where its time runs on screen, as last drawn
    Rect tlArea;
    // What a press on the timeline does (as last drawn): areas from the screen's top left, and the drag that follows.
    readonly List<(Rect Area, Func<Action<float>?> Press)> tlHits = new();
    Action<float>? tlDrag;
    Action? tlDone;

    const float TrackHeight = 24, Ruler = 24, Header = 170;

    float TimeAt(float x) => tlStart + (x - tlX0) / Math.Max(1, tlX1 - tlX0) * tlSpan;
    float XAt(float t) => tlX0 + (t - tlStart) / tlSpan * (tlX1 - tlX0);
    static float Snap(float t) => Math.Max(0, MathF.Round(t * 10) / 10);

    /// The tanks with a track: those with keys, and the picked one.
    List<BattleUnit> KeyedTanks() =>
        C.Replay != null ? new() : file.Units.Where(u => C.Tanks.Any(t => t.Unit == u.Id && t.Keys.Count > 0) || u == selected).ToList();

    void Timeline()
    {
        var c = C;
        tlHits.Clear();
        var tanks = KeyedTanks();
        int rows = 1 + Math.Max(1, c.Cameras.Count) + tanks.Count + (c.Replay != null ? 1 : 0);
        float height = Ruler + rows * TrackHeight + 10;
        var box = Panel(new Rect(16, Screen.height - 16 - height, Screen.width - 32, height));
        tlArea = box;
        GUI.Box(box, ""); GUI.Box(box, ""); // see-through box style: darker drawn again, so the timeline reads over the map
        tlX0 = box.x + Header + 8; tlX1 = box.xMax - 12;
        scrollers.Add((box, by =>
        {
            float focus = Mouse.current is { } m ? TimeAt(m.position.ReadValue().x) : tlStart + tlSpan / 2;
            if (Keyboard.current?.shiftKey.isPressed == true) tlStart = Math.Max(0, tlStart - by * tlSpan * 0.1f);
            else
            {
                float span = Math.Clamp(tlSpan * (by > 0 ? 0.8f : 1.25f), 2, 600);
                tlStart = Math.Max(0, focus - (focus - tlStart) * span / tlSpan);
                tlSpan = span;
            }
        }));
        float end = tlStart + tlSpan;

        // The ruler: a tick every step (1-2-5 so they're at least 60 pixels apart), the time on each.
        var ruler = new Rect(tlX0, box.y + 4, tlX1 - tlX0, Ruler - 4);
        GUI.Box(ruler, "");
        float step = new[] { 0.1f, 0.2f, 0.5f, 1, 2, 5, 10, 20, 30, 60, 120 }.FirstOrDefault(s => s / tlSpan * (tlX1 - tlX0) >= 60, 300);
        for (float t = MathF.Ceiling(tlStart / step) * step; t <= end; t += step)
        {
            float x = XAt(t);
            GUI.Button(new Rect(x, ruler.yMax - 8, 2, 8), "");
            GUI.Label(new Rect(x + 3, ruler.y, 60, 18), step < 1 ? $"{t:0.0}" : $"{t:0}");
        }
        GUI.Label(new Rect(box.x + 8, box.y + 4, Header, 18), $"{scrub:0.0} s / {c.Length:0.0} s");
        tlHits.Add((ruler, () => Scrubbing()));
        // The end: the cinematic stops there (at its last key until the mark is dragged).
        if (c.Length >= tlStart && c.Length <= end)
        {
            var mark = new Rect(XAt(c.Length) - 18, ruler.y, 36, Ruler - 4);
            GUI.Button(mark, "End");
            tlHits.Add((mark, () => Retime(c.Length, v => c.End = Math.Max(0.1f, v))));
        }

        float y = box.y + Ruler + 4;
        Rect Track(float at) => new(tlX0, at, tlX1 - tlX0, TrackHeight - 3);
        bool Visible(float t) => t >= tlStart - 0.01f && t <= end + 0.01f;

        // Cuts: a bar for each stretch, named after the camera shown; drag a cut's left edge to move it.
        Button(new Rect(box.x + 8, y, Header - 34, TrackHeight - 3), "Cuts", () => { });
        Button(new Rect(box.x + Header - 22, y, 26, TrackHeight - 3), "+", AddCut);
        var track = Track(y);
        GUI.Box(track, "");
        tlHits.Add((track, () => Scrubbing()));
        var cuts = c.Cuts.OrderBy(k => k.Time).ToList();
        float from = 0;
        int shown = 0;
        for (int i = 0; i <= cuts.Count; i++)
        {
            float to = i < cuts.Count ? cuts[i].Time : Math.Max(c.Length, from);
            float a = Math.Max(XAt(from), tlX0), b = Math.Min(XAt(to), tlX1);
            if (b - a > 4 && c.Cameras.Count > 0)
            {
                GUI.Box(new Rect(a, y + 2, b - a, TrackHeight - 7), "");
                GUI.Label(new Rect(a + 6, y + 1, b - a - 8, TrackHeight - 4), shown < c.Cameras.Count ? Short(c.Cameras[shown].Name, Math.Max(4, (int)((b - a) / 7))) : "?");
            }
            if (i < cuts.Count)
            {
                var cut = cuts[i];
                shown = cut.Camera;
                from = cut.Time;
                if (Visible(cut.Time))
                {
                    var edge = new Rect(XAt(cut.Time) - 4, y, 8, TrackHeight - 3);
                    GUI.Button(edge, "");
                    tlHits.Add((edge, () => Retime(cut.Time, v => cut.Time = v)));
                }
            }
        }
        y += TrackHeight;

        // A track for each camera: its keys.
        if (c.Replay is { } replay)
        {
            GUI.Label(new Rect(box.x + 8, y, Header - 12, TrackHeight - 3), "Recorded tanks");
            var recorded = Track(y); GUI.Box(recorded, "");
            float a = Math.Max(XAt(0), tlX0), b = Math.Min(XAt(replay.Duration), tlX1);
            if (b > a) GUI.Box(new Rect(a, y + 3, b - a, TrackHeight - 7), "");
            tlHits.Add((recorded, () => Scrubbing())); y += TrackHeight;
        }
        for (int i = 0; i < c.Cameras.Count; i++)
        {
            int index = i;
            var cam = c.Cameras[i];
            Toggle(new Rect(box.x + 8, y, Header - 34, TrackHeight - 3), index == this.cam, " " + Short(cam.Name, 18), () => { this.cam = index; camKey = -1; Rebuild(); });
            Button(new Rect(box.x + Header - 22, y, 26, TrackHeight - 3), "+", () => { this.cam = index; AddCamKey(); });
            track = Track(y);
            GUI.Box(track, "");
            tlHits.Add((track, () => Scrubbing()));
            for (int k = 0; k < cam.Keys.Count; k++)
            {
                var key = cam.Keys[k];
                if (!Visible(key.Time)) continue;
                int keyIndex = k;
                bool picked = index == this.cam && k == camKey;
                var diamond = new Rect(XAt(key.Time) - (picked ? 9 : 6), y + 1, picked ? 18 : 12, TrackHeight - 5);
                GUI.Button(diamond, picked ? "*" : "");
                tlHits.Add((diamond, () =>
                {
                    this.cam = index; camKey = keyIndex; scrub = key.Time; ScrubCameraView(scrub); Rebuild();
                    return Retime(key.Time, v => key.Time = v);
                }));
            }
            y += TrackHeight;
        }
        if (c.Cameras.Count == 0)
        {
            GUI.Label(new Rect(box.x + 8, y, Header + (tlX1 - tlX0), TrackHeight - 3), "No cameras yet: + New in the panel above, fly the view where you want it, then + on its track.");
            y += TrackHeight;
        }

        // A track for each keyed tank (and the picked one): its keys, ! where it fires.
        foreach (var unit in tanks)
        {
            var tankTrack = c.Tanks.FirstOrDefault(t => t.Unit == unit.Id);
            Toggle(new Rect(box.x + 8, y, Header - 34, TrackHeight - 3), unit == selected, $" Tank {unit.Id}", () => { selected = unit; tankKey = -1; Rebuild(); });
            Button(new Rect(box.x + Header - 22, y, 26, TrackHeight - 3), "+", () => { selected = unit; AddTankKey(unit); });
            track = Track(y);
            GUI.Box(track, "");
            tlHits.Add((track, () => Scrubbing()));
            // Each "drive to": a bar from its key to when the tank gets there (or the key that ends it first).
            if (tankTrack != null)
                foreach (var drive in Cinema.Legs(unit, tankTrack).GroupBy(p => p.Value))
                {
                    if (drive.Key.Arrives is not { } at) continue;
                    float a = Math.Max(XAt(drive.Min(p => p.Key.Time)), tlX0), b = Math.Min(XAt(Math.Min(at, drive.Key.Ends)), tlX1);
                    if (b - a < 2) continue;
                    GUI.Button(new Rect(a, y + TrackHeight - 9, b - a, 6), "");
                    if (at <= drive.Key.Ends && XAt(at) < tlX1 - 70) GUI.Label(new Rect(XAt(at) + 3, y + 1, 90, TrackHeight - 4), $"there {at:0.0} s");
                }
            if (tankTrack != null)
                for (int k = 0; k < tankTrack.Keys.Count; k++)
                {
                    var key = tankTrack.Keys[k];
                    if (!Visible(key.Time)) continue;
                    int keyIndex = k;
                    bool picked = unit == selected && k == tankKey;
                    string label = (key.DriveTo != null ? "D" : key.Drives ? "d" : "") + (key.ShootAt != null ? "S" : key.Aims ? "A" : "") + (key.Fire ? "!" : "");
                    var diamond = new Rect(XAt(key.Time) - 6, y + 2, Math.Max(12, 9 * label.Length + 4), TrackHeight - 7);
                    if (picked) diamond = new Rect(diamond.x - 3, diamond.y - 1, diamond.width + 6, diamond.height + 2);
                    GUI.Button(diamond, picked ? "*" + label : label);
                    tlHits.Add((diamond, () =>
                    {
                        selected = unit; tankKey = keyIndex; scrub = key.Time; ViewAt(scrub);
                        return Retime(key.Time, v => key.Time = v);
                    }));
                }
            y += TrackHeight;
        }

        // The end down through the tracks, then the playhead, over everything.
        if (c.Length >= tlStart && c.Length <= end)
        {
            var line = new Rect(XAt(c.Length) - 1, box.y + Ruler, 3, height - Ruler - 4);
            GUI.Box(line, ""); GUI.Box(line, "");
        }
        if (Visible(scrub))
        {
            float x = XAt(scrub);
            GUI.Button(new Rect(x - 1, box.y + 2, 3, box.height - 4), "");
            GUI.Button(new Rect(x - 7, box.y + 2, 14, 10), "");
        }
    }

    /// Dragging the playhead: the view follows the picked camera.
    Action<float> Scrubbing() => t => { scrub = Snap(t); ScrubCameraView(scrub); };

    /// Dragging a key or cut by as much as the mouse moved since it was grabbed (a click alone leaves it where it is).
    static Action<float> Retime(float start, Action<float> set)
    {
        float? grabbed = null;
        return t => { grabbed ??= t; if (t != grabbed) set(Snap(start + t - grabbed.Value)); };
    }

    /// Every frame on the Cinematic tab: a press on the timeline starts what was there; the drag follows the mouse
    /// until the button is let go (then keys are put back in time order).
    void TimelineInput(Mouse? mouse)
    {
        if (mouse == null) return;
        var p = mouse.position.ReadValue();
        var gui = new Vector2(p.x, Screen.height - p.y);
        if (mouse.leftButton.wasPressedThisFrame && Inside(tlArea, gui))
        {
            tlDrag = null;
            // Keys and cut edges are drawn over the tracks: the last added under the mouse is the one on top.
            for (int i = tlHits.Count - 1; i >= 0; i--)
                if (Inside(tlHits[i].Area, gui)) { tlDrag = tlHits[i].Press(); break; }
            tlDone = () =>
            {
                // The picked keys stay picked once their order changes.
                var camPicked = Cam is { } picked && camKey >= 0 && camKey < picked.Keys.Count ? picked.Keys[camKey] : null;
                var tankPicked = PickedTankKey();
                foreach (var cam in C.Cameras) cam.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                foreach (var tank in C.Tanks) tank.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                C.Cuts.Sort((a, b) => a.Time.CompareTo(b.Time));
                if (camPicked != null) camKey = Cam!.Keys.IndexOf(camPicked);
                if (tankPicked != null) tankKey = C.Tanks.First(t => t.Unit == selected!.Id).Keys.IndexOf(tankPicked);
                Rebuild();
            };
        }
        if (tlDrag == null) return;
        if (mouse.leftButton.isPressed) tlDrag(TimeAt(Math.Clamp(gui.x, tlX0, tlX1)));
        else { tlDrag = null; tlDone?.Invoke(); tlDone = null; }
    }

    void AddCut()
    {
        if (Cam == null) { Say("Pick a camera first: the cut switches to it."); return; }
        C.Cuts.RemoveAll(k => Math.Abs(k.Time - scrub) < 0.05f);
        C.Cuts.Add(new Cut { Time = scrub, Camera = cam });
        C.Cuts.Sort((a, b) => a.Time.CompareTo(b.Time));
        Say($"From {scrub:0.0} s, {Cam.Name} is shown.");
    }

    void AddTankKey(BattleUnit unit)
    {
        if (C.Replay != null) return; // Recorded tank motion is immutable; replay edits only change cameras/cuts.
        var track = C.Tanks.FirstOrDefault(t => t.Unit == unit.Id);
        if (track == null) C.Tanks.Add(track = new TankTrack { Unit = unit.Id });
        var last = track.Keys.LastOrDefault(k => k.Time <= scrub);
        // Carries on from the key before (its aim and drive) until changed; the first key leaves the tank to its AI.
        var key = last == null ? new TankKey { Time = scrub } : new TankKey
        {
            Time = scrub, Turret = last.Turret, Gun = last.Gun, AimPoint = last.AimPoint?.ToArray(), AimUnit = last.AimUnit, ShootAt = last.ShootAt,
            Throttle = last.Throttle, Steer = last.Steer, DriveTo = last.DriveTo?.ToArray(),
        };
        track.Keys.RemoveAll(k => Math.Abs(k.Time - scrub) < 0.05f);
        track.Keys.Add(key);
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        tankKey = track.Keys.IndexOf(key);
        Rebuild();
    }

    /// Delete on the Cinematic tab: the picked tank key, else the picked camera key.
    void DeleteKey()
    {
        if (C.Replay == null && selected != null && C.Tanks.FirstOrDefault(t => t.Unit == selected.Id) is { } track && tankKey >= 0 && tankKey < track.Keys.Count)
        { track.Keys.RemoveAt(tankKey); tankKey = -1; Rebuild(); return; }
        if (Cam is { } cam && camKey >= 0 && camKey < cam.Keys.Count) { cam.Keys.RemoveAt(camKey); camKey = -1; Rebuild(); }
    }
}
