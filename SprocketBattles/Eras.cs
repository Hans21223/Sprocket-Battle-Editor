using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SprocketBattles;

/// The game's eras (StreamingAssets\Eras: its own and any custom ones dropped in there, as the game reads them) and the
/// era a design is from: the last to start on or before the date in its header ("creationDate": "1945.09.02"), or, for
/// a design from before dates, the era its header names ("era": "Latewar").
/// Nothing here touches Unity, so the tests run it.
public static class Eras
{
    public sealed record Era(string Name, DateTime Start, int? CustomCost = null, int? MediumMass = null, int? HeavyMass = null);

    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly Regex Created = new("\"creationDate\"\\s*:\\s*\"([0-9.]+)\"");
    static readonly Regex Named = new("\"era\"\\s*:\\s*\"([^\"]+)\"");

    /// The eras in a folder of era files, oldest first (a file that can't be read is left out).
    public static List<Era> Read(string dir)
    {
        var eras = new List<Era>();
        if (!Directory.Exists(dir)) return eras;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), Lenient);
                var r = doc.RootElement;
                if (r.TryGetProperty("name", out var n) && r.TryGetProperty("start", out var s) && Date(s.GetString()) is { } start)
                {
                    int? cost = null;
                    if (r.TryGetProperty("cost", out var c) && c.TryGetInt32(out int cv)) cost = cv;
                    else if (r.TryGetProperty("eraCost", out var ec) && ec.TryGetInt32(out int ecv)) cost = ecv;
                    int? mediumMass = r.TryGetProperty("mediumVehicleMass", out var mm) && mm.TryGetInt32(out int mv) ? mv : null;
                    int? heavyMass = r.TryGetProperty("heavyVehicleMass", out var hm) && hm.TryGetInt32(out int hv) ? hv : null;
                    eras.Add(new Era(n.GetString() ?? Path.GetFileNameWithoutExtension(file), start, cost, mediumMass, heavyMass));
                }
            }
            catch (Exception) { }
        return eras.OrderBy(e => e.Start).ToList();
    }

    /// The era a date falls in, or null if it's before them all.
    public static string? Of(IReadOnlyList<Era> eras, DateTime date) => eras.LastOrDefault(e => e.Start <= date)?.Name;

    /// A design's era, from the start of its file (the header comes first), or null.
    public static string? Of(IReadOnlyList<Era> eras, string blueprint)
    {
        string head;
        try
        {
            using var reader = new StreamReader(blueprint);
            var chars = new char[4096];
            head = new string(chars, 0, reader.ReadBlock(chars, 0, chars.Length));
        }
        catch (Exception) { return null; }
        if (Created.Match(head) is { Success: true } created && Date(created.Groups[1].Value) is { } date) return Of(eras, date);
        var named = Named.Match(head);
        return named.Success ? eras.FirstOrDefault(e => e.Name.Equals(named.Groups[1].Value, StringComparison.OrdinalIgnoreCase))?.Name : null;
    }

    static DateTime? Date(string? s) =>
        DateTime.TryParseExact(s, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
