using System.IO.Compression;
using SprocketBattles;

/// A battle shared as a zip and put in on someone else's computer (two folders standing in for the two).
static class SharingTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("sharing: " + message); }

    public static void Run()
    {
        var home = Path.Combine(Path.GetTempPath(), "sb-share-" + Guid.NewGuid().ToString("N")[..8]);
        var mine = Path.Combine(home, "me", "My Games", "Sprocket");
        var theirs = Path.Combine(home, "them", "My Games", "Sprocket");
        try
        {
            Directory.CreateDirectory(Path.Combine(mine, "Factions", "PMC", "Blueprints", "Vehicles"));
            Directory.CreateDirectory(Path.Combine(mine, "Decals"));
            File.WriteAllText(Path.Combine(mine, "Decals", "star 1.png"), "png");
            var decal = new Uri(Path.Combine(mine, "Decals", "star 1.png")).AbsoluteUri;
            File.WriteAllText(Path.Combine(mine, "Factions", "PMC", "Blueprints", "Vehicles", "T1.blueprint"),
                              $"{{\"imageURL\": \"{decal}\", \"colourMapUrl\": \"821c5c82\", " +
                              "\"other\": \"file:///C:/Users/someone/OneDrive/Dokumenty/My%20Games/Sprocket/Decals/theirs.png\"}");
            var duel = new BattleFile { Name = "Duel", Map = "Fields" };
            duel.Units.Add(new BattleUnit { Id = "u1", Blueprint = @"Factions\PMC\Blueprints\Vehicles\T1.blueprint" });
            duel.Units.Add(new BattleUnit { Id = "u2", Team = 1, Blueprint = @"Factions\PMC\Blueprints\Vehicles\T1.blueprint" });
            duel.Units.Add(new BattleUnit { Id = "u3", Team = 1, Blueprint = @"game:Blueprints\Vehicles\NTL AT Gun.blueprint" });

            var zipPath = Sharing.Export(duel, mine, Path.Combine(mine, "Battles", "Shared"));
            string inside;
            using (var z = ZipFile.OpenRead(zipPath))
                inside = string.Join("\n", z.Entries.Select(e => { using var r = new StreamReader(e.Open()); return e.FullName + ": " + r.ReadToEnd(); }));
            Check(!inside.Contains("/me/") && !inside.Contains(@"\me\") && !inside.Contains("someone"), "no one's folders (or user name) in a shared battle");
            Check(inside.Contains("designs/T1.blueprint") && inside.Contains("pictures/Decals/star 1.png"), "the design and its decal go in, once");
            Check(inside.Split("designs/T1").Length == 4, "the design's file once, named by both tanks");

            string TheirPath(string name) => Path.Combine(theirs, "Battles", name + ".json");
            var (got, _, added, _) = Sharing.Import(zipPath, theirs, TheirPath);
            var design = Path.Combine(theirs, got.Units[0].Blueprint);
            Check(added == 1 && got.Units[1].Blueprint == got.Units[0].Blueprint && File.Exists(design), "one design put in for both tanks");
            Check(got.Units[0].Blueprint.StartsWith(Path.Combine("Factions", Sharing.Faction)), "into the Shared battles faction");
            Check(File.Exists(Path.Combine(theirs, "Factions", Sharing.Faction, Sharing.Faction + ".fdef")), "the faction made like the game's");
            Check(got.Units[2].Blueprint.StartsWith("game:"), "the game's own vehicle left as it is");
            Check(File.ReadAllText(design).Contains(new Uri(Path.Combine(theirs, "Decals", "theirs.png")).AbsoluteUri), "a link into a third computer's folder made the importer's");
            Check(File.ReadAllText(design).Contains(new Uri(Path.Combine(theirs, "Decals", "star 1.png")).AbsoluteUri)
                  && File.Exists(Path.Combine(theirs, "Decals", "star 1.png")), "the decal linked from their own folder");

            var again = Sharing.Import(zipPath, theirs, TheirPath);
            Check(again.Added == 0 && again.Reused == 1 && again.Battle.Name == "Duel 2", "put in twice: the design reused, the battle named apart");
            Check(Sharing.Find(new[] { Path.Combine(mine, "Battles", "Shared"), Path.Combine(home, "nowhere") }).Count == 1, "found where it was shared");
            Check(!again.Battle.Locked, "shared plainly: editable");

            var lockedZip = Sharing.Export(duel, mine, Path.Combine(mine, "Battles", "Shared"), locked: true);
            Check(!duel.Locked, "sharing locked leaves the maker's own battle editable");
            Check(Sharing.Import(lockedZip, theirs, TheirPath).Battle.Locked, "shared locked: put in locked");
            Check(BattleFile.FromJson(duel.ToJson()).Locked == false && !duel.ToJson().Contains("locked"), "unlocked isn't written");
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
        Console.WriteLine("SHARING_TESTS_OK: no folders in the zip, designs and decals in and back, reused on a second import");
    }
}
