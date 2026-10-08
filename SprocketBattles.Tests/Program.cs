try
{
if (args.Length == 1 && args[0] == "--test-map-import")
{
    MapImportTests.Run();
    return;
}
if (args.Length == 2 && args[0] == "--verify-plugin")
{
    var assembly = System.Reflection.Assembly.LoadFile(Path.GetFullPath(args[1]));
    using var resource = assembly.GetManifestResourceStream("SprocketMaps.AutoMapExporter") ?? throw new Exception("Exporter resource missing from plugin");
    using var reader = new StreamReader(resource);
    if (reader.ReadToEnd() != SprocketMaps.MapImportConversion.Template()) throw new Exception("Packaged exporter differs from verified source");
    Console.WriteLine("PACKAGED_MAP_EXPORTER_OK: " + assembly.GetName().Version);
    return;
}
if (args.Length == 4 && args[0] == "--import-map")
{
    var imported = await SprocketMaps.MapImportConversion.Convert(args[1], args[2],
        args[3], SprocketMaps.MapImportConversion.Template(), Console.WriteLine, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(imported));
    return;
}
if (args.Length == 3 && args[0] == "--check-data")
{
    UserDataAudit.Run(args[1], args[2]);
    return;
}
AuditRegressionTests.Run();
MapImportTests.Run();
MapImportDiscoveryTests.Run();
ReplayTests.Run();
ReplayMediaTests.Run();
CameraEditingTests.Run();
BattleTests.Run();
DriveSimTests.Run();
SharingTests.Run();
SpawnClearanceTests.Run();
SpawnTeamMappingTests.Run();
RestartGateTests.Run();
MovementHoldTests.Run();
FreeForAllRulesTests.Run();
RandomSpawnTests.Run();
GauntletRulesTests.Run();
DesignPoolRulesTests.Run();
MapOwnershipTests.Run();
CustomMapLifetimeTests.Run();
CustomMapCapacityTests.Run();
NativeSceneLoadAbiTests.Run();
CustomMapPathTests.Run();
Console.WriteLine("ALL_TESTS_OK");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
