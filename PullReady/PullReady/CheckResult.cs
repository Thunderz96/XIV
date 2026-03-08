// =============================================================================
// CheckResult.cs — Data model for a single readiness check result
// =============================================================================
//
// Every check (food, repairs, items, etc.) produces one CheckResult.
// The UI iterates this list and renders each row with the correct color icon.
//
// STATUS MEANINGS:
//   Pass    — everything looks good, show green ✓
//   Warn    — something is marginal (e.g. food < 10 min left), show yellow ⚠
//   Fail    — something is wrong (no food, broken gear), show red ✗
//   Skip    — check is disabled in config, don't show it at all
// =============================================================================

namespace PullReady;

/// <summary>The outcome of a single readiness check.</summary>
public enum CheckStatus
{
    Pass,   // Green — all good
    Warn,   // Yellow — marginal but not broken
    Fail,   // Red — missing or broken
    Skip    // Hidden — check was disabled in config
}

/// <summary>
/// Holds the result of one readiness check.
/// Produced by ICheck.Run() and consumed by the HUD window.
/// </summary>
public class CheckResult
{
    /// <summary>Short display name shown on the left of each row, e.g. "Well Fed".</summary>
    public string Name { get; init; } = "";

    /// <summary>Pass / Warn / Fail / Skip — controls the row's color and icon.</summary>
    public CheckStatus Status { get; init; } = CheckStatus.Skip;

    /// <summary>
    /// Extra detail shown to the right of the status icon.
    /// Examples: "+3% EXP · 42 min left", "Lowest: Pants 48%", "x3 in bag"
    /// Keep it short — one line max.
    /// </summary>
    public string Detail { get; init; } = "";

    // -------------------------------------------------------------------------
    // Static factory helpers so callers don't have to write "new CheckResult {}"
    // every time. Makes the check code much more readable.
    // -------------------------------------------------------------------------

    public static CheckResult Pass(string name, string detail = "")
        => new() { Name = name, Status = CheckStatus.Pass, Detail = detail };

    public static CheckResult Warn(string name, string detail = "")
        => new() { Name = name, Status = CheckStatus.Warn, Detail = detail };

    public static CheckResult Fail(string name, string detail = "")
        => new() { Name = name, Status = CheckStatus.Fail, Detail = detail };

    public static CheckResult Skip(string name)
        => new() { Name = name, Status = CheckStatus.Skip };
}
