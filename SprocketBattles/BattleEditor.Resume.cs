using UnityEngine;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    readonly EditorResume suspendedEditor = new();
    (Vector3 At, Quaternion Rotation, float Fov)? suspendedView;
    (BattleFile Battle, int Scene, float Speed, float RequestedAt)? pendingPlayReturn;

    void AllowPlayReturn(BattleFile battle)
    {
        if (pendingPlayReturn is not { } request || !ReferenceEquals(request.Battle, battle)) return;
        pendingPlayReturn = null;
        if (Battle.Mode is { } mode && mode.gameObject.scene.handle == request.Scene && mode.gameObject.scene.isLoaded)
            suspendedEditor.Suspend(mode.GetInstanceID(), request.Scene, request.Speed);
    }

    void ResumeEditor()
    {
        var mode = Battle.Mode;
        if (mode == null || !mode.gameObject.scene.isLoaded || !suspendedEditor.Matches(mode.GetInstanceID(), mode.gameObject.scene.handle))
        { suspendedEditor.Clear(); return; }
        if (Battle.RestartQueued || !Battle.Spawned(mode))
        { Say($"The battle is loading. Press {Plugin.EditorKey} when its tanks are ready."); return; }
        if (commanding) StopCommand();
        var controller = UnityEngine.Object.FindObjectOfType<Sprocket.GameControl.GameController>();
        if (UnityEngine.SceneManagement.SceneManager.GetSceneByName("PauseMenu").isLoaded
            || controller != null && controller.PauseState != Sprocket.PauseState.Unpaused)
        {
            if (suspendedEditor.Waiting) return;
            if (controller == null) { Say($"Resume the pause menu, then press {Plugin.EditorKey} to return to the editor."); return; }
            // Native unpause is asynchronous. Let it finish before creating/hiding cameras and freezing again.
            controller.timescaleOnPause = suspendedEditor.Speed;
            if (controller.PauseState != Sprocket.PauseState.Unpausing) controller.Sprocket_IPauseHandler_RequestUnpause();
            suspendedEditor.Waiting = true;
            return;
        }
        float speed = suspendedEditor.Speed;
        var camera = suspendedView; suspendedView = null;
        suspendedEditor.Clear(); pausing = -1;
        Enter(mode);
        if (editing)
        {
            timeScaleBefore = speed;
            if (view != null && camera is { } saved)
            {
                view.transform.SetPositionAndRotation(saved.At, saved.Rotation); view.fieldOfView = saved.Fov;
                var e = saved.Rotation.eulerAngles; pitch = e.x > 180 ? e.x - 360 : e.x; yaw = e.y;
            }
        }
    }

    void ResumeTick()
    {
        if (pendingPlayReturn is { } request && (Time.unscaledTime - request.RequestedAt > 120
            || !Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Select(UnityEngine.SceneManagement.SceneManager.GetSceneAt).Any(s => s.handle == request.Scene && s.isLoaded))) pendingPlayReturn = null;
        var mode = Battle.Mode;
        if (mode == null || !mode.gameObject.scene.isLoaded || !suspendedEditor.Matches(mode.GetInstanceID(), mode.gameObject.scene.handle))
        { suspendedEditor.Clear(); return; }
        var controller = UnityEngine.Object.FindObjectOfType<Sprocket.GameControl.GameController>();
        if (controller != null && controller.PauseState is Sprocket.PauseState.Paused or Sprocket.PauseState.Pausing)
            controller.timescaleOnPause = suspendedEditor.Speed; // Native Resume must not restore the editor's frozen zero clock.
        if (suspendedEditor.Waiting && !UnityEngine.SceneManagement.SceneManager.GetSceneByName("PauseMenu").isLoaded
            && controller?.PauseState == Sprocket.PauseState.Unpaused)
            ResumeEditor();
    }
}
