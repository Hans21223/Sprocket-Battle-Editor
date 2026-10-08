using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SprocketMaps;

internal static class MapImportConversion
{
    internal sealed record Result(string Source, string Fingerprint, string Bundle, int Capacity);
    internal sealed class EditorUnavailableException : InvalidOperationException
    { internal EditorUnavailableException(string message) : base(message) { } }
    internal static string? FindEditor(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new EditorUnavailableException("The configured Unity editor was not found.");
            return CheckEditor(configured);
        }
        var options = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor", MapImportSource.UnityVersion, "Editor", "Unity.exe"),
        };
        foreach (var drive in DriveInfo.GetDrives())
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    options.Add(Path.Combine(drive.RootDirectory.FullName,"Unity",MapImportSource.UnityVersion,"Editor","Unity.exe"));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        return options.FirstOrDefault(File.Exists) is { } found ? CheckEditor(found) : null;
    }
    static string CheckEditor(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
        // Some Unity Windows installations omit executable version metadata. The exporter
        // checks Application.unityVersion before loading any staged scenery.
        if (version.Length != 0 && version.Split(' ','_')[0] != MapImportSource.UnityVersion)
            throw new EditorUnavailableException("Automatic map import needs Unity " + MapImportSource.UnityVersion + ".");
        return Path.GetFullPath(path);
    }
    internal static string Template()
    {
        using var stream = typeof(MapImportConversion).Assembly.GetManifestResourceStream("SprocketMaps.AutoMapExporter")
            ?? throw new InvalidOperationException("The automatic map exporter is missing. Reinstall Map Framework.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal static async Task<Result> Convert(string source, string cache, string configuredEditor, string template,
        Action<string> progress, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        progress("Checking map files");
        var fingerprint = MapImportSource.Fingerprint(source,token);
        var key = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant() + fingerprint + template))).ToLowerInvariant();
        var folder = Path.GetFullPath(Path.Combine(cache,key));
        Directory.CreateDirectory(folder);
        // Serialise converters across processes too: a second importer must never observe or
        // overwrite another invocation's partially built reports and bundle.
        using var ownership = await Lock(folder,token);
        var output = Path.Combine(folder,"Output");
        var name = MapImportSource.SceneName(source);
        var bundle = Path.Combine(output,name + ".bundle");
        if (Ready(Path.Combine(output,"ready.json"),bundle,out var capacity,token))
        {
            PublishBundlePath(Path.Combine(output,"ready.json"),bundle);
            progress("Using the previously imported map"); return new(source,fingerprint,bundle,capacity);
        }
        // A valid cached map can load on computers which do not have Unity installed.
        var editor = FindEditor(configuredEditor) ?? throw new EditorUnavailableException("Automatic map import needs Unity " + MapImportSource.UnityVersion + " installed. Ready-made map bundles still work.");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var project = Path.Combine(folder,"Work-" + id);
        var attempt = Path.Combine(folder,"Attempt-" + id);
        var input = Path.Combine(project,"Assets","Input");
        var log = Path.Combine(folder,"import.log");
        Process? process = null;
        try
        {
            progress("Unpacking map and textures");
            MapImportSource.Stage(source,input,token);
            if (MapImportSource.Fingerprint(source,token) != fingerprint) throw new InvalidOperationException("The map changed while it was being imported. Try again after saving its files.");
            Directory.CreateDirectory(Path.Combine(project,"Assets","Editor"));
            Directory.CreateDirectory(Path.Combine(project,"Packages"));
            Directory.CreateDirectory(Path.Combine(project,"ProjectSettings"));
            Directory.CreateDirectory(attempt);
            File.WriteAllText(Path.Combine(project,"Assets","Editor","SprocketAutoMapExport.cs"),template);
            File.WriteAllText(Path.Combine(project,"Packages","manifest.json"),"{\"dependencies\":{\"com.unity.modules.ai\":\"1.0.0\",\"com.unity.modules.animation\":\"1.0.0\",\"com.unity.modules.audio\":\"1.0.0\",\"com.unity.modules.assetbundle\":\"1.0.0\",\"com.unity.modules.imageconversion\":\"1.0.0\",\"com.unity.modules.physics\":\"1.0.0\",\"com.unity.modules.terrain\":\"1.0.0\",\"com.unity.modules.terrainphysics\":\"1.0.0\"}}");
            File.WriteAllText(Path.Combine(project,"ProjectSettings","ProjectVersion.txt"),"m_EditorVersion: " + MapImportSource.UnityVersion + "\n");
            var start = new ProcessStartInfo(editor) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = project };
            foreach (var arg in new[] { "-batchmode", "-nographics", "-quit", "-projectPath", project,
                "-executeMethod", "SprocketAutoMapExport.Build", "-logFile", log, "-sprocketOutput", attempt, "-sprocketMap", name,
                "-sprocketDisplayName", Directory.Exists(source) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)) : Path.GetFileNameWithoutExtension(source) }) start.ArgumentList.Add(arg);
            if (!Directory.Exists(source) && MapImportSource.Model(source))
            {
                start.ArgumentList.Add("-sprocketModel");
                start.ArgumentList.Add("Assets/Input/" + Path.GetFileName(source));
            }
            progress("Converting map in the background");
            token.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new InvalidOperationException("Unity could not start the automatic map conversion.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                var exited = process.WaitForExitAsync(timeout.Token);
                string last = "";
                while (!exited.IsCompleted)
                {
                    var status = Path.Combine(attempt,"status.txt");
                    string value = "";
                    try { if (File.Exists(status) && new FileInfo(status).Length < 1024) value = File.ReadAllText(status); } catch (IOException) { }
                    if (value.Length is > 0 and < 160 && value != last) { progress(value); last = value; }
                    await Task.WhenAny(exited,Task.Delay(1000,timeout.Token));
                }
                await exited;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            { throw new InvalidOperationException("Map conversion exceeded its 15-minute limit. Details: " + log,ex); }
            var attemptBundle = Path.Combine(attempt,name + ".bundle");
            if (process.ExitCode != 0 || !Ready(Path.Combine(attempt,"ready.json"),attemptBundle,out capacity,token))
                throw new InvalidOperationException(FailureReason(attempt) + " Details: " + log);
            if (MapImportSource.Fingerprint(source,token) != fingerprint)
                throw new InvalidOperationException("The map changed during conversion. Save the map files and try again.");
            token.ThrowIfCancellationRequested();
            PublishBundlePath(Path.Combine(attempt,"ready.json"),bundle);
            PublishBundlePath(Path.Combine(attempt,"custom-map-validation.json"),bundle);
            // Publish only fully checked output while holding exclusive cache ownership.
            await RemoveOwned(folder,output,"Output",true);
            Directory.Move(attempt,output);
            return new(source,fingerprint,bundle,capacity);
        }
        finally
        {
            bool stopped = process == null || process.HasExited;
            if (!stopped)
                try
                {
                    process!.Kill(entireProcessTree:true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    stopped = process.HasExited;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            process?.Dispose();
            // Do not remove files while an editor which could not be stopped still owns them.
            if (stopped)
            {
                var validation = Path.Combine(attempt,"custom-map-validation.json");
                try
                {
                    if (File.Exists(validation) && new FileInfo(validation).Length <= 1024*1024)
                        File.Copy(validation,Path.Combine(folder,"last-failure.json"),true);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                await RemoveOwned(folder,project,"Work-" + id);
                await RemoveOwned(folder,attempt,"Attempt-" + id);
            }
        }
    }
    static async Task<FileStream> Lock(string folder, CancellationToken token)
    {
        var until = DateTime.UtcNow.AddMinutes(16);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(folder,"conversion.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(250,token); }
        }
    }
    static void PublishBundlePath(string report, string bundle)
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(report))
            ?? throw new InvalidOperationException("Map conversion report is missing.");
        if (metadata.TryGetValue("bundlePath",out var existing) && existing.ValueKind == JsonValueKind.String && existing.GetString() == bundle) return;
        metadata["bundlePath"] = JsonSerializer.SerializeToElement(bundle);
        File.WriteAllText(report,JsonSerializer.Serialize(metadata));
    }
    static async Task RemoveOwned(string folder, string path, string expectedName, bool required = false)
    {
        var boundary = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(boundary,StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != expectedName)
            throw new InvalidOperationException("Unsafe map conversion cleanup path.");
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path,true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; }
            if (attempt < 4) await Task.Delay(100 * (attempt + 1));
        }
        if (required) throw new IOException("The previous map cache could not be replaced.",last);
    }
    static string FailureReason(string attempt)
    {
        var fallback = "The map could not be converted. Check its ground, textures and tank clearance.";
        var validation = Path.Combine(attempt,"custom-map-validation.json");
        try
        {
            if (!File.Exists(validation) || new FileInfo(validation).Length > 1024*1024) return fallback;
            using var document = JsonDocument.Parse(File.ReadAllText(validation));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error",out var message) && message.ValueKind == JsonValueKind.String)
            { var reason = message.GetString()?.Split('\n')[0].Trim(); if (!string.IsNullOrEmpty(reason)) return reason; }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return fallback;
    }
    internal static bool Ready(string report, string bundle, out int capacity, CancellationToken token = default)
    {
        capacity = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(report) || !File.Exists(bundle) || new FileInfo(report).Length > 1024*1024 || new FileInfo(bundle).Length > MapImportSource.MaxBytes) return false;
            using var data = JsonDocument.Parse(File.ReadAllText(report));
            var root = data.RootElement;
            bool Flag(string name) => root.TryGetProperty(name,out var value) && value.ValueKind == JsonValueKind.True;
            if (root.ValueKind != JsonValueKind.Object || !Flag("ready") || !Flag("pathComplete") || !Flag("originalsPreserved") || !Flag("originalImportersRestored")) return false;
            if (!root.TryGetProperty("spawnCount",out var spawnCount) || spawnCount.ValueKind != JsonValueKind.Number || !spawnCount.TryGetInt32(out capacity) || capacity is < 1 or > 16) return false;
            if (!root.TryGetProperty("bundleSha256",out var expectedHash) || expectedHash.ValueKind != JsonValueKind.String) return false;
            var manifest = Path.ChangeExtension(bundle,".json");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 65536 || CustomMapManifest.Capacity(File.ReadAllText(manifest)) != capacity) return false;
            using var stream = File.OpenRead(bundle);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; int n;
            while ((n = stream.Read(buffer,0,buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0,n)); }
            return System.Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() == expectedHash.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException) { return false; }
    }
}
