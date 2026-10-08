using System.Numerics;
using SprocketBattles;

internal static class ReplayTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static ReplayPoseKey Key(float at, float x = 0, bool visible = true) => new() { Time = at, Position = new[] { x, 2f, 3f }, Visible = visible };
    static ReplayRecording Recording() => new()
    {
        Battle = new BattleFile { Name = "Movement recording", Map = "Fields",
            Units = new() { new BattleUnit { Id = "u1", Blueprint = "tank.blueprint", Position = new[] { 0f, 2f, 3f } } },
            Cinema = new CinemaData { PlayOnStart = false, Replay = new ReplayData { Duration = 10,
                Meshes = new() { new ReplayMesh { Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, Triangles = new[] { 0, 1, 2 } } },
                Actors = new() { new ReplayActor { Unit = "u1", Keys = new() { Key(0), Key(10, 20) },
                    Parts = new() { new ReplayPart { Mesh = 0, Keys = new() { Key(0) } } } } } } } }
    };

    internal static void Run()
    {
        var recording = Recording(); var replay = recording.Battle.Cinema!.Replay!;
        Check(recording.Error == null && recording.Battle.Cinema.Length == 10, "recorded duration works without camera keys");
        var middle = ReplayPoses.At(replay.Actors[0].Keys, 5);
        Check(middle.Position == new Vector3(10, 2, 3) && middle.Rotation == Quaternion.Identity, "recorded movement interpolates between hull samples");
        Check(ReplayPoses.At(replay.Actors[0].Keys, -5).Position.X == 0 && ReplayPoses.At(replay.Actors[0].Keys, 100).Position.X == 20,
            "scrubbing outside the recording holds endpoint poses");
        replay.Actors[0].Keys[1].Rotation = new[] { 0f, 1f, 0f, 0f };
        middle = ReplayPoses.At(replay.Actors[0].Keys, 5);
        Check(Math.Abs(middle.Rotation.Length() - 1) < 0.0001 && Math.Abs(middle.Rotation.W - Math.Sqrt(.5)) < 0.0001,
            "tank orientation uses normalized quaternion interpolation");
        var vanished = new[] { Key(0), Key(2, 4, false), Key(4, 8) };
        Check(ReplayPoses.At(vanished, 1).Visible && ReplayPoses.At(vanished, 1).Position.X == 0
            && !ReplayPoses.At(vanished, 2).Visible && !ReplayPoses.At(vanished, 3).Visible && ReplayPoses.At(vanished, 4).Visible,
            "removed and respawned actors change visibility at their recorded times without travelling through missing poses");
        var late = new[] { Key(0, 0, false), Key(3, 10), Key(5, 12) };
        Check(!ReplayPoses.At(late, 2).Visible && ReplayPoses.At(late, 3).Position.X == 10,
            "a reserve remains absent until its recorded spawn time");

        void Invalid(Action<ReplayData> breakData, string reason)
        {
            var bad = Recording().Battle.Cinema!.Replay!; breakData(bad);
            Check(bad.Check(new[] { "u1" }) != null, reason);
        }
        Invalid(r => r.Actors.Add(r.Actors[0]), "duplicate actor identities are rejected");
        Invalid(r => r.Actors[0].Unit = "missing", "unknown tank references are rejected");
        Invalid(r => r.Actors[0].Keys[1].Time = 0, "duplicate sample times are rejected");
        Invalid(r => r.Actors[0].Keys[0].Time = 1, "every actor has a defined pose at recording start");
        Invalid(r => r.Actors[0].Keys[1].Time = 11, "samples cannot extend beyond recorded duration");
        Invalid(r => r.Actors[0].Keys[0].Position[0] = float.NaN, "invalid physics poses cannot enter a replay");
        Invalid(r => r.Actors[0].Keys[0].Rotation = new float[4], "zero quaternions are rejected");
        Invalid(r => r.Actors[0].Parts[0].Mesh = 1, "parts must reference a captured mesh");
        Invalid(r => r.Meshes[0].Triangles[2] = 9, "mesh indices cannot address outside vertex storage");
        Invalid(r => r.Duration = ReplayData.MaxSeconds + 1, "recording duration is bounded");
        Invalid(r => r.VisualVersion = 4, "unknown visual formats are rejected before playback");
        Invalid(r => r.Meshes[0].Normals = new[] { 1f }, "truncated normals cannot enter native mesh upload");
        Invalid(r => r.Meshes[0].Uv = new[] { float.NaN, 0, 0, 0, 0, 0 }, "non-finite UVs are rejected");
        Invalid(r => r.Meshes[0].Submeshes.Add(new[] { 0, 1, 7 }), "submesh indices must reference stored vertices");
        Invalid(r => r.Actors[0].Parts[0].Surfaces.Add(0), "missing materials are rejected");
        Invalid(r => r.Surfaces.Add(new ReplaySurface { Texture = 0 }), "missing paint textures are rejected");
        Invalid(r => r.Textures.Add(new byte[33]), "a malformed texture cannot reach the native image decoder");
        Invalid(r => r.Surfaces.Add(new ReplaySurface { Smoothness = float.PositiveInfinity }), "non-finite material values are rejected");
        Invalid(r => r.Actors[0].Keys = null!, "explicit null tracks are rejected");
        Invalid(r => r.Actors[0].Keys[0].Scale = new[] { 1f }, "malformed scales are rejected");
        Invalid(r => r.Actors[0].Keys = Enumerable.Range(0, ReplayData.MaxKeys + 1)
            .Select(i => Key(i * .0001f)).ToList(), "total pose storage is bounded before native replay visuals are created");

        var painted = Recording(); var paint = painted.Battle.Cinema!.Replay!;
        paint.VisualVersion = 2;
        paint.Meshes[0].Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
        paint.Meshes[0].Uv = new[] { 0f, 0f, 1f, 0f, 0f, 1f };
        paint.Meshes[0].Submeshes.Add(new[] { 0, 1, 2 });
        // Valid small image, separate from the mesh, survives self-contained replay save/load.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aSf0AAAAASUVORK5CYII=");
        paint.Textures.Add(png);
        paint.Surfaces.Add(new ReplaySurface { Texture = 0, Tiling = new[] { 2f, 3f }, Offset = new[] { .25f, .5f }, Metallic = .2f });
        paint.Actors[0].Parts[0].Surfaces.Add(0);
        Check(painted.Error == null, "a painted GPU mesh with normals, UVs, submeshes and materials validates");
        var restoredPaint = BattleFile.FromJson(painted.Battle.ToJson()).Cinema!.Replay!;
        Check(restoredPaint.Textures[0].SequenceEqual(png) && restoredPaint.Meshes[0].Uv.SequenceEqual(paint.Meshes[0].Uv)
            && restoredPaint.Meshes[0].Normals.SequenceEqual(paint.Meshes[0].Normals)
            && restoredPaint.Actors[0].Parts[0].Surfaces[0] == 0 && restoredPaint.Surfaces[0].Offset[0] == .25f,
            "paint textures and their original UV mapping survive cinematic editing and serialization");
        paint.Meshes[0].Submeshes.Add(Array.Empty<int>());
        Check(painted.Error != null, "material assignments must cover every recorded submesh");
        paint.Meshes[0].Submeshes.RemoveAt(1);
        string identity = ReplayGeometry.Identity(paint.Meshes[0]);
        Check(identity == ReplayGeometry.Identity(restoredPaint.Meshes[0]), "identical tank models share geometry across actors");
        restoredPaint.Meshes[0].Uv[0] = .5f;
        Check(identity != ReplayGeometry.Identity(restoredPaint.Meshes[0]), "different texture coordinates cannot share replay geometry");
        Check(ReplayGeometry.ActorAllowance(6) * 6 <= ReplayData.MaxVertices && ReplayGeometry.ActorAllowance(32) >= 30000,
            "geometry has a reserved allowance for all actors, including the last tank");
        byte[] tooWide = png.ToArray(); tooWide[16] = 0; tooWide[17] = 0; tooWide[18] = 16; tooWide[19] = 0;
        Check(!ReplayGeometry.Png(tooWide), "texture dimensions are checked before native allocation");
        Check(ReplayGeometry.Attribute(BitConverter.GetBytes((ushort)0x3C00), 0, 1) == 1
            && ReplayGeometry.Attribute(new byte[] { 255 }, 0, 2) == 1
            && ReplayGeometry.Attribute(new byte[] { 128 }, 0, 3) == -1
            && ReplayGeometry.Attribute(BitConverter.GetBytes((ushort)65535), 0, 4) == 1,
            "compressed GPU vertex attributes decode half floats and normalized signed and unsigned values");

        string dir = Path.Combine(Path.GetTempPath(), "sb-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            string original = Path.Combine(dir, "original.replay.json");
            SavedFiles.Write(original, recording.ToJson(), overwrite: false);
            ReplaySummary.Write(original, recording);
            Check(ReplaySummary.Read(original).Name == recording.Battle.Name && ReplaySummary.Read(original).Map == "Fields"
                && ReplaySummary.Read(original).Duration == 10, "the replay list can show name, map and duration without loading all geometry");
            Check(ReplaySummary.Read(Path.Combine(dir, "unknown.replay.json")).Map == "unknown map",
                "missing list metadata does not prevent opening a valid recording");
            byte[] before = File.ReadAllBytes(original);
            var loaded = ReplayRecording.Read(original);
            Check(loaded.RecordedAtUtc == recording.RecordedAtUtc && loaded.Battle.Cinema!.Replay!.Actors[0].Parts[0].Keys.Count == 1,
                "saved replay retains recording identity, tank tracks and static local parts");
            var edited = BattleFile.FromJson(loaded.Battle.ToJson());
            edited.Cinema!.Cameras.Add(new CameraTrack { Name = "Edited camera", Keys = new() { new CamKey { Time = 5 } } });
            SavedFiles.Write(Path.Combine(dir, "edited.json"), edited.ToJson());
            Check(File.ReadAllBytes(original).SequenceEqual(before) && loaded.Battle.Cinema!.Cameras.Count == 0,
                "editing a replay writes a separate battle and preserves the original recording and buffer");
            Check(BattleFile.FromJson(File.ReadAllText(Path.Combine(dir, "edited.json"))).Cinema!.Replay!.Duration == 10,
                "cinematic save retains recorded movement alongside edited cameras");
            File.WriteAllText(Path.Combine(dir, "broken.replay.json"), "{\"Version\":2}");
            bool rejected = false; try { ReplayRecording.Read(Path.Combine(dir, "broken.replay.json")); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "unsupported replay versions are rejected before changing the editor");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        var resume = new EditorResume();
        Check(!resume.Matches(7, 3), "F9 cannot enter ordinary gameplay without an Esc suspension");
        resume.Suspend(7, 3, .25f);
        Check(resume.Matches(7, 3) && resume.Speed == .25f && !resume.Matches(8, 3) && !resume.Matches(7, 4),
            "paused editor resumes only its original battle and scene, retaining slow-motion speed");
        resume.Waiting = true; resume.Clear();
        Check(!resume.Waiting && !resume.Matches(7, 3), "scene exit or explicit Leave revokes a pending resume");
        resume.Suspend(9, 4, 0);
        Check(resume.Speed == 1, "a native pause's zero clock cannot freeze gameplay after leaving the resumed editor");
        Console.WriteLine("REPLAY_AND_EDITOR_RESUME_TESTS_OK");
    }
}
