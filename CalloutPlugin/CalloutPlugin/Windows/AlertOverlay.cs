// =============================================================================
// AlertOverlay.cs — The On-Screen Callout Display
// =============================================================================
//
// A transparent full-screen ImGui window that draws callout alerts.
// Subscribes to TimelineEngine events and renders text directly using
// ImGui.SetCursorPos + ImGui.TextColored — no ForegroundDrawList needed.
//
// WHY NOT ForegroundDrawList?
// ForegroundDrawList.AddText bypasses the window system entirely and requires
// manual font size management that isn't well-supported in Dalamud's bindings.
// Using ImGui widget calls (SetCursorPos, TextColored) inside the window is
// simpler, more reliable, and supports font scaling correctly.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Speech.Synthesis;

using CalloutPlugin.Data;

namespace CalloutPlugin.Windows;

/// <summary>
/// Runtime state for one active alert being displayed on screen.
/// Not saved — purely used for rendering each frame.
/// </summary>
public class ActiveAlert
{
    public required TimelineEntry Entry { get; init; }
    public required float FiredAtFightTime { get; init; }
    public float SecondsUntilTrigger { get; set; }
    public float ElapsedSinceCreation { get; set; } = 0f;
    public bool HasTriggered { get; set; } = false;

    /// <summary>Total on-screen lifetime = pre-alert countdown + post-trigger display.</summary>
    public float TotalLifetime => Entry.PreAlertSeconds + Entry.DisplayDuration;

    /// <summary>True when the alert has been on screen longer than its total lifetime.</summary>
    public bool IsExpired => ElapsedSinceCreation >= TotalLifetime;
}

/// <summary>
/// Transparent full-screen overlay that renders callout alerts.
/// </summary>
public class AlertOverlay : Window, IDisposable
{
    private readonly Plugin Plugin;
    private readonly List<ActiveAlert> activeAlerts = new();
    private DateTime lastFrameTime = DateTime.UtcNow;
    private readonly SpeechSynthesizer tts = new();

    public AlertOverlay(Plugin plugin)
        : base("##CalloutAlertOverlay",
               ImGuiWindowFlags.NoDecoration |
               ImGuiWindowFlags.NoBackground |
               ImGuiWindowFlags.NoInputs |
               ImGuiWindowFlags.NoNav |
               ImGuiWindowFlags.NoFocusOnAppearing |
               ImGuiWindowFlags.NoBringToFrontOnFocus |
               ImGuiWindowFlags.NoSavedSettings |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin = plugin;
        IsOpen = true;

        // Force the window to always be exactly viewport size.
        // This ensures Draw() is called every frame even with no ImGui widget content,
        // since ImGui won't cull a window that has an explicitly set size.
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;

        tts.SetOutputToDefaultAudioDevice();
        Plugin.Engine.OnCalloutTriggered += OnCalloutTriggered;
        Plugin.Engine.OnCombatEnded += OnCombatEnded;
    }

    public void Dispose()
    {
        Plugin.Engine.OnCalloutTriggered -= OnCalloutTriggered;
        Plugin.Engine.OnCombatEnded -= OnCombatEnded;
        tts.Dispose();
    }
    
    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnCalloutTriggered(object? sender, CalloutEventArgs e)
    {
        activeAlerts.Add(new ActiveAlert
        {
            Entry = e.Entry,
            FiredAtFightTime = e.CurrentFightTime,
            SecondsUntilTrigger = e.SecondsUntilTrigger,
        });
    }

    private void OnCombatEnded(object? sender, EventArgs e)
    {
        activeAlerts.Clear();
    }

    // =========================================================================
    // PRE-DRAW — Set window to fill the screen every frame
    // =========================================================================
    public override void PreDraw()
    {
        // Pin this window to cover the entire screen.
        // PreDraw() runs before Draw() each frame and is the correct place
        // to call SetNextWindow* functions in Dalamud's Window system.
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos;
        Size = viewport.Size;
    }

    // =========================================================================
    // DRAW — Called every frame by Dalamud's WindowSystem
    // =========================================================================
    public override void Draw()
    {
        // Always update lastFrameTime each frame so delta doesn't spike
        // when there are no alerts (which would cause a huge jump when one appears)
        var now = DateTime.UtcNow;
        var deltaTime = (float)(now - lastFrameTime).TotalSeconds;
        lastFrameTime = now;

        if (activeAlerts.Count == 0)
            return;

        var viewport = ImGui.GetMainViewport();
        var screenSize = viewport.Size;

        // Starting Y: AlertVerticalPosition is 0.0 (top) to 1.0 (bottom)
        // Default 0.3 = 30% down the screen
        var yOffset = screenSize.Y * Plugin.Configuration.AlertVerticalPosition;

        // Walk backwards so we can remove expired entries safely
        for (int i = activeAlerts.Count - 1; i >= 0; i--)
        {
            var alert = activeAlerts[i];

            alert.ElapsedSinceCreation += deltaTime;
            alert.SecondsUntilTrigger  -= deltaTime;

            if (alert.SecondsUntilTrigger <= 0 && !alert.HasTriggered)
            {
                alert.HasTriggered = true;
                
                // If this entry has the Sound flag, speak the callout text!
                if ((alert.Entry.AlertTypes & AlertType.Sound) != 0)
                {
                    tts.SpeakAsync(alert.Entry.CalloutText);
                }
            }

            if (alert.IsExpired)
            {
                activeAlerts.RemoveAt(i);
                continue;
            }

            DrawAlert(alert, screenSize, ref yOffset);
        }
    }

    // =========================================================================
    // DRAW ALERT — Renders a single alert using ImGui widget calls
    // =========================================================================
    private void DrawAlert(ActiveAlert alert, Vector2 screenSize, ref float yOffset)
    {
        var config = Plugin.Configuration;

        // ---- Build display text ----
        string displayText;
        if (alert.SecondsUntilTrigger > 0)
        {
            // Countdown phase: "Reprisal in 3..."
            var countdown = (int)Math.Ceiling(alert.SecondsUntilTrigger);
            displayText = $"{alert.Entry.CalloutText} in {countdown}...";
        }
        else
        {
            // Triggered phase: ">>> REPRISAL <<<"
            displayText = $">>> {alert.Entry.CalloutText.ToUpperInvariant()} <<<";
        }

        // ---- Calculate alpha (opacity) ----
        // Start fully opaque, then fade out in the last second of display
        var timeAfterTrigger = alert.ElapsedSinceCreation - alert.Entry.PreAlertSeconds;
        var alpha = 1f;

        if (timeAfterTrigger > 0)
        {
            // Fade starts 1 second before the alert expires
            var fadeStart = alert.Entry.DisplayDuration - 1f;
            if (timeAfterTrigger > fadeStart && fadeStart >= 0f)
            {
                alpha = Math.Max(0f, 1f - (timeAfterTrigger - fadeStart));
            }

            // Pulse/flash alpha when it first triggers
            if (timeAfterTrigger < 1f)
            {
                var flash = (float)(Math.Sin(timeAfterTrigger * 8f * Math.PI) * 0.3 + 0.7);
                alpha *= flash;
            }
        }

        // ---- Pick color ----
        var colorArr = alert.Entry.Color ?? config.DefaultAlertColor;
        var color = new Vector4(colorArr[0], colorArr[1], colorArr[2], alpha);

        // ---- Push the large pre-baked alert font ----
        // IFontHandle.Push() tells ImGui to use this font for all subsequent text
        // calls until we call Pop(). The font was baked at 36px in Plugin.cs, so
        // it renders sharp at its native size — no blurry upscaling.
        // We wrap everything in a using block so Pop() is always called, even if
        // an exception occurs. This is the standard Dalamud pattern for fonts.
        using (Plugin.AlertFont.Push())
        {
            var textSize = ImGui.CalcTextSize(displayText);
            var xPos = (screenSize.X - textSize.X) * 0.5f;

            // ---- Shadow pass ----
            // Draw the same text slightly offset in 4 directions with a dark color.
            // This creates a clean outline effect that makes text readable on any background.
            var shadowColor = new Vector4(0f, 0f, 0f, alpha * 0.75f);
            foreach (var (ox, oy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2) })
            {
                ImGui.SetCursorPos(new Vector2(xPos + ox, yOffset + oy));
                ImGui.TextColored(shadowColor, displayText);
            }

            // ---- Main text pass ----
            ImGui.SetCursorPos(new Vector2(xPos, yOffset));
            ImGui.TextColored(color, displayText);

            // Advance Y so the next alert draws below this one
            yOffset += textSize.Y + 10f;
        }
    }
}
