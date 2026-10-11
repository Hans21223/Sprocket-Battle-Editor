using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InGameCheck;

/// Development-only: plays one saved battle unattended, then logs tank heights, frame times and screenshots and quits.
/// Inert unless BepInEx/config/ingame-check.json exists; that request is renamed before anything runs, so a crash
/// cannot repeat it. Never shipped with the mods.
[BepInPlugin("local.sprocket.ingame-check", "In-game check", "1.0.0")]
[BepInDependency("local.sprocket.battles")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource Log = null!;
    internal static Request Settings = null!;

    public override void Load()
    {
        Log = base.Log;
        var request = Path.Combine(Paths.ConfigPath, "ingame-check.json");
        if (!File.Exists(request)) return;
        var running = request + ".running";
        File.Move(request, running, true);
        Settings = JsonSerializer.Deserialize<Request>(File.ReadAllText(running)) ?? new Request();
        Log.LogInfo($"In-game check requested: {JsonSerializer.Serialize(Settings)}");
        AddComponent<Check>();
    }
}

public sealed class Request
{
    public string Battle { get; set; } = "";
    public bool EditorPlay { get; set; }     // open the battle in the editor and press its Play, instead of the menu's Play
    public float SettleSeconds { get; set; } = 8;
    public float MeasureSeconds { get; set; } = 15;
    public int Shots { get; set; } = 3;
    public string Output { get; set; } = "";
    public bool Quit { get; set; } = true;
    public bool Experiments { get; set; }    // time the custom map with parts of it switched off in turn
}

public sealed class Check : MonoBehaviour
{
    public Check(IntPtr pointer) : base(pointer) { }

    static Request S => Plugin.Settings;
    static readonly Type Menu = Type.GetType("SprocketBattles.Menu, SprocketBattles", true)!;
    static readonly Type Battle = Type.GetType("SprocketBattles.Files, SprocketBattles", true)!;
    static readonly Type Editor = Type.GetType("SprocketBattles.BattleEditor, SprocketBattles", true)!;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    string stage = "menu";
    float since;
    readonly List<float> frames = new();
    int shots, samples, experiment, unfocused;
    float appeared = -1;

    static T Need<T>(T? member, string what) where T : class => member ?? throw new MissingMemberException(what);

    void Next(string next) { stage = next; since = Time.realtimeSinceStartup; Plugin.Log.LogInfo($"check: {next}"); }
    float Elapsed => Time.realtimeSinceStartup - since;

    void Update()
    {
        try { Step(); }
        catch (Exception ex) { Plugin.Log.LogError($"check failed in {stage}: {ex}"); Finish(); }
    }

    void Step()
    {
        switch (stage)
        {
            case "menu":
                if (!SceneManager.GetSceneByName("MainMenu").isLoaded || (bool)Need(Menu.GetProperty("Busy", Any), "Menu.Busy").GetValue(null)!)
                { since = Time.realtimeSinceStartup; return; }
                if (Elapsed < 5) return;
                var battle = Need(Battle.GetMethod("ReadBattle", Any), "Files.ReadBattle").Invoke(null, new object[] { S.Battle })
                    ?? throw new InvalidOperationException("battle file unreadable: " + S.Battle);
                var map = (string)Need(battle.GetType().GetProperty("Map", Any), "BattleFile.Map").GetValue(battle)!;
                Need(Menu.GetMethod("Launch", Any), "Menu.Launch").Invoke(null, new object?[] { map, battle, !S.EditorPlay });
                Next(S.EditorPlay ? "editor" : "spawn");
                break;
            case "editor":
                var instance = Need(Editor.GetField("instance", Any), "BattleEditor.instance").GetValue(null);
                if (instance == null || !(bool)Need(Editor.GetField("editing", Any), "BattleEditor.editing").GetValue(instance)!) { since = Time.realtimeSinceStartup; return; }
                if (Elapsed < 5) return;
                Need(Editor.GetMethod("Play", Any, null, Type.EmptyTypes, null), "BattleEditor.Play").Invoke(instance, null);
                Next("spawn");
                break;
            case "spawn":
                // Heights right after the tanks appear show any thrown into the air; later ones show them settling.
                // Editor Play restarts the battle a few frames after it is pressed, replacing the editor's tanks.
                if (Elapsed < (S.EditorPlay ? 8 : 3)) return;
                if (appeared < 0)
                {
                    if (SceneManager.GetSceneByName("LoadingScreen").isLoaded || UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>().Length == 0) return;
                    appeared = Time.realtimeSinceStartup;
                }
                float after = Time.realtimeSinceStartup - appeared;
                if (samples < 5 && after >= samples * 3) { samples++; Heights($"+{after:0}s after the tanks appeared"); }
                if (samples >= 5 && after >= Math.Max(S.SettleSeconds, 12)) Next("measure");
                break;
            case "measure":
                FrameTimingManager.CaptureFrameTimings();
                // The game limits its frame rate while its window is in the background; those frames don't count.
                if (Application.isFocused) frames.Add(Time.unscaledDeltaTime); else unfocused++;
                if (shots < S.Shots && Elapsed > (shots + 1) * S.MeasureSeconds / (S.Shots + 1))
                {
                    // ScreenCapture.CaptureScreenshot's file name doesn't pass through the interop; read the finished frame.
                    StartCoroutine(Shoot(Path.Combine(S.Output, $"shot-{shots + 1}.png")).WrapToIl2Cpp());
                    shots++;
                }
                if (Elapsed < S.MeasureSeconds) return;
                Report("all on", frames);
                Plugin.Log.LogInfo($"check: {unfocused} frames skipped while the window was in the background; vSync {QualitySettings.vSyncCount}, target {Application.targetFrameRate} fps");
                Heights("end");
                Experiment.Census();
                if (S.Experiments) { experiment = 0; Next("experiment"); } else Finish();
                break;
            case "experiment":
                FrameTimingManager.CaptureFrameTimings();
                // Each part of the map off for 8 seconds; frames while the pause menu is open don't count.
                if (Elapsed < 2) { frames.Clear(); return; }
                if (!SceneManager.GetSceneByName("PauseMenu").isLoaded && Application.isFocused) frames.Add(Time.unscaledDeltaTime);
                // 8 seconds of focused frames, or give up after 40.
                if (frames.Sum() < 8 && Elapsed < 40) return;
                Report(Experiment.All[experiment].Name, frames);
                Experiment.All[experiment].Undo?.Invoke();
                if (++experiment >= Experiment.All.Length) { Finish(); return; }
                Experiment.All[experiment].Undo = Experiment.All[experiment].Apply();
                Next("experiment");
                break;
        }
    }

    static void Report(string what, List<float> times)
    {
        try
        {
            var timings = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>(60);
            uint got = FrameTimingManager.GetLatestTimings(60, timings);
            if (got > 0)
            {
                double cpu = 0, main = 0, render = 0, gpu = 0;
                for (int i = 0; i < got; i++) { cpu += timings[i].cpuFrameTime; main += timings[i].cpuMainThreadFrameTime; render += timings[i].cpuRenderThreadFrameTime; gpu += timings[i].gpuFrameTime; }
                Plugin.Log.LogInfo($"check:   {what}: CPU frame {cpu / got:0.0} ms (main thread {main / got:0.0}, render thread {render / got:0.0}), GPU {gpu / got:0.0} ms over {got} frames");
            }
            else Plugin.Log.LogInfo("check:   frame timings unavailable in this build");
        }
        catch (Exception ex) { Plugin.Log.LogInfo($"check:   frame timings failed: {ex.Message}"); }
        var sorted = times.Skip(5).OrderByDescending(f => f).ToList();
        if (sorted.Count == 0) { Plugin.Log.LogInfo($"check: {what}: no frames"); return; }
        Plugin.Log.LogInfo($"check: {what}: {sorted.Count} frames, average {sorted.Count / sorted.Sum():0.0} fps, " +
            $"1% low {1 / sorted[Math.Max(0, sorted.Count / 100 - 1)]:0.0} fps, worst {sorted[0] * 1000:0} ms");
        times.Clear();
    }

    static System.Collections.IEnumerator Shoot(string path)
    {
        yield return new WaitForEndOfFrame();
        var frame = ScreenCapture.CaptureScreenshotAsTexture();
        var pixels = frame.GetPixels32();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(pixels[i].r, pixels[i].g, pixels[i].b, 255);
        frame.SetPixels32(pixels); frame.Apply();
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(frame).ToArray());
        UnityEngine.Object.Destroy(frame);
        Plugin.Log.LogInfo($"check: screenshot {path}");
    }

    static void Heights(string when)
    {
        var tanks = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>();
        Plugin.Log.LogInfo($"check: {when}: {tanks.Length} tanks");
        foreach (var tank in tanks)
        {
            var at = tank.transform.position;
            // The first surface below that isn't part of a tank.
            var below = Physics.RaycastAll(at + Vector3.up * 2, Vector3.down, 2000, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider != null && h.collider.attachedRigidbody == null && h.collider.GetComponentInParent<VehicleBehaviour>() == null)
                .OrderBy(h => h.distance).ToList();
            string ground = below.Count > 0 ? $"{at.y - below[0].point.y:0.00} m above '{below[0].collider.name}'" : "no ground below";
            var body = tank.GetComponentInChildren<Rigidbody>();
            Plugin.Log.LogInfo($"check:   {tank.name} at ({at.x:0.0}, {at.y:0.00}, {at.z:0.0}), {ground}, speed {(body ? body.linearVelocity.magnitude : 0):0.0} m/s");
        }
    }

    void Finish()
    {
        stage = "done";
        var running = Path.Combine(Paths.ConfigPath, "ingame-check.json.running");
        if (File.Exists(running)) File.Move(running, Path.ChangeExtension(running, ".done"), true);
        Plugin.Log.LogInfo("check: finished");
        if (S.Quit) Application.Quit();
    }
}

/// One part of the custom map switched off; Apply returns how to switch it back.
sealed class Experiment
{
    public string Name = "";
    public Func<Action?> Apply = () => null;
    public Action? Undo;

    static GameObject? Environment() => UnityEngine.Object.FindObjectsOfType<GameObject>()
        .FirstOrDefault(o => o.transform.parent == null && o.name.StartsWith("Custom environment: ", StringComparison.Ordinal));
    static List<Renderer> Renderers(Func<Renderer, bool> which) =>
        Environment()?.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && which(r)).ToList() ?? new();
    static Action Hide(List<Renderer> renderers) { foreach (var r in renderers) r.enabled = false; return () => { foreach (var r in renderers) if (r) r.enabled = true; }; }
    static bool Uses(Renderer r, Func<Material, bool> test) => r.sharedMaterials.Any(m => m != null && test(m));

    static Func<Action?> ShadowsOff(Func<Renderer, bool> which) => () =>
    {
        var cast = Renderers(r => r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off && which(r));
        var old = cast.Select(r => r.shadowCastingMode).ToList();
        foreach (var r in cast) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Plugin.Log.LogInfo($"check:   {cast.Count} renderers");
        return () => { for (int i = 0; i < cast.Count; i++) if (cast[i]) cast[i].shadowCastingMode = old[i]; };
    };
    static bool Named(Renderer r, string part) => Uses(r, m => m.name.Contains(part, StringComparison.Ordinal));

    static bool Cutout(Renderer r) => Uses(r, m => m.IsKeywordEnabled("_ALPHATEST_ON") || m.name.Contains("(custom map cutout)", StringComparison.Ordinal));
    static bool Tree(Renderer r) => Named(r, "(native scenery)");
    static bool Far(Renderer r, float metres) => Camera.main && Vector3.Distance(Camera.main.transform.position, r.bounds.center) > metres;

    /// What the cameras (main and shadow) drew last frame: renderers, triangles, and the biggest materials and objects.
    public static void Census()
    {
        try
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var hdType = Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime");
            object? hd = hdType == null || pipeline == null ? null : typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                .GetMethod("TryCast")!.MakeGenericMethod(hdType).Invoke(pipeline, null);
            object? Get(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o) ?? o?.GetType().GetField(name)?.GetValue(o);
            var drawer = Get(Get(Get(hd, "currentPlatformRenderPipelineSettings"), "gpuResidentDrawerSettings"), "mode");
            Plugin.Log.LogInfo($"check: pipeline {pipeline?.name}, GPU Resident Drawer {drawer ?? "unknown"}, SRP batcher {UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching}, " +
                $"camera FOV {(Camera.main ? Camera.main.fieldOfView : 0):0}, LOD bias {QualitySettings.lodBias}, shadow distance {QualitySettings.shadowDistance}");
        }
        catch (Exception ex) { Plugin.Log.LogInfo($"check: pipeline settings unreadable: {ex.GetBaseException().Message}"); }
        var all = Renderers(_ => true);
        var seen = all.Where(r => r.isVisible).Select(r =>
        {
            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
            long triangles = 0;
            if (mesh != null) for (int s = 0; s < mesh.subMeshCount; s++) triangles += mesh.GetIndexCount(s) / 3;
            return (Renderer: r, Triangles: triangles, Material: r.sharedMaterial ? r.sharedMaterial.name : "none", Cutout: Cutout(r));
        }).ToList();
        Plugin.Log.LogInfo($"check: census: {seen.Count} of {all.Count} renderers visible, {seen.Sum(s => s.Triangles) / 1e6:0.00} M triangles, " +
            $"cutouts {seen.Where(s => s.Cutout).Sum(s => s.Triangles) / 1e6:0.00} M in {seen.Count(s => s.Cutout)} renderers, shadow casters {seen.Count(s => s.Renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)}");
        foreach (var g in seen.GroupBy(s => s.Material).OrderByDescending(g => g.Sum(s => s.Triangles)).Take(10))
            Plugin.Log.LogInfo($"check:   material {g.Key}: {g.Sum(s => s.Triangles) / 1e6:0.00} M triangles in {g.Count()} renderers{(g.First().Cutout ? ", cutout" : "")}");
        foreach (var s in seen.OrderByDescending(s => s.Triangles).Take(6))
            Plugin.Log.LogInfo($"check:   object {s.Renderer.name} ({s.Renderer.transform.parent?.name}): {s.Triangles / 1e3:0} k triangles, {s.Material}, " +
                $"{Vector3.Distance(Camera.main ? Camera.main.transform.position : Vector3.zero, s.Renderer.bounds.center):0} m away");
    }

    public static readonly Experiment[] All =
    {
        new() { Name = "baseline again" },
        new() { Name = "half resolution", Apply = () =>
            {
                int w = Screen.width, h = Screen.height; var mode = Screen.fullScreenMode;
                Screen.SetResolution(w / 2, h / 2, mode);
                return () => Screen.SetResolution(w, h, mode);
            } },
        new() { Name = "no cutouts", Apply = () => Hide(Renderers(Cutout)) },
        new() { Name = "no native trees", Apply = () => Hide(Renderers(Tree)) },
        new() { Name = "no trees beyond 500 m", Apply = () => Hide(Renderers(r => Tree(r) && Far(r, 500))) },
        new() { Name = "no other scenery beyond 500 m", Apply = () => Hide(Renderers(r => !Tree(r) && Far(r, 500))) },
        new() { Name = "no other scenery beyond 250 m", Apply = () => Hide(Renderers(r => !Tree(r) && Far(r, 250))) },
        new() { Name = "no cutout shadows", Apply = ShadowsOff(Cutout) },
        new() { Name = "no scenery shadows", Apply = ShadowsOff(_ => true) },
        new() { Name = "no scenery at all", Apply = () => Hide(Renderers(_ => true)) },
    };
}
