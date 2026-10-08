using System.IO.Compression;
using SprocketBattles;

internal static class AuditRegressionTests
{
    internal static void Run()
    {
        foreach (string json in new[] {
            "{\"units\":null}", "{\"units\":[null]}",
            "{\"units\":[{\"id\":\"u1\"},{\"id\":\"u1\"}]}",
            "{\"units\":[{\"position\":[1,2]}]}", "{\"units\":[{\"team\":2}]}",
            "{\"units\":[{\"orders\":[null]}]}", "{\"units\":[{\"orders\":[{\"type\":\"move\"}]}]}",
            "{\"mission\":{\"rules\":[{\"then\":null}]}}", "{\"mission\":{\"zones\":null}}",
            "{\"cinema\":{\"cameras\":[{\"keys\":null}]}}", "{\"cinema\":{\"tanks\":[null]}}",
            "{\"cinema\":{\"speed\":0}}", "{\"name\":null}",
            "{\"cinema\":{\"cameras\":[{\"keys\":[{\"position\":[0]}]}]}}" })
            Throws<InvalidDataException>(() => BattleFile.FromJson(json), "bad save is rejected before native code: " + json);
        var nonfinite = new BattleFile { Units = { new BattleUnit { Id = "bad", Position = new[] { float.NaN, 0, 0 } } } };
        Check(nonfinite.CheckData() != null, "in-memory nonfinite poses are rejected too");
        Check(new BattleFile { Units = { new BattleUnit() } }.CheckData(forPlay: true) != null, "missing tank identities cannot spawn");
        var unordered = BattleFile.FromJson("{\"cinema\":{\"cameras\":[{\"keys\":[{\"time\":9},{\"time\":2}]}],\"tanks\":[{\"keys\":[{\"time\":7},{\"time\":1}]}]}}");
        Check(unordered.Cinema!.Cameras[0].Keys[0].Time == 2 && unordered.Cinema.Tanks[0].Keys[0].Time == 1,
            "imported key order is chronological for playback");
        var aliases = new BattleFile { Units = {
            new BattleUnit { Id = "a", Blueprint = "Factions/PMC/T.blueprint" },
            new BattleUnit { Id = "b", Blueprint = "other.blueprint" },
            new BattleUnit { Id = "c", Blueprint = "factions\\pmc\\t.BLUEPRINT" },
            new BattleUnit { Id = "d", Team = 1, Blueprint = "Factions/PMC/T.blueprint" } } };
        Check(aliases.SpawnGroups(0).Select(g => string.Join(",", g.Select(u => u.Id))).SequenceEqual(new[] { "a,c", "b" }),
            "Windows casing and slash aliases share a definition without changing native spawn order");
        var absoluteAlias = new BattleFile { Units = { new BattleUnit { Blueprint = "relative" }, new BattleUnit { Blueprint = "absolute" } } };
        Check(absoluteAlias.SpawnGroups(0, _ => "C:\\Tanks\\T.blueprint").Single().Count == 2,
            "absolute and relative aliases use the same resolved definition");
        for (int self = 0; self < 8; self++) for (int enemy = 0; enemy < 8; enemy++)
            Check(FreeForAllRules.AreEnemies(true, 1, 1, FreeForAllRules.TeamMask(self), FreeForAllRules.TeamMask(enemy)) == (self != enemy),
                "FFA fights every independent contender even in the same spawn group");
        Check(!FreeForAllRules.AreEnemies(false, 0, 0, 1, 2) && FreeForAllRules.AreEnemies(false, 0, 1, 1, 1),
            "ordinary battles retain authored alliances");
        Check(!FreeForAllRules.AreEnemies(true, 0, 1, 0, 2), "unassigned native identities cannot receive FFA attacks");
        foreach (int capacity in new[] { 1, 2, 8, 16 })
            Check(GauntletProgression.Wave(10, 10, GauntletProgression.SpawnCapacity(capacity), 20).Count == capacity,
                "Gauntlet respects the map's actual safe capacity");
        var paid = Save(1, 0, 1);
        var restored = paid.ResumeLedger();
        Check(restored.Quote(60000, "original").Total == 0 && restored.Commit(1, 60000, "original") && restored.Credits == 40000,
            "an interrupted paid round resumes without a duplicate purchase");
        Check(!restored.Commit(1, 60000, "original"), "a resumed launch can only settle once");
        restored = paid.ResumeLedger();
        Check(restored.Quote(80000, "changed").Total == 24000 && restored.Commit(1, 80000, "changed") && restored.Credits == 16000,
            "a refit before resuming still pays upgrades and labour");
        var intermission = Save(2, 2, 2); intermission.Repair = 9000;
        restored = intermission.ResumeLedger();
        Check(restored.Round == 3 && restored.Quote(60000, "original").Total == 9000 && restored.Commit(3, 60000, "original"),
            "a saved victory resumes the next round and retains the repair bill");
        var preparation = Save(3, 2, 2);
        Check(preparation.ResumeLedger().Round == 3, "a saved preparation does not skip a round");
        Check(GauntletSaveData.FromJson("{\"Id\":\"bad\",\"Settings\":null}") == null, "invalid Gauntlet save is rejected");
        preparation.Completed = 10;
        Check(GauntletSaveData.FromJson(preparation.ToJson()) == null, "inconsistent Gauntlet finances cannot resume");
        Check(GauntletProgression.CalculateEraCosts(new[] { new Eras.Era("WWI", new DateTime(1914, 1, 1), MediumMass: 0),
            new Eras.Era("Other", new DateTime(1920, 1, 1), MediumMass: 10000) }, Array.Empty<(string, int)>())[1] > 0,
            "a zero mass in a custom era never divides by zero");
        FileAndShareChecks();
        Console.WriteLine("AUDIT_REGRESSION_TESTS_OK");
    }

    static GauntletSaveData Save(int round, int completed, int committed) => new() {
        Id = "run", Settings = new GauntletSettings { Map = "Fields" }, Round = round, Completed = completed,
        Committed = committed, Credits = 40000, TankCost = 60000, Fingerprint = "original", Capacity = 8 };

    static void FileAndShareChecks()
    {
        string home = Path.Combine(Path.GetTempPath(), "sb-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(home);
            string save = Path.Combine(home, "save.json");
            SavedFiles.Write(save, "original");
            using (File.Open(save, FileMode.Open, FileAccess.Read, FileShare.None))
                ThrowsFileFailure(() => SavedFiles.Write(save, "replacement"));
            Check(File.ReadAllText(save) == "original" && Directory.GetFiles(home, "*.tmp").Length == 0,
                "failed replacement preserves the last save and removes its staging file");
            ThrowsFileFailure(() => SavedFiles.Write(save, "duplicate", overwrite: false));
            Check(File.ReadAllText(save) == "original" && Directory.GetFiles(home, "*.tmp").Length == 0,
                "new saves never replace a conflicting name or leave staging files");
            SavedFiles.Write(save, "ทดสอบ");
            Check(File.ReadAllText(save) == "ทดสอบ", "successful save preserves Unicode");
            string brokenZip = Path.Combine(home, "broken.zip");
            using (var zip = ZipFile.Open(brokenZip, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("battle.json").Open()))
                    writer.Write(new BattleFile { Units = { new BattleUnit { Id = "u1", Blueprint = "designs/missing.blueprint" } } }.ToJson());
                using (var writer = new StreamWriter(zip.CreateEntry("pictures/Decals/test.png").Open())) writer.Write("image");
            }
            string root = Path.Combine(home, "import");
            Throws<InvalidDataException>(() => Sharing.Import(brokenZip, root, n => Path.Combine(root, "Battles", n + ".json")),
                "a package missing a design is rejected before import");
            Check(!Directory.Exists(root), "a broken shared package creates no faction, pictures or battle files");
            string a = Path.Combine(home, "FactionA", "Tank.blueprint"), b = Path.Combine(home, "FactionB", "tank.blueprint");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!); Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            File.WriteAllText(a, "{\"armour\":20}"); File.WriteAllText(b, "{\"armour\":40}");
            string beltPath = Path.Combine(home, "belt.blueprint");
            string Belt(string body) => "{\"blueprints\":[{\"type\":\"trackBelt\",\"blueprint\":{ " + body + " }}]}";
            var segments = new HashSet<string> { "known" };
            File.WriteAllText(beltPath, Belt("\"pitch\":175,\"segmentID\":\"known\""));
            Check(Blueprints.Broken(beltPath, segments) == null, "valid track fields may appear in either order");
            foreach (string body in new[] { "\"pitch\":0,\"segmentID\":\"known\"", "\"segmentID\":\"known\"",
                "\"pitch\":175", "\"pitch\":175,\"segmentID\":null", "\"pitch\":1e999,\"segmentID\":\"known\"" })
            {
                File.WriteAllText(beltPath, Belt(body));
                Check(Blueprints.Broken(beltPath, segments) != null, "missing, reordered and nonfinite track data cannot bypass the startup check");
            }
            File.WriteAllText(beltPath, "not json");
            Check(Blueprints.Broken(beltPath, segments) != null, "invalid blueprint JSON cannot reach native loading");
            var battle = new BattleFile { Name = "Case", Units = { new BattleUnit { Id = "a", Blueprint = a }, new BattleUnit { Id = "b", Blueprint = b } } };
            string exported = Sharing.Export(battle, home, home);
            using (var zip = ZipFile.OpenRead(exported))
                Check(zip.Entries.Where(e => e.FullName.StartsWith("designs/")).Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                    "same-name designs from different factions have Windows-safe archive names");
            var imported = Sharing.Import(exported, root, n => Path.Combine(root, "Battles", n + ".json"));
            Check(imported.Added == 2 && File.ReadAllText(Path.Combine(root, imported.Battle.Units[0].Blueprint)).Contains("20")
                && File.ReadAllText(Path.Combine(root, imported.Battle.Units[1].Blueprint)).Contains("40"),
                "both same-name shared tanks retain their different contents");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void ThrowsFileFailure(Action action)
    {
        try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        throw new Exception("a locked or conflicting destination must fail cleanly");
    }
    static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception(message);
    }
}
