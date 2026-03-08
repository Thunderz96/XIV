// =============================================================================
// Configuration.cs — Persisted user settings for PullReady
// =============================================================================

using System;
using System.Collections.Generic;

using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PullReady;

/// <summary>
/// Represents one item the player wants to verify is in their inventory before pulling.
/// Examples: Grade 8 Tincture of Strength, Hi-Elixir, a specific food item.
/// </summary>
[Serializable]
public class WatchedItem
{
    /// <summary>Display name shown in the checklist (user-entered, not looked up).</summary>
    public string Name { get; set; } = "Item";

    /// <summary>
    /// FFXIV item ID. Find these on XIVAPI (xivapi.com/Item?name=...) or Garland Tools.
    /// The plugin checks your inventory for at least MinQuantity of this ID.
    /// </summary>
    public uint ItemId { get; set; } = 0;

    /// <summary>Minimum number of this item you must have in your bags. Default: 1.</summary>
    public int MinQuantity { get; set; } = 1;

    /// <summary>
    /// If true, a missing item causes a Fail (red). If false, it's a Warn (yellow).
    /// Set to true for things like tinctures. Set to false for optional comfort items.
    /// </summary>
    public bool Required { get; set; } = true;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // =========================================================================
    // WHICH CHECKS TO RUN
    // =========================================================================

    /// <summary>Whether to check if YOU have the Well Fed status.</summary>
    public bool CheckSelfFood { get; set; } = true;

    /// <summary>Whether to check if each PARTY MEMBER has Well Fed.</summary>
    public bool CheckPartyFood { get; set; } = true;

    /// <summary>Whether to check your equipped gear condition (repair level).</summary>
    public bool CheckRepairs { get; set; } = true;

    /// <summary>Whether to check your inventory for the items in WatchedItems.</summary>
    public bool CheckInventory { get; set; } = true;

    // =========================================================================
    // FOOD / WARN THRESHOLDS
    // =========================================================================

    /// <summary>
    /// Warn (yellow) if your Well Fed buff has less than this many minutes remaining.
    /// Example: 15 means "warn me if food is about to fall off within 15 minutes."
    /// A pull typically takes 10-15 minutes per attempt, so default is 20.
    /// </summary>
    public int FoodWarnMinutes { get; set; } = 20;

    /// <summary>
    /// Same threshold applied to party members. If any party member's food
    /// has less than this many minutes left, warn (or ping party chat).
    /// </summary>
    public int PartyFoodWarnMinutes { get; set; } = 10;

    // =========================================================================
    // REPAIR THRESHOLD
    // =========================================================================

    /// <summary>
    /// Gear condition is stored as 0–30000 (30000 = 100%).
    /// Warn if any equipped piece is below this percentage.
    /// Default: 50 (warn at 50% condition — gear is getting dangerously low).
    /// </summary>
    public int RepairWarnPercent { get; set; } = 50;

    /// <summary>
    /// Fail (red) if any equipped piece is below this percentage.
    /// Default: 10 (fail at 10% — gear is basically broken and affecting stats).
    /// </summary>
    public int RepairFailPercent { get; set; } = 10;

    // =========================================================================
    // PARTY CHAT PING
    // =========================================================================

    /// <summary>
    /// If true, the plugin can send a /party message summarising readiness.
    /// This is opt-in and only fires when the user clicks "Ping Party" in the UI.
    /// It will NOT auto-send without user action.
    /// </summary>
    public bool EnablePartyChatPing { get; set; } = false;

    /// <summary>
    /// If EnablePartyChatPing is on, only ping the party if at least one member
    /// has less than this many minutes of food remaining. Prevents spamming the
    /// chat when everyone is fine.
    /// Example: 15 → only ping if someone has &lt; 15 min of food left.
    /// </summary>
    public int PingThresholdMinutes { get; set; } = 15;

    // =========================================================================
    // WINDOW BEHAVIOUR
    // =========================================================================

    /// <summary>
    /// Automatically open the PullReady window when you zone into a duty
    /// (i.e., when TerritoryChanged fires for a duty territory).
    /// </summary>
    public bool AutoOpenOnZoneIn { get; set; } = true;

    // =========================================================================
    // INVENTORY ITEMS TO CHECK
    // =========================================================================

    /// <summary>
    /// User-defined list of items to verify before pulling.
    /// Each entry has a name, item ID, minimum quantity, and required flag.
    /// Edit this list in the plugin's Settings window.
    /// </summary>
    public List<WatchedItem> WatchedItems { get; set; } = new()
    {
        // Default: Grade 8 Tincture of Strength (most common WAR/MNK/etc. pot)
        // Players should replace this with their job's correct tincture.
        new WatchedItem
        {
            Name        = "Grade 8 Tincture of Strength",
            ItemId      = 37840,
            MinQuantity = 1,
            Required    = true
        }
    };

    // =========================================================================
    // INTERNAL DALAMUD PLUMBING
    // =========================================================================

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
