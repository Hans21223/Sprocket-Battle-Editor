namespace SprocketMaps;

/// Complete paths can still project their endpoints to a different nearby platform or road.
internal static class CustomMapPathRules
{
    internal readonly record struct Point(float X, float Y, float Z);

    internal static bool Reaches(Point from, Point to, Point first, Point last, int corners, bool complete)
        => complete && corners > 0 && Near(from, first) && Near(to, last);

    static bool Near(Point expected, Point actual)
    {
        if (!Finite(expected) || !Finite(actual)) return false;
        double x = (double)expected.X - actual.X, z = (double)expected.Z - actual.Z;
        return x * x + z * z <= 0.25 && Math.Abs((double)expected.Y - actual.Y) <= 1;
    }

    static bool Finite(Point p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
