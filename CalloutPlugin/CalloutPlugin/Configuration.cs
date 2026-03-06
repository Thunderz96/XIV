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
    // INTERNAL
    // =========================================================================

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
