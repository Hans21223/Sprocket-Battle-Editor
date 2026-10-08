using System.Numerics;
using SprocketBattles;

internal static class CameraEditingTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    internal static void Run()
    {
        var rotation = new[] { 0f, 0f, 0f, 1f };
        var key = new CamKey { Time = 17, Fov = 43, Rotation = rotation };
        Check(CameraEditing.Move(key, new Vector3(25, 15, -12), null)
            && key.Position.SequenceEqual(new[] { 25f, 15f, -12f }), "dragging an independent camera stores its world position");
        Check(key.Time == 17 && key.Fov == 43 && ReferenceEquals(key.Rotation, rotation),
            "moving a camera cannot retime the shot or overwrite its zoom and orientation");
        var origin = new Vector3(100, 0, 50); var world = new Vector3(110, 5, 50);
        Check(CameraEditing.Move(key, world, (origin, 90)), "follow-camera drag succeeds");
        var stored = new Vector3(key.Position[0], key.Position[1], key.Position[2]);
        Check(Vector3.Distance(stored, new Vector3(0, 5, 10)) < .0001f
            && Vector3.Distance(origin + Vector3.Transform(stored, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)), world) < .0001f,
            "a dragged follow-camera remains at the chosen world position in the tank's rotated frame");
        var before = key.Position.ToArray();
        Check(!CameraEditing.Move(key, new Vector3(float.NaN, 0, 0), null)
            && !CameraEditing.Move(key, world, (origin, float.PositiveInfinity)) && key.Position.SequenceEqual(before),
            "invalid drag positions and follow headings leave the last good camera key unchanged");
        var resume = new EditorResume(); resume.Suspend(8, 31, .5f);
        var position = key.Position; var target = Quaternion.CreateFromYawPitchRoll(.7f, -.4f, .2f);
        Check(CameraEditing.Rotate(key, target, null) && Math.Abs(Quaternion.Dot(target, new Quaternion(key.Rotation[0], key.Rotation[1], key.Rotation[2], key.Rotation[3]))) > .99999f,
            "independent camera stores yaw, pitch and roll");
        Check(CameraEditing.Rotate(key, target, 90), "follow-camera rotation succeeds");
        var localRotation = new Quaternion(key.Rotation[0], key.Rotation[1], key.Rotation[2], key.Rotation[3]);
        Check(Math.Abs(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) * localRotation, target)) > .99999f
            && ReferenceEquals(position, key.Position) && key.Time == 17 && key.Fov == 43,
            "rotation preserves the followed frame, position, time and zoom");
        var lastRotation = key.Rotation;
        Check(!CameraEditing.Rotate(key, new Quaternion(float.NaN, 0, 0, 1), null)
            && !CameraEditing.Rotate(key, new Quaternion(0, 0, 0, 0), null)
            && !CameraEditing.Rotate(key, target, float.PositiveInfinity) && ReferenceEquals(key.Rotation, lastRotation),
            "invalid rotation never overwrites the last good orientation");
        Check(resume.Matches(8, 31) && !resume.Matches(9, 31) && !resume.Matches(8, 32) && resume.Speed == .5f,
            "editor Play return remains limited to the owned battle and scene with its original speed");
        Console.WriteLine("CAMERA_EDITING_TESTS_OK");
    }
}
