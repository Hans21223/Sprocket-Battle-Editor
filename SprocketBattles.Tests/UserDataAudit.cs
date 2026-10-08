using SprocketBattles;

internal static class UserDataAudit
{
    internal static void Run(string gameRoot, string documentsRoot)
    {
        string streaming = Path.Combine(gameRoot, "Sprocket_Data", "StreamingAssets");
        var segments = Blueprints.Segments(Path.Combine(streaming, "Parts"));
        int tanks = 0, battles = 0, replays = 0, rejected = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(streaming, "Blueprints", "Vehicles"), "*.blueprint"))
        {
            tanks++;
            if (Blueprints.Broken(file, segments) is { } problem)
                throw new Exception("Shipped vehicle rejected: " + Path.GetFileName(file) + ": " + problem);
        }
        var battleDir = Path.Combine(documentsRoot, "Battles");
        if (Directory.Exists(battleDir)) foreach (var file in Directory.GetFiles(battleDir, "*.json"))
        {
            try { BattleFile.FromJson(File.ReadAllText(file)); battles++; }
            catch (Exception e) { rejected++; Console.WriteLine("INVALID_SAVED_BATTLE: " + Path.GetFileName(file) + ": " + e.Message); }
        }
        string replayDir = Path.Combine(battleDir, "Replays");
        if (Directory.Exists(replayDir)) foreach (var file in Directory.GetFiles(replayDir, "*.replay.json"))
        {
            try { ReplayRecording.Read(file); replays++; }
            catch (Exception e) { rejected++; Console.WriteLine("INVALID_REPLAY: " + Path.GetFileName(file) + ": " + e.Message); }
        }
        Console.WriteLine($"USER_DATA_AUDIT_OK: {tanks} shipped blueprints, {battles} valid saved battles, {replays} valid recordings, {rejected} invalid saves, {segments.Count} known track segments; files read only");
        if (rejected > 0) throw new Exception("Existing saved battle validation needs review");
    }
}
