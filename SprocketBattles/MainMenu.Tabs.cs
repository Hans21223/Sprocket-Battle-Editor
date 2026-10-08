using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SprocketBattles;

internal static partial class MainMenu
{
    enum MenuSection { Editor, Playing }
    static MenuSection menuSection = MenuSection.Playing;
    static RectTransform? menuTabs, menuViewport;
    static readonly Il2CppStructArray<Vector3> tabCorners = new(4);
    static readonly List<UnityAction> keepTabs = new();
    static readonly List<(MenuSection Section, Image Image, TMP_Text Label)> tabLabels = new();
    const float TabHeight = 42, TabGap = 6;

    static void SelectSection(MenuSection section)
    {
        if (section == menuSection) return;
        StopTyping();
        menuSection = section;
        choosingMap = false; actionsFor = -1; quickOpen = false; gauntletOpen = false; importOpen = false;
        shownKey = null;
        if (screen != null) { Fill(); RefreshMenuTabs(); if (battleScroll != null) battleScroll.verticalNormalizedPosition = 1; }
        else Build();
    }

    // A fixed toolbar above the list; its position follows the native viewport and canvas scale.
    static void SetUpMenuTabs()
    {
        if (battleScroll == null) return;
        menuViewport = battleScroll.viewport ?? battleScroll.GetComponent<RectTransform>();
        if (menuViewport == null || menuViewport.parent == null) return;
        menuViewport.offsetMax -= new Vector2(0, TabHeight + TabGap);
        var tabs = Node("Battle Editor tabs", menuViewport.parent, 0, 0, menuViewport.rect.width, TabHeight);
        menuTabs = tabs;
        tabs.anchorMin = tabs.anchorMax = new Vector2(0.5f, 0.5f);
        tabs.pivot = new Vector2(0, 1);
        keepTabs.Clear(); tabLabels.Clear();
        Add(MenuSection.Editor, "Editor", 0);
        Add(MenuSection.Playing, "Playing", 0.5f);
        LayoutMenuTabs(); RefreshMenuTabs();

        void Add(MenuSection section, string text, float left)
        {
            var rect = Node("Tab " + text, tabs, 0, 0, 0, 0);
            rect.anchorMin = new Vector2(left, 0); rect.anchorMax = new Vector2(left + 0.5f, 1);
            rect.offsetMin = new Vector2(left == 0 ? 0 : TabGap / 2, 0);
            rect.offsetMax = new Vector2(left == 0 ? -TabGap / 2 : 0, 0);
            var image = rect.gameObject.AddComponent<Image>();
            Click(rect.gameObject, image, () => SelectSection(section));
            keepTabs.Add(keep[^1]); // Fill clears row callbacks, so the fixed tabs keep their own.
            var label = Text(rect, text, 0, 0, 0, 0, 22, Ink, TextAlignmentOptions.Center, bold: true);
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(4, 0); label.rectTransform.offsetMax = new Vector2(-4, 0);
            label.enableWordWrapping = false;
            label.fontSizeMin = 15; label.fontSizeMax = 22; label.enableAutoSizing = true;
            tabLabels.Add((section, image, label));
        }
    }

    static void LayoutMenuTabs()
    {
        if (menuTabs == null || menuViewport == null) return;
        // The interop wrapper copies managed arrays into native memory; read the native array Unity fills.
        menuViewport.GetWorldCorners(tabCorners);
        menuTabs.localRotation = menuViewport.localRotation; menuTabs.localScale = menuViewport.localScale;
        menuTabs.localPosition = menuTabs.parent.InverseTransformPoint(tabCorners[1])
            + menuViewport.localRotation * new Vector3(0, (TabHeight + TabGap) * menuViewport.localScale.y, 0);
        menuTabs.sizeDelta = new Vector2(menuViewport.rect.width, TabHeight);
    }

    static void RefreshMenuTabs()
    {
        foreach (var tab in tabLabels)
        {
            if (tab.Image != null) tab.Image.color = tab.Section == menuSection ? Amber : ButtonColour;
            if (tab.Label != null) tab.Label.color = tab.Section == menuSection ? Dark : Ink;
        }
    }

    static void CloseMenuTabs()
    {
        if (menuTabs != null) { menuTabs.gameObject.SetActive(false); UnityEngine.Object.Destroy(menuTabs.gameObject); }
        menuTabs = null; menuViewport = null; keepTabs.Clear(); tabLabels.Clear();
    }

    static void ChooseTanksAndPlay(BattleFile battle)
    {
        // Older battles have fixed tanks. Offer player selection without rewriting the author's saved battle.
        var played = BattleFile.FromJson(battle.ToJson());
        if (played.Slots.Count == 0)
            foreach (var unit in played.Units.Where(u => u.Team == 0)) unit.Pick = true;
        if (played.PickCapacity == 0) { Tell("This battle has no Team 1 tank positions. Add them in Editor first."); return; }
        Start(played, play: true);
    }

    static void PlayAuthoredTanks(BattleFile battle)
    {
        var played = BattleFile.FromJson(battle.ToJson());
        foreach (var unit in played.Units) unit.Pick = false;
        Start(played, play: true);
    }

}
