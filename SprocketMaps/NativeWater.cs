using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketMaps;

/// A map's "Sprocket Water Surface" plane becomes the game's own water: HDRP's water system, as on Sandbox's ocean.
/// When the current graphics settings have no water, or anything fails, the map keeps its own plane.
internal static class NativeWater
{
    internal const string Marker = "Sprocket Water Surface";

    internal static void Apply(GameObject environment, IEnumerable<GameObject> roots, List<UnityEngine.Object> owned)
    {
        var planes = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).Where(r => r.gameObject.name.StartsWith(Marker, StringComparison.Ordinal)).ToList();
        if (planes.Count == 0) return;
        // Water is never ground, even when the game's water is unavailable; this runs before tank navigation is built.
        foreach (var plane in planes) foreach (var collider in plane.GetComponents<Collider>()) collider.enabled = false;
        var created = new List<GameObject>();
        try
        {
            var asset = GraphicsSettings.currentRenderPipeline?.TryCast<HDRenderPipelineAsset>();
            if (asset == null || !asset.currentPlatformRenderPipelineSettings.supportWater)
            {
                Plugin.ModLog.LogInfo("The game's water is off in the current graphics settings; the map's own water plane is used.");
                return;
            }
            foreach (var plane in planes)
            {
                var bounds = plane.bounds;
                var go = new GameObject("Sprocket water");
                created.Add(go);
                go.transform.SetParent(environment.transform, false);
                // A quad surface spans its transform's X/Z scale at the transform's height.
                go.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.max.y, bounds.center.z), Quaternion.identity);
                go.transform.localScale = new Vector3(bounds.size.x, 1, bounds.size.z);
                var water = go.AddComponent<WaterSurface>();
                water.surfaceType = WaterSurfaceType.Pool; // calm, ripples only: a lake, not Sandbox's ocean swell
                water.geometryType = WaterGeometryType.Quad;
            }
            // Water surfaces draw only while a volume turns water rendering on; carrier scenes without water have none.
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            owned.Add(profile);
            profile.Add(Il2CppType.Of<WaterRendering>(), true).Cast<WaterRendering>().enable.Override(true);
            var holder = new GameObject("Sprocket water rendering");
            created.Add(holder);
            holder.transform.SetParent(environment.transform, false);
            var volume = holder.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 100; volume.sharedProfile = profile;
            foreach (var plane in planes) plane.enabled = false;
            Plugin.ModLog.LogInfo($"Map water uses the game's own water system ({planes.Count} surface{(planes.Count == 1 ? "" : "s")}).");
        }
        catch (Exception ex)
        {
            foreach (var go in created) if (go) UnityEngine.Object.Destroy(go);
            foreach (var plane in planes) if (plane) plane.enabled = true;
            Plugin.ModLog.LogWarning($"The game's water could not be used; the map's own water plane stays: {ex.Message}");
        }
    }
}
