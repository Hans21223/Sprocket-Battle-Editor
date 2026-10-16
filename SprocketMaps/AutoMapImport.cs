using BepInEx.Configuration;
using UnityEngine;

namespace SprocketMaps;

internal static class AutoMapImport
{
    static ConfigEntry<bool> enabled = null!;
    static ConfigEntry<string> sourceFolder = null!, unityEditor = null!, buildInGame = null!;
    static readonly MapImportQueue queue = new();
    static readonly CancellationTokenSource stopping = new();
    static readonly Dictionary<string,string> scanErrors = new(StringComparer.OrdinalIgnoreCase), registered = new(StringComparer.OrdinalIgnoreCase);
    static Task<MapImportDiscovery.Snapshot>? scanning;
    static Task<MapImportConversion.Result>? running;
    static RuntimeMapImport? building; // a map the game builds itself (no Unity)
    static readonly Dictionary<string, Exception> inGame = new(StringComparer.OrdinalIgnoreCase); // Unity is missing or failed for these: why
    static readonly HashSet<string> unityOnly = new(StringComparer.OrdinalIgnoreCase); // no OBJ after all (FBX): Unity's, whatever the setting
    static string runtimeCache = "";
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
        unityEditor = config.Bind("Import","Unity editor path","","Optional, usually left empty: Unity 6000.3.21f1 is found wherever Unity Hub or Unity's installer put it. For a copy without an installer, its Editor\\Unity.exe (or its folder), without quotes, e.g. D:\\Unity\\6000.3.21f1\\Editor\\Unity.exe");
        roots = new[] { Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!,"CustomMaps"), Path.Combine(BepInEx.Paths.PluginPath,"CustomMaps") };
        buildInGame = config.Bind("Import","Build maps in the game","Automatic",new ConfigDescription(
            "Automatic: OBJ and Garry's Mod maps go to Unity when it is installed, and the game builds them itself when Unity is missing or fails. "
            + "Always: the game always builds them itself (no Unity needed). Never: Unity only. FBX models and Unity packages always need Unity.",
            new AcceptableValueList<string>("Automatic","Always","Never")));
        cache = Path.Combine(BepInEx.Paths.CachePath,"SprocketMaps-imports");
        runtimeCache = Path.Combine(BepInEx.Paths.CachePath,"SprocketMaps-runtime");
        template = MapImportConversion.Template();
    }
    internal static void Step()
    {
        if (stopping.IsCancellationRequested || enabled == null) return;
        try
        {
            var enableImport = enabled.Value; var sourceSetting = sourceFolder.Value; var editorSetting = unityEditor.Value + "|" + buildInGame.Value;
            if (!settingsInitialized || configuredEnabled != enableImport || configuredSource != sourceSetting || configuredEditor != editorSetting)
            {
                if (settings.Length != 0 && (running != null || building != null)) { importCancellation?.Cancel(); Notice("Stopping the previous map import."); }
                scanCancellation?.Cancel();
                configuredEnabled = enableImport; configuredSource = sourceSetting; configuredEditor = editorSetting; settingsInitialized = true;
                settings = enableImport + "\n" + sourceSetting + "\n" + editorSetting;
                queue.Clear(); scanErrors.Clear(); inGame.Clear(); unityOnly.Clear(); scanAt = editorRetryAt = 0;
            }
            CompleteScan();
            CompleteImport();
            CompleteBuild();
            RuntimeMapImport.Retire();
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
            if (running != null || building != null || now < editorRetryAt || queue.Next() is not { } next) return;
            active = next; importSettings = settings;
            importCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            var importToken = importCancellation.Token; var editorPath = unityEditor.Value;
            status = "Preparing map import";
            var mode = buildInGame.Value;
            if (RuntimeMapImport.Supports(next.Source) && !unityOnly.Contains(next.Source) && (mode == "Always" || mode == "Automatic" && inGame.ContainsKey(next.Source)))
            {
                Plugin.ModLog.LogInfo("Automatic map import starting, built in the game: " + next.Source);
                building = new RuntimeMapImport(next.Source, runtimeCache, value => { if (!importToken.IsCancellationRequested) status = value; }, Plugin.ModLog.LogInfo, importToken);
                return;
            }
            Plugin.ModLog.LogInfo("Automatic map import starting: " + next.Source);
            running = Task.Run(() => MapImportConversion.Convert(next.Source,cache,editorPath,template,
                value => { if (!importToken.IsCancellationRequested) status = value; },importToken,Plugin.ModLog.LogInfo),importToken);
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
                var bundle = result.Bundle; _ = Task.Run(() => MapImportConversion.RemoveOlder(bundle)); // gigabytes: off the game's thread
                Notice("Map imported: " + MapImportSource.DisplayName(result.Source) + ". Open the map picker to choose it.");
                Plugin.ModLog.LogInfo($"Automatic map import ready: source='{result.Source}', bundle='{result.Bundle}', capacity={result.Capacity} per side");
            }
            registered[result.Source] = result.Fingerprint;
        }
        else if (finished.IsFaulted)
        {
            var error = finished.Exception!.GetBaseException();
            if (valid)
            {
                // Unity is missing, or failed for its own reasons: the game builds an OBJ or Garry's Mod map itself.
                if (error is MapImportConversion.EditorUnavailableException or MapImportConversion.UnityProblemException
                    && buildInGame.Value == "Automatic" && RuntimeMapImport.Supports(job.Source) && !unityOnly.Contains(job.Source))
                {
                    Plugin.ModLog.LogInfo($"Map import: {error.Message} The game builds '{MapImportSource.DisplayName(job.Source)}' itself instead.");
                    inGame[job.Source] = error; queue.Retry(job);
                    return;
                }
                Notice("Map import failed: " + error.Message);
                if (error is MapImportConversion.EditorUnavailableException)
                { queue.Retry(job); editorRetryAt = Time.realtimeSinceStartup + 60; }
            }
        }
        else if (valid && finished.IsCanceled) queue.Retry(job);
    }
    static void CompleteBuild()
    {
        if (building == null) return;
        building.Step(UnityEngine.SceneManagement.SceneManager.GetSceneByName("MainMenu").isLoaded);
        if (!building.Finished) return;
        var job = active; var done = building; bool cancelled = importCancellation?.IsCancellationRequested == true;
        building = null; active = null; importCancellation?.Dispose(); importCancellation = null;
        if (job == null) return;
        var valid = !cancelled && enabled.Value && importSettings == settings && queue.Current(job);
        if (done.Result is { } manifest && valid)
        {
            if (CustomMapBridge.LoadName(manifest.Name) != null)
            {
                if (!registered.TryGetValue(job.Source,out var fingerprint) || fingerprint != manifest.Fingerprint)
                    Notice("Map already loaded. Restart Sprocket to use a newly converted version.");
            }
            else
            {
                CustomMapBridge.RegisterRuntime(done.Folder, manifest);
                var folder = done.Folder; var name = manifest.Name; _ = Task.Run(() => RuntimeMapImport.RemoveOlder(folder, name));
                Notice("Map imported: " + manifest.DisplayName + ". Open the map picker to choose it.");
                Plugin.ModLog.LogInfo($"Automatic map import ready, built in the game: source='{job.Source}', capacity={manifest.Capacity} per side");
            }
            registered[job.Source] = manifest.Fingerprint;
        }
        else if (done.Failure is { } error && valid)
        {
            if (error is OperationCanceledException) queue.Retry(job);
            else if (error is RuntimeMapImport.NeedsUnityException)
            {
                // No OBJ after all (FBX models): Unity's job. When it came here from Unity, what Unity said stands, and a
                // missing Unity is looked for again in a minute, as before.
                unityOnly.Add(job.Source);
                if (!inGame.Remove(job.Source, out var unity)) queue.Retry(job);
                else
                {
                    Notice("Map import failed: " + unity.Message);
                    if (unity is MapImportConversion.EditorUnavailableException) { queue.Retry(job); editorRetryAt = Time.realtimeSinceStartup + 60; }
                }
            }
            else Notice("Map import failed: " + error.Message);
        }
        else if (cancelled && valid) queue.Retry(job);
    }
    static void Notice(string message)
    {
        status = message; noticeUntil = Time.realtimeSinceStartup + 20;
        Plugin.ModLog.LogInfo(message);
    }
    internal static void Draw()
    {
        var current = status;
        bool busy = running != null || building != null;
        if ((!busy && Time.realtimeSinceStartup > noticeUntil) || string.IsNullOrEmpty(current)) return;
        // Progress stays in the menus; it sat over whole battles while a slow import ran. Short notices show anywhere.
        if (Time.realtimeSinceStartup > noticeUntil && !UnityEngine.SceneManagement.SceneManager.GetSceneByName("MainMenu").isLoaded) return;
        var source = !busy ? "" : active?.Source ?? "";
        if (drawnStatus != current || drawnSource != source)
        {
            drawnStatus = current; drawnSource = source;
            drawnLabel = source.Length == 0 ? current : "Importing " + Path.GetFileNameWithoutExtension(source) + ": " + current;
        }
        GUI.Label(new Rect(12,Screen.height-52,Math.Max(0,Math.Min(Screen.width-24,1100)),44),drawnLabel);
    }
    internal static void Stop() => stopping.Cancel();
}
