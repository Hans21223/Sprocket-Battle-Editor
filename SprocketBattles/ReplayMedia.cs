using System.IO.Compression;

namespace SprocketBattles;

public sealed class ReplayShaderProperty
{
    public string Name { get; set; } = "";
    // Float, vector, colour, matrix, texture. Matrices include non-Properties paint uniforms.
    public int Kind { get; set; }
    public float[] Value { get; set; } = Array.Empty<float>();
    public int Texture { get; set; } = -1;
    public bool Linear { get; set; }
}
public sealed class ReplayBelt
{
    public int Mesh { get; set; }
    public int Surface { get; set; }
    public int Count { get; set; }
    public List<ReplayBeltFrame> Frames { get; set; } = new();
}
public sealed class ReplayBeltFrame
{
    public float Time { get; set; }
    public bool Visible { get; set; } = true;
    // Deflated little-endian position(3), quaternion(4), scale(3) for each segment, in hull space.
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
public sealed class ReplaySoundClip
{
    public string Name { get; set; } = "";
    public int Samples { get; set; }
    public int Channels { get; set; }
    public int Frequency { get; set; }
    // Compressed PCM16 if readable. Otherwise resolve this exact loaded game clip by name and format.
    public byte[] Pcm { get; set; } = Array.Empty<byte>();
}
public sealed class ReplaySound
{
    public int Clip { get; set; }
    public bool Loop { get; set; }
    public float SpatialBlend { get; set; }
    public float MinDistance { get; set; } = 1;
    public float MaxDistance { get; set; } = 500;
    public int Rolloff { get; set; }
    public List<ReplaySoundKey> Keys { get; set; } = new();
}
public sealed class ReplaySoundKey
{
    public float Time { get; set; }
    public float[] Position { get; set; } = new float[3];
    public float Volume { get; set; }
    public float Pitch { get; set; } = 1;
    public int Sample { get; set; }
    public bool Playing { get; set; }
}

internal static class ReplayMedia
{
    internal const int MaxBeltBytes = 24 * 1024 * 1024, MaxAudioBytes = 12 * 1024 * 1024;
    internal static float[] BeltPoses(byte[] buffer, int stride, int count, System.Numerics.Matrix4x4 toHull)
    {
        if (stride < 64 || count < 1 || count > 2048 || (long)(count - 1) * stride + 64 > buffer.Length)
            throw new InvalidDataException("Invalid track instance buffer.");
        var result = new float[count * 10];
        for (int i = 0; i < count; i++)
        {
            int at = i * stride; float F(int n) => BitConverter.ToSingle(buffer, at + n * 4);
            // Unity's column-major bytes become Numerics' transposed row-vector matrix.
            // Consequently local = world * inverse(hull), preserving mirrored segment scale.
            var world = new System.Numerics.Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7),
                F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
            if (!System.Numerics.Matrix4x4.Decompose(world * toHull, out var scale, out var rotation, out var position))
                throw new InvalidDataException("Invalid track segment matrix.");
            int o = i * 10;
            result[o] = position.X; result[o+1] = position.Y; result[o+2] = position.Z;
            result[o+3] = rotation.X; result[o+4] = rotation.Y; result[o+5] = rotation.Z; result[o+6] = rotation.W;
            result[o+7] = scale.X; result[o+8] = scale.Y; result[o+9] = scale.Z;
        }
        if (result.Any(x => !float.IsFinite(x) || Math.Abs(x) >= 10000000)) throw new InvalidDataException("Invalid track segment pose.");
        return result;
    }
    internal static byte[] Pack(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(bytes);
        return output.ToArray();
    }
    internal static byte[] Unpack(byte[] bytes, int length)
    {
        if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized replay media.");
        using var input = new MemoryStream(bytes);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        var result = new byte[length]; int at = 0, read;
        while (at < length && (read = zip.Read(result, at, length - at)) > 0) at += read;
        if (at != length || zip.ReadByte() != -1) throw new InvalidDataException("Invalid replay media length.");
        return result;
    }
    static bool Values(float[]? a, int n) => a?.Length == n && a.All(x => float.IsFinite(x) && Math.Abs(x) < 10000000);
    internal static bool CheckSurface(ReplaySurface s, int textures) => s.Shader != null && s.Shader.Length <= 256
        && s.Keywords != null && s.Keywords.Length <= 128 && s.Keywords.All(k => k != null && k.Length <= 128)
        && s.Properties != null && s.Properties.Count <= 512 && s.Properties.All(p => p != null && !string.IsNullOrEmpty(p.Name)
            && p.Name.Length <= 256 && p.Kind >= 0 && p.Kind <= 4 && Values(p.Value, p.Kind == 0 ? 1 : p.Kind == 3 ? 16 : 4)
            && (p.Kind != 4 || p.Texture >= -1 && p.Texture < textures));
    internal static bool CheckBelts(List<ReplayBelt> belts, ReplayData replay, ref long bytes, ref long total, ref long segments)
    {
        if (belts.Count > 16) return false;
        try
        {
            foreach (var b in belts)
            {
                if (b == null || b.Count < 1 || b.Count > 2048 || b.Mesh < 0 || b.Mesh >= replay.Meshes.Count
                    || b.Surface < 0 || b.Surface >= replay.Surfaces.Count || b.Frames == null || b.Frames.Count < 1
                    || b.Frames.Count > ReplayData.MaxSeconds * 20 + 2 || b.Frames[0]?.Time != 0) return false;
                segments += b.Count;
                if (segments > 8192) return false;
                float before = -1;
                foreach (var f in b.Frames)
                {
                    if (f == null || !float.IsFinite(f.Time) || f.Time <= before || f.Time > replay.Duration || f.Data == null) return false;
                    before = f.Time; bytes += f.Data.Length; total += (long)b.Count * 40;
                    if (bytes > MaxBeltBytes || total > 512L * 1024 * 1024) return false;
                    var data = Unpack(f.Data, b.Count * 40);
                    for (int i = 0; i < data.Length; i += 4)
                        if (!float.IsFinite(BitConverter.ToSingle(data, i)) || Math.Abs(BitConverter.ToSingle(data, i)) >= 10000000) return false;
                    for (int i = 0; i < b.Count; i++)
                        if (Enumerable.Range(3, 4).Sum(j => Math.Pow(BitConverter.ToSingle(data, i * 40 + j * 4), 2)) < .000001) return false;
                }
            }
        }
        catch (InvalidDataException) { return false; }
        return true;
    }
    internal static string? CheckAudio(ReplayData replay)
    {
        const string error = "The replay has invalid or oversized sound data.";
        if (replay.SoundClips == null || replay.Sounds == null || replay.SoundClips.Count > 256 || replay.Sounds.Count > 1024) return error;
        long bytes = 0, decoded = 0, keys = 0;
        try
        {
            foreach (var clip in replay.SoundClips)
            {
                if (clip == null || clip.Name == null || clip.Name.Length > 512 || clip.Samples < 1 || clip.Channels < 1 || clip.Channels > 8
                    || clip.Frequency < 8000 || clip.Frequency > 192000 || clip.Samples / (double)clip.Frequency > 180 || clip.Pcm == null) return error;
                bytes += clip.Pcm.Length;
                if (bytes > MaxAudioBytes) return error;
                if (clip.Pcm.Length > 0)
                {
                    long size = (long)clip.Samples * clip.Channels * 2; decoded += size;
                    if (size > 32 * 1024 * 1024 || decoded > 128 * 1024 * 1024) return error;
                    Unpack(clip.Pcm, (int)size);
                }
            }
            foreach (var sound in replay.Sounds)
            {
                if (sound == null || sound.Clip < 0 || sound.Clip >= replay.SoundClips.Count || sound.Keys == null || sound.Keys.Count < 1
                    || !float.IsFinite(sound.SpatialBlend) || sound.SpatialBlend < 0 || sound.SpatialBlend > 1
                    || !float.IsFinite(sound.MinDistance) || !float.IsFinite(sound.MaxDistance) || sound.MinDistance < 0
                    || sound.MaxDistance < sound.MinDistance || sound.Rolloff < 0 || sound.Rolloff > 2) return error;
                float before = -1;
                foreach (var k in sound.Keys)
                {
                    if (k == null || !float.IsFinite(k.Time) || k.Time < 0 || k.Time <= before || k.Time > replay.Duration
                        || !Values(k.Position, 3) || !float.IsFinite(k.Volume) || k.Volume < 0 || k.Volume > 4
                        || !float.IsFinite(k.Pitch) || Math.Abs(k.Pitch) > 3 || k.Sample < 0 || k.Sample >= replay.SoundClips[sound.Clip].Samples) return error;
                    before = k.Time; keys++;
                    if (keys > 100000) return error;
                }
            }
        }
        catch (InvalidDataException) { return error; }
        return null;
    }
    internal static int SoundKeyAt(IReadOnlyList<ReplaySoundKey> keys, float time)
    {
        int lo = 0, hi = keys.Count - 1;
        if (time < keys[0].Time) return -1;
        while (lo < hi) { int m = (lo + hi + 1) / 2; if (keys[m].Time <= time) lo = m; else hi = m - 1; }
        return lo;
    }
}
