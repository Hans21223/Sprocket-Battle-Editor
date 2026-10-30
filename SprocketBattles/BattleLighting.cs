using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.CustomBattles;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SprocketBattles;

/// A battle's lighting lease. Native day/night controls run only when a choice changes; returning to
/// Map default or leaving the owning battle puts back the exact map state, including imported lighting.
internal static class BattleLighting
{
    static LightingScope? scope;
    static string? failed;

    internal static void Update(DeathmatchGameMode? mode, BattleFile? battle, Camera? camera)
    {
        if (mode == null || battle == null || !BattleTimeOfDay.TryGet(battle.TimeOfDay, out var preset))
        { Restore(); return; }
        var scene = mode.gameObject.scene;
        if (!scene.IsValid() || !scene.isLoaded) { Restore(); return; }
        if (scope != null && !scope.Owns(mode)) Restore();
        // A retry can reapply native environment configuration. Reapply the choice after setup finishes.
        if (mode.gameState != DeathmatchGameMode.ModeState.Running || Battle.RestartQueued)
        { if (scope != null) scope.Applied = null; return; }
        if (MapBridge.Call("MapNotReadyReason", Battle.Map(mode)) != null) return;
        string key = mode.GetInstanceID() + ":" + preset.Name;
        if (failed == key) return;
        if (scope?.Applied == preset.Name) { scope.Track(camera); return; }
        try
        {
            scope ??= new LightingScope(mode, MapBridge.Call("CustomMapLoadName", Battle.Map(mode)) != null);
            scope.Apply(preset);
            failed = null;
            string clock = TimeSpan.FromHours(preset.Hour).ToString(@"hh\:mm");
            Trace.Write($"Time of day: {preset.Name} ({clock}) on {Battle.Map(mode)}");
        }
        catch (Exception ex)
        {
            Restore();
            failed = key;
            Trace.Write($"Time of day could not be applied: {ex}");
            BattleEditor.Tell("Couldn't change the time of day. The map's lighting was restored; see SprocketBattles-trace.log.");
        }
    }

    internal static void Restore()
    {
        var previous = scope;
        scope = null;
        failed = null;
        previous?.Restore();
    }

    sealed class LightingScope
    {
        readonly DeathmatchGameMode owner;
        readonly Scene scene;
        readonly int ownerId;
        readonly bool customMap;
        readonly EnvironmentController? controller;
        readonly List<LightState> lights;
        readonly LensFlareComponentSRP? flare;
        readonly float flareIntensity;
        readonly Volume? nativeVolume;
        readonly ColorParameter? cloudTint;
        readonly Color cloudColour;
        readonly bool cloudOverride;
        readonly List<Object> created = new();
        readonly List<Volume> mapLighting = new();
        GameObject? holder, disc;
        GradientSky? gradient;
        Exposure? exposure;
        IndirectLightingController? indirect;
        Material? discMaterial;
        Light? fallbackSun, shown;
        readonly string? map;
        bool dark;
        float lampExposure = float.NaN; // what the map's lamps were last set for (NaN: as the map made them)
        public string? Applied;
        const float NoonLux = 110000; // the Noon preset's sun, which the game's exposure suits

        internal LightingScope(DeathmatchGameMode mode, bool custom)
        {
            owner = mode; ownerId = mode.GetInstanceID(); scene = mode.gameObject.scene; customMap = custom; map = Battle.Map(mode);
            var controllers = Object.FindObjectsOfType<EnvironmentController>();
            controller = controllers.FirstOrDefault(c => c != null && c.gameObject.scene.handle == scene.handle);
            var directional = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true))
                .Where(l => l != null && l.type == LightType.Directional).ToList();
            if (controller?.sun != null) directional.Add(controller.sun);
            if (controller?.moon != null) directional.Add(controller.moon);
            lights = directional.GroupBy(l => l.Pointer).Select(g => new LightState(g.First())).ToList();
            if (controller?.sun != null) flare = controller.sun.GetComponent<LensFlareComponentSRP>();
            if (flare != null) flareIntensity = flare.intensity;
            nativeVolume = controller?.skyVolume;
            if (nativeVolume != null)
            {
                // The native setter also uses this instance. Never mutate the shared profile asset.
                var profile = nativeVolume.profile;
                if (profile != null)
                    foreach (var part in profile.components)
                        if (part?.TryCast<CloudLayer>() is { } clouds)
                        { cloudTint = clouds.layerA?.tint; break; }
                if (cloudTint != null) { cloudColour = cloudTint.value; cloudOverride = cloudTint.overrideState; }
            }
            // The Map Framework's own lighting (its MapLighting volumes, by name), which a chosen time replaces.
            if (custom) mapLighting.AddRange(Object.FindObjectsOfType<Volume>().Where(v => v.enabled && v.gameObject.name is "Map lighting" or "Map sky and sun"));
        }

        internal bool Owns(DeathmatchGameMode mode) => owner != null && mode.GetInstanceID() == ownerId
            && mode.gameObject.scene.handle == scene.handle && scene.IsValid() && scene.isLoaded;

        internal void Apply(BattleTimeOfDay.Preset preset)
        {
            if (fallbackSun != null) fallbackSun.enabled = false;
            if (controller != null)
            {
                bool bake = controller.autoBake;
                try
                {
                    // SetConfig rebakes probes and changes unrelated weather; the native time setter does neither
                    // while autoBake is suspended, and respects the map's latitude and sunset bearing.
                    controller.autoBake = false;
                    controller.SetTimeCycleFraction(preset.CycleFraction);
                }
                finally { if (controller != null) controller.autoBake = bake; }
            }
            Light? native = controller == null ? null : preset.CycleFraction < .5f ? controller.sun : controller.moon;
            if (customMap || native == null)
            {
                // A custom map's own lighting (fixed exposure, no tonemapping, its sky) under the game's noon sun drew a black
                // sky and white-hot ground, and the game's own sky doesn't show there either: black sky, black shadows
                // (Demo City, 2026-10-27 and -30). So the map's volumes go off and the time brings its own sky.
                foreach (var volume in mapLighting) if (volume != null) volume.enabled = false;
                EnsureSky();
                // A dimmer sun than noon's is half made up for, as an eye adapts (Sunset was too dark, 2026-10-30), the
                // shade gets the other half too, up to 4x (tanks in a Sunset street went black), and the sky brightens to
                // match, so a low sun's sky outshines its ground.
                float dim = Mathf.Log(NoonLux / Mathf.Max(preset.SunLux, .01f), 2);
                dark = dim > 3; // Dawn, Sunset and Night: the map's lamps shine
                exposure?.compensation.Override(dim / 2);
                indirect?.indirectDiffuseLightingMultiplier.Override(Mathf.Min(Mathf.Pow(2, dim / 2), 4));
                if (gradient != null)
                {
                    gradient.top.Override(Colour(preset.SkyTop));
                    gradient.middle.Override(Colour(preset.SkyMiddle));
                    gradient.bottom.Override(Colour(preset.SkyBottom));
                    gradient.multiplier.Override(preset.SunLux * .1f * Mathf.Pow(2, dim / 2)); // relative colours: at noon the sky gives about a sixth of the sun's light
                }
                discMaterial?.SetColor("_UnlitColor", Colour(preset.SunColor) * 3);
                // Imported maps may have disabled/dimmed the native sun. An explicit battle choice temporarily
                // replaces those directional lights; point lamps and baked lightmaps remain map-owned.
                foreach (var light in lights)
                    if (light.Item != null && light.Item != controller?.sun && light.Item != controller?.moon)
                        light.Item.enabled = false;
                Light? active = native;
                if (active == null) active = EnsureFallbackSun();
                if (active != null)
                {
                    if (active == fallbackSun)
                        active.transform.rotation = Quaternion.Euler(preset.SunElevation, preset.SunAzimuth, 0);
                    active.useColorTemperature = false;
                    active.color = Colour(preset.SunColor);
                    active.intensity = preset.SunLux; // HDRP directional-light intensity is lux.
                    active.enabled = true;
                }
                shown = active;
            }
            Applied = preset.Name;
        }

        /// Each frame: the map's lamps follow the camera's exposure, and a far, unlit disc stands where the light comes
        /// from (a gradient sky has no sun).
        internal void Track(Camera? camera)
        {
            if (camera == null) return;
            if (customMap) Lamps(camera);
            if (disc == null || shown == null) return;
            var toward = -shown.transform.forward;
            disc.SetActive(shown.enabled && toward.y > -.02f);
            float distance = camera.farClipPlane * .9f;
            disc.transform.position = camera.transform.position + toward * distance;
            disc.transform.localScale = Vector3.one * (distance * .026f); // 1.5 degrees across, three times the real sun
        }

        /// The map's own lamps and lit windows: off by day; at Dawn, Sunset and Night as bright on screen as under the
        /// map's own lighting, at the exposure the camera used last frame.
        void Lamps(Camera camera)
        {
            float ev = float.NegativeInfinity;
            if (dark && HDCamera.GetOrCreate(camera, 0).volumeStack?.GetComponent(Il2CppType.Of<Exposure>())?.TryCast<Exposure>() is { } e)
                // shortcut: an automatic exposure's level isn't readable here, so it counts as 13 (what the game's maps showed); read the exposure texture if one is.
                ev = (e.mode.value == ExposureMode.Fixed ? e.fixedExposure.value : 13) - e.compensation.value;
            if (ev == lampExposure || Mathf.Abs(ev - lampExposure) < .1f) return;
            lampExposure = ev;
            MapBridge.Call("MapLamps", map, ev);
        }

        void EnsureSky()
        {
            if (holder != null) return;
            holder = new GameObject("Battle time of day");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            created.Add(profile);
            T Add<T>() where T : VolumeComponent
            {
                var component = profile.Add(Il2CppType.Of<T>(), true).Cast<T>();
                created.Add(component);
                return component;
            }
            // The sky also lights the shadows (the game's screen-space GI falls back to it); the game's exposure stays.
            var visual = Add<VisualEnvironment>();
            visual.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            visual.skyType.Override((int)SkyType.Gradient);
            gradient = Add<GradientSky>();
            gradient.skyIntensityMode.Override(SkyIntensityMode.Multiplier); // HDRP reads the multiplier only in this mode
            gradient.gradientDiffusion.Override(1);
            exposure = profile.Add(Il2CppType.Of<Exposure>(), false).Cast<Exposure>(); // only its compensation is set
            created.Add(exposure);
            indirect = profile.Add(Il2CppType.Of<IndirectLightingController>(), false).Cast<IndirectLightingController>();
            created.Add(indirect);
            // The game sun's screen-space contact shadows smeared into small dark blots on tanks in this light (2026-10-30).
            Add<ContactShadows>().enable.Override(false);
            var volume = holder.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 2000; volume.sharedProfile = profile;
            if (Shader.Find("HDRP/Unlit") is { } unlit)
            {
                disc = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                disc.name = "Battle time of day sun disc";
                Object.Destroy(disc.GetComponent<Collider>());
                disc.transform.SetParent(holder.transform, false);
                var renderer = disc.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sharedMaterial = discMaterial = new Material(unlit);
                created.Add(discMaterial);
            }
        }

        Light EnsureFallbackSun()
        {
            if (fallbackSun != null) return fallbackSun;
            var sun = new GameObject("Battle time of day sun");
            sun.transform.SetParent(holder!.transform, false);
            fallbackSun = sun.AddComponent<Light>();
            fallbackSun.type = LightType.Directional;
            fallbackSun.shadows = LightShadows.Soft;
            sun.AddComponent<HDAdditionalLightData>();
            return fallbackSun;
        }

        internal void Restore()
        {
            // Remove the override immediately; Destroy alone leaves it active until the end of this frame.
            if (holder != null) Safe("override volume", () => { holder.SetActive(false); Object.Destroy(holder); });
            holder = null; disc = null; gradient = null; exposure = null; indirect = null; discMaterial = null; fallbackSun = null; shown = null;
            foreach (var volume in mapLighting) if (volume != null) Safe("map lighting", () => volume.enabled = true);
            if (!float.IsNaN(lampExposure)) Safe("map lamps", () => MapBridge.Call("MapLamps", map, float.NaN));
            foreach (var light in lights) Safe("directional light", light.Restore);
            if (flare != null) Safe("sun flare", () => flare.intensity = flareIntensity);
            if (nativeVolume != null && cloudTint != null)
                Safe("cloud tint", () => { cloudTint.value = cloudColour; cloudTint.overrideState = cloudOverride; });
            foreach (var item in created) if (item != null) Safe("owned profile", () => Object.Destroy(item));
            created.Clear();
            Applied = null;
        }
    }

    sealed class LightState
    {
        internal readonly Light Item;
        readonly Quaternion rotation;
        readonly Color colour;
        readonly float intensity;
        readonly bool enabled, temperature;
        internal LightState(Light light)
        {
            Item = light; rotation = light.transform.rotation; colour = light.color;
            intensity = light.intensity; enabled = light.enabled; temperature = light.useColorTemperature;
        }
        internal void Restore()
        {
            if (Item == null) return;
            Item.transform.rotation = rotation; Item.color = colour; Item.intensity = intensity;
            Item.useColorTemperature = temperature; Item.enabled = enabled;
        }
    }

    static Color Colour(BattleTimeOfDay.Rgb c) => new(c.R, c.G, c.B);
    static void Safe(string what, Action restore)
    {
        try { restore(); }
        catch (Exception ex) { Trace.Write($"Time of day: couldn't restore {what}: {ex.Message}"); }
    }
}
