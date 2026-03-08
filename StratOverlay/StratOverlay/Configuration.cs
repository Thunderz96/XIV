// =============================================================================
// Configuration.cs — Persisted Settings for StratOverlay
// =============================================================================

using System;
using System.Collections.Generic;

using Dalamud.Configuration;
using Dalamud.Plugin;

using StratOverlay.Data;

namespace StratOverlay;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // =========================================================================
    // TIMELINE STORAGE
    // =========================================================================

    /// <summary>All saved strat timelines. Each has its own variant list and entries.</summary>
    public List<StratTimeline> Timelines { get; set; } = new();

    /// <summary>ID of the currently selected timeline (null = none selected).</summary>
    public string? SelectedTimelineId { get; set; } = null;

    /// <summary>Whether to auto-load a timeline when entering a matching territory.</summary>
    public bool AutoLoadTimelines { get; set; } = true;

    // =========================================================================
    // OVERLAY DISPLAY SETTINGS
    // =========================================================================

    /// <summary>
    /// Corner the overlay anchors to.
    /// 0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight
    /// </summary>
    public int OverlayAnchor { get; set; } = 1; // default: top-right

    /// <summary>X offset from the anchor corner in pixels.</summary>
    public float OverlayOffsetX { get; set; } = 20f;

    /// <summary>Y offset from the anchor corner in pixels.</summary>
    public float OverlayOffsetY { get; set; } = 20f;

    /// <summary>
    /// Display width of the image in pixels.
    /// Height is scaled automatically to preserve aspect ratio.
    /// </summary>
    public float OverlayImageWidth { get; set; } = 400f;

    /// <summary>Background panel opacity (0=transparent, 1=opaque).</summary>
    public float OverlayBgAlpha { get; set; } = 0.85f;

    /// <summary>Whether to show the entry label (caption) below the image.</summary>
    public bool OverlayShowCaption { get; set; } = true;

    /// <summary>
    /// Whether to show the active variant name as a small watermark on the overlay.
    /// Highly recommended — helps you spot if you forgot to switch variants.
    /// </summary>
    public bool OverlayShowVariantLabel { get; set; } = true;

    /// <summary>Fade in/out duration in seconds.</summary>
    public float OverlayFadeDuration { get; set; } = 0.3f;

    // =========================================================================
    // FREE-POSITION MODE
    // =========================================================================

    /// <summary>
    /// When true, the overlay is positioned by OverlayFreeX/Y (absolute screen px)
    /// instead of the anchor + offset system. Drag to reposition in edit mode.
    /// </summary>
    public bool OverlayFreePosition { get; set; } = false;

    /// <summary>Absolute X position when OverlayFreePosition = true.</summary>
    public float OverlayFreeX { get; set; } = 100f;

    /// <summary>Absolute Y position when OverlayFreePosition = true.</summary>
    public float OverlayFreeY { get; set; } = 100f;

    // =========================================================================
    // COMMUNITY STRATS
    // =========================================================================

    /// <summary>Whether the built-in community strat timelines have been generated.</summary>
    public bool CommunityStratsGenerated { get; set; } = false;

    // =========================================================================
    // INTERNAL
    // =========================================================================

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
