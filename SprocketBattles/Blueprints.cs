using System.Text.Json;
using System.Text.RegularExpressions;

namespace SprocketBattles;

/// Designs the game can't build properly: a track belt naming no track segment the game has (designs saved by older
/// versions can have an empty one), or with no pitch. Such a tank can load with damaged track state and crash during
/// native teardown; a pitch of 0 stops it spawning ("Segment pitch must be greater than 0").
/// Nothing here touches Unity, so the tests run it.
public static class Blueprints
{
    static readonly Regex Guid = new("\"guid\"\\s*:\\s*\"([^\"]+)\"");
    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// Select a loading vehicle only after its track data and native card have both passed validation.
    /// A rejected or unreadable first design must never become the editor's hidden vehicle.
    public static string? ChooseLoadingVehicle(IEnumerable<string> candidates, Func<string, string?> problem,
        Func<string, bool> canLoad, Action<string, string>? skipped = null)
    {
        foreach (var path in candidates)
        {
            if (problem(path) is { } why) { skipped?.Invoke(path, why); continue; }
            if (canLoad(path)) return path;
            skipped?.Invoke(path, "the game couldn't read its vehicle card");
        }
        return null;
    }

    /// The track segments the game has: the ids of its segment parts (StreamingAssets\Parts\*Segment*.json).
    public static HashSet<string> Segments(string partsDir)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(partsDir)) return ids;
        foreach (var file in Directory.GetFiles(partsDir, "*Segment*.json"))
            try { if (Guid.Match(File.ReadAllText(file)) is { Success: true } m) ids.Add(m.Groups[1].Value); }
            catch (Exception) { }
        return ids;
    }

    /// What's wrong with a design, or null. With no segments known, only the pitch is checked.
    public static string? Broken(string blueprint, IReadOnlySet<string> segments)
    {
        string text;
        try { text = File.ReadAllText(blueprint); }
        catch (Exception ex) { return "its file can't be read (" + ex.Message + ")"; }
        try
        {
            using var json = JsonDocument.Parse(text, Lenient);
            return Check(json.RootElement);
        }
        catch (JsonException) { return "its blueprint JSON can't be read"; }

        string? Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) if (Check(item) is { } error) return error;
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "trackBelt", StringComparison.OrdinalIgnoreCase))
                {
                    if (!value.TryGetProperty("blueprint", out var belt) || belt.ValueKind != JsonValueKind.Object)
                        return "its track belt blueprint is missing";
                    if (CheckBelt(belt) is { } error) return error;
                }
                // Also recognize older layouts by the belt's own segment field, independent of property order.
                if (value.TryGetProperty("segmentID", out _) && CheckBelt(value) is { } beltError) return beltError;
                foreach (var property in value.EnumerateObject()) if (Check(property.Value) is { } error) return error;
            }
            return null;
        }

        string? CheckBelt(JsonElement belt)
        {
            string? segment = belt.TryGetProperty("segmentID", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(segment)) return "its tracks have no track segment (saved by an older game version)";
            if (segments.Count > 0 && !segments.Contains(segment)) return "its tracks use a track segment the game doesn't have";
            if (!belt.TryGetProperty("pitch", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var pitch)
                || !double.IsFinite(pitch) || pitch <= 0) return "its tracks have no segment pitch";
            return null;
        }
    }
}
