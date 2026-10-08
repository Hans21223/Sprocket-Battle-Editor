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
    internal sealed record Definition(string Identifier, string ScenePath, AssetBundle Bundle, Maps.Map Map);
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
            definitions.Add(name, new Definition(name, path, bundle, map));
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
        CloneMaterials(s);
        HideCarrierEnvironment(s);
        Physics.SyncTransforms();
        // Match the carrier's serialized tank agent (radius, height, climb and slope), rather than editor defaults.
        var settings = s.NativeNavigation.Count > 0 ? s.NativeNavigation[0].Item.GetBuildSettings() : NavMesh.GetSettingsByID(0);
        var sources = new Il2CppSystem.Collections.Generic.List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(s.Environment.transform, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
            new Il2CppSystem.Collections.Generic.List<NavMeshBuildMarkup>(), sources);
        if (sources.Count == 0) throw new InvalidOperationException("This map has no ground collision for tanks.");
        s.Navigation = new NavMeshData(settings.agentTypeID);
        s.NavigationOperation = NavMeshBuilder.UpdateNavMeshDataAsync(s.Navigation, settings, sources, s.Bounds);
        if (s.NavigationOperation == null) throw new InvalidOperationException("Unity did not start building tank navigation.");
        s.Lifetime.NavigationStarted();
        s.Prepared = true;
        Plugin.ModLog.LogInfo($"Custom environment prepared: '{s.Definition.Identifier}', bounds={s.Bounds}, navigation sources={sources.Count}, scoped material clones={s.Materials.Count}");
    }

    static void CompleteEnvironment(Session s)
    {
        s.NavigationInstance = NavMesh.AddNavMeshData(s.Navigation!);
        if (!s.NavigationInstance.valid) throw new InvalidOperationException("Tank navigation could not be created for this map.");
        s.Lifetime.NavigationFinished();
        var (a, d) = SpawnAnchors(s);
        s.BuildMap = s.Definition.Map with { Attackers = a, Defenders = d };
        UnityEngine.SceneManagement.SceneManager.MergeScenes(s.CustomScene, s.Controller.gameObject.scene);
        s.Merged = true;
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

    static (Vector2 A, Vector2 D) SpawnAnchors(Session s)
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
        bool TeamClear(Vector3 at, Vector3 toward)
        {
            var direction = toward - at; direction.y = 0;
            var facing = Quaternion.LookRotation(direction);
            var side = facing * Vector3.right; var back = facing * Vector3.back;
            for (int i = 0; i < s.Definition.Map.Spawns; i++)
            {
                var offset = SpawnFormation.Offset(s.Definition.Map.Spawns, i, SpawnSpacing);
                var place = at + side * offset.Side + back * offset.Back;
                if (!GroundAt(place.x, place.z, out var ground)
                    || Math.Abs(ground.y - at.y) > 1
                    || Physics.CheckBox(ground + Vector3.up * 3, new Vector3(4, 2.4f, 4), facing, ~0, QueryTriggerInteraction.Ignore)) return false;
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = place + side * (x * 4) + back * (z * 4);
                        if (!GroundAt(corner.x, corner.z, out var supported) || Math.Abs(supported.y - ground.y) > 0.5f) return false;
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
            if ((p - q).sqrMagnitude < 100 * 100 || !TeamClear(first, second) || !TeamClear(second, first)) return false;
            return navigation.Connects(p, q);
        }
        var transforms = s.Environment!.GetComponentsInChildren<Transform>(true);
        var authoredA = transforms.FirstOrDefault(t => t.name == "Sprocket Spawn Area A");
        var authoredD = transforms.FirstOrDefault(t => t.name == "Sprocket Spawn Area B");
        if (authoredA != null && authoredD != null
            && GroundAt(authoredA.position.x, authoredA.position.z, out var pa)
            && GroundAt(authoredD.position.x, authoredD.position.z, out var pd)
            && ValidPair(pa, pd, out var startA, out var startD))
            return (new Vector2(startA.x, startA.z), new Vector2(startD.x, startD.z));
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
            new CustomMapNavigation.Vertex(point.x, point.y, point.z)).ToArray(), 512, 100 * 100);
        // Bound expensive physics/path queries; a large city with disconnected roofs must not freeze the UI.
        foreach (var pair in pairs)
            if (ValidPair(candidates[pair.First], candidates[pair.Second], out var first, out var second))
            { a = first; d = second; distance = pair.DistanceSquared; break; }
        if (distance < 100 * 100) throw new InvalidOperationException("This map needs two connected, clear team spawn formations at least 100 metres apart.");
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

    static void CloneMaterials(Session s)
    {
        var lit = Shader.Find("HDRP/Lit") ?? throw new InvalidOperationException("The game's map lighting shader was not available.");
        var clones = new Dictionary<IntPtr, Material>();
        foreach (var root in s.Roots)
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var old = materials[i];
                    if (!old) continue;
                    var shader = old.shader?.name ?? "";
                    if (shader.StartsWith("HDRP/", StringComparison.Ordinal)) continue;
                    if (!clones.TryGetValue(old.Pointer, out var clone))
                    {
                        var color = old.HasProperty("_BaseColor") ? old.GetColor("_BaseColor") : old.HasProperty("_Color") ? old.GetColor("_Color") : Color.white;
                        var texture = old.HasProperty("_BaseMap") ? old.GetTexture("_BaseMap") : old.HasProperty("_MainTex") ? old.GetTexture("_MainTex") : null;
                        var normal = old.HasProperty("_BumpMap") ? old.GetTexture("_BumpMap") : null;
                        var emission = old.HasProperty("_EmissionColor") ? old.GetColor("_EmissionColor") : Color.black;
                        clone = new Material(lit) { name = old.name + " (custom map HDRP)" };
                        s.Materials.Add(clone);
                        clone.SetColor("_BaseColor", color);
                        if (texture != null)
                        {
                            clone.SetTexture("_BaseColorMap", texture);
                            string property = old.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                            clone.SetTextureScale("_BaseColorMap", old.GetTextureScale(property));
                            clone.SetTextureOffset("_BaseColorMap", old.GetTextureOffset(property));
                        }
                        if (normal != null) clone.SetTexture("_NormalMap", normal);
                        if (old.HasProperty("_BumpScale")) clone.SetFloat("_NormalScale", old.GetFloat("_BumpScale"));
                        HDMaterial.SetEmissiveColor(clone, emission);
                        if (old.HasProperty("_Metallic")) clone.SetFloat("_Metallic", old.GetFloat("_Metallic"));
                        if (old.HasProperty("_Smoothness")) clone.SetFloat("_Smoothness", old.GetFloat("_Smoothness"));
                        else if (old.HasProperty("_Glossiness")) clone.SetFloat("_Smoothness", old.GetFloat("_Glossiness"));
                        bool transparent = old.HasProperty("_Surface") && old.GetFloat("_Surface") > 0.5f
                            || old.HasProperty("_Mode") && old.GetFloat("_Mode") >= 2;
                        bool clipping = old.HasProperty("_AlphaClip") && old.GetFloat("_AlphaClip") > 0.5f
                            || old.IsKeywordEnabled("_ALPHATEST_ON");
                        HDMaterial.SetSurfaceType(clone, transparent);
                        HDMaterial.SetAlphaClipping(clone, clipping);
                        if (old.HasProperty("_Cutoff")) HDMaterial.SetAlphaCutoff(clone, old.GetFloat("_Cutoff"));
                        if (old.HasProperty("_Cull") && old.GetFloat("_Cull") == 0) clone.SetFloat("_DoubleSidedEnable", 1);
                        // HDRP's own validator selects supported blend/depth/normal/emission keywords and passes.
                        if (!HDMaterial.ValidateMaterial(clone))
                            throw new InvalidOperationException($"The map material '{old.name}' could not use the game's lighting shader.");
                        clones.Add(old.Pointer, clone);
                    }
                    materials[i] = clone; changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
    }

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
        internal Bounds Bounds;
        internal GameObject? Environment;
        internal NavMeshData? Navigation;
        internal NavMeshDataInstance NavigationInstance;
        internal Maps.Map? BuildMap;
        internal bool Prepared, Merged, Initialized, Recovering;
        internal readonly List<GameObject> Roots = new();
        internal readonly List<Material> Materials = new();
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
                foreach (var (item, enabled) in Renderers) if (item) item.enabled = enabled;
                foreach (var (item, enabled) in Terrains) if (item) item.enabled = enabled;
                foreach (var (item, enabled) in Colliders) if (item) item.enabled = enabled;
                Renderers.Clear(); Terrains.Clear(); Colliders.Clear();
                if (Environment != null && Environment) { Environment.SetActive(false); UnityEngine.Object.Destroy(Environment); }
                if (!Merged && CustomScene.IsValid() && CustomScene.isLoaded)
                    UnloadOperation = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(CustomScene)
                        ?? throw new InvalidOperationException("Unity did not start removing the rejected custom scene.");
                foreach (var material in Materials) if (material) UnityEngine.Object.Destroy(material);
                Materials.Clear(); Roots.Clear();
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
    void Update() { AutoMapImport.Step(); CustomMapBridge.Step(); }
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
