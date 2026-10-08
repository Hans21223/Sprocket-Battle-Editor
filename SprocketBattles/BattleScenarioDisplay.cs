using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VehicleDesigner;

namespace SprocketBattles;

/// Adds battle limits to the game's own Scenario tracker while leaving its validity goals intact.
[HarmonyPatch]
internal static class BattleScenarioDisplay
{
    static GoalDisplayer? display;
    static GoalDisplayer? lastNativeDisplay;
    static Il2CppReferenceArray<GoalInfo>? lastNativeGoals;
    static Il2CppReferenceArray<GoalInfo>? nativeGoals;
    static string originalHeader = "", currentLimits = "", currentIssue = "";
    static bool active, ownWrite, originalActive, ready, seenGoals;
    static float nextLookup;

    internal static bool Show(string limits, string issue, bool canPlay)
    {
        bool changed = !active || currentLimits != limits || currentIssue != issue || ready != canPlay;
        active = true; currentLimits = limits; currentIssue = issue; ready = canPlay;
        if (display == null)
        {
            if (Time.unscaledTime < nextLookup) return false;
            nextLookup = Time.unscaledTime + 0.5f;
            foreach (var found in UnityEngine.Object.FindObjectsOfType<GoalDisplayer>(true)
                .Where(found => found != null && found.headerDisplay != null && found.prefab != null && found.layout != null)
                .OrderByDescending(found => string.Equals(found.headerDisplay.text?.Trim(), "Scenario", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(found => found.gameObject.activeInHierarchy))
            {
                display = found;
                originalHeader = found.headerDisplay.text;
                originalActive = found.gameObject.activeSelf;
                nativeGoals = lastNativeDisplay != null && lastNativeDisplay.Pointer == found.Pointer
                    ? lastNativeGoals : CaptureWidgets(found);
                found.gameObject.SetActive(true);
                changed = true;
                break;
            }
        }
        if (display == null) return false;
        if (changed) Refresh();
        return true;
    }

    static Il2CppReferenceArray<GoalInfo> CaptureWidgets(GoalDisplayer found)
    {
        var goals = new List<GoalInfo>();
        if (found.widgets is { } widgets)
            foreach (var widget in widgets)
                if (widget != null && widget.gameObject.activeSelf && !string.IsNullOrEmpty(widget.Message))
                    goals.Add(new GoalInfo { Message = widget.Message, Flags = widget.image != null && widget.complete != null &&
                        widget.image.sprite != null && widget.image.sprite.Pointer == widget.complete.Pointer ? GoalFlags.Complete : GoalFlags.None });
        return new Il2CppReferenceArray<GoalInfo>(goals.ToArray());
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GoalDisplayer), nameof(GoalDisplayer.SetGoals))]
    static void AddBattleGoals(GoalDisplayer __instance, ref Il2CppReferenceArray<GoalInfo> goals)
    {
        if (ownWrite) return;
        if (!seenGoals) { seenGoals = true; Trace.Write($"designer: first Scenario goals ({goals?.Length ?? 0})"); }
        // Plugin initialization can draw validity goals before the battle status first reaches this UI.
        lastNativeDisplay = __instance; lastNativeGoals = goals;
        if (!active || display == null || __instance.Pointer != display.Pointer) return;
        nativeGoals = goals;
        goals = Merge(goals);
        if (__instance.headerDisplay != null) __instance.headerDisplay.text = "Scenario";
    }

    static Il2CppReferenceArray<GoalInfo> Merge(Il2CppReferenceArray<GoalInfo>? originals)
    {
        var rows = new List<GoalInfo>();
        // Native redraw still supplies every operability goal. The battle rows are refreshed only when status changes.
        foreach (string line in currentLimits.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            foreach (string item in line.Split(new[] { "    " }, StringSplitOptions.RemoveEmptyEntries))
                rows.Add(new GoalInfo { Message = item.Trim(), Flags = ready && currentIssue.Length == 0 ? GoalFlags.Complete : GoalFlags.None });
        if (currentIssue.Length > 0) rows.Add(new GoalInfo { Message = currentIssue, Flags = GoalFlags.None });
        if (originals != null) foreach (var goal in originals) if (goal != null) rows.Add(goal);
        return new Il2CppReferenceArray<GoalInfo>(rows.ToArray());
    }

    static void Refresh()
    {
        if (display == null) return;
        ownWrite = true;
        try
        {
            display.headerDisplay.text = "Scenario";
            display.SetGoals(Merge(nativeGoals));
        }
        finally { ownWrite = false; }
    }

    internal static void Hide()
    {
        active = false;
        if (display != null)
        {
            ownWrite = true;
            try
            {
                display.SetGoals(nativeGoals ?? new Il2CppReferenceArray<GoalInfo>(0));
                // SetGoals restores the colour from the latest native validity flags, which may have changed while editing.
                if (display.headerDisplay != null) display.headerDisplay.text = originalHeader;
                display.gameObject.SetActive(originalActive);
            }
            catch (Exception ex) { Trace.Write($"designer: couldn't restore Scenario tracker: {ex.Message}"); }
            finally { ownWrite = false; }
        }
        display = null; nativeGoals = null; nextLookup = 0;
        lastNativeDisplay = null; lastNativeGoals = null;
        currentLimits = ""; currentIssue = ""; ready = false;
    }
}
