using Sprocket.ArtificialIntelligence;
using Sprocket.Orders;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Control;
using UnityEngine;

namespace SprocketBattles;

internal static partial class Battle
{
    /// Automatic opponents are a fallback. Player control, an explicit stop and active authored/F10 orders win.
    internal static bool CanAutoEngage(VehicleBehaviour tank)
    {
        if (tank == null || tank.ControlType == ControlType.Player || Puppet.Of(tank) != null
            || explicitStops.Contains(tank.Pointer) || Out(tank)) return false;
        if (UnitOf(tank) is { } unit && Mission.Waiting(unit.Id)) return false;
        var receiver = tank.OrderReciever;
        if (receiver == null || !receiver.CanReceiveOrder) return false;
        if (manualMoves.ContainsKey(tank.Pointer)) return false;
        var runner = runners?.FirstOrDefault(r => r.Tank != null && r.Tank.Pointer == tank.Pointer);
        if (runner != null && (runner.Step < runner.Unit.Path.Count
            || runner.Unit.Attack?.Target is { } id && tanksById.TryGetValue(id, out var target) && !Out(target))) return false;
        return !attacks.Any(a => !a.Automatic && a.Tank != null && a.Tank.Pointer == tank.Pointer && !Out(a.Enemy));
    }

    internal static VehicleBehaviour? AutomaticTarget(VehicleBehaviour tank) =>
        attacks.FirstOrDefault(a => a.Automatic && a.Tank != null && a.Tank.Pointer == tank.Pointer && !Out(a.Enemy))?.Enemy;

    /// Uses the ordinary attack order so pathfinding, aiming and firing remain under the native AI.
    /// It never takes over a manual order or clears an explicit stop.
    internal static bool SendAutoAgainst(VehicleBehaviour tank, VehicleBehaviour enemy)
    {
        if (!CanAutoEngage(tank) || Out(enemy) || tank.Pointer == enemy.Pointer
            || !AreEnemies(tank, enemy)) return false;
        Hold(tank);
        idleCandidates[tank.Pointer] = tank;
        ReleaseMovementHold(tank);
        FireAtWill(tank);
        if (Commander(tank) is { } commander)
        {
            commander.FireMode = Sprocket.ArtificialIntelligence.FireMode.FireAtWill;
        }
        StartAttack(tank, enemy, $"{NameOf(tank)} automatically on {NameOf(enemy)}", true,
            AttackApproachType.Aggressive, automatic: true);
        if (attacks.FirstOrDefault(a => a.Tank.Pointer == tank.Pointer && a.Automatic) is { } attack)
        {
            attack.LastSeenAt = Time.time;
            attack.Closing = false;
        }
        return true;
    }

    internal static bool AreEnemies(VehicleBehaviour tank, VehicleBehaviour other) =>
        FreeForAllRules.AreEnemies(playing?.FreeForAll == true, UnitOf(tank)?.Team, UnitOf(other)?.Team,
            (byte)tank.ID.TeamID, (byte)other.ID.TeamID);

    // Keep combat orders active so the native planner closes in, aims and shoots without being canceled by raw move tasks.
    static void WatchAutoApproach(Attack attack)
    {
        if (!CanAutoEngage(attack.Tank)) return;
        ReleaseMovementHold(attack.Tank);
        FireAtWill(attack.Tank);
        if (Commander(attack.Tank) is { } cmd)
        {
            cmd.FireMode = Sprocket.ArtificialIntelligence.FireMode.FireAtWill;
            var cts = cmd.attackOrderCts;
            bool active = cts != null && !cts.IsCancellationRequested;
            if (!active && Time.time - attack.GivenAt > 5)
            {
                GiveAttack(attack, "re-issued aggressive attack");
            }
        }
    }

    static void GiveAutoApproach(Attack attack)
    {
        GiveAttack(attack, "approach");
    }
}
