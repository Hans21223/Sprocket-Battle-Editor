using Sprocket.Painting;
using UnityEngine;
using UnityEngine.Rendering;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    static void CaptureShader(Capture c, ReplaySurface surface, Material material, Renderer? renderer, int submesh)
    {
        surface.Shader = material.shader?.name ?? "";
        surface.Keywords = material.shaderKeywords.ToArray();
        var shared = new MaterialPropertyBlock(); var perSlot = new MaterialPropertyBlock();
        try
        {
            if (renderer != null) { renderer.GetPropertyBlock(shared); renderer.GetPropertyBlock(perSlot, submesh); }
            MaterialPropertyBlock? Block(string name) => perSlot.HasProperty(name) ? perSlot : shared.HasProperty(name) ? shared : null;
            var shader = material.shader;
            if (shader == null) return;
            // Reserve image space for the visible paint before auxiliary normal/mask maps.
            var properties = Enumerable.Range(0, Math.Min(512, shader.GetPropertyCount()))
                .Select(i => (Index: i, Name: shader.GetPropertyName(i)))
                .OrderBy(p => p.Name.Contains("paint", StringComparison.OrdinalIgnoreCase) ? 0
                    : p.Name.Contains("color", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("colour", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            foreach (var property in properties)
            {
                int i = property.Index; string name = property.Name;
                var block = Block(name); var p = new ReplayShaderProperty { Name = name };
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        var color = block?.GetColor(name) ?? material.GetColor(name); p.Kind = 2;
                        p.Value = new[] { color.r, color.g, color.b, color.a }; break;
                    case ShaderPropertyType.Vector:
                        var vector = block?.GetVector(name) ?? material.GetVector(name); p.Kind = 1;
                        p.Value = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range: case ShaderPropertyType.Int:
                        p.Kind = 0; p.Value = new[] { block?.GetFloat(name) ?? material.GetFloat(name) }; break;
                    case ShaderPropertyType.Texture:
                        if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                        var texture = block?.GetTexture(name) ?? material.GetTexture(name);
                        if (texture == null) continue;
                        p.Kind = 4; p.Texture = CapturePaintTexture(c, texture);
                        var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
                        p.Value = new[] { scale.x, scale.y, offset.x, offset.y };
                        p.Linear = name.Contains("Normal", StringComparison.OrdinalIgnoreCase) || name.Contains("Mask", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("ARM", StringComparison.OrdinalIgnoreCase) || name.Contains("DPM", StringComparison.OrdinalIgnoreCase);
                        break;
                    default: continue;
                }
                if (p.Value.All(float.IsFinite)) surface.Properties.Add(p);
            }
            // Paint coordinates are a matrix uniform, so Shader.GetPropertyCount does not enumerate it.
            foreach (var settings in Resources.FindObjectsOfTypeAll<PainterSettings>())
            {
                if (settings == null || settings.shader?.name != surface.Shader || string.IsNullOrEmpty(settings.paintCoordMtxShaderReference)) continue;
                string name = settings.paintCoordMtxShaderReference;
                var matrix = Block(name)?.GetMatrix(name) ?? material.GetMatrix(name);
                var values = new float[16];
                for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) values[row * 4 + col] = matrix[row, col];
                if (values.All(float.IsFinite) && values.Any(v => v != 0))
                    surface.Properties.Add(new ReplayShaderProperty { Name = name, Kind = 3, Value = values });
                break;
            }
        }
        catch (Exception ex) { c.Data.Simplified = true; Trace.Write($"replay paint '{surface.Shader}': {ex.Message}"); }
        finally { shared.Dispose(); perSlot.Dispose(); }
    }
    static int CapturePaintTexture(Capture c, Texture texture)
    {
        if (c.Textures.TryGetValue(texture.Pointer, out int image)) return image;
        image = -1;
        try
        {
            if (c.TextureBytes < ReplayData.MaxTextureBytes && c.Data.Textures.Count < 256)
            {
                var png = ReplayTexture(texture); long bytes = (png.Length + 2L) / 3 * 4;
                if (ReplayGeometry.Png(png) && c.TextureBytes + png.Length <= ReplayData.MaxTextureBytes && c.VisualBytes + bytes <= 64 * 1024 * 1024)
                { image = c.Data.Textures.Count; c.Data.Textures.Add(png); c.TextureBytes += png.Length; c.VisualBytes += bytes; }
            }
        }
        catch (Exception ex) { Trace.Write("replay paint texture: " + ex.Message); }
        c.Textures[texture.Pointer] = image;
        if (image < 0) c.Data.Simplified = true;
        return image;
    }
    readonly Dictionary<int, Texture2D> replayLinearTextures = new();
    Texture2D ReplayPaintTexture(int index, bool linear)
    {
        if (!linear) return replayTextures[index];
        if (!replayLinearTextures.TryGetValue(index, out var texture))
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true);
            if (!ImageConversion.LoadImage(texture, visualReplay!.Textures[index], true)) { Destroy(texture); throw new InvalidDataException("A replay material texture could not load."); }
            replayLinearTextures[index] = texture;
        }
        return texture;
    }
    void ApplyReplayShader(Material material, ReplaySurface surface)
    {
        material.shaderKeywords = surface.Keywords;
        foreach (var p in surface.Properties)
        {
            // Matrix uniforms are not exposed in the shader's Properties block.
            if (p.Kind != 3 && !material.HasProperty(p.Name)) continue;
            var v = p.Value;
            switch (p.Kind)
            {
                case 0: material.SetFloat(p.Name, v[0]); break;
                case 1: material.SetVector(p.Name, new Vector4(v[0], v[1], v[2], v[3])); break;
                case 2: material.SetColor(p.Name, new Color(v[0], v[1], v[2], v[3])); break;
                case 3:
                    var matrix = new Matrix4x4();
                    for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) matrix[row, col] = v[row * 4 + col];
                    material.SetMatrix(p.Name, matrix); break;
                case 4:
                    if (p.Texture >= 0) material.SetTexture(p.Name, ReplayPaintTexture(p.Texture, p.Linear));
                    material.SetTextureScale(p.Name, new Vector2(v[0], v[1])); material.SetTextureOffset(p.Name, new Vector2(v[2], v[3])); break;
            }
        }
    }
}
