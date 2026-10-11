using UnityEngine;

namespace SprocketMaps;

// Locally authored replacements retain the native shader's saved property layout and keywords.
// Only these installed-game foliage shaders are eligible; generic imports retain normal conversion.
internal static class NativeSceneryMaterial
{
    static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    internal static bool TryClone(Material source, out Material material)
    {
        material = null!;
        if (source.GetTag("SprocketMapNativePreset", false, "") != "1") return false;
        var name = source.GetTag("SprocketMapNativeShader", false, "");
        if (name is not ("NatureManufacture/HDRP/Foliage/Foliage"
            or "NatureManufacture/HDRP/Foliage/Bark" or "NatureManufacture/HDRP/Foliage/Cross")) return false;
        var shader = Shader.Find(name);
        if (shader == null)
        {
            if (Missing.Add(name)) Plugin.ModLog.LogInfo($"Native scenery shader '{name}' is not loaded; using the imported material fallback.");
            return false;
        }
        var keywords = source.GetTag("SprocketMapNativeKeywords", false, "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (keywords.Length > 32 || keywords.Any(keyword => keyword.Length > 128
            || keyword.Any(character => character != '_' && !(character >= 'a' && character <= 'z')
                && !(character >= 'A' && character <= 'Z') && !(character >= '0' && character <= '9')))) return false;
        var clone = new Material(source)
        {
            name = source.name + " (native scenery)",
            shader = shader,
            enableInstancing = source.enableInstancing,
        };
        try
        {
            // Rebinding makes the shader's saved colour/scalar values readable again. A generic
            // HDRP validator would replace the native graph's transmission and foliage keywords.
            clone.shaderKeywords = keywords;
            if (int.TryParse(source.GetTag("SprocketMapNativeRenderQueue", false, ""), out int queue)
                && queue >= -1 && queue <= 5000) clone.renderQueue = queue;
            material = clone;
            Plugin.ModLog.LogInfo($"Native scenery material restored: '{source.name}', shader='{name}'.");
            return true;
        }
        catch
        {
            UnityEngine.Object.Destroy(clone);
            throw;
        }
    }
}
