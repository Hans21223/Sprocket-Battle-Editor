using System.Diagnostics;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SprocketMaps;

/// A map the game builds itself from its converted OBJ (no Unity): the same objects Unity's import makes (a part per
/// group, its materials, mesh colliders except on visual-only parts and water, collision-only parts never drawn), as
/// plain runtime objects. The OBJ is read on a worker; textures, meshes and colliders are made a few milliseconds a frame.
/// `check` builds only what tanks collide with, for the import's starting-area check.
internal sealed class RuntimeMapBuild
{
    internal static bool Marker(string name) => name.StartsWith("Sprocket Spawn Area", StringComparison.Ordinal) || name.StartsWith(MapLighting.OptOut, StringComparison.Ordinal);
    internal static bool Collides(string name) => !name.StartsWith("Sprocket Visual", StringComparison.Ordinal)
        && !name.StartsWith(NativeWater.Marker, StringComparison.Ordinal) && !Marker(name);
    static bool Destructible(string name) => name.StartsWith(Destructibles.BreakableMarker, StringComparison.Ordinal) || name.StartsWith(Destructibles.TreeMarker, StringComparison.Ordinal);
    // Names Map Framework reads, named back as the Unity import does: OBJ exporters (Blender) write their spaces as underscores.
    static readonly string[] MarkerNames = { NativeWater.Marker, Destructibles.BreakableMarker, Destructibles.TreeMarker, "Sprocket Spawn Area A",
        "Sprocket Spawn Area B", MapLighting.OptOut, "Sprocket Visual", "Sprocket Collision" };
    internal static string Sanitize(string name)
    {
        foreach (var marker in MarkerNames)
        {
            var underscored = marker.Replace(' ', '_');
            if (name.StartsWith(underscored, StringComparison.Ordinal)) return marker + name[underscored.Length..].Replace('_', ' ');
        }
        return name;
    }

    readonly string input;
    readonly RuntimeMapFiles.Manifest manifest;
    readonly bool check;
    readonly Scene scene;
    readonly Vector3 offset;
    readonly CancellationTokenSource cancel = new();
    Task<(ObjModel.Scene Model, Dictionary<string, byte[]> Images)>? reading;
    ObjModel.Scene? model;
    Dictionary<string, byte[]>? images;
    IEnumerator<int>? work;
    readonly Dictionary<string, Texture2D?> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Material> materials = new(StringComparer.Ordinal);
    static Shader? sourceShader;
    internal readonly List<UnityEngine.Object> Owned = new(); // meshes, textures and the materials the map is converted from
    internal GameObject? Root;
    internal bool Done;
    internal int Parts, MissingTextures;

    internal RuntimeMapBuild(string folder, RuntimeMapFiles.Manifest manifest, bool check, Scene scene, Vector3 offset)
    { input = Path.Combine(folder, "Input"); this.manifest = manifest; this.check = check; this.scene = scene; this.offset = offset; }

    /// One frame's work; true once the map is built. Throws what went wrong.
    internal bool Step()
    {
        if (Done) return true;
        if (reading == null)
        {
            var obj = Path.Combine(input, manifest.Model); var token = cancel.Token; bool collisionOnly = check;
            reading = Task.Run(() => Read(obj, collisionOnly, token), token);
            return false;
        }
        if (!reading.IsCompleted) return false;
        if (work == null)
        {
            (model, images) = reading.GetAwaiter().GetResult();
            Root = new GameObject((check ? "Map Framework check: " : "Map: ") + manifest.DisplayName);
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(Root, scene);
            else UnityEngine.Object.DontDestroyOnLoad(Root);
            Root.transform.position = offset;
            work = Work().GetEnumerator();
        }
        // About a frame's worth (12 ms) of making objects; the loading screen keeps drawing.
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 12)
            if (!work.MoveNext())
            {
                // Unity's meshes hold the geometry now; the session keeps this object for the whole battle.
                Done = true; reading = null; work = null; model = null; images = null;
                return true;
            }
        return false;
    }

    internal void Dispose()
    {
        cancel.Cancel();
        Done = true;
        if (Root != null && Root) UnityEngine.Object.Destroy(Root);
        foreach (var item in Owned) if (item) UnityEngine.Object.Destroy(item);
        Owned.Clear();
    }

    static (ObjModel.Scene, Dictionary<string, byte[]>) Read(string obj, bool collisionOnly, CancellationToken token)
    {
        var scene = ObjModel.Read(obj, name => !collisionOnly || Collides(Sanitize(name)) && !Destructible(Sanitize(name)), token);
        foreach (var part in scene.Parts) part.Name = Sanitize(part.Name);
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!collisionOnly)
            foreach (var name in scene.Parts.SelectMany(p => p.Submeshes).Select(s => s.Material).Distinct())
                if (scene.Materials.TryGetValue(name, out var material) && material.Texture is { } file && !images.ContainsKey(file) && File.Exists(file))
                {
                    // Other formats than .smtex, PNG and JPG (TGA, PSD...) fail in Upload and count as missing.
                    token.ThrowIfCancellationRequested();
                    images[file] = File.ReadAllBytes(file);
                }
        return (scene, images);
    }

    IEnumerable<int> Work()
    {
        foreach (var file in images!.Keys.ToList())
        {
            textures[file] = Upload(file, images[file]);
            images.Remove(file); // uploaded: the texture has its own copy
            yield return 0;
        }
        // The check tries the map's own starting areas first; once checked, the starts it found stand in for them.
        bool startsGiven = manifest.StartA != null && manifest.StartB != null;
        foreach (var part in model!.Parts)
        {
            if (!startsGiven || !part.Name.StartsWith("Sprocket Spawn Area", StringComparison.Ordinal)) MakePart(part);
            yield return 0;
        }
        if (!check && manifest.StartA is { Length: 3 } a && manifest.StartB is { Length: 3 } b)
        {
            Spot("Sprocket Spawn Area A", a); Spot("Sprocket Spawn Area B", b);
        }
    }

    void Spot(string name, float[] at)
    {
        var spot = new GameObject(name);
        spot.transform.SetParent(Root!.transform, false);
        spot.transform.localPosition = new Vector3(at[0], at[1], at[2]);
    }

    void MakePart(ObjModel.Part part)
    {
        var go = new GameObject(part.Name);
        go.transform.SetParent(Root!.transform, false);
        go.transform.localPosition = new Vector3(part.CenterX, part.CenterY, part.CenterZ);
        Parts++;
        if (Marker(part.Name)) return; // a named place, not scenery
        var mesh = Mesh(part);
        Owned.Add(mesh);
        if (!check && !part.Name.StartsWith("Sprocket Collision", StringComparison.Ordinal))
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Il2CppReferenceArray<Material>(part.Submeshes.Select(s => MaterialFor(s.Material)).ToArray());
        }
        if (Collides(part.Name)) go.AddComponent<MeshCollider>().sharedMesh = mesh; // cooked here, as a loaded map's are
    }

    static Mesh Mesh(ObjModel.Part part)
    {
        int n = part.VertexCount;
        var mesh = new Mesh { name = part.Name };
        if (n > 65535) mesh.indexFormat = IndexFormat.UInt32;
        var vertices = new Il2CppStructArray<Vector3>(n);
        var normals = new Il2CppStructArray<Vector3>(n);
        var uvs = new Il2CppStructArray<Vector2>(n);
        var p = part.Positions; var q = part.Normals; var t = part.Uvs;
        for (int i = 0; i < n; i++)
        {
            vertices[i] = new Vector3(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
            normals[i] = new Vector3(q[3 * i], q[3 * i + 1], q[3 * i + 2]);
            uvs[i] = new Vector2(t[2 * i], t[2 * i + 1]);
        }
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = part.Submeshes.Count;
        for (int s = 0; s < part.Submeshes.Count; s++)
            mesh.SetIndices(new Il2CppStructArray<int>(part.Submeshes[s].Triangles), MeshTopology.Triangles, s, false, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// The material the map is converted from, as a Unity import leaves it: its colour and texture, and tags for what a
    /// fallback shader can't hold (cut out, see-through). The map's loading turns it into the game's HDRP material.
    Material MaterialFor(string name)
    {
        if (materials.TryGetValue(name, out var made)) return made;
        model!.Materials.TryGetValue(name, out var authored);
        sourceShader ??= Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/InternalErrorShader");
        var material = new Material(sourceShader!) { name = name.Length > 0 ? name : "default" };
        Owned.Add(material);
        var colour = authored == null ? Color.white : new Color(authored.R, authored.G, authored.B, authored.A);
        material.SetColor("_Color", colour);
        material.SetOverrideTag("SprocketMapInterchange", "standard-v1");
        material.SetOverrideTag("SprocketMapBaseColor", string.Join(",", new[] { colour.r, colour.g, colour.b, colour.a }.Select(c => c.ToString("R", CultureInfo.InvariantCulture))));
        if (authored?.Texture is { } file && textures.GetValueOrDefault(file) is { } texture)
        {
            material.SetTexture("_MainTex", texture);
            material.SetTextureScale("_MainTex", new Vector2(authored.ScaleU, authored.ScaleV));
            material.SetTextureOffset("_MainTex", new Vector2(authored.OffsetU, authored.OffsetV));
        }
        if (authored?.Cutout == true) { material.SetOverrideTag("SprocketMapAlphaClip", "1"); material.SetOverrideTag("SprocketMapAlphaCutoff", "0.5"); }
        else if (colour.a < .999f) material.SetOverrideTag("SprocketMapTransparent", "1");
        materials[name] = material;
        return material;
    }

    /// A texture uploaded as it is (.smtex: its own mips, DXT blocks kept) or decoded by the game (PNG, JPG), then
    /// compressed to DXT as Unity's import would. Null (an untextured surface) if it can't be read.
    unsafe Texture2D? Upload(string file, byte[] data)
    {
        try
        {
            Texture2D texture;
            if (file.EndsWith(".smtex", StringComparison.OrdinalIgnoreCase))
            {
                var image = MapTexture.Read(data);
                var format = image.Format switch { MapTexture.Format.Dxt1 => TextureFormat.DXT1, MapTexture.Format.Dxt5 => TextureFormat.DXT5, _ => TextureFormat.RGBA32 };
                texture = new Texture2D(image.Width, image.Height, format, image.Mips.Length, false);
                var all = new byte[image.Mips.Sum(m => m.Length)];
                int at = 0;
                foreach (var mip in image.Mips) { Buffer.BlockCopy(mip, 0, all, at, mip.Length); at += mip.Length; }
                fixed (byte* pointer = all) texture.LoadRawTextureData((IntPtr)pointer, all.Length);
                if (format == TextureFormat.RGBA32 && image.Width * image.Height > 4096) texture.Compress(false);
                texture.Apply(false, true);
            }
            else
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!ImageConversion.LoadImage(texture, new Il2CppStructArray<byte>(data), false))
                {
                    UnityEngine.Object.Destroy(texture);
                    if (++MissingTextures <= 3) Plugin.ModLog.LogWarning($"Map texture '{Path.GetFileName(file)}' is not a PNG or JPG; the surface stays untextured.");
                    return null;
                }
                texture.Apply(true, false);
                texture.Compress(false);
                texture.Apply(false, true);
            }
            texture.name = Path.GetFileNameWithoutExtension(file);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.anisoLevel = 4;
            Owned.Add(texture);
            return texture;
        }
        catch (Exception ex)
        {
            if (++MissingTextures <= 3) Plugin.ModLog.LogWarning($"Map texture '{Path.GetFileName(file)}' could not be used: {ex.Message}");
            return null;
        }
    }
}

/// Importing a map the game builds itself: its files converted on a worker (a Garry's Mod map to an OBJ with its own
/// DXT textures, light and sky; an OBJ, folder or archive copied), then checked in the game as Unity's import checks
/// one: its collision built well out of the way (10 km up), tank navigation, and two clear, connected starting areas
/// at 16, 8, 4, 2 or 1 tanks a side, or else open ground far apart. The result is kept in map.json, so later starts skip both.
internal sealed class RuntimeMapImport
{
    /// Maps the game can build: Garry's Mod maps and OBJ (alone, or in a folder or ZIP/7z). FBX and Unity packages need Unity.
    internal static bool Supports(string source) => SourceMaps.Is(source) || Directory.Exists(source)
        || Path.GetExtension(source).ToLowerInvariant() is ".obj" or ".zip" or ".7z";

    static readonly Vector3 CheckOffset = new(0, 10000, 0);
    readonly string source, cache;
    readonly Action<string> progress;
    readonly CancellationTokenSource cancel;
    readonly Task<(RuntimeMapFiles.Manifest Manifest, string Folder)> preparing;
    RuntimeMapFiles.Manifest? manifest;
    string folder = "";
    RuntimeMapBuild? build;
    NavMeshData? navigation;
    NavMeshDataInstance instance;
    AsyncOperation? navigationBuild;
    Bounds bounds;
    int attempt;
    string lastReason = "";
    internal RuntimeMapFiles.Manifest? Result;
    internal string Folder => folder;
    internal Exception? Failure;
    internal bool Finished => Result != null || Failure != null;
    static readonly int[] Capacities = { 16, 8, 4, 2, 1 };

    internal RuntimeMapImport(string source, string cache, Action<string> progress, Action<string> log, CancellationToken token)
    {
        this.source = Path.GetFullPath(source); this.cache = cache; this.progress = progress;
        cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = cancel.Token;
        preparing = Task.Run(() => Prepare(ct, log), ct);
    }

    (RuntimeMapFiles.Manifest, string) Prepare(CancellationToken token, Action<string> log)
    {
        progress("Checking map files");
        var fingerprint = MapImportSource.Fingerprint(source, token);
        var where = Path.Combine(cache, RuntimeMapFiles.Key(source, fingerprint));
        var file = Path.Combine(where, "map.json");
        if (RuntimeMapFiles.Ready(file, fingerprint) is { } ready) { progress("Using the previously imported map"); return (ready, where); }
        if (RuntimeMapFiles.Failed(file, fingerprint) is { } failed) throw new InvalidDataException(failed + " (Change the map's files to check it again.)");
        var input = Path.Combine(where, "Input");
        if (Directory.Exists(input)) Directory.Delete(input, true);
        Directory.CreateDirectory(where);
        progress("Converting the map for the game to build");
        MapImportSource.Stage(source, input, token, log, runtime: true);
        if (MapImportSource.Fingerprint(source, token) != fingerprint) throw new InvalidOperationException("The map changed while it was being imported. Try again after saving its files.");
        var model = RuntimeMapFiles.Model(input, !Directory.Exists(source) && Path.GetExtension(source).Equals(".obj", StringComparison.OrdinalIgnoreCase) ? source : null)
            ?? throw new NeedsUnityException("This map has no OBJ: importing FBX models and Unity packages needs Unity " + MapImportSource.UnityVersion
                + ". The game builds OBJ and Garry's Mod maps itself.");
        var created = new RuntimeMapFiles.Manifest
        {
            Source = source, Fingerprint = fingerprint, Name = MapImportSource.SceneName(source), DisplayName = MapImportSource.DisplayName(source), Model = model,
            Lighting = File.Exists(Path.Combine(input, "map.source.json")) ? "map.source.json" : null,
        };
        File.WriteAllText(file, RuntimeMapFiles.Write(created));
        return (created, where);
    }

    /// One frame of the import; the caller reads Result or Failure once Finished. The check (`inMenu`) runs only in the
    /// main menu: building collision and navigation would cost frames in a battle.
    internal void Step(bool inMenu)
    {
        if (Finished) return;
        try
        {
            if (cancel.IsCancellationRequested) throw new OperationCanceledException();
            if (!inMenu)
            {
                // A battle started mid-check: its collision and navigation go, and the check starts again in the menu.
                if (build != null) { Cleanup(); attempt = 0; lastReason = ""; }
                return;
            }
            if (manifest == null)
            {
                if (!preparing.IsCompleted) return;
                (manifest, folder) = preparing.GetAwaiter().GetResult();
                if (manifest.Capacity > 0) { Result = manifest; return; }
            }
            if (build == null)
            {
                progress("Checking tank ground and starting areas");
                build = new RuntimeMapBuild(folder, manifest, check: true, default, CheckOffset);
                return;
            }
            if (!build.Done) { build.Step(); return; }
            if (navigationBuild == null)
            {
                Physics.SyncTransforms();
                Bounds? found = null;
                foreach (var collider in build.Root!.GetComponentsInChildren<Collider>(true))
                    if (found is { } b) { b.Encapsulate(collider.bounds); found = b; } else found = collider.bounds;
                // InvalidDataException: the map's own problem, kept in map.json (below).
                bounds = found ?? throw new InvalidDataException("The map has no solid ground or geometry.");
                if (bounds.size.x < 100 || bounds.size.z < 100 || bounds.size.x > 10000 || bounds.size.z > 10000)
                    throw new InvalidDataException("Map ground must span at least 100 metres on both horizontal axes and no more than 10 kilometres.");
                // The game's tank agent, as Unity's import checks with.
                var settings = NavMesh.GetSettingsByID(0);
                settings.agentRadius = 2.5f; settings.agentHeight = 5; settings.agentSlope = 23; settings.agentClimb = .5f;
                settings.overrideVoxelSize = true; settings.voxelSize = 2.5f / 3;
                var sources = new Il2CppSystem.Collections.Generic.List<NavMeshBuildSource>();
                NavMeshBuilder.CollectSources(build.Root.transform, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
                    new Il2CppSystem.Collections.Generic.List<NavMeshBuildMarkup>(), sources);
                if (sources.Count == 0) throw new InvalidDataException("The map has no ground collision for tanks.");
                navigation = new NavMeshData(settings.agentTypeID);
                navigationBuild = NavMeshBuilder.UpdateNavMeshDataAsync(navigation, settings, sources, bounds)
                    ?? throw new InvalidOperationException("Unity did not start building tank navigation.");
                return;
            }
            if (!navigationBuild.isDone) return;
            if (!instance.valid)
            {
                instance = NavMesh.AddNavMeshData(navigation!);
                if (!instance.valid) throw new InvalidOperationException("Tank navigation could not be created for this map.");
                return;
            }
            // One capacity a frame: each search is bounded but takes a moment on a big map.
            if (attempt < Capacities.Length)
            {
                int count = Capacities[attempt++];
                try
                {
                    var (_, _, a, b) = CustomMapBridge.FindStarts(build.Root!, bounds, (ushort)count, manifest.Name);
                    Accept(count, a, b, $"In-game map check passed: '{manifest.DisplayName}', starts clear for {count} tanks a side, {Vector3.Distance(a, b):0} m apart");
                }
                catch (InvalidOperationException ex) { lastReason = ex.Message; }
                return;
            }
            // No clear, connected starting areas: the map is added anyway, starting on open ground (the Battle Editor places its own tanks).
            var (openA, openB) = CustomMapBridge.OpenGround(build.Root!, bounds);
            Accept(CustomMapBridge.TanksPerSide, openA, openB, $"In-game map check: no clear, connected starting areas on '{manifest.DisplayName}' ({lastReason}); " +
                $"battles start on open ground {Vector3.Distance(openA, openB):0} m apart");
        }
        catch (Exception ex)
        {
            Failure = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
            // The map itself fails the check: kept, so it isn't converted and checked again on every start.
            if (Failure is InvalidDataException && manifest != null)
                try { manifest.Failure = Failure.Message; File.WriteAllText(Path.Combine(folder, "map.json"), RuntimeMapFiles.Write(manifest)); }
                catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
            Cleanup();
        }
    }

    /// The check's result kept in map.json: the starting areas (map space) and how many tanks they were checked for.
    void Accept(int count, Vector3 a, Vector3 b, string log)
    {
        manifest!.Capacity = count;
        manifest.StartA = new[] { a.x - CheckOffset.x, a.y - CheckOffset.y, a.z - CheckOffset.z };
        manifest.StartB = new[] { b.x - CheckOffset.x, b.y - CheckOffset.y, b.z - CheckOffset.z };
        File.WriteAllText(Path.Combine(folder, "map.json"), RuntimeMapFiles.Write(manifest));
        Plugin.ModLog.LogInfo(log);
        Result = manifest;
        Cleanup();
    }

    internal void Cancel()
    {
        cancel.Cancel();
        if (!Finished) Failure = new OperationCanceledException();
        Cleanup();
    }

    void Cleanup()
    {
        if (instance.valid) instance.Remove();
        instance = default;
        if (navigationBuild is { isDone: false } && navigation != null && build != null)
        {
            // Unity is still building navigation from these colliders: cancelled, and both destroyed once it stops.
            NavMeshBuilder.Cancel(navigation);
            retiring.Add((navigationBuild, navigation, build));
        }
        else
        {
            if (navigation != null) UnityEngine.Object.Destroy(navigation);
            build?.Dispose();
        }
        navigation = null; navigationBuild = null; build = null;
    }

    static readonly List<(AsyncOperation Build, NavMeshData Navigation, RuntimeMapBuild Map)> retiring = new();
    /// Every frame: what a cancelled navigation build was using, once it has stopped.
    internal static void Retire()
    {
        for (int i = retiring.Count - 1; i >= 0; i--)
            if (retiring[i].Build.isDone) { UnityEngine.Object.Destroy(retiring[i].Navigation); retiring[i].Map.Dispose(); retiring.RemoveAt(i); }
    }

    internal sealed class NeedsUnityException : InvalidOperationException { internal NeedsUnityException(string message) : base(message) { } }

    /// Earlier in-game imports of the same map (older files or versions), never used again once this one is registered.
    internal static void RemoveOlder(string folder, string name)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder));
        if (parent == null) return;
        foreach (var other in Directory.GetDirectories(parent))
        {
            if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;
            try { if (RuntimeMapFiles.Read(Path.Combine(other, "map.json"))?.Name == name) Directory.Delete(other, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
