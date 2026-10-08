using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    const int CameraGizmoLayer = 2; // Ignore Raycast; camera picking explicitly includes this layer.
    readonly Dictionary<IntPtr, (int Camera, int Key, GameObject Object)> cameraWidgets = new();
    CameraTrack? movingCamera;
    CamKey? movingCameraKey;
    Vector3 cameraDragStart, cameraDragGrab;
    Vector2 cameraDragMouse;
    Plane cameraDragPlane;
    float cameraDragPixelScale;
    bool cameraDragRaise;
    bool cameraDragRotate;
    Quaternion cameraDragRotation;
    bool cameraPovEnabled = true;
    Camera? cameraPov;
    RenderTexture? cameraPovTexture;
    int? cameraViewMask;

    void ClearCameraTools()
    {
        movingCamera = null; movingCameraKey = null;
        if (view != null && cameraViewMask is { } mask) view.cullingMask = mask;
        cameraViewMask = null;
        if (cameraPov != null) { cameraPov.enabled = false; cameraPov.targetTexture = null; Destroy(cameraPov.gameObject); }
        cameraPov = null;
        if (cameraPovTexture != null) { cameraPovTexture.Release(); Destroy(cameraPovTexture); }
        cameraPovTexture = null;
    }

    void CameraPovTick()
    {
        bool active = editing && tab == Tab.Cinema && !previewing && savedList == null && replayList == null
            && HasCameraPov && view != null;
        if (!active) { if (cameraPov != null) cameraPov.enabled = false; return; }
        if (Cinema.CameraAt(Cam!, scrub, id => EditingAnchor(id, scrub)) is not { } pose) return;
        int previewHeight = Math.Max(1, (int)MathF.Round(384f * Screen.height / Math.Max(1, Screen.width)));
        if (cameraPovTexture != null && cameraPovTexture.height != previewHeight) ClearCameraTools();
        if (cameraPov == null)
        {
            // Native GUI.Label draws an image at its own size. The generated GUI/Graphics.DrawTexture
            // overloads are stripped in this game, so size this target to the actual preview panel.
            cameraPovTexture = new RenderTexture(384, previewHeight,
                24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            if (!cameraPovTexture.Create())
            { ClearCameraTools(); cameraPovEnabled = false; Say("The camera preview could not open."); return; }
            try
            {
                cameraPov = new GameObject("Battle Editor camera POV").AddComponent<Camera>();
                cameraPov.enabled = false;
                cameraPov.CopyFrom(view!);
                cameraPov.targetTexture = cameraPovTexture;
                if (view!.GetComponent<HDAdditionalCameraData>() is { } hd)
                    hd.CopyTo(cameraPov.gameObject.AddComponent<HDAdditionalCameraData>());
                cameraPov.rect = new Rect(0, 0, 1, 1);
                cameraPov.depth = view.depth - 1;
            }
            catch (Exception ex)
            { ClearCameraTools(); cameraPovEnabled = false; Trace.Write("camera view: " + ex); Say("The camera preview could not open."); return; }
        }
        cameraPov.cullingMask = view!.cullingMask & ~(1 << CameraGizmoLayer);
        cameraPov.aspect = (float)Screen.width / Math.Max(1, Screen.height);
        cameraPov.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        cameraPov.fieldOfView = pose.Fov;
        cameraPov.enabled = true;
    }

    bool HasCameraPov => cameraPovEnabled && Cam is { Keys.Count: > 0 } && (selected == null || C.Replay != null);
    float CameraFovValue => Cam is { } track && camKey >= 0 && camKey < track.Keys.Count ? track.Keys[camKey].Fov
        : Cam is { } camera ? Cinema.CameraAt(camera, scrub, id => EditingAnchor(id, scrub))?.Fov ?? view?.fieldOfView ?? 60 : 60;
    float CameraPovHeight => HasCameraPov ? Row + 384f * Screen.height / Math.Max(1, Screen.width) + 2 * Pad : 0;
    float CameraDetailsY => 56 + (HasCameraPov ? CameraPovHeight + 8 : 0);

    void CameraPovPanel()
    {
        if (!HasCameraPov) return;
        var box = Panel(new Rect(Screen.width - 416, 56, 400, CameraPovHeight));
        GUI.Label(new Rect(box.x + Pad, box.y + Pad, 282, Row), $"Camera view: {Short(Cam!.Name, 27)} · {scrub:0.0} s");
        Button(new Rect(box.xMax - 96, box.y + Pad, 88, Row - 3), "View here", () => ViewAt(scrub));
        if (cameraPovTexture != null)
            GUI.Label(new Rect(box.x + Pad, box.y + Pad + Row, 384, box.height - Row - 2 * Pad), cameraPovTexture, GUIStyle.none);
    }

    (int Camera, int Key)? PickCamera(Ray ray)
    {
        foreach (var hit in Physics.RaycastAll(ray, 5000, 1 << CameraGizmoLayer).OrderBy(h => h.distance))
            if (hit.collider != null && cameraWidgets.TryGetValue(hit.collider.gameObject.Pointer, out var widget))
                return (widget.Camera, widget.Key);
        return null;
    }

    void BeginCameraMove(int camera, int index, Mouse mouse)
    {
        if (view == null || camera < 0 || camera >= C.Cameras.Count) return;
        var track = C.Cameras[camera];
        if (index < 0 || index >= track.Keys.Count) return;
        var key = track.Keys[index];
        cam = camera; camKey = index; selected = null; tankKey = -1; scrub = key.Time;
        ReplayAt(scrub, sync: true);
        var pose = Cinema.CameraAt(track, key.Time, id => EditingAnchor(id, key.Time))
            ?? Cinema.World(key, track.Follow, id => EditingAnchor(id, key.Time));
        cameraDragStart = pose.Position; cameraDragMouse = mouse.position.ReadValue();
        cameraDragRaise = Keyboard.current?.shiftKey.isPressed == true;
        cameraDragRotate = Keyboard.current?.ctrlKey.isPressed == true;
        cameraDragRotation = pose.Rotation;
        var ray = view.ScreenPointToRay(cameraDragMouse);
        // A near-horizontal view needs a screen-facing plane instead of a parallel ground plane.
        var normal = Math.Abs(Vector3.Dot(ray.direction, Vector3.up)) > .15f ? Vector3.up : view.transform.forward;
        cameraDragPlane = new Plane(normal, pose.Position);
        movingCamera = null; movingCameraKey = null;
        if (cameraDragRotate) { movingCamera = track; movingCameraKey = key; }
        else if (cameraDragPlane.Raycast(ray, out float distance))
        {
            cameraDragGrab = ray.GetPoint(distance);
            cameraDragPixelScale = 2 * Vector3.Distance(view.transform.position, pose.Position)
                * MathF.Tan(view.fieldOfView * MathF.PI / 360) / Math.Max(1, Screen.height);
            movingCamera = track; movingCameraKey = key;
        }
        Rebuild(); // Highlight the chosen key without moving the editing view inside it.
        Say(cameraDragRotate ? $"{track.Name}: drag to rotate. Manual rotation turns off automatic Look at."
            : $"{track.Name}: drag to move; Shift changes height. Ctrl-drag rotates it.");
    }

    bool CameraMoving(Mouse mouse, bool overPanel)
    {
        if (movingCamera is not { } track || movingCameraKey is not { } key) return false;
        if (!C.Cameras.Contains(track) || !track.Keys.Contains(key) || view == null)
        { movingCamera = null; movingCameraKey = null; return false; }
        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true)
        {
            movingCamera = null; movingCameraKey = null;
            ClearMarks(); CinemaMarks(); Physics.SyncTransforms();
            return true;
        }
        if (!mouse.leftButton.isPressed)
        {
            movingCamera = null; movingCameraKey = null;
            ClearMarks(); CinemaMarks(); Physics.SyncTransforms();
            return true;
        }
        if (overPanel) return true;
        var pointer = mouse.position.ReadValue();
        if (cameraDragRotate)
        {
            var delta = pointer - cameraDragMouse;
            var rotation = Quaternion.AngleAxis(delta.x * .25f, Vector3.up) * cameraDragRotation
                * Quaternion.AngleAxis(-delta.y * .25f, Vector3.right);
            if (RotateCameraKey(track, key, rotation))
                foreach (var widget in cameraWidgets.Values)
                    if (widget.Camera == cam && widget.Key == camKey && widget.Object != null)
                        widget.Object.transform.rotation = rotation;
            Physics.SyncTransforms();
            return true;
        }
        bool raise = Keyboard.current?.shiftKey.isPressed == true;
        if (raise != cameraDragRaise)
        {
            // Switching to height adjustment keeps the horizontal movement already made.
            cameraDragStart = Cinema.World(key, track.Follow, id => EditingAnchor(id, key.Time)).Position;
            cameraDragMouse = pointer; cameraDragRaise = raise;
            var ray = view.ScreenPointToRay(pointer);
            var normal = Math.Abs(Vector3.Dot(ray.direction, Vector3.up)) > .15f ? Vector3.up : view.transform.forward;
            cameraDragPlane = new Plane(normal, cameraDragStart);
            if (cameraDragPlane.Raycast(ray, out float distance)) cameraDragGrab = ray.GetPoint(distance);
            return true;
        }
        Vector3 next;
        if (raise)
            next = cameraDragStart + Vector3.up * (pointer.y - cameraDragMouse.y) * cameraDragPixelScale;
        else
        {
            if (!cameraDragPlane.Raycast(view.ScreenPointToRay(pointer), out float distance)) return true;
            next = cameraDragStart + view.ScreenPointToRay(pointer).GetPoint(distance) - cameraDragGrab;
        }
        if (!MoveCameraKey(track, key, next)) return true;
        foreach (var widget in cameraWidgets.Values)
            if (widget.Camera == cam && widget.Key == camKey && widget.Object != null)
                widget.Object.transform.position = next;
        Physics.SyncTransforms();
        return true;
    }

    bool MoveCameraKey(CameraTrack track, CamKey key, Vector3 world)
    {
        System.Numerics.Vector3 V(Vector3 p) => new(p.x, p.y, p.z);
        (System.Numerics.Vector3 At, float Yaw)? follow = track.Follow != null && EditingAnchor(track.Follow, key.Time) is { } a ? (V(a.At), a.Yaw) : null;
        return CameraEditing.Move(key, V(world), follow);
    }

    CamKey? EnsureCameraKey()
    {
        if (Cam is not { } track || view == null) return null;
        if (camKey >= 0 && camKey < track.Keys.Count) return track.Keys[camKey];
        int existing = track.Keys.FindIndex(k => Math.Abs(k.Time - scrub) < .05f);
        if (existing >= 0) { camKey = existing; return track.Keys[existing]; }
        var pose = Cinema.CameraAt(track, scrub, id => EditingAnchor(id, scrub));
        var key = Cinema.Key(scrub, pose?.Position ?? view.transform.position, pose?.Rotation ?? view.transform.rotation,
            pose?.Fov ?? view.fieldOfView, track, id => EditingAnchor(id, scrub));
        track.Keys.Add(key); track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time)); camKey = track.Keys.IndexOf(key);
        return key;
    }

    void AddCinemaCamera()
    {
        C.Cameras.Add(new CameraTrack { Name = $"Camera {C.Cameras.Count + 1}" });
        cam = C.Cameras.Count - 1; camKey = -1; selected = null;
        AddCamKey();
    }

    void CameraHeight(float amount)
    {
        if (Cam is not { } track || EnsureCameraKey() is not { } key) return;
        var pose = Cinema.World(key, track.Follow, id => EditingAnchor(id, key.Time));
        MoveCameraKey(track, key, pose.Position + Vector3.up * amount);
        ClearMarks(); CinemaMarks(); Physics.SyncTransforms();
    }

    void CameraFov(float amount)
    {
        if (EnsureCameraKey() is { } key) key.Fov = Math.Clamp(key.Fov + amount, 10, 110);
    }

    bool RotateCameraKey(CameraTrack track, CamKey key, Quaternion world)
    {
        float? followYaw = track.Follow != null && EditingAnchor(track.Follow, key.Time) is { } a ? a.Yaw : null;
        if (!CameraEditing.Rotate(key, new System.Numerics.Quaternion(world.x, world.y, world.z, world.w), followYaw)) return false;
        track.LookAt = null; // Automatic aim would otherwise override the manually edited orientation.
        return true;
    }

    void CameraRotate(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        if (Cam is not { } track || EnsureCameraKey() is not { } key) return;
        var pose = Cinema.CameraAt(track, key.Time, id => EditingAnchor(id, key.Time))
            ?? Cinema.World(key, track.Follow, id => EditingAnchor(id, key.Time));
        var rotation = Quaternion.AngleAxis(yawDegrees, Vector3.up) * pose.Rotation
            * Quaternion.Euler(pitchDegrees, 0, rollDegrees);
        if (!RotateCameraKey(track, key, rotation)) return;
        ClearMarks(); CinemaMarks(); Physics.SyncTransforms();
    }

    void CameraUseView()
    {
        if (view == null || Cam is not { } track || EnsureCameraKey() is not { } key) return;
        var replacement = Cinema.Key(key.Time, view.transform.position, view.transform.rotation, view.fieldOfView,
            track, id => EditingAnchor(id, key.Time));
        key.Position = replacement.Position; key.Rotation = replacement.Rotation; key.Fov = replacement.Fov;
        Rebuild();
    }

    void ScrubCameraView(float time)
    {
        if (cameraPovEnabled) ReplayAt(time, sync: true);
        else ViewAt(time);
    }
}
