// =============================================================================
// CheckEngine.cs — Runs all readiness checks and produces a snapshot result
// =============================================================================
//
// This is the "brain" of PullReady. It calls each individual check module
// and assembles the results into a ReadinessSnapshot that the UI can display.
//
// USAGE:
//   var snapshot = CheckEngine.Run(clientState, partyList, gameInventory, config);
//   // snapshot.OverallStatus tells you if you're Ready, Marginal, or NotReady
//   // snapshot.SelfChecks    = checks about the local player
//   // snapshot.PartyChecks   = checks about party members
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;

using PullReady.Checks;

namespace PullReady;

/// <summary>
/// The overall readiness verdict shown in the HUD banner.
/// Controls the big green/yellow/red color at the top of the window.
/// </summary>
public enum OverallStatus
{
    Ready,      // All checks Pass — pull when you are!
    Marginal,   // At least one Warn — you CAN pull but something is low
    NotReady    // At least one Fail — fix this before pulling
}

/// <summary>
/// A point-in-time snapshot of all check results.
/// Created by CheckEngine.Run() and handed to the HUD window for display.
/// Immutable — create a new one each time the player clicks Re-check.
/// </summary>
public class ReadinessSnapshot
{
    /// <summary>Overall verdict — worst status across all checks.</summary>
    public OverallStatus Overall { get; init; }

    /// <summary>When this snapshot was taken (for "Last checked: 30s ago" display).</summary>
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Checks that apply to the LOCAL PLAYER only.
    /// Includes: WellFed(self), GearCondition, Inventory items.
    /// </summary>
    public List<CheckResult> SelfChecks { get; init; } = new();

    /// <summary>
    /// One CheckResult per party member.
    /// Each shows their name and food status/time remaining.
    /// </summary>
    public List<CheckResult> PartyChecks { get; init; } = new();
}

/// <summary>
/// Static class that runs all enabled checks and returns a ReadinessSnapshot.
/// </summary>
public static class CheckEngine
{
    /// <summary>
    /// Executes all enabled readiness checks and returns a snapshot.
    /// Call this when the player clicks Re-check or on zone-in.
    /// </summary>
    public static ReadinessSnapshot Run(
        IObjectTable objectTable,
        IPartyList partyList,
        IGameInventory gameInventory,
        Configuration config)
    {
        var selfChecks  = new List<CheckResult>();
        var partyChecks = new List<CheckResult>();

        // ---- Self: Well Fed ----
        if (config.CheckSelfFood)
            selfChecks.Add(WellFedCheck.RunSelf(objectTable, config));

        // ---- Self: Gear Repair ----
        if (config.CheckRepairs)
            selfChecks.Add(GearRepairCheck.Run(gameInventory, config));

        // ---- Self: Inventory items ----
        if (config.CheckInventory && config.WatchedItems.Count > 0)
            selfChecks.AddRange(InventoryCheck.Run(gameInventory, config));

        // ---- Party: Well Fed ----
        if (config.CheckPartyFood)
            partyChecks.AddRange(WellFedCheck.RunParty(partyList, config));

        // ---- Determine overall status ----
        // We combine self and party checks. The worst single result wins.
        // Fail beats Warn beats Pass. Skip results are ignored.
        var allResults = selfChecks.Concat(partyChecks);
        var overall    = DetermineOverall(allResults);

        return new ReadinessSnapshot
        {
            Overall     = overall,
            CheckedAt   = DateTime.Now,
            SelfChecks  = selfChecks,
            PartyChecks = partyChecks,
        };
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Collapses a list of CheckResults into a single OverallStatus.
    /// Rule: any Fail → NotReady; any Warn (no Fail) → Marginal; else → Ready.
    /// </summary>
    private static OverallStatus DetermineOverall(IEnumerable<CheckResult> results)
    {
        bool anyFail = false;
        bool anyWarn = false;

        foreach (var r in results)
        {
            if (r.Status == CheckStatus.Fail) anyFail = true;
            if (r.Status == CheckStatus.Warn) anyWarn = true;
        }

        if (anyFail) return OverallStatus.NotReady;
        if (anyWarn) return OverallStatus.Marginal;
        return OverallStatus.Ready;
    }
}
