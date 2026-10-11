using UnityEngine;

namespace SprocketMaps;

/// HDRP/Lit compiled by the game's own Unity and HDRP version with every material feature imported maps need:
/// alpha-test cutouts, both sides, transparency, normal and mask maps (tools/ShaderBundle). The game's copy holds
/// only the variants its own materials use. Loaded on first use; without it, materials fall back to the game's shaders.
internal static class MapShaders
{
    const string BundleFile = "sprocketmaps.shaders"; // not .bundle: the map scan would take it for a map
    static bool tried;
    static AssetBundle? bundle;
    internal static Shader? Lit { get; private set; }
    internal static Texture2D? FlatNormal { get; private set; }
    internal static Texture2D? NeutralMask { get; private set; }

    internal static void Load()
    {
        if (tried) return;
        tried = true;
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", BundleFile);
        try
        {
            if (!File.Exists(path)) { Plugin.ModLog.LogWarning($"{BundleFile} is missing; imported materials use the game's shaders."); return; }
            bundle = CustomMapBundle.Open(path); // AssetBundle.LoadFromFile's path does not pass through the interop
            foreach (var asset in CustomMapBundle.All<UnityEngine.Object>(bundle))
            {
                if (asset.TryCast<Material>() is { } material && material.shader != null && material.shader.name == "HDRP/Lit") Lit ??= material.shader;
                else if (asset.TryCast<Texture2D>() is { } texture)
                {
                    if (texture.name == "FlatNormal") FlatNormal = texture;
                    else if (texture.name == "NeutralMask") NeutralMask = texture;
                }
            }
            if (Lit == null || !Lit.isSupported || FlatNormal == null || NeutralMask == null)
                throw new InvalidOperationException("it has no usable HDRP/Lit");
            // Nothing in the game references these between maps, so the scene change's unused-asset sweep would
            // destroy them and the next map would get a dead shader (2026-10-11: Poland after Demo City).
            foreach (UnityEngine.Object kept in new UnityEngine.Object[] { Lit, FlatNormal, NeutralMask })
                kept.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            Plugin.ModLog.LogInfo($"Map shaders loaded from {BundleFile}: HDRP/Lit with cutout, two-sided and transparent variants.");
        }
        catch (Exception ex)
        {
            Lit = null;
            Plugin.ModLog.LogWarning($"{BundleFile} could not be used ({ex.Message}); imported materials use the game's shaders.");
        }
    }
}
