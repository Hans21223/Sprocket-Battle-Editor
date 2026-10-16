using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SprocketMaps;

/// A Garry's Mod map's own light and sky, read from its entities and skybox images, in Unity's space and linear colour:
/// the sun (light_environment), its ambient, fog (env_fog_controller), the sky (the skyname's six images as one
/// cubemap, or GMod's painted sky, env_skypaint, as a gradient) and its lamps (light, light_spot). Saved beside a
/// converted map as `<map>.source.json` (+ `<map>.sky` for the cubemap) and applied by MapLighting as the map loads.
internal static class SourceLighting
{
    internal sealed class Lamp
    {
        public float[] Position { get; set; } = new float[3];
        public float[] Color { get; set; } = { 1, 1, 1 };   // linear
        public float Brightness { get; set; }                // Source brightness: this at 100 units (2.54 m)
        public float[]? Direction { get; set; }              // a spotlight's beam, Unity space
        public float Inner { get; set; }                     // full cone angles, degrees
        public float Outer { get; set; }
    }

    internal sealed class Settings
    {
        public int Version { get; set; } = 1;
        public float[]? SunDirection { get; set; }           // where the sunlight goes, Unity space; null: no light_environment
        public float[] SunColor { get; set; } = { 1, 1, 1 };
        public float SunBrightness { get; set; } = 250;
        public float[] AmbientColor { get; set; } = { .5f, .55f, .6f };
        public float AmbientBrightness { get; set; } = 80;
        public float[]? FogColor { get; set; }               // null: no fog
        public float FogStart { get; set; }                  // metres
        public float FogEnd { get; set; }
        public float FogMaxDensity { get; set; } = 1;
        public string? Cubemap { get; set; }                 // the .sky file beside this one
        public int CubemapSize { get; set; }
        public float CubemapUpperLux { get; set; }           // the cubemap's upper-hemisphere illuminance at a multiplier of 1
        public float[]? GradientTop { get; set; }            // a painted sky
        public float[]? GradientHorizon { get; set; }
        public float[]? GradientBottom { get; set; }
        public List<Lamp> Lamps { get; set; } = new();
    }

    internal static string Write(Settings settings) => JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    internal static Settings? Read(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json);
        return settings?.Version == 1 ? settings : null;
    }

    const float Scale = .0254f;
    /// Source (x forward, y left, z up, inches) into Unity: the OBJ's (x, z, -y), mirrored on X by the import.
    internal static float[] Position(float x, float y, float z) => new[] { -x * Scale, z * Scale, -y * Scale };
    internal static float[] Direction(float x, float y, float z) => new[] { -x, z, -y };

    internal static float Linear(float srgb) => srgb <= .04045f ? srgb / 12.92f : MathF.Pow((srgb + .055f) / 1.055f, 2.4f);

    /// "R G B brightness" (0-255 colour), as light entities write their _light and _ambient.
    internal static (float[] Color, float Brightness)? LightValue(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
        if (parts.Length < 3 || parts.Take(3).Any(float.IsNaN)) return null;
        var colour = parts.Take(3).Select(c => Linear(Math.Clamp(c / 255f, 0, 1))).ToArray();
        float brightness = parts.Length > 3 && float.IsFinite(parts[3]) ? parts[3] : 200;
        return (colour, Math.Max(0, brightness));
    }

    /// Where a light entity points (VRAD: the "pitch" key over the angles' pitch, -90 straight down), Unity space.
    internal static float[] Aim(Dictionary<string, string> entity)
    {
        var angles = (entity.GetValueOrDefault("angles") ?? "0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).Concat(new float[3]).Take(3).ToArray();
        float yaw = angles[1], pitch = angles[0];
        if (entity.TryGetValue("pitch", out var given) && float.TryParse(given, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p != 0) pitch = p;
        if (entity.TryGetValue("angle", out var single) && float.TryParse(single, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a != 0)
        {
            if (a is -1 or -2) return Direction(0, 0, a == -1 ? 1 : -1); // Hammer's "up" and "down"
            yaw = a;
        }
        double y = yaw * Math.PI / 180, x = pitch * Math.PI / 180;
        return Direction((float)(Math.Cos(x) * Math.Cos(y)), (float)(Math.Cos(x) * Math.Sin(y)), (float)Math.Sin(x));
    }

    // ------------------------------------------------------------------------------------------------ sky

    /// Source's skybox images by suffix, in the order of its six axes (Quake 2's st_to_vec / skytexorder, kept by Source):
    /// +X "rt", -X "lf", +Y "bk", -Y "ft", +Z "up", -Z "dn".
    internal static readonly string[] Suffixes = { "rt", "lf", "bk", "ft", "up", "dn" };

    /// Which Source sky image a Source-space direction looks at, and where on it (u right, v down from the top row).
    internal static (int Axis, float U, float V) SourceSky(float x, float y, float z)
    {
        float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        int axis; float s, t;
        if (ax >= ay && ax >= az) { axis = x > 0 ? 0 : 1; s = x > 0 ? -y / ax : y / ax; t = z / ax; }
        else if (ay >= az) { axis = y > 0 ? 2 : 3; s = y > 0 ? x / ay : -x / ay; t = z / ay; }
        else { axis = z > 0 ? 4 : 5; s = -y / az; t = z > 0 ? -x / az : x / az; }
        return (axis, (s + 1) / 2, 1 - (t + 1) / 2);
    }

    /// The Unity world direction of a cubemap face texel (faces +X, -X, +Y, -Y, +Z, -Z; sc and tc from -1 to 1 across the
    /// face's stored rows), as Direct3D lays cube faces out.
    internal static (float X, float Y, float Z) UnityCube(int face, float sc, float tc) => face switch
    {
        0 => (1, -tc, -sc),
        1 => (-1, -tc, sc),
        2 => (sc, 1, tc),
        3 => (sc, -1, -tc),
        4 => (sc, -tc, 1),
        _ => (-sc, -tc, -1),
    };

    /// Half-height sky sides (Half-Life 2's stock skies, 512 x 256): `$basetexturetransform "center 0 0 scale 1 2"` shows
    /// the image on the face's top half and its clamped bottom row below that, so the image is padded the same way.
    /// shortcut: assumes `center 0 0`, as those skies use; take the centre into account if a map's sky needs it.
    internal static (int Width, int Height, byte[] Rgba) SkyImage((int Width, int Height, byte[] Rgba) image, string transform)
    {
        var scale = Regex.Match(transform, @"scale\s+\S+\s+([0-9.]+)");
        if (!scale.Success || !float.TryParse(scale.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sy) || sy < 1.01f || sy > 8)
            return image;
        int row = image.Width * 4, height = (int)MathF.Round(image.Height * sy);
        var rgba = new byte[row * height];
        Buffer.BlockCopy(image.Rgba, 0, rgba, 0, image.Rgba.Length);
        for (int y = image.Height; y < height; y++) Buffer.BlockCopy(image.Rgba, image.Rgba.Length - row, rgba, y * row, row);
        return (image.Width, height, rgba);
    }

    /// The six Source sky images (RGBA, top row first; any missing face is black) as Unity cubemap faces of size n,
    /// each n x n RGBA in stored row order, with the cubemap's upper-hemisphere illuminance at radiance scale 1.
    internal static (byte[][] Faces, float UpperLux) Cube(IReadOnlyList<(int Width, int Height, byte[] Rgba)?> source, int n)
    {
        var faces = new byte[6][];
        double upper = 0;
        for (int face = 0; face < 6; face++)
        {
            var pixels = faces[face] = new byte[n * n * 4];
            for (int row = 0; row < n; row++)
                for (int column = 0; column < n; column++)
                {
                    float sc = 2 * (column + .5f) / n - 1, tc = 2 * (row + .5f) / n - 1;
                    var (ux, uy, uz) = UnityCube(face, sc, tc);
                    // Unity (-x, z, -y) back to Source.
                    var (axis, u, v) = SourceSky(-ux, -uz, uy);
                    int at = (row * n + column) * 4;
                    if (source[axis] is { } image)
                    {
                        Sample(image, u, v, pixels, at);
                        if (uy > 0)
                        {
                            float length = MathF.Sqrt(ux * ux + uy * uy + uz * uz);
                            float luminance = .2126f * Linear(pixels[at] / 255f) + .7152f * Linear(pixels[at + 1] / 255f) + .0722f * Linear(pixels[at + 2] / 255f);
                            float solidAngle = 4f / (n * n) / MathF.Pow(1 + sc * sc + tc * tc, 1.5f);
                            upper += luminance * (uy / length) * solidAngle;
                        }
                    }
                    pixels[at + 3] = 255;
                }
        }
        return (faces, (float)upper);
    }

    static void Sample((int Width, int Height, byte[] Rgba) image, float u, float v, byte[] into, int at)
    {
        float x = Math.Clamp(u * image.Width - .5f, 0, image.Width - 1), y = Math.Clamp(v * image.Height - .5f, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        float fx = x - x0, fy = y - y0;
        for (int c = 0; c < 3; c++)
        {
            float top = image.Rgba[(y0 * image.Width + x0) * 4 + c] * (1 - fx) + image.Rgba[(y0 * image.Width + x1) * 4 + c] * fx;
            float bottom = image.Rgba[(y1 * image.Width + x0) * 4 + c] * (1 - fx) + image.Rgba[(y1 * image.Width + x1) * 4 + c] * fx;
            into[at + c] = (byte)Math.Clamp(MathF.Round(top * (1 - fy) + bottom * fy), 0, 255);
        }
    }

    /// The .sky file: its size, then the six faces' RGBA.
    internal static void WriteCube(string path, int n, byte[][] faces)
    {
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes(n));
        foreach (var face in faces) file.Write(face);
    }
    internal static (int Size, byte[][] Faces) ReadCube(byte[] data)
    {
        int n = BitConverter.ToInt32(data, 0);
        if (n is < 1 or > 4096 || data.Length != 4 + 6L * n * n * 4) throw new InvalidDataException("The map's sky file is damaged.");
        var faces = new byte[6][];
        for (int f = 0; f < 6; f++) faces[f] = data.AsSpan(4 + f * n * n * 4, n * n * 4).ToArray();
        return (n, faces);
    }

    /// GMod's painted sky (env_skypaint) as a gradient: its top colour above, the bottom colour at the horizon and below.
    internal static void PaintedSky(Settings settings, Dictionary<string, string>? paint)
    {
        float[] Colour(string key, float[] fallback)
        {
            var parts = (paint?.GetValueOrDefault(key) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
            return parts.Length >= 3 && parts.Take(3).All(float.IsFinite) ? parts.Take(3).Select(c => Linear(Math.Clamp(c, 0, 1))).ToArray() : fallback;
        }
        // GMod's defaults (gm_construct's sky).
        var top = Colour("topcolor", new[] { Linear(.2f), Linear(.5f), Linear(1f) });
        var bottom = Colour("bottomcolor", new[] { Linear(.8f), Linear(1f), Linear(1f) });
        settings.GradientTop = top; settings.GradientHorizon = bottom; settings.GradientBottom = bottom.Select(c => c * .5f).ToArray();
    }
}
