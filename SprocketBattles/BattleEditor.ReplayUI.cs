using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    List<(string Path, ReplaySummary Info)>? replayList;
    int replayTop;
    BattleFile? replayReturn, replayToOpen;
    Tab replayReturnTab;
    bool CanReturnFromReplay => replayReturn != null && string.Equals(replayReturn.Map, map, StringComparison.OrdinalIgnoreCase);
    readonly List<Mesh> replayMeshes = new();
    readonly List<Texture2D> replayTextures = new();
    readonly List<Material> replayMaterials = new();
    readonly Dictionary<(float R, float G, float B, float A), Material> replayLegacyMaterials = new();
    readonly List<(ReplayActor Actor, GameObject Root, List<(ReplayPart Data, GameObject Object)> Parts)> replayVisuals = new();
    ReplayData? visualReplay;
    float replayTime;

    void ListReplays()
    {
        CancelLimitTyping();
        savedList = null;
        Directory.CreateDirectory(ReplayFolder);
        replayList = Directory.EnumerateFiles(ReplayFolder, "*.replay.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).Select(path => (path, ReplaySummary.Read(path))).ToList();
        replayTop = 0;
    }

    void ReplayListPanel()
    {
        var list = replayList!;
        int rows = Math.Clamp((int)((Screen.height - 220) / Row) - 4, 3, 16);
        var box = Panel(new Rect((Screen.width - 680) / 2f, 90, 680, (rows + 4) * Row + 2 * Pad));
        float x = box.x + Pad, w = box.width - 2 * Pad, y = box.y + Pad;
        Rect Line(float left, float width) => new(x + left, y, width, Row - 3);
        GUI.Label(Line(0, w - 90), $"Recorded replays ({list.Count}) — choose Edit to change cameras and cuts");
        Button(Line(w - 85, 85), "Close", () => replayList = null);
        y += Row;
        if (list.Count == 0) GUI.Label(Line(0, w), "No recordings yet. Use Record battle in Cinematic setup.");
        for (int i = replayTop; i < Math.Min(list.Count, replayTop + rows); i++, y += Row)
        {
            var (path, info) = list[i];
            GUI.Label(Line(0, w - 90), $"{Short(info.Name, 34)} · {Short(info.Map, 18)} · {info.Duration:0.0} s");
            Button(Line(w - 85, 85), "Edit", () => OpenReplay(path));
        }
        scrollers.Add((box, by => replayTop = Math.Clamp(replayTop - by, 0, Math.Max(0, list.Count - rows))));
        y = box.y + box.height - Pad - 2 * Row;
        Button(Line(0, 85), "Previous", () => replayTop = Math.Max(0, replayTop - rows));
        Button(Line(90, 85), "Next", () => replayTop = Math.Min(Math.Max(0, list.Count - rows), replayTop + rows));
        GUI.Label(Line(185, w - 185), list.Count == 0 ? "" : $"{replayTop + 1}–{Math.Min(list.Count, replayTop + rows)} of {list.Count}. Wheel scrolls.");
        y += Row;
        GUI.Label(Line(0, w), "Recordings stay unchanged. Save writes a separate editable cinematic battle.");
    }

    void OpenReplay(string path)
    {
        try
        {
            var replay = ReplayRecording.Read(path);
            if (string.Equals(replay.Battle.Map, map, StringComparison.OrdinalIgnoreCase)) LoadReplay(replay);
            else
            {
                // Finish the native return before launching the replay's map. Never invoke a stale menu delegate.
                replayToOpen = replay.Battle;
                if (!ReturnToMainMenu()) { replayToOpen = null; return; }
                replayList = null;
            }
        }
        catch (Exception ex) { Trace.Write($"replay open: {ex}"); Say("Couldn't open replay: " + ex.Message); }
    }

    void ReplayMenuTick()
    {
        if (replayToOpen == null || !Menu.HasCurrentCustomBattleButton || Menu.Busy) return;
        var opening = replayToOpen; replayToOpen = null;
        Menu.Launch(opening.Map, opening, play: false);
    }

    void LoadReplay(ReplayRecording recording)
    {
        if (recording.Error is { } why) { Say(why); return; }
        if (file.Cinema?.Replay == null) { replayReturn = file; replayReturnTab = tab; }
        SetEditorFile(BattleFile.FromJson(recording.Battle.ToJson()), Tab.Cinema); // Original recording is never the edit buffer.
        Say($"Editing replay: {file.Cinema!.Replay!.Duration:0.0} s. Save keeps your camera edits separately.");
    }

    // Menu opening, saved edits and original recordings must all enter the same isolated workspace.
    void SetEditorFile(BattleFile opening, Tab normalTab)
    {
        ClearCameraTools();
        ClearReplayVisuals();
        file = opening;
        savedList = null; replayList = null; selected = null;
        tool = Tool.Tanks; missionTool = MissionTool.None; drag = Drag.None; typing = null; keyPick = KeyPick.None;
        cam = 0; camKey = tankKey = -1; scrub = replayTime = tlStart = 0;
        tlDrag = null; tlDone = null; tlHits.Clear();
        tab = file.Cinema?.Replay != null ? Tab.Cinema : normalTab;
        tlSpan = Math.Clamp(file.Cinema?.Replay?.Duration ?? file.Cinema?.Length ?? 30, 30, 600);
        if (file.Cinema?.Replay != null) file.Cinema.PlayOnStart = false;
        Rebuild(); ToTopView();
        if (file.Cinema?.Replay != null)
        {
            if (file.Cinema.Cameras.All(c => c.Keys.Count == 0) && view != null) AddCamKey();
            ViewAt(0);
        }
    }

    void BackFromReplay()
    {
        if (!CanReturnFromReplay) return;
        var returning = replayReturn!; replayReturn = null;
        SetEditorFile(returning, replayReturnTab);
        Say("Back in the battle editor.");
    }

    void ClearReplayVisuals()
    {
        StopReplayAudio();
        replayBelts.Clear();
        replayVisuals.Clear(); visualReplay = null;
        foreach (var mesh in replayMeshes) if (mesh != null) Destroy(mesh);
        replayMeshes.Clear();
        foreach (var material in replayMaterials) if (material != null) Destroy(material);
        replayMaterials.Clear();
        foreach (var material in replayLegacyMaterials.Values) if (material != null) Destroy(material);
        replayLegacyMaterials.Clear();
        foreach (var texture in replayTextures) if (texture != null) Destroy(texture);
        replayTextures.Clear();
        foreach (var texture in replayLinearTextures.Values) if (texture != null) Destroy(texture);
        replayLinearTextures.Clear();
    }

    bool ReplayMarkers()
    {
        replayVisuals.Clear();
        replayBelts.Clear();
        if (file.Cinema?.Replay is not { } replay) { ClearReplayVisuals(); return false; }
        if (!ReferenceEquals(visualReplay, replay))
        {
            ClearReplayVisuals(); visualReplay = replay;
            foreach (var data in replay.Meshes)
            {
                var vertices = new Vector3[data.Vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(data.Vertices[i * 3], data.Vertices[i * 3 + 1], data.Vertices[i * 3 + 2]);
                var mesh = new Mesh { name = "Battle replay mesh", indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                replayMeshes.Add(mesh);
                mesh.vertices = vertices;
                if (data.Submeshes.Count > 0)
                {
                    mesh.subMeshCount = data.Submeshes.Count;
                    for (int s = 0; s < data.Submeshes.Count; s++) mesh.SetTriangles(data.Submeshes[s], s);
                }
                else mesh.triangles = data.Triangles;
                if (data.Normals.Length == data.Vertices.Length)
                    mesh.normals = Enumerable.Range(0, vertices.Length).Select(i => new Vector3(data.Normals[3*i], data.Normals[3*i+1], data.Normals[3*i+2])).ToArray();
                else mesh.RecalculateNormals();
                if (data.Uv.Length == vertices.Length * 2)
                {
                    mesh.uv = Enumerable.Range(0, vertices.Length).Select(i => new Vector2(data.Uv[2*i], data.Uv[2*i+1])).ToArray();
                    mesh.RecalculateTangents();
                }
                mesh.RecalculateBounds();
            }
            foreach (var png in replay.Textures)
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                replayTextures.Add(texture);
                if (!ImageConversion.LoadImage(texture, png, true)) throw new InvalidDataException("A replay paint texture could not load.");
            }
            foreach (var surface in replay.Surfaces) replayMaterials.Add(ReplayMaterial(surface));
        }
        foreach (var actor in replay.Actors)
        {
            var unit = file.Units.FirstOrDefault(u => u.Id == actor.Unit);
            if (unit == null) continue;
            var root = new GameObject("Replay tank " + actor.Unit);
            markers.Add((unit, root));
            var parts = new List<(ReplayPart Data, GameObject Object)>();
            foreach (var part in actor.Parts)
            {
                var o = new GameObject("Replay visual part"); o.transform.SetParent(root.transform, false);
                o.AddComponent<MeshFilter>().sharedMesh = replayMeshes[part.Mesh];
                var renderer = o.AddComponent<MeshRenderer>();
                if (part.Surfaces.Count > 0) renderer.sharedMaterials = part.Surfaces.Select(i => replayMaterials[i]).ToArray();
                else
                {
                    var key = (part.Colour[0], part.Colour[1], part.Colour[2], part.Colour[3]);
                    if (!replayLegacyMaterials.TryGetValue(key, out var material))
                        replayLegacyMaterials[key] = material = ReplayMaterial(new ReplaySurface { Colour = part.Colour });
                    renderer.sharedMaterial = material;
                }
                var bounds = replayMeshes[part.Mesh].bounds;
                var box = o.AddComponent<BoxCollider>(); box.center = bounds.center;
                box.size = new Vector3(Math.Max(.05f, bounds.size.x), Math.Max(.05f, bounds.size.y), Math.Max(.05f, bounds.size.z));
                pickable[o.Pointer] = new Hit(unit, Part.Body, 0); parts.Add((part, o));
            }
            if (actor.Parts.Count == 0)
                Block(root, new Vector3(0, 1, 0), new Vector3(3, 2, 6), TeamColours[unit.Team % 2], new Hit(unit, Part.Body, 0));
            replayVisuals.Add((actor, root, parts));
            ReplayBeltMarkers(actor, root);
        }
        ReplayAt(scrub);
        return true;
    }

    Material ReplayMaterial(ReplaySurface surface)
    {
        var shader = (!string.IsNullOrEmpty(surface.Shader) ? Shader.Find(surface.Shader) : null)
            ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) throw new InvalidOperationException("The tank lighting shader is unavailable.");
        var material = new Material(shader) { name = "Battle replay tank paint", enableInstancing = true };
        var colour = new Color(surface.Colour[0], surface.Colour[1], surface.Colour[2], surface.Colour[3]);
        if (material.HasColor("_BaseColor")) material.SetColor("_BaseColor", colour);
        else if (material.HasColor("_Color")) material.SetColor("_Color", colour);
        if (material.HasFloat("_Metallic")) material.SetFloat("_Metallic", surface.Metallic);
        if (material.HasFloat("_Smoothness")) material.SetFloat("_Smoothness", surface.Smoothness);
        if (material.HasFloat("_DoubleSidedEnable")) material.SetFloat("_DoubleSidedEnable", 1);
        if (surface.Texture >= 0)
        {
            string property = material.HasTexture("_BaseColorMap") ? "_BaseColorMap"
                : material.HasTexture("_BaseMap") ? "_BaseMap" : "_MainTex";
            material.SetTexture(property, replayTextures[surface.Texture]);
            material.SetTextureScale(property, new Vector2(surface.Tiling[0], surface.Tiling[1]));
            material.SetTextureOffset(property, new Vector2(surface.Offset[0], surface.Offset[1]));
        }
        if (surface.Shader == shader.name) ApplyReplayShader(material, surface);
        if (shader.name == "HDRP/Lit") HDMaterial.ValidateMaterial(material);
        return material;
    }

    void ReplayAt(float time, bool sync = false)
    {
        replayTime = time;
        foreach (var (actor, root, parts) in replayVisuals)
        {
            ApplyPose(root, ReplayPoses.At(actor.Keys, time), local: false);
            foreach (var (data, o) in parts) ApplyPose(o, ReplayPoses.At(data.Keys, time), local: true);
        }
        ReplayBeltsAt(time);
        if (sync && replayVisuals.Count > 0) Physics.SyncTransforms(); // Scrubbing keeps frozen-scene picking at the displayed pose.
    }
    static void ApplyPose(GameObject o, ReplayPoses.Pose pose, bool local)
    {
        o.SetActive(pose.Visible);
        var at = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        if (local) { o.transform.localPosition = at; o.transform.localRotation = rotation; }
        else o.transform.SetPositionAndRotation(at, rotation);
        o.transform.localScale = new Vector3(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
    }
    (Vector3 At, float Yaw)? ReplayAnchor(string id, float time)
    {
        if (C.Replay?.Actors.FirstOrDefault(a => a.Unit == id) is not { } actor) return null;
        var pose = ReplayPoses.At(actor.Keys, time);
        return (new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
            new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W).eulerAngles.y);
    }

    void ReplayInfoPanel()
    {
        var box = Panel(new Rect(Screen.width - 416, CameraDetailsY, 400, 4 * Row + 2 * Pad));
        float x = box.x + Pad, y = box.y + Pad;
        GUI.Label(new Rect(x, y, 384, Row), $"Replay: {C.Replay!.Actors.Count} tanks, {C.Replay.Duration:0.0} s{(C.Replay.Simplified ? " (simplified geometry)" : "")}"); y += Row;
        GUI.Label(new Rect(x, y, 384, Row * 2), "Tank movement and part poses are recorded.\nEdit camera keys, follow targets and cuts."); y += 2 * Row;
        GUI.Label(new Rect(x, y, 384, Row), C.Replay.VisualVersion < 3
            ? "Older replay: record again for complete painted tanks."
            : C.Replay.AudioIncomplete ? "Some sounds could not be captured. See the replay log."
            : $"Tracks, paint and {C.Replay.Sounds.Count} recorded sounds."); y += Row;
    }
}
