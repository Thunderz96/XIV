// =============================================================================
// StratModels.cs (continued) — StratVariant and StratTimeline
// =============================================================================
// NOTE: This file is a continuation — C# allows splitting a namespace across
// files. StratEntry and its supporting types are in StratModels.cs.
// We keep this separate to stay within reasonable file lengths.
// =============================================================================

using System;
using System.Collections.Generic;

namespace StratOverlay.Data;

// =============================================================================
// STRAT VARIANT — a named strategy option
// =============================================================================

/// <summary>
/// A named strategy variant within a fight timeline.
/// e.g. "Hector", "Nukemaru", "Guild strat", "Default".
///
/// Each StratEntry's Images dictionary uses StratVariant.Id as its key,
/// so switching the active variant instantly changes all displayed images.
///
/// The "default" variant (Id = "default") always exists and cannot be deleted.
/// </summary>
[Serializable]
public class StratVariant
{
    /// <summary>
    /// Unique key used in StratEntry.Images dictionary.
    /// "default" is reserved for the built-in fallback variant.
    /// All other variants use GUIDs.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name shown in the variant dropdown, e.g. "Hector".</summary>
    public string Name { get; set; } = "Default";

    /// <summary>Optional note — good for storing a raidplan URL or Discord link.</summary>
    public string Note { get; set; } = "";

    /// <summary>Creates the built-in "default" variant. Used when a new timeline is created.</summary>
    public static StratVariant CreateDefault() => new StratVariant
    {
        Id   = "default",
        Name = "Default",
        Note = "Fallback images shown when no variant-specific image is defined."
    };
}

// =============================================================================
// STRAT TIMELINE — one fight definition
// =============================================================================

/// <summary>
/// A complete strat setup for one fight.
/// Analogous to FightTimeline in CalloutPlugin — same TerritoryTypeId field
/// for auto-loading, same Enabled flag.
///
/// A StratTimeline always has at least one variant: "Default" (Id = "default").
/// </summary>
[Serializable]
public class StratTimeline
{
    /// <summary>Unique identifier. Never changes once created.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name, e.g. "M9S Savage — Hector/Nukemaru".</summary>
    public string Name { get; set; } = "New Timeline";

    /// <summary>
    /// FFXIV territory/zone ID for this fight.
    /// When the player enters this zone, the engine auto-loads this timeline.
    /// 0 = never auto-load (manual only).
    /// </summary>
    public int TerritoryTypeId { get; set; } = 0;

    /// <summary>
    /// All defined strategy variants for this fight.
    /// Always contains at least one entry: the Default variant (Id = "default").
    /// </summary>
    public List<StratVariant> Variants { get; set; } = new() { StratVariant.CreateDefault() };

    /// <summary>
    /// ID of the currently active variant.
    /// The engine uses this to resolve which image to display for each entry.
    /// Persisted so your variant choice survives plugin reloads.
    /// </summary>
    public string ActiveVariantId { get; set; } = "default";

    /// <summary>All trigger entries for this fight.</summary>
    public List<StratEntry> Entries { get; set; } = new();

    /// <summary>Whether this timeline is active. Disabled timelines never trigger.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional description or notes about this timeline.</summary>
    public string Description { get; set; } = "";

    /// <summary>Who made this timeline (for community sharing).</summary>
    public string Author { get; set; } = "";

    /// <summary>Version string for community sharing, e.g. "1.0".</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// True for auto-generated community strats from wtfdig.
    /// Built-in timelines display with a lock icon in the UI and
    /// can be reset from the Settings tab.
    /// </summary>
    public bool IsBuiltIn { get; set; } = false;

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Returns the currently active StratVariant object.
    /// Falls back to the Default variant if the active ID is not found.
    /// </summary>
    public StratVariant GetActiveVariant()
    {
        return Variants.Find(v => v.Id == ActiveVariantId)
            ?? Variants.Find(v => v.Id == "default")
            ?? Variants[0];
    }

    /// <summary>
    /// Resolves which StratImage to display for a given entry using the active variant.
    ///
    /// Resolution order:
    ///   1. Images[activeVariantId]  — variant-specific image
    ///   2. Images["default"]        — fallback image
    ///   3. null                     — no image, entry is silently skipped
    /// </summary>
    public StratImage? ResolveImage(StratEntry entry)
    {
        if (entry.Images.TryGetValue(ActiveVariantId, out var variantImage)
            && !string.IsNullOrWhiteSpace(variantImage.Path))
            return variantImage;

        if (entry.Images.TryGetValue("default", out var defaultImage)
            && !string.IsNullOrWhiteSpace(defaultImage.Path))
            return defaultImage;

        return null;
    }
}
