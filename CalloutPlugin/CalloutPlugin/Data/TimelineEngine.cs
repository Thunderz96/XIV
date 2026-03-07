// =============================================================================
// TimelineEngine.cs — The Brain: Tracks Combat Time & Fires Callouts
// =============================================================================
//
// This is the runtime engine that:
// 1. Detects when you enter combat (via Dalamud's ICondition service)
// 2. Starts a stopwatch from combat start
// 3. Checks each frame if any timeline entries should fire
// 4. Notifies the overlay system when a callout is triggered
//
// DESIGN:
// The engine doesn't draw anything — it just tracks time and raises events.
// The overlay window subscribes to those events and handles the visuals.
// This separation means we can change how alerts look without touching
// the timing logic, and vice versa.
//
// COMBAT DETECTION:
// Dalamud's ICondition service provides a set of boolean flags about
// the player's current state. One of those flags is "InCombat".
// We watch for the transition from false→true to detect pull start,
// and true→false to detect combat end.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace CalloutPlugin.Data;

/// <summary>
/// Event data passed when a callout fires.
/// The overlay subscribes to this to know what to display.
/// </summary>
public class CalloutEventArgs : EventArgs
{
    public required TimelineEntry Entry { get; init; }
    public required float CurrentFightTime { get; init; }

    /// <summary>
    /// How many seconds until the actual trigger time.
    /// Positive = countdown in progress, 0 or negative = it's NOW.
    /// </summary>
    public required float SecondsUntilTrigger { get; init; }
}

/// <summary>
/// The runtime engine that drives timeline playback during combat.
/// </summary>
public class TimelineEngine : IDisposable
{
    private readonly ICondition Condition;
    private readonly IClientState ClientState;
    private readonly IFramework Framework;
    private readonly IPluginLog Log;

    // ---- State ----

    /// <summary>The currently loaded/active timeline (null if none).</summary>
    private FightTimeline? activeTimeline;

    /// <summary>High-precision stopwatch for tracking fight duration.</summary>
    private readonly Stopwatch fightStopwatch = new();

    /// <summary>Was the player in combat last frame? Used to detect transitions.</summary>
    private bool wasInCombat = false;

    /// <summary>
    /// Tracks which entries have already fired their callout this fight.
    /// Keyed by entry ID. Reset when combat starts.
    /// We track pre-alert separately from the main trigger.
    /// </summary>
    private readonly HashSet<string> firedEntries = new();
    private readonly HashSet<string> firedPreAlerts = new();

    // ---- Events ----

    /// <summary>
    /// Raised when a callout should be displayed (pre-alert countdown starts).
    /// The overlay subscribes to this.
    /// </summary>
    public event EventHandler<CalloutEventArgs>? OnCalloutTriggered;

    /// <summary>
    /// Raised when combat starts (for UI feedback).
    /// </summary>
    public event EventHandler? OnCombatStarted;

    /// <summary>
    /// Raised when combat ends.
    /// </summary>
    public event EventHandler? OnCombatEnded;

    // ---- Public Properties ----

    /// <summary>Is the engine currently tracking a fight?</summary>
    public bool IsRunning => fightStopwatch.IsRunning;

    /// <summary>Current elapsed fight time in seconds.</summary>
    public float CurrentTime => (float)fightStopwatch.Elapsed.TotalSeconds;

    /// <summary>The currently active timeline name (for display).</summary>
    public string? ActiveTimelineName => activeTimeline?.Name;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public TimelineEngine(
        ICondition condition,
        IClientState clientState,
        IFramework framework,
        IPluginLog log)
    {
        Condition = condition;
        ClientState = clientState;
        Framework = framework;
        Log = log;

        // Subscribe to the per-frame update to check combat state and timers
        Framework.Update += OnFrameworkUpdate;

        Log.Debug("TimelineEngine initialized.");
    }

    // =========================================================================
    // TIMELINE MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Sets the active timeline. Call this when the user selects a timeline,
    /// or when auto-detection loads one based on territory.
    /// </summary>
    public void LoadTimeline(FightTimeline? timeline)
    {
        activeTimeline = timeline;
        Reset();
        Log.Info($"Timeline loaded: {timeline?.Name ?? "(none)"}");
    }

    /// <summary>
    /// Resets the engine state (clears timers and fired entries).
    /// Called on combat start and when loading a new timeline.
    /// </summary>
    public void Reset()
    {
        fightStopwatch.Reset();
        firedEntries.Clear();
        firedPreAlerts.Clear();
    }

    /// <summary>
    /// Manually start the timer (for testing or manual trigger via command).
    /// </summary>
    public void ManualStart()
    {
        Reset();
        fightStopwatch.Start();
        OnCombatStarted?.Invoke(this, EventArgs.Empty);
        Log.Info("Timeline manually started.");
    }

    /// <summary>
    /// Manually stop the timer.
    /// </summary>
    public void ManualStop()
    {
        fightStopwatch.Stop();
        OnCombatEnded?.Invoke(this, EventArgs.Empty);
        Log.Info("Timeline manually stopped.");
    }

    // =========================================================================
    // FRAMEWORK UPDATE — Runs every frame
    // =========================================================================
    private void OnFrameworkUpdate(IFramework framework)
    {
        // Don't do anything if not logged in
        if (ClientState.LocalPlayer == null)
            return;

        // ---- Combat Detection ----
        var isInCombat = Condition[ConditionFlag.InCombat];

        // Detect combat START (transition from not-in-combat → in-combat)
        if (isInCombat && !wasInCombat)
        {
            Reset();
            fightStopwatch.Start();
            OnCombatStarted?.Invoke(this, EventArgs.Empty);
            Log.Debug("Combat started — timeline timer running.");
        }
        // Detect combat END (transition from in-combat → not-in-combat)
        else if (!isInCombat && wasInCombat)
        {
            fightStopwatch.Stop();
            OnCombatEnded?.Invoke(this, EventArgs.Empty);
            Log.Debug($"Combat ended at {CurrentTime:F1}s.");
        }

        wasInCombat = isInCombat;

        // ---- Check Timeline Entries ----
        if (!fightStopwatch.IsRunning || activeTimeline == null)
            return;

        var currentTime = CurrentTime;

        foreach (var entry in activeTimeline.Entries)
        {
            if (!entry.Enabled)
                continue;
            // Check if the player's role matches the target role ---
            if (entry.TargetRole != TargetRole.All && ClientState.LocalPlayer != null)
            {
                // Lumina's Role ID mapping: 1 = Tank, 2 = Melee DPS, 3 = Ranged DPS, 4 = Healer
                var roleId = ClientState.LocalPlayer.ClassJob.Value.Role;
                bool roleMatch = entry.TargetRole switch
                {
                    TargetRole.Tank => roleId == 1,
                    TargetRole.Healer => roleId == 5, 
                    TargetRole.DPS => roleId == 2 || roleId == 3 || roleId == 4, 
                    _ => true
                };

                // If it doesn't match, skip processing this entry entirely
                if (!roleMatch) continue;
            }
            // ---------------------------------------------------------------
           // Check if we should start the PRE-ALERT (countdown)
            var preAlertTime = entry.TriggerTime - entry.PreAlertSeconds;
            if (currentTime >= preAlertTime && !firedPreAlerts.Contains(entry.Id))
            {
                firedPreAlerts.Add(entry.Id);

                var args = new CalloutEventArgs
                {
                    Entry = entry,
                    CurrentFightTime = currentTime,
                    SecondsUntilTrigger = entry.TriggerTime - currentTime,
                };

                OnCalloutTriggered?.Invoke(this, args);

                Log.Debug($"Pre-alert fired: \"{entry.CalloutText}\" at {currentTime:F1}s " +
                         $"({args.SecondsUntilTrigger:F1}s until trigger)");
            }
        }
    }

    // =========================================================================
    // DISPOSE
    // =========================================================================
    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        fightStopwatch.Stop();
        Log.Debug("TimelineEngine disposed.");
    }
}
