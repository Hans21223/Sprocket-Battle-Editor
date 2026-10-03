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

        Console.WriteLine("BATTLE_TESTS_OK: files read back, ids unique, spawn order by design, menu settings, removal");
    }
}
