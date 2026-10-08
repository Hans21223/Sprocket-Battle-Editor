namespace SprocketMaps;

/// Compact, centered formation shared by authored custom maps and native battle setup.
internal static class SpawnFormation
{
    internal static int ColumnCount(int count)
    {
        if (count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(count));
        return (int)Math.Ceiling(Math.Sqrt(count));
    }
    internal static int RowCount(int count) => (count + ColumnCount(count) - 1) / ColumnCount(count);
    internal static float FrontDistance(int count, float spacing) => (RowCount(count) - 1) * spacing / 2;
    internal static (float Side, float Back) Offset(int count, int index, float spacing)
    {
        int columns = ColumnCount(count);
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        if (!float.IsFinite(spacing) || spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        return ((index % columns - (columns - 1) / 2f) * spacing, index / columns * spacing);
    }
}
