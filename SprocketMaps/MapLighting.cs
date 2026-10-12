using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace SprocketMaps;

/// A map built with its own lighting (baked lightmaps or its own sky) keeps it instead of the carrier's daylight:
/// Unity's built-in brightness scale rather than the game's daylight exposure, the map's real-time sun or moon (none
/// when the map baked it), a sky from the map's ambient colours or skybox, its fog and its light probes.
/// A map with an object named "Sprocket Game Lighting" keeps the game's sun and sky.
internal static class MapLighting
{
    internal const string OptOut = "Sprocket Game Lighting";
    static ConfigEntry<float>? exposure, lampDistance, lampBrightness;

    internal static void Configure(ConfigFile config)
    {
        exposure = config.Bind("Lighting", "Map lighting exposure", -0.263f,
            "Exposure (EV100) for maps that bring their own lighting. -0.263 shows them at the brightness Unity's built-in renderer gave them; higher is darker.");
        lampDistance = config.Bind("Lighting", "Real-time lamp distance", 150f,
            "Lamps baked into a map also shine in real time within this many metres of the camera, so they light tanks. 0 keeps them baked only; lower it if a lamp-heavy map runs slowly.");
        lampBrightness = config.Bind("Lighting", "Real-time lamp brightness", 1f,
            "Multiplies those real-time lamps' brightness. 1 is as the map's author set them; map lamps are often authored dim " +
            "(Demo City's street lamps: about 6 candela, 16-19 m up), so raise it (4-10) for lamps that visibly light streets and tanks.");
    }

    // Baked-only lamps also shine in real time, so they light tanks and draw crisp pools. The bake still holds their light,
    // so lit ground gets it twice: the user chose that over dark tanks (2026-10-11). Spot and point lamps only (real-time
    // area lights are costly), no shadows, and HDRP drops those beyond the configured distance.
    static int LiveLamps(IEnumerable<GameObject> roots)
    {
        float distance = lampDistance?.Value ?? 150;
        if (distance <= 0) return 0;
        int count = 0;
        foreach (var lamp in roots.SelectMany(r => r.GetComponentsInChildren<Light>(true)))
        {
            if (lamp.type is not (LightType.Spot or LightType.Point)) continue;
            var bake = lamp.bakingOutput;
            if (!bake.isBaked || bake.lightmapBakeType != LightmapBakeType.Baked) continue;
            float intensity = lamp.intensity;
            lamp.bakingOutput = new LightBakingOutput { isBaked = true, lightmapBakeType = LightmapBakeType.Realtime,
                mixedLightingMode = MixedLightingMode.IndirectOnly, probeOcclusionLightIndex = -1, occlusionMaskChannel = -1 };
            lamp.shadows = LightShadows.None;
            var hd = lamp.GetComponent<HDAdditionalLightData>();
            if (hd == null) hd = lamp.gameObject.AddComponent<HDAdditionalLightData>();
            lamp.intensity = intensity * Mathf.PI * Mathf.Max(0, lampBrightness?.Value ?? 1); // built-in brightness to candela, as for the sun
            hd.fadeDistance = distance;
            hd.affectsVolumetric = false;
            lamp.enabled = false; lamp.enabled = true; // registered again as a real-time light
            count++;
        }
        return count;
    }

    internal static void Apply(CustomMapManifest.Lighting? recorded, Scene carrier, IEnumerable<GameObject> roots, GameObject environment, List<UnityEngine.Object> owned, List<Action> undo)
    {
        try
        {
            var all = roots.ToList();
            if (all.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Any(t => t.name.StartsWith(OptOut, StringComparison.Ordinal))) return;
            var renderers = all.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).ToList();
            bool lightmapped = renderers.Any(r => r.lightmapIndex is >= 0 and < 65534);
            if (!lightmapped && recorded?.CustomSky != true) return; // models without scene lighting use the game's
            // Maps converted before the importer recorded lighting: a dim neutral sky, as a lightmapped map's ambient.
            static Color C(CustomMapManifest.Rgb c) => new(c.R, c.G, c.B);
            var neutral = new CustomMapManifest.Rgb(.18f, .17f, .17f);
            var light = recorded ?? new CustomMapManifest.Lighting("Flat", neutral, neutral, neutral, 1, false, false, neutral, neutral, 1, false, neutral, "Linear", 0, 0);

            // Lightmaps are left to Unity: reading or setting LightmapSettings.lightmaps makes the runtime create
            // LightmapData objects, which crashes the mod runtime in this game build (crash dumps 2026-10-10 20:48, 20:58).
            if (lightmapped)
            {
                // A statically batched mesh already carries its lightmap UVs scaled into its tile, but the renderer keeps the
                // tile's scale/offset and this game applied it again: every piece read one wrong texel (flat blocks, no
                // lamp pools on Demo City, 2026-10-11). Identity is what Unity means for batched renderers anyway.
                int batched = 0;
                foreach (var renderer in renderers)
                    if (renderer.isPartOfStaticBatch && renderer.lightmapIndex is >= 0 and < 65534)
                    { renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0); batched++; }
                Plugin.ModLog.LogInfo($"Map lighting: lightmap tiles reset on {batched} batched renderers");
                Plugin.ModLog.LogInfo("Map lighting: light probes"); // breadcrumbs: the last one logged shows where a failure happened
                LightProbes.Tetrahedralize(); // the map's probes light tanks with its baked light
            }

            Plugin.ModLog.LogInfo("Map lighting: sky and exposure");
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            owned.Add(profile);
            T Add<T>() where T : VolumeComponent => profile.Add(Il2CppType.Of<T>(), true).Cast<T>();
            var fixedExposure = Add<Exposure>();
            fixedExposure.mode.Override(ExposureMode.Fixed);
            fixedExposure.fixedExposure.Override(exposure?.Value ?? -0.263f);
            var visual = Add<VisualEnvironment>();
            visual.cloudType.Override(0);
            visual.skyAmbientMode.Override(SkyAmbientMode.Dynamic); // the sky below lights the scene, as the map's ambient did
            Add<VolumetricClouds>().enable.Override(false);
            // Unity's built-in renderer and URP's default profile draw without tonemapping or bloom, as the map was made;
            // the game's ACES darkened the night and greyed the lamps' warm light (Demo City, 2026-10-11).
            Add<Tonemapping>().mode.Override(TonemappingMode.None);
            Add<Bloom>().intensity.Override(0);
            // The game's default profile turns on screen-space GI, which replaces lightmaps and light probes: a baked
            // map then showed none of its lamp light (Demo City, 2026-10-11).
            if (lightmapped) Add<GlobalIllumination>().enable.Override(false);
            visual.skyType.Override((int)SkyType.Gradient);
            var gradient = Add<GradientSky>();
            var (top, middle, bottom) = light.AmbientMode switch
            {
                "Trilight" => (C(light.Sky), C(light.Equator), C(light.Ground)),
                "Flat" => (C(light.Sky), C(light.Sky), C(light.Sky)),
                // Skybox ambient from a procedural sky: its tint above, its ground colour below.
                _ when light.ProceduralSky => (C(light.SkyTint) * .5f * light.SkyExposure * light.AmbientIntensity,
                    Color.Lerp(C(light.SkyTint), C(light.SkyGround), .5f) * .5f * light.SkyExposure * light.AmbientIntensity,
                    C(light.SkyGround) * light.SkyExposure * light.AmbientIntensity),
                _ => (C(light.Sky) * light.AmbientIntensity, C(light.Equator) * light.AmbientIntensity, C(light.Ground) * light.AmbientIntensity),
            };
            gradient.top.Override(top); gradient.middle.Override(middle); gradient.bottom.Override(bottom);
            gradient.gradientDiffusion.Override(1);
            var mist = Add<Fog>();
            mist.enabled.Override(light.Fog);
            if (light.Fog)
            {
                mist.colorMode.Override(FogColorMode.ConstantColor);
                mist.color.Override(C(light.FogColor));
                mist.albedo.Override(C(light.FogColor));
                mist.meanFreePath.Override(light.FogMode == "Linear" ? Mathf.Max(1, light.FogEnd / 3) : Mathf.Max(1, 1 / Mathf.Max(1e-4f, light.FogDensity)));
            }
            var holder = new GameObject("Map lighting");
            holder.transform.SetParent(environment.transform, false);
            var volume = holder.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 1000; volume.sharedProfile = profile;

            // The carrier's reflection probes hold its daylight: at the map's exposure everything inside them reflects
            // it as glare (Sandbox's covers 750 x 2000 m up to 14 m high: Demo City's streets went white, 2026-10-11).
            foreach (var probe in carrier.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ReflectionProbe>(true)))
            {
                var probeObject = probe.gameObject;
                if (!probeObject.activeSelf) continue;
                probeObject.SetActive(false);
                undo.Add(() => { if (probeObject) probeObject.SetActive(true); });
            }

            Plugin.ModLog.LogInfo("Map lighting: sun");
            // The map's sun: a real-time one drives the game's sun; a baked one (or none) means no real-time sun.
            // ponytail: exported scenes often lose the light's bake record (Demo City's baked moon reads "not baked"), so a
            // lightmapped map's sun counts as real-time only when the record says so. Record the bake mode in the
            // manifest if a lightmapped map with a real-time sun turns up.
            var mapSun = all.SelectMany(r => r.GetComponentsInChildren<Light>(true)).Where(l => l.type == LightType.Directional)
                .OrderByDescending(l => l.enabled).ThenByDescending(l => l.intensity).FirstOrDefault();
            var bake = mapSun?.bakingOutput ?? default;
            bool realtimeSun = mapSun != null && mapSun.enabled
                && (lightmapped ? bake.isBaked && bake.lightmapBakeType != LightmapBakeType.Baked : !(bake.isBaked && bake.lightmapBakeType == LightmapBakeType.Baked));
            foreach (var sun in carrier.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).Where(l => l.type == LightType.Directional && l.enabled))
            {
                var (rotation, colour, intensity, temperature) = (sun.transform.rotation, sun.color, sun.intensity, sun.useColorTemperature);
                undo.Add(() => { if (sun) { sun.enabled = true; sun.transform.rotation = rotation; sun.color = colour; sun.intensity = intensity; sun.useColorTemperature = temperature; } });
                if (!realtimeSun) { sun.enabled = false; continue; }
                sun.transform.rotation = mapSun!.transform.rotation;
                sun.color = mapSun.color; sun.useColorTemperature = false;
                sun.intensity = mapSun.intensity * Mathf.PI; // lux matching the built-in renderer at exposure 1
            }
            if (realtimeSun) mapSun!.enabled = false; // the game's sun stands in for it
            Plugin.ModLog.LogInfo("Map lighting: lamps");
            try
            {
                int lamps = LiveLamps(all);
                if (lamps > 0) Plugin.ModLog.LogInfo($"Map lighting: {lamps} baked lamps also shine in real time within {lampDistance?.Value ?? 150} m, at {lampBrightness?.Value ?? 1}x their brightness.");
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"The map's lamps stay baked only: {ex.Message}"); }
            Plugin.ModLog.LogInfo($"Map lighting: the map's own ({(lightmapped ? "lightmaps, " : "")}{(realtimeSun ? "its sun" : "no real-time sun")}, " +
                $"{(light.Fog ? "fog" : "no fog")}, {(recorded == null ? "a neutral sky: the map file has no recorded lighting" : light.AmbientMode + " ambient")}, exposure {exposure?.Value ?? -0.263f}).");
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"The map's own lighting could not be used; the game's stays: {ex.Message}"); }
    }

    /// The merge moves the map into the carrier scene; its light probes are registered again for tanks.
    internal static void AfterMerge() => LightProbes.Tetrahedralize();
}
