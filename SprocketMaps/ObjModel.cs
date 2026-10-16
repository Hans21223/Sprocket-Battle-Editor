using System.Buffers.Text;
using System.Text;

namespace SprocketMaps;

/// An OBJ map (with its MTL materials) read as Unity's OBJ importer reads it, for maps built in the game: one part per
/// group (`g`, or `o` in a file without groups), a submesh per material, X mirrored into Unity's left-handed space (each
/// triangle reversed so faces still face out), and normals smoothed across edges under 60 degrees (hard edges stay sharp)
/// unless the file has its own. Each part's pivot is the bottom centre of its bounds. Plain maths: the game makes meshes
/// from the arrays.
internal static class ObjModel
{
    internal sealed record Material(string Name, float R, float G, float B, float A, string? Texture, bool Cutout,
        float ScaleU = 1, float ScaleV = 1, float OffsetU = 0, float OffsetV = 0);

    internal sealed class Part
    {
        internal string Name = "";
        internal float CenterX, CenterY, CenterZ;   // the pivot, in Unity space
        internal float[] Positions = Array.Empty<float>(), Normals = Array.Empty<float>(), Uvs = Array.Empty<float>(); // relative to the pivot
        internal readonly List<(string Material, int[] Triangles)> Submeshes = new();
        internal int VertexCount => Positions.Length / 3;
    }

    internal sealed record Scene(List<Part> Parts, Dictionary<string, Material> Materials);

    static readonly byte[] UseMtl = Encoding.ASCII.GetBytes("usemtl");

    sealed class Polygons
    {
        internal readonly List<int> Corners = new(); // (v, vt, vn) per corner, -1 for none
        internal readonly List<int> Sizes = new();   // corners per polygon
    }

    internal static Scene Read(string obj, Func<string, bool> keep, CancellationToken token = default)
    {
        var positions = new List<float>(); var uvs = new List<float>(); var normals = new List<float>();
        var parts = new Dictionary<string, Dictionary<string, Polygons>>(StringComparer.Ordinal);
        var order = new List<string>();
        string group = "default", material = "";
        bool hasGroups = false, keeping = keep(group);
        Polygons? current = null;
        foreach (var line in Lines(obj, token))
        {
            var text = line.Span;
            if (text.Length < 2) continue;
            byte a = text[0], b = text[1];
            if (a == 'v' && b == ' ') Floats(text[2..], positions, 3);
            else if (a == 'v' && b == 't') Floats(text[2..], uvs, 2);
            else if (a == 'v' && b == 'n') Floats(text[2..], normals, 3);
            else if (a == 'f' && b == ' ')
            {
                if (!keeping) continue;
                if (current == null)
                {
                    if (!parts.TryGetValue(group, out var byMaterial)) { parts[group] = byMaterial = new(StringComparer.Ordinal); order.Add(group); }
                    if (!byMaterial.TryGetValue(material, out current)) byMaterial[material] = current = new();
                }
                int count = 0, at = 2;
                while (Next(text, ref at, out var corner))
                {
                    int v = 0, t = 0, n = 0, field = 0, start = 0;
                    for (int i = 0; i <= corner.Length; i++)
                        if (i == corner.Length || corner[i] == '/')
                        {
                            if (i > start)
                            {
                                if (!Utf8Parser.TryParse(corner[start..i], out int value, out _)) throw new InvalidDataException("An OBJ face has an invalid index.");
                                if (field == 0) v = value; else if (field == 1) t = value; else n = value;
                            }
                            field++; start = i + 1;
                        }
                    current.Corners.Add(Resolve(v, positions.Count / 3));
                    current.Corners.Add(t == 0 ? -1 : Resolve(t, uvs.Count / 2));
                    current.Corners.Add(n == 0 ? -1 : Resolve(n, normals.Count / 3));
                    count++;
                }
                if (count >= 3) current.Sizes.Add(count);
                else current.Corners.RemoveRange(current.Corners.Count - 3 * count, 3 * count);
            }
            else if (a == 'g' && b == ' ') { hasGroups = true; group = Name(text[1..], "default"); keeping = keep(group); current = null; }
            else if (a == 'o' && b == ' ' && !hasGroups) { group = Name(text[1..], "default"); keeping = keep(group); current = null; }
            else if (text.Length >= 6 && text[..6].SequenceEqual(UseMtl)) { material = Name(text[6..], ""); current = null; }
        }
        var materials = ReadMaterials(Path.GetDirectoryName(Path.GetFullPath(obj))!, token);
        var built = new List<Part>();
        foreach (var name in order)
        {
            token.ThrowIfCancellationRequested();
            if (Build(name, parts[name], positions, uvs, normals) is { } part) built.Add(part);
        }
        return new(built, materials);
    }

    static int Resolve(int index, int count)
    {
        int resolved = index > 0 ? index - 1 : count + index;
        if (resolved < 0 || resolved >= count) throw new InvalidDataException("An OBJ face refers to a point that isn't there.");
        return resolved;
    }

    static Part? Build(string name, Dictionary<string, Polygons> byMaterial, List<float> positions, List<float> uvs, List<float> normals)
    {
        var materials = byMaterial.Keys.ToArray();
        bool ownNormals = byMaterial.Values.All(p => { for (int i = 2; i < p.Corners.Count; i += 3) if (p.Corners[i] < 0) return false; return true; });
        // Each polygon's normal over its whole outline (Newell), area-weighted; and the polygons at each point.
        var polygonNormals = new float[materials.Length][];
        var atPoint = new Dictionary<int, List<(int Material, int Polygon)>>();
        if (!ownNormals)
            for (int m = 0; m < materials.Length; m++)
            {
                var polygons = byMaterial[materials[m]]; var list = polygonNormals[m] = new float[polygons.Sizes.Count * 3];
                int at = 0;
                for (int poly = 0; poly < polygons.Sizes.Count; poly++)
                {
                    int size = polygons.Sizes[poly];
                    float nx = 0, ny = 0, nz = 0;
                    for (int k = 0; k < size; k++)
                    {
                        int p = polygons.Corners[3 * (at + k)], q = polygons.Corners[3 * (at + (k + 1) % size)];
                        float px = positions[3 * p], py = positions[3 * p + 1], pz = positions[3 * p + 2];
                        float qx = positions[3 * q], qy = positions[3 * q + 1], qz = positions[3 * q + 2];
                        nx += (py - qy) * (pz + qz); ny += (pz - qz) * (px + qx); nz += (px - qx) * (py + qy);
                        if (!atPoint.TryGetValue(p, out var users)) atPoint[p] = users = new(4);
                        users.Add((m, poly));
                    }
                    list[3 * poly] = nx; list[3 * poly + 1] = ny; list[3 * poly + 2] = nz;
                    at += size;
                }
            }
        const float Smooth = 0.5f; // cos 60 degrees: Unity's default smoothing angle
        var vertices = new Dictionary<(int P, int T, int Nx, int Ny, int Nz), int>();
        var outPositions = new List<float>(); var outNormals = new List<float>(); var outUvs = new List<float>();
        var part = new Part { Name = name };
        var corner = new List<int>(16);
        for (int m = 0; m < materials.Length; m++)
        {
            var polygons = byMaterial[materials[m]];
            var indices = new List<int>(polygons.Corners.Count);
            int at = 0;
            for (int poly = 0; poly < polygons.Sizes.Count; poly++)
            {
                int size = polygons.Sizes[poly];
                corner.Clear();
                for (int k = 0; k < size; k++)
                {
                    int p = polygons.Corners[3 * (at + k)], t = polygons.Corners[3 * (at + k) + 1], n = polygons.Corners[3 * (at + k) + 2];
                    float nx = 0, ny = 0, nz = 0;
                    if (ownNormals) { nx = normals[3 * n]; ny = normals[3 * n + 1]; nz = normals[3 * n + 2]; }
                    else
                    {
                        var own = polygonNormals[m];
                        float fx = own[3 * poly], fy = own[3 * poly + 1], fz = own[3 * poly + 2], fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
                        if (fl > 0)
                            foreach (var (om, op) in atPoint[p])
                            {
                                var other = polygonNormals[om];
                                float gx = other[3 * op], gy = other[3 * op + 1], gz = other[3 * op + 2], gl = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
                                if (gl == 0 || (fx * gx + fy * gy + fz * gz) < Smooth * fl * gl) continue;
                                nx += gx; ny += gy; nz += gz;
                            }
                    }
                    float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length > 0) { nx /= length; ny /= length; nz /= length; } else { nx = 0; ny = 1; nz = 0; }
                    var key = (p, t, (int)MathF.Round(nx * 1024), (int)MathF.Round(ny * 1024), (int)MathF.Round(nz * 1024));
                    if (!vertices.TryGetValue(key, out int index))
                    {
                        vertices[key] = index = outPositions.Count / 3;
                        // Unity's OBJ importer mirrors X into its left-handed space.
                        outPositions.Add(-positions[3 * p]); outPositions.Add(positions[3 * p + 1]); outPositions.Add(positions[3 * p + 2]);
                        outNormals.Add(-nx); outNormals.Add(ny); outNormals.Add(nz);
                        if (t >= 0) { outUvs.Add(uvs[2 * t]); outUvs.Add(uvs[2 * t + 1]); } else { outUvs.Add(0); outUvs.Add(0); }
                    }
                    corner.Add(index);
                }
                // A fan from the first corner; the mirror turns faces inside out, so each triangle is reversed.
                for (int k = 1; k + 1 < size; k++) { indices.Add(corner[0]); indices.Add(corner[k + 1]); indices.Add(corner[k]); }
                at += size;
            }
            if (indices.Count > 0) part.Submeshes.Add((materials[m], indices.ToArray()));
        }
        if (outPositions.Count == 0) return null;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < outPositions.Count; i += 3)
        {
            minX = Math.Min(minX, outPositions[i]); maxX = Math.Max(maxX, outPositions[i]);
            minY = Math.Min(minY, outPositions[i + 1]); maxY = Math.Max(maxY, outPositions[i + 1]);
            minZ = Math.Min(minZ, outPositions[i + 2]); maxZ = Math.Max(maxZ, outPositions[i + 2]);
        }
        part.CenterX = (minX + maxX) / 2; part.CenterY = minY; part.CenterZ = (minZ + maxZ) / 2; // trees bend at the pivot
        var final = outPositions.ToArray();
        for (int i = 0; i < final.Length; i += 3) { final[i] -= part.CenterX; final[i + 1] -= part.CenterY; final[i + 2] -= part.CenterZ; }
        part.Positions = final; part.Normals = outNormals.ToArray(); part.Uvs = outUvs.ToArray();
        return part;
    }

    /// The MTL files beside the OBJ, as the importer reads them: diffuse colour, opacity, texture (with its -s and -o
    /// scale and offset) and an alpha map (map_d), which cuts the surface out by the texture's alpha.
    internal static Dictionary<string, Material> ReadMaterials(string folder, CancellationToken token = default)
    {
        var result = new Dictionary<string, Material>(StringComparer.Ordinal);
        var boundary = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        static bool Number(string text, out float value) => float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
        foreach (var file in Directory.GetFiles(folder, "*.mtl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Material? current = null;
            foreach (var raw in File.ReadLines(file))
            {
                token.ThrowIfCancellationRequested();
                var line = raw;
                int hash = line.IndexOf('#'); if (hash >= 0) line = line[..hash];
                var words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                if (words[0] == "newmtl") { current = new Material(string.Join(' ', words.Skip(1)), 1, 1, 1, 1, null, false); result[current.Name] = current; continue; }
                if (current == null) continue;
                float F(int i, float fallback) => i < words.Length && Number(words[i], out var v) ? v : fallback;
                switch (words[0])
                {
                    case "Kd": current = current with { R = F(1, 1), G = F(2, 1), B = F(3, 1) }; break;
                    case "d": current = current with { A = Math.Clamp(F(words.Length - 1, 1), 0, 1) }; break;
                    case "Tr": current = current with { A = Math.Clamp(1 - F(words.Length - 1, 0), 0, 1) }; break;
                    case "map_d": current = current with { Cutout = true }; break;
                    case "map_Kd":
                    {
                        float su = 1, sv = 1, ou = 0, ov = 0; int at = 1;
                        while (at < words.Length && words[at].StartsWith('-'))
                        {
                            var option = words[at++];
                            var values = new List<float>();
                            while (at < words.Length && values.Count < 3 && Number(words[at], out var v)) { values.Add(v); at++; }
                            if (option == "-s" && values.Count > 0) { su = values[0]; sv = values.Count > 1 ? values[1] : 1; }
                            else if (option == "-o" && values.Count > 0) { ou = values[0]; ov = values.Count > 1 ? values[1] : 0; }
                            else if (values.Count == 0 && at < words.Length) at++; // an option with a word value (-clamp on, -type sphere)
                        }
                        if (at >= words.Length) break;
                        var texture = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, string.Join(' ', words.Skip(at)).Replace('\\', '/')));
                        if (!texture.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An OBJ texture reference points outside its map files.");
                        current = current with { Texture = texture, ScaleU = su, ScaleV = sv, OffsetU = ou, OffsetV = ov };
                        break;
                    }
                }
                result[current.Name] = current;
            }
        }
        return result;
    }

    static string Name(ReadOnlySpan<byte> rest, string fallback)
    {
        var name = Encoding.UTF8.GetString(rest).Trim();
        return name.Length == 0 ? fallback : name;
    }

    /// The next space-separated word of a line, from `at`.
    static bool Next(ReadOnlySpan<byte> text, ref int at, out ReadOnlySpan<byte> word)
    {
        while (at < text.Length && (text[at] == ' ' || text[at] == '\t')) at++;
        int start = at;
        while (at < text.Length && text[at] != ' ' && text[at] != '\t') at++;
        word = text[start..at];
        return at > start;
    }

    static void Floats(ReadOnlySpan<byte> text, List<float> into, int count)
    {
        int added = 0, at = 0;
        while (added < count && Next(text, ref at, out var word))
        {
            if (!Utf8Parser.TryParse(word, out float value, out _) || !float.IsFinite(value)) throw new InvalidDataException("An OBJ file has an invalid number.");
            into.Add(value); added++;
        }
        for (; added < count; added++) into.Add(0);
    }

    /// The file's lines, without line ends, streamed in 1 MB blocks (a converted city is half a gigabyte).
    static IEnumerable<ReadOnlyMemory<byte>> Lines(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var buffer = new byte[1 << 20]; int filled = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            filled += read;
            int start = 0;
            for (int i = 0; i < filled; i++)
                if (buffer[i] == '\n')
                {
                    int end = i > start && buffer[i - 1] == '\r' ? i - 1 : i;
                    yield return new ReadOnlyMemory<byte>(buffer, start, end - start);
                    start = i + 1;
                }
            if (read == 0)
            {
                if (start < filled) yield return new ReadOnlyMemory<byte>(buffer, start, filled - start);
                yield break;
            }
            Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
            filled -= start;
            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // a line longer than the buffer
        }
    }
}
