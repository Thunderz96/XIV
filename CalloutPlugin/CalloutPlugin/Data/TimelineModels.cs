// =============================================================================
// TimelineModels.cs — Core Data Structures for Fight Timelines
// =============================================================================
//
// These models define WHAT a timeline is and WHAT a callout entry looks like.
// Think of it as the "schema" for our timeline data.
//
// HIERARCHY:
//   FightTimeline          — One per fight (e.g., "M4S - Wicked Thunder")
//     └── TimelineEntry[]  — Individual callouts within that fight
//          each has: trigger time, ability name, alert settings
//
// SERIALIZATION:
//   These classes are designed to serialize cleanly to/from JSON, so users
//   can import/export timeline files and share them with friends.
// =============================================================================

using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace CalloutPlugin.Data;

// =============================================================================
// ALERT TYPE — How should the callout appear?
// =============================================================================

/// <summary>
/// Defines the visual/audio style of a callout alert.
/// Users pick this per-entry in the timeline editor.
/// 
/// [Flags] means these can be combined with bitwise OR:
///   AlertType.ScreenFlash | AlertType.Countdown = both at once
/// This is like Python's Flag enum or C++ bitmask flags.
/// </summary>
[Flags]
public enum AlertType
{
    None        = 0,
    ScreenFlash = 1 << 0,  // Big flashing text overlay
    Countdown   = 1 << 1,  // "3... 2... 1... NOW" countdown
    Sound       = 1 << 2,  // Play an alert sound
    TextPopup   = 1 << 3,  // Floating text that fades out
}

// =============================================================================
// TIMELINE ENTRY — A single callout moment
// =============================================================================

/// <summary>
/// One callout within a fight timeline. For example:
///   "At 1:15, flash REPRISAL with a 3-second countdown"
/// </summary>

public enum TargetRole
{
    Tank = 1,
    Healer = 2,
    DPS = 3,
    All = 0
}

public class TimelineEntry
{
    /// <summary>
    /// which role this callout is for (tank/healer/dps/all).
    /// </summary>
    public TargetRole TargetRole { get; set; } = TargetRole.All;
    
    /// <summary>
    /// Unique ID for this entry (used for editing/deleting).
    /// Generated automatically when creating a new entry.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// When this callout triggers, in seconds from fight start.
    /// Example: 75.0 = 1 minute 15 seconds into the fight.
    /// We use float for sub-second precision (e.g., 75.5 = 1:15.5).
    /// </summary>
    public float TriggerTime { get; set; } = 0f;

    /// <summary>
    /// The text to display when this callout fires.
    /// Usually an ability name like "Reprisal" or a custom note
    /// like "Stack + Reprisal" or "Spread!".
    /// </summary>
    public string CalloutText { get; set; } = "";

    /// <summary>
    /// Optional: the actual game ability name (for icon lookup).
    /// If set, we can display the ability's icon from game data.
    /// </summary>
    public string? AbilityName { get; set; } = null;

    /// <summary>
    /// How many seconds BEFORE the trigger time to start the countdown.
    /// Example: if PreAlertSeconds = 5 and TriggerTime = 75, the countdown
    /// starts at 70 seconds (showing "5... 4... 3... 2... 1... NOW!").
    /// Set to 0 for instant callout with no countdown.
    /// </summary>
    public float PreAlertSeconds { get; set; } = 5f;

    /// <summary>
    /// How long the alert stays visible on screen (seconds).
    /// After this duration, the overlay fades out.
    /// </summary>
    public float DisplayDuration { get; set; } = 3f;

    /// <summary>
    /// What type(s) of alert to show. Can combine multiple.
    /// Default: screen flash + countdown.
    /// </summary>
    public AlertType AlertTypes { get; set; } = AlertType.ScreenFlash | AlertType.Countdown;

    /// <summary>
    /// Optional color override for this specific callout (RGBA, 0-1 range).
    /// Null = use the default color from global settings.
    /// Useful for color-coding: red for tankbusters, blue for raidwides, etc.
    /// </summary>
    public float[]? Color { get; set; } = null;

    /// <summary>
    /// Whether this entry is currently enabled. Lets users temporarily
    /// disable entries without deleting them (useful for tweaking timelines).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional note/comment for the user's reference. Not displayed in-game.
    /// Example: "This is the first raidwide after adds phase"
    /// </summary>
    public string? Note { get; set; } = null;

    // ---- Helpers ----

    /// <summary>
    /// Formats TriggerTime as "M:SS.s" for display.
    /// Example: 75.5f → "1:15.5"
    /// </summary>
    [JsonIgnore] // Don't save this to JSON — it's computed from TriggerTime
    public string FormattedTime
    {
        get
        {
            var minutes = (int)(TriggerTime / 60);
            var seconds = TriggerTime % 60;
            return $"{minutes}:{seconds:00.#}";
        }
    }
}

// =============================================================================
// FIGHT TIMELINE — A collection of entries for one encounter
// =============================================================================

/// <summary>
/// Represents a complete timeline for a single fight/encounter.
/// Users create one of these per boss fight they want callouts for.
/// </summary>
public class FightTimeline
{
    /// <summary>Unique identifier for this timeline.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Display name for this timeline.
    /// Example: "M4S - Wicked Thunder" or "EX4 - Hell on Rails"
    /// </summary>
    public string Name { get; set; } = "New Timeline";

    /// <summary>
    /// Optional: The TerritoryType ID for the duty this timeline is for.
    /// If set, the plugin can auto-load this timeline when you enter the duty.
    /// 0 = no auto-load (manual selection only).
    /// 
    /// TerritoryType is FFXIV's internal ID for every zone/instance in the game.
    /// You can find these via Dalamud's data browser or community resources.
    /// </summary>
    public uint TerritoryTypeId { get; set; } = 0;

    /// <summary>
    /// The list of callout entries in this timeline, sorted by trigger time.
    /// </summary>
    public List<TimelineEntry> Entries { get; set; } = new();

    /// <summary>Whether this timeline is currently active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional description or notes about this timeline.</summary>
    public string? Description { get; set; } = null;

    /// <summary>
    /// Version string for tracking changes when sharing timelines.
    /// Example: "1.0" or "2024-12-15"
    /// </summary>
    public string Version { get; set; } = "1.0";

    /// <summary>Who created this timeline (for shared timelines).</summary>
    public string? Author { get; set; } = null;
}
