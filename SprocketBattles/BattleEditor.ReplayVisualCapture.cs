using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AssetManagement;
using Sprocket.Vehicles.Tracks.Belts;
using UnityEngine;
using UnityEngine.Rendering;
using System.Text.Json;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    static IEnumerable<T> ReplayEach<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
    {
        int count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;
        for (int i = 0; i < count; i++) yield return list[i];
    }

    void CaptureVisuals(Capture c, CapturedActor actor)
    {
        var tank = actor.Tank;
        var gateway = VehicleVisualGateway(tank);
        var root = gateway?.gameObject ?? tank.gameObject;
        var seen = new HashSet<IntPtr>();
        var ignored = new HashSet<IntPtr>();
        string design = Files.Absolute(c.Recording.Battle.Units.First(u => u.Id == actor.Data.Unit).Blueprint);
        int used = 0, allowance = ReplayGeometry.ActorAllowance(c.Recording.Battle.Units
            .Select(u => Files.Absolute(u.Blueprint)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        int? Geometry(Mesh mesh, bool temporary = false)
        {
            if (mesh == null || mesh.vertexCount < 3 || mesh.vertexCount > allowance || mesh.subMeshCount > 64) return null;
            if (c.Meshes.TryGetValue(mesh.Pointer, out int index) && !temporary) return index;
            var geometry = ReadReplayMesh(mesh);
            if (geometry.Triangles.Length == 0) return null;
            string identity = ReplayGeometry.Identity(geometry);
            if (!c.Geometry.TryGetValue(identity, out index))
            {
                int count = geometry.Vertices.Length / 3;
                c.DesignVertices.TryGetValue(design, out int designVertices);
                if (designVertices + count > allowance || c.Vertices + count > ReplayData.MaxVertices) return null;
                int bytes = JsonSerializer.SerializeToUtf8Bytes(geometry).Length;
                if (c.VisualBytes + bytes > 64 * 1024 * 1024) return null;
                index = c.Data.Meshes.Count;
                c.Data.Meshes.Add(geometry); c.Geometry.Add(identity, index);
                used += count; c.Vertices += count; c.DesignVertices[design] = designVertices + count; c.VisualBytes += bytes;
            }
            if (!temporary) c.Meshes[mesh.Pointer] = index;
            return index;
        }
        bool Add(Renderer renderer, VehicleRendererGroup? group, Il2CppReferenceArray<VehicleMaterial>? originals)
        {
            if (seen.Contains(renderer.Pointer) || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return false;
            if (c.Parts >= ReplayData.MaxParts || c.Keys + 1 >= ReplayData.MaxKeys) { c.Data.Simplified = true; return false; }
            Mesh? baked = null;
            try
            {
                Mesh? mesh = null;
                if (renderer.TryCast<MeshRenderer>() != null) mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                else if (renderer.TryCast<SkinnedMeshRenderer>() is { } skin)
                { baked = new Mesh(); skin.BakeMesh(baked, false); mesh = baked; }
                if (mesh == null || Geometry(mesh, baked != null) is not { } index) return false;
                var part = new ReplayPart { Mesh = index };
                var native = renderer.sharedMaterials;
                for (int sub = 0; sub < c.Data.Meshes[index].Submeshes.Count; sub++)
                {
                    // The renderer's mapped material contains the final paint shader/tint. Its asset material is only a fallback.
                    var material = sub < native.Length ? native[sub] : null;
                    material ??= originals != null && sub < originals.Length ? originals[sub]?.Material : null;
                    part.Surfaces.Add(CaptureSurface(c, material, renderer, sub));
                }
                part.Colour = c.Data.Surfaces[part.Surfaces[0]].Colour;
                part.Keys.Add(ReplayKey(renderer.transform, tank.transform, 0, true)); c.Keys++;
                actor.Data.Parts.Add(part); actor.Parts.Add((renderer.transform, renderer, group, part));
                c.Parts++; seen.Add(renderer.Pointer);
                return true;
            }
            catch (Exception ex)
            { Trace.Write($"replay visual '{renderer.name}': {ex.Message}"); return false; }
            finally { if (baked != null) Destroy(baked); }
        }

        // The register owns the actual plate models, their native materials and LODs.
        // Collider geometry and shadow proxies are never substitutes for those visible models.
        if (gateway?.RendererRegister?.Groups is { } groups)
            foreach (var group in ReplayEach(groups))
            {
                if (group == null) continue;
                if (group.shadow?.Renderer is { } shadow) ignored.Add(shadow.Pointer);
                if (group.lods is not { } lods) continue;
                foreach (var lod in lods) if (lod?.Renderer is { } renderer) ignored.Add(renderer.Pointer);
                if (!group.visible) continue;
                bool added = false;
                // Prefer the detailed model, falling back to a lower LOD within this actor's allowance.
                foreach (var lod in lods)
                    if (lod?.Renderer is { } renderer && Add(renderer, group, group.Materials)) { added = true; break; }
                if (!added) c.Data.Simplified = true;
            }
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            if (!ignored.Contains(renderer.Pointer) && !seen.Contains(renderer.Pointer)
                && renderer.enabled && renderer.gameObject.activeInHierarchy
                && renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly
                && (renderer.TryCast<MeshRenderer>() != null || renderer.TryCast<SkinnedMeshRenderer>() != null)
                && !Add(renderer, null, null)) c.Data.Simplified = true;
        // Native track segment models can live outside the vehicle's transform hierarchy.
        foreach (var part in root.GetComponentsInChildren<VehicleObject>(true))
            foreach (var component in ReplayEach(part.Components))
                if (component?.TryCast<TrackBelt>() is { } belt)
                {
                    if (CaptureInstancedBelt(c, actor, belt, mesh => Geometry(mesh))) continue;
                    if (belt.segmentModels is not { } segments) continue;
                    foreach (var segment in segments)
                        if (segment?.renderer is { } renderer && !ignored.Contains(renderer.Pointer)
                            && !seen.Contains(renderer.Pointer) && renderer.enabled
                            && !Add(renderer, null, null)) c.Data.Simplified = true;
                }
        Trace.Write($"replay visual {actor.Data.Unit}: {actor.Data.Parts.Count} parts, {used} new vertices, {allowance} allowance");
    }

    static IVehicleGateway? VehicleVisualGateway(VehicleBehaviour tank)
    {
        if (tank.VehicleInfo?.TryCast<IVehicleGateway>() is { } gateway) return gateway;
        if (Battle.Mode?.vehicleSpawns is { } spawns)
            for (int i = 0; i < spawns.Count; i++)
                if (spawns[i]?.Vehicle?.TryCast<IVehicleGateway>() is { } candidate
                    && candidate.Behaviour?.TryCast<VehicleBehaviour>() is { } behaviour && behaviour.Pointer == tank.Pointer)
                    return candidate;
        return null;
    }

    static IEnumerable<Renderer> VehicleVisualRenderers(GameObject root)
    {
        var seen = new HashSet<IntPtr>();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            if (renderer != null && seen.Add(renderer.Pointer)) yield return renderer;
        var tank = root.GetComponentInChildren<VehicleBehaviour>(true);
        if (tank != null && VehicleVisualGateway(tank)?.RendererRegister?.Groups is { } groups)
            foreach (var group in ReplayEach(groups))
            {
                if (group == null) continue;
                if (group.shadow?.Renderer is { } shadow && seen.Add(shadow.Pointer)) yield return shadow;
                if (group.lods is { } lods)
                    foreach (var lod in lods)
                        if (lod?.Renderer is { } renderer && seen.Add(renderer.Pointer)) yield return renderer;
            }
        foreach (var part in root.GetComponentsInChildren<VehicleObject>(true))
            foreach (var component in ReplayEach(part.Components))
                if (component?.TryCast<TrackBelt>() is { } belt && belt.segmentModels is { } segments)
                    foreach (var segment in segments)
                        if (segment?.renderer is { } renderer && seen.Add(renderer.Pointer)) yield return renderer;
    }

    static ReplayMesh ReadReplayMesh(Mesh mesh)
    {
        Vector3[] vertices, normals;
        Vector2[] uv;
        if (mesh.isReadable)
        { vertices = mesh.vertices.ToArray(); normals = mesh.normals.ToArray(); uv = mesh.uv.ToArray(); }
        else
        {
            var streams = new Dictionary<int, byte[]>();
            Vector3[] Attribute(VertexAttribute attribute, bool required)
            {
                if (!mesh.HasVertexAttribute(attribute))
                {
                    if (required) throw new InvalidDataException("The mesh has no positions.");
                    return Array.Empty<Vector3>();
                }
                int stream = mesh.GetVertexAttributeStream(attribute), format = (int)mesh.GetVertexAttributeFormat(attribute);
                int stride = mesh.GetVertexBufferStride(stream), offset = mesh.GetVertexAttributeOffset(attribute), dimension = mesh.GetVertexAttributeDimension(attribute);
                if (!streams.TryGetValue(stream, out var bytes))
                {
                    var buffer = mesh.GetVertexBuffer(stream);
                    try { streams[stream] = bytes = ReplayBuffer(buffer); }
                    finally { buffer?.Dispose(); }
                }
                int width = ReplayGeometry.AttributeBytes(format);
                if (stride <= 0 || dimension < 1 || dimension > 4 || offset < 0 || offset + dimension * width > stride
                    || (long)mesh.vertexCount * stride > bytes.Length) throw new InvalidDataException("Invalid GPU vertex layout.");
                var values = new Vector3[mesh.vertexCount];
                for (int i = 0; i < values.Length; i++)
                {
                    int start = checked(i * stride + offset);
                    values[i] = new Vector3(ReplayGeometry.Attribute(bytes, start, format),
                        dimension > 1 ? ReplayGeometry.Attribute(bytes, start + width, format) : 0,
                        dimension > 2 ? ReplayGeometry.Attribute(bytes, start + 2 * width, format) : 0);
                }
                return values;
            }
            vertices = Attribute(VertexAttribute.Position, true); normals = Attribute(VertexAttribute.Normal, false);
            uv = Attribute(VertexAttribute.TexCoord0, false).Select(v => new Vector2(v.x, v.y)).ToArray();
        }
        if (vertices.Any(v => !SpawnGround.Finite(v)) || normals.Any(v => !SpawnGround.Finite(v))
            || uv.Any(v => !float.IsFinite(v.x) || !float.IsFinite(v.y))) throw new InvalidDataException("Invalid mesh attributes.");
        var result = new ReplayMesh { Vertices = vertices.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(),
            Normals = normals.Length == vertices.Length ? normals.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray() : Array.Empty<float>(),
            Uv = uv.Length == vertices.Length ? uv.SelectMany(v => new[] { v.x, v.y }).ToArray() : Array.Empty<float>() };
        byte[]? indicesBytes = null;
        if (!mesh.isReadable)
        {
            var buffer = mesh.GetIndexBuffer();
            try { indicesBytes = ReplayBuffer(buffer); }
            finally { buffer?.Dispose(); }
        }
        long total = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            var topology = mesh.GetTopology(sub);
            if (topology is not (MeshTopology.Triangles or MeshTopology.Quads))
            { result.Submeshes.Add(Array.Empty<int>()); continue; }
            int[] indices;
            if (mesh.isReadable) indices = mesh.GetIndices(sub, true).ToArray();
            else
            {
                var desc = mesh.GetSubMesh(sub);
                int width = mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2;
                if (desc.indexStart < 0 || desc.indexCount < 0 || ((long)desc.indexStart + desc.indexCount) * width > indicesBytes!.Length)
                    throw new InvalidDataException("Invalid GPU index range.");
                indices = new int[desc.indexCount];
                for (int i = 0; i < indices.Length; i++)
                {
                    int start = checked((desc.indexStart + i) * width);
                    indices[i] = checked((width == 4 ? (int)BitConverter.ToUInt32(indicesBytes, start) : BitConverter.ToUInt16(indicesBytes, start)) + desc.baseVertex);
                }
            }
            if (indices.Any(i => i < 0 || i >= vertices.Length)) throw new InvalidDataException("Out-of-range replay triangle.");
            int step = topology == MeshTopology.Quads ? 4 : 3;
            if (indices.Length % step != 0) throw new InvalidDataException("Incomplete mesh polygons.");
            if (step == 4)
            {
                var triangles = new int[checked(indices.Length / 4 * 6)];
                for (int i = 0, j = 0; i < indices.Length; i += 4, j += 6)
                { triangles[j] = indices[i]; triangles[j+1] = indices[i+1]; triangles[j+2] = indices[i+2];
                    triangles[j+3] = indices[i]; triangles[j+4] = indices[i+2]; triangles[j+5] = indices[i+3]; }
                indices = triangles;
            }
            total += indices.Length;
            if (total > vertices.Length * 12L) throw new InvalidDataException("Oversized replay triangle storage.");
            result.Submeshes.Add(indices);
        }
        result.Triangles = result.Submeshes.SelectMany(s => s).ToArray();
        return result;
    }

    static byte[] ReplayBuffer(GraphicsBuffer? buffer)
    {
        if (buffer == null || !buffer.IsValid()) throw new InvalidDataException("The mesh graphics buffer is unavailable.");
        long length = (long)buffer.count * buffer.stride;
        if (length <= 0 || length > 128 * 1024 * 1024) throw new InvalidDataException("Oversized mesh graphics buffer.");
        var bytes = new Il2CppStructArray<byte>(length);
        buffer.InternalGetData(bytes.Cast<Il2CppSystem.Array>(), 0, 0, checked((int)length), 1);
        return bytes.ToArray();
    }

    static int CaptureSurface(Capture c, Material? material, Renderer? renderer = null, int submesh = 0)
    {
        var pointer = material?.Pointer ?? IntPtr.Zero;
        if (renderer == null && c.Materials.TryGetValue(pointer, out int cached)) return cached;
        var surface = new ReplaySurface();
        if (material != null)
        {
            CaptureShader(c, surface, material, renderer, submesh);
            surface.Colour = new[] { 1f, 1f, 1f, 1f };
            foreach (string name in new[] { "_BaseColor", "_Color", "_BaseColour" })
                if (material.HasColor(name))
                { var colour = material.GetColor(name); surface.Colour = new[] { colour.r, colour.g, colour.b, colour.a }; break; }
            foreach (string name in new[] { VehicleMaterial.BaseColourTextureReference, "_BaseColorMap", "_MainTex", "_BaseColourTexture", "_BaseColorTexture" })
                if (material.HasTexture(name) && material.GetTexture(name) is { } texture)
                {
                    var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
                    surface.Tiling = new[] { scale.x, scale.y }; surface.Offset = new[] { offset.x, offset.y };
                    if (!c.Textures.TryGetValue(texture.Pointer, out int image))
                    {
                        image = -1;
                        try
                        {
                            if (c.TextureBytes < ReplayData.MaxTextureBytes && c.Data.Textures.Count < 256)
                            {
                                var png = ReplayTexture(texture);
                                long bytes = (png.Length + 2L) / 3 * 4;
                                if (ReplayGeometry.Png(png) && c.TextureBytes + png.Length <= ReplayData.MaxTextureBytes
                                    && c.VisualBytes + bytes <= 64 * 1024 * 1024)
                                { image = c.Data.Textures.Count; c.Data.Textures.Add(png); c.TextureBytes += png.Length; c.VisualBytes += bytes; }
                            }
                        }
                        catch (Exception ex) { Trace.Write("replay texture: " + ex.Message); }
                        c.Textures[texture.Pointer] = image;
                    }
                    surface.Texture = image;
                    if (image < 0) c.Data.Simplified = true;
                    break;
                }
            if (material.HasFloat("_Metallic")) surface.Metallic = Math.Clamp(material.GetFloat("_Metallic"), 0, 1);
            if (material.HasFloat("_Smoothness")) surface.Smoothness = Math.Clamp(material.GetFloat("_Smoothness"), 0, 1);
        }
        string identity = JsonSerializer.Serialize(surface);
        if (c.SurfaceIdentities.TryGetValue(identity, out int index)) return index;
        index = c.Data.Surfaces.Count; c.Data.Surfaces.Add(surface); c.SurfaceIdentities[identity] = index;
        if (renderer == null) c.Materials[pointer] = index;
        return index;
    }

    static byte[] ReplayTexture(Texture texture)
    {
        if (texture.width <= 0 || texture.height <= 0) throw new InvalidDataException("Empty material texture.");
        float factor = Math.Min(1, 2048f / Math.Max(texture.width, texture.height));
        int width = Math.Max(1, (int)(texture.width * factor)), height = Math.Max(1, (int)(texture.height * factor));
        RenderTexture? target = null; Texture2D? copy = null;
        var previous = RenderTexture.active;
        try
        {
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, target); RenderTexture.active = target;
            copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0); copy.Apply();
            return ImageConversion.EncodeToPNG(copy).ToArray();
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (copy != null) Destroy(copy);
        }
    }
}
