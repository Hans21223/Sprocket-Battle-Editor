using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SprocketMaps;

/// A map the game builds itself (no Unity): its converted files in BepInEx\cache\SprocketMaps-runtime\<key>\Input and
/// map.json, which records the tank capacity and starting areas the in-game check found (capacity 0: not checked yet).
internal static class RuntimeMapFiles
{
    /// Part of the cache key: changing it converts and checks every in-game map again.
    internal const string Version = "1";

    internal sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string Source { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string Name { get; set; } = "";          // the scene name, as the Unity import names the same map
        public string DisplayName { get; set; } = "";
        public string Model { get; set; } = "";         // the OBJ, relative to Input
        public string? Lighting { get; set; }           // a Garry's Mod map's light and sky (.source.json), relative to Input
        public int Capacity { get; set; }               // tanks a side; 0 until checked
        public float[]? StartA { get; set; }            // the two teams' starting areas, map space
        public float[]? StartB { get; set; }
        public string? Failure { get; set; }            // why the check refused this exact source: not checked again until it changes
    }

    internal static string Key(string source, string fingerprint) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.GetFullPath(source).ToUpperInvariant() + fingerprint + "runtime-" + Version)))[..32].ToLowerInvariant();

    internal static string Write(Manifest manifest) => JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    internal static Manifest? Read(string file)
    {
        try { return File.Exists(file) && new FileInfo(file).Length <= 65536 ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// A manifest that is complete and checked for this exact source, or null.
    internal static Manifest? Ready(string file, string fingerprint) => Read(file) is { Version: 1, Failure: null } manifest
        && manifest.Fingerprint == fingerprint && manifest.Capacity is >= 1 and <= 16 && manifest.StartA is { Length: 3 }
        && manifest.StartB is { Length: 3 } && manifest.Name.Length > 0
        && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "Input", manifest.Model)) ? manifest : null;

    /// Why this exact source failed the check before, or null.
    internal static string? Failed(string file, string fingerprint) => Read(file) is { Version: 1, Failure: { } why } manifest && manifest.Fingerprint == fingerprint ? why : null;

    /// The map's model among its converted files: the chosen OBJ, or (a folder or archive) the largest one.
    internal static string? Model(string input, string? chosen)
    {
        if (chosen != null)
        {
            var exact = Directory.GetFiles(input, Path.GetFileName(chosen), SearchOption.AllDirectories).FirstOrDefault();
            if (exact != null) return Path.GetRelativePath(input, exact);
        }
        return Directory.GetFiles(input, "*.obj", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length).ThenBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(input, f)).FirstOrDefault();
    }
}
