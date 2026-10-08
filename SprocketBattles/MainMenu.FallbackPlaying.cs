using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SprocketBattles;

internal static partial class MainMenu
{
    static readonly List<UnityAction<Vector2>> keepFallbackScroll = new();
    static ScrollRect? fallbackSettingsScroll;
    static float fallbackScrollPosition = 1;
    static string fallbackSettingsMode = "";
    static bool rebuildingFallback;
    static int fallbackUiGeneration;
    static bool deferFallbackRebuild;
    static int fallbackReleasedAt = -1;

    static bool FallbackRebuildReady()
    {
        if (!deferFallbackRebuild) return true;
        if (UnityEngine.InputSystem.Mouse.current?.leftButton.isPressed == true) { fallbackReleasedAt = -1; return false; }
        if (fallbackReleasedAt < 0) { fallbackReleasedAt = Time.frameCount; return false; }
        return Time.frameCount > fallbackReleasedAt;
    }

    static void ToggleFallbackMode(bool gauntlet)
    {
        StopTyping(); factionPicker = null; choosingMap = false;
        if (gauntlet) { gauntletOpen = !gauntletOpen; quickOpen = false; }
        else { quickOpen = !quickOpen; gauntletOpen = false; }
        Build();
    }

    // Both menus use the same setting rows and launch callbacks; a failed native screen still exposes these modes.
    static void FallbackPlaying(Transform parent)
    {
        string mode = quickOpen ? "quick" : "gauntlet";
        if (fallbackSettingsMode != mode) { fallbackSettingsMode = mode; fallbackScrollPosition = 1; }
        Text(parent, quickOpen ? "QUICK BATTLE" : "GAUNTLET", 0, 0, 1100, 50, 34, Amber, TextAlignmentOptions.Left, bold: true);
        Text(parent, quickOpen ? "Choose a faction for each side. Click a faction to open its list."
            : "Choose the enemy faction. Prepare your own tank in the full vehicle designer.",
            0, 54, 1100, 56, 20, Ink, TextAlignmentOptions.TopLeft);

        var rows = new List<(string Key, string Text, Action Click, Action Hover, bool Dim)>();
        int generation = fallbackUiGeneration;
        AddPlayingRows(rows);
        rows.RemoveAll(r => r.Key == "quick" || r.Key == "gauntlet");
        var rect = Node("Mode settings", parent, 0, 122, 1100, 694);
        var scroll = rect.gameObject.AddComponent<ScrollRect>();
        fallbackSettingsScroll = scroll;
        var viewport = Node("Viewport", rect, 0, 0, 1100, 694);
        viewport.gameObject.AddComponent<Image>().color = Card;
        viewport.gameObject.AddComponent<RectMask2D>();
        float contentHeight = Math.Max(694, rows.Count * 48);
        var content = Node("Settings", viewport, 0, 0, 1080, contentHeight);
        scroll.viewport = viewport; scroll.content = content;
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
        scroll.inertia = true; scroll.verticalNormalizedPosition = fallbackScrollPosition;
        var changed = DelegateSupport.ConvertDelegate<UnityAction<Vector2>>(new Action<Vector2>(_ =>
        { if (!rebuildingFallback && fallbackSettingsScroll == scroll)
            fallbackScrollPosition = content.rect.height > viewport.rect.height + 1 ? scroll.verticalNormalizedPosition : 1; }))!;
        keepFallbackScroll.Add(changed); scroll.onValueChanged.AddListener(changed);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; float y = i * 48;
            if (typingKey == row.Key)
            {
                string label = row.Text.Split(':')[0].Trim();
                Text(content, label, 16, y + 4, 480, 38, 20, Ink, TextAlignmentOptions.Left);
                TextInput(content, typingStart, 510, y + 2, 550, 42, value =>
                {
                    if (rebuildingFallback || generation != fallbackUiGeneration) return;
                    // Deselect runs on pointer-down. Keep the clicked control alive through pointer-up before
                    // repainting settings, so a typed value and the following button press both take effect.
                    deferFallbackRebuild = true; fallbackReleasedAt = -1;
                    CompleteTyping(row.Key, value);
                });
                var field = content.GetChild(content.childCount - 1).GetComponent<TMP_InputField>();
                if (field != null) { field.contentType = TMP_InputField.ContentType.IntegerNumber; field.ActivateInputField(); field.Select(); }
                continue;
            }
            float indent = row.Key.Contains(" choice ", StringComparison.Ordinal) ? 36 : 0;
            var button = Node(row.Key, content, 8 + indent, y + 2, 1052 - indent, 42);
            var background = button.gameObject.AddComponent<Image>(); background.color = row.Dim ? CardPicked : ButtonColour;
            Click(button.gameObject, background, row.Click);
            var text = Text(button, row.Text.TrimStart(), 12, 0, 1028 - indent, 42, 21,
                row.Dim ? Amber : Ink, TextAlignmentOptions.Left);
            text.enableWordWrapping = false; text.fontSizeMin = 14; text.fontSizeMax = 21; text.enableAutoSizing = true;
        }
        Text(parent, "Scroll for more settings and factions.", 0, 822, 1100, 28, 17, Dim, TextAlignmentOptions.Left);
    }
}
