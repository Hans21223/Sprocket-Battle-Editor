using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives.Zip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;

namespace SprocketMaps;

/// Source engine maps (Garry's Mod: a .bsp, or a workshop addon .gma holding one) as an OBJ with MTL and PNG textures,
/// which the importer then converts like any OBJ map. Converted: world brushes, brush entities, displacements and props
/// (static props and prop_* entities: LOD 0 of the default body, in their skin), with their materials from the addon,
/// the map's own pak lump and Garry's Mod's content (found through Steam's libraries). Left out: tool and sky faces and
/// the 3D skybox (the area around sky_camera). Water faces become the game's water; props under 15 m across, and the
/// map's physics props, become breakables. 1 unit = 1 inch. Filesystem only: it runs on the importer's worker.
internal static class SourceMaps
{
    /// Part of the import fingerprint of these maps: a change here converts them again.
    internal const string Version = "2";
    const float Scale = .0254f;

    internal static bool Is(string path) => Path.GetExtension(path).ToLowerInvariant() is ".gma" or ".bsp" && !Directory.Exists(path);

    /// The map's own name (an addon's map file, as workshop addons are all called gmpublisher.gma).
    internal static string MapName(string path)
    {
        if (!Path.GetExtension(path).Equals(".gma", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(path);
        try { return Path.GetFileNameWithoutExtension(new Addon(path).MapFile ?? path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return Path.GetFileNameWithoutExtension(path); }
    }

    // ------------------------------------------------------------------------------------------------ game content

    /// Garry's Mod's content folders (its own, and the Half-Life 2 / Counter-Strike content it ships) in every Steam
    /// library: Steam's library list sits beside this game's library, and Steam's own folder is in the registry.
    internal static List<string> GameFolders()
    {
        var steams = new List<string>();
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            for (var dir = exe == null ? null : Path.GetDirectoryName(exe); dir != null; dir = Path.GetDirectoryName(dir))
                if (Path.GetFileName(dir).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) { steams.Add(Path.GetDirectoryName(dir)!); break; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        try { if (SteamPath() is { } steam) steams.Add(steam); } catch (Exception) { } // no registry: the game's own library still counts
        var libraries = new List<string>(steams);
        foreach (var steam in steams)
        {
            var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(list)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return libraries.Select(l => Path.Combine(l, "steamapps", "common", "GarrysMod")).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => new[] { Path.Combine(g, "garrysmod"), Path.Combine(g, "sourceengine") }).Where(Directory.Exists).ToList();
    }
    static string? SteamPath() => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string s ? s.Replace('/', '\\') : null;

    /// A Garry's Mod addon: a header, a file list, then the files back to back.
    sealed class Addon
    {
        readonly string path;
        internal readonly Dictionary<string, (long Offset, long Size)> Files = new();
        internal string? MapFile => Files.Keys.Where(n => n.StartsWith("maps/") && n.EndsWith(".bsp")).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        internal Addon(string path)
        {
            this.path = path;
            using var stream = File.OpenRead(path);
            var reader = new BinaryReader(stream);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "GMAD") throw new InvalidDataException("This is not a Garry's Mod addon.");
            int version = reader.ReadByte();
            reader.ReadInt64(); reader.ReadInt64(); // Steam ID, timestamp
            if (version > 1) while (Text(reader).Length > 0) { } // required content, ended by an empty string
            Text(reader); Text(reader); Text(reader); reader.ReadInt32(); // name, description, author, addon version
            var entries = new List<(string, long)>();
            while (reader.ReadUInt32() != 0)
            {
                var name = Text(reader).Replace('\\', '/').ToLowerInvariant();
                long size = reader.ReadInt64(); reader.ReadUInt32();
                if (size < 0 || entries.Count > 200000) throw new InvalidDataException("The addon's file list is damaged.");
                entries.Add((name, size));
            }
            long offset = stream.Position;
            foreach (var (name, size) in entries) { Files[name] = (offset, size); offset += size; }
            if (offset > stream.Length) throw new InvalidDataException("The addon is cut short.");
        }
        internal byte[] Read(string name)
        {
            var (offset, size) = Files[name];
            if (size > int.MaxValue) throw new InvalidDataException("An addon file is too large: " + name);
            using var stream = File.OpenRead(path);
            stream.Seek(offset, SeekOrigin.Begin);
            var data = new byte[size];
            Fill(stream, data, 0, data.Length);
            return data;
        }
        static string Text(BinaryReader reader)
        {
            var bytes = new List<byte>();
            for (byte b; (b = reader.ReadByte()) != 0;)
                if (bytes.Count < 65536) bytes.Add(b); else throw new InvalidDataException("The addon's header is damaged.");
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    /// A Valve pak (VPK) directory and its numbered archives.
    sealed class Vpk
    {
        readonly string path; readonly byte[] dir; readonly int dataStart;
        internal readonly Dictionary<string, (int Preload, int At, int Archive, uint Offset, uint Length)> Files = new();
        internal Vpk(string path)
        {
            this.path = path; dir = File.ReadAllBytes(path);
            if (dir.Length < 12 || U32(dir, 0) != 0x55aa1234) throw new InvalidDataException("Not a VPK: " + path);
            int p = U32(dir, 4) == 1 ? 12 : 28;
            dataStart = p + (int)U32(dir, 8);
            string Text() { int e = Array.IndexOf(dir, (byte)0, p); var s = Encoding.Latin1.GetString(dir, p, e - p); p = e + 1; return s; }
            for (string ext; (ext = Text()).Length > 0;)
                for (string folder; (folder = Text()).Length > 0;)
                    for (string name; (name = Text()).Length > 0;)
                    {
                        int preload = U16(dir, p + 4), archive = U16(dir, p + 6);
                        uint offset = U32(dir, p + 8), length = U32(dir, p + 12);
                        p += 18;
                        Files[((folder == " " ? "" : folder + "/") + name + "." + ext).ToLowerInvariant()] = (preload, p, archive, offset, length);
                        p += preload;
                    }
        }
        internal byte[] Read(string name)
        {
            var (preload, at, archive, offset, length) = Files[name];
            var data = new byte[preload + length];
            Array.Copy(dir, at, data, 0, preload);
            if (length == 0) return data;
            if (archive == 0x7fff) { Array.Copy(dir, dataStart + offset, data, preload, length); return data; }
            using var stream = File.OpenRead(path.Replace("_dir.vpk", $"_{archive:000}.vpk"));
            stream.Seek(offset, SeekOrigin.Begin);
            Fill(stream, data, preload, (int)length);
            return data;
        }
    }

    /// Files as the game mounts them: the addon, the map's pak lump, then Garry's Mod's loose files and VPKs.
    sealed class Content : IDisposable
    {
        readonly Addon? addon; readonly SharpCompress.Archives.IArchive pak; readonly Dictionary<string, SharpCompress.Archives.IArchiveEntry> packed = new();
        readonly List<string> loose = new(); readonly List<Vpk> vpks = new();
        internal Content(Addon? addon, ArraySegment<byte> pakLump, IEnumerable<string> games)
        {
            this.addon = addon;
            pak = ZipArchive.OpenArchive(new MemoryStream(pakLump.Array!, pakLump.Offset, pakLump.Count, false), new ReaderOptions());
            foreach (var entry in pak.Entries) if (!entry.IsDirectory && entry.Key != null) packed[entry.Key.Replace('\\', '/').ToLowerInvariant()] = entry;
            foreach (var game in games)
            {
                if (Directory.Exists(Path.Combine(game, "materials")) || Directory.Exists(Path.Combine(game, "models"))) loose.Add(game);
                foreach (var file in Directory.GetFiles(game, "*_dir.vpk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    try { vpks.Add(new Vpk(file)); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
            }
        }
        internal byte[]? Read(string name)
        {
            name = name.Replace('\\', '/').ToLowerInvariant();
            if (addon != null && addon.Files.ContainsKey(name)) return addon.Read(name);
            if (packed.TryGetValue(name, out var entry))
            {
                using var stream = entry.OpenEntryStream(); var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
            }
            foreach (var game in loose) { var file = Path.Combine(game, name); if (File.Exists(file)) return File.ReadAllBytes(file); }
            foreach (var vpk in vpks) if (vpk.Files.ContainsKey(name)) return vpk.Read(name);
            return null;
        }
        internal bool Has(string name)
        {
            name = name.Replace('\\', '/').ToLowerInvariant();
            return addon?.Files.ContainsKey(name) == true || packed.ContainsKey(name) || loose.Any(g => File.Exists(Path.Combine(g, name))) || vpks.Any(v => v.Files.ContainsKey(name));
        }
        public void Dispose() => pak.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ materials

    /// A VMT: its shader and its keys (lower case), with sub-blocks as nested dictionaries.
    static (string Shader, Dictionary<string, object> Values) KeyValues(string text)
    {
        text = Regex.Replace(text, "//[^\n]*", "");
        var tokens = Regex.Matches(text, "\"([^\"]*)\"|([{}])|([^\\s{}\"]+)")
            .Select(m => (Value: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, Brace: m.Groups[2].Success)).ToList();
        int pos = 0;
        bool Open(int i) => i < tokens.Count && tokens[i].Brace && tokens[i].Value == "{";
        Dictionary<string, object> Block()
        {
            var block = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (pos < tokens.Count)
            {
                var token = tokens[pos++];
                if (token.Brace && token.Value == "}") return block;
                if (Open(pos)) { pos++; block[token.Value] = Block(); }
                else if (pos < tokens.Count) block[token.Value] = tokens[pos++].Value;
            }
            return block;
        }
        var shader = tokens.Count > 0 ? tokens[0].Value.ToLowerInvariant() : "";
        pos = Open(1) ? 2 : 1;
        return (shader, Block());
    }

    static (string Shader, Dictionary<string, object> Values) Material(Content content, string name, int depth = 0)
    {
        var raw = content.Read("materials/" + name + ".vmt");
        if (raw == null) return ("", new(StringComparer.OrdinalIgnoreCase));
        var (shader, values) = KeyValues(Encoding.Latin1.GetString(raw));
        if (shader != "patch" || depth >= 4) return (shader, values);
        var include = Regex.Replace(Str(values, "include").Replace('\\', '/').ToLowerInvariant(), "^materials/|\\.vmt$", "");
        var (baseShader, merged) = Material(content, include, depth + 1);
        foreach (var section in new[] { "insert", "replace" })
            if (values.TryGetValue(section, out var block) && block is Dictionary<string, object> changes)
                foreach (var (key, value) in changes) merged[key] = value;
        return (baseShader, merged);
    }
    static string Str(Dictionary<string, object> values, string key) => values.TryGetValue(key, out var v) && v is string s ? s : "";

    // ------------------------------------------------------------------------------------------------ textures

    /// The largest mip of a VTF's first frame, as RGBA rows from the top.
    static (int Width, int Height, byte[] Rgba) Vtf(byte[] d)
    {
        if (d.Length < 64 || d[0] != 'V' || d[1] != 'T' || d[2] != 'F') throw new InvalidDataException("not a VTF");
        uint minor = U32(d, 8), header = U32(d, 12);
        int width = U16(d, 16), height = U16(d, 18), frames = Math.Max(1, (int)U16(d, 24));
        uint flags = U32(d, 20);
        int format = I32(d, 52), mips = d[56], lowFormat = I32(d, 57), lowW = d[61], lowH = d[62];
        int depth = minor >= 2 ? Math.Max(1, (int)U16(d, 63)) : 1, faces = (flags & 0x4000) != 0 ? 6 : 1;
        long Size(int f, int w, int h) => f switch
        {
            13 or 20 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
            14 or 15 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
            0 or 1 or 11 or 12 or 16 or 23 => (long)w * h * 4,
            2 or 3 or 9 or 10 => (long)w * h * 3,
            4 or 6 or 17 or 18 or 19 or 21 or 22 => (long)w * h * 2,
            5 or 8 => (long)w * h,
            24 or 25 => (long)w * h * 8,
            _ => throw new InvalidDataException("VTF image format " + f),
        };
        long start;
        if (minor >= 3)
        {
            start = -1;
            for (int i = 0, count = (int)U32(d, 68); i < count && 80 + 8 * i + 8 <= d.Length; i++)
                if (d[80 + 8 * i] == 0x30 && d[81 + 8 * i] == 0 && d[82 + 8 * i] == 0) start = U32(d, 84 + 8 * i);
            if (start < 0) throw new InvalidDataException("VTF without an image");
        }
        else start = header + (lowFormat != -1 && lowW > 0 ? Size(lowFormat, lowW, lowH) : 0);
        for (int mip = mips - 1; mip > 0; mip--)
            start += Size(format, Math.Max(1, width >> mip), Math.Max(1, height >> mip)) * frames * faces * Math.Max(1, depth >> mip);
        if (start < 0 || start + Size(format, width, height) > d.Length) throw new InvalidDataException("VTF image cut short");
        int at = (int)start;
        var rgba = new byte[width * height * 4];
        if (format is 13 or 20 or 14 or 15) { Dxt(d, at, width, height, format is 14 ? 2 : format is 15 ? 3 : 1, rgba); return (width, height, rgba); }
        for (int i = 0; i < width * height; i++)
        {
            byte r, g, b, a = 255;
            switch (format)
            {
                case 0: r = d[at + 4 * i]; g = d[at + 4 * i + 1]; b = d[at + 4 * i + 2]; a = d[at + 4 * i + 3]; break;
                case 1: a = d[at + 4 * i]; b = d[at + 4 * i + 1]; g = d[at + 4 * i + 2]; r = d[at + 4 * i + 3]; break;
                case 11: a = d[at + 4 * i]; r = d[at + 4 * i + 1]; g = d[at + 4 * i + 2]; b = d[at + 4 * i + 3]; break;
                case 12: b = d[at + 4 * i]; g = d[at + 4 * i + 1]; r = d[at + 4 * i + 2]; a = d[at + 4 * i + 3]; break;
                case 16: b = d[at + 4 * i]; g = d[at + 4 * i + 1]; r = d[at + 4 * i + 2]; break;
                case 2: case 9: r = d[at + 3 * i]; g = d[at + 3 * i + 1]; b = d[at + 3 * i + 2]; break;
                case 3: case 10: b = d[at + 3 * i]; g = d[at + 3 * i + 1]; r = d[at + 3 * i + 2]; break;
                case 5: r = g = b = d[at + i]; break;
                case 6: r = g = b = d[at + 2 * i]; a = d[at + 2 * i + 1]; break;
                case 8: r = g = b = 255; a = d[at + i]; break;
                case 4: case 17:
                    int c = U16(d, at + 2 * i);
                    r = (byte)((c >> 11 & 31) * 255 / 31); g = (byte)((c >> 5 & 63) * 255 / 63); b = (byte)((c & 31) * 255 / 31);
                    if (format == 17) (r, b) = (b, r);
                    break;
                default: throw new InvalidDataException("VTF image format " + format + " is not converted");
            }
            rgba[4 * i] = r; rgba[4 * i + 1] = g; rgba[4 * i + 2] = b; rgba[4 * i + 3] = a;
        }
        return (width, height, rgba);
    }

    /// DXT1 (kind 1), DXT3 (2) and DXT5 (3) blocks to RGBA.
    static void Dxt(byte[] d, int at, int width, int height, int kind, byte[] rgba)
    {
        int blocksWide = Math.Max(1, (width + 3) / 4), blocksHigh = Math.Max(1, (height + 3) / 4), size = kind == 1 ? 8 : 16;
        Span<byte> colours = stackalloc byte[16]; Span<byte> alphas = stackalloc byte[8];
        for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int block = at + (by * blocksWide + bx) * size, colour = kind == 1 ? block : block + 8;
                int c0 = U16(d, colour), c1 = U16(d, colour + 2);
                for (int k = 0; k < 2; k++)
                {
                    int c = k == 0 ? c0 : c1;
                    colours[4 * k] = (byte)((c >> 11 & 31) * 255 / 31); colours[4 * k + 1] = (byte)((c >> 5 & 63) * 255 / 63);
                    colours[4 * k + 2] = (byte)((c & 31) * 255 / 31); colours[4 * k + 3] = 255;
                }
                bool four = kind != 1 || c0 > c1;
                for (int j = 0; j < 3; j++)
                {
                    colours[8 + j] = (byte)(four ? (2 * colours[j] + colours[4 + j]) / 3 : (colours[j] + colours[4 + j]) / 2);
                    colours[12 + j] = (byte)(four ? (colours[j] + 2 * colours[4 + j]) / 3 : 0);
                }
                colours[11] = 255; colours[15] = (byte)(four ? 255 : 0);
                if (kind == 3)
                {
                    alphas[0] = d[block]; alphas[1] = d[block + 1];
                    for (int i = 1; i < 7; i++)
                        alphas[1 + i] = alphas[0] > alphas[1] ? (byte)(((7 - i) * alphas[0] + i * alphas[1]) / 7)
                            : i < 5 ? (byte)(((5 - i) * alphas[0] + i * alphas[1]) / 5) : (byte)(i == 5 ? 0 : 255);
                }
                ulong alphaBits = kind == 3 ? (ulong)U32(d, block + 2) | (ulong)U16(d, block + 6) << 32 : 0;
                uint bits = U32(d, colour + 4);
                for (int p = 0; p < 16; p++)
                {
                    int x = bx * 4 + (p & 3), y = by * 4 + (p >> 2);
                    if (x >= width || y >= height) continue;
                    int o = 4 * (y * width + x), index = (int)(bits >> (2 * p) & 3);
                    rgba[o] = colours[4 * index]; rgba[o + 1] = colours[4 * index + 1]; rgba[o + 2] = colours[4 * index + 2];
                    rgba[o + 3] = kind switch
                    {
                        2 => (byte)((d[block + p / 2] >> (4 * (p & 1)) & 15) * 17),
                        3 => alphas[(int)(alphaBits >> (3 * p) & 7)],
                        _ => colours[4 * index + 3],
                    };
                }
            }
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static void Png(string path, int width, int height, byte[] rgba)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var head = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(head, width); BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4), height);
        head[8] = 8; head[9] = 6; // 8 bits, RGBA
        Chunk(file, "IHDR", head);
        var image = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(image, System.IO.Compression.CompressionLevel.Fastest, true))
            for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgba, y * width * 4, width * 4); }
        Chunk(file, "IDAT", image.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
        static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length); s.Write(number);
            var name = Encoding.ASCII.GetBytes(type); s.Write(name); s.Write(data);
            uint crc = 0xffffffff;
            foreach (var b in name) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); s.Write(number);
        }
    }

    // ------------------------------------------------------------------------------------------------ models

    sealed record Model(List<(int Slot, Vector[] Positions, (float U, float V)[] Uvs)> Triangles, Func<int, int, string> MaterialOf);

    /// LOD 0 of a model's default body, in model space, its triangles wound counter-clockwise around their vertex
    /// normals (MDL + VVD + VTX); null when a part is missing.
    static Model? LoadModel(Content content, string name)
    {
        var mdl = content.Read(name);
        if (mdl == null || mdl.Length < 240 || Encoding.ASCII.GetString(mdl, 0, 4) != "IDST") return null;
        var stem = name[..^4];
        var vvd = content.Read(stem + ".vvd");
        var vtx = new[] { ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx" }.Select(e => content.Read(stem + e)).FirstOrDefault(x => x != null);
        if (vvd == null || vtx == null || vvd.Length < 64 || Encoding.ASCII.GetString(vvd, 0, 4) != "IDSV") return null;
        string CString(int at) { int end = Array.IndexOf(mdl, (byte)0, at); return Encoding.Latin1.GetString(mdl, at, end - at); }
        int textureCount = I32(mdl, 204), textureIndex = I32(mdl, 208), cdCount = I32(mdl, 212), cdIndex = I32(mdl, 216);
        int skinRefs = I32(mdl, 220), skinFamilies = I32(mdl, 224), skinIndex = I32(mdl, 228), bodyCount = I32(mdl, 232), bodyIndex = I32(mdl, 236);
        var textures = Enumerable.Range(0, textureCount).Select(i => CString(textureIndex + 64 * i + I32(mdl, textureIndex + 64 * i)).Replace('\\', '/').Trim('/')).ToArray();
        var folders = Enumerable.Range(0, cdCount).Select(i => CString(I32(mdl, cdIndex + 4 * i)).Replace('\\', '/').Trim('/')).ToArray();
        var skins = Enumerable.Range(0, skinRefs > 0 ? skinFamilies : 0)
            .Select(f => Enumerable.Range(0, skinRefs).Select(r => (int)I16(mdl, skinIndex + 2 * (skinRefs * f + r))).ToArray()).ToArray();
        // VVD: LOD 0's vertices, put in order by the fixup table when there is one.
        int lod0 = I32(vvd, 16), fixups = I32(vvd, 48), fixupIndex = I32(vvd, 52), data = I32(vvd, 56);
        var order = new List<int>();
        for (int k = 0; k < fixups; k++)
            if (I32(vvd, fixupIndex + 12 * k) >= 0) order.AddRange(Enumerable.Range(I32(vvd, fixupIndex + 12 * k + 4), I32(vvd, fixupIndex + 12 * k + 8)));
        if (fixups == 0) order.AddRange(Enumerable.Range(0, lod0));
        Vector Vec(byte[] b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));
        var vertices = order.Select(i => data + 48 * i + 16).Select(at => (Position: Vec(vvd, at), Normal: Vec(vvd, at + 12), Uv: (F32(vvd, at + 24), 1 - F32(vvd, at + 28)))).ToArray();
        var triangles = new List<(int, Vector[], (float, float)[])>();
        int vtxBodies = I32(vtx, 28), vtxBodyIndex = I32(vtx, 32);
        for (int bp = 0; bp < Math.Min(vtxBodies, bodyCount); bp++)
        {
            int vb = vtxBodyIndex + 8 * bp, mb = bodyIndex + 16 * bp;
            if (I32(vtx, vb) == 0 || I32(mdl, mb + 4) == 0) continue;
            int vm = vb + I32(vtx, vb + 4);              // model 0: the default body
            int vl = vm + I32(vtx, vm + 4);              // LOD 0
            int mm = mb + I32(mdl, mb + 12);
            int meshes = Math.Min(I32(vtx, vl), I32(mdl, mm + 72)), meshIndex = I32(mdl, mm + 76), first = I32(mdl, mm + 84) / 48;
            for (int m = 0; m < meshes; m++)
            {
                int vme = vl + I32(vtx, vl + 4) + 9 * m, mme = mm + meshIndex + 116 * m;
                int slot = I32(mdl, mme), offset = I32(mdl, mme + 12);
                for (int g = 0, groups = I32(vtx, vme); g < groups; g++)
                {
                    int vg = vme + I32(vtx, vme + 4) + 25 * g;
                    int nv = I32(vtx, vg), vo = I32(vtx, vg + 4), ni = I32(vtx, vg + 8), io = I32(vtx, vg + 12), ns = I32(vtx, vg + 16), so = I32(vtx, vg + 20);
                    var ids = Enumerable.Range(0, nv).Select(i => (int)U16(vtx, vg + vo + 9 * i + 4)).ToArray();
                    var indices = Enumerable.Range(0, ni).Select(i => (int)U16(vtx, vg + io + 2 * i)).ToArray();
                    for (int st = 0; st < ns; st++)
                    {
                        int sh = vg + so + 27 * st, count = I32(vtx, sh), start = I32(vtx, sh + 4);
                        bool strip = (vtx[sh + 18] & 2) != 0;
                        for (int t = 0; t + 2 < count; t += strip ? 1 : 3)
                        {
                            var corner = new[] { indices[start + t], indices[start + t + 1], indices[start + t + 2] };
                            if (strip && t % 2 == 1) (corner[0], corner[1]) = (corner[1], corner[0]);
                            var v = corner.Select(i => vertices[first + offset + ids[i]]).ToArray();
                            var n = v[0].Normal + v[1].Normal + v[2].Normal;
                            if (Vector.Dot(Vector.Cross(v[1].Position - v[0].Position, v[2].Position - v[0].Position), n) < 0) Array.Reverse(v);
                            triangles.Add((slot, v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv).ToArray()));
                        }
                    }
                }
            }
        }
        string MaterialOf(int slot, int skin)
        {
            if (skins.Length > 0 && slot >= 0 && slot < skinRefs) slot = skins[skin >= 0 && skin < skins.Length ? skin : 0][slot];
            var texture = slot >= 0 && slot < textures.Length ? textures[slot] : "";
            foreach (var folder in folders)
            {
                var path = (folder.Length > 0 ? folder + "/" : "") + texture;
                if (content.Has("materials/" + path + ".vmt")) return path;
            }
            return (folders.Length > 0 && folders[0].Length > 0 ? folders[0] + "/" : "") + texture;
        }
        return new(triangles, MaterialOf);
    }

    /// Source's AngleMatrix (degrees, pitch yaw roll): rows of the rotation from model axes to world axes.
    static Vector[] AngleMatrix(float pitch, float yaw, float roll)
    {
        double p = pitch * Math.PI / 180, y = yaw * Math.PI / 180, r = roll * Math.PI / 180;
        float sp = (float)Math.Sin(p), cp = (float)Math.Cos(p), sy = (float)Math.Sin(y), cy = (float)Math.Cos(y), sr = (float)Math.Sin(r), cr = (float)Math.Cos(r);
        return new[] { new Vector(cp * cy, sr * sp * cy - cr * sy, cr * sp * cy + sr * sy), new Vector(cp * sy, sr * sp * sy + cr * cy, cr * sp * sy - sr * cy), new Vector(-sp, sr * cp, cr * cp) };
    }

    // ------------------------------------------------------------------------------------------------ the map

    /// Writes <map>.obj, materials.mtl and textures/*.png into output. Returns a one-line summary.
    internal static string Convert(string source, string output, IEnumerable<string> games, CancellationToken token)
    {
        Addon? addon = null;
        byte[] b;
        string mapName;
        if (Path.GetExtension(source).Equals(".gma", StringComparison.OrdinalIgnoreCase))
        {
            addon = new Addon(source);
            var map = addon.MapFile ?? throw new InvalidDataException("This Garry's Mod addon has no map (maps/*.bsp).");
            b = addon.Read(map); mapName = Path.GetFileNameWithoutExtension(map);
        }
        else { b = File.ReadAllBytes(source); mapName = Path.GetFileNameWithoutExtension(source); }
        if (b.Length < 1036 || Encoding.ASCII.GetString(b, 0, 4) != "VBSP") throw new InvalidDataException("This is not a Source engine map.");
        if (I32(b, 4) is < 19 or > 21) throw new InvalidDataException("Source map version " + I32(b, 4) + " is not supported (19 to 21 are).");
        ArraySegment<byte> Lump(int i)
        {
            int off = I32(b, 8 + 16 * i), len = I32(b, 12 + 16 * i);
            if (off < 0 || len < 0 || (long)off + len > b.Length) throw new InvalidDataException("A map lump lies outside the file.");
            return Unpack(new ArraySegment<byte>(b, off, len));
        }
        T[] Items<T>(int lump, int size, Func<byte[], int, T> read)
        {
            var l = Lump(lump); var a = l.Array!;
            return Enumerable.Range(0, l.Count / size).Select(k => read(a, l.Offset + k * size)).ToArray();
        }
        var planes = Items(1, 20, (a, o) => (Normal: Vec(a, o), Dist: F32(a, o + 12)));
        var verts = Items(3, 12, Vec);
        var edges = Items(12, 4, (a, o) => (U16(a, o), U16(a, o + 2)));
        var surfedges = Items(13, 4, I32);
        var texinfo = Items(6, 72, (a, o) => (S: new[] { F32(a, o), F32(a, o + 4), F32(a, o + 8), F32(a, o + 12) }, T: new[] { F32(a, o + 16), F32(a, o + 20), F32(a, o + 24), F32(a, o + 28) }, Flags: I32(a, o + 64), Data: I32(a, o + 68)));
        var texdata = Items(2, 32, (a, o) => (Name: I32(a, o + 12), Width: I32(a, o + 16), Height: I32(a, o + 20)));
        var faces = Items(7, 56, (a, o) => (Plane: (int)U16(a, o), FirstEdge: I32(a, o + 4), Edges: (int)I16(a, o + 8), TexInfo: (int)I16(a, o + 10), Disp: (int)I16(a, o + 12)));
        var models = Items(14, 48, (a, o) => (First: I32(a, o + 40), Count: I32(a, o + 44)));
        var nodes = Items(5, 32, (a, o) => (Plane: I32(a, o), Front: I32(a, o + 4), Back: I32(a, o + 8)));
        var leafFaces = Items(16, 2, (a, o) => (int)U16(a, o));
        int leafSize = I32(b, 8 + 16 * 10 + 8) == 1 ? 32 : 56; // lump version 1 dropped the ambient light cube
        var leaves = Items(10, leafSize, (a, o) => (Area: I16(a, o + 6) & 0x1ff, First: (int)U16(a, o + 20), Count: (int)U16(a, o + 22)));
        var dispinfo = Items(26, 176, (a, o) => (Start: Vec(a, o), VertStart: I32(a, o + 12), Power: I32(a, o + 20)));
        var dispverts = Items(33, 20, (a, o) => (Offset: Vec(a, o), Dist: F32(a, o + 12)));
        var strings = Lump(43); var table = Items(44, 4, I32);
        var names = table.Select(o => { int at = strings.Offset + o, end = Array.IndexOf(strings.Array!, (byte)0, at); return Encoding.Latin1.GetString(strings.Array!, at, Math.Max(0, end - at)); }).ToArray();
        var entityLump = Lump(0);
        var entities = Regex.Matches(Encoding.Latin1.GetString(entityLump.Array!, entityLump.Offset, entityLump.Count), "\\{([^}]*)\\}")
            .Select(m => Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"\\s+\"([^\"]*)\"").GroupBy(k => k.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value)).ToList();
        using var content = new Content(addon, Lump(40), games);

        // The 3D skybox: the area of the leaf holding sky_camera.
        int LeafAt(Vector p)
        {
            int n = 0;
            for (int guard = 0; n >= 0 && guard < 100000; guard++)
            {
                var plane = planes[nodes[n].Plane];
                n = Vector.Dot(plane.Normal, p) - plane.Dist >= 0 ? nodes[n].Front : nodes[n].Back;
            }
            return Math.Clamp(-1 - n, 0, leaves.Length - 1);
        }
        var skyFaces = new HashSet<int>(); int? skyArea = null;
        if (entities.FirstOrDefault(e => e.GetValueOrDefault("classname") == "sky_camera") is { } sky && nodes.Length > 0 && leaves.Length > 0)
        {
            skyArea = leaves[LeafAt(Parse(sky.GetValueOrDefault("origin", "0 0 0")))].Area;
            foreach (var leaf in leaves.Where(l => l.Area == skyArea))
                for (int i = leaf.First; i < leaf.First + leaf.Count && i < leafFaces.Length; i++) skyFaces.Add(leafFaces[i]);
        }
        bool InSkybox(Vector p) => skyArea != null && leaves[LeafAt(p)].Area == skyArea;

        Directory.CreateDirectory(Path.Combine(output, "textures"));
        var materials = new Dictionary<string, (string Mtl, bool Water, bool AlphaTest, bool Translucent, string? Texture)>(StringComparer.OrdinalIgnoreCase);
        int missingTextures = 0;
        (string Mtl, bool Water, bool AlphaTest, bool Translucent, string? Texture) Mat(string name)
        {
            if (materials.TryGetValue(name, out var known)) return known;
            var lower = name.ToLowerInvariant().Replace('\\', '/');
            var (shader, values) = Material(content, lower);
            bool water = shader == "water" || lower.StartsWith("water/") || values.ContainsKey("$abovewater");
            // Authors write "\models\x" and "models/x.vtf"; the game accepts them all.
            var baseTexture = Regex.Replace(Str(values, "$basetexture").ToLowerInvariant().Replace('\\', '/').Trim('/'), "\\.(vtf|vmt)$", "");
            string? texture = null;
            if (baseTexture.Length > 0 && !water)
            {
                texture = Regex.Replace(baseTexture, "[^a-z0-9_]+", "_");
                // Short file names: the import's Unity project can't open paths over 260 characters.
                if (texture.Length > 60) texture = texture[..48] + "_" + System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(texture)))[..8].ToLowerInvariant();
                texture += ".png";
                var file = Path.Combine(output, "textures", texture);
                if (!File.Exists(file))
                    try
                    {
                        var raw = content.Read("materials/" + baseTexture + ".vtf") ?? throw new FileNotFoundException();
                        var (w, h, rgba) = Vtf(raw);
                        Png(file, w, h, rgba);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { texture = null; missingTextures++; }
            }
            return materials[name] = (Regex.Replace(name, "[^A-Za-z0-9_]+", "_"), water, Str(values, "$alphatest") == "1", Str(values, "$translucent") == "1", texture);
        }

        // Geometry, as Source coordinates; one OBJ group (Unity object) per name, a submesh per material.
        var groups = new Dictionary<string, Dictionary<string, List<float>>>(StringComparer.Ordinal);
        void Add(string group, string mtl, IReadOnlyList<Vector> positions, IReadOnlyList<(float U, float V)> uvs)
        {
            if (!groups.TryGetValue(group, out var byMaterial)) groups[group] = byMaterial = new(StringComparer.Ordinal);
            if (!byMaterial.TryGetValue(mtl, out var list)) byMaterial[mtl] = list = new();
            list.Add(positions.Count);
            for (int i = 0; i < positions.Count; i++) { list.Add(positions[i].X); list.Add(positions[i].Y); list.Add(positions[i].Z); list.Add(uvs[i].U); list.Add(uvs[i].V); }
        }

        int brushFaces = 0, displacements = 0, skyboxParts = 0;
        var wanted = new List<(int Model, Vector Origin)> { (0, default) };
        foreach (var e in entities)
        {
            var model = e.GetValueOrDefault("model", ""); var cls = e.GetValueOrDefault("classname", "");
            if (model.StartsWith("*") && int.TryParse(model[1..], out var index) && index < models.Length
                && !cls.StartsWith("trigger_") && !cls.StartsWith("func_areaportal") && !cls.StartsWith("func_occluder") && !cls.StartsWith("func_vehicleclip") && !cls.StartsWith("func_clip"))
                wanted.Add((index, Parse(e.GetValueOrDefault("origin", "0 0 0"))));
        }
        foreach (var (model, origin) in wanted)
            for (int fi = models[model].First; fi < models[model].First + models[model].Count && fi < faces.Length; fi++)
            {
                token.ThrowIfCancellationRequested();
                var f = faces[fi]; var ti = texinfo[f.TexInfo]; var td = texdata[ti.Data]; var name = names[td.Name];
                if (skyFaces.Contains(fi)) { skyboxParts++; continue; }
                if ((ti.Flags & (0x2 | 0x4 | 0x40 | 0x80 | 0x100 | 0x200)) != 0
                    || name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) && !name.Equals("tools/toolsblack", StringComparison.OrdinalIgnoreCase)) continue;
                var poly = new List<Vector>();
                for (int k = f.FirstEdge; k < f.FirstEdge + f.Edges; k++)
                {
                    int e = surfedges[k];
                    poly.Add(verts[e >= 0 ? edges[e].Item1 : edges[-e].Item2] + origin);
                }
                if (poly.Count < 3) continue;
                // A face's front is its plane's normal whatever its side flag: faces are wound clockwise around it.
                var normal = planes[f.Plane].Normal;
                var (mtl, water, _, _, _) = Mat(name);
                var group = water ? "Sprocket Water Surface" : mtl;
                (float, float) Uv(Vector p) => ((Vector.Dot(p, new(ti.S[0], ti.S[1], ti.S[2])) + ti.S[3]) / Math.Max(td.Width, 1),
                                                -(Vector.Dot(p, new(ti.T[0], ti.T[1], ti.T[2])) + ti.T[3]) / Math.Max(td.Height, 1));
                if (f.Disp >= 0 && f.Disp < dispinfo.Length)
                {
                    if (poly.Count != 4) continue;
                    var d = dispinfo[f.Disp];
                    // Displacements aren't in the leaves' face lists: placed by their centre, just in front of the face.
                    if (model == 0 && InSkybox((poly[0] + poly[1] + poly[2] + poly[3]) * .25f + normal)) { skyboxParts++; continue; }
                    int startCorner = Enumerable.Range(0, 4).OrderBy(k => (poly[k] - d.Start).LengthSquared).First();
                    var c = Enumerable.Range(0, 4).Select(k => poly[(startCorner + k) % 4]).ToArray();
                    int n = (1 << d.Power) + 1;
                    var grid = new (Vector Pos, (float, float) Uv, Vector Base)[n, n];
                    for (int i = 0; i < n; i++)
                        for (int k = 0; k < n; k++)
                        {
                            float s = i / (float)(n - 1), t = k / (float)(n - 1);
                            var left = c[0] + (c[1] - c[0]) * s; var right = c[3] + (c[2] - c[3]) * s;
                            var basePoint = left + (right - left) * t;
                            var dv = dispverts[Math.Min(d.VertStart + i * n + k, dispverts.Length - 1)];
                            grid[i, k] = (basePoint + dv.Offset * dv.Dist, Uv(basePoint), basePoint);
                        }
                    bool flip = Vector.Dot(Vector.Cross(grid[1, 0].Base - grid[0, 0].Base, grid[0, 1].Base - grid[0, 0].Base), normal) < 0;
                    for (int i = 0; i < n - 1; i++)
                        for (int k = 0; k < n - 1; k++)
                        {
                            var q = new[] { grid[i, k], grid[i + 1, k], grid[i + 1, k + 1], grid[i, k + 1] };
                            var tris = (i + k) % 2 == 0 ? new[] { (0, 1, 2), (0, 2, 3) } : new[] { (0, 1, 3), (1, 2, 3) };
                            foreach (var (x, y, z) in tris)
                            {
                                var t = flip ? new[] { q[z], q[y], q[x] } : new[] { q[x], q[y], q[z] };
                                Add(group, mtl, t.Select(v => v.Pos).ToArray(), t.Select(v => v.Uv).ToArray());
                            }
                        }
                    displacements++;
                    continue;
                }
                // The whole outline's normal (Newell): faces often start with three corners in a line.
                var newell = default(Vector);
                for (int k = 0; k < poly.Count; k++) { var p = poly[k]; var q = poly[(k + 1) % poly.Count]; newell += new Vector((p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y)); }
                if (Vector.Dot(newell, normal) < 0) poly.Reverse();
                Add(group, mtl, poly, poly.Select(Uv).ToArray());
                brushFaces++;
            }

        // Props: static props (game lump "sprp") and prop_* entities.
        var placed = new List<(string Model, Vector Origin, Vector Angles, int Skin, float Scale, bool Physics)>();
        var gameLump = Lump(35);
        for (int k = 0, count = gameLump.Count >= 4 ? I32(gameLump.Array!, gameLump.Offset) : 0; k < count; k++)
        {
            int at = gameLump.Offset + 4 + 16 * k;
            if (Encoding.ASCII.GetString(gameLump.Array!, at, 4) != "prps") continue;
            int offset = I32(gameLump.Array!, at + 8), length = I32(gameLump.Array!, at + 12);
            var g = Unpack(new ArraySegment<byte>(b, offset, length));
            var a = g.Array!; int p = g.Offset, end = g.Offset + g.Count;
            int dictionary = I32(a, p); p += 4;
            var modelNames = Enumerable.Range(0, dictionary).Select(i => { int s = p + 128 * i, z = Array.IndexOf(a, (byte)0, s, 128); return Encoding.Latin1.GetString(a, s, (z < 0 ? s + 128 : z) - s); }).ToArray();
            p += 128 * dictionary;
            p += 4 + 2 * I32(a, p); // leaves
            int props = I32(a, p); p += 4;
            int size = props > 0 ? (end - p) / props : 0;
            for (int i = 0; i < props && size >= 36; i++)
            {
                int o = p + size * i, kind = U16(a, o + 24);
                if (kind < modelNames.Length) placed.Add((modelNames[kind], Vec(a, o), Vec(a, o + 12), I32(a, o + 32), 1, false));
            }
        }
        foreach (var e in entities)
        {
            var cls = e.GetValueOrDefault("classname", ""); var model = e.GetValueOrDefault("model", "");
            if (!cls.StartsWith("prop_") || !model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            float.TryParse(e.GetValueOrDefault("modelscale", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale);
            float.TryParse(e.GetValueOrDefault("skin", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var skin);
            placed.Add((model, Parse(e.GetValueOrDefault("origin", "0 0 0")), Parse(e.GetValueOrDefault("angles", "0 0 0")), (int)skin, scale > 0 ? scale : 1, cls.StartsWith("prop_physics")));
        }
        var loaded = new Dictionary<string, Model?>(StringComparer.Ordinal);
        int propCount = 0, breakables = 0, missingModels = 0;
        foreach (var (rawModel, origin, angles, skin, scale, physics) in placed)
        {
            token.ThrowIfCancellationRequested();
            var modelName = rawModel.ToLowerInvariant().Replace('\\', '/');
            if (InSkybox(origin)) { skyboxParts++; continue; }
            if (!loaded.TryGetValue(modelName, out var model))
            {
                try { model = LoadModel(content, modelName); }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException) { model = null; }
                loaded[modelName] = model;
            }
            if (model == null) { missingModels++; continue; }
            var rotation = AngleMatrix(angles.X, angles.Y, angles.Z);
            Vector Place(Vector v) => origin + new Vector(Vector.Dot(rotation[0], v), Vector.Dot(rotation[1], v), Vector.Dot(rotation[2], v)) * scale;
            var label = Regex.Replace(modelName.StartsWith("models/") ? modelName[7..^4] : modelName[..^4], "[^A-Za-z0-9_]+", "_");
            var slots = new Dictionary<int, string>();
            var parts = model.Triangles.Select(t => (Mtl: slots.TryGetValue(t.Slot, out var m) ? m : slots[t.Slot] = Mat(model.MaterialOf(t.Slot, skin)).Mtl,
                                                     Positions: t.Positions.Select(Place).ToArray(), t.Uvs)).ToList();
            if (parts.Count == 0) continue;
            var min = parts.SelectMany(t => t.Positions).Aggregate(Vector.Min); var max = parts.SelectMany(t => t.Positions).Aggregate(Vector.Max);
            // Props under about 15 m across (cars, poles, bins, signs) and the map's physics props become breakables:
            // physics bodies a tank shoves aside, which don't block tank navigation. One object each.
            bool breakable = physics || (max - min).Length * Scale < 15;
            var group = breakable ? $"Sprocket Breakable {label} {propCount}" : "prop_" + label;
            if (breakable) breakables++;
            foreach (var (mtl, positions, uvs) in parts) Add(group, mtl, positions, uvs);
            propCount++;
        }

        var mtlFile = new StringBuilder();
        foreach (var (mtl, water, alphaTest, translucent, texture) in materials.Values.GroupBy(m => m.Mtl).Select(g => g.First()).OrderBy(m => m.Mtl, StringComparer.Ordinal))
        {
            // Water is replaced by the game's; an untextured surface (reflective glass) gets a glass grey, not white.
            mtlFile.Append("newmtl ").Append(mtl).Append("\nKd ").Append(water ? "0.2 0.35 0.4" : texture != null ? "1 1 1" : "0.45 0.5 0.55").Append('\n');
            if (translucent && !alphaTest) mtlFile.Append("d 0.998\n");
            if (texture != null) mtlFile.Append("map_Kd textures/").Append(texture).Append('\n');
            if (texture != null && alphaTest) mtlFile.Append("map_d textures/").Append(texture).Append('\n');
        }
        File.WriteAllText(Path.Combine(output, "materials.mtl"), mtlFile.ToString());
        using (var obj = new StreamWriter(Path.Combine(output, mapName + ".obj"), false, new UTF8Encoding(false), 1 << 20))
        {
            obj.Write("# " + mapName + ", converted from the Source map by Map Framework\nmtllib materials.mtl\n");
            int vertex = 1;
            var number = CultureInfo.InvariantCulture;
            foreach (var (group, byMaterial) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                obj.Write("g " + group + "\n"); // Unity's OBJ importer makes one object per g (not per o), a submesh per usemtl
                foreach (var (mtl, list) in byMaterial.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    obj.Write("usemtl " + mtl + "\n");
                    for (int i = 0; i < list.Count;)
                    {
                        int corners = (int)list[i++];
                        for (int k = 0; k < corners; k++, i += 5)
                        {
                            // Source: right-handed, Z up, inches. OBJ: right-handed, Y up, metres.
                            obj.Write("v "); obj.Write((list[i] * Scale).ToString("F5", number)); obj.Write(' ');
                            obj.Write((list[i + 2] * Scale).ToString("F5", number)); obj.Write(' ');
                            obj.Write((-list[i + 1] * Scale).ToString("F5", number)); obj.Write("\nvt ");
                            obj.Write(list[i + 3].ToString("F6", number)); obj.Write(' '); obj.Write(list[i + 4].ToString("F6", number)); obj.Write('\n');
                        }
                        obj.Write('f');
                        for (int k = 0; k < corners; k++) { obj.Write(' '); obj.Write(vertex + k); obj.Write('/'); obj.Write(vertex + k); }
                        obj.Write('\n');
                        vertex += corners;
                    }
                }
            }
        }
        return $"Source map '{mapName}': {brushFaces} faces, {displacements} displacements, {propCount} props ({breakables} breakable), "
            + $"{skyboxParts} 3D skybox parts left out, {materials.Count} materials, {missingTextures} textures missing, {missingModels} props without their model.";
    }

    /// A lump as stored, or unpacked when it is LZMA-compressed (later Source branches).
    static ArraySegment<byte> Unpack(ArraySegment<byte> lump)
    {
        var a = lump.Array!; int o = lump.Offset;
        if (lump.Count < 17 || a[o] != 'L' || a[o + 1] != 'Z' || a[o + 2] != 'M' || a[o + 3] != 'A') return lump;
        int actual = I32(a, o + 4), packed = I32(a, o + 8);
        var properties = new byte[5]; Array.Copy(a, o + 12, properties, 0, 5);
        var output = new byte[actual];
        using var stream = LzmaStream.Create(properties, new MemoryStream(a, o + 17, Math.Min(packed, lump.Count - 17), false), packed, actual, false);
        Fill(stream, output, 0, output.Length);
        return output;
    }

    static void Fill(Stream stream, byte[] buffer, int offset, int count)
    {
        for (int read; count > 0; offset += read, count -= read)
            if ((read = stream.Read(buffer, offset, count)) == 0) throw new InvalidDataException("The file is cut short.");
    }

    static Vector Parse(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
        return new(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2));
    }

    readonly record struct Vector(float X, float Y, float Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator *(Vector a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector Cross(Vector a, Vector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static Vector Min(Vector a, Vector b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static Vector Max(Vector a, Vector b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        public float LengthSquared => X * X + Y * Y + Z * Z;
        public float Length => MathF.Sqrt(LengthSquared);
    }
    static Vector Vec(byte[] a, int o) => new(F32(a, o), F32(a, o + 4), F32(a, o + 8));
    static float F32(byte[] a, int o) => BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(o, 4));
    static int I32(byte[] a, int o) => BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(o, 4));
    static uint U32(byte[] a, int o) => BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(o, 4));
    static short I16(byte[] a, int o) => BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(o, 2));
    static ushort U16(byte[] a, int o) => BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(o, 2));
}
