using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
    static ConfigEntry<bool>? sourceLighting;

    internal static void Configure(ConfigFile config)
    {
        exposure = config.Bind("Lighting", "Map lighting exposure", -0.263f,
            "Exposure (EV100) for maps that bring their own lighting. -0.263 shows them at the brightness Unity's built-in renderer gave them; higher is darker.");
        lampDistance = config.Bind("Lighting", "Real-time lamp distance", 150f,
            "Lamps baked into a map also shine in real time within this many metres of the camera, so they light tanks. 0 keeps them baked only; lower it if a lamp-heavy map runs slowly.");
        lampBrightness = config.Bind("Lighting", "Real-time lamp brightness", 1f,
            "Multiplies those real-time lamps' brightness. 1 is as the map's author set them; map lamps are often authored dim " +
            "(Demo City's street lamps: about 6 candela, 16-19 m up), so raise it (4-10) for lamps that visibly light streets and tanks.");
        sourceLighting = config.Bind("Lighting", "GMod map light and sky", true,
            "Garry's Mod maps use their own sun, sky, fog and lamps. Off: the game's daylight on them, as before 1.1.0.");
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
            DisableCarrierProbes(carrier, undo);

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

    /// The carrier's reflection probes hold its own daylight and sky: under a map's own lighting everything inside them
    /// reflects that as glare (Sandbox's covers 750 x 2000 m up to 14 m high: Demo City's streets went white, 2026-10-11).
    static void DisableCarrierProbes(Scene carrier, List<Action> undo)
    {
        foreach (var probe in carrier.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ReflectionProbe>(true)))
        {
            var probeObject = probe.gameObject;
            if (!probeObject.activeSelf) continue;
            probeObject.SetActive(false);
            undo.Add(() => { if (probeObject) probeObject.SetActive(true); });
        }
    }

    /// The merge moves the map into the carrier scene; its light probes are registered again for tanks.
    internal static void AfterMerge() => LightProbes.Tetrahedralize();

    /// A Garry's Mod map's own light and sky (SourceLighting), at the game's own exposure: its sun's direction and colour;
    /// its sky (the skybox images as an HDRI cubemap, or GMod's painted sky as a gradient) lighting the scene as the map's
    /// ambient did; its fog; and its lamps as real-time lights near the camera (as the baked lamps of Unity maps).
    /// Brightness: a daylight sun (Source brightness 250 or more) is the game's sun, as Source's auto-exposure adapted to
    /// it; a dimmer one keeps its share of 250, so night maps stay dark. The ambient and lamps keep their share of the sun.
    /// Anything unavailable keeps the game's.
    internal static unsafe void ApplySource(SourceLighting.Settings source, string folder, Scene carrier, IEnumerable<GameObject> roots,
        GameObject environment, Bounds bounds, List<UnityEngine.Object> owned, List<Action> undo)
    {
        if (sourceLighting?.Value == false
            || roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Any(t => t.name.StartsWith(OptOut, StringComparison.Ordinal))) return;
        static float Luminance(float[] c) => .2126f * c[0] + .7152f * c[1] + .0722f * c[2];
        static float Encode(float x) => x <= .0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - .055f;
        static Color Gamma(float[] c) => new(Encode(c[0]), Encode(c[1]), Encode(c[2])); // light and sky colours are given in gamma space
        var suns = carrier.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).Where(l => l.type == LightType.Directional && l.enabled).ToList();
        float sunLux = suns.Count > 0 ? suns.Max(l => l.intensity) : 100000;
        // The game's lux for one unit of Source light.
        float sunSource = source.SunDirection != null ? source.SunBrightness * Luminance(source.SunColor) : 0;
        float unit = sunLux / Mathf.Max(250, sunSource);

        if (source.SunDirection is { Length: 3 } aim)
            foreach (var sun in suns)
            {
                var (rotation, colour, intensity, temperature) = (sun.transform.rotation, sun.color, sun.intensity, sun.useColorTemperature);
                undo.Add(() => { if (sun) { sun.transform.rotation = rotation; sun.color = colour; sun.intensity = intensity; sun.useColorTemperature = temperature; } });
                sun.transform.rotation = Quaternion.LookRotation(new Vector3(aim[0], aim[1], aim[2]));
                sun.color = Gamma(source.SunColor); sun.useColorTemperature = false; // the colour carries its luminance
                sun.intensity = intensity / sunLux * unit * source.SunBrightness;
            }

        // The sky lights the scene as the map's ambient did: Source's ambient is what its sky gives a surface facing up.
        float skyLux = Mathf.Clamp(source.AmbientBrightness * Luminance(source.AmbientColor) * unit, .002f * sunLux, sunLux);
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        owned.Add(profile);
        T Add<T>() where T : VolumeComponent => profile.Add(Il2CppType.Of<T>(), true).Cast<T>();
        string? sky = null;
        VisualEnvironment Sky()
        {
            var visual = Add<VisualEnvironment>();
            visual.cloudType.Override(0);
            visual.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            Add<VolumetricClouds>().enable.Override(false);
            return visual;
        }
        if (source.Cubemap != null && source.CubemapUpperLux > 0 && File.Exists(Path.Combine(folder, source.Cubemap)))
        {
            var (n, faces) = SourceLighting.ReadCube(File.ReadAllBytes(Path.Combine(folder, source.Cubemap)));
            // sRGB, as the images and the brightness maths are: Cubemap's TextureFormat constructors are always linear.
            const GraphicsFormat Srgb = GraphicsFormat.R8G8B8A8_SRGB;
            var cube = new Cubemap(n, Srgb, TextureCreationFlags.None) { name = "Map sky" };
            owned.Add(cube);
            cube.Apply(false, true); // the faces are copied on the GPU
            for (int face = 0; face < 6; face++)
            {
                var image = new Texture2D(n, n, Srgb, TextureCreationFlags.None);
                fixed (byte* pixels = faces[face]) image.LoadRawTextureData((IntPtr)pixels, faces[face].Length);
                image.Apply(false, false);
                Graphics.CopyTexture(image, 0, 0, cube, face, 0);
                UnityEngine.Object.Destroy(image);
            }
            Sky().skyType.Override((int)SkyType.HDRI);
            var hdri = Add<HDRISky>();
            hdri.hdriSky.Override(cube);
            hdri.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
            hdri.multiplier.Override(skyLux / source.CubemapUpperLux);
            sky = $"its skybox ({n} px)";
        }
        else if (source.GradientTop is { Length: 3 } top && source.GradientHorizon is { Length: 3 } horizon && source.GradientBottom is { Length: 3 } bottom)
        {
            Sky().skyType.Override((int)SkyType.Gradient);
            var gradient = Add<GradientSky>();
            gradient.top.Override(Gamma(top)); gradient.middle.Override(Gamma(horizon)); gradient.bottom.Override(Gamma(bottom));
            gradient.gradientDiffusion.Override(1);
            gradient.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
            // A cosine-weighted average of the upper half: mostly the top colour.
            gradient.multiplier.Override(skyLux / Mathf.Max(1e-4f, Mathf.PI * (Luminance(top) * 2 + Luminance(horizon)) / 3));
            sky = "its painted sky";
        }
        if (source.FogColor is { Length: 3 } fogColour)
        {
            var mist = Add<Fog>();
            mist.enabled.Override(true);
            // Source fogs the world but never its skybox. HDRP fogs everything, so with the map's own sky the fog takes the
            // sky's colour behind it (sharp): fogged scenery fades into the sky, and the sky keeps its look.
            if (sky != null) { mist.colorMode.Override(FogColorMode.SkyColor); mist.mipFogMaxMip.Override(0); }
            else { mist.colorMode.Override(FogColorMode.ConstantColor); mist.color.Override(new Color(fogColour[0], fogColour[1], fogColour[2])); }
            mist.albedo.Override(new Color(fogColour[0], fogColour[1], fogColour[2]));
            // Thickest at the map's fog end, and no thicker beyond: its maximum density there.
            float end = Mathf.Max(5, source.FogEnd), density = Mathf.Clamp(source.FogMaxDensity, .05f, .99f);
            mist.maxFogDistance.Override(end);
            mist.meanFreePath.Override(Mathf.Max(1, end / -Mathf.Log(1 - density)));
            // Height fog thins out above its base: the whole map stays under it.
            mist.baseHeight.Override(bounds.max.y);
            mist.maximumHeight.Override(bounds.max.y + 500);
        }
        var holder = new GameObject("Map sky and sun");
        holder.transform.SetParent(environment.transform, false);
        var volume = holder.AddComponent<Volume>();
        volume.isGlobal = true; volume.priority = 1000; volume.sharedProfile = profile;
        DisableCarrierProbes(carrier, undo);

        // Lamps: a lamp of brightness B lights a wall 100 units (2.54 m) away as a sun of brightness B would (VRAD's scale).
        int lamps = 0;
        float distance = lampDistance?.Value ?? 150, boost = Mathf.Max(0, lampBrightness?.Value ?? 1);
        if (distance > 0 && source.Lamps.Count > 0)
        {
            var group = new GameObject("Map lamps");
            group.transform.SetParent(environment.transform, false);
            foreach (var lamp in source.Lamps)
            {
                if (lamp.Position is not { Length: 3 } at || lamp.Color is not { Length: 3 }) continue;
                var go = new GameObject("Lamp");
                go.transform.SetParent(group.transform, false);
                go.transform.position = new Vector3(at[0], at[1], at[2]);
                var light = go.AddComponent<Light>();
                bool spot = lamp.Direction is { Length: 3 };
                light.type = spot ? LightType.Spot : LightType.Point;
                if (spot)
                {
                    go.transform.rotation = Quaternion.LookRotation(new Vector3(lamp.Direction![0], lamp.Direction[1], lamp.Direction[2]));
                    light.spotAngle = lamp.Outer; light.innerSpotAngle = lamp.Inner;
                }
                light.color = Gamma(lamp.Color);
                light.shadows = LightShadows.None;
                // Where it falls to a hundredth of its brightness at 2.54 m, within reason.
                light.range = Mathf.Clamp(25.4f * Mathf.Sqrt(Mathf.Max(lamp.Brightness, 1) / 200f), 4, 60);
                var hd = go.AddComponent<HDAdditionalLightData>();
                hd.SetIntensity(unit * lamp.Brightness * 2.54f * 2.54f * boost, LightUnit.Candela);
                hd.fadeDistance = distance;
                hd.affectsVolumetric = false;
                lamps++;
            }
        }
        Plugin.ModLog.LogInfo($"Map lighting: the Garry's Mod map's own ({(source.SunDirection != null ? "its sun" : "the game's sun")}, {sky ?? "the game's sky"}, "
            + $"{(source.FogColor != null ? "its fog" : "no fog")}, {lamps} lamps within {distance} m).");
    }
}
