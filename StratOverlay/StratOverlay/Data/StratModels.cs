// =============================================================================
// StratModels.cs — Core Data Structures for StratOverlay
// =============================================================================
//
// This file defines everything that gets saved to disk and passed around
// at runtime. The hierarchy is:
//
//   StratTimeline          ← one fight (e.g. "M9S Savage")
//     └─ StratVariant[]    ← named strategies ("Hector", "Nukemaru", ...)
//     └─ StratEntry[]      ← one trigger event (e.g. "Limit Cut @ 0:45")
//          └─ Images{}     ← Dictionary<variantId, StratImage>
//
// VARIANT RESOLUTION (at display time):
//   1. Look up Images[activeVariantId]  → use if present
//   2. Fall back to Images["default"]   → use if present
//   3. Neither exists                   → skip silently
//
// This means every entry only needs a "default" image to work. Variant-
// specific images are strictly opt-in overrides.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StratOverlay.Data;

// =============================================================================
// ENUMS
// =============================================================================

/// <summary>What kind of event fires this strat image.</summary>
public enum TriggerType
{
    /// <summary>Fires at a fixed number of seconds into the fight (stopwatch-based).</summary>
    Timeline,

    /// <summary>Fires when a specific boss ability ID is detected on the network.</summary>
    BossAction,

    /// <summary>Only shown when the user manually requests it via /strat command.</summary>
    Manual,
}

/// <summary>
/// For BossAction triggers: which moment of the ability lifecycle fires the image.
/// </summary>
public enum CastPhase
{
    /// <summary>Boss begins casting — fires as soon as the cast bar appears. Best for prep time.</summary>
    OnCastStart,

    /// <summary>Boss ability resolves — fires on the frame the hit lands.</summary>
    OnAbilityUse,

    /// <summary>A specific status effect is applied to a player. Useful for debuff-resolve mechanics.</summary>
    OnStatusApplied,
}

/// <summary>
/// Where the image comes from. Determines how ImagePath is interpreted.
/// </summary>
public enum ImageSource
{
    /// <summary>
    /// Path is a wtfdig mechanic slug (e.g. "limit-cut").
    /// The full URL is assembled at runtime using the fight ID and role.
    /// Image is downloaded and cached to disk on first use.
    /// </summary>
    WtfDig,

    /// <summary>
    /// Path is a direct URL to any image (Imgur, Discord CDN, etc.).
    /// Downloaded and cached to disk on first use.
    /// </summary>
    Url,

    /// <summary>
    /// Path is a local file path. File is read directly each time (with caching).
    /// Supports drag-and-drop from Windows Explorer in the Config UI.
    /// </summary>
    LocalFile,
}

/// <summary>
/// Which role(s) this entry should be shown to.
/// Matches the same values as CalloutPlugin for cross-plugin consistency.
/// </summary>
public enum TargetRole
{
    All    = 0,
    Tank   = 1,
    Healer = 2,
    DPS    = 3,
}

// =============================================================================
// STRAT IMAGE — one image for one variant
// =============================================================================

/// <summary>
/// Holds the source and path for a single image, tied to one variant of one entry.
/// Multiple StratImage objects can exist per entry — one per variant.
/// </summary>
[Serializable]
public class StratImage
{
    /// <summary>Where this image comes from (wtfdig slug, URL, or local file).</summary>
    public ImageSource Source { get; set; } = ImageSource.Url;

    /// <summary>
    /// The image path — interpretation depends on Source:
    ///   WtfDig   → mechanic slug, e.g. "limit-cut" (role is stored separately on the entry)
    ///   Url      → full URL, e.g. "https://imgur.com/abc123.png"
    ///   LocalFile → absolute or relative file path
    /// </summary>
    public string Path { get; set; } = "";

    // Note: no role/variant field needed — Path already contains the full
    // relative path including filename, e.g. "74/m12s/p1-toxic-act1-dps-zoomed.webp"
    // ImageCache builds: https://wtfdig.info/{Path}

    /// <summary>
    /// Runtime-only: resolved local file path to the cached image on disk.
    /// Not saved to config — rebuilt at load time.
    /// </summary>
    [JsonIgnore]
    public string? CachedPath { get; set; }

    /// <summary>
    /// Runtime-only: true if the last download/load attempt failed.
    /// Triggers a "image unavailable" placeholder in the overlay.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public bool LoadFailed { get; set; } = false;
}

// =============================================================================
// STRAT ENTRY — one trigger event
// =============================================================================

/// <summary>
/// One trigger event that displays an image on screen.
/// Each entry holds a dictionary of images keyed by variant ID,
/// so different groups can see different strat images for the same mechanic.
/// </summary>
[Serializable]
public class StratEntry
{
    /// <summary>Unique identifier. Never changes once created.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable name shown in the Config UI and on the overlay caption.</summary>
    public string Label { get; set; } = "";

    /// <summary>What kind of event fires this entry.</summary>
    public TriggerType TriggerType { get; set; } = TriggerType.Timeline;

    /// <summary>
    /// Seconds into the fight at which this fires.
    /// Only used when TriggerType = Timeline.
    /// </summary>
    public float TriggerTime { get; set; } = 0f;

    /// <summary>
    /// The ability/action ID to listen for.
    /// Only used when TriggerType = BossAction.
    /// Use the Debug tab's Cast Listener to find the right ID in-game.
    /// </summary>
    public uint AbilityId { get; set; } = 0;

    /// <summary>
    /// Which part of the ability lifecycle fires the image.
    /// Only used when TriggerType = BossAction.
    /// </summary>
    public CastPhase CastPhase { get; set; } = CastPhase.OnCastStart;

    /// <summary>
    /// Images per variant. Key is the StratVariant.Id, or "default" for the fallback.
    /// At runtime, the engine resolves which image to show using the active variant
    /// with a fallback to "default".
    /// </summary>
    public Dictionary<string, StratImage> Images { get; set; } = new();

    /// <summary>
    /// Only show this entry to players of this role.
    /// All = show to everyone.
    /// </summary>
    public TargetRole RoleFilter { get; set; } = TargetRole.All;

    /// <summary>
    /// Show the image this many seconds BEFORE TriggerTime.
    /// Gives players prep time. Default 3s.
    /// </summary>
    public float PreShowSeconds { get; set; } = 3f;

    /// <summary>
    /// How long (seconds) the image stays on screen after appearing.
    /// 0 = stays until manually dismissed.
    /// </summary>
    public float DisplayDuration { get; set; } = 8f;

    /// <summary>Whether this entry is active. Disabled entries are never triggered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which strat image to show for THIS entry specifically.
    /// Matches a key in the Images dictionary (e.g. "Hector", "Toxic", "default").
    /// Null or missing key = fall back to "default".
    /// This lets each mechanic independently use a different named strat.
    /// </summary>
    public string ActiveStratKey { get; set; } = "default";

    /// <summary>
    /// Optional short text shown as a caption below the image on screen.
    /// Good for quick reminders like "NE safe" or "Spread → Stack".
    /// </summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// Convenience: returns trigger time formatted as M:SS for display.
    /// Not saved — computed on the fly.
    /// </summary>
    [JsonIgnore]
    public string FormattedTime
    {
        get
        {
            int m = (int)(TriggerTime / 60f);
            int s = (int)(TriggerTime % 60f);
            return $"{m}:{s:00}";
        }
    }
}
