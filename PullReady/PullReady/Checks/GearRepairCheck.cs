// =============================================================================
// GearRepairCheck.cs — Checks equipped gear condition for the local player
// =============================================================================
//
// WHAT WE CHECK:
//   Each equipped item has a Condition field ranging from 0 to 30000.
//   30000 = 100% condition (brand new / freshly repaired).
//   0     = 0% condition (completely broken — gives major stat penalty).
//
// HOW WE READ GEAR:
//   Dalamud's IGameInventory service exposes your equipped gear via:
//     InventoryType.EquippedItems
//   We iterate all 13 equipment slots and find the lowest condition value.
//
// WHY THIS MATTERS:
//   Gear that drops below 50% starts losing stats. At 0% the penalty is severe.
//   Catching this before a pull saves an attempt.
//
// NOTE: We can ONLY check the LOCAL player's gear condition. The game server
//       does not transmit party members' item conditions.
// =============================================================================

using System;

using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace PullReady.Checks;

public static class GearRepairCheck
{
    /// <summary>
    /// Max condition value the game uses to represent 100%.
    /// Dividing any item's Condition by this gives a 0.0–1.0 fraction.
    /// </summary>
    private const float MaxCondition = 30000f;

    /// <summary>
    /// Checks all 13 equipped item slots and returns a single CheckResult
    /// based on the LOWEST condition piece found.
    ///
    /// Logic:
    ///   - Any piece below RepairFailPercent → Fail (red)
    ///   - Any piece below RepairWarnPercent → Warn (yellow)
    ///   - Otherwise → Pass (green)
    /// </summary>
    /// <param name="gameInventory">Dalamud inventory service.</param>
    /// <param name="config">Plugin config — contains RepairWarnPercent and RepairFailPercent.</param>
    public static CheckResult Run(IGameInventory gameInventory, Configuration config)
    {
        int lowestPercent = 100;     // Track the worst piece
        string lowestSlot = "Unknown";

        // EquippedItems covers all 13 equipment slots (Main Hand → Ring R).
        // Slot 0 = Main Hand, 1 = Off Hand, 2 = Head, ... 12 = Ring R
        // We iterate all of them looking for the lowest condition.
        var equippedItems = gameInventory.GetInventoryItems(GameInventoryType.EquippedItems);

        foreach (var item in equippedItems)
        {
            // Skip empty slots (unequipped slots have ItemId == 0)
            if (item.ItemId == 0)
                continue;

            // Convert raw condition (0–30000) to a percentage (0–100)
            int conditionPercent = (int)((item.Condition / MaxCondition) * 100f);

            if (conditionPercent < lowestPercent)
            {
                lowestPercent = conditionPercent;

                // Give a human-readable slot name based on the container slot index.
                // Slot numbers match the game's equipment slot order.
                lowestSlot = item.InventorySlot switch
                {
                    0  => "Main Hand",
                    1  => "Off Hand",
                    2  => "Head",
                    3  => "Body",
                    4  => "Hands",
                    5  => "Belt",
                    6  => "Legs",
                    7  => "Feet",
                    8  => "Earrings",
                    9  => "Necklace",
                    10 => "Bracelet",
                    11 => "Ring L",
                    12 => "Ring R",
                    13 => "Soul Crystal",
                    _  => $"Slot {item.InventorySlot}"
                };
            }
        }

        // Edge case: no items equipped at all (happens if not logged in / not geared)
        if (lowestPercent == 100 && lowestSlot == "Unknown")
            return CheckResult.Skip("Gear Condition");

        string detail = $"Lowest: {lowestSlot} {lowestPercent}%";

        if (lowestPercent <= config.RepairFailPercent)
            return CheckResult.Fail("Gear Condition", detail + " — BROKEN!");

        if (lowestPercent <= config.RepairWarnPercent)
            return CheckResult.Warn("Gear Condition", detail);

        return CheckResult.Pass("Gear Condition", detail);
    }
}
