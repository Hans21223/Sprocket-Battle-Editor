using SprocketBattles;

internal static class GauntletRulesTests
{
    internal static void Run()
    {
        var ledger = new GauntletLedger(100000);
        Check(ledger.Quote(60000, "original").Total == 60000, "first tank is purchased from starting funds");
        Check(!ledger.Commit(1, 100001, "x") && ledger.Credits == 100000, "over-budget purchase cannot spend funds");
        Check(ledger.Commit(1, 60000, "original") && ledger.Credits == 40000, "purchase is charged once");
        Check(!ledger.Commit(1, 60000, "original") && ledger.Credits == 40000, "repeated launch cannot spend twice");
        long reward = ledger.Complete(0.5);
        Check(reward == 12500 && ledger.Credits == 52500 && ledger.Repair == 9000, "win pays reward and quotes measured damage");
        Check(ledger.Complete(1) == 0 && ledger.Credits == 52500, "a repeated win never pays a second reward");
        Check(ledger.Advance() && !ledger.Advance(), "round advances only after one completed battle");
        Check(ledger.Quote(60000, "original").Total == 9000, "unchanged tank pays repairs without refit labour");
        var refit = ledger.Quote(80000, "changed");
        Check(refit.Repair == 9000 && refit.Upgrade == 20000 && refit.Labour == 4000 && refit.Total == 33000,
            "repair, upgrade and labour are separate charges");
        Check(!ledger.Commit(2, 200000, "unaffordable") && ledger.Credits == 52500, "unaffordable refit leaves ledger untouched");
        Check(ledger.Commit(2, 80000, "changed") && ledger.Credits == 19500, "refit spends only its quoted cost");
        ledger.Complete(double.NaN);
        Check(ledger.Repair == 24000, "unknown damage uses a full repair quote instead of a free repair");
        ledger.Advance();
        Check(ledger.Quote(50000, "downgrade").Upgrade == 0, "downgrading cannot manufacture a refund");
        string a = GauntletLedger.DesignFingerprint("{\"header\":{\"name\":\"A\"},\"parts\":[{\"x\":1.00000001,\"armour\":20}]}");
        string b = GauntletLedger.DesignFingerprint("{\"parts\":[{\"armour\":20,\"x\":1}],\"header\":{\"name\":\"Saved copy\"}}");
        Check(a == b, "name, property order and native float jitter do not charge a refit");
        Check(a != GauntletLedger.DesignFingerprint("{\"parts\":[{\"armour\":40,\"x\":1}]}"), "equipment edits count as refits");
        Check(GauntletLedger.DesignFingerprint("{\"x\":-0.0}") == GauntletLedger.DesignFingerprint("{\"x\":0}"), "negative zero does not create a paid refit");
        Check(GauntletLedger.DesignFingerprint("{\"header\":{\"creationDate\":\"1942\"},\"x\":0}") !=
            GauntletLedger.DesignFingerprint("{\"header\":{\"creationDate\":\"1945\"},\"x\":0}"), "changing the tank's era counts as a refit");
        int count = 0, tier = -1;
        for (int round = 1; round <= 10; round++)
        {
            var wave = GauntletProgression.Wave(round, 10, 12, 30);
            Check(wave.Count >= count && wave.Tier >= tier && wave.Count <= 12, "waves grow within native capacity");
            count = wave.Count; tier = wave.Tier;
        }
        Check(count == 12 && tier == 29, "final round uses the hardest cost tier and full safe capacity");
        Check(GauntletProgression.Wave(1, 10, 12, 30).Count == 3, "initial round starts with at least 3 opponents when capacity permits");
        Check(GauntletProgression.Wave(10, 10, 24, 30).Count == 16, "wave capacity clamps at 16");
        Check(GauntletProgression.Wave(10, 10, 2, 1) == (2, 0), "small maps and small design pools remain valid");
        Check(GauntletProgression.Wave(1, 10, 1, 1) == (1, 0), "capacity of 1 produces 1 opponent");
        Check(new GauntletSettings { Map = "Sandbox" }.Check() == null, "default run settings are valid");
        Check(new GauntletSettings { Map = "Sandbox", Budget = -1 }.Check() != null, "invalid funds are rejected");

        // Era advancement fee tests
        var eraLedger = new GauntletLedger(100000, startingEraRank: 0);
        Check(eraLedger.EraRank == 0 && eraLedger.EraStepFee == 30000, "ledger initializes with era 0 and 30k step fee");
        var quoteSameEra = eraLedger.Quote(50000, "start", targetEraRank: 0);
        Check(quoteSameEra.EraFee == 0 && quoteSameEra.Total == 50000, "same era has no era fee");
        var quoteHigherEra = eraLedger.Quote(40000, "start", targetEraRank: 1);
        Check(quoteHigherEra.EraFee == 30000 && quoteHigherEra.Total == 70000, "advancing 1 era costs 30k era fee");
        Check(eraLedger.Commit(1, 40000, "start", targetEraRank: 1) && eraLedger.EraRank == 1 && eraLedger.Credits == 30000,
            "committing higher era sets new era rank and charges era fee");
        eraLedger.Complete(0);
        eraLedger.Advance();
        var quoteUnlockedEra = eraLedger.Quote(50000, "start", targetEraRank: 0);
        Check(quoteUnlockedEra.EraFee == 0, "using earlier or already unlocked era has 0 era fee");
        var quoteAdvanceFurther = eraLedger.Quote(50000, "start", targetEraRank: 2);
        Check(quoteAdvanceFurther.EraFee == 30000, "advancing 1 further era costs 30k");

        // Opponents chronological era sorting test
        var unsorted = new[]
        {
            ("mid.bp", "MidTank", 30000, 3),
            ("ww1_heavy.bp", "Ww1Heavy", 50000, 0),
            ("ww1_light.bp", "Ww1Light", 20000, 0),
            ("early.bp", "EarlyTank", 15000, 1),
        };
        var sorted = GauntletProgression.SortOpponents(unsorted);
        Check(sorted[0].Name == "Ww1Light" && sorted[1].Name == "Ww1Heavy" && sorted[2].Name == "EarlyTank" && sorted[3].Name == "MidTank",
            "opponents sort chronologically by era rank first, then by cost");

        // Automatic custom era cost calculation tests
        var testEras = new List<Eras.Era>
        {
            new("WWI", new DateTime(1914, 7, 28), MediumMass: 8000),
            new("Interwar", new DateTime(1918, 11, 12), MediumMass: 10000),
            new("Earlywar", new DateTime(1939, 9, 2), MediumMass: 12000),
            new("AtomicCustom", new DateTime(1950, 1, 1), CustomCost: 120000),
            new("ModernCustom", new DateTime(1975, 1, 1), MediumMass: 40000),
        };
        var testDesigns = new List<(string Era, int Cost)>
        {
            ("WWI", 25000),
            ("Interwar", 35000),
            ("Earlywar", 50000),
            ("ModernCustom", 260000),
        };
        var calculatedCosts = GauntletProgression.CalculateEraCosts(testEras, testDesigns);
        Check(calculatedCosts[0] == 0, "base era cost is 0");
        Check(calculatedCosts[1] >= 25000, "interwar costs at least baseline fee (25k)");
        Check(calculatedCosts[2] >= 60000, "earlywar costs at least 2nd tier fee (60k)");
        Check(calculatedCosts[3] == 120000, "explicit custom cost is respected");
        Check(calculatedCosts[4] >= 180000, "modern custom era cost automatically scales with modern tank cost delta and timeline");
        Check(calculatedCosts[4] > calculatedCosts[3] && calculatedCosts[3] > calculatedCosts[2],
            "era costs strictly increase monotonically");
        foreach (int funds in new[] { 1000, 100000, 10000000 })
        {
            var independent = new GauntletLedger(funds);
            Check(independent.EraStepFee == 30000 && independent.Quote(500, "x", 2).EraFee == 60000,
                "era fees are independent of starting funds");
            Check(independent.Commit(1, 500, "x") && independent.Complete(0) == 12500,
                "round rewards are independent of starting funds");
            var resumed = new GauntletLedger(funds, 100000, 2, 1, 500, 0, "x", 1, 0);
            Check(resumed.EraStepFee == 30000 && resumed.Commit(2, 500, "x") && resumed.Complete(0) == 15000,
                "resumed runs also use fixed era fees and rewards");
        }

        // Save data serialization and restoration test
        var saveData = new GauntletSaveData
        {
            Id = "test-run-123",
            Credits = 65000,
            Round = 4,
            Completed = 3,
            TankCost = 42000,
            Repair = 3500,
            Fingerprint = "fp123",
            Committed = 3,
            EraRank = 2,
            EraCosts = calculatedCosts,
            HeroPath = "C:/Tanks/Hero.blueprint",
            Capacity = 12,
            Settings = new GauntletSettings { Budget = 100000, Rounds = 10, Map = "Fields" }
        };
        string json = saveData.ToJson();
        var loaded = GauntletSaveData.FromJson(json);
        Check(loaded != null && loaded.Id == "test-run-123" && loaded.Round == 4 && loaded.Credits == 65000,
            "save data round-trips through json cleanly");
        Check(loaded!.EraCosts.Count == calculatedCosts.Count && loaded.EraCosts[4] == calculatedCosts[4],
            "era costs dictionary preserved in save data");
        loaded.Settings.Budget = 10000000;
        loaded.EraCosts = new() { [0] = 0, [1] = 999999, [2] = 1999999 };
        loaded.UpdateEconomy(testEras, testDesigns);
        Check(loaded.EconomyVersion == GauntletProgression.EconomyVersion && loaded.EraCosts[3] == 120000
            && loaded.Credits == 65000 && loaded.Repair == 3500 && loaded.EraRank == 2,
            "old saved runs migrate scaled fees without changing earned credits, repair bills or unlocked eras");
        var firstSnapshot = loaded.EraCosts;
        loaded.UpdateEconomy(new List<Eras.Era>(), Array.Empty<(string, int)>());
        Check(ReferenceEquals(firstSnapshot, loaded.EraCosts), "corrected saved runs retain their campaign's era price snapshot");
        var restoredLedger = new GauntletLedger(loaded.Settings.Budget, loaded.Credits, loaded.Round, loaded.Completed,
            loaded.TankCost, loaded.Repair, loaded.Fingerprint, loaded.Committed, loaded.EraRank, eraCosts: loaded.EraCosts);
        Check(restoredLedger.Credits == 65000 && restoredLedger.EraRank == 2 && restoredLedger.Repair == 3500,
            "ledger restored correctly from save data");

        Console.WriteLine("GAUNTLET_RULES_TESTS_OK");
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
