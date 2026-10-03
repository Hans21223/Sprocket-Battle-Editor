using SprocketBattles;

/// Battle Editor files: they read back as written, ids stay unique, and a team's tanks come out in the order the game
/// spawns them (one design at a time, designs in the order first placed).
static class BattleTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("battle file: " + message); }

    public static void Run()
    {
        var file = new BattleFile { Map = "Defence" };
        void Add(int team, string design) => file.Units.Add(new BattleUnit { Id = file.NewId(), Team = team, Blueprint = design, Position = new[] { 1f, 2, 3 }, Yaw = 90 });
        Add(0, "A"); Add(1, "C"); Add(0, "B"); Add(0, "A");
        file.Units[1].Control = "player";
        file.Units[2].Orders = new() { new BattleOrder { Type = "attack", Target = "u2", Engage = "stopToFire" } };
        Check(file.Units.Select(u => u.Id).SequenceEqual(new[] { "u1", "u2", "u3", "u4" }), "ids u1..u4");
        file.Units.RemoveAt(1);
        Check(file.NewId() == "u2", "a freed id is reused");

        var back = BattleFile.FromJson(file.ToJson());
        Check(back.Map == "Defence" && back.Units.Count == 3 && back.Units[1].Orders![0].Target == "u2" && back.Units[0].Yaw == 90 && back.Units[0].Position[2] == 3,
              "reads back as written");
        Check(!file.ToJson().Contains("\"orders\": null"), "empty orders not written");

        var groups = back.SpawnGroups(0);
        Check(groups.Count == 2 && groups[0].Select(u => u.Id).SequenceEqual(new[] { "u1", "u4" }) && groups[1][0].Id == "u3", "team 0 spawns design A (u1, u4), then B (u3)");
        Check(back.SpawnGroups(1).Count == 0, "team 1 has none left");

        // Paths and targets: points stay in order before the target, and a removed tank stops being anyone's target.
        var a = back.Units[0];
        a.SetTarget("u3", "fireOnTheMove");
        a.AddPoint(new[] { 10f, 0, 0 }); a.AddPoint(new[] { 20f, 0, 0 });
        Check(a.Orders!.Select(o => o.Type).SequenceEqual(new[] { "move", "move", "attack" }) && a.Path[1][0] == 20, "path points go before the target");
        a.Path[0][0] = 15;
        Check(a.Orders[0].To![0] == 15, "moving a path point changes the saved order");
        var again = BattleFile.FromJson(back.ToJson()).Units[0];
        Check(again.Path.Count == 2 && again.Attack?.Target == "u3" && again.Attack.Engage == "fireOnTheMove", "path and target read back");
        back.Remove(back.Units.First(u => u.Id == "u3"));
        Check(a.Attack == null && a.Path.Count == 2, "a removed tank is no longer a target");
        a.RemoveLastPoint(); a.ClearPath();
        Check(a.Orders == null, "no orders left: none written");

        // The menu's settings and the cinematic read back; an older file without them gets the defaults.
        var made = new BattleFile { Name = "Dawn raid", Map = "Fields", Description = "Two tanks, one road.", Clouds = "Overcast", Fog = "Light" };
        made.Units.Add(new BattleUnit { Id = "u1", Team = 0, Blueprint = "A" });
        made.Units.Add(new BattleUnit { Id = "u2", Team = 1, Blueprint = "B" });
        made.Cinema = new CinemaData { End = 60 };
        made.Cinema.Cameras.Add(new CameraTrack { Name = "Camera 1", Follow = "u2", LookAt = "u2", Keys = { new CamKey { Time = 5 } } });
        made.Cinema.Tanks.Add(new TankTrack { Unit = "u1", Keys = { new TankKey { Time = 3, ShootAt = "u2" }, new TankKey { Time = 9, AimUnit = "u2", DriveTo = new[] { 1f, 0, 1 } } } });
        made.Cinema.Tanks.Add(new TankTrack { Unit = "u2", Keys = { new TankKey { Time = 1, Turret = 30 } } });
        var read = BattleFile.FromJson(made.ToJson());
        Check(read.Description == "Two tanks, one road." && read.Clouds == "Overcast" && read.Fog == "Light", "description and weather read back");
        Check(read.Cinema!.End == 60 && read.Cinema.Length == 60, "the cinematic's end mark sets its length");
        read.Cinema.End = null;
        Check(read.Cinema.Length == 9, "without an end mark it ends at its last key (9 s)");
        var old = BattleFile.FromJson("{\"name\":\"Old\",\"map\":\"Defence\",\"units\":[]}");
        Check(old.Clouds == "Clear" && old.Fog == "None" && old.Description == "", "an older file gets clear weather and no description");

        // A removed tank leaves no camera on it, no key aiming or shooting at it, and no track of its own.
        read.Remove(read.Units.First(u => u.Id == "u2"));
        var c = read.Cinema;
        Check(c.Cameras[0].Follow == null && c.Cameras[0].LookAt == null, "cameras no longer follow or look at it");
        Check(c.Tanks.Count == 1 && c.Tanks[0].Keys.All(k => k.ShootAt == null && k.AimUnit == null), "keys no longer aim or shoot at it, its track gone");
        Check(c.Tanks[0].Keys[1].DriveTo != null, "the rest of a key stays");

        // A quick battle's tanks stay where the game spawns them; others don't say so in the file.
        var quick = new BattleFile { Map = "Fields" };
        quick.Units.Add(new BattleUnit { Id = "u1", Blueprint = "A", AtSpawn = true });
        quick.Units.Add(new BattleUnit { Id = "u2", Team = 1, Blueprint = "B" });
        var json = quick.ToJson();
        Check(BattleFile.FromJson(json).Units[0].AtSpawn && !BattleFile.FromJson(json).Units[1].AtSpawn, "at-spawn reads back");
        Check(json.Split("atSpawn").Length == 2, "at-spawn written only where set");

        // An action left on "pick zone" goes to the zone its rule watches, else the first; one picked stays picked.
        var zoned = new MissionData();
        zoned.Zones.Add(new Zone { Id = "z1" });
        zoned.Zones.Add(new Zone { Id = "z2" });
        var enters = new Rule { When = "unitEnters", Unit = "u1", Zone = "z2", Then = new RuleAction { Do = "artillery" } };
        Check(enters.ActionZone(zoned)?.Id == "z2", "artillery with no zone picked falls on the zone entered");
        Check(new Rule { When = "time", Then = new RuleAction { Do = "artillery" } }.ActionZone(zoned)?.Id == "z1", "no zone anywhere: the first");
        enters.Then.Zone = "z1";
        Check(enters.ActionZone(zoned)?.Id == "z1", "a picked zone is kept");
        Check(new Rule().ActionZone(new MissionData()) == null, "no zones: none");

        // Objectives and fail conditions as written, several split by ";"; an older file has none (its rules say).
        var told = BattleFile.FromJson(new BattleFile { Objective = "Hold the bridge; Keep the convoy alive", Failure = "Lose the bridge" }.ToJson());
        Check(BattleFile.Lines(told.Objective).SequenceEqual(new[] { "Hold the bridge", "Keep the convoy alive" }) && told.Failure == "Lose the bridge", "objectives read back, split");
        Check(BattleFile.FromJson("{\"map\": \"Fields\"}").Objective == "", "an older file has no written objectives");

        // The player's picks: up to as many as Team 1's Pick tanks, within the limits; put in order, the rest taken out.
        var open = new BattleFile { Map = "Fields", Limits = new PickLimits { Budget = 100000, MaxCost = 60000, Eras = { "Midwar", "Latewar" } } };
        open.Units.Add(new BattleUnit { Id = "u1", Blueprint = "Stand-in", Pick = true, Control = "player" });
        open.Units.Add(new BattleUnit { Id = "u2", Blueprint = "Stand-in", Pick = true });
        open.Units.Add(new BattleUnit { Id = "u3", Blueprint = "Ally" });
        open.Units.Add(new BattleUnit { Id = "u4", Team = 1, Blueprint = "Enemy", Pick = true }); // Team 2: never the player's
        open.Units[2].SetTarget("u4");
        open.Mission = new MissionData { Rules = { new Rule { When = "destroyed", Unit = "u2", Then = new RuleAction { Do = "defeat" } } } };
        Check(open.Slots.Select(u => u.Id).SequenceEqual(new[] { "u1", "u2" }), "the slots: Team 1's Pick tanks");
        Check(open.CheckPicks(new (string, int, string?)[0]) != null, "nothing picked");
        Check(open.CheckPicks(new[] { ("A", 1, (string?)"Midwar"), ("B", 1, "Midwar"), ("C", 1, "Midwar") })!.Contains("takes 2"), "too many");
        Check(open.CheckPicks(new[] { ("A", 70000, (string?)"Midwar") })!.Contains("60,000"), "one over the cost a tank");
        Check(open.CheckPicks(new[] { ("A", 50000, (string?)"Earlywar") })!.Contains("Midwar to Latewar"), "from an era not taken");
        Check(open.CheckPicks(new[] { ("A", 50000, (string?)null) }) != null, "from no era, when eras are limited");
        Check(open.CheckPicks(new[] { ("A", 55000, (string?)"Midwar"), ("B", 55000, "Latewar") })!.Contains("110,000"), "over the budget together");
        Check(open.CheckPicks(new[] { ("A", 45000, (string?)"Midwar"), ("B", 55000, "Latewar") }) == null, "within every limit");
        var one = open.WithPicks(new[] { "Mine" });
        Check(one.Units.Select(u => u.Id).SequenceEqual(new[] { "u1", "u3", "u4" }) && one.Units[0].Blueprint == "Mine" && !one.Units[0].Pick
              && one.Units[0].Control == "player", "one pick: into the first slot (still yours to drive), the second slot gone");
        Check(one.Mission!.Rules.Count == 0 && open.Units.Count == 4 && open.Units[1].Pick, "the gone slot out of the rules; the battle itself unchanged");
        var picked = BattleFile.FromJson(open.ToJson());
        Check(picked.Limits!.Budget == 100000 && picked.Limits.Eras.Count == 2 && picked.Units[1].Pick && !picked.Units[2].Pick, "limits and picks read back");
        Check(open.ToJson().Split("\"pick\"").Length == 4, "pick written only where set");

        // Eras as the game's files have them (trailing commas and all), a custom one among them; a design's by its date.
        var eraDir = Path.Combine(Path.GetTempPath(), "sb-eras-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(eraDir);
        try
        {
            File.WriteAllText(Path.Combine(eraDir, "Midwar.json"), "{\n  \"name\": \"Midwar\",\n  \"start\": \"1942.01.01\",\n  \"playable\": true,\n}");
            File.WriteAllText(Path.Combine(eraDir, "Earlywar.json"), "{ \"name\": \"Earlywar\", \"start\": \"1939.09.02\", }");
            File.WriteAllText(Path.Combine(eraDir, "My Era.json"), "{ \"name\": \"Atomic\", \"start\": \"1950.01.01\" }");
            File.WriteAllText(Path.Combine(eraDir, "broken.json"), "not json");
            var eras = Eras.Read(eraDir);
            Check(eras.Select(e => e.Name).SequenceEqual(new[] { "Earlywar", "Midwar", "Atomic" }), "eras read oldest first, a custom one too, a broken file left out");
            Check(Eras.Of(eras, new DateTime(1941, 12, 31)) == "Earlywar" && Eras.Of(eras, new DateTime(1942, 1, 1)) == "Midwar"
                  && Eras.Of(eras, new DateTime(1960, 1, 1)) == "Atomic" && Eras.Of(eras, new DateTime(1930, 1, 1)) == null, "a date's era");
            var design = Path.Combine(eraDir, "T.blueprint");
            File.WriteAllText(design, "{\n  \"v\": \"2.0\",\n  \"header\": {\n    \"name\": \"T\",\n    \"creationDate\": \"1945.09.02\",\n  }\n}");
            var older = Path.Combine(eraDir, "Old.blueprint");
            File.WriteAllText(older, "{ \"header\": { \"name\": \"Old\", \"era\": \"midwar\" } }");
            Check(Eras.Of(eras, design) == "Midwar", "a design's era by its date (1945 is before the custom 1950 one)");
            Check(Eras.Of(eras, older) == "Midwar" && Eras.Of(eras, Path.Combine(eraDir, "none.blueprint")) == null, "an older design's by the era it names");
        }
        finally { Directory.Delete(eraDir, true); }

        Console.WriteLine("BATTLE_TESTS_OK: files read back, ids unique, spawn order by design, menu settings, removal, at-spawn, action zones, objectives, picks, eras");
    }
}
