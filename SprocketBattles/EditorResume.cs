namespace SprocketBattles;

/// Return only to an editor that owned this native battle scene, after Esc, F9 or editor Play.
internal sealed class EditorResume
{
    int owner, scene;
    internal bool Waiting { get; set; }
    internal float Speed { get; private set; } = 1;
    internal void Suspend(int owner, int scene, float speed)
    {
        this.owner = owner; this.scene = scene;
        Speed = float.IsFinite(speed) && speed > 0 ? speed : 1;
        Waiting = false;
    }
    internal bool Matches(int owner, int scene) => this.owner != 0 && this.owner == owner && this.scene == scene;
    internal void Clear() { owner = 0; scene = 0; Waiting = false; }
}
