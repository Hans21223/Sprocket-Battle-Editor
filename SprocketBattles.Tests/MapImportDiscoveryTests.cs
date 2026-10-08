using SprocketMaps;

internal static class MapImportDiscoveryTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception("Map discovery: " + why); }
    static MapImportDiscovery.Snapshot Snapshot(params MapImportDiscovery.Candidate[] sources) => new(sources,Array.Empty<MapImportDiscovery.Failure>());
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),"SprocketMapDiscoveryTests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var loose = Path.Combine(root,"Road.obj"); File.WriteAllText(loose,"v 1 2 3");
            File.WriteAllText(Path.Combine(root,"ignore.txt"),"not scenery");
            var prefab = Path.Combine(root,"Unity map"); Directory.CreateDirectory(prefab);
            File.WriteAllText(Path.Combine(prefab,"city.prefab"),"authored scenery");
            var archive = Path.Combine(root,"City.zip"); File.WriteAllText(archive,"converted later");
            var missing = Path.Combine(root,"missing.zip");
            var found = MapImportDiscovery.Scan(new[]{root,root.ToUpperInvariant()},missing,default);
            Check(found.Candidates.Select(c=>c.Source).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[]{loose,prefab,archive}),
                "OBJ, archive and prefab folders are discovered exactly once while unrelated files are ignored");
            Check(found.Failures.Count==1 && found.Failures[0].Source==missing,"a missing additional source does not hide valid maps");
            Check(found.Candidates.All(candidate => candidate.Stamp == LegacyStamp(candidate.Source)),
                "shared scan metadata uses exactly the previous path-length-time stamp framing");
            var stamp=MapImportDiscovery.Stamp(loose);
            File.AppendAllText(Path.Combine(root,"ignore.txt"),"changed");
            Check(MapImportDiscovery.Stamp(loose)==stamp,"unrelated documentation does not invalidate the selected model");
            File.AppendAllText(loose,"\nv 4 5 6");
            Check(MapImportDiscovery.Stamp(loose)!=stamp,"a changed source invalidates its scan stamp");
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            try { MapImportDiscovery.Scan(new[]{root},"",cancelled.Token); throw new Exception("discovery ignored cancellation"); }
            catch (OperationCanceledException) { }

            var oversized=Path.Combine(root,"many sources"); Directory.CreateDirectory(oversized);
            for(int i=0;i<MapImportDiscovery.MaxSources+40;i++) File.WriteAllText(Path.Combine(oversized,$"{i:D4}.zip"),"map");
            var bounded=MapImportDiscovery.Scan(new[]{oversized},"",default);
            Check(bounded.Candidates.Count==MapImportDiscovery.MaxSources,"oversized libraries retain a bounded valid source set");
            Check(bounded.Failures.Count==MapImportDiscovery.MaxFailures+1 && bounded.Failures[^1].Message.Contains("8 additional"),
                "excessive source failures produce bounded diagnostics and a summary");
            var shared = Path.Combine(root,"shared sidecars"); Directory.CreateDirectory(shared);
            for(int i=0;i<32;i++) File.WriteAllText(Path.Combine(shared,$"model{i:D2}.obj"),"v 0 0 0");
            for(int i=0;i<64;i++) File.WriteAllText(Path.Combine(shared,$"texture{i:D2}.mat"),"material");
            File.WriteAllText(Path.Combine(shared,"สี.mat"),"unicode material");
            var nested=Path.Combine(shared,"nested"); Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested,"detail.tga"),"texture");
            var cached=MapImportDiscovery.Scan(new[]{shared},"",default);
            Check(cached.Candidates.SequenceEqual(cached.Candidates.OrderBy(candidate=>candidate.Source,StringComparer.OrdinalIgnoreCase)),
                "parent-folder processing preserves the public source ordering");
            Check(cached.Candidates.All(candidate=>candidate.Stamp==LegacyStamp(candidate.Source)),
                "selected models and nested sidecars retain identical stamps with bounded shared caching");
            var initial=cached.Candidates.ToDictionary(candidate=>candidate.Source,candidate=>candidate.Stamp);
            File.AppendAllText(Path.Combine(shared,"texture00.mat"),"changed sidecar");
            var refreshed=MapImportDiscovery.Scan(new[]{shared},"",default);
            Check(refreshed.Candidates.All(candidate=>candidate.Stamp!=initial[candidate.Source]
                && candidate.Stamp==LegacyStamp(candidate.Source)),
                "the cache is scoped to one scan; changed shared sidecars update every affected model");
        }
        finally
        {
            var full=Path.GetFullPath(root);
            var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            Check(full.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("SprocketMapDiscoveryTests-"),"cleanup remains in the owned temporary directory");
            Directory.Delete(full,true);
        }

        var queue=new MapImportQueue();
        var original=new MapImportDiscovery.Candidate("C:/Maps/city.zip","a");
        var updated=original with {Stamp="b"};
        queue.Observe(Snapshot(original,original));
        Check(queue.Count==1 && queue.Next()==original && queue.Next()==null,"duplicate sources only convert once");
        queue.Observe(Snapshot(original));
        Check(queue.Next()==null,"an unchanged attempted source is not repeatedly converted");
        queue.Observe(Snapshot(updated));
        Check(!queue.Current(original) && queue.Current(updated) && queue.Next()==updated,"obsolete conversion results cannot replace changed sources");
        queue.Retry(updated); queue.Observe(Snapshot(updated));
        Check(queue.Next()==updated,"an unavailable editor can be retried without changing the source");
        queue.Observe(Snapshot(original)); queue.Observe(Snapshot(updated));
        Check(queue.Next()==null,"a queued stale version is removed when the latest version has already been attempted");
        queue.Observe(Snapshot());
        Check(!queue.Current(updated) && queue.Count==0,"deleted sources invalidate active results and queued work");
        queue.Observe(Snapshot(original)); queue.Clear();
        Check(queue.Next()==null && !queue.Current(original),"changing settings clears old work");
        for(int i=0;i<3000;i++) { queue.Observe(Snapshot(original)); queue.Observe(Snapshot()); }
        queue.Observe(Snapshot(updated));
        Check(queue.Count==1 && queue.Next()==updated && queue.Next()==null,"long-running source churn never repeats stale queue entries");
        queue.Observe(Snapshot(original with {Source="c:/maps/CITY.ZIP"}));
        Check(queue.Count==1,"Windows path aliases share one queue identity");

        var metadata=CustomMapManifest.Read("{\"schemaVersion\":1,\"spawnCount\":8,\"displayName\":\" Demo City (Night) \"}");
        Check(metadata.Capacity==8 && metadata.DisplayName=="Demo City (Night)","friendly names preserve authored map capacity");
        Check(CustomMapManifest.Read(null).DisplayName==null && CustomMapManifest.DisplayName("{\"schemaVersion\":1,\"spawnCount\":1}")==null,"legacy map manifests remain compatible");
        foreach(var label in new[]{"null","42","\"\"","\"bad\\nname\"","\""+new string('x',81)+"\""})
        {
            try {CustomMapManifest.Read("{\"schemaVersion\":1,\"spawnCount\":1,\"displayName\":"+label+"}");throw new Exception("invalid display name accepted");}
            catch(InvalidOperationException){}
        }
        Console.WriteLine("MAP_IMPORT_DISCOVERY_TESTS_OK");
    }
    static string LegacyStamp(string source)
    {
        using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach(var path in MapImportSource.SourceFiles(source).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase))
        {
            var file=new FileInfo(path);
            var name=System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path));
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            hash.AppendData(BitConverter.GetBytes(file.Length)); hash.AppendData(BitConverter.GetBytes(file.LastWriteTimeUtc.Ticks));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
