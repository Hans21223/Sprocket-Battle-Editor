using System.Text.Json;

namespace SprocketMaps;

internal static class CustomMapManifest
{
    internal readonly record struct Metadata(ushort Capacity, string? DisplayName, Lighting? Lighting = null);
    internal readonly record struct Rgb(float R, float G, float B);
    /// The map scene's own lighting settings, which the importer records (RenderSettings belong to the active scene,
    /// and switching the game's active scene at runtime crashes the mod runtime).
    internal sealed record Lighting(string AmbientMode, Rgb Sky, Rgb Equator, Rgb Ground, float AmbientIntensity, bool CustomSky,
        bool ProceduralSky, Rgb SkyTint, Rgb SkyGround, float SkyExposure, bool Fog, Rgb FogColor, string FogMode, float FogDensity, float FogEnd);
    internal static ushort Capacity(string? json) => Read(json).Capacity;
    internal static string? DisplayName(string? json) => Read(json).DisplayName;
    internal static Metadata Read(string? json)
    {
        if (json == null) return new(16,null); // Existing authored bundles retain their declared default.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 1
            || !root.TryGetProperty("spawnCount", out var value) || !value.TryGetInt32(out var count) || count is < 1 or > 16)
            throw new InvalidOperationException("The map manifest needs schemaVersion 1 and spawnCount from 1 to 16.");
        string? name = null;
        if (root.TryGetProperty("displayName",out var label))
        {
            if (label.ValueKind != JsonValueKind.String) throw new InvalidOperationException("The map displayName must be text.");
            name = label.GetString()!.Trim();
            if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidOperationException("The map displayName needs 1 to 80 visible characters.");
        }
        Lighting? lighting = null;
        if (root.TryGetProperty("lighting", out var light) && light.ValueKind == JsonValueKind.Object)
            try { lighting = ReadLighting(light); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException) { lighting = null; } // optional: the map keeps the game's lighting
        return new((ushort)count,name,lighting);
    }

    static Lighting ReadLighting(JsonElement e)
    {
        float Number(string name, float low, float high)
        {
            float v = e.GetProperty(name).GetSingle();
            if (!float.IsFinite(v) || v < low || v > high) throw new InvalidOperationException("Map lighting value out of range: " + name);
            return v;
        }
        Rgb Colour(string name)
        {
            var c = e.GetProperty(name);
            float Channel(string k) { float v = c.GetProperty(k).GetSingle(); return float.IsFinite(v) && v >= 0 && v <= 64 ? v : throw new InvalidOperationException("Map lighting colour out of range: " + name); }
            return new(Channel("r"), Channel("g"), Channel("b"));
        }
        string Text(string name) => e.GetProperty(name).GetString() ?? "";
        bool Flag(string name) => e.GetProperty(name).ValueKind == JsonValueKind.True;
        return new(Text("ambientMode"), Colour("ambientSky"), Colour("ambientEquator"), Colour("ambientGround"), Number("ambientIntensity", 0, 64),
            Flag("customSky"), Flag("proceduralSky"), Colour("skyTint"), Colour("skyGround"), Number("skyExposure", 0, 64),
            Flag("fog"), Colour("fogColor"), Text("fogMode"), Number("fogDensity", 0, 100), Number("fogEnd", 0, 1e6f));
    }
}
