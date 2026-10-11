using System.Text.RegularExpressions;
using HarmonyLib;
using Sprocket.TreeDestruction;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketMaps;

/// Scenery that gives way under tanks, chosen by name in the map:
/// - Trees bend and snap with the game's own DestructableTree, tuned like the game's poplars for their height, with a
///   trunk collider on the game's tree layer. LOD groups drawn with the game's foliage (native tree presets) are trees;
///   so is anything named "Sprocket Tree" (poles and posts too).
/// - "Sprocket Breakable" objects (fences, crates, barrels, wrecks) become physics bodies that sleep until a tank hits
///   them. A number after the name is the mass in kilograms ("Sprocket Breakable 800"); otherwise it comes from the size.
/// Neither kind blocks tank navigation, so AI tanks drive through them as players can.
internal static class Destructibles
{
    internal const string TreeMarker = "Sprocket Tree", BreakableMarker = "Sprocket Breakable";
    static readonly Regex BreakableName = new(@"^Sprocket Breakable(?:\s+(\d+(?:\.\d+)?)\s*(?:kg)?)?\b", RegexOptions.CultureInvariant);
    const int TreeLayer = 21, BendLayer = 14; // as on the game's poplar prefabs (sharedassets17)
    // The game's poplars: height in metres, mass and maximum bend-back force; the bend curves are the same on all five.
    static readonly (float Height, float Mass, float Force)[] Poplars =
        { (3.7f, 10, 500), (7.8f, 500, 25000), (14, 650, 50000), (17, 1000, 200000), (23, 1500, 300000) };

    internal sealed record Item(GameObject Object, bool Tree, float? Mass, Collider[] Disabled);

    /// Run before the map's materials are converted (native presets are recognised by their material tags) and before
    /// tank navigation collects its sources: the items' colliders are switched off until Make.
    internal static List<Item> Find(IEnumerable<GameObject> roots)
    {
        var items = new List<Item>();
        var seen = new HashSet<int>();
        void Add(GameObject go, bool tree, float? mass)
        {
            if (!seen.Add(go.GetInstanceID())) return;
            var colliders = go.GetComponentsInChildren<Collider>(true).Where(c => c.enabled).ToArray();
            foreach (var collider in colliders) collider.enabled = false;
            items.Add(new(go, tree, mass, colliders));
        }
        foreach (var root in roots)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.name.StartsWith(TreeMarker, StringComparison.Ordinal)) Add(transform.gameObject, true, null);
                else if (BreakableName.Match(transform.name) is { Success: true } match)
                    Add(transform.gameObject, false, match.Groups[1].Success && float.TryParse(match.Groups[1].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kg) ? kg : null);
            }
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                if (NativeFoliage(group)) Add(group.gameObject, true, null);
        }
        return items;
    }

    internal static void Make(List<Item> items)
    {
        running.Clear(); ours.Clear(); Active = false;
        int trees = 0, breakables = 0;
        foreach (var item in items)
        {
            bool made = false;
            try
            {
                if (item.Object)
                    made = item.Tree ? !Plugin.DestructibleTreesOff && MakeTree(item.Object) : MakeBreakable(item.Object, item.Mass, item.Disabled);
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"'{item.Object.name}' stays fixed: {ex.Message}"); }
            if (made) { if (item.Tree) trees++; else breakables++; }
            else foreach (var collider in item.Disabled) if (collider) collider.enabled = true;
        }
        Active = trees > 0;
        if (trees + breakables > 0)
            Plugin.ModLog.LogInfo($"Destructible scenery: {trees} trees use the game's tree bending and breaking, {breakables} breakable objects are physics bodies ({items.Count} marked).");
    }

    // The game's own trees are terrain instances that cost nothing until a tank reaches them; thousands of imported
    // trees each running physics updates would not. Only trees near a tank run, and settle back once upright.
    const float WakeRadius = 40, TickSeconds = .25f, VehicleRefreshSeconds = 2;
    static readonly Dictionary<int, DestructableTree> running = new();
    static readonly HashSet<int> near = new(), ours = new(); // ours: trees this map made, not the game's own
    static readonly Collider[] hits = new Collider[512];
    static VehicleBehaviour[] vehicles = Array.Empty<VehicleBehaviour>();
    static float nextTick, nextVehicles;
    internal static bool Active;
    internal static void Tick()
    {
        if (!Active || Time.time < nextTick) return;
        nextTick = Time.time + TickSeconds;
        if (Time.time >= nextVehicles) { vehicles = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>().ToArray(); nextVehicles = Time.time + VehicleRefreshSeconds; }
        near.Clear();
        foreach (var vehicle in vehicles)
        {
            if (!vehicle) continue;
            int count = Physics.OverlapSphereNonAlloc(vehicle.transform.position, WakeRadius, hits, 1 << TreeLayer, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var tree = hits[i].GetComponent<DestructableTree>();
                if (tree == null) continue;
                int id = tree.GetInstanceID();
                if (!ours.Contains(id)) continue;
                near.Add(id);
                if (!running.ContainsKey(id)) { tree.enabled = true; running[id] = tree; }
            }
        }
        foreach (var (id, tree) in running.ToList())
            if (!tree) running.Remove(id);
            else if (!near.Contains(id) && tree.BendFactor < .01f && !tree.FullyBent) { tree.enabled = false; running.Remove(id); }
    }

    static bool NativeFoliage(LODGroup group) => group.GetLODs().SelectMany(l => l.renderers).Where(r => r)
        .SelectMany(r => r.sharedMaterials).Any(m => m && m.GetTag("SprocketMapNativeShader", false, "").StartsWith("NatureManufacture/HDRP/Foliage/", StringComparison.Ordinal));

    static bool MakeTree(GameObject tree)
    {
        if (tree.GetComponent<DestructableTree>()) return false;
        var renderers = tree.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        float height = bounds.size.y;
        if (!(height > .5f) || height > 100) return false;
        var (mass, force) = Tuned(height);
        var scale = tree.transform.lossyScale;
        float sy = Mathf.Max(1e-4f, Mathf.Abs(scale.y)), sxz = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)));
        bool active = tree.activeSelf;
        tree.SetActive(false); // the component's Awake waits until its settings are in
        try
        {
            // The tree's own collider if it has one; its parts' mesh colliders stay off, since the trunk moves as one.
            var collider = tree.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
            else
            {
                // The game's poplar trunk: a thin vertical capsule over the lower three quarters of the tree.
                var capsule = tree.AddComponent<CapsuleCollider>();
                capsule.direction = 1;
                capsule.radius = Mathf.Max(.1f, .013f * height) / sxz;
                capsule.height = .77f * height / sy;
                capsule.center = tree.transform.InverseTransformPoint(new Vector3(bounds.center.x, bounds.min.y + .385f * height, bounds.center.z));
                collider = capsule;
            }
            foreach (var part in tree.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = TreeLayer;
            var destructible = tree.AddComponent<DestructableTree>();
            destructible.col = collider;
            destructible.bendLayer = BendLayer;
            destructible.mass = mass;
            destructible.maxBendBackForce = force;
            destructible.bendResistanceOverAngle = new AnimationCurve(new[] { new Keyframe(0, .5f), new Keyframe(.35f, 1), new Keyframe(1, 0) });
            destructible.bendReturnForceOverAngle = new AnimationCurve(new[] { new Keyframe(0, 0), new Keyframe(.5f, 1), new Keyframe(1, 0) });
            destructible.effectOffset = new Vector3(0, .47f * height / sy, 0); // the canopy, where leaves fall from
            destructible.effectRadius = .29f * height;
            destructible.enabled = false; // woken by Tick near a tank; its collider works regardless
            ours.Add(destructible.GetInstanceID());
            return true;
        }
        finally { tree.SetActive(active); }
    }

    static bool MakeBreakable(GameObject item, float? kilograms, Collider[] colliders)
    {
        if (item.GetComponent<Rigidbody>() || LocalBounds(item) is not { } bounds) return false;
        var scale = item.transform.lossyScale;
        var size = Vector3.Scale(bounds.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        if (size.magnitude > 30) { Plugin.ModLog.LogWarning($"'{item.name}' is {size.magnitude:0} m across, too big to knock loose; it stays fixed."); return false; }
        // A moving body needs convex colliders: mesh colliders become their convex hulls. Unreadable meshes can't be
        // hulled, so then a box around the object stands in.
        bool usable = false;
        foreach (var collider in colliders)
        {
            if (!collider) continue;
            if (collider is MeshCollider mesh)
            {
                if (mesh.sharedMesh == null || !mesh.sharedMesh.isReadable) continue;
                mesh.convex = true;
            }
            collider.enabled = true; usable = true;
        }
        if (!usable) { var box = item.AddComponent<BoxCollider>(); box.center = bounds.center; box.size = bounds.size; }
        var body = item.AddComponent<Rigidbody>();
        body.mass = kilograms is > 0 ? kilograms.Value : Mathf.Clamp(size.x * size.y * size.z * 200, 5, 4000); // mostly hollow props
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.Sleep(); // no cost and no settling until something hits it
        return true;
    }

    static Bounds? LocalBounds(GameObject item)
    {
        Bounds? result = null;
        var toLocal = item.transform.worldToLocalMatrix;
        foreach (var filter in item.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            if (mesh == null) continue;
            var matrix = toLocal * filter.transform.localToWorldMatrix;
            var source = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = matrix.MultiplyPoint3x4(source.center + Vector3.Scale(source.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)));
                if (result is { } current) { current.Encapsulate(corner); result = current; }
                else result = new Bounds(corner, Vector3.zero);
            }
        }
        return result;
    }

    static (float Mass, float Force) Tuned(float height)
    {
        if (height <= Poplars[0].Height) return (Poplars[0].Mass, Poplars[0].Force);
        for (int i = 1; i < Poplars.Length; i++)
            if (height <= Poplars[i].Height)
            {
                var (a, b) = (Poplars[i - 1], Poplars[i]);
                float t = (height - a.Height) / (b.Height - a.Height);
                return (Mathf.Lerp(a.Mass, b.Mass, t), Mathf.Lerp(a.Force, b.Force, t));
            }
        return (Poplars[^1].Mass, Poplars[^1].Force);
    }
}

/// The game's leaf-fall effects belong to its own maps' assets, which a custom map's carrier scene doesn't load;
/// imported trees bend and break without them instead of failing to play a missing effect.
[HarmonyPatch]
static class TreeEffectGuard
{
    static bool HasEffects(DestructableTree tree) => tree.knockEffect != null && tree.groundImpactEffect != null;
    [HarmonyPrefix, HarmonyPatch(typeof(DestructableTree), nameof(DestructableTree.PlayBendStartEffect))]
    static bool Start(DestructableTree __instance) => HasEffects(__instance);
    [HarmonyPrefix, HarmonyPatch(typeof(DestructableTree), nameof(DestructableTree.PlayBendFinishEffect))]
    static bool Finish(DestructableTree __instance) => HasEffects(__instance);
}
