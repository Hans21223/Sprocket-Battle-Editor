using System.Numerics;

namespace SprocketBattles;

internal static class CameraEditing
{
    // Changing a camera's position must keep its time, orientation, zoom and followed-tank frame.
    internal static bool Move(CamKey key, Vector3 world, (Vector3 At, float Yaw)? follow)
    {
        if (!Finite(world) || follow is { } invalid && (!Finite(invalid.At) || !float.IsFinite(invalid.Yaw))) return false;
        var local = follow is { } frame ? Vector3.Transform(world - frame.At,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, -frame.Yaw * MathF.PI / 180)) : world;
        if (!Finite(local)) return false;
        key.Position = new[] { local.X, local.Y, local.Z };
        return true;
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && Math.Abs(v.X) < 10000000 && Math.Abs(v.Y) < 10000000 && Math.Abs(v.Z) < 10000000;

    // Store orientation in the followed tank's frame, without changing the camera's position/time/zoom.
    internal static bool Rotate(CamKey key, Quaternion world, float? followYaw)
    {
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z) || !float.IsFinite(world.W)
            || world.LengthSquared() < 1e-8f || !float.IsFinite(world.LengthSquared())
            || followYaw is { } invalid && !float.IsFinite(invalid)) return false;
        world = Quaternion.Normalize(world);
        var local = followYaw is { } yaw ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw * MathF.PI / 180) * world : world;
        local = Quaternion.Normalize(local);
        key.Rotation = new[] { local.X, local.Y, local.Z, local.W };
        return true;
    }
}
