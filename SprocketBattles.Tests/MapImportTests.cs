using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SprocketMaps;

static class MapImportTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception("Map import: " + why); }
    static void Reject(Action action, string why)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Map import accepted " + why);
    }
    internal static void Run()
    {
        Check(MapImportConversion.Template().Contains("SprocketAutoMapExport"),"converter is embedded without an accidental culture satellite");
        var root = Path.Combine(Path.GetTempPath(), "SprocketMapImportTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var unsafeName in new[] { "../escape.obj", "C:/escape.obj", "/escape.obj", "sub/../../escape.obj", "sub\\..\\escape.obj", "NUL.obj", "folder/CON.meta", "x./a.obj", "folder/new\nline.obj", new string('x',256)+".obj" })
                Reject(() => MapImportSource.SafeRelative(unsafeName), unsafeName);
            Check(MapImportSource.SafeRelative("models/tank model.obj") == "models/tank model.obj", "ordinary asset names survive");
            Check(MapImportSource.SceneName(Path.Combine(root,"city.zip")) != MapImportSource.SceneName(Path.Combine(root,"other","city.zip")), "identical names in different folders have distinct IDs");
            var archiveNamedFolder=Path.Combine(root,"folder.zip"); Directory.CreateDirectory(archiveNamedFolder);
            File.WriteAllText(Path.Combine(archiveNamedFolder,"map.obj"),"v 0 0 0");
            Check(!MapImportSource.Archive(archiveNamedFolder) && MapImportSource.SourceFiles(archiveNamedFolder).Length==1,"archive-named folders retain directory semantics");
            var framedA=Path.Combine(root,"framed-a"); var framedB=Path.Combine(root,"framed-b"); Directory.CreateDirectory(framedA); Directory.CreateDirectory(framedB);
            File.WriteAllText(Path.Combine(framedA,"a.obj"),"xb.objY");
            File.WriteAllText(Path.Combine(framedB,"a.obj"),"x"); File.WriteAllText(Path.Combine(framedB,"b.obj"),"Y");
            Check(MapImportSource.Fingerprint(framedA)!=MapImportSource.Fingerprint(framedB),"file boundaries are framed in cache fingerprints");
            var selection=Path.Combine(root,"selection"); Directory.CreateDirectory(selection);
            var selected=Path.Combine(selection,"chosen.obj"); File.WriteAllText(selected,"v 0 0 0");
            File.WriteAllText(Path.Combine(selection,"larger.obj"),new string('v',1000));
            File.WriteAllText(Path.Combine(selection,"other.unity"),"another scene");
            File.WriteAllText(Path.Combine(selection,"chosen.mtl"),"newmtl authored");
            var selectedKey=MapImportSource.Fingerprint(selected);
            File.AppendAllText(Path.Combine(selection,"larger.obj"),"unrelated");
            Check(MapImportSource.Fingerprint(selected)==selectedKey,"an explicit model ignores neighboring models and scenes");
            var selectionStage=Path.Combine(root,"selection-stage"); MapImportSource.Stage(selected,selectionStage);
            Check(File.Exists(Path.Combine(selectionStage,"chosen.obj")) && File.Exists(Path.Combine(selectionStage,"chosen.mtl"))
                && !File.Exists(Path.Combine(selectionStage,"larger.obj")) && !File.Exists(Path.Combine(selectionStage,"other.unity")),"selected model and material sidecars are staged without competing scenery");
            Reject(()=>MapImportSource.Stage(selected,Path.Combine(selection,"nested-input")),"staging inside supplied scenery");
            var nested = Path.Combine(root,"nested.zip");
            using (var bytes = new MemoryStream())
            {
                using (var inner = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                {
                    WriteZip(inner,"model/map.obj","v 1 2 3\n");
                    WriteZip(inner,"model/map.mtl","newmtl Stone\n");
                    WriteZip(inner,"editor/Untrusted.cs","#error PACKAGE_CODE_MUST_NOT_RUN");
                    WriteZip(inner,"Plugins/untrusted.dll","executable bytes");
                }
                using var outer = ZipFile.Open(nested,ZipArchiveMode.Create);
                var entry = outer.CreateEntry("source/map.zip"); using var stream = entry.Open(); stream.Write(bytes.ToArray());
            }
            var before = MapImportSource.Fingerprint(nested);
            var staged = Path.Combine(root,"staged"); MapImportSource.Stage(nested,staged);
            Check(MapImportSource.Files(staged).Count() == 2 && MapImportSource.Files(staged).Any(p=>p.EndsWith("map.obj")), "nested archives retain scenery and block executable assets");
            Check(MapImportSource.Fingerprint(nested) == before,"staging leaves the supplied archive unchanged");
            var bad = Path.Combine(root,"bad.zip");
            using (var zip = ZipFile.Open(bad,ZipArchiveMode.Create)) WriteZip(zip,"../outside.obj","v 0 0 0");
            Reject(()=>MapImportSource.Stage(bad,Path.Combine(root,"bad-stage")),"archive traversal");
            Check(!File.Exists(Path.Combine(root,"outside.obj")),"traversal created no external file");
            var package = Path.Combine(root,"scene.unitypackage");
            using (var file = File.Create(package))
            using (var gzip = new GZipStream(file,CompressionMode.Compress))
            using (var tar = new TarWriter(gzip))
            {
                AddAsset(tar,new string('a',32),"Assets/City/Map.unity","scene bytes","fileFormatVersion: 2\nguid: "+new string('a',32));
                AddAsset(tar,new string('b',32),"Assets/Editor/Untrusted.cs","#error PACKAGE_CODE_MUST_NOT_RUN","guid: "+new string('b',32));
            }
            var packageStage = Path.Combine(root,"package-stage"); MapImportSource.Stage(package,packageStage);
            Check(File.ReadAllText(Path.Combine(packageStage,"City/Map.unity")) == "scene bytes", "Unity package asset paths restored");
            Check(File.ReadAllText(Path.Combine(packageStage,"City/Map.unity.meta")).Contains(new string('a',32)),"Unity package GUIDs preserved for scene references");
            Check(!MapImportSource.Files(packageStage).Any(p=>p.EndsWith(".cs")),"Unity package scripts are never passed to the editor");
            var oldKey=MapImportSource.Fingerprint(packageStage); File.AppendAllText(Path.Combine(packageStage,"City/Map.unity"),"changed");
            Check(MapImportSource.Fingerprint(packageStage)!=oldKey,"source byte changes invalidate cached conversions");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { MapImportSource.Fingerprint(nested,cancel.Token); throw new Exception("cancellation ignored"); } catch(OperationCanceledException) { }
            var budget = new MapImportSource.Budget { Bytes=MapImportSource.MaxBytes };
            Reject(()=>budget.Copy(new MemoryStream(new byte[]{1}),Path.Combine(root,"over-limit"),default),"expansion byte limit");
            var bundle = Path.Combine(root,"city.bundle"); File.WriteAllText(bundle,"bundle data");
            File.WriteAllText(Path.ChangeExtension(bundle,".json"),"{\"schemaVersion\":1,\"spawnCount\":2}");
            var report = Path.Combine(root,"ready.json");
            File.WriteAllText(report,JsonSerializer.Serialize(new { ready=true,pathComplete=true,originalsPreserved=true,originalImportersRestored=true,spawnCount=2,
                bundleSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bundle))).ToLowerInvariant() }));
            Check(MapImportConversion.Ready(report,bundle,out var capacity)&&capacity==2,"complete cache entries accepted");
            File.AppendAllText(bundle,"broken");
            Check(!MapImportConversion.Ready(report,bundle,out _),"tampered/partial cache bundle rejected");
            foreach(var malformed in new[]{"[]","{\"ready\":true}","{\"ready\":true,\"pathComplete\":true,\"originalsPreserved\":true,\"originalImportersRestored\":true,\"spawnCount\":999999999999999999999}","{\"ready\":true,\"pathComplete\":true,\"originalsPreserved\":true,\"originalImportersRestored\":true,\"spawnCount\":\"two\"}"})
            {
                File.WriteAllText(report,malformed);
                Check(!MapImportConversion.Ready(report,bundle,out _),"malformed cache metadata is rejected without exceptions");
            }
            var template=MapImportConversion.Template();
            var cache=Path.Combine(root,"cache");
            var cacheKey=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(nested).ToUpperInvariant()+MapImportSource.Fingerprint(nested)+template))).ToLowerInvariant();
            var output=Path.Combine(cache,cacheKey,"Output");Directory.CreateDirectory(output);
            var cachedBundle=Path.Combine(output,MapImportSource.SceneName(nested)+".bundle"); File.WriteAllText(cachedBundle,"valid cached bundle");
            File.WriteAllText(Path.ChangeExtension(cachedBundle,".json"),"{\"schemaVersion\":1,\"spawnCount\":2}");
            File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new {ready=true,pathComplete=true,originalsPreserved=true,originalImportersRestored=true,spawnCount=2,
                bundleSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cachedBundle))).ToLowerInvariant()}));
            var cached=MapImportConversion.Convert(nested,cache,Path.Combine(root,"Unity-not-installed.exe"),template,_=>{},default).GetAwaiter().GetResult();
            Check(cached.Bundle==cachedBundle && cached.Capacity==2,"a valid cached map loads without an installed editor");
            using(var locked=new FileStream(Path.Combine(cache,cacheKey,"conversion.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            using(var blocked=new CancellationTokenSource(100))
                try {MapImportConversion.Convert(nested,cache,"",template,_=>{},blocked.Token).GetAwaiter().GetResult();throw new Exception("cache ownership wait ignored cancellation");}catch(OperationCanceledException){}
            using(var interrupted=new CancellationTokenSource())
            {
                interrupted.Cancel();
                var cancelledTarget=Path.Combine(root,"cancelled-copy");
                try {new MapImportSource.Budget().Copy(new MemoryStream(new byte[]{1}),cancelledTarget,interrupted.Token);throw new Exception("copy ignored cancellation");}catch(OperationCanceledException){}
                Check(!File.Exists(cancelledTarget),"an already-cancelled copy creates no partial asset");
            }
            var installedEditor=MapImportConversion.FindEditor("");
            if(installedEditor!=null)
            {
                Reject(()=>MapImportConversion.Convert(bad,cache,installedEditor,template,_=>{},default).GetAwaiter().GetResult(),"unsafe archive conversion");
                Check(!Directory.EnumerateDirectories(cache,"Work-*",SearchOption.AllDirectories).Any(),"failed staging removes its temporary editor project");
                using var stopEditor=new CancellationTokenSource();
                bool launching=false;
                try
                {
                    MapImportConversion.Convert(nested,Path.Combine(root,"cancel-cache"),installedEditor,template,message=>
                    {if(message=="Converting map in the background"){launching=true;stopEditor.CancelAfter(500);}},stopEditor.Token).GetAwaiter().GetResult();
                    throw new Exception("running editor conversion ignored cancellation");
                }
                catch(OperationCanceledException) { }
                Check(launching,"subprocess cancellation exercised the editor launch phase");
                Check(!Directory.EnumerateDirectories(Path.Combine(root,"cancel-cache"),"Work-*",SearchOption.AllDirectories).Any()
                    && !Directory.EnumerateDirectories(Path.Combine(root,"cancel-cache"),"Attempt-*",SearchOption.AllDirectories).Any(),"cancelled editor exits before its project and partial exports are removed");
            }
            Console.WriteLine("MAP_IMPORT_TESTS_OK: nesting, package GUIDs, source preservation, scripts, traversal, limits, cancellation, framed identities, selected model scope, malformed cache, editor-free cache, exclusive cache ownership");
        }
        finally
        {
            var boundary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(boundary,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("SprocketMapImportTests-")) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(root,true);
        }
    }
    static void WriteZip(ZipArchive zip,string name,string value) { using var writer=new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
    static void AddAsset(TarWriter tar,string guid,string path,string bytes,string meta)
    {
        foreach(var pair in new[]{("pathname",path),("asset",bytes),("asset.meta",meta)})
        {
            using var data=new MemoryStream(Encoding.UTF8.GetBytes(pair.Item2));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile,guid+"/"+pair.Item1){DataStream=data});
        }
    }
}
