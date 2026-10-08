using BepInEx.Configuration;
using UnityEngine;

namespace SprocketMaps;

internal static class AutoMapImport
{
    static ConfigEntry<bool> enabled = null!;
    static ConfigEntry<string> sourceFolder = null!, unityEditor = null!;
    static readonly MapImportQueue queue = new();
    static readonly CancellationTokenSource stopping = new();
    static readonly Dictionary<string,string> scanErrors = new(StringComparer.OrdinalIgnoreCase), registered = new(StringComparer.OrdinalIgnoreCase);
    static Task<MapImportDiscovery.Snapshot>? scanning;
    static Task<MapImportConversion.Result>? running;
    static CancellationTokenSource? scanCancellation, importCancellation;
    static MapImportDiscovery.Candidate? active;
    static string settings = "", scanSettings = "", importSettings = "", template = "", cache = "";
    static bool settingsInitialized, configuredEnabled;
    static string configuredSource = "", configuredEditor = "";
    static string[] roots = Array.Empty<string>();
    static volatile string status = "";
    static string drawnStatus = "", drawnSource = "", drawnLabel = "";
    static float scanAt, noticeUntil, editorRetryAt;
    internal static void Configure(ConfigFile config)
    {
        enabled = config.Bind("Import","Automatic import",true,"Automatically convert OBJ, FBX, Unity packages, ZIPs and 7z map sources using the matching installed Unity editor.");
        sourceFolder = config.Bind("Import","Additional source folder","","Optional map folder or source archive. CustomMaps is always scanned.");
        unityEditor = config.Bind("Import","Unity editor path","","Optional path to Unity 6000.3.21f1 Editor/Unity.exe. Standard Unity Hub and drive/Unity locations are detected automatically.");
        roots = new[] { Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!,"CustomMaps"), Path.Combine(BepInEx.Paths.PluginPath,"CustomMaps") };
        cache = Path.Combine(BepInEx.Paths.CachePath,"SprocketMaps-imports");
        template = MapImportConversion.Template();
    }
    internal static void Step()
    {
        if (stopping.IsCancellationRequested || enabled == null) return;
        try
        {
            var enableImport = enabled.Value; var sourceSetting = sourceFolder.Value; var editorSetting = unityEditor.Value;
            if (!settingsInitialized || configuredEnabled != enableImport || configuredSource != sourceSetting || configuredEditor != editorSetting)
            {
                if (settings.Length != 0 && running != null) { importCancellation?.Cancel(); Notice("Stopping the previous map import."); }
                scanCancellation?.Cancel();
                configuredEnabled = enableImport; configuredSource = sourceSetting; configuredEditor = editorSetting; settingsInitialized = true;
                settings = enableImport + "\n" + sourceSetting + "\n" + editorSetting;
                queue.Clear(); scanErrors.Clear(); scanAt = editorRetryAt = 0;
            }
            CompleteScan();
            CompleteImport();
            if (!enabled.Value) return;
            var now = Time.realtimeSinceStartup;
            if (scanning == null && now >= scanAt)
            {
                scanAt = now + 15;
                scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                var token = scanCancellation.Token; var folders = roots; var additional = sourceFolder.Value;
                scanSettings = settings;
                scanning = Task.Run(() => MapImportDiscovery.Scan(folders,additional,token),token);
            }
            if (running != null || now < editorRetryAt || queue.Next() is not { } next) return;
            active = next; importSettings = settings;
            importCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            var importToken = importCancellation.Token; var editorPath = unityEditor.Value;
            status = "Preparing map import";
            Plugin.ModLog.LogInfo("Automatic map import starting: " + next.Source);
            running = Task.Run(() => MapImportConversion.Convert(next.Source,cache,editorPath,template,
                value => { if (!importToken.IsCancellationRequested) status = value; },importToken),importToken);
        }
        catch (Exception ex)
        {
            // Optional importer failures cannot stop the runtime map runner.
            Notice("Map import failed: " + ex.Message); scanAt = Time.realtimeSinceStartup + 15;
        }
    }
    static void CompleteScan()
    {
        if (scanning is not { IsCompleted: true } finished) return;
        scanning = null; scanCancellation?.Dispose(); scanCancellation = null;
        if (finished.IsCompletedSuccessfully && enabled.Value && scanSettings == settings)
        {
            var snapshot = finished.Result; queue.Observe(snapshot);
            var currentErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var error in snapshot.Failures)
            {
                currentErrors.Add(error.Source);
                if (scanErrors.TryGetValue(error.Source,out var prior) && prior == error.Message) continue;
                scanErrors[error.Source] = error.Message;
                Notice("Map source skipped: " + error.Source + ": " + error.Message);
            }
            foreach (var old in scanErrors.Keys.Where(k => !currentErrors.Contains(k)).ToArray()) scanErrors.Remove(old);
            if (active != null && !queue.Current(active)) importCancellation?.Cancel();
        }
        else if (finished.IsFaulted)
        {
            var failure = finished.Exception!.GetBaseException();
            if (scanSettings == settings && enabled.Value) Notice("Map folder scan failed: " + failure.Message);
        }
    }
    static void CompleteImport()
    {
        if (running is not { IsCompleted: true } finished) return;
        var job = active; bool cancelled = importCancellation?.IsCancellationRequested == true;
        running = null; active = null; importCancellation?.Dispose(); importCancellation = null;
        if (job == null) { _ = finished.Exception; return; }
        var valid = !cancelled && enabled.Value && importSettings == settings && queue.Current(job);
        if (finished.IsCompletedSuccessfully && valid)
        {
            var result = finished.Result;
            if (CustomMapBridge.LoadName(MapImportSource.SceneName(result.Source)) != null)
            {
                if (!registered.TryGetValue(result.Source,out var fingerprint) || fingerprint != result.Fingerprint)
                    Notice("Map already loaded. Restart Sprocket to use a newly converted version.");
            }
            else
            {
                CustomMapBridge.Register(result.Bundle);
                Notice("Map imported: " + Path.GetFileNameWithoutExtension(result.Source) + ". Open the map picker to choose it.");
                Plugin.ModLog.LogInfo($"Automatic map import ready: source='{result.Source}', bundle='{result.Bundle}', capacity={result.Capacity} per side");
            }
            registered[result.Source] = result.Fingerprint;
        }
        else if (finished.IsFaulted)
        {
            var error = finished.Exception!.GetBaseException();
            if (valid)
            {
                Notice("Map import failed: " + error.Message);
                if (error is MapImportConversion.EditorUnavailableException)
                { queue.Retry(job); editorRetryAt = Time.realtimeSinceStartup + 60; }
            }
        }
        else if (valid && finished.IsCanceled) queue.Retry(job);
    }
    static void Notice(string message)
    {
        status = message; noticeUntil = Time.realtimeSinceStartup + 20;
        Plugin.ModLog.LogInfo(message);
    }
    internal static void Draw()
    {
        var current = status;
        if ((running == null && Time.realtimeSinceStartup > noticeUntil) || string.IsNullOrEmpty(current)) return;
        var source = running == null ? "" : active?.Source ?? "";
        if (drawnStatus != current || drawnSource != source)
        {
            drawnStatus = current; drawnSource = source;
            drawnLabel = source.Length == 0 ? current : "Importing " + Path.GetFileNameWithoutExtension(source) + ": " + current;
        }
        GUI.Label(new Rect(12,Screen.height-52,Math.Max(0,Math.Min(Screen.width-24,1100)),44),drawnLabel);
    }
    internal static void Stop() => stopping.Cancel();
}
