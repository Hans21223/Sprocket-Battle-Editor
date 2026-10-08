using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    BattleFile? pendingReplay;
    float replayRequestedAt;
    Capture? recordingReplay;
    static string ReplayFolder => Path.Combine(Files.Battles, "Replays");

    sealed class Capture
    {
        internal ReplayRecording Recording = new();
        internal ReplayData Data = new();
        internal readonly List<CapturedActor> Actors = new();
        internal readonly Dictionary<IntPtr, int> Meshes = new(), Materials = new(), Textures = new();
        internal readonly Dictionary<string, int> Geometry = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> SurfaceIdentities = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> DesignVertices = new(StringComparer.OrdinalIgnoreCase);
        internal int Owner, Scene, Vertices, Parts, Keys, TextureBytes, BeltBytes, AudioBytes, AudioKeys;
        internal long VisualBytes, AudioDecodedBytes, BeltDecodedBytes;
        internal float Start, Next, NextCamera, Speed = 1;
        internal readonly Dictionary<IntPtr, int> AudioClips = new();
        internal readonly Dictionary<IntPtr, CapturedSound> RegularSounds = new();
        internal readonly List<CapturedSound> AudioTracks = new();
        internal AudioSource[] AudioSources = Array.Empty<AudioSource>();
        internal float NextAudioDiscovery;
    }
    sealed class CapturedActor
    {
        internal VehicleBehaviour Tank = null!;
        internal ReplayActor Data = new();
        internal readonly List<(Transform Transform, Renderer Renderer, VehicleRendererGroup? Group, ReplayPart Part)> Parts = new();
        internal readonly List<CapturedBelt> Belts = new();
    }

    void RecordReplay()
    {
        if (file.Cinema?.Replay != null) { Say("Open your battle to record a new replay. This recording stays unchanged."); return; }
        pendingReplay = file; replayRequestedAt = Time.unscaledTime;
        try { Play(); }
        finally { if (editing) pendingReplay = null; }
    }

    void RecordRunningBattle()
    {
        if (recordingReplay != null || Battle.Playing is not { } battle || Battle.Mode is not { } mode) return;
        if (!Battle.Spawned(mode)) { Say("Wait for the battle's tanks to load before recording."); return; }
        var tanks = new Dictionary<string, VehicleBehaviour>();
        foreach (var unit in battle.Units) if (Battle.TankOf(unit.Id) is { } tank) tanks[unit.Id] = tank;
        if (tanks.Count == 0) { Say("There are no loaded battle tanks to record."); return; }
        if (commanding) StopCommand();
        if (cinemaPlaying) StopCinema("recording the current battle");
        // Capture the live scene at this instant. No retry, respawn, relocation or AI simulation is requested.
        BeginReplayCapture(battle, tanks);
    }

    void BeginReplayCapture(BattleFile battle, Dictionary<string, VehicleBehaviour> tanks)
    {
        pendingReplay = null;
        while (audioEvents.TryDequeue(out _)) { }
        if (Battle.Mode is not { } mode) return;
        var copy = BattleFile.FromJson(battle.ToJson());
        copy.Name = $"{battle.Name} replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        copy.Gauntlet = null; copy.Locked = false; copy.FreeForAllSpawns = null;
        var data = new ReplayData { VisualVersion = 3, CaptureSpeed = Time.timeScale > 0 ? Time.timeScale : 1,
            AudioIncomplete = !ReplayAudioHooks.Attached };
        copy.Cinema = new CinemaData { Replay = data, PlayOnStart = false };
        copy.Cinema.Cameras.Add(new CameraTrack { Name = "Recorded camera" });
        copy.Cinema.Cuts.Add(new Cut { Time = 0, Camera = 0 });
        var capture = new Capture { Data = data, Recording = new ReplayRecording { Battle = copy },
            Owner = mode.GetInstanceID(), Scene = mode.gameObject.scene.handle, Start = Time.time,
            Speed = Time.timeScale > 0 ? Time.timeScale : 1 };
        recordingReplay = capture;
        try
        {
            foreach (var (id, tank) in tanks) if (tank != null) CaptureActor(capture, id, tank);
            CaptureFrame(capture, 0);
            CaptureAudio(capture, 0);
            Trace.Write($"replay: recording '{battle.Name}', {capture.Actors.Count} tanks, {capture.Parts} visual parts");
            Say("Recording replay. F9 stops, saves and opens it for cinematic editing.");
        }
        catch { recordingReplay = null; throw; }
    }

    void CaptureActor(Capture capture, string id, VehicleBehaviour tank, float at = 0)
    {
        if (capture.Actors.Count >= 32 || capture.Keys + 2 >= ReplayData.MaxKeys
            || !capture.Recording.Battle.Units.Any(u => u.Id == id)) return;
        var actor = new CapturedActor { Tank = tank, Data = new ReplayActor { Unit = id } };
        var pose = ReplayKey(tank.transform, null, 0, at == 0);
        actor.Data.Keys.Add(pose); capture.Keys++;
        if (at > 0) { actor.Data.Keys.Add(ReplayKey(tank.transform, null, at, true)); capture.Keys++; }

        CaptureVisuals(capture, actor);
        capture.Actors.Add(actor); capture.Data.Actors.Add(actor.Data);
    }

    static ReplayPoseKey ReplayKey(Transform transform, Transform? parent, float time, bool visible)
    {
        var position = parent == null ? transform.position : parent.InverseTransformPoint(transform.position);
        var rotation = parent == null ? transform.rotation : Quaternion.Inverse(parent.rotation) * transform.rotation;
        var scale = transform.lossyScale;
        if (parent != null)
        {
            var p = parent.lossyScale;
            scale = new Vector3(p.x != 0 ? scale.x / p.x : 1, p.y != 0 ? scale.y / p.y : 1, p.z != 0 ? scale.z / p.z : 1);
        }
        if (!SpawnGround.Finite(position) || !SpawnGround.Finite(scale) || !float.IsFinite(rotation.x)
            || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
            throw new InvalidDataException("A tank has an invalid pose; stopped recording before saving broken data.");
        return new ReplayPoseKey { Time = time, Position = Files.Array(position),
            Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, Scale = Files.Array(scale), Visible = visible };
    }

    void CaptureFrame(Capture c, float at)
    {
        if (c.Keys + c.Actors.Count + 2 * c.Parts > ReplayData.MaxKeys) { StopReplayCapture(false); return; }
        c.Data.Duration = at; // A destroyed native part halfway through this frame still leaves a savable prefix.
        void Add(List<ReplayPoseKey> keys, ReplayPoseKey key)
        {
            if (keys[^1].Time == at) keys[^1] = key;
            else { keys.Add(key); c.Keys++; }
        }
        foreach (var actor in c.Actors)
        {
            var tank = actor.Tank;
            bool alive = tank != null && tank.gameObject.activeInHierarchy;
            if (alive) Add(actor.Data.Keys, ReplayKey(tank!.transform, null, at, true));
            else if (actor.Data.Keys[^1].Visible) Add(actor.Data.Keys, Missing(actor.Data.Keys[^1], at));
            foreach (var (part, renderer, group, data) in actor.Parts)
            {
                // A camera LOD switch disables the chosen renderer; it must not remove the recorded part.
                bool visible = alive && part != null && renderer != null
                    && (group != null ? group.visible : part.gameObject.activeInHierarchy && renderer.enabled);
                // Static parts stay local to the moving hull; only moving/hidden parts need subsequent samples.
                var key = visible ? ReplayKey(part!, tank!.transform, at, true) : Missing(data.Keys[^1], at);
                var before = data.Keys[^1];
                if (Changed(before, key))
                {
                    float hold = Math.Max(0, at - 0.1f);
                    if (hold > before.Time) { data.Keys.Add(CopyAt(before, hold)); c.Keys++; }
                    Add(data.Keys, key);
                }
            }
            foreach (var belt in actor.Belts) CaptureBeltFrame(c, actor, belt, at, alive);
        }
        c.Data.Duration = at;
        if (at >= c.NextCamera && Camera.main is { } camera && SpawnGround.Finite(camera.transform.position)
            && float.IsFinite(camera.fieldOfView) && camera.fieldOfView > 0 && camera.fieldOfView < 180)
        {
            c.Recording.Battle.Cinema!.Cameras[0].Keys.Add(new CamKey { Time = at,
                Position = Files.Array(camera.transform.position), Rotation = Q(camera.transform.rotation), Fov = camera.fieldOfView });
            c.NextCamera = at + 2;
        }
        c.Next = at + 0.1f;
    }
    static float[] Q(Quaternion rotation) => new[] { rotation.x, rotation.y, rotation.z, rotation.w };
    static ReplayPoseKey CopyAt(ReplayPoseKey from, float time) => new() { Time = time, Position = from.Position,
        Rotation = from.Rotation, Scale = from.Scale, Visible = from.Visible };
    static ReplayPoseKey Missing(ReplayPoseKey from, float time) { var key = CopyAt(from, time); key.Visible = false; return key; }
    static bool Changed(ReplayPoseKey a, ReplayPoseKey b) => a.Visible != b.Visible
        || a.Position.Zip(b.Position, (x, y) => Math.Abs(x - y)).Any(n => n > 0.002f)
        || a.Rotation.Zip(b.Rotation, (x, y) => Math.Abs(x - y)).Any(n => n > 0.001f)
        || a.Scale.Zip(b.Scale, (x, y) => Math.Abs(x - y)).Any(n => n > 0.002f);

    void ReplayTick()
    {
        if (pendingReplay != null && Time.unscaledTime - replayRequestedAt > 60)
        { pendingReplay = null; Say("The battle didn't start; replay recording was cancelled."); }
        if (recordingReplay is not { } c) return;
        var mode = Battle.Mode;
        if (mode == null || mode.GetInstanceID() != c.Owner || mode.gameObject.scene.handle != c.Scene
            || !mode.gameObject.scene.isLoaded) { StopReplayCapture(false); return; }
        float at = Math.Clamp(Time.time - c.Start, 0, ReplayData.MaxSeconds);
        try
        {
            CaptureAudio(c, at); // Audio starts/stops are observed every rendered frame, including short effects.
            if (at < c.Next) return;
            foreach (var unit in c.Recording.Battle.Units)
                if (!c.Actors.Any(a => a.Data.Unit == unit.Id) && Battle.TankOf(unit.Id) is { } late)
                    CaptureActor(c, unit.Id, late, at);
            CaptureFrame(c, at);
            if (at >= ReplayData.MaxSeconds) StopReplayCapture(false);
        }
        catch (Exception ex) { Trace.Write($"replay recording stopped: {ex}"); StopReplayCapture(false); Say(ex.Message); }
    }

    void StopReplayCapture(bool edit)
    {
        if (recordingReplay is not { } c) return;
        try { CaptureAudio(c, Math.Clamp(Time.time - c.Start, 0, ReplayData.MaxSeconds)); }
        catch (Exception ex) { c.Data.AudioIncomplete = true; Trace.Write("replay audio final frame: " + ex.Message); }
        c.Data.Duration = Math.Max(c.Data.Duration, Math.Clamp(Time.time - c.Start, 0, ReplayData.MaxSeconds));
        recordingReplay = null; // Never let a repeated click or scene exit write twice.
        try
        {
            if (c.Data.Check(c.Recording.Battle.Units.Select(u => u.Id)) is { } why) throw new InvalidDataException(why);
            string path = Path.Combine(ReplayFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.replay.json");
            SavedFiles.Write(path, c.Recording.ToJson(), overwrite: false);
            try { ReplaySummary.Write(path, c.Recording); }
            catch (Exception ex) { Trace.Write($"replay list metadata: {ex.Message}"); }
            Trace.Write($"replay: saved {path}, {c.Data.Duration:0.0} s, {c.Keys} pose samples, {c.Data.Actors.Sum(a => a.Belts.Sum(b => b.Count))} track segments, {c.Data.Sounds.Count} sounds, {c.Data.Surfaces.Count} materials");
            if (edit && Battle.Mode is { } mode && mode.GetInstanceID() == c.Owner && mode.gameObject.scene.handle == c.Scene)
            {
                Enter(mode);
                if (editing && timeScaleBefore <= 0) timeScaleBefore = c.Speed;
                if (editing) LoadReplay(c.Recording);
            }
            else Say("Replay saved. Open Cinematic → Replays to edit it.");
        }
        catch (Exception ex) { Trace.Write($"replay save: {ex}"); Say("Couldn't save replay: " + ex.Message); }
    }

    void ReplayRecordingPanel()
    {
        buttons.Clear();
        var box = Panel(new Rect((Screen.width - 540) / 2f, 16, 540, 2 * Row + 2 * Pad));
        GUI.Label(new Rect(box.x + Pad, box.y + Pad, 524, Row),
            $"Recording replay: {recordingReplay!.Data.Duration:0.0} s. F9: stop & edit");
        Button(new Rect(box.x + Pad, box.y + Pad + Row, 258, Row - 3), "Stop & edit", () => StopReplayCapture(true));
        Button(new Rect(box.x + 274, box.y + Pad + Row, 258, Row - 3), "Stop & save", () => StopReplayCapture(false));
    }
}
