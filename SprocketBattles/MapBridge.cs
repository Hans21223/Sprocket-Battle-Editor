namespace SprocketBattles;

/// Optional map integration keeps Battle Editor usable without installing Map Framework.
internal static class MapBridge
{
    static Type? framework;

    internal static string? Call(string method, string? map = null, float value = 0)
    {
        framework ??= AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SprocketMaps.Plugin", false)).FirstOrDefault(t => t != null);
        var action = framework?.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        return action?.Invoke(null, action.GetParameters().Length switch { 0 => null, 1 => new object?[] { map }, _ => new object?[] { map, value } }) as string;
    }
}
