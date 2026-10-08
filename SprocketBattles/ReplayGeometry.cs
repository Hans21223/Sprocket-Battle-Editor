using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SprocketBattles;

internal static class ReplayGeometry
{
    internal static int ActorAllowance(int designs) => Math.Min(500000, ReplayData.MaxVertices / Math.Clamp(designs, 1, 32));
    internal static int AttributeBytes(int format) => format switch {
        0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, 2 or 3 or 6 or 7 => 1,
        _ => throw new InvalidDataException("Unsupported replay vertex format.") };
    internal static float Attribute(byte[] bytes, int offset, int format) => format switch {
        0 => BitConverter.ToSingle(bytes, offset),
        1 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, offset)),
        2 => bytes[offset] / 255f, 3 => Math.Max(-1, unchecked((sbyte)bytes[offset]) / 127f),
        4 => BitConverter.ToUInt16(bytes, offset) / 65535f, 5 => Math.Max(-1, BitConverter.ToInt16(bytes, offset) / 32767f),
        6 => bytes[offset], 7 => unchecked((sbyte)bytes[offset]), 8 => BitConverter.ToUInt16(bytes, offset),
        9 => BitConverter.ToInt16(bytes, offset), 10 => BitConverter.ToUInt32(bytes, offset), 11 => BitConverter.ToInt32(bytes, offset),
        _ => throw new InvalidDataException("Unsupported replay vertex format.") };
    internal static string Identity(ReplayMesh mesh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (int length in new[] { mesh.Vertices.Length, mesh.Normals.Length, mesh.Uv.Length, mesh.Submeshes.Count })
            hash.AppendData(BitConverter.GetBytes(length));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Normals.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Uv.AsSpan()));
        foreach (var sub in mesh.Submeshes)
        { hash.AppendData(BitConverter.GetBytes(sub.Length)); hash.AppendData(MemoryMarshal.AsBytes(sub.AsSpan())); }
        if (mesh.Submeshes.Count == 0) hash.AppendData(MemoryMarshal.AsBytes(mesh.Triangles.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static bool Png(byte[]? png) => png != null && png.Length >= 33 && png.Length <= ReplayData.MaxTextureBytes
        && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && png.AsSpan(12, 4).SequenceEqual(new byte[] { 73, 72, 68, 82 })
        && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) is > 0 and <= 2048
        && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) is > 0 and <= 2048;
}
