using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// On-screen pieces the game's GUI doesn't give mods in a battle: sliders (drawn here, dragged with the mouse read
/// directly) and typing (keys read directly, as the GUI never hears them).
public sealed partial class BattleEditor
{
    // ---------- sliders ----------

    readonly List<(Rect Area, float Min, float Max, Action<float> Set)> sliders = new();
    (Rect Area, float Min, float Max, Action<float> Set)? sliding;

    /// A bar from `min` to `max`, filled to `value`, with `label` on it; click or drag along it to set the value.
    void Slider(Rect area, float value, float min, float max, string label, Action<float> set)
    {
        // Boxes only: this build of the game strips GUI.color and GUI.DrawTexture. The filled part is a second box
        // over the first (darker), with a thin box as the knob.
        GUI.Box(area, "");
        float f = Math.Clamp((value - min) / (max - min), 0, 1);
        float filled = (area.width - 4) * f;
        if (filled > 2) GUI.Box(new Rect(area.x + 2, area.y + 2, filled, area.height - 4), "");
        GUI.Box(new Rect(area.x + 2 + Math.Max(0, filled - 4), area.y + 1, 6, area.height - 2), "");
        GUI.Label(new Rect(area.x + 8, area.y + 1, area.width - 16, area.height), label);
        sliders.Add((area, min, max, set));
    }

    /// The left mouse button on a slider sets it, and keeps setting it while held.
    void Sliding(Mouse mouse)
    {
        var at = mouse.position.ReadValue();
        var p = new Vector2(at.x, Screen.height - at.y);
        if (mouse.leftButton.wasPressedThisFrame)
        {
            sliding = null;
            foreach (var one in sliders) if (Inside(one.Area, p)) { sliding = one; break; }
        }
        if (!mouse.leftButton.isPressed) sliding = null;
        if (sliding is not { } s) return;
        float f = Math.Clamp((p.x - s.Area.x - 2) / Math.Max(1, s.Area.width - 4), 0, 1);
        s.Set(s.Min + f * (s.Max - s.Min));
    }

    // ---------- typing ----------

    string? typing;            // what's being typed into (null: nothing)
    string typed = "", typedBefore = "";
    Action<string>? typedInto;

    void StartTyping(string what, string now, Action<string> into)
    {
        typing = what; typed = typedBefore = now ?? ""; typedInto = into;
        Say("Type, then Enter. Esc puts it back as it was.");
    }

    static readonly (Key Key, char Plain, char Shifted)[] Typeable = BuildTypeable();

    static (Key, char, char)[] BuildTypeable()
    {
        var keys = new List<(Key, char, char)>();
        for (int i = 0; i < 26; i++) keys.Add((Key.A + i, (char)('a' + i), (char)('A' + i)));
        keys.AddRange(new (Key, char, char)[]
        {
            (Key.Digit1, '1', '!'), (Key.Digit2, '2', '@'), (Key.Digit3, '3', '#'), (Key.Digit4, '4', '$'), (Key.Digit5, '5', '%'),
            (Key.Digit6, '6', '^'), (Key.Digit7, '7', '&'), (Key.Digit8, '8', '*'), (Key.Digit9, '9', '('), (Key.Digit0, '0', ')'),
        });
        keys.AddRange(new (Key, char, char)[]
        {
            (Key.Space, ' ', ' '), (Key.Minus, '-', '_'), (Key.Period, '.', '>'), (Key.Comma, ',', '<'), (Key.Slash, '/', '?'),
            (Key.Quote, '\'', '"'), (Key.Semicolon, ';', ':'), (Key.Equals, '=', '+'), (Key.LeftBracket, '[', '{'), (Key.RightBracket, ']', '}'),
        });
        return keys.ToArray();
    }

    /// Keys while typing: letters, digits, space and punctuation added, Backspace takes the last one off, Enter ends it,
    /// Esc puts it back as it was.
    void Type(Keyboard keys)
    {
        if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) { typing = null; Rebuild(); return; }
        if (keys.escapeKey.wasPressedThisFrame) { typedInto?.Invoke(typedBefore); typing = null; Rebuild(); return; }
        if (keys.backspaceKey.wasPressedThisFrame && typed.Length > 0) typed = typed[..^1];
        bool shift = keys.shiftKey.isPressed;
        foreach (var (key, plain, upper) in Typeable)
            if (keys[key].wasPressedThisFrame && typed.Length < 120) typed += shift ? upper : plain;
        typedInto?.Invoke(typed);
    }

    /// A button showing a text that can be typed into: click to type.
    void TextButton(Rect area, string what, string value, string empty, Action<string> set) =>
        Button(area, typing == what ? value + "_" : string.IsNullOrEmpty(value) ? empty : value, () => StartTyping(what, value, set));
}
