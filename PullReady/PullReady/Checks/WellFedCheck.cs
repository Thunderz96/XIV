// =============================================================================
// WellFedCheck.cs — Checks Well Fed status for self and party members
// =============================================================================
//
// WHAT WE CHECK:
//   Status ID 48 = "Well Fed" — the buff applied when you eat food.
//   The status has a RemainingTime field (in seconds) that we convert to minutes.
//
// SELF CHECK:
//   Reads LocalPlayer.StatusList directly. Full access — we get exact time left.
//
// PARTY CHECK:
//   Reads each IPartyMember.Statuses. The game server sends party member status
//   lists over the network, so we CAN see their Well Fed buff and remaining time.
//
// HOW STATUS TIME WORKS:
//   status.RemainingTime is a float (seconds). 3600f = 60 minutes = full food.
//   We convert to minutes by dividing by 60.
// =============================================================================

using System;
using System.Collections.Generic;

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;

namespace PullReady.Checks;

public static class WellFedCheck
{
    /// <summary>
    /// The FFXIV status ID for "Well Fed".
    /// This is the buff you receive when you eat food before a fight.
    /// </summary>
    private const uint WellFedStatusId = 48;

    // -------------------------------------------------------------------------
    // SELF CHECK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Checks whether the local player has the Well Fed buff and how long it lasts.
    /// Returns a CheckResult with Pass/Warn/Fail based on remaining minutes vs config thresholds.
    /// </summary>
    /// <param name="objectTable">Dalamud object table — gives us LocalPlayer.</param>
    /// <param name="config">Plugin config — contains FoodWarnMinutes threshold.</param>
    public static CheckResult RunSelf(IObjectTable objectTable, Configuration config)
    {
        // Get the local player from the object table (non-obsolete approach)
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return CheckResult.Skip("Well Fed (You)");

        // Search the local player's active status list for Well Fed
        foreach (var status in localPlayer.StatusList)
        {
            if (status.StatusId == WellFedStatusId)
            {
                // Found it — convert remaining seconds to minutes
                int minutesLeft = (int)(status.RemainingTime / 60f);

                if (minutesLeft < config.FoodWarnMinutes)
                    // Running low — warn the player
                    return CheckResult.Warn("Well Fed (You)", $"{minutesLeft} min remaining");

                // Plenty of time left
                return CheckResult.Pass("Well Fed (You)", $"{minutesLeft} min remaining");
            }
        }

        // Status not found — no food buff at all
        return CheckResult.Fail("Well Fed (You)", "No food buff!");
    }

    // -------------------------------------------------------------------------
    // PARTY CHECK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Checks every party member's Well Fed status.
    /// Returns one CheckResult per party member (by name).
    /// Members with low food or no food are flagged with Warn or Fail.
    /// </summary>
    /// <param name="partyList">Dalamud party list service.</param>
    /// <param name="config">Plugin config — contains PartyFoodWarnMinutes threshold.</param>
    public static List<CheckResult> RunParty(IPartyList partyList, Configuration config)
    {
        var results = new List<CheckResult>();

        // Iterate every member currently in the party list.
        // IPartyList implements IReadOnlyCollection<IPartyMember> so we can foreach it.
        foreach (var member in partyList)
        {
            // Skip empty/invalid slots (the party list can have gaps)
            if (member == null || member.Name.ToString() == "")
                continue;

            string name = member.Name.ToString();
            bool found = false;

            // Check this member's status list for Well Fed
            foreach (var status in member.Statuses)
            {
                if (status.StatusId == WellFedStatusId)
                {
                    int minutesLeft = (int)(status.RemainingTime / 60f);

                    if (minutesLeft < config.PartyFoodWarnMinutes)
                        results.Add(CheckResult.Warn(name, $"Food: {minutesLeft} min left ⚠"));
                    else
                        results.Add(CheckResult.Pass(name, $"Food: {minutesLeft} min"));

                    found = true;
                    break;
                }
            }

            if (!found)
                results.Add(CheckResult.Fail(name, "No food buff!"));
        }

        return results;
    }
}
