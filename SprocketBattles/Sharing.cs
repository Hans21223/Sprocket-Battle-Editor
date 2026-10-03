using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SprocketBattles;

/// A battle shared as one .zip: the battle (battle.json), every design of yours it uses (designs/), and the paint and
/// decal pictures those use (pictures/Paint, pictures/Decals). The game's own vehicles aren't put in: everyone has them.
/// A design links its pictures by their full address in your Documents folder (file:///C:/Users/<you>/...); in the zip
/// that part is written {SPROCKET}, so no one's folders (or user name) are in it, and an import puts back the
/// importer's own. Imported designs go into a faction of their own, "Shared battles". Nothing here touches Unity, so
/// the tests run it.
public static class Sharing
{
    public const string Faction = "Shared battles";
    const string Token = "file:///{SPROCKET}/";
    static readonly Regex Link = new("\"file:///[^\"]*\"");

    /// The battle written as "<name> (Sprocket battle).zip" in `toDir`; the zip's path. `root`: Documents\My Games\Sprocket.
    public static string Export(BattleFile battle, string root, string toDir)
    {
        var copy = BattleFile.FromJson(battle.ToJson());
        Directory.CreateDirectory(toDir);
        var zipPath = Path.Combine(toDir, Clean(battle.Name) + " (Sprocket battle).zip");
        var part = zipPath + ".part";
        if (File.Exists(part)) File.Delete(part);
        using (var zip = ZipFile.Open(part, ZipArchiveMode.Create))
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // a design as saved -> its entry
            var pictures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in copy.Units)
            {
                if (unit.Blueprint.StartsWith("game:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!entries.TryGetValue(unit.Blueprint, out var entry))
                {
                    var file = Path.IsPathRooted(unit.Blueprint) ? unit.Blueprint : Path.Combine(root, unit.Blueprint);
                    if (!File.Exists(file)) throw new FileNotFoundException($"{unit.Id}'s design isn't there any more ({unit.Blueprint})");
                    var name = Path.GetFileNameWithoutExtension(file);
                    entry = $"designs/{name}.blueprint";
                    for (int n = 2; entries.ContainsValue(entry); n++) entry = $"designs/{name} ({n}).blueprint";
                    entries[unit.Blueprint] = entry;
                    Write(zip, entry, Unlink(File.ReadAllText(file), root, pictures));
                }
                unit.Blueprint = entry;
            }
            foreach (var picture in pictures)
                if (File.Exists(Path.Combine(root, picture)))
                    zip.CreateEntryFromFile(Path.Combine(root, picture), "pictures/" + picture);
            Write(zip, "battle.json", copy.ToJson());
        }
        File.Move(part, zipPath, overwrite: true);
        return zipPath;
    }

    /// A design's links to pictures in a My Games\Sprocket folder made {SPROCKET}/..., each such picture noted (put in
    /// if it's in yours). Any computer's: a design someone else made links into their folders (C:/Users/<them>/...).
    static string Unlink(string text, string root, HashSet<string> pictures) => Link.Replace(text, m =>
    {
        string local;
        try { local = Uri.UnescapeDataString(m.Value[(1 + "file:///".Length)..^1]).Replace('\\', '/'); }
        catch (Exception) { return m.Value; }
        const string Mine = "/My Games/Sprocket/";
        int at = local.IndexOf(Mine, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return m.Value; // somewhere else: left as it is
        var rel = local[(at + Mine.Length)..];
        if (rel.Split('/').Contains("..")) return m.Value;
        pictures.Add(rel);
        return "\"" + Token + string.Join("/", rel.Split('/').Select(Uri.EscapeDataString)) + "\"";
    });

    /// A shared battle put in: its pictures (those not there already), its designs (into the "Shared battles"
    /// faction; one already there with the same contents is used, not copied again), and the battle, saved under a
    /// name not taken yet (`pathFor`: a battle name's file).
    public static (BattleFile Battle, string Path, int Added, int Reused) Import(string zipPath, string root, Func<string, string> pathFor)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var battle = BattleFile.FromJson(Read(zip.GetEntry("battle.json") ?? throw new InvalidDataException("it isn't a shared battle (no battle.json in it)")));
        var full = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("pictures/") && e.Name.Length > 0))
        {
            var to = Path.GetFullPath(Path.Combine(full, e.FullName["pictures/".Length..]));
            // ponytail: a picture of the same name already there is kept as it is, even if it differs.
            if (!to.StartsWith(full, StringComparison.OrdinalIgnoreCase) || File.Exists(to)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            e.ExtractToFile(to);
        }
        var faction = Path.Combine(full, "Factions", Faction);
        var vehicles = Path.Combine(faction, "Blueprints", "Vehicles");
        Directory.CreateDirectory(vehicles);
        var fdef = Path.Combine(faction, Faction + ".fdef");
        if (!File.Exists(fdef)) File.WriteAllText(fdef, $"{{\n  \"name\": \"{Faction}\",\n  \"designPrefix\": \"\",\n  \"designCounter\": 0\n}}");
        string rootLink = new Uri(full).AbsoluteUri;
        int added = 0, reused = 0;
        var placed = new Dictionary<string, string>();
        foreach (var unit in battle.Units)
        {
            if (!unit.Blueprint.StartsWith("designs/")) continue;
            if (!placed.TryGetValue(unit.Blueprint, out var saved))
            {
                var entry = zip.GetEntry(unit.Blueprint) ?? throw new InvalidDataException($"{unit.Blueprint} is missing from it");
                var text = Read(entry).Replace(Token, rootLink);
                var name = Clean(Path.GetFileNameWithoutExtension(entry.Name));
                var to = Path.Combine(vehicles, name + ".blueprint");
                for (int n = 2; File.Exists(to) && File.ReadAllText(to) != text; n++) to = Path.Combine(vehicles, $"{name} ({n}).blueprint");
                if (File.Exists(to)) reused++;
                else { File.WriteAllText(to, text); added++; }
                placed[unit.Blueprint] = saved = Path.GetRelativePath(full, to);
            }
            unit.Blueprint = saved;
        }
        string named = string.IsNullOrWhiteSpace(battle.Name) ? "Shared battle" : battle.Name.Trim();
        string candidate = named;
        for (int n = 2; File.Exists(pathFor(candidate)); n++) candidate = $"{named} {n}";
        battle.Name = candidate;
        var path = pathFor(candidate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, battle.ToJson());
        return (battle, path, added, reused);
    }

    /// The shared battles in these folders (zips with a battle.json in them), newest first.
    public static List<string> Find(IEnumerable<string> dirs) =>
        dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.zip")).Where(IsShared).OrderByDescending(File.GetLastWriteTimeUtc).ToList();

    static bool IsShared(string zip)
    {
        try { using var z = ZipFile.OpenRead(zip); return z.GetEntry("battle.json") != null; }
        catch (Exception) { return false; }
    }

    static void Write(ZipArchive zip, string entry, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open());
        w.Write(text);
    }

    static string Read(ZipArchiveEntry entry)
    {
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }

    static string Clean(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return clean.Length == 0 ? "Shared battle" : clean;
    }
}
