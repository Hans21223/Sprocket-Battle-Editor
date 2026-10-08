#nullable enable
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SprocketBattles;

internal static partial class MainMenu
{
    static RectTransform? pickerEditButton;
    static UnityAction? pickerEditClick;
    static GameObject? designerReturn;
    static TMP_Text? designerStatus;
    static Button? designerSave, designerCancel;
    static readonly List<UnityAction> keepDesigner = new();
    static GameObject? battleDesigner;
    static RectTransform? battleDesignerFallback, battleDesignerActions;
    static TMP_Text? battleDesignerName, battleDesignerLimits, battleDesignerIssue, battleDesignerLineup;
    static RectTransform? battleDesignerSlots;
    static Button? battleDesignerPlay, battleDesignerAdd, battleDesignerRemove, battleDesignerPrevious, battleDesignerNext;
    static readonly List<UnityAction> keepBattleDesigner = new();
    static Action? designerPrevious, designerNext, designerAdd, designerRemove, designerPlay;

    static void CreatePickerEditButton()
    {
        if (picker == null) return;
        pickerEditButton = Node("Edit selected tank", picker.transform, 0, 0, 250, 44);
        pickerEditButton.anchorMin = pickerEditButton.anchorMax = pickerEditButton.pivot = Vector2.zero;
        var background = pickerEditButton.gameObject.AddComponent<Image>();
        background.color = ButtonColour;
        var button = pickerEditButton.gameObject.AddComponent<Button>();
        button.targetGraphic = background;
        pickerEditClick = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Guard.Run("Battle Editor vehicle designer", Menu.EditSelectedTank)))!;
        button.onClick.AddListener(pickerEditClick);
        var label = Text(pickerEditButton, "EDIT SELECTED TANK", 0, 0, 250, 44, 20, Ink, TextAlignmentOptions.Center, bold: true);
        label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(4, 0); label.rectTransform.offsetMax = new Vector2(-4, 0);
        label.enableWordWrapping = false; label.fontSizeMin = 13; label.fontSizeMax = 20; label.enableAutoSizing = true;
    }

    internal static void ShowDesignerReturn(Action saveAndReturn, Action cancel, string status, bool ready)
    {
        if (designerReturn == null)
        {
            designerReturn = new GameObject("Battle Editor designer return", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
            var canvasOf = designerReturn.AddComponent<Canvas>();
            canvasOf.renderMode = RenderMode.ScreenSpaceOverlay; canvasOf.sortingOrder = 31000;
            var scaler = designerReturn.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            designerReturn.AddComponent<GraphicRaycaster>();
            var panel = Node("Return to battle setup", designerReturn.transform, 0, 0, 900, 112);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1, 0);
            panel.anchoredPosition = new Vector2(-24, 24);
            panel.gameObject.AddComponent<Image>().color = Back;
            designerStatus = Text(panel, status, 16, 8, 868, 36, 19, Ink, TextAlignmentOptions.Left);
            designerSave = Add("SAVE & RETURN TO BATTLE", 16, saveAndReturn, true);
            designerCancel = Add("RETURN WITHOUT SAVING", 460, cancel, false);

            Button Add(string label, float x, Action click, bool primary)
            {
                var rect = Node(label, panel, x, 54, 424, 42);
                var image = rect.gameObject.AddComponent<Image>(); image.color = primary ? Amber : ButtonColour;
                Click(rect.gameObject, image, click);
                keepDesigner.Add(keep[^1]);
                Text(rect, label, 0, 0, 424, 42, 20, primary ? Dark : Ink, TextAlignmentOptions.Center, bold: true);
                return rect.GetComponent<Button>();
            }
        }
        if (designerStatus != null) designerStatus.text = status;
        if (designerSave != null) designerSave.interactable = ready;
        if (designerCancel != null) designerCancel.interactable = true;
    }

    internal static void HideDesignerReturn()
    {
        if (designerReturn != null) { designerReturn.SetActive(false); UnityEngine.Object.Destroy(designerReturn); }
        designerReturn = null; designerStatus = null; designerSave = null; designerCancel = null; keepDesigner.Clear();
    }

    // The battle stays in the normal vehicle designer. Keep its scenario information in the same area as the game,
    // with no full-screen panel over the tank, part categories or native save/load buttons.
    internal static void ShowBattleDesigner(string name, string limits, string issue, string lineup,
        Action previous, Action next, Action add, Action remove, Action play,
        bool ready, bool canAdd, bool canRemove, bool canSwitch)
    {
        designerPrevious = previous; designerNext = next; designerAdd = add; designerRemove = remove;
        designerPlay = play;
        if (battleDesigner == null)
        {
            keepBattleDesigner.Clear();
            battleDesigner = new GameObject("Battle Editor scenario setup", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
            var canvasOf = battleDesigner.AddComponent<Canvas>();
            canvasOf.renderMode = RenderMode.ScreenSpaceOverlay; canvasOf.sortingOrder = 31000;
            var scaler = battleDesigner.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            battleDesigner.AddComponent<GraphicRaycaster>();
            var group = Node("Scenario", battleDesigner.transform, 0, 0, 900, 246);
            battleDesignerFallback = group;
            group.anchorMin = group.anchorMax = new Vector2(0.16f, 1); group.pivot = new Vector2(0, 1);
            group.anchoredPosition = new Vector2(0, -72);
            var heading = Text(group, "Scenario", 0, 0, 360, 30, 21, new Color(0.55f, 0.85f, 0.36f), TextAlignmentOptions.Left);
            heading.fontStyle = FontStyles.Underline;
            battleDesignerName = Text(group, name, 0, 30, 710, 28, 18, Ink, TextAlignmentOptions.Left);
            battleDesignerLimits = Text(group, limits, 20, 60, 710, 90, 18, Ink, TextAlignmentOptions.TopLeft);
            battleDesignerLimits.fontSizeMin = 14; battleDesignerLimits.fontSizeMax = 18; battleDesignerLimits.enableAutoSizing = true;
            battleDesignerIssue = Text(group, issue, 20, 150, 710, 44, 17, Red, TextAlignmentOptions.TopLeft);
            battleDesignerActions = Node("Battle lineup controls", battleDesigner.transform, 0, 0, 730, 82);
            battleDesignerActions.anchorMin = battleDesignerActions.anchorMax = new Vector2(0.16f, 0);
            battleDesignerActions.pivot = Vector2.zero;
            battleDesignerActions.anchoredPosition = new Vector2(0, 150);
            // Only when the designer's own Play arrow couldn't be taken over. Leaving is the game's Esc → Main menu.
            battleDesignerPlay = Add(battleDesignerActions, "PLAY BATTLE", 0, 46, 210, () => designerPlay?.Invoke(), true);
            battleDesignerSlots = Node("Player lineup", battleDesignerActions, 0, 0, 730, 42);
            battleDesignerPrevious = Add(battleDesignerSlots, "<", 0, 0, 42, () => designerPrevious?.Invoke());
            battleDesignerLineup = Text(battleDesignerSlots, "", 48, 0, 318, 40, 18, Ink, TextAlignmentOptions.Center);
            battleDesignerLineup.enableWordWrapping = false;
            battleDesignerLineup.fontSizeMin = 13; battleDesignerLineup.fontSizeMax = 18; battleDesignerLineup.enableAutoSizing = true;
            battleDesignerNext = Add(battleDesignerSlots, ">", 372, 0, 42, () => designerNext?.Invoke());
            battleDesignerAdd = Add(battleDesignerSlots, "ADD TANK", 428, 0, 138, () => designerAdd?.Invoke());
            battleDesignerRemove = Add(battleDesignerSlots, "REMOVE", 580, 0, 150, () => designerRemove?.Invoke());

            Button Add(Transform parent, string text, float x, float y, float width, Action action, bool primary = false)
            {
                var rect = Node(text, parent, x, y, width, 30);
                rect.gameObject.AddComponent<CanvasGroup>();
                var image = rect.gameObject.AddComponent<Image>(); image.color = primary ? Amber : ButtonColour;
                Click(rect.gameObject, image, action); keepBattleDesigner.Add(keep[^1]);
                var label = Text(rect, text, 4, 0, width - 8, 30, 17, primary ? Dark : Ink, TextAlignmentOptions.Center);
                label.enableWordWrapping = false; label.fontSizeMin = 13; label.fontSizeMax = 17; label.enableAutoSizing = true;
                return rect.GetComponent<Button>();
            }
        }
        if (battleDesignerName != null) battleDesignerName.text = name;
        if (battleDesignerLimits != null) battleDesignerLimits.text = limits;
        if (battleDesignerIssue != null) battleDesignerIssue.text = issue;
        bool nativeScenario = BattleScenarioDisplay.Show(limits, issue, ready);
        if (battleDesignerFallback != null) battleDesignerFallback.gameObject.SetActive(!nativeScenario);
        if (battleDesignerLineup != null) battleDesignerLineup.text = lineup;
        if (battleDesignerSlots != null) battleDesignerSlots.gameObject.SetActive(!string.IsNullOrEmpty(lineup));
        SetButtonState(battleDesignerPlay, ready);
        if (battleDesignerPlay != null) battleDesignerPlay.gameObject.SetActive(!nativeScenario || !NativeDesigner.PlayWrapped);
        SetButtonState(battleDesignerAdd, canAdd);
        SetButtonState(battleDesignerRemove, canRemove);
        SetButtonState(battleDesignerPrevious, canSwitch);
        SetButtonState(battleDesignerNext, canSwitch);
    }

    static void SetButtonState(Button? button, bool enabled)
    {
        if (button == null) return;
        button.interactable = enabled;
        if (button.GetComponent<CanvasGroup>() is { } group) group.alpha = enabled ? 1 : 0.35f;
    }

    internal static void HideBattleDesigner()
    {
        BattleScenarioDisplay.Hide();
        if (battleDesigner != null) { battleDesigner.SetActive(false); UnityEngine.Object.Destroy(battleDesigner); }
        battleDesigner = null; battleDesignerName = null; battleDesignerLimits = null; battleDesignerIssue = null;
        battleDesignerFallback = null; battleDesignerActions = null;
        battleDesignerLineup = null; battleDesignerSlots = null; battleDesignerPlay = null; battleDesignerAdd = null; battleDesignerRemove = null;
        battleDesignerPrevious = null; battleDesignerNext = null;
        designerPrevious = null; designerNext = null; designerAdd = null; designerRemove = null; designerPlay = null;
        keepBattleDesigner.Clear();
    }
}
