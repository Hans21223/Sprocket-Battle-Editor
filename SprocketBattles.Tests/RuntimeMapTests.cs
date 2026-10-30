using System.Text;
using SprocketMaps;

/// Maps the game builds itself: textures kept in their own blocks and turned for Unity, OBJ read as Unity's importer
/// reads it, a Garry's Mod map's sky and light, and the checked import record.
static class RuntimeMapTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception("Runtime maps: " + why); }

    internal static void Run()
    {
        Textures();
        Obj();
        Sky();
        Files();
        Console.WriteLine("RUNTIME_MAP_TESTS_OK: texture flips and mips, OBJ mirror/winding/hard edges/materials, sky faces and brightness, light entities, import records");
    }

    static void Textures()
    {
        // An 8x8 DXT1 image: 2x2 blocks, each with its own colour and row bytes 0..3.
        var dxt1 = new byte[32];
        for (int b = 0; b < 4; b++) { dxt1[8 * b] = (byte)(b + 1); for (int r = 0; r < 4; r++) dxt1[8 * b + 4 + r] = (byte)(16 * b + r); }
        var flipped = MapTexture.Flip(MapTexture.Format.Dxt1, dxt1, 8, 8);
        // Block rows swap (blocks 0,1 <-> 2,3); each block's index rows reverse.
        Check(flipped[0] == 3 && flipped[8] == 4 && flipped[16] == 1 && flipped[24] == 2, "DXT1 block rows are swapped");
        Check(flipped[4] == 16 * 2 + 3 && flipped[7] == 16 * 2 + 0, "DXT1 index rows are reversed");
        Check(MapTexture.Flip(MapTexture.Format.Dxt1, flipped, 8, 8).SequenceEqual(dxt1), "flipping DXT1 twice gives it back");
        // DXT5 alpha: 3-bit indices, rows of 12 bits.
        var dxt5 = new byte[16];
        ulong alpha = 0; for (int r = 0; r < 4; r++) alpha |= (ulong)(0x100 + r) << (12 * r);
        for (int i = 0; i < 6; i++) dxt5[2 + i] = (byte)(alpha >> (8 * i));
        for (int r = 0; r < 4; r++) dxt5[12 + r] = (byte)r;
        var turned = MapTexture.Flip(MapTexture.Format.Dxt5, dxt5, 4, 4);
        ulong after = 0; for (int i = 0; i < 6; i++) after |= (ulong)turned[2 + i] << (8 * i);
        Check((after & 0xFFF) == 0x103 && (after >> 36 & 0xFFF) == 0x100, "DXT5 alpha rows reverse");
        Check(turned[12] == 3 && turned[15] == 0, "DXT5 colour rows reverse");
        Check(MapTexture.Flip(MapTexture.Format.Dxt5, turned, 4, 4).SequenceEqual(dxt5), "flipping DXT5 twice gives it back");
        // A 2-pixel-tall mip keeps its rows at the top of the block.
        var small = new byte[8]; small[4] = 10; small[5] = 11; small[6] = 12; small[7] = 13;
        var smallFlipped = MapTexture.Flip(MapTexture.Format.Dxt1, small, 4, 2);
        Check(smallFlipped[4] == 11 && smallFlipped[5] == 10 && smallFlipped[6] == 12, "a short mip flips only its own rows");
        // RGBA rows, and the mip chain.
        var rgba = new byte[] { 0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 50, 50, 50, 255 }; // 2x2
        var rows = MapTexture.Flip(MapTexture.Format.Rgba32, rgba, 2, 2);
        Check(rows[0] == 200 && rows[8] == 0, "RGBA rows are swapped");
        var mips = MapTexture.Mips(rgba, 2, 2);
        Check(mips.Length == 2 && mips[1].Length == 4 && mips[1][0] == 88, "a 2x2 image averages to one pixel");
        Check(MapTexture.Mips(new byte[4 * 8], 4, 2).Select(m => m.Length).SequenceEqual(new[] { 32, 8, 4 }), "a 4x2 chain is 4x2, 2x1, 1x1");
        var file = Path.GetTempFileName();
        try
        {
            var image = new MapTexture.Image(MapTexture.Format.Dxt1, 8, 8, new[] { dxt1, new byte[8], new byte[8], new byte[8] });
            MapTexture.Write(file, image);
            var back = MapTexture.Read(File.ReadAllBytes(file));
            Check(back.Format == MapTexture.Format.Dxt1 && back.Width == 8 && back.Mips.Length == 4 && back.Mips[0].SequenceEqual(dxt1), "a texture file reads back");
            var cut = File.ReadAllBytes(file)[..40];
            try { MapTexture.Read(cut); Check(false, "a cut texture is refused"); } catch (InvalidDataException) { }
        }
        finally { File.Delete(file); }
        // A real VTF header: DXT1 8x8, one mip, version 7.2.
        var vtf = new byte[80 + 32];
        Encoding.ASCII.GetBytes("VTF\0").CopyTo(vtf, 0);
        BitConverter.GetBytes(7).CopyTo(vtf, 4); BitConverter.GetBytes(2).CopyTo(vtf, 8); BitConverter.GetBytes(80).CopyTo(vtf, 12);
        BitConverter.GetBytes((ushort)8).CopyTo(vtf, 16); BitConverter.GetBytes((ushort)8).CopyTo(vtf, 18);
        BitConverter.GetBytes((ushort)1).CopyTo(vtf, 24); BitConverter.GetBytes(13).CopyTo(vtf, 52); vtf[56] = 1;
        BitConverter.GetBytes(-1).CopyTo(vtf, 57); BitConverter.GetBytes((ushort)1).CopyTo(vtf, 63);
        dxt1.CopyTo(vtf, 80);
        var converted = SourceMaps.VtfImage(vtf);
        Check(converted.Format == MapTexture.Format.Dxt1 && converted.Mips.Length == 1 && converted.Mips[0].SequenceEqual(flipped), "a DXT1 VTF keeps its blocks, turned for Unity");
    }

    static void Obj()
    {
        var root = Path.Combine(Path.GetTempPath(), "SprocketRuntimeMapTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        try
        {
            File.WriteAllText(Path.Combine(root, "textures", "brick.png"), "x");
            File.WriteAllText(Path.Combine(root, "map.mtl"), "newmtl brick\nKd 1 0.5 0.25\nmap_Kd -s 2 3 textures/brick.png\nmap_d textures/brick.png\nnewmtl glass\nKd 0.4 0.5 0.6\nd 0.5\n");
            var obj = new StringBuilder("mtllib map.mtl\n");
            // A unit cube from (0,0,0) to (1,1,1), quads wound counter-clockwise from outside.
            foreach (var v in new[] { "0 0 0", "1 0 0", "1 1 0", "0 1 0", "0 0 1", "1 0 1", "1 1 1", "0 1 1" }) obj.Append("v ").Append(v).Append('\n');
            obj.Append("vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n");
            obj.Append("g Box\nusemtl brick\n");
            foreach (var f in new[] { "1 4 3 2", "5 6 7 8", "1 2 6 5", "4 8 7 3", "1 5 8 4", "2 3 7 6" })
                obj.Append("f ").Append(string.Join(' ', f.Split(' ').Select((i, k) => i + "/" + (k + 1)))).Append('\n');
            // A gently bent sheet (10 degrees) and a sharply bent one (90), with negative indices.
            obj.Append("g Sprocket Water Surface\nusemtl glass\nv 0 5 0\nv 1 5 0\nv 1 5 1\nv 0 5 1\nf -4 -1 -2 -3\n");
            obj.Append("g Bent\nv 0 10 0\nv 1 10 0\nv 1 10 1\nv 0 10 1\nv 2 10.1763 0\nv 2 10.1763 1\nf -6 -3 -4 -5\nf -5 -4 -1 -2\n");
            obj.Append("g Sharp\nv 0 20 0\nv 1 20 0\nv 1 20 1\nv 0 20 1\nv 1 21 0\nv 1 21 1\nf -6 -3 -4 -5\nf -5 -4 -1 -2\n");
            File.WriteAllText(Path.Combine(root, "map.obj"), obj.ToString());
            var scene = ObjModel.Read(Path.Combine(root, "map.obj"), name => name != "Skipped");
            Check(scene.Parts.Select(p => p.Name).SequenceEqual(new[] { "Box", "Sprocket Water Surface", "Bent", "Sharp" }), "one part per group, in order");
            var box = scene.Parts[0];
            Check(box.VertexCount == 24 && box.Submeshes.Count == 1 && box.Submeshes[0].Triangles.Length == 36, "a cube keeps its hard edges: 24 corners, 12 triangles");
            // Mirrored on X: the cube spans x -1..0; its pivot is its bottom centre (-0.5, 0, 0.5), where a tree bends.
            Check(Math.Abs(box.CenterX + .5f) < 1e-5 && box.CenterY == 0 && Math.Abs(box.CenterZ - .5f) < 1e-5, "X is mirrored and the pivot is the bottom centre");
            foreach (var part in scene.Parts)
                for (int t = 0; t < part.Submeshes[0].Triangles.Length; t += 3)
                {
                    var tri = part.Submeshes[0].Triangles;
                    (float X, float Y, float Z) P(int i) => (part.Positions[3 * i], part.Positions[3 * i + 1], part.Positions[3 * i + 2]);
                    var (a, b, c) = (P(tri[t]), P(tri[t + 1]), P(tri[t + 2]));
                    float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    int corner = tri[t];
                    float dot = nx * part.Normals[3 * corner] + ny * part.Normals[3 * corner + 1] + nz * part.Normals[3 * corner + 2];
                    Check(dot > 0, $"{part.Name}: triangles face the way their normals do (Unity's front)");
                }
            // The cube's outward normals: its OBJ +X face (x = 1) is Unity's -X side.
            bool outward = true;
            for (int i = 0; i < box.VertexCount; i++)
            {
                float x = box.Positions[3 * i], y = box.Positions[3 * i + 1] - .5f, z = box.Positions[3 * i + 2]; // from the cube's centre
                float d = x * box.Normals[3 * i] + y * box.Normals[3 * i + 1] + z * box.Normals[3 * i + 2];
                outward &= d > 0;
            }
            Check(outward, "the cube's normals point out of it");
            Check(scene.Parts[2].VertexCount == 6, "a 10-degree bend is smoothed (shared corners)");
            Check(scene.Parts[3].VertexCount == 8, "a 90-degree bend stays sharp");
            Check(scene.Parts[1].Submeshes[0].Material == "glass" && box.Submeshes[0].Material == "brick", "submeshes keep their materials");
            var brick = scene.Materials["brick"];
            Check(brick.R == 1 && Math.Abs(brick.G - .5f) < 1e-6 && brick.Cutout && brick.ScaleU == 2 && brick.ScaleV == 3
                && brick.Texture == Path.GetFullPath(Path.Combine(root, "textures", "brick.png")), "MTL colour, texture, scale and alpha map");
            Check(Math.Abs(scene.Materials["glass"].A - .5f) < 1e-6 && !scene.Materials["glass"].Cutout, "MTL opacity");
            var skipped = ObjModel.Read(Path.Combine(root, "map.obj"), name => name == "Box");
            Check(skipped.Parts.Count == 1 && skipped.Parts[0].Name == "Box", "only the kept groups are built");
            // A file of objects only (Blender's o lines) splits by object.
            File.WriteAllText(Path.Combine(root, "objects.obj"), "o First\nv 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\no Second\nv 0 1 0\nv 1 1 0\nv 0 1 1\nf 4 5 6\n");
            Check(ObjModel.Read(Path.Combine(root, "objects.obj"), _ => true).Parts.Select(p => p.Name).SequenceEqual(new[] { "First", "Second" }), "o splits a file without groups");
            File.WriteAllText(Path.Combine(root, "evil.mtl"), "newmtl evil\nmap_Kd ../../outside.png\n");
            try { ObjModel.ReadMaterials(root); Check(false, "a texture outside the map files is refused"); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    static void Sky()
    {
        // Source's six images by direction (Quake 2's layout): straight up is "up"'s centre, forward (+X) is "rt".
        var up = SourceLighting.SourceSky(0, 0, 1);
        Check(up.Axis == 4 && Math.Abs(up.U - .5f) < 1e-6 && Math.Abs(up.V - .5f) < 1e-6, "straight up is the up image's centre");
        Check(SourceLighting.SourceSky(1, 0, 0).Axis == 0 && SourceLighting.Suffixes[0] == "rt", "Source forward is rt");
        Check(SourceLighting.SourceSky(0, -1, 0).Axis == 3 && SourceLighting.Suffixes[3] == "ft", "Source right is ft");
        var horizon = SourceLighting.SourceSky(1, 0, .999f);
        Check(horizon.V < .01f, "looking up the rt image goes to its top row");
        // Unity's +Y face centre looks straight up; its top row towards Unity -Z (Source left, +Y).
        var (x, y, z) = SourceLighting.UnityCube(2, 0, 0);
        Check(x == 0 && y == 1 && z == 0, "Unity's +Y face looks up");
        var (tx, ty, tz) = SourceLighting.UnityCube(2, 0, -1);
        Check(ty == 1 && tz == -1 && tx == 0, "Unity's +Y face's top row is towards -Z");
        // A white sky's upper half gives pi (the cosine integral over a hemisphere).
        var white = new byte[16 * 16 * 4]; Array.Fill(white, (byte)255);
        var faces = Enumerable.Range(0, 6).Select(_ => ((int, int, byte[])?)(16, 16, white)).ToArray();
        var (cube, upper) = SourceLighting.Cube(faces, 32);
        Check(Math.Abs(upper - Math.PI) < .02 * Math.PI, $"a white sky's upper illuminance is pi ({upper:0.000})");
        Check(cube.Length == 6 && cube.All(f => f.Length == 32 * 32 * 4 && f[0] == 255 && f[3] == 255), "six full faces");
        // Half-Life 2's half-height sides (512 x 256, "scale 1 2"): the image on the face's top half, its bottom row below.
        var half = SourceLighting.SkyImage((1, 2, new byte[] { 10, 10, 10, 255, 20, 20, 20, 255 }), "center 0 0 scale 1 2 rotate 0 translate 0 0");
        Check(half.Height == 4 && half.Rgba[0] == 10 && half.Rgba[4] == 20 && half.Rgba[8] == 20 && half.Rgba[12] == 20, "a half-height sky side fills the top half of its face");
        Check(SourceLighting.SourceSky(1, 0, 0).V == .5f, "the horizon is the face's middle row, where the image ends");
        Check(SourceLighting.SkyImage((1, 2, new byte[8]), "").Height == 2, "a full-height sky side stays as it is");
        var path = Path.GetTempFileName();
        try
        {
            SourceLighting.WriteCube(path, 32, cube);
            var (n, back) = SourceLighting.ReadCube(File.ReadAllBytes(path));
            Check(n == 32 && back[5].SequenceEqual(cube[5]), "the sky file reads back");
        }
        finally { File.Delete(path); }
        // Light entities.
        var orange = SourceLighting.LightValue("255 128 0 300")!.Value;
        Check(orange.Brightness == 300 && orange.Color[0] == 1 && Math.Abs(orange.Color[1] - .2158f) < .001f && orange.Color[2] == 0, "_light is sRGB colour and brightness");
        Check(SourceLighting.LightValue("nonsense") == null, "a broken _light is ignored");
        var down = SourceLighting.Aim(new() { ["angles"] = "0 30 0", ["pitch"] = "-90" });
        Check(Math.Abs(down[1] + 1) < 1e-5, "pitch -90 shines straight down");
        Check(SourceLighting.Aim(new() { ["angles"] = "0 45 0", ["angle"] = "-2" })[1] == -1, "Hammer's angle -2 shines straight down");
        var west = SourceLighting.Aim(new() { ["angles"] = "0 90 0" });
        Check(Math.Abs(west[2] + 1) < 1e-5 && Math.Abs(west[0]) < 1e-5, "yaw 90 (Source +Y) is Unity -Z");
        var position = SourceLighting.Position(100, 200, 300);
        Check(Math.Abs(position[0] + 2.54f) < 1e-4 && Math.Abs(position[1] - 7.62f) < 1e-4 && Math.Abs(position[2] + 5.08f) < 1e-4, "Source inches to Unity metres, mirrored like the map");
        var settings = new SourceLighting.Settings { SunDirection = down, Cubemap = "map.sky", CubemapSize = 32, CubemapUpperLux = 3.1f };
        settings.Lamps.Add(new SourceLighting.Lamp { Position = position, Brightness = 200, Direction = west, Inner = 30, Outer = 60 });
        var read = SourceLighting.Read(SourceLighting.Write(settings))!;
        Check(read.Cubemap == "map.sky" && read.Lamps.Count == 1 && read.Lamps[0].Direction![2] == west[2] && read.SunDirection![1] == down[1], "light and sky settings read back");
        var painted = new SourceLighting.Settings();
        SourceLighting.PaintedSky(painted, new() { ["topcolor"] = "0 0 1", ["bottomcolor"] = "1 1 1" });
        Check(painted.GradientTop![2] == 1 && painted.GradientHorizon![0] == 1 && painted.GradientBottom![0] == .5f, "a painted sky becomes a gradient");
    }

    static void Files()
    {
        var root = Path.Combine(Path.GetTempPath(), "SprocketRuntimeFiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Input", "sub"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Input", "small.obj"), "v 0 0 0");
            File.WriteAllText(Path.Combine(root, "Input", "sub", "big.obj"), new string('x', 1000));
            Check(RuntimeMapFiles.Model(Path.Combine(root, "Input"), null) == Path.Combine("sub", "big.obj"), "the largest OBJ is the map");
            Check(RuntimeMapFiles.Model(Path.Combine(root, "Input"), @"C:\elsewhere\small.obj") == "small.obj", "a chosen OBJ is the map");
            var manifest = new RuntimeMapFiles.Manifest { Name = "import_x_1", Fingerprint = "f", Model = "small.obj" };
            var file = Path.Combine(root, "map.json");
            File.WriteAllText(file, RuntimeMapFiles.Write(manifest));
            Check(RuntimeMapFiles.Ready(file, "f") == null, "an unchecked import isn't ready");
            manifest.Capacity = 8; manifest.StartA = new[] { 1f, 2, 3 }; manifest.StartB = new[] { 4f, 5, 6 };
            File.WriteAllText(file, RuntimeMapFiles.Write(manifest));
            Check(RuntimeMapFiles.Ready(file, "f")?.Capacity == 8, "a checked import is ready");
            Check(RuntimeMapFiles.Ready(file, "changed") == null, "a changed source isn't");
            manifest.Failure = "no starting areas";
            File.WriteAllText(file, RuntimeMapFiles.Write(manifest));
            Check(RuntimeMapFiles.Ready(file, "f") == null && RuntimeMapFiles.Failed(file, "f") == "no starting areas", "a refused source is remembered, not checked again");
            Check(RuntimeMapFiles.Failed(file, "changed") == null, "a changed source is checked again");
            manifest.Failure = "No connected, clear tank formation pair exists at 16, 8, 4, 2 or 1 tanks a side: This map needs two connected, clear team spawn formations at least 100 metres apart.";
            File.WriteAllText(file, RuntimeMapFiles.Write(manifest));
            Check(RuntimeMapFiles.Failed(file, "f") == null, "a map 1.1.0 refused for lacking starting areas is checked again");
            Check(RuntimeMapFiles.Key("a", "f") == RuntimeMapFiles.Key("a", "f") && RuntimeMapFiles.Key("a", "f") != RuntimeMapFiles.Key("a", "g") && RuntimeMapFiles.Key("a", "f").Length == 32, "cache keys");
        }
        finally { Directory.Delete(root, true); }
    }
}
