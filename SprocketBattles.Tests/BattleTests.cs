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
        Check(old.FreeForAllSpawns == null && old.Gauntlet == null, "old battles have no added mode metadata");
        Check(!old.ToJson().Contains("freeForAllSpawns") && !old.ToJson().Contains("gauntlet"), "unused mode metadata stays out of saved battles");
        var ffa = new BattleFile { FreeForAll = true,
            FreeForAllSpawns = new FreeForAllSpawnSettings { MinSpacing = 90, MaxSpacing = 300 },
            Gauntlet = new GauntletBattleInfo { RunId = "test-run", Round = 4 } };
        var modeBack = BattleFile.FromJson(ffa.ToJson());
        Check(modeBack.FreeForAll && modeBack.FreeForAllSpawns is { Randomize: true, MinSpacing: 90, MaxSpacing: 300 }
            && modeBack.Gauntlet is { RunId: "test-run", Round: 4 }, "mode settings survive serialization and cloned player lineups");
        var spacing = new FreeForAllSpawnSettings();
        Check(spacing.Check() == null && spacing.MinSpacing == 80 && spacing.MaxSpacing == 250, "FFA random spacing defaults are usable");
        spacing.MinSpacing = 15; Check(spacing.Check() != null, "spawn minimum rejects unsafe close placements");
        spacing.MinSpacing = 500; spacing.MaxSpacing = 300; Check(spacing.Check() != null, "spawn bounds cannot be reversed");
        spacing.MaxSpacing = 2001; Check(spacing.Check() != null, "spawn spacing stays within map planning bounds");
        spacing.MinSpacing = float.NaN; Check(spacing.Check() != null, "invalid spawn values cannot reach native positions");
        spacing.Randomize = false; Check(spacing.Check() == null, "fixed authored spawns ignore unused random-range settings");

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
        Check(open.CheckPicks(new[] { ("A", 50000, (string?)"Earlywar") })!.Contains("Midwar, Latewar designs"), "from an era not taken");
        Check(open.CheckPicks(new[] { ("A", 50000, (string?)null) }) != null, "from no era, when eras are limited");
        Check(open.CheckPicks(new[] { ("A", 55000, (string?)"Midwar"), ("B", 55000, "Latewar") })!.Contains("110,000"), "over the budget together");
        Check(open.CheckPicks(new[] { ("A", 45000, (string?)"Midwar"), ("B", 55000, "Latewar") }) == null, "within every limit");
        var one = open.WithPicks(new[] { "Mine" });
        Check(one.Units.Select(u => u.Id).SequenceEqual(new[] { "u1", "u3", "u4" }) && one.Units[0].Blueprint == "Mine" && !one.Units[0].Pick
              && one.Units[0].Control == "player", "one pick: into the first slot (still yours to drive), the second slot gone");
        Check(one.Mission!.Rules.Count == 0 && open.Units.Count == 4 && open.Units[1].Pick, "the gone slot out of the rules; the battle itself unchanged");
        var arena = BattleFile.FromJson(open.ToJson());
        arena.FreeForAll = true;
        arena.Units[0].ForceStopped = true;
        var arenaRead = BattleFile.FromJson(arena.ToJson());
        Check(arenaRead.FreeForAll && arenaRead.Units[0].ForceStopped && arenaRead.Units[0].Team == arena.Units[0].Team,
              "free-for-all and force stop round-trip without changing authored spawn groups");
        Check(arena.WithPicks(new[] { "Chosen tank" }).FreeForAll && arena.WithPicks(new[] { "Chosen tank" }).Units[0].ForceStopped,
              "player choices preserve battle mode and forced stationary slot behavior");
        Check(!BattleFile.FromJson("{}").FreeForAll && !new BattleUnit().ForceStopped && !new BattleFile().ToJson().Contains("freeForAll"),
              "legacy battles keep team mode; new optional modes are omitted when off");
        arenaRead.Units[0].SetTarget(null);
        Check(arenaRead.Units[0].ForceStopped, "clearing a target does not release force stop");
        arenaRead.Units[0].AddPoint(new[] { 10f, 0, 20 });
        Check(!arenaRead.Units[0].ForceStopped, "an explicit new path releases force stop");
        arenaRead.Units[0].ForceStopped = true;
        arenaRead.Units[0].SetTarget("u4");
        Check(!arenaRead.Units[0].ForceStopped, "an explicit attack order releases force stop");
        var crowdedArena = new BattleFile { FreeForAll = true };
        for (int i = 0; i < 5; i++) crowdedArena.Units.Add(new BattleUnit { Id = "choice" + i, Pick = true });
        for (int i = 0; i < 6; i++) crowdedArena.Units.Add(new BattleUnit { Id = "fixed" + i, Team = 1 });
        Check(crowdedArena.PickCapacity == 2 && crowdedArena.Slots.Count == 5,
              "free-for-all pick capacity leaves room for every fixed contender without deleting authored positions");
        Check(crowdedArena.CheckPicks(new[] { ("A", 1, (string?)null), ("B", 1, (string?)null), ("C", 1, (string?)null) }) != null,
              "too many player choices cannot exceed the eight independent factions");
        crowdedArena.Units.Add(new BattleUnit { Id = "reserve", Team = 1, Reserve = true });
        Check(crowdedArena.PickCapacity == 1, "reserves occupy a free-for-all contender place too");
        crowdedArena.FreeForAll = false;
        Check(crowdedArena.PickCapacity == 5, "team battles keep the authored player-choice capacity");
        var picked = BattleFile.FromJson(open.ToJson());
        Check(picked.Limits!.Budget == 100000 && picked.Limits.Eras.Count == 2 && picked.Units[1].Pick && !picked.Units[2].Pick, "limits and picks read back");
        Check(open.ToJson().Split("\"pick\"").Length == 4, "pick written only where set");

        // An explicit count cap can lower the number picked, but never create more spawn slots.
        var capped = BattleFile.FromJson(open.ToJson());
        capped.Limits!.MaxTanks = 1;
        Check(capped.PickCapacity == 1 && capped.Slots.Count == 2, "a count cap limits picks without removing saved slots");
        Check(capped.CheckPicks(new[] { ("A", 45000, (string?)"Midwar"), ("B", 55000, "Latewar") })!.Contains("takes 1"), "count limit rejects picks even when their cost and era fit");
        Check(capped.CheckPicks(new[] { ("A", 45000, (string?)"Midwar") }) == null, "a pick within the count and other limits fits");
        string cappedSource = capped.ToJson();
        var cappedPlayed = capped.WithPicks(new[] { "Mine" });
        Check(cappedPlayed.Units.Count == 3 && cappedPlayed.Units[0].Id == "u1" && cappedPlayed.Units[0].Blueprint == "Mine"
              && cappedPlayed.Units.Any(u => u.Id == "u4" && u.Team == 1 && u.Pick), "count-limited picks keep slot identity and leave enemy picks alone");
        Check(capped.ToJson() == cappedSource, "applying picks never changes the original battle or its limits");
        bool overCountRejected = false;
        try { capped.WithPicks(new[] { "Mine", "Other" }); }
        catch (ArgumentException) { overCountRejected = true; }
        Check(overCountRejected && capped.ToJson() == cappedSource, "applying extra picks is rejected without changing the source");
        var cappedRead = BattleFile.FromJson(cappedSource);
        Check(cappedRead.Limits!.MaxTanks == 1 && cappedRead.PickCapacity == 1 && !cappedSource.Contains("pickCapacity"), "count limit is saved; capacity is calculated, not saved");
        cappedRead.Limits.MaxTanks = int.MaxValue;
        Check(cappedRead.PickCapacity == 2, "a count limit above the slot count is capped by available slots");
        cappedRead.Limits.MaxTanks = 0;
        Check(cappedRead.PickCapacity == 2, "zero restores the available slot count");
        var legacyPicks = BattleFile.FromJson("{\"limits\":{\"budget\":100},\"units\":[{\"id\":\"u1\",\"pick\":true},{\"id\":\"u2\",\"pick\":true},{\"team\":1,\"pick\":true}]}");
        Check(legacyPicks.Limits!.MaxTanks == 0 && legacyPicks.PickCapacity == 2
              && legacyPicks.CheckPicks(new[] { ("A", 50, (string?)null), ("B", 50, (string?)null) }) == null, "old files without a count limit still use all player slots");
        Check(new BattleFile { Limits = new PickLimits { MaxTanks = 10 } }.PickCapacity == 0, "a limit cannot create slots in an empty battle");

        // Invalid limits have useful defaults, and imported costs cannot wrap the total or reduce it below zero.
        var sanitized = BattleFile.FromJson("{\"limits\":{\"budget\":-1,\"maxCost\":-2,\"maxTanks\":-3,\"eras\":null}}").Limits!;
        Check(sanitized.Budget == 0 && sanitized.MaxCost == 0 && sanitized.MaxTanks == 0 && sanitized.Eras.Count == 0, "negative limits and null eras read as no extra limits");
        sanitized.Eras = new() { " Midwar ", "midwar", "", null!, " Latewar " };
        Check(sanitized.Eras.SequenceEqual(new[] { "Midwar", "Latewar" }), "eras are trimmed and deduplicated without changing their order");
        cappedRead.Limits.Eras = sanitized.Eras;
        Check(cappedRead.CheckPicks(new[] { ("A", 45000, (string?)" midwar ") }) == null, "era matching accepts case and whitespace differences");
        var expensive = BattleFile.FromJson(open.ToJson());
        expensive.Limits = new PickLimits { Budget = int.MaxValue };
        Check(expensive.CheckPicks(new[] { ("A", int.MaxValue, (string?)null), ("B", int.MaxValue, (string?)null) })!.Contains("together"), "the sum of large costs exceeds the budget without integer overflow");
        expensive.Limits = null;
        Check(expensive.CheckPicks(new[] { ("A", int.MaxValue, (string?)null), ("B", int.MaxValue, (string?)null) }) == null, "large costs remain valid when there is no budget");
        Check(expensive.CheckPicks(new[] { ("A", -1, (string?)null) })!.Contains("invalid cost"), "a negative cost is rejected instead of making other picks cheaper");
        Check(PickLimits.Describe(new() { "Earlywar", "Latewar" }) == "Earlywar, Latewar designs"
              && PickLimits.Describe(new()) == "any era", "non-contiguous allowed eras are named individually, never implied as a range");

        // Fewer picks retain the authored starting player, without moving slots or losing mission references to it.
        var mapped = new BattleFile { Units =
        {
            new BattleUnit { Id = "first", Pick = true, Position = new[] { 1f, 0, 1 }, Orders = new() { new BattleOrder { Type = "move", To = new[] { 9f, 0, 9 } } } },
            new BattleUnit { Id = "middle", Pick = true },
            new BattleUnit { Id = "driver", Pick = true, Control = "player", Position = new[] { 30f, 2, 40 }, Yaw = 75 },
            new BattleUnit { Id = "enemy", Team = 1 }
        } };
        mapped.Units[3].SetTarget("driver");
        mapped.Mission = new MissionData { Rules = { new Rule { When = "destroyed", Unit = "driver", Then = new RuleAction { Do = "defeat" } } } };
        mapped.Cinema = new CinemaData { Cameras = { new CameraTrack { Follow = "driver" } }, Tanks = { new TankTrack { Unit = "driver" } } };
        string mappedSource = mapped.ToJson();
        var fewer = mapped.WithPicks(new[] { "A", "B" });
        Check(fewer.Units.Select(u => u.Id).SequenceEqual(new[] { "first", "driver", "enemy" })
              && fewer.Units[0].Blueprint == "A" && fewer.Units[1].Blueprint == "B", "the later driver slot is kept and picks follow the retained slots' original order");
        Check(fewer.Units[1].Control == "player" && fewer.Units[1].Position.SequenceEqual(new[] { 30f, 2, 40 }) && fewer.Units[1].Yaw == 75
              && fewer.Units[0].Path[0][0] == 9, "retained player and AI slots keep authored control, position, rotation and orders");
        Check(fewer.Units[2].Attack?.Target == "driver" && fewer.Mission!.Rules[0].Unit == "driver"
              && fewer.Cinema!.Cameras[0].Follow == "driver" && fewer.Cinema.Tanks[0].Unit == "driver", "mission, target and cinematic references to the retained player survive");
        Check(mapped.ToJson() == mappedSource, "choosing fewer tanks leaves authored slot metadata and source references untouched");

        var reserves = new BattleFile { Units =
        {
            new BattleUnit { Id = "waiting", Pick = true, Reserve = true, Control = "player" },
            new BattleUnit { Id = "active", Pick = true, Position = new[] { 4f, 0, 5 } },
            new BattleUnit { Id = "enemy", Team = 1 }
        } };
        var activeOnly = reserves.WithPicks(new[] { "A" });
        Check(activeOnly.Units[0].Id == "active" && !activeOnly.Units[0].Reserve && activeOnly.Units[0].Control == "player"
              && activeOnly.Units[0].Position[0] == 4, "one pick uses a starting slot rather than a player marked in reserve");
        var activeAndReserve = reserves.WithPicks(new[] { "A", "B" });
        Check(activeAndReserve.Units[0].Reserve && activeAndReserve.Units[0].Control == "ai" && activeAndReserve.Units[1].Control == "player",
              "a retained reserve cannot become the starting player ahead of the active chosen tank");
        Check(reserves.Units[0].Control == "player" && reserves.Units[1].Control == "ai", "fallback control changes affect only the played copy");
        var fixedPlayer = BattleFile.FromJson(reserves.ToJson());
        fixedPlayer.Units[0].Control = "ai";
        fixedPlayer.Units.Add(new BattleUnit { Id = "fixed", Blueprint = "Fixed tank", Control = "player" });
        var fixedPlayed = fixedPlayer.WithPicks(new[] { "Reserve choice" });
        Check(fixedPlayed.Units.Single(u => u.Id == "fixed").Control == "player"
              && fixedPlayed.Units.Single(u => u.Id == "waiting").Control == "ai" && fixedPlayed.Units.Single(u => u.Id == "waiting").Reserve,
              "an explicit fixed starting player keeps control; picked reserve AI roles are not overridden");
        reserves.Units.RemoveAt(1);
        Check(reserves.CheckPicks(new[] { ("A", 1, (string?)null) })!.Contains("only reserve"), "an all-reserve player lineup gives an actionable start error");
        bool reserveStartRejected = false;
        try { reserves.WithPicks(new[] { "A" }); }
        catch (ArgumentException) { reserveStartRejected = true; }
        Check(reserveStartRejected && reserves.Units[0].Reserve, "applying all-reserve picks rejects them without silently activating an authored reserve");

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

        // Broken designs: a track belt with no segment the game has (an older save's empty one), or no pitch.
        var partDir = Path.Combine(Path.GetTempPath(), "sb-parts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(partDir);
        try
        {
            File.WriteAllText(Path.Combine(partDir, "shermanTrackBeltSegment.json"), "{ \"v\": \"0.0\", \"guid\": \"222d83dc\", \"name\": \"shermanTrackSegment\" }");
            File.WriteAllText(Path.Combine(partDir, "engine.json"), "{ \"guid\": \"not-a-segment\" }");
            var segments = Blueprints.Segments(partDir);
            Check(segments.SetEquals(new[] { "222d83dc" }), "segments: the game's segment parts only");
            string Design(string name, string belt)
            {
                var path = Path.Combine(partDir, name + ".blueprint");
                File.WriteAllText(path, "{ \"blueprints\": [ { \"type\": \"trackBelt\", \"blueprint\": { " + belt + " } }, { \"type\": \"engine\", \"blueprint\": { \"pitch\": 0 } } ] }");
                return path;
            }
            Check(Blueprints.Broken(Design("ok", "\"segmentID\": \"222d83dc\", \"x\": 510, \"pitch\": 153"), segments) == null, "a known segment with a pitch is fine (another part's pitch 0 doesn't count)");
            Check(Blueprints.Broken(Design("empty", "\"segmentID\": \"\", \"pitch\": 174"), segments)?.Contains("no track segment") == true, "an empty segment is broken");
            Check(Blueprints.Broken(Design("unknown", "\"segmentID\": \"ffff\", \"pitch\": 174"), segments) != null, "a segment the game doesn't have is broken");
            Check(Blueprints.Broken(Design("flat", "\"segmentID\": \"222d83dc\", \"pitch\": 0"), segments)?.Contains("pitch") == true, "a pitch of 0 is broken");
            Check(Blueprints.Broken(Path.Combine(partDir, "gone.blueprint"), segments) != null, "a missing file is broken");

            var brokenFirst = Design("old-save", "\"segmentID\": \"\", \"pitch\": 174");
            var unreadableCard = Design("bad-card", "\"segmentID\": \"222d83dc\", \"pitch\": 153");
            var healthy = Design("loading-tank", "\"segmentID\": \"222d83dc\", \"pitch\": 153");
            var loaded = new List<string>();
            var rejected = new List<string>();
            bool LoadCard(string path) { loaded.Add(path); return path != unreadableCard; }
            var selected = Blueprints.ChooseLoadingVehicle(new[] { brokenFirst, unreadableCard, healthy },
                p => Blueprints.Broken(p, segments), LoadCard, (p, _) => rejected.Add(p));
            Check(selected == healthy && !loaded.Contains(brokenFirst) && loaded.SequenceEqual(new[] { unreadableCard, healthy })
                  && rejected.SequenceEqual(new[] { brokenFirst, unreadableCard }),
                  "editor loading skips the old save before native loading and falls back after an unreadable card");
            loaded.Clear();
            Check(Blueprints.ChooseLoadingVehicle(new[] { brokenFirst }, p => Blueprints.Broken(p, segments), LoadCard) == null
                  && loaded.Count == 0, "with no healthy loading tank, editing fails without loading broken physics");
        }
        finally { Directory.Delete(partDir, true); }

        Console.WriteLine("BATTLE_TESTS_OK: files read back, ids unique, spawn order by design, menu settings, removal, at-spawn, action zones, objectives, picks, eras, broken designs");
    }
}
