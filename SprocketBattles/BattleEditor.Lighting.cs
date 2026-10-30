namespace SprocketBattles;

public sealed partial class BattleEditor
{
    void TickBattleLighting()
    {
        var mode = Battle.Mode;
        var lighting = Battle.Playing;
        if (editing || mode != null && suspendedEditor.Matches(mode.GetInstanceID(), mode.gameObject.scene.handle))
            lighting = file;
        BattleLighting.Update(mode, lighting, view != null && view.enabled ? view : UnityEngine.Camera.main);
    }

    public void OnDestroy() => BattleLighting.Restore();
}
