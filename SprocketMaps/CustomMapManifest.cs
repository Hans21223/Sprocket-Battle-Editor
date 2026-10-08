using System.Text.Json;

namespace SprocketMaps;

internal static class CustomMapManifest
{
    internal readonly record struct Metadata(ushort Capacity, string? DisplayName);
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
        return new((ushort)count,name);
    }
}
