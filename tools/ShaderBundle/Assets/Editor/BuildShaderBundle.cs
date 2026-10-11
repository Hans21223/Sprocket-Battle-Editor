// Builds sprocketmaps-shaders.bundle: HDRP/Lit compiled by the game's own Unity 6000.3.21f1 / HDRP 17.3.0, with the
// material features imported maps need (alpha test, both sides, transparency, normal, mask and emission maps) and the
// global keyword combinations the game's own HDRP/Lit was compiled with (game-lit-globals.json, read from the game),
// plus their non-directional lightmap versions.
// The game's copy has only the variants its own materials use, so imported cutouts and transparents had none.
//
//   Unity.exe -batchmode -quit -projectPath tools/ShaderBundle -executeMethod BuildShaderBundle.Build -logFile build.log
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class BuildShaderBundle
{
    const string Folder = "Assets/Bundle", PipelinePath = "Assets/Bundle/HDRP.asset";
    internal const string BundleName = "sprocketmaps-shaders.bundle";

    public static void Build()
    {
        SetUpPipeline();
        var assets = CreateMaterials();
        var output = Path.GetFullPath("Build");
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets };
        if (BuildPipeline.BuildAssetBundles(output, new[] { build },
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64) == null)
            throw new InvalidOperationException("The shader bundle build failed.");
        Debug.Log($"SHADER_BUNDLE_OK: {Path.Combine(output, BundleName)}; variants kept {KeepGameVariants.Kept}, removed {KeepGameVariants.Removed}");
    }

    // The game's pipeline features, as its compiled keywords show; anything off here would be stripped before the
    // filter below could choose it.
    static void SetUpPipeline()
    {
        Directory.CreateDirectory(Folder);
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
        var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelinePath);
        if (asset == null) { asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>(); AssetDatabase.CreateAsset(asset, PipelinePath); }
        var s = asset.currentPlatformRenderPipelineSettings;
        s.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.Both;
        s.supportDecals = true; s.decalSettings.perChannelMask = true; s.supportSurfaceGradient = true; s.supportDecalLayers = true;
        s.supportLightLayers = true; s.renderingLayerMaskBuffer = true;
        s.hdShadowInitParams.punctualShadowFilteringQuality = HDShadowFilteringQuality.High;
        s.hdShadowInitParams.directionalShadowFilteringQuality = HDShadowFilteringQuality.High;
        s.hdShadowInitParams.areaShadowFilteringQuality = HDAreaShadowFilteringQuality.Medium;
        s.hdShadowInitParams.supportScreenSpaceShadows = true;
        s.supportDitheringCrossFade = true;
        s.msaaSampleCount = MSAASamples.MSAA2x;
        s.supportShadowMask = false;
        s.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.LegacyLightProbes;
        s.gpuResidentDrawerSettings.mode = GPUResidentDrawerMode.InstancedDrawing;
        asset.currentPlatformRenderPipelineSettings = s;
        EditorUtility.SetDirty(asset);
        GraphicsSettings.defaultRenderPipeline = asset;
        for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = asset; }
        // Keep lightmap, instancing and BatchRendererGroup variants for the filter to choose from.
        var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        foreach (var (name, value) in new[] { ("m_LightmapStripping", 1), ("m_LightmapKeepPlain", 1), ("m_LightmapKeepDirCombined", 1),
                     ("m_LightmapKeepDynamicPlain", 1), ("m_LightmapKeepDynamicDirCombined", 1), ("m_LightmapKeepShadowMask", 1),
                     ("m_LightmapKeepSubtractive", 1), ("m_InstancingStripping", 2), ("m_BrgStripping", 2) })
        {
            var property = graphics.FindProperty(name);
            if (property == null) Debug.LogWarning("Graphics setting not found: " + name); else property.intValue = value;
        }
        graphics.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }

    // One material per combination of the features the runtime asks for; their keywords select the variants built.
    static string[] CreateMaterials()
    {
        var lit = Shader.Find("HDRP/Lit") ?? throw new InvalidOperationException("HDRP/Lit is unavailable.");
        Texture2D Tex(string name, Color color)
        {
            var path = $"{Folder}/{name}.asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null) return texture;
            texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = name };
            texture.SetPixels(Enumerable.Repeat(color, 16).ToArray()); texture.Apply();
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }
        // Shipped too: a flat normal and a neutral mask, for materials that have only one of the two.
        var flatNormal = Tex("FlatNormal", new Color(.5f, .5f, 1, .5f));
        var neutralMask = Tex("NeutralMask", new Color(0, 1, 0, 1));
        var paths = new List<string> { AssetDatabase.GetAssetPath(flatNormal), AssetDatabase.GetAssetPath(neutralMask) };
        foreach (bool maps in new[] { false, true })
            foreach (string surface in new[] { "Opaque", "Cutout", "Transparent" })
                foreach (bool twoSided in new[] { false, true })
                foreach (bool emissive in new[] { false, true })
                {
                    // Emission maps (lit windows, signs) on opaque and cut-out surfaces; glowing glass is rare.
                    if (emissive && surface == "Transparent") continue;
                    var material = new Material(lit) { name = $"{surface}{(twoSided ? "TwoSided" : "")}{(maps ? "Mapped" : "")}{(emissive ? "Emissive" : "")}", enableInstancing = true };
                    if (maps) { material.SetTexture("_NormalMap", flatNormal); material.SetTexture("_MaskMap", neutralMask); }
                    if (emissive) { material.SetTexture("_EmissiveColorMap", neutralMask); material.SetColor("_EmissiveColor", Color.white); }
                    HDMaterial.SetSurfaceType(material, surface == "Transparent");
                    HDMaterial.SetAlphaClipping(material, surface == "Cutout");
                    if (twoSided) material.SetFloat("_DoubleSidedEnable", 1);
                    HDMaterial.ValidateMaterial(material);
                    var path = $"{Folder}/{material.name}.mat";
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(material, path);
                    paths.Add(path);
                    Debug.Log($"SHADER_BUNDLE_MATERIAL {material.name}: {string.Join(" ", material.shaderKeywords.OrderBy(k => k))}");
                }
        AssetDatabase.SaveAssets();
        return paths.ToArray();
    }
}

/// Keeps exactly the game's own global keyword combinations, per pass, for HDRP/Lit's fragment programs; material
/// keywords (those starting with '_', HDRP's convention) are left to the materials above.
class KeepGameVariants : IPreprocessShaders
{
    internal static int Kept, Removed;
    internal static bool KeepRayTracingPasses;
    public int callbackOrder => 1000; // after HDRP's own stripping
    static Dictionary<string, HashSet<string>> allowed;

    static Dictionary<string, HashSet<string>> Allowed()
    {
        if (allowed != null) return allowed;
        var json = JsonUtility.FromJson<GameVariants>(File.ReadAllText(Path.GetFullPath("game-lit-globals.json")));
        allowed = json.passes.ToDictionary(p => p.name, p => new HashSet<string>(p.sets.Select(s => Key(s.keywords))));
        // Imported maps bring non-directional lightmaps; the game's own are all directional. For every lightmapped
        // combination keep the same one without DIRLIGHTMAP_COMBINED too.
        foreach (var pass in json.passes)
            foreach (var set in pass.sets)
                if (set.keywords.Contains("LIGHTMAP_ON") && set.keywords.Contains("DIRLIGHTMAP_COMBINED"))
                    allowed[pass.name].Add(Key(set.keywords.Where(k => k != "DIRLIGHTMAP_COMBINED")));
        return allowed;
    }

    static string Key(IEnumerable<string> keywords) => string.Join(" ", keywords.Where(k => !k.StartsWith("_")).OrderBy(k => k, StringComparer.Ordinal));

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (shader.name != "HDRP/Lit") return;
        // A build that keeps ray tracing passes (the game has none of them) leaves them to HDRP's own stripping.
        if (KeepRayTracingPasses && (snippet.passName.EndsWith("DXR") || snippet.passName == "RayTracingPrepass")) return;
        if (!Allowed().TryGetValue(snippet.passName, out var sets)) { Removed += data.Count; data.Clear(); return; }
        // The game's sets are its fragment programs'; other stages see fewer keywords and are left to HDRP.
        if (snippet.shaderType != ShaderType.Fragment) return;
        for (int i = data.Count - 1; i >= 0; i--)
        {
            var names = data[i].shaderKeywordSet.GetShaderKeywords().Select(k => k.name);
            if (sets.Contains(Key(names))) Kept++;
            else { data.RemoveAt(i); Removed++; }
        }
    }

    [Serializable] class GameVariants { public Pass[] passes = Array.Empty<Pass>(); }
    [Serializable] class Pass { public string name = ""; public Set[] sets = Array.Empty<Set>(); }
    [Serializable] class Set { public string[] keywords = Array.Empty<string>(); }
}
