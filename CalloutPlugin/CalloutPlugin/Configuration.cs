// =============================================================================
// Configuration.cs — Persisted User Settings + Timeline Storage
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CalloutPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // =========================================================================
    // ALERT DISPLAY SETTINGS
    // =========================================================================

    /// <summary>Default alert color (RGBA 0-1). Bright yellow-orange by default.</summary>
    public float[] DefaultAlertColor { get; set; } = [1f, 0.85f, 0.2f, 1f];

    /// <summary>
    /// Font size in pixels for the callout overlay text.
    /// This is the native baked size — larger = sharper and bigger on screen.
    /// The font atlas is rebuilt when this changes. Suggested range: 18–72.
    /// </summary>
    public float AlertFontSize { get; set; } = 36f;

    /// <summary>Screen position for alerts: 0.0=top, 0.5=center, 1.0=bottom.</summary>
    public float AlertVerticalPosition { get; set; } = 0.3f;

    /// <summary>Default pre-alert countdown seconds for new entries.</summary>
    public float DefaultPreAlertSeconds { get; set; } = 5f;

    /// <summary>Default display duration for new entries.</summary>
    public float DefaultDisplayDuration { get; set; } = 3f;

    /// <summary>Whether to show the fight timer HUD during combat.</summary>
    public bool ShowFightTimer { get; set; } = true;

    /// <summary>Whether to auto-load timelines based on duty/territory.</summary>
    public bool AutoLoadTimelines { get; set; } = true;

    // =========================================================================
    // TIMELINE STORAGE
    // =========================================================================

    /// <summary>
    /// All saved fight timelines. Persisted to the config JSON on disk.
    /// Users can also import/export these as standalone JSON files.
    /// </summary>
    public List<Data.FightTimeline> Timelines { get; set; } = new();

    /// <summary>ID of the currently selected timeline (null if none).</summary>
    public string? SelectedTimelineId { get; set; } = null;

    // =========================================================================
    // TIMELINE VIEW SETTINGS
    // =========================================================================

    /// <summary>Horizontal zoom: how many pixels represent one second on the timeline.</summary>
    public float TimelinePixelsPerSecond { get; set; } = 8f;

    /// <summary>Height of each role track in pixels.</summary>
    public float TimelineTrackHeight { get; set; } = 32f;

    /// <summary>Whether to draw entry labels (callout text) on the timeline.</summary>
    public bool TimelineShowLabels { get; set; } = true;

    /// <summary>Whether to draw the pre-alert window block (dimmer region before trigger).</summary>
    public bool TimelineShowPreAlertBlocks { get; set; } = true;

    /// <summary>Whether to draw the live playhead line during combat.</summary>
    public bool TimelineShowPlayhead { get; set; } = true;

    /// <summary>Color for Tank-role entries on the timeline (RGBA 0-1).</summary>
    public float[] TimelineColorTank    { get; set; } = [0.3f, 0.6f, 1.0f, 1f];   // blue

    /// <summary>Color for Healer-role entries on the timeline (RGBA 0-1).</summary>
    public float[] TimelineColorHealer  { get; set; } = [0.3f, 1.0f, 0.4f, 1f];   // green

    /// <summary>Color for DPS-role entries on the timeline (RGBA 0-1).</summary>
    public float[] TimelineColorDPS     { get; set; } = [1.0f, 0.35f, 0.35f, 1f]; // red

    /// <summary>Color for All-role entries on the timeline (RGBA 0-1).</summary>
    public float[] TimelineColorAll     { get; set; } = [1.0f, 0.85f, 0.2f,  1f]; // yellow

    // =========================================================================
    // COOLDOWN TRACKER HUD SETTINGS
    // =========================================================================

    /// <summary>Whether the cooldown tracker HUD is visible during combat.</summary>
    public bool CooldownTrackerEnabled { get; set; } = true;

    /// <summary>How many upcoming callouts to show in the tracker.</summary>
    public int CooldownTrackerEntryCount { get; set; } = 4;

    /// <summary>Screen position X (0-1, left to right).</summary>
    public float CooldownTrackerX { get; set; } = 0.02f;

    /// <summary>Screen position Y (0-1, top to bottom).</summary>
    public float CooldownTrackerY { get; set; } = 0.4f;

    /// <summary>Background opacity for the tracker panel (0=transparent, 1=opaque).</summary>
    public float CooldownTrackerBgAlpha { get; set; } = 0.6f;

    /// <summary>Whether to show only entries matching the player's current role.</summary>
    public bool CooldownTrackerRoleFilter { get; set; } = true;

    /// <summary>Whether to show the fight timer inside the tracker.</summary>
    public bool CooldownTrackerShowTimer { get; set; } = true;

    // =========================================================================
    // FFLOGS IMPORT
    // =========================================================================

    /// <summary>
    /// FF Logs v1 public API key. Get one at: https://www.fflogs.com/profile
    /// Scroll down to "Web API Key". This is a single string — no OAuth needed.
    /// </summary>
    public string? FflogsApiKey { get; set; } = null;

    // =========================================================================
    // INTERNAL
    // =========================================================================

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
