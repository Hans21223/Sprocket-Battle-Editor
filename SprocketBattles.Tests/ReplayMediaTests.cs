using SprocketBattles;

internal static class ReplayMediaTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static ReplayData Sample()
    {
        var transform = new float[] { 1, 2, 3, 0, 0, 0, 1, 1, 1, 1 };
        var bytes = new byte[40]; Buffer.BlockCopy(transform, 0, bytes, 0, 40);
        return new ReplayData { VisualVersion = 3, Duration = 10,
            Meshes = new() { new ReplayMesh { Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } } },
            Surfaces = new() { new ReplaySurface { Shader = "Sprocket/Tank paint", Keywords = new[] { "PAINT_ENABLED" },
                Properties = new() { new ReplayShaderProperty { Name = "_PaintTint", Kind = 2, Value = new float[] { .1f, .6f, .2f, 1 } },
                    new ReplayShaderProperty { Name = "_PaintCoords", Kind = 3, Value = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } } } } },
            Actors = new() { new ReplayActor { Unit = "u1", Keys = new() { new ReplayPoseKey() },
                Belts = new() { new ReplayBelt { Mesh = 0, Surface = 0, Count = 1, Frames = new() {
                    new ReplayBeltFrame { Data = ReplayMedia.Pack(bytes) }, new ReplayBeltFrame { Time = 4, Visible = false, Data = ReplayMedia.Pack(bytes) } } } } } },
            SoundClips = new() { new ReplaySoundClip { Name = "engine", Samples = 8000, Channels = 1, Frequency = 8000, Pcm = ReplayMedia.Pack(new byte[16000]) } },
            Sounds = new() { new ReplaySound { Clip = 0, Loop = true, Keys = new() {
                new ReplaySoundKey { Time = 2, Playing = true, Volume = .5f }, new ReplaySoundKey { Time = 5, Playing = false } } } } };
    }
    internal static void Run()
    {
        var hull = System.Numerics.Matrix4x4.CreateRotationY(.7f) * System.Numerics.Matrix4x4.CreateTranslation(100, 5, -30);
        var local = System.Numerics.Matrix4x4.CreateScale(-.4f, .2f, .7f) * System.Numerics.Matrix4x4.CreateRotationX(.3f)
            * System.Numerics.Matrix4x4.CreateTranslation(2, -.5f, -4);
        var world = local * hull;
        var columnMajor = new float[] { world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
            world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44 };
        var instance = new byte[192]; Buffer.BlockCopy(columnMajor, 0, instance, 0, 64);
        System.Numerics.Matrix4x4.Invert(hull, out var inverse);
        var pose = ReplayMedia.BeltPoses(instance, 192, 1, inverse);
        var rebuilt = System.Numerics.Matrix4x4.CreateScale(pose[7], pose[8], pose[9])
            * System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(pose[3], pose[4], pose[5], pose[6]))
            * System.Numerics.Matrix4x4.CreateTranslation(pose[0], pose[1], pose[2]);
        Check(System.Numerics.Vector3.Distance(System.Numerics.Vector3.Transform(new System.Numerics.Vector3(.4f, .6f, .8f), local),
            System.Numerics.Vector3.Transform(new System.Numerics.Vector3(.4f, .6f, .8f), rebuilt)) < .0001f,
            "native belt instance matrices stay aligned with a moved/rotated hull, preserving mirrored segment scale and buffer stride");
        var replay = Sample();
        Check(replay.Check(new[] { "u1" }) == null, "tracks, native paint uniforms and embedded sound validate together");
        var battle = new BattleFile { Map = "Fields", Units = new() { new BattleUnit { Id = "u1", Blueprint = "tank.blueprint" } }, Cinema = new CinemaData { Replay = replay } };
        var saved = BattleFile.FromJson(battle.ToJson()).Cinema!.Replay!;
        Check(saved.Actors[0].Belts[0].Frames[1].Visible == false && saved.Surfaces[0].Shader == "Sprocket/Tank paint"
            && saved.Surfaces[0].Properties[0].Value[1] == .6f && saved.SoundClips[0].Pcm.SequenceEqual(replay.SoundClips[0].Pcm),
            "the recorded belt, tint, paint matrix and sound survive camera-edit saves");
        var decoded = ReplayMedia.Unpack(saved.Actors[0].Belts[0].Frames[0].Data, 40);
        Check(BitConverter.ToSingle(decoded, 0) == 1 && BitConverter.ToSingle(decoded, 24) == 1,
            "compressed segment poses retain hull-relative location and quaternion");
        Check(ReplayMedia.SoundKeyAt(saved.Sounds[0].Keys, 1) == -1 && ReplayMedia.SoundKeyAt(saved.Sounds[0].Keys, 2) == 0
            && ReplayMedia.SoundKeyAt(saved.Sounds[0].Keys, 4.999f) == 0 && ReplayMedia.SoundKeyAt(saved.Sounds[0].Keys, 5) == 1,
            "sound starts only at its recorded time and stops on its stop key when seeking");
        void Invalid(Action<ReplayData> damage, string why)
        { var bad = Sample(); damage(bad); Check(bad.Check(new[] { "u1" }) != null, why); }
        Invalid(r => r.Actors[0].Belts[0].Frames[0].Data = ReplayMedia.Pack(new byte[39]), "truncated belt matrices cannot enter playback");
        Invalid(r => r.Actors[0].Belts[0].Frames[1].Time = 0, "duplicate belt timestamps are rejected");
        Invalid(r => r.Actors[0].Belts[0].Count = 2049, "native segment allocation is bounded");
        Invalid(r => r.Actors[0].Belts[0].Surface = 1, "belt materials must exist");
        Invalid(r => r.Actors[0].Belts[0].Frames[0].Data = ReplayMedia.Pack(new byte[40]), "zero belt quaternions are rejected");
        Invalid(r => r.Surfaces[0].Properties[0].Value[0] = float.NaN, "invalid native shader colour cannot reach material upload");
        Invalid(r => r.Surfaces[0].Properties[1].Value = new float[15], "paint matrices must have sixteen values");
        Invalid(r => r.Surfaces[0].Properties.Add(new ReplayShaderProperty { Name = "_PaintTexture", Kind = 4, Value = new float[] { 1, 1, 0, 0 }, Texture = 0 }),
            "paint textures must exist before native shader upload");
        Invalid(r => r.SoundClips[0].Pcm = ReplayMedia.Pack(new byte[2]), "truncated PCM cannot reach AudioClip.SetData");
        Invalid(r => r.SoundClips[0].Channels = 9, "audio channel count is bounded");
        Invalid(r => r.SoundClips[0].Samples = int.MaxValue, "audio allocations are bounded before decompression");
        Invalid(r => r.Sounds[0].Clip = 1, "sound events must reference recorded clips");
        Invalid(r => r.Sounds[0].Keys[0].Sample = 8000, "sample cursor cannot seek past the clip");
        Invalid(r => r.Sounds[0].Keys[1].Time = 11, "sound keys cannot extend beyond the recording");
        Invalid(r => r.Sounds[0].Keys[0].Pitch = float.NaN, "non-finite sound pitch is rejected");
        replay.SoundClips[0].Pcm = Array.Empty<byte>();
        Check(replay.Check(new[] { "u1" }) == null, "compressed game sounds can use an exact asset identity without falsely embedding silence");
        Console.WriteLine("REPLAY_TRACK_PAINT_AUDIO_TESTS_OK");
    }
}
