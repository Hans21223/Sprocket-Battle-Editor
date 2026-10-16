using System.Buffers.Binary;

namespace SprocketMaps;

/// A texture as Map Framework keeps it for maps built in the game (.smtex): every mip, largest first, rows bottom-up
/// as Unity keeps them, so the game uploads it as it is. Garry's Mod textures stay in their own DXT1/DXT5 blocks
/// (no decoding, a quarter of the memory); anything else is RGBA.
internal static class MapTexture
{
    internal enum Format : byte { Rgba32 = 0, Dxt1 = 1, Dxt5 = 2 }
    internal sealed record Image(Format Format, int Width, int Height, byte[][] Mips);
    const uint Magic = 0x58544D53; // "SMTX"

    internal static int MipBytes(Format format, int width, int height) => format == Format.Rgba32
        ? Math.Max(1, width) * Math.Max(1, height) * 4
        : Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * (format == Format.Dxt1 ? 8 : 16);

    internal static void Write(string path, Image image)
    {
        using var file = File.Create(path);
        Span<byte> head = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        head[4] = 1; head[5] = (byte)image.Format; head[6] = (byte)image.Mips.Length; head[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(head[12..], image.Height);
        file.Write(head);
        foreach (var mip in image.Mips) file.Write(mip);
    }

    internal static Image Read(byte[] data)
    {
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic || data[4] != 1 || data[5] > 2)
            throw new InvalidDataException("Not a Map Framework texture.");
        var format = (Format)data[5];
        int mips = data[6], width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)), height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || mips < 1 || mips > 15) throw new InvalidDataException("A map texture has an invalid size.");
        var list = new byte[mips][];
        int at = 16;
        for (int m = 0; m < mips; m++)
        {
            int size = MipBytes(format, width >> m, height >> m);
            if (at + size > data.Length) throw new InvalidDataException("A map texture is cut short.");
            list[m] = data.AsSpan(at, size).ToArray(); at += size;
        }
        return new(format, width, height, list);
    }

    /// One mip turned upside down: Source and image files store the top row first, Unity the bottom row.
    /// Block-compressed images are flipped block row by block row, and each block's own rows reversed.
    internal static byte[] Flip(Format format, byte[] data, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var flipped = new byte[data.Length];
        if (format == Format.Rgba32)
        {
            int row = width * 4;
            for (int y = 0; y < height; y++) Buffer.BlockCopy(data, y * row, flipped, (height - 1 - y) * row, row);
            return flipped;
        }
        int blockBytes = format == Format.Dxt1 ? 8 : 16, across = Math.Max(1, (width + 3) / 4), down = Math.Max(1, (height + 3) / 4);
        // A 2- or 1-pixel-tall mip keeps its rows at the top of its blocks.
        int rows = Math.Min(4, height);
        for (int by = 0; by < down; by++)
            for (int bx = 0; bx < across; bx++)
            {
                int from = (by * across + bx) * blockBytes, to = ((down - 1 - by) * across + bx) * blockBytes;
                Buffer.BlockCopy(data, from, flipped, to, blockBytes);
                int colour = to + (format == Format.Dxt1 ? 0 : 8);
                // Colour indices: one byte per row.
                for (int r = 0; r < rows; r++) flipped[colour + 4 + r] = data[from + (format == Format.Dxt1 ? 0 : 8) + 4 + rows - 1 - r];
                if (format == Format.Dxt5)
                {
                    // Alpha indices: 48 bits, 12 per row (3 bits a pixel).
                    ulong bits = 0, swapped = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)data[from + 2 + i] << (8 * i);
                    for (int r = 0; r < 4; r++)
                    {
                        int source = r < rows ? rows - 1 - r : r;
                        swapped |= (bits >> (12 * source) & 0xFFF) << (12 * r);
                    }
                    for (int i = 0; i < 6; i++) flipped[to + 2 + i] = (byte)(swapped >> (8 * i));
                }
            }
        return flipped;
    }

    /// The mip chain of an RGBA image (top row first, as given), each a box-filtered half of the last.
    internal static byte[][] Mips(byte[] rgba, int width, int height)
    {
        var list = new List<byte[]> { rgba };
        while (width > 1 || height > 1)
        {
            int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
            var last = list[^1]; var next = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(2 * x, width - 1), x1 = Math.Min(2 * x + 1, width - 1), y0 = Math.Min(2 * y, height - 1), y1 = Math.Min(2 * y + 1, height - 1);
                        next[(y * w + x) * 4 + c] = (byte)((last[(y0 * width + x0) * 4 + c] + last[(y0 * width + x1) * 4 + c]
                            + last[(y1 * width + x0) * 4 + c] + last[(y1 * width + x1) * 4 + c] + 2) / 4);
                    }
            list.Add(next); width = w; height = h;
        }
        return list.ToArray();
    }
}
