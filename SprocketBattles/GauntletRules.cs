using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SprocketBattles;

public sealed class GauntletSettings
{
    public int Budget { get; set; } = 100000;
    public int Rounds { get; set; } = 10;
    public string Map { get; set; } = "";
    public string Pool { get; set; } = "the game's";
    public string? EnemyFaction { get; set; }
    public string? Era { get; set; }
    public FreeForAllSpawnSettings? Spawns { get; set; }
    public string? Check() => Budget < 1000 || Budget > 10000000 ? "Starting funds must be from 1,000 to 10,000,000."
        : Rounds < 2 || Rounds > 30 ? "Choose from 2 to 30 Gauntlet rounds."
        : string.IsNullOrWhiteSpace(Map) ? "Choose a map for Gauntlet."
        : Spawns?.Check();
}

/// The campaign's finances are checked before the native loading bridge and paid once a round actually spawns.
internal sealed class GauntletLedger
{
    internal readonly record struct Bill(long Purchase, long Repair, long Upgrade, long Labour, long EraFee)
    {
        internal long Total => Purchase + Repair + Upgrade + Labour + EraFee;
    }
    internal long Credits { get; private set; }
    internal int Round { get; private set; } = 1;
    internal int Completed { get; private set; }
    internal int TankCost { get; private set; }
    internal long Repair { get; private set; }
    internal string Fingerprint { get; private set; } = "";
    internal int Committed { get; private set; }
    internal int EraRank { get; private set; }
    internal long EraStepFee { get; private set; }
    internal IReadOnlyDictionary<int, long> EraCosts { get; }
    bool resumePaidRound;

    internal GauntletLedger(int funds, int startingEraRank = 0, long? customEraStepFee = null, IReadOnlyDictionary<int, long>? eraCosts = null)
    {
        if (funds < 1000 || funds > 10000000) throw new ArgumentOutOfRangeException(nameof(funds));
        Credits = funds;
        EraRank = Math.Max(0, startingEraRank);
        EraStepFee = customEraStepFee ?? 30000;
        EraCosts = eraCosts ?? new Dictionary<int, long>();
    }

    internal GauntletLedger(int startingFunds, long credits, int round, int completed, int tankCost, long repair, string fingerprint, int committed, int eraRank, long? customEraStepFee = null, IReadOnlyDictionary<int, long>? eraCosts = null)
    {
        Credits = credits;
        Round = round;
        Completed = completed;
        TankCost = tankCost;
        Repair = repair;
        Fingerprint = fingerprint;
        Committed = committed;
        EraRank = Math.Max(0, eraRank);
        EraStepFee = customEraStepFee ?? 30000;
        EraCosts = eraCosts ?? new Dictionary<int, long>();
        resumePaidRound = committed == round && completed == round - 1;
    }

    internal long EraCost(int eraRank) =>
        EraCosts != null && EraCosts.TryGetValue(eraRank, out var c) ? c : (long)eraRank * EraStepFee;

    internal Bill Quote(int cost, string fingerprint, int targetEraRank = 0)
    {
        if (cost <= 0) throw new ArgumentOutOfRangeException(nameof(cost));
        long eraFee = targetEraRank > EraRank ? Math.Max(0, EraCost(targetEraRank) - EraCost(EraRank)) : 0;
        if (Committed == 0) return new Bill(cost, 0, 0, 0, eraFee);
        bool refit = !string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);
        return new Bill(0, Repair, Math.Max(0L, (long)cost - TankCost), refit ? (long)Math.Ceiling(cost * 0.05) : 0, eraFee);
    }

    internal bool Commit(int round, int cost, string fingerprint, int targetEraRank = 0)
    {
        if (round != Round || (Committed >= round && !resumePaidRound) || Completed != round - 1) return false;
        var bill = Quote(cost, fingerprint, targetEraRank);
        if (bill.Total > Credits) return false;
        Credits -= bill.Total;
        TankCost = cost; Fingerprint = fingerprint; Repair = 0; Committed = round;
        resumePaidRound = false;
        if (targetEraRank > EraRank) EraRank = targetEraRank;
        return true;
    }

    internal long Complete(double damage)
    {
        if (Committed != Round || Completed >= Round) return 0;
        if (!double.IsFinite(damage)) damage = 1;
        Repair = (long)Math.Ceiling(TankCost * Math.Clamp(damage, 0, 1) * 0.30);
        long reward = 10000L + Round * 2500L;
        Credits += reward; Completed = Round;
        return reward;
    }

    internal bool Advance()
    {
        if (Completed != Round) return false;
        Round++;
        return true;
    }

    internal int DesignBudget => (int)Math.Clamp(Committed == 0 ? Credits : TankCost + Credits - Repair, 1, int.MaxValue);

    // Identity and JSON formatting do not turn a saved copy into a paid refit. Tiny native float round-trips are
    // normalized; structural, equipment and paint changes remain part of the blueprint fingerprint.
    internal static string DesignFingerprint(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(doc.RootElement, writer, true);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));

        static void Write(JsonElement value, Utf8JsonWriter writer, bool root = false)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (root && p.Name.Equals("header", StringComparison.OrdinalIgnoreCase))
                        {
                            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject()
                                .FirstOrDefault(h => h.Name.Equals("creationDate", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } date)
                            { writer.WritePropertyName("creationDate"); Write(date.Value, writer); }
                            continue;
                        }
                        writer.WritePropertyName(p.Name); Write(p.Value, writer);
                    }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray(); break;
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long n)) writer.WriteNumberValue(n);
                    else
                    {
                        double rounded = Math.Round(value.GetDouble(), 5);
                        writer.WriteNumberValue(rounded == 0 ? 0d : rounded);
                    }
                    break;
                default: value.WriteTo(writer); break;
            }
        }
    }
}

internal static class GauntletProgression
{
    internal const int EconomyVersion = 2;
    // A fixed balancing reference, never the player's chosen starting funds.
    const int EraCostReference = 100000;
    internal static int SpawnCapacity(int nativeCapacity) => Math.Clamp(nativeCapacity, 1, 16);

    // Cost tiers advance across the selected pool; spawn counts are bounded by the map's native capacity.
    internal static (int Count, int Tier) Wave(int round, int rounds, int nativeCapacity, int designs)
    {
        if (round < 1 || round > rounds || rounds < 2 || nativeCapacity < 1 || designs < 1)
            throw new ArgumentOutOfRangeException(nameof(round));
        int maximum = SpawnCapacity(nativeCapacity);
        int start = Math.Min(3, maximum);
        int count = start + (round - 1) * (maximum - start) / (rounds - 1);
        int tier = (round - 1) * (designs - 1) / (rounds - 1);
        return (count, tier);
    }

    internal static List<(string Path, string Name, int Cost, int EraRank)> SortOpponents(
        IEnumerable<(string Path, string Name, int Cost, int EraRank)> opponents) =>
        opponents.OrderBy(d => d.EraRank).ThenBy(d => d.Cost).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();

    internal static Dictionary<int, long> CalculateEraCosts(
        IReadOnlyList<Eras.Era> eras,
        IEnumerable<(string Era, int Cost)> designs)
    {
        var result = new Dictionary<int, long>();
        if (eras == null || eras.Count == 0) return result;

        var designsByEra = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (designs != null)
        {
            foreach (var d in designs)
            {
                if (string.IsNullOrEmpty(d.Era) || d.Cost <= 0) continue;
                if (!designsByEra.TryGetValue(d.Era, out var list))
                    designsByEra[d.Era] = list = new List<int>();
                list.Add(d.Cost);
            }
        }

        int baselineVehicleCost = 25000;
        for (int i = 0; i < eras.Count; i++)
        {
            if (designsByEra.TryGetValue(eras[i].Name, out var list) && list.Count > 0)
            {
                list.Sort();
                baselineVehicleCost = list[list.Count / 2];
                break;
            }
        }

        int baselineMass = Math.Max(1, eras[0].MediumMass ?? 8000);
        long runningCost = 0;

        for (int i = 0; i < eras.Count; i++)
        {
            if (i == 0)
            {
                result[0] = 0;
                continue;
            }

            var era = eras[i];
            long minStepFee = (long)Math.Ceiling(EraCostReference * (0.15 + 0.10 * i));
            long baselineProgression = runningCost + minStepFee;
            long eraCalculatedFee = baselineProgression;

            // 1. Vehicle cost delta from custom or standard designs in this era
            if (designsByEra.TryGetValue(era.Name, out var list) && list.Count > 0)
            {
                list.Sort();
                int median = list[list.Count / 2];
                long vehicleDelta = Math.Max(0L, (long)median - baselineVehicleCost);
                long vehicleFee = (long)Math.Ceiling(vehicleDelta * 0.60);
                eraCalculatedFee = Math.Max(eraCalculatedFee, runningCost + vehicleFee);
            }

            // 2. Mass scaling factor if defined (e.g. modern heavier tanks)
            int mass = era.MediumMass ?? (era.HeavyMass.HasValue ? era.HeavyMass.Value / 2 : 0);
            if (mass > baselineMass)
            {
                double massRatio = (double)mass / baselineMass;
                long massFee = (long)Math.Ceiling(minStepFee * (massRatio - 1.0) * 0.50);
                eraCalculatedFee = Math.Max(eraCalculatedFee, baselineProgression + massFee);
            }

            // 3. Chronological timeline jump for modern/future eras beyond WW2 (1945)
            double yearsFromBaseline = (era.Start - eras[0].Start).TotalDays / 365.25;
            if (yearsFromBaseline > 35)
            {
                long timeFee = (long)Math.Ceiling(EraCostReference * 0.015 * (yearsFromBaseline - 35));
                eraCalculatedFee = Math.Max(eraCalculatedFee, baselineProgression + timeFee);
            }

            // 4. Explicit custom cost from era json (if present)
            if (era.CustomCost is { } custom && custom > 0)
            {
                eraCalculatedFee = Math.Max(eraCalculatedFee, (long)custom);
            }

            long costForEra = Math.Max(baselineProgression, eraCalculatedFee);
            runningCost = costForEra;
            result[i] = costForEra;
        }

        return result;
    }
}

public sealed class GauntletSaveData
{
    public int EconomyVersion { get; set; }
    public string Id { get; set; } = "";
    public GauntletSettings Settings { get; set; } = new();
    public long Credits { get; set; }
    public int Round { get; set; } = 1;
    public int Completed { get; set; }
    public int TankCost { get; set; }
    public long Repair { get; set; }
    public string Fingerprint { get; set; } = "";
    public int Committed { get; set; }
    public int EraRank { get; set; }
    public Dictionary<int, long> EraCosts { get; set; } = new();
    public string HeroPath { get; set; } = "";
    public int Capacity { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static GauntletSaveData? FromJson(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<GauntletSaveData>(json);
            return data?.Check() == null ? data : null;
        }
        catch { return null; }
    }

    public string? Check() => EconomyVersion < 0 || EconomyVersion > GauntletProgression.EconomyVersion
        || string.IsNullOrWhiteSpace(Id) || Settings == null || Settings.Check() != null
        || Round < 1 || Round > Settings.Rounds || Completed < Round - 1 || Completed > Round
        || Committed < Completed || Committed > Round || Credits < 0 || Credits > long.MaxValue / 2
        || TankCost < 0 || (Committed == 0 ? TankCost != 0 : TankCost == 0) || Repair < 0 || Repair > TankCost
        || Fingerprint == null || HeroPath == null || EraRank < 0 || EraCosts == null
        || EraCosts.Any(e => e.Key < 0 || e.Value < 0 || e.Value > long.MaxValue / 4)
        || Capacity < 0 || Capacity > 16 ? "The saved Gauntlet run has invalid progress or finances." : null;

    internal void UpdateEconomy(IReadOnlyList<Eras.Era> eras, IEnumerable<(string Era, int Cost)> designs)
    {
        if (EconomyVersion == GauntletProgression.EconomyVersion && EraCosts.Count > 0) return;
        EraCosts = GauntletProgression.CalculateEraCosts(eras, designs);
        EconomyVersion = GauntletProgression.EconomyVersion;
    }

    internal GauntletLedger ResumeLedger()
    {
        if (Check() is { } error) throw new InvalidDataException(error);
        if (Completed == Settings.Rounds) throw new InvalidOperationException("This Gauntlet run is complete.");
        var ledger = new GauntletLedger(Settings.Budget, Credits, Round, Completed, TankCost, Repair,
            Fingerprint, Committed, EraRank, eraCosts: EraCosts);
        if (Completed == Round) ledger.Advance();
        return ledger;
    }
}
