using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// Player selection belongs to the battle's mission, not to the saved-battle menu or an individual tank's panel.
public sealed partial class BattleEditor
{
    bool playerLimitsOpen;
    int playerLimitsTop;
    string? limitField;
    Action<PickLimits, int>? limitSet;
    bool replaceLimitText;

    /// The fold occupies the Mission tab's left column. Tools and zones return when it is closed; rules stay visible.
    bool MissionLimitsPanel()
    {
        float height = playerLimitsOpen ? Math.Min(18 * Row + 2 * Pad, Screen.height - 72) : Row + 2 * Pad;
        var box = Panel(new Rect(16, 56, 480, height));
        float x = box.x + Pad, width = box.width - 2 * Pad;
        Button(new Rect(x, box.y + Pad, width, Row - 3),
            (playerLimitsOpen ? "v " : "> ") + "Player tank limits", () =>
            {
                if (playerLimitsOpen && !TryCommitPendingLimit()) return;
                playerLimitsOpen = !playerLimitsOpen;
                if (!playerLimitsOpen) CancelLimitTyping();
            });
        if (!playerLimitsOpen) return false;

        var limits = file.Limits ?? new PickLimits();
        var rows = new List<Action<Rect>>();
        void Label(string text) => rows.Add(area => GUI.Label(area, text));
        void Number(string key, string label, int amount, Action<PickLimits, int> set)
        {
            rows.Add(area =>
            {
                GUI.Label(new Rect(area.x, area.y, 190, area.height), label);
                string text = typing == key ? typed + "_" : amount == 0 ? "0  (no extra limit)" : amount.ToString("N0");
                Button(new Rect(area.x + 194, area.y, area.width - 194, area.height), text, () =>
                {
                    if (!TryCommitPendingLimit()) return;
                    StartTyping(key, amount.ToString(CultureInfo.InvariantCulture), _ => { });
                    limitField = key; limitSet = set; replaceLimitText = true;
                    Say("Type a whole number. Enter applies; Esc cancels. 0 means no extra limit.");
                });
            });
        }
        Number("player.maximum", "Maximum tanks", limits.MaxTanks, (l, n) => l.MaxTanks = n);
        Number("player.budget", "Total tank budget", limits.Budget, (l, n) => l.Budget = n);
        Number("player.cost", "Maximum cost per tank", limits.MaxCost, (l, n) => l.MaxCost = n);
        Label($"Player positions: {file.Slots.Count}; at most {file.PickCapacity} chosen tanks.");
        rows.Add(area => Button(area, "Add player position", AddPlayerPosition));
        bool all = file.Units.Any(u => u.Team == 0) && file.Units.Where(u => u.Team == 0).All(u => u.Pick);
        rows.Add(area => Toggle(area, all, " Players choose all Team 1 tanks", () =>
        {
            if (!file.Units.Any(u => u.Team == 0)) { Say("Place a Team 1 tank in the Tanks tab first."); return; }
            foreach (var unit in file.Units.Where(u => u.Team == 0)) unit.Pick = !all;
            Rebuild();
        }));
        Label("Individual player positions: select a tank in the Tanks tab.");
        rows.Add(area =>
        {
            GUI.Label(new Rect(area.x, area.y, area.width - 120, area.height), "Allowed eras");
            Button(new Rect(area.xMax - 116, area.y, 116, area.height), limits.Eras.Count == 0 ? "Any era [x]" : "Any era", () =>
                (file.Limits ??= new PickLimits()).Eras.Clear());
        });
        var eraNames = Files.EraList().Select(e => e.Name).Concat(limits.Eras).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (eraNames.Count == 0) Label("No era list found; all eras are currently allowed.");
        foreach (string eraName in eraNames)
        {
            bool allowed = limits.Eras.Count == 0 || limits.Eras.Contains(eraName, StringComparer.OrdinalIgnoreCase);
            rows.Add(area => Toggle(area, allowed, " " + eraName, () =>
            {
                var chosen = file.Limits ??= new PickLimits();
                if (chosen.Eras.Count == 0) chosen.Eras = new List<string>(eraNames);
                if (allowed)
                {
                    if (chosen.Eras.Count <= 1) { Say("Keep at least one era, or choose Any era."); return; }
                    chosen.Eras.RemoveAll(e => string.Equals(e, eraName, StringComparison.OrdinalIgnoreCase));
                }
                else chosen.Eras.Add(eraName);
            }));
        }

        float top = box.y + Pad + Row;
        int shown = Math.Max(1, (int)((box.height - 2 * Pad - 3 * Row) / Row));
        int last = Math.Max(0, rows.Count - shown);
        playerLimitsTop = Math.Clamp(playerLimitsTop, 0, last);
        var viewport = new Rect(x, top, width, shown * Row);
        scrollers.Add((viewport, by => playerLimitsTop = Math.Clamp(playerLimitsTop - by * 2, 0, last)));
        for (int i = playerLimitsTop; i < Math.Min(rows.Count, playerLimitsTop + shown); i++)
            rows[i](new Rect(x, top + (i - playerLimitsTop) * Row, width, Row - 3));

        float footer = box.yMax - Pad - 2 * Row;
        GUI.Label(new Rect(x, footer, width - 132, Row - 3), rows.Count > shown ? "Scroll for more settings" : "Save keeps these battle settings.");
        if (rows.Count > shown)
        {
            Button(new Rect(x + width - 128, footer, 62, Row - 3), "Up", () => playerLimitsTop = Math.Max(0, playerLimitsTop - shown));
            Button(new Rect(x + width - 62, footer, 62, Row - 3), "Down", () => playerLimitsTop = Math.Min(last, playerLimitsTop + shown));
        }
        FileButtons((left, wide) => new Rect(x + left, footer + Row, wide, Row - 3), width);
        return true;
    }

    void AddPlayerPosition()
    {
        var source = file.Units.FirstOrDefault(u => u.Team == 0);
        if (source == null) { Say("Place a Team 1 tank in the Tanks tab first, then add player positions."); return; }
        int maximum = MainMenu.PlayerSpawnCapacity(map);
        if (maximum > 0 && file.Units.Count(u => u.Team == 0) >= maximum)
        { Say($"{map} supports {maximum} Team 1 tanks. Remove a tank to make room first."); return; }
        var marker = Mission.Ground(Files.Vector(source.Position) + Vector3.right * 6);
        var added = new BattleUnit { Id = file.NewId(), Team = 0, Blueprint = source.Blueprint, Pick = true, AtSpawn = true,
            Position = Files.Array(marker), Yaw = source.Yaw };
        file.Units.Add(added);
        Select(added);
        Say($"Added player position {added.Id}. Drag its marker in the Tanks tab to choose its exact place.");
    }

    void CancelLimitTyping()
    {
        if (typing == limitField) typing = null;
        limitField = null; limitSet = null;
    }

    /// Save and Play also apply a typed amount, so a visible edit cannot silently save the previous value.
    bool TryCommitPendingLimit()
    {
        if (limitField == null || typing != limitField) { limitField = null; limitSet = null; return true; }
        if (!int.TryParse(typed.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int amount) || amount < 0)
        { Say("Enter a whole number from 0 to 2,147,483,647 before saving or playing. Esc cancels."); return false; }
        limitSet?.Invoke(file.Limits ??= new PickLimits(), amount);
        CancelLimitTyping();
        return true;
    }

    /// Read numeric keys directly because native gameplay consumes IMGUI events. No map or editor shortcuts run here.
    bool LimitTyping(Keyboard keys)
    {
        if (limitField == null || typing != limitField) { limitField = null; limitSet = null; return false; }
        if (keys.escapeKey.wasPressedThisFrame) { CancelLimitTyping(); return true; }
        if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)
        {
            if (TryCommitPendingLimit()) Say("Player tank limit updated. Save keeps it with this battle.");
            return true;
        }
        if (keys.ctrlKey.isPressed)
        {
            if (keys.aKey.wasPressedThisFrame) { typed = ""; replaceLimitText = false; }
            return true;
        }
        if (keys.backspaceKey.wasPressedThisFrame)
        { typed = replaceLimitText || typed.Length == 0 ? "" : typed[..^1]; replaceLimitText = false; }
        if (keys.deleteKey.wasPressedThisFrame) { typed = ""; replaceLimitText = false; }
        void Append(char value)
        {
            if (replaceLimitText) { typed = ""; replaceLimitText = false; }
            if (typed.Length < 20) typed += value;
        }
        for (int i = 0; i < 10; i++)
        {
            var digit = i == 0 ? Key.Digit0 : Key.Digit1 + i - 1;
            if (!keys.shiftKey.isPressed && keys[digit].wasPressedThisFrame) Append((char)('0' + i));
            if (keys[Key.Numpad0 + i].wasPressedThisFrame) Append((char)('0' + i));
        }
        if (keys.commaKey.wasPressedThisFrame) Append(',');
        return true;
    }
}
