using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SprocketBattles;

/// Visual poses only: replay never owns a vehicle, rigidbody, AI controller or mission runner.
public sealed class ReplayData
{
    public const int MaxSeconds = 1200, MaxKeys = 150000, MaxVertices = 1000000, MaxParts = 2048, MaxTextureBytes = 16 * 1024 * 1024;
    public int VisualVersion { get; set; }
    public bool Simplified { get; set; }
    public float Duration { get; set; }
    public List<ReplayMesh> Meshes { get; set; } = new();
    public List<ReplayActor> Actors { get; set; } = new();
    public List<ReplaySurface> Surfaces { get; set; } = new();
    public List<byte[]> Textures { get; set; } = new();
    public List<ReplaySoundClip> SoundClips { get; set; } = new();
    public List<ReplaySound> Sounds { get; set; } = new();
    public float CaptureSpeed { get; set; } = 1;
    public bool AudioIncomplete { get; set; }

    public string? Check(IEnumerable<string> unitIds)
    {
        const string invalid = "The replay has invalid or oversized visual data.";
        if (VisualVersion < 0 || VisualVersion > 3 || !float.IsFinite(Duration) || Duration < 0 || Duration > MaxSeconds || Meshes == null || Actors == null
            || Actors.Count == 0 || Actors.Count > 32 || Meshes.Count > MaxParts || Surfaces == null || Textures == null
            || Surfaces.Count > MaxParts * 8 || Textures.Count > 256 || !float.IsFinite(CaptureSpeed) || CaptureSpeed <= 0 || CaptureSpeed > 100) return invalid;
        if (ReplayMedia.CheckAudio(this) is { } audioError) return audioError;
        long textureBytes = 0;
        foreach (var png in Textures)
        {
            if (!ReplayGeometry.Png(png)) return invalid;
            textureBytes += png.Length;
            if (textureBytes > MaxTextureBytes) return invalid;
        }
        foreach (var surface in Surfaces)
            if (surface == null || !Array(surface.Colour, 4) || !Array(surface.Tiling, 2) || !Array(surface.Offset, 2)
                || surface.Texture < -1 || surface.Texture >= Textures.Count || !float.IsFinite(surface.Metallic)
                || surface.Metallic < 0 || surface.Metallic > 1 || !float.IsFinite(surface.Smoothness)
                || surface.Smoothness < 0 || surface.Smoothness > 1 || !ReplayMedia.CheckSurface(surface, Textures.Count)) return invalid;
        var ids = unitIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long vertices = 0, keys = 0, parts = 0;
        long beltBytes = 0, beltDecoded = 0, beltSegments = 0;
        foreach (var mesh in Meshes)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Triangles == null || mesh.Vertices.Length < 9
                || mesh.Vertices.Length % 3 != 0 || mesh.Triangles.Length < 3 || mesh.Triangles.Length % 3 != 0
                || mesh.Triangles.Length > (long)mesh.Vertices.Length * 4 || mesh.Vertices.Any(v => !Finite(v))
                || mesh.Triangles.Any(i => i < 0 || i >= mesh.Vertices.Length / 3)
                || mesh.Normals == null || mesh.Uv == null || mesh.Submeshes == null
                || mesh.Normals.Length != 0 && (mesh.Normals.Length != mesh.Vertices.Length || mesh.Normals.Any(v => !Finite(v)))
                || mesh.Uv.Length != 0 && (mesh.Uv.Length != mesh.Vertices.Length / 3 * 2 || mesh.Uv.Any(v => !Finite(v)))
                || mesh.Submeshes.Count > 64 || mesh.Submeshes.Any(s => s == null || s.Length % 3 != 0 || s.Any(i => i < 0 || i >= mesh.Vertices.Length / 3))
                || mesh.Submeshes.Count > 0 && mesh.Submeshes.Sum(s => (long)s.Length) != mesh.Triangles.Length) return invalid;
            vertices += mesh.Vertices.Length / 3;
            if (vertices > MaxVertices) return invalid;
        }
        bool Track(List<ReplayPoseKey>? track)
        {
            if (track == null || track.Count == 0 || track[0] == null || track[0].Time != 0) return false;
            float before = -1;
            foreach (var k in track)
            {
                if (k == null || !float.IsFinite(k.Time) || k.Time <= before || k.Time > Duration
                    || !Array(k.Position, 3) || !Array(k.Rotation, 4) || !Array(k.Scale, 3)
                    || k.Rotation.Sum(v => (double)v * v) < 0.000001) return false;
                before = k.Time;
            }
            keys += track.Count;
            return keys <= MaxKeys;
        }
        foreach (var actor in Actors)
        {
            if (actor == null || string.IsNullOrWhiteSpace(actor.Unit) || !ids.Contains(actor.Unit) || !seen.Add(actor.Unit)
                || actor.Parts == null || actor.Belts == null || !Track(actor.Keys)) return invalid;
            if (!ReplayMedia.CheckBelts(actor.Belts, this, ref beltBytes, ref beltDecoded, ref beltSegments)) return invalid;
            parts += actor.Parts.Count;
            if (parts > MaxParts) return invalid;
            foreach (var part in actor.Parts)
                if (part == null || part.Mesh < 0 || part.Mesh >= Meshes.Count || !Array(part.Colour, 4) || part.Surfaces == null
                    || part.Surfaces.Count > 64 || part.Surfaces.Any(s => s < 0 || s >= Surfaces.Count)
                    || part.Surfaces.Count > 0 && part.Surfaces.Count != Math.Max(1, Meshes[part.Mesh].Submeshes.Count)
                    || !Track(part.Keys)) return invalid;
        }
        return null;
    }
    static bool Finite(float n) => float.IsFinite(n) && Math.Abs(n) < 10000000;
    static bool Array(float[]? a, int count) => a?.Length == count && a.All(Finite);
}

public sealed class ReplayMesh
{
    public float[] Vertices { get; set; } = System.Array.Empty<float>();
    public int[] Triangles { get; set; } = System.Array.Empty<int>();
    public float[] Normals { get; set; } = System.Array.Empty<float>();
    public float[] Uv { get; set; } = System.Array.Empty<float>();
    public List<int[]> Submeshes { get; set; } = new();
}
public sealed class ReplaySurface
{
    public string Shader { get; set; } = "";
    public string[] Keywords { get; set; } = System.Array.Empty<string>();
    public List<ReplayShaderProperty> Properties { get; set; } = new();
    public float[] Colour { get; set; } = { 0.35f, 0.4f, 0.3f, 1 };
    public int Texture { get; set; } = -1;
    public float[] Tiling { get; set; } = { 1, 1 };
    public float[] Offset { get; set; } = { 0, 0 };
    public float Metallic { get; set; }
    public float Smoothness { get; set; } = .35f;
}
public sealed class ReplayActor
{
    public string Unit { get; set; } = "";
    public List<ReplayPoseKey> Keys { get; set; } = new();
    public List<ReplayPart> Parts { get; set; } = new();
    public List<ReplayBelt> Belts { get; set; } = new();
}
public sealed class ReplayPart
{
    public int Mesh { get; set; }
    public List<int> Surfaces { get; set; } = new();
    public float[] Colour { get; set; } = { 0.4f, 0.4f, 0.4f, 1 };
    public List<ReplayPoseKey> Keys { get; set; } = new();
}
public sealed class ReplayPoseKey
{
    public float Time { get; set; }
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = { 0, 0, 0, 1 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
    public bool Visible { get; set; } = true;
}

internal static class ReplayPoses
{
    internal readonly record struct Pose(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool Visible);
    internal static Pose At(IReadOnlyList<ReplayPoseKey> keys, float time)
    {
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi) { int m = (lo + hi + 1) / 2; if (keys[m].Time <= time) lo = m; else hi = m - 1; }
        var a = keys[lo]; var b = keys[Math.Min(lo + 1, keys.Count - 1)];
        float blend = b.Time > a.Time ? Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1) : 0;
        Vector3 V(float[] p) => new(p[0], p[1], p[2]);
        Quaternion Q(float[] p) => Quaternion.Normalize(new Quaternion(p[0], p[1], p[2], p[3]));
        // Visibility changes are discrete. No interpolation through a missing or destroyed part.
        if (a.Visible != b.Visible) blend = 0;
        return new(Vector3.Lerp(V(a.Position), V(b.Position), blend), Quaternion.Slerp(Q(a.Rotation), Q(b.Rotation), blend),
            Vector3.Lerp(V(a.Scale), V(b.Scale), blend), a.Visible);
    }
}

public sealed class ReplayRecording
{
    public int Version { get; set; } = 1;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public BattleFile Battle { get; set; } = new();
    [JsonIgnore] public string? Error => Version != 1 || Battle == null || Battle.Cinema?.Replay == null
        ? "This file isn't a supported replay." : Battle.CheckData();
    public string ToJson()
    {
        if (Error is { } error) throw new InvalidDataException(error);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 128 * 1024 * 1024) throw new InvalidDataException("The replay file is too large.");
        return json;
    }
    public static ReplayRecording Read(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new InvalidDataException("The replay file is too large.");
        var replay = JsonSerializer.Deserialize<ReplayRecording>(File.ReadAllText(path));
        if (replay == null || replay.Error is { }) throw new InvalidDataException(replay?.Error ?? "The replay is empty.");
        return replay;
    }
}

/// Small optional list metadata. The recording itself is always validated again when opened.
internal sealed class ReplaySummary
{
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public float Duration { get; set; }
    internal static void Write(string path, ReplayRecording replay) => SavedFiles.Write(path + ".info.json",
        JsonSerializer.Serialize(new ReplaySummary { Name = replay.Battle.Name, Map = replay.Battle.Map,
            Duration = replay.Battle.Cinema!.Replay!.Duration }), overwrite: false);
    internal static ReplaySummary Read(string path)
    {
        try
        {
            var info = new FileInfo(path + ".info.json");
            if (info.Exists && info.Length < 16384 && JsonSerializer.Deserialize<ReplaySummary>(File.ReadAllText(info.FullName)) is { } summary
                && summary.Name != null && summary.Map != null && float.IsFinite(summary.Duration)
                && summary.Duration >= 0 && summary.Duration <= ReplayData.MaxSeconds) return summary;
        }
        catch { /* Losing optional list metadata must not hide a valid recording. */ }
        return new ReplaySummary { Name = Path.GetFileNameWithoutExtension(path), Map = "unknown map" };
    }
}
