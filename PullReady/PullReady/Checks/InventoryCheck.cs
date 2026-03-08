// =============================================================================
// InventoryCheck.cs — Checks the local player's inventory for required items
// =============================================================================
//
// WHAT WE CHECK:
//   Each WatchedItem in config has an ItemId (uint) and MinQuantity.
//   We search all four main inventory bags (Inventory1–4) and count how many
//   stacks of that item we have in total. If the count is below MinQuantity,
//   we Fail or Warn depending on the Required flag.
//
// HOW FFXIV INVENTORY WORKS:
//   Items exist in four bag pages: Inventory1, Inventory2, Inventory3, Inventory4.
//   Each slot has an ItemId (0 = empty), a Quantity (stack size), and IsHQ.
//   We check all four pages to get the total across all stacks.
//
// WHY WE DON'T DISTINGUISH HQ vs NQ:
//   For tinctures/pots, players almost always use HQ. But since this is a
//   "do you HAVE one" check and not a "did you use it" check, we count both.
//   A future version could add an HQ-only flag to WatchedItem if needed.
// =============================================================================

using System.Collections.Generic;

using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace PullReady.Checks;

public static class InventoryCheck
{
    /// <summary>
    /// Runs inventory checks for every item in config.WatchedItems.
    /// Returns one CheckResult per watched item.
    /// </summary>
    /// <param name="gameInventory">Dalamud inventory service.</param>
    /// <param name="config">Plugin config — contains WatchedItems list.</param>
    public static List<CheckResult> Run(IGameInventory gameInventory, Configuration config)
    {
        var results = new List<CheckResult>();

        foreach (var watchedItem in config.WatchedItems)
        {
            // Count how many of this item the player has across all inventory bags.
            int totalQuantity = CountItem(gameInventory, watchedItem.ItemId);

            if (totalQuantity >= watchedItem.MinQuantity)
            {
                // Have enough — green
                results.Add(CheckResult.Pass(
                    watchedItem.Name,
                    $"x{totalQuantity} in bag"));
            }
            else if (totalQuantity > 0)
            {
                // Have some, but not enough — warn or fail depending on Required flag
                string detail = $"x{totalQuantity} (need {watchedItem.MinQuantity})";
                results.Add(watchedItem.Required
                    ? CheckResult.Fail(watchedItem.Name, detail)
                    : CheckResult.Warn(watchedItem.Name, detail));
            }
            else
            {
                // Have none at all
                string detail = watchedItem.Required ? "Missing! (Required)" : "Not in bags";
                results.Add(watchedItem.Required
                    ? CheckResult.Fail(watchedItem.Name, detail)
                    : CheckResult.Warn(watchedItem.Name, detail));
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPER
    // -------------------------------------------------------------------------

    /// <summary>
    /// Searches the four main inventory bags and returns the total count of
    /// the item with the given ItemId (both HQ and NQ combined).
    /// </summary>
    private static int CountItem(IGameInventory gameInventory, uint itemId)
    {
        // Inventory bags 1-4 are where non-equipped, non-key items live.
        // The game splits your 140-slot inventory into four bags of 35 slots each.
        var bagTypes = new[]
        {
            GameInventoryType.Inventory1,
            GameInventoryType.Inventory2,
            GameInventoryType.Inventory3,
            GameInventoryType.Inventory4,
        };

        int total = 0;

        foreach (var bagType in bagTypes)
        {
            var items = gameInventory.GetInventoryItems(bagType);
            foreach (var slot in items)
            {
                // ItemId can be the HQ version (ItemId + 1,000,000 in some APIs).
                // IGameInventory normalizes this — just compare the base ItemId.
                if (slot.ItemId == itemId)
                    total += slot.Quantity;
            }
        }

        return total;
    }
}
