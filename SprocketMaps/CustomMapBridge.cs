using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.GameControl;
using Sprocket.SceneManagement;
using Sprocket.SettingConfiguration;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Unity.AI.Navigation;
using UnityEngine.Rendering.HighDefinition;
using Sprocket.Gameplay;
using Sprocket.VehicleDesigner;
using Sprocket.Spawning;
using Sprocket.Vehicles.Missions;
using NativeTask = Il2CppSystem.Threading.Tasks.Task;
using NativeCompletion = Il2CppSystem.Threading.Tasks.TaskCompletionSource<Il2CppSystem.Object>;
using NativeCancellation = Il2CppSystem.Threading.CancellationToken;

namespace SprocketMaps;

/// Custom maps supply an environment, not a cloned game controller. The real native map owns player states,
/// cameras, plugins and disposal; its initialization waits until the custom environment and navigation are ready.
internal static class CustomMapBridge
{
    internal const string Carrier = "Sandbox (Low performance)";
    internal const float SpawnSpacing = 8;
    internal sealed record Definition(string Identifier, string ScenePath, AssetBundle Bundle, Maps.Map Map, CustomMapManifest.Lighting? Lighting = null);
    static readonly Dictionary<string, Definition> definitions = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    static string? disabledReason;
    static string? loadFailure;
    sealed record Request(Definition Definition, NativeCancellation? Cancellation);
    static Request? requested;
    static Session? session;
    static readonly List<Session> retired = new();
    static bool resuming;

    internal static IEnumerable<Maps.Map> ListedMaps => definitions.Values.Where(d => !failures.ContainsKey(d.Identifier)).Select(d => d.Map);
    internal static string? LoadName(string? name) => name != null && definitions.TryGetValue(name, out var d) && !failures.ContainsKey(name) ? d.Identifier : null;
    internal static string? FailureFor(string? name) => name != null && failures.TryGetValue(name, out var reason) ? reason : null;
    internal static string? NotReadyReason(string? name)
    {
        if (FailureFor(name) is { } failure) return failure;
        if (name == null || !definitions.ContainsKey(name)) return null;
        return session is { } s && s.Definition.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase)
            && s.Lifetime.CanEnterMap ? null : "The custom map is still loading.";
    }
    internal static string? TakeLoadFailure() { var reason = loadFailure; loadFailure = null; return reason; }
    internal static void Disable(string userReason)
    {
        disabledReason = userReason;
        foreach (var definition in definitions.Values) failures[definition.Identifier] = userReason;
        Cleanup();
    }

    internal static void Register(string file)
    {
        if (disabledReason != null) throw new InvalidOperationException(disabledReason);
        var manifest = Path.ChangeExtension(file, ".json");
        if (File.Exists(manifest) && new FileInfo(manifest).Length > 16384)
            throw new InvalidOperationException("The custom map manifest is too large.");
        var metadata = CustomMapManifest.Read(File.Exists(manifest) ? File.ReadAllText(manifest) : null);
        ushort capacity = metadata.Capacity;
        string? displayName = metadata.DisplayName;
        var bundle = CustomMapBundle.Open(file);
        var found = new List<(string Name, string Path)>();
        try
        {
            var paths = bundle.GetAllScenePaths();
            if (paths == null || paths.Length == 0) throw new InvalidOperationException("The map file has no playable scene.");
            var names = new HashSet<string>(definitions.Keys, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                if (string.IsNullOrWhiteSpace(path)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (MapOwnership.IsNativeMap(name)) throw new InvalidOperationException("The map scene name belongs to a built-in map.");
                if (names.Add(name)) found.Add((name, path));
            }
            if (found.Count == 0) throw new InvalidOperationException("The map file has no unique scene name.");
        }
        catch { bundle.Unload(true); throw; }
        foreach (var (name, path) in found)
        {
            var map = new Maps.Map(name, capacity, "custom map with the native game controller", Carrier,
                Description: $"Custom map: {displayName ?? name}. Up to {capacity} tanks per side. Uses the game's normal vehicle designer and battle controls.",
                DisplayName: displayName);
            definitions.Add(name, new Definition(name, path, bundle, map, metadata.Lighting));
            if (disabledReason != null) failures[name] = disabledReason;
            Plugin.ModLog.LogInfo($"Custom environment registered: '{name}', scene='{path}', native carrier='{Carrier}', bundle='{file}'");
        }
    }

    internal static bool Resolve(SceneDatabase database, string name, out SprocketScene? native)
    {
        native = null;
        return LoadName(name) != null && database.TryGet(Carrier, out native) && native != null;
    }

    internal static void Loading(MainSceneManager manager, ref string name, NativeCancellation? cancellation)
    {
        if (LoadName(name) is { } custom)
        {
            // Also protect native non-map scenes (menu/transition/designer aliases), which do not belong
            // to the explicit native map allowlist. A genuine custom lookup returns only the carrier descriptor.
            if (manager.sceneDatabase is { } database && database.TryGet(name, out var descriptor)
                && descriptor != null && descriptor.SceneName != Carrier)
            {
                NativeCollision(custom);
                Cleanup();
                return;
            }
            if (LoadName(custom) == null) { Cleanup(); return; }
            Cleanup();
            requested = new Request(definitions[custom], cancellation);
            name = Carrier;
            Plugin.ModLog.LogInfo($"Custom map request: '{custom}' through native carrier '{Carrier}'");
            return;
        }
        // A carrier name alone is a native request. Custom retries must retain the registered logical map name.
        Cleanup();
    }

    internal static void NativeCollision(string name)
    {
        failures[name] = $"The map name '{name}' is reserved by the game's own scene loader. Rename its authored scene and rebuild the map file.";
        Plugin.ModLog.LogWarning($"Custom environment name '{name}' also identifies a native scene; native scene resolution was preserved.");
    }


    internal static bool Initiating(GameController controller, SettingsProfile settings,
        Il2CppReferenceArray<Il2CppSystem.Object> arguments, ref NativeTask __result)
    {
        if (!resuming && session is { Initialized: false } current && current.Controller.Pointer == controller.Pointer)
        { __result = current.Completion.Task; return false; }
        if (resuming || requested == null || controller.gameObject.scene.name != Carrier) return true;
        var request = requested;
        requested = null;
        var next = new Session(request.Definition, controller, settings, arguments, request.Cancellation);
        session = next;
        __result = next.Completion.Task;
        return false;
    }

    internal static bool Waiting(GameController controller) => !resuming && session is { Initialized: false } s
        && s.Controller.Pointer == controller.Pointer;
    internal static Maps.Map? BuildMap(GameController controller) => session is { Prepared: true } s
        && s.Controller.Pointer == controller.Pointer ? s.BuildMap : null;

    internal static void Step()
    {
        if (requested?.Cancellation?.IsCancellationRequested == true) requested = null;
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            try { if (retired[i].DisposeEnvironment()) retired.RemoveAt(i); }
            catch (Exception ex) { Plugin.ModLog.LogError($"Cleaning up a canceled custom map: {ex}"); }
        }
        if (session is not { } s) return;
        try
        {
            if (s.Cancellation?.IsCancellationRequested == true || !s.Controller || !s.Controller.gameObject.scene.IsValid())
            { Cleanup(); return; }
            if (s.InitTask != null)
            {
                if (!s.InitTask.IsCompleted) return;
                if (s.InitTask.IsFaulted || s.InitTask.IsCanceled)
                {
                    s.Fault(s.InitTask.Exception ?? new Il2CppSystem.Exception("The native map did not start."));
                    Cleanup();
                    return;
                }
                s.Initialized = true;
                s.Succeed();
                s.InitTask = null;
                if (s.Recovering)
                {
                    var scenes = s.Controller.SceneManager;
                    if (scenes != null && scenes.TryGetFirstScene(SceneFlags.MainMenu, out var menu)) s.Controller.RequestSceneChange(menu);
                }
                else Plugin.ModLog.LogInfo($"Custom map ready: '{s.Definition.Identifier}', player={s.Controller.player != null}, mode={s.Controller.gamemode?.GetType().Name}, owned roots={s.Roots.Count}");
                return;
            }
            if (s.Initialized) return;
            if (s.Lifetime.Recovering)
            {
                if (s.DisposeEnvironment() && s.Lifetime.CanResumeNative) ResumeNative(s);
                return;
            }
            // Unity cannot cancel an additive scene load. Finish removing a previous request before starting
            // another one so identical scene paths cannot be confused with each other's owned scene.
            if (retired.Count != 0) return;
            if (Time.realtimeSinceStartup - s.Since > 120) throw new TimeoutException("The map took too long to load.");
            if (s.Operation == null)
            {
                s.StartSceneLoad();
                return;
            }
            if (s.Operation?.isDone != true) return;
            s.CaptureLoadedScene();
            if (!s.Prepared)
            {
                var scene = s.CustomScene;
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("The custom environment scene did not load.");
                s.CustomScene = scene;
                Prepare(s, scene);
                return;
            }
            if (s.NavigationOperation?.isDone == false) return;
            CompleteEnvironment(s);
            ResumeNative(s);
        }
        catch (Exception ex) { Recover(s, ex); }
    }

    static void Prepare(Session s, Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            s.Roots.Add(root);
            // Environment bundles cannot supply game code, game input, or another player/controller.
            if (root.GetComponentsInChildren<MonoBehaviour>(true).Any(b => b != null))
                throw new InvalidOperationException("This map contains scripts that are not supported by the game.");
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
                if (collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy
                    && collider.sharedMesh is { } mesh && !mesh.isReadable)
                    throw new InvalidOperationException($"The collision mesh '{mesh.name}' needs Read/Write enabled for runtime tank navigation. Rebuild this map file.");
            foreach (var camera in root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
        }
        s.Environment = new GameObject($"Custom environment: {s.Definition.Identifier}");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(s.Environment, scene);
        foreach (var root in s.Roots) root.transform.SetParent(s.Environment.transform, true);
        s.Bounds = BoundsOf(s.Roots);
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        if (!Finite(s.Bounds.min) || !Finite(s.Bounds.max) || !Finite(s.Bounds.size) || s.Bounds.size.y > 10000)
            throw new InvalidOperationException("This map has invalid ground bounds.");
        if (s.Bounds.size.x < 100 || s.Bounds.size.z < 100 || s.Bounds.size.x > 10000 || s.Bounds.size.z > 10000)
            throw new InvalidOperationException("This map has no usable battle area.");
        var destructibles = s.Destructibles = Optional("destructible scenery", () => Destructibles.Find(s.Roots)) ?? new(); // before conversion replaces the materials that mark native trees, and before navigation
        CloneMaterials(s);
        CloneTerrainMaterials(s);
        Optional("the game's water", () => NativeWater.Apply(s.Environment, s.Roots, s.Owned));
        HideCarrierEnvironment(s);
        Optional("the map's own lighting", () => MapLighting.Apply(s.Definition.Lighting, s.Controller.gameObject.scene, s.Roots, s.Environment, s.Owned, s.Undo));
        Physics.SyncTransforms();
        // Match the carrier's serialized tank agent (radius, height, climb and slope), rather than editor defaults.
        var settings = s.NativeNavigation.Count > 0 ? s.NativeNavigation[0].Item.GetBuildSettings() : NavMesh.GetSettingsByID(0);
        var sources = new Il2CppSystem.Collections.Generic.List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(s.Environment.transform, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
            new Il2CppSystem.Collections.Generic.List<NavMeshBuildMarkup>(), sources);
        if (sources.Count == 0) throw new InvalidOperationException("This map has no ground collision for tanks.");
        Optional("destructible scenery", () => Destructibles.Make(destructibles));
        s.Navigation = new NavMeshData(settings.agentTypeID);
        s.NavigationOperation = NavMeshBuilder.UpdateNavMeshDataAsync(s.Navigation, settings, sources, s.Bounds);
        if (s.NavigationOperation == null) throw new InvalidOperationException("Unity did not start building tank navigation.");
        s.Lifetime.NavigationStarted();
        s.Prepared = true;
        Plugin.ModLog.LogInfo($"Custom environment prepared: '{s.Definition.Identifier}', bounds={s.Bounds}, navigation sources={sources.Count}, scoped material clones={s.Materials.Count}");
    }

    // Extras on top of the map (water, destructibles, lighting) must never stop a map from loading. Guarding the call
    // also catches failures the feature's own code cannot, such as an interop type that fails to load.
    static void Optional(string what, Action action) => Optional<object?>(what, () => { action(); return null; });
    static T? Optional<T>(string what, Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Map loads without {what}: {ex.GetType().Name}: {ex.Message}"); return default; }
    }

    static void CompleteEnvironment(Session s)
    {
        s.NavigationInstance = NavMesh.AddNavMeshData(s.Navigation!);
        if (!s.NavigationInstance.valid) throw new InvalidOperationException("Tank navigation could not be created for this map.");
        s.Lifetime.NavigationFinished();
        var (a, d) = SpawnAnchors(s);
        s.BuildMap = s.Definition.Map with { Attackers = a, Defenders = d };
        // Merging drops the map scene's lightmaps, so its baked light went missing (Demo City's lamp-lit streets went
        // dark, 2026-10-11). A lightmapped map stays a scene of its own; cleanup unloads it when the battle ends.
        if (!s.Roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).Any(r => r.lightmapIndex is >= 0 and < 65534))
        {
            UnityEngine.SceneManagement.SceneManager.MergeScenes(s.CustomScene, s.Controller.gameObject.scene);
            s.Merged = true;
        }
        Optional("the map's light probes", MapLighting.AfterMerge);
        AdaptNativeAuthoring(s, a, d);
        Physics.SyncTransforms();
    }

    static Vector3 CustomGround(Session s, Vector2 at)
    {
        foreach (var hit in Physics.RaycastAll(new Vector3(at.x, s.Bounds.max.y + 100, at.y), Vector3.down,
            s.Bounds.size.y + 200, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            if (hit.collider != null && s.Environment != null && hit.collider.transform.IsChildOf(s.Environment.transform)
                && hit.normal.y >= 0.95f) return hit.point + Vector3.up * 0.5f;
        throw new InvalidOperationException("The native designer's starting point has no supported custom ground.");
    }

    static void AdaptNativeAuthoring(Session s, Vector2 attackers, Vector2 defenders)
    {
        var spawn = CustomGround(s, attackers);
        var toward = new Vector3(defenders.x - attackers.x, 0, defenders.y - attackers.y);
        var facing = Quaternion.LookRotation(toward);
        var markers = new HashSet<IntPtr>();
        var locators = new HashSet<IntPtr>();
        foreach (var root in s.Controller.gameObject.scene.GetRootGameObjects())
        {
            // The custom scene contains no scripts; these are the carrier's actual native components.
            foreach (var bounds in root.GetComponentsInChildren<WorldBounds>(true))
            {
                s.WorldBounds.Add((bounds, bounds.centre, bounds.size, bounds.thickness));
                bounds.centre = s.Bounds.center;
                bounds.size = s.Bounds.size + new Vector3(40, 20, 40);
                bounds.Generate();
            }
            foreach (var core in root.GetComponentsInChildren<VehicleDesignerCore>(true))
            {
                var area = core.designArea;
                var marker = area?.designTransformMarker;
                if (area == null || marker == null || marker.gameObject.scene.handle != s.Controller.gameObject.scene.handle
                    || !markers.Add(marker.Pointer)) continue;
                s.DesignerMarkers.Add((area, marker, marker.position, marker.rotation, area.transformChanged));
                marker.SetPositionAndRotation(spawn, facing);
                area.transformChanged = true;
            }
            foreach (var spawnEvent in root.GetComponentsInChildren<StageVehicleSpawnEvent>(true))
            {
                // Preserve every event/spawner and its team. Only update its referenced serialized locator;
                // the carrier's unused alternative points and any independently supplied locators remain owned by it.
                var points = spawnEvent.spawnLocator?.TryCast<SpawnPoints>();
                var spawner = spawnEvent.spawner;
                if (points == null || spawner == null || points.gameObject.scene.handle != s.Controller.gameObject.scene.handle
                    || !locators.Add(points.Pointer)) continue;
                int team = (int)spawner.teamID;
                if (team != 1 && team != 2) continue;
                if (points.transform.childCount == 0 || points.transform.childCount > 16)
                    throw new InvalidOperationException("The native game mode has no supported initial spawn locator.");
                var snapshot = new LocatorSnapshot(points);
                s.Locators.Add(snapshot);
                snapshot.LimitChildren(s.Definition.Map.Spawns, s.Environment!.transform);
                var at = team == 1 ? attackers : defenders;
                var other = team == 1 ? defenders : attackers;
                var rotation = Quaternion.LookRotation(new Vector3(other.x - at.x, 0, other.y - at.y));
                var side = rotation * Vector3.right; var back = rotation * Vector3.back;
                for (int i = 0; i < points.transform.childCount; i++)
                {
                    var offset = SpawnFormation.Offset(s.Definition.Map.Spawns, i, SpawnSpacing);
                    var delta = side * offset.Side + back * offset.Back;
                    var place = CustomGround(s, at + new Vector2(delta.x, delta.z));
                    points.transform.GetChild(i).SetPositionAndRotation(place + Vector3.up * 2, rotation);
                }
                points.random = false; points.randomOnConnectingLines = false;
                points.seed = 0; points.lastSpawnIndex = 0;
            }
        }
        Plugin.ModLog.LogInfo($"Custom native authoring: '{s.Definition.Identifier}', designer markers={s.DesignerMarkers.Count}, native spawn locators={s.Locators.Count}, world bounds={s.WorldBounds.Count}, starting point={spawn}");
    }

    sealed class LocatorSnapshot
    {
        readonly SpawnPoints points;
        readonly bool random, connecting;
        readonly int seed, last;
        readonly List<(Transform Item, Vector3 Position, Quaternion Rotation)> children = new();
        internal LocatorSnapshot(SpawnPoints points)
        {
            this.points = points; random = points.random; connecting = points.randomOnConnectingLines;
            seed = points.seed; last = points.lastSpawnIndex;
            for (int i = 0; i < points.transform.childCount; i++)
            {
                var child = points.transform.GetChild(i);
                children.Add((child, child.position, child.rotation));
            }
        }
        internal void Restore()
        {
            if (!points) return;
            points.random = random; points.randomOnConnectingLines = connecting;
            points.seed = seed; points.lastSpawnIndex = last;
            for (int i = 0; i < children.Count; i++)
            {
                var (item, position, rotation) = children[i];
                if (!item) continue;
                item.SetParent(points.transform, true);
                item.SetSiblingIndex(i);
                item.SetPositionAndRotation(position, rotation);
            }
        }
        internal void LimitChildren(int count, Transform parking)
        {
            // Keep authored marker objects alive while excluding unsupported slots from native selection.
            for (int i = count; i < children.Count; i++) children[i].Item.SetParent(parking, true);
        }
    }

    static Bounds BoundsOf(List<GameObject> roots)
    {
        Bounds? bounds = null;
        foreach (var root in roots)
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                if (bounds is { } b) { b.Encapsulate(collider.bounds); bounds = b; }
                else bounds = collider.bounds;
            }
        return bounds ?? throw new InvalidOperationException("This map has no solid ground.");
    }

    // Breakables and trees give way under tanks, so a tank may start on a bike or a sign, as the importer checked. By now
    // they are physics bodies with their colliders on: they're set aside while the starts are found.
    static (Vector2 A, Vector2 D) SpawnAnchors(Session s)
    {
        var shoved = s.Destructibles.Where(item => item.Object).SelectMany(item => item.Object.GetComponentsInChildren<Collider>(true))
            .Where(collider => collider.enabled)
            // So are the carrier's colliders still standing: its world-bounds walls are fitted to the map only after this,
            // and gm_1993 (826 m wide) had a start 14 m inside Sandbox's wall. The importer saw only the map.
            .Concat(UnityEngine.Object.FindObjectsOfType<Collider>().Where(collider => collider.enabled && !collider.isTrigger
                && !collider.transform.IsChildOf(s.Environment!.transform)))
            .ToArray();
        foreach (var collider in shoved) collider.enabled = false;
        Physics.SyncTransforms();
        try { return OpenSpawnAnchors(s); }
        finally { foreach (var collider in shoved) if (collider) collider.enabled = true; Physics.SyncTransforms(); }
    }

    static (Vector2 A, Vector2 D) OpenSpawnAnchors(Session s)
    {
        // Find two unobstructed, level areas inside the custom map, rather than using the carrier's spawn positions.
        var candidates = new List<Vector3>();
        var bounds = s.Bounds;
        using var navigation = new CustomMapPathQuery();
        var groundCache = new Dictionary<(float X, float Z), Vector3?>();
        bool GroundAt(float x, float z, out Vector3 point)
        {
            point = default;
            if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
            if (groundCache.TryGetValue((x, z), out var cached))
            { if (cached is not { } ground) return false; point = ground; return true; }
            groundCache[(x, z)] = null;
            var from = new Vector3(x, bounds.max.y + 100, z);
            if (!Physics.Raycast(from, Vector3.down, out var hit, bounds.size.y + 200, ~0, QueryTriggerInteraction.Ignore)
                || hit.collider == null || s.Environment == null || !hit.collider.transform.IsChildOf(s.Environment.transform)
                || hit.normal.y < 0.95f) return false;
            if (!navigation.Contains(hit.point)) return false;
            point = hit.point;
            groundCache[(x, z)] = point;
            return true;
        }
        // Level ground at a tank's corners; unlike GroundAt, not on tank navigation, which keeps a tank's radius from walls
        // (in a street the corners of a tank that fits fall outside it).
        bool SupportAt(float x, float z, out Vector3 point)
        {
            point = default;
            if (!Physics.Raycast(new Vector3(x, bounds.max.y + 100, z), Vector3.down, out var hit, bounds.size.y + 200, ~0, QueryTriggerInteraction.Ignore)
                || hit.collider == null || s.Environment == null || !hit.collider.transform.IsChildOf(s.Environment.transform)
                || hit.normal.y < 0.95f) return false;
            point = hit.point;
            return true;
        }
        // Teams start 100 m apart, or half the map's width on a smaller map (as the importer checked).
        float apart = Math.Min(100, 0.5f * Math.Max(bounds.size.x, bounds.size.z));
        string why = "";
        bool TeamClear(Vector3 at, Vector3 toward)
        {
            var direction = toward - at; direction.y = 0;
            var facing = Quaternion.LookRotation(direction);
            var side = facing * Vector3.right; var back = facing * Vector3.back;
            for (int i = 0; i < s.Definition.Map.Spawns; i++)
            {
                var offset = SpawnFormation.Offset(s.Definition.Map.Spawns, i, SpawnSpacing);
                var place = at + side * offset.Side + back * offset.Back;
                if (!GroundAt(place.x, place.z, out var ground)) { why = $"tank {i} at {place}: no drivable ground"; return false; }
                if (Math.Abs(ground.y - at.y) > 1) { why = $"tank {i} at {place}: ground {ground.y - at.y:0.0} m off level"; return false; }
                var blocking = Physics.OverlapBox(ground + Vector3.up * 3, new Vector3(2, 2.4f, 4.5f), facing, ~0, QueryTriggerInteraction.Ignore); // a tank's room: 4 x 9 m
                if (blocking.Length > 0) { why = $"tank {i} at {place}: space taken by '{blocking[0].name}'"; return false; }
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = place + side * (x * 2) + back * (z * 4.5f);
                        if (!SupportAt(corner.x, corner.z, out var supported) || Math.Abs(supported.y - ground.y) > 0.5f) { why = $"tank {i} at {place}: a corner has no level ground"; return false; }
                    }
            }
            return true;
        }
        Vector3 FrontRow(Vector3 center, Vector3 toward)
        {
            var forward = toward - center; forward.y = 0;
            return center + forward.normalized * SpawnFormation.FrontDistance(s.Definition.Map.Spawns, SpawnSpacing);
        }
        bool ValidPair(Vector3 p, Vector3 q, out Vector3 first, out Vector3 second)
        {
            first = FrontRow(p, q); second = FrontRow(q, p);
            if ((p - q).sqrMagnitude < apart * apart) { why = $"starts {(p - q).magnitude:0} m apart, {apart:0} m needed"; return false; }
            if (!TeamClear(first, second) || !TeamClear(second, first)) return false;
            if (navigation.Connects(p, q)) return true;
            why = "no tank route between the starts"; return false;
        }
        var transforms = s.Environment!.GetComponentsInChildren<Transform>(true);
        var authoredA = transforms.FirstOrDefault(t => t.name == "Sprocket Spawn Area A");
        var authoredD = transforms.FirstOrDefault(t => t.name == "Sprocket Spawn Area B");
        if (authoredA != null && authoredD != null)
        {
            if (GroundAt(authoredA.position.x, authoredA.position.z, out var pa) && GroundAt(authoredD.position.x, authoredD.position.z, out var pd))
            {
                if (ValidPair(pa, pd, out var startA, out var startD)) return (new Vector2(startA.x, startA.z), new Vector2(startD.x, startD.z));
            }
            else why = "a starting area has no drivable ground";
            Plugin.ModLog.LogWarning($"'{s.Definition.Identifier}': the map's starting areas don't pass here ({why}); searching the map instead.");
        }
        var min = bounds.min; var max = bounds.max;
        for (int z = 1; z <= 31; z++)
            for (int x = 1; x <= 31; x++)
            {
                float px = Mathf.Lerp(min.x, max.x, x / 32f), pz = Mathf.Lerp(min.z, max.z, z / 32f);
                if (!GroundAt(px, pz, out var ground)) continue;
                float half = Math.Max(SpawnFormation.ColumnCount(s.Definition.Map.Spawns), SpawnFormation.RowCount(s.Definition.Map.Spawns)) * SpawnSpacing / 2;
                if (Physics.CheckBox(ground + Vector3.up * 3, new Vector3(half, 2.4f, half), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)) continue;
                candidates.Add(ground);
            }
        if (candidates.Count < 2) throw new InvalidOperationException("This map needs two open spawn areas large enough for its declared tank capacity.");
        var a = candidates[0]; var d = candidates[1]; float distance = 0;
        var pairs = CustomMapNavigation.FarthestPairs(candidates.Select(point =>
            new CustomMapNavigation.Vertex(point.x, point.y, point.z)).ToArray(), 512, apart * apart);
        // Bound expensive physics/path queries; a large city with disconnected roofs must not freeze the UI.
        foreach (var pair in pairs)
            if (ValidPair(candidates[pair.First], candidates[pair.Second], out var first, out var second))
            { a = first; d = second; distance = pair.DistanceSquared; break; }
        if (distance < apart * apart) throw new InvalidOperationException($"This map needs two connected, clear team spawn formations at least {apart:0} metres apart.");
        return (new Vector2(a.x, a.z), new Vector2(d.x, d.z));
    }

    static void HideCarrierEnvironment(Session s)
    {
        foreach (var root in s.Controller.gameObject.scene.GetRootGameObjects())
        {
            foreach (var surface in root.GetComponentsInChildren<NavMeshSurface>(true))
            {
                if (Protected(surface)) continue;
                s.NativeNavigation.Add((surface, surface.enabled, surface.navMeshDataInstance.valid));
                surface.RemoveData();
                surface.enabled = false;
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (Protected(renderer)) continue;
                s.Renderers.Add((renderer, renderer.enabled)); renderer.enabled = false;
            }
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
            { s.Terrains.Add((terrain, terrain.enabled)); terrain.enabled = false; }
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger || Protected(collider)) continue;
                s.Colliders.Add((collider, collider.enabled)); collider.enabled = false;
            }
        }
    }

    static bool Protected(Component component) => component.GetComponentInParent<GameController>() != null
        || component.GetComponentInParent<Camera>() != null || component.GetComponentInParent<Canvas>() != null
        || component.GetComponentInParent<Sprocket.PlayerControl.Player>() != null || component.GetComponentInParent<GameMode>() != null
        || component.GetComponentInParent<WorldBounds>() != null;

    // A player build only has the shader variants its own materials use, and Unity silently substitutes the nearest one.
    // The game's HDRP/Lit has no alpha-test or double-sided variant (cutout leaves drew as solid cards) and one
    // transparent variant, a parallax one. Each clone gets exactly one of these keyword sets.
    static readonly string[] OpaqueKeywords = { "_NORMALMAP_TANGENT_SPACE", "_DISABLE_SSR_TRANSPARENT" };
    static readonly string[] MappedKeywords = { "_NORMALMAP", "_MASKMAP", "_NORMALMAP_TANGENT_SPACE", "_DISABLE_SSR_TRANSPARENT" };
    static readonly string[] BlendedKeywords = { "_SURFACE_TYPE_TRANSPARENT", "_ENABLE_FOG_ON_TRANSPARENT", "_DISABLE_SSR_TRANSPARENT",
        "_NORMALMAP", "_MASKMAP", "_NORMALMAP_TANGENT_SPACE", "_HEIGHTMAP", "_PIXEL_DISPLACEMENT",
        "_PIXEL_DISPLACEMENT_LOCK_OBJECT_SCALE", "_DISPLACEMENT_LOCK_TILING_SCALE" };

    static void CloneMaterials(Session s)
    {
        // Map Framework's own HDRP/Lit has every variant these materials need. Without it: the game's HDRP/Lit, with
        // cutouts on the game's foliage shader (loaded with the carrier scene) or, failing that, alpha-blended.
        MapShaders.Load();
        var full = MapShaders.Lit;
        var lit = full ?? Shader.Find("HDRP/Lit") ?? throw new InvalidOperationException("The game's map lighting shader was not available.");
        var foliage = full == null ? Shader.Find(FoliageShader) : null;
        int blendedCount = 0, cutoutCount = 0;
        var clones = new Dictionary<IntPtr, Material>();
        foreach (var root in s.Roots)
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true).Concat(TerrainDetailRenderers(root)))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var old = materials[i];
                    if (!old) continue;
                    var shader = old.shader?.name ?? "";
                    if (shader.StartsWith("HDRP/", StringComparison.Ordinal)) continue;
                    bool nativePreset = old.GetTag("SprocketMapNativePreset", false, "") == "1";
                    if (nativePreset
                        && !clones.ContainsKey(old.Pointer) && NativeSceneryMaterial.TryClone(old, out var native))
                    {
                        s.Materials.Add(native);
                        clones.Add(old.Pointer, native);
                    }
                    if (!clones.TryGetValue(old.Pointer, out var clone))
                    {
                        // A fallback shader retains raw texture bindings, but color and scalar getters
                        // return zero without shader properties. Explicit metadata preserves those values.
                        bool interchange = old.GetTag("SprocketMapInterchange", false, "") == "standard-v1"
                            || old.GetTag("SprocketMapAlphaClip", false, "") == "1"
                            || old.GetTag("SprocketMapMaskMap", false, "") == "1"
                            || old.GetTag("SprocketMapTransparent", false, "") == "1";
                        var color = !nativePreset && old.HasProperty("_BaseColor") ? old.GetColor("_BaseColor")
                            : ReadMaterialColor(old, "_Color", "SprocketMapBaseColor", Color.white, nativePreset);
                        var texture = old.HasProperty("_BaseMap") ? old.GetTexture("_BaseMap") : old.HasProperty("_MainTex") || interchange ? old.GetTexture("_MainTex") : null;
                        var normal = old.HasProperty("_BumpMap") || interchange ? old.GetTexture("_BumpMap") : null;
                        var mask = old.HasProperty("_MetallicGlossMap") || interchange ? old.GetTexture("_MetallicGlossMap") : null;
                        var emission = ReadMaterialColor(old, "_EmissionColor", "SprocketMapEmissionColor", Color.black, nativePreset);
                        var emissionMap = old.HasProperty("_EmissionMap") || interchange ? old.GetTexture("_EmissionMap") : null;
                        float metallic = ReadMaterialFloat(old, "_Metallic", "SprocketMapMetallic", 0, nativePreset);
                        float smoothness = !nativePreset && old.HasProperty("_Smoothness") ? old.GetFloat("_Smoothness")
                            : ReadMaterialFloat(old, "_Glossiness", "SprocketMapSmoothness", .15f, nativePreset);
                        bool transparent = old.HasProperty("_Surface") && old.GetFloat("_Surface") > 0.5f
                            || old.HasProperty("_Mode") && old.GetFloat("_Mode") >= 2
                            || old.GetTag("SprocketMapTransparent", false, "") == "1";
                        bool clipping = old.HasProperty("_AlphaClip") && old.GetFloat("_AlphaClip") > 0.5f
                            || old.HasProperty("_Mode") && old.GetFloat("_Mode") == 1
                            || old.IsKeywordEnabled("_ALPHATEST_ON")
                            || old.GetTag("SprocketMapAlphaClip", false, "") == "1";
                        if (clipping && !transparent && foliage != null)
                        {
                            clone = FoliageCutout(foliage, old, color, texture, normal, mask, smoothness, nativePreset);
                            s.Materials.Add(clone);
                            clones.Add(old.Pointer, clone);
                            cutoutCount++;
                            materials[i] = clone; changed = true;
                            continue;
                        }
                        clone = new Material(lit)
                        {
                            name = old.name + " (custom map HDRP)",
                            enableInstancing = old.enableInstancing,
                        };
                        s.Materials.Add(clone);
                        clone.SetColor("_BaseColor", color);
                        if (texture != null)
                        {
                            clone.SetTexture("_BaseColorMap", texture);
                            string property = old.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                            clone.SetTextureScale("_BaseColorMap", old.GetTextureScale(property));
                            clone.SetTextureOffset("_BaseColorMap", old.GetTextureOffset(property));
                        }
                        // Normal and mask maps come as a pair; a missing one is the flat or neutral map.
                        if (normal != null) clone.SetTexture("_NormalMap", normal);
                        else if (full != null && mask != null) clone.SetTexture("_NormalMap", MapShaders.FlatNormal);
                        if (full != null && normal != null && mask == null) clone.SetTexture("_MaskMap", MapShaders.NeutralMask);
                        if (mask == null)
                        {
                            // The keyword sets above always carry _MASKMAP with _NORMALMAP; the default white
                            // mask then has to give the material's own metallic, smoothness and no occlusion.
                            clone.SetFloat("_MetallicRemapMin", metallic); clone.SetFloat("_MetallicRemapMax", metallic);
                            clone.SetFloat("_SmoothnessRemapMin", smoothness); clone.SetFloat("_SmoothnessRemapMax", smoothness);
                            clone.SetFloat("_AORemapMin", 1); clone.SetFloat("_AORemapMax", 1);
                        }
                        else
                        {
                            clone.SetTexture("_MaskMap", mask);
                            clone.SetTextureScale("_MaskMap", old.GetTextureScale("_MetallicGlossMap"));
                            clone.SetTextureOffset("_MaskMap", old.GetTextureOffset("_MetallicGlossMap"));
                            // Standard metallic maps share HDRP's red/alpha channels. Only an explicitly
                            // converted HDRP mask carries ambient occlusion in green as well.
                            if (old.GetTag("SprocketMapMaskMap", false, "") != "1")
                            {
                                clone.SetFloat("_AORemapMin", 1);
                                clone.SetFloat("_AORemapMax", 1);
                            }
                            clone.SetFloat("_SmoothnessRemapMax", ReadMaterialFloat(old, "_GlossMapScale", "SprocketMapSmoothnessScale", 1, nativePreset));
                        }
                        clone.SetFloat("_NormalScale", ReadMaterialFloat(old, "_BumpScale", "SprocketMapNormalScale", 1, nativePreset));
                        HDMaterial.SetEmissiveColor(clone, emission);
                        // An emission map limits the glow to its texture (lit windows, signs). The game's own HDRP/Lit has
                        // no variant for one, and a whole glowing surface would be worse than none.
                        if (emissionMap != null)
                        {
                            if (full != null)
                            {
                                clone.SetTexture("_EmissiveColorMap", emissionMap);
                                clone.SetTextureScale("_EmissiveColorMap", old.GetTextureScale("_EmissionMap"));
                                clone.SetTextureOffset("_EmissiveColorMap", old.GetTextureOffset("_EmissionMap"));
                            }
                            else HDMaterial.SetEmissiveColor(clone, Color.black);
                        }
                        clone.SetFloat("_Metallic", metallic);
                        clone.SetFloat("_Smoothness", smoothness);
                        // Without the foliage shader, cutouts are alpha-blended: the parallax variant with zero depth
                        // and one sample.
                        bool blended = transparent || clipping && full == null;
                        HDMaterial.SetSurfaceType(clone, blended);
                        if (full != null && clipping && !transparent)
                        {
                            float cutoff = old.HasProperty("_Cutoff") ? old.GetFloat("_Cutoff") : .5f;
                            if (TryFiniteFloat(old.GetTag("SprocketMapAlphaCutoff", false, ""), out var tagged)) cutoff = tagged;
                            HDMaterial.SetAlphaClipping(clone, true);
                            HDMaterial.SetAlphaCutoff(clone, Mathf.Clamp01(cutoff));
                            cutoutCount++;
                        }
                        if (blended && full == null)
                        {
                            clone.SetFloat("_DisplacementMode", 2);
                            clone.SetFloat("_HeightPoMAmplitude", 0);
                            clone.SetFloat("_PPDMinSamples", 1); clone.SetFloat("_PPDMaxSamples", 1);
                            clone.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
                            blendedCount++;
                        }
                        // Validation sets cull off for double-sided. The game's HDRP/Lit has no _DOUBLESIDED_ON variant,
                        // so with it the keyword set below leaves that keyword out.
                        if (old.HasProperty("_Cull") && old.GetFloat("_Cull") == 0
                            || old.GetTag("SprocketMapDoubleSided", false, "") == "1") clone.SetFloat("_DoubleSidedEnable", 1);
                        // HDRP's own validator sets the blend, depth, cull and stencil state and the passes.
                        if (!HDMaterial.ValidateMaterial(clone))
                            throw new InvalidOperationException($"The map material '{old.name}' could not use the game's lighting shader.");
                        if (full == null) clone.shaderKeywords = blended ? BlendedKeywords : normal != null || mask != null ? MappedKeywords : OpaqueKeywords;
                        else if (blended) blendedCount++;
                        if (blended && full == null)
                        {
                            clone.SetFloat("_HeightAmplitude", 0);
                            // Its shadow pass cannot clip, so a cutout would cast a solid card's shadow.
                            clone.SetShaderPassEnabled("ShadowCaster", false);
                        }
                        clones.Add(old.Pointer, clone);
                    }
                    materials[i] = clone; changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
        // Terrains cache their grass and rock prototypes' materials; hand the converted ones back.
        foreach (var root in s.Roots)
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                if (terrain.terrainData is { } data && data.detailPrototypes.Length > 0)
                {
                    data.detailPrototypes = data.detailPrototypes;
                    terrain.Flush();
                }
        if (cutoutCount + blendedCount > 0)
            Plugin.ModLog.LogInfo(full != null
                ? $"Custom map materials: {cutoutCount} alpha-test cutouts and {blendedCount} transparent on Map Framework's HDRP/Lit ({clones.Count} converted)."
                : $"Custom map materials: {cutoutCount} cutouts use the game's foliage shader; {blendedCount} transparent or cutout ones are alpha-blended without shadows ({clones.Count} converted).");
    }

    const string FoliageShader = "NatureManufacture/HDRP/Foliage/Foliage";
    // Its compiled variants: alpha test, both sides and transmission, in every pass including shadows.
    static readonly string[] FoliageKeywords = { "_ALPHATEST_ON", "_DOUBLESIDED_ON", "_MATERIAL_FEATURE_TRANSMISSION" };

    /// A cutout (leaves, ivy, fences, grates) on the game's own foliage shader. HDRP/Lit has no alpha-test variant
    /// and can only blend, which costs about half the frame on a dense map and casts no shadow; this one cuts out,
    /// draws both sides and casts cut-out shadows like the game's own vegetation.
    static Material FoliageCutout(Shader foliage, Material old, Color color, Texture? texture, Texture? normal, Texture? mask,
        float smoothness, bool nativePreset)
    {
        var clone = new Material(foliage) { name = old.name + " (custom map cutout)", enableInstancing = old.enableInstancing };
        if (texture != null)
        {
            clone.SetTexture("_BaseColorMap", texture);
            string property = old.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            var scale = old.GetTextureScale(property); var offset = old.GetTextureOffset(property);
            clone.SetVector("_TilingOffset", new Vector4(scale.x, scale.y, offset.x, offset.y));
        }
        // Its leaf tint varies between these two by noise; equal, it is the material's own colour.
        clone.SetColor("_HealthyColor", color); clone.SetColor("_DryColor", color);
        if (normal != null) clone.SetTexture("_NormalMap", normal);
        clone.SetFloat("_NormalScale", ReadMaterialFloat(old, "_BumpScale", "SprocketMapNormalScale", 1, nativePreset));
        if (mask == null)
        {
            clone.SetFloat("_SmoothnessRemapMin", smoothness); clone.SetFloat("_SmoothnessRemapMax", smoothness);
            clone.SetFloat("_AORemapMin", 1); clone.SetFloat("_AORemapMax", 1);
        }
        else
        {
            clone.SetTexture("_MaskMap", mask);
            if (old.GetTag("SprocketMapMaskMap", false, "") != "1") { clone.SetFloat("_AORemapMin", 1); clone.SetFloat("_AORemapMax", 1); }
            clone.SetFloat("_SmoothnessRemapMax", ReadMaterialFloat(old, "_GlossMapScale", "SprocketMapSmoothnessScale", 1, nativePreset));
        }
        float cutoff = old.HasProperty("_Cutoff") ? old.GetFloat("_Cutoff") : .5f;
        if (TryFiniteFloat(old.GetTag("SprocketMapAlphaCutoff", false, ""), out var tagged)) cutoff = tagged;
        cutoff = Mathf.Clamp01(cutoff);
        clone.SetFloat("_AlphaCutoff", cutoff); clone.SetFloat("_AlphaShadowThreshold", cutoff);
        // Still scenery: no wind bending (merged meshes carry no bend weights) and no distance fade.
        foreach (var still in new[] { "_InitialBend", "_Drag", "_AlphaDrag", "_ShiverDrag", "_HeightDrag" }) clone.SetFloat(still, 0);
        clone.SetFloat("_CullFarStart", 100000);
        clone.shaderKeywords = FoliageKeywords;
        clone.renderQueue = 2450; // HDRP's opaque alpha-test queue
        return clone;
    }

    static Color ReadMaterialColor(Material material, string property, string tag, Color fallback, bool metadataOnly = false)
    {
        if (!metadataOnly && material.HasProperty(property)) return material.GetColor(property);
        var parts = material.GetTag(tag, false, "").Split(',');
        if (parts.Length != 4 || !TryFiniteFloat(parts[0], out var red)
            || !TryFiniteFloat(parts[1], out var green) || !TryFiniteFloat(parts[2], out var blue)
            || !TryFiniteFloat(parts[3], out var alpha)) return fallback;
        return new Color(red, green, blue, alpha);
    }

    static float ReadMaterialFloat(Material material, string property, string tag, float fallback, bool metadataOnly = false)
    {
        if (!metadataOnly && material.HasProperty(property)) return material.GetFloat(property);
        return TryFiniteFloat(material.GetTag(tag, false, ""), out var value) ? value : fallback;
    }

    static bool TryFiniteFloat(string text, out float value) => float.TryParse(text,
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
        out value) && float.IsFinite(value);

    // Terrain grass and rocks draw their prototype prefabs, which live outside the scene hierarchy.
    static IEnumerable<Renderer> TerrainDetailRenderers(GameObject root) => root.GetComponentsInChildren<Terrain>(true)
        .Where(t => t.terrainData != null).SelectMany(t => t.terrainData.detailPrototypes)
        .Where(p => p.prototype != null).SelectMany(p => p.prototype.GetComponentsInChildren<Renderer>(true));

    static void CloneTerrainMaterials(Session s)
    {
        Shader? terrainShader = null;
        foreach (var root in s.Roots)
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
            {
                var old = terrain.materialTemplate;
                if (old != null && (old.shader?.name ?? "").StartsWith("HDRP/", StringComparison.Ordinal)) continue;
                terrainShader ??= Shader.Find("HDRP/TerrainLit")
                    ?? throw new InvalidOperationException("The game's terrain lighting shader was not available.");
                // Terrain does not inherit Renderer. Its native heightmap, layers and collision must
                // remain intact; only the material on this owned custom scene needs adapting to HDRP.
                var clone = new Material(terrainShader)
                {
                    name = terrain.name + " (custom terrain HDRP)",
                    enableInstancing = terrain.drawInstanced,
                };
                s.Materials.Add(clone);
                // Only the editor's inspector sets the layer keywords; without them HDRP draws four layers and no
                // normal or mask maps. The game's build has eight layers only together with height blending.
                var layers = terrain.terrainData != null ? terrain.terrainData.terrainLayers : null;
                int count = layers?.Length ?? 0;
                bool mapped = layers != null && layers.Any(l => l != null && (l.normalMapTexture != null || l.maskMapTexture != null));
                clone.SetFloat("_EnableInstancedPerPixelNormal", 1);
                clone.SetFloat("_EnableHeightBlend", count > 4 ? 1 : 0);
                if (!HDMaterial.ValidateMaterial(clone))
                    throw new InvalidOperationException($"The terrain '{terrain.name}' could not use the game's lighting shader.");
                clone.shaderKeywords = count > 4 ? EightLayerTerrainKeywords : mapped ? MappedTerrainKeywords : TerrainKeywords;
                if (count > 4)
                {
                    // Equal heights make the height blend a plain weighted blend, as the map was painted.
                    foreach (var layer in layers!)
                    {
                        if (layer == null) continue;
                        var min = layer.maskMapRemapMin; var max = layer.maskMapRemapMax;
                        min.z = max.z = .5f;
                        layer.maskMapRemapMin = min; layer.maskMapRemapMax = max;
                    }
                    terrain.terrainData!.terrainLayers = layers;
                }
                if (count > 8) Plugin.ModLog.LogWarning($"The terrain '{terrain.name}' has {count} layers; the game draws the first 8.");
                terrain.materialTemplate = clone;
            }
    }

    static readonly string[] TerrainKeywords = { "_TERRAIN_INSTANCED_PERPIXEL_NORMAL" };
    static readonly string[] MappedTerrainKeywords = { "_NORMALMAP", "_MASKMAP", "_TERRAIN_INSTANCED_PERPIXEL_NORMAL" };
    static readonly string[] EightLayerTerrainKeywords = { "_NORMALMAP", "_MASKMAP", "_TERRAIN_8_LAYERS",
        "_TERRAIN_BLEND_HEIGHT", "_TERRAIN_INSTANCED_PERPIXEL_NORMAL" };

    static void ResumeNative(Session s)
    {
        resuming = true;
        try { s.InitTask = s.Controller.Initiate(s.Settings, s.Arguments)
            ?? throw new InvalidOperationException("The native game controller did not return its initialization task."); }
        finally { resuming = false; }
    }

    static void Recover(Session s, Exception ex)
    {
        if (!s.Lifetime.BeginRecovery()) { s.Fault(new Il2CppSystem.Exception(ex.Message)); Cleanup(); return; }
        loadFailure = $"The map '{s.Definition.Identifier}' could not open. Returned to the main menu; built-in maps are unchanged.";
        failures[s.Definition.Identifier] = $"The map '{s.Definition.Identifier}' could not open. Choose another map or restart after updating it.";
        Plugin.ModLog.LogError($"Custom environment rejected safely: {s.Definition.Identifier}: {ex}");
        s.Recovering = true; s.Prepared = false;
        // Cleanup can need more frames: a timed-out Unity scene load is still allowed to finish, and its
        // exact scene must be unloaded before the fallback can initialize vehicles or return to the menu.
    }

    static void Cleanup()
    {
        if (session is { } old)
        {
            old.Abandon();
            if (!retired.Contains(old)) retired.Add(old);
        }
        session = null;
        requested = null;
    }

    sealed class Session
    {
        internal readonly Definition Definition;
        internal readonly GameController Controller;
        internal readonly SettingsProfile Settings;
        internal readonly Il2CppReferenceArray<Il2CppSystem.Object> Arguments;
        internal readonly NativeCancellation? Cancellation;
        internal readonly CustomMapLifetime Lifetime = new();
        internal readonly NativeCompletion Completion = new(Il2CppSystem.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly float Since = Time.realtimeSinceStartup;
        internal AsyncOperation? Operation, NavigationOperation, UnloadOperation;
        internal NativeTask? InitTask;
        internal Scene CustomScene;
        internal List<Destructibles.Item> Destructibles = new(); // breakables and trees: set aside while starts are checked
        internal Bounds Bounds;
        internal GameObject? Environment;
        internal NavMeshData? Navigation;
        internal NavMeshDataInstance NavigationInstance;
        internal Maps.Map? BuildMap;
        internal bool Prepared, Merged, Initialized, Recovering;
        internal readonly List<GameObject> Roots = new();
        internal readonly List<Material> Materials = new();
        internal readonly List<UnityEngine.Object> Owned = new(); // other runtime assets made for this map
        internal readonly List<Action> Undo = new(); // carrier changes to reverse when the map goes
        internal readonly List<(Renderer Item, bool Enabled)> Renderers = new();
        internal readonly List<(Terrain Item, bool Enabled)> Terrains = new();
        internal readonly List<(Collider Item, bool Enabled)> Colliders = new();
        internal readonly List<(NavMeshSurface Item, bool Enabled, bool HadData)> NativeNavigation = new();
        internal readonly List<(WorldBounds Item, Vector3 Centre, Vector3 Size, float Thickness)> WorldBounds = new();
        internal readonly List<(VehicleDesignArea Area, Transform Marker, Vector3 Position, Quaternion Rotation, bool Changed)> DesignerMarkers = new();
        internal readonly List<LocatorSnapshot> Locators = new();
        readonly HashSet<int> previousScenes = new();
        bool navigationCanceled, disposed;

        internal Session(Definition definition, GameController controller, SettingsProfile settings,
            Il2CppReferenceArray<Il2CppSystem.Object> arguments, NativeCancellation? cancellation)
        { Definition = definition; Controller = controller; Settings = settings; Arguments = arguments; Cancellation = cancellation; }

        internal void Succeed()
        {
            if (Lifetime.TrySettle(CustomMapLifetime.Outcome.Succeeded)) Completion.TrySetResult(null!);
        }
        internal void Fault(Il2CppSystem.Exception exception)
        {
            if (Lifetime.TrySettle(CustomMapLifetime.Outcome.Faulted)) Completion.TrySetException(exception);
        }
        internal void Abandon()
        {
            // This Unity player's stripped TCS wrapper has no TrySetCanceled, but its owned Task does.
            if (Lifetime.Abandon()) Completion.Task.TrySetCanceled(Cancellation ?? NativeCancellation.None);
        }

        internal void StartSceneLoad()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var existing = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (string.Equals(existing.path, Definition.ScenePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This custom environment is already loaded by another scene owner.");
                previousScenes.Add(existing.handle);
            }
            Operation = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(Definition.ScenePath, LoadSceneMode.Additive)
                ?? throw new InvalidOperationException("Unity did not start loading the map scene.");
            Lifetime.SceneLoadStarted();
            Plugin.ModLog.LogInfo($"Loading custom environment '{Definition.Identifier}' before native game initialization.");
        }

        internal void CaptureLoadedScene()
        {
            if (Operation?.isDone != true) return;
            Lifetime.SceneLoadFinished();
            if (CustomScene.IsValid()) return;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var candidate = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!previousScenes.Contains(candidate.handle)
                    && string.Equals(candidate.path, Definition.ScenePath, StringComparison.OrdinalIgnoreCase))
                { CustomScene = candidate; return; }
            }
        }

        internal bool DisposeEnvironment()
        {
            // A native initialization already in progress may still be reading these roots and colliders.
            if (InitTask?.IsCompleted == false) return false;
            CaptureLoadedScene();
            if (NavigationOperation?.isDone == false && Navigation != null && !navigationCanceled)
            { NavMeshBuilder.Cancel(Navigation); navigationCanceled = true; }
            if (NavigationOperation?.isDone == true) Lifetime.NavigationFinished();
            if (!Lifetime.CanDisposeResources) return false;
            if (!disposed)
            {
                if (NavigationInstance.valid) NavigationInstance.Remove();
                if (Navigation != null) UnityEngine.Object.Destroy(Navigation);
                foreach (var (item, centre, size, thickness) in WorldBounds)
                    if (item) { item.centre = centre; item.size = size; item.thickness = thickness; item.Generate(); }
                WorldBounds.Clear();
                foreach (var (area, marker, position, rotation, changed) in DesignerMarkers)
                    if (area && marker) { marker.SetPositionAndRotation(position, rotation); area.transformChanged = changed; }
                DesignerMarkers.Clear();
                foreach (var locator in Locators) locator.Restore();
                Locators.Clear();
                foreach (var (item, enabled, hadData) in NativeNavigation)
                    if (item)
                    {
                        item.enabled = enabled;
                        if (hadData && !item.navMeshDataInstance.valid) item.AddData();
                    }
                NativeNavigation.Clear();
                foreach (var undo in Undo) try { undo(); } catch (Exception ex) { Plugin.ModLog.LogWarning($"A carrier setting was not restored: {ex.Message}"); }
                Undo.Clear();
                foreach (var (item, enabled) in Renderers) if (item) item.enabled = enabled;
                foreach (var (item, enabled) in Terrains) if (item) item.enabled = enabled;
                foreach (var (item, enabled) in Colliders) if (item) item.enabled = enabled;
                Renderers.Clear(); Terrains.Clear(); Colliders.Clear();
                if (Environment != null && Environment) { Environment.SetActive(false); UnityEngine.Object.Destroy(Environment); }
                if (!Merged && CustomScene.IsValid() && CustomScene.isLoaded)
                    UnloadOperation = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(CustomScene)
                        ?? throw new InvalidOperationException("Unity did not start removing the rejected custom scene.");
                foreach (var material in Materials) if (material) UnityEngine.Object.Destroy(material);
                foreach (var item in Owned) if (item) UnityEngine.Object.Destroy(item);
                Materials.Clear(); Owned.Clear(); Roots.Clear();
                Physics.SyncTransforms();
                disposed = true;
            }
            if (UnloadOperation?.isDone == false) return false;
            return Lifetime.FinishCleanup();
        }
    }
}

public sealed class CustomMapRunner : MonoBehaviour
{
    public CustomMapRunner(IntPtr pointer) : base(pointer) { }
    void Update() { AutoMapImport.Step(); CustomMapBridge.Step(); Destructibles.Tick(); }
    void OnGUI() => AutoMapImport.Draw();
    void OnApplicationQuit() => AutoMapImport.Stop();
}

[HarmonyPatch]
internal static class CustomSceneLookupPatch
{
    [HarmonyPostfix, HarmonyPatch(typeof(SceneDatabase), nameof(SceneDatabase.TryGet))]
    static void Lookup(SceneDatabase __instance, string sceneName, ref SprocketScene scene, ref bool __result)
    {
        if (CustomMapBridge.LoadName(sceneName) == null) return;
        if (__result) { CustomMapBridge.NativeCollision(sceneName); return; }
        if (!CustomMapBridge.Resolve(__instance, sceneName, out var native)) return;
        scene = native!; __result = true;
    }
}

[HarmonyPatch(typeof(GameController), nameof(GameController.Initiate))]
internal static class CustomMapInitiatePatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    static bool Initiate(GameController __instance, SettingsProfile settings, Il2CppReferenceArray<Il2CppSystem.Object> arguments, ref NativeTask __result)
        => CustomMapBridge.Initiating(__instance, settings, arguments, ref __result);
}
