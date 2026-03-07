// =============================================================================
// CooldownTracker.cs — Persistent HUD: Upcoming Callouts Sidebar
// =============================================================================
//
// A small always-on-screen panel that shows your next N upcoming callouts
// during combat. Unlike the AlertOverlay (which fires big flashes reactively),
// this is a proactive "what's coming up" list — like a raid CD timeline you
// can glance at any time.
//
// DESIGN:
// - Reads directly from the engine's active timeline + CurrentTime each frame
// - No event subscriptions needed — purely polling
// - Drawn using ImGui's DrawList for full pixel control
// - Draggable: the user holds the panel and drags it anywhere on screen
//   (position saved to config as 0-1 normalized so it survives resolution changes)
// - Only visible when the engine is running (combat is active)
//
// LAYOUT (top to bottom):
//   [ Fight Timer ]       ← optional, shows elapsed combat time
//   ─────────────────
//   0:45  Reprisal        ← next callout (bright)
//   1:15  Dark Miss...    ← subsequent callouts (dimmer)
//   1:55  Reprisal
//   2:30  Dark Miss...
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Game.ClientState.Objects.Enums;

using CalloutPlugin.Data;

namespace CalloutPlugin.Windows;

public class CooldownTracker : Window, IDisposable
{
    private readonly Plugin Plugin;

    // Panel dimensions — computed each frame based on content
    private const float PanelWidth     = 200f;
    private const float RowHeight      = 20f;
    private const float Padding        = 6f;
    private const float TimerBarHeight = 22f;

    // When true, NoInputs is removed so the panel accepts mouse events for dragging.
    // Toggled from the Settings tab. Automatically disables itself on combat start.
    public bool IsEditMode { get; set; } = false;

    private bool    isDragging  = false;
    private Vector2 dragOffset  = Vector2.Zero;

    public CooldownTracker(Plugin plugin)
        : base("##CooldownTracker",
               ImGuiWindowFlags.NoDecoration |
               ImGuiWindowFlags.NoBackground |
               ImGuiWindowFlags.NoNav |
               ImGuiWindowFlags.NoFocusOnAppearing |
               ImGuiWindowFlags.NoBringToFrontOnFocus |
               ImGuiWindowFlags.NoSavedSettings |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin    = plugin;
        IsOpen    = true;
        SizeCondition     = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    // =========================================================================
    // PRE-DRAW — pin window to full viewport, toggle NoInputs based on edit mode
    // =========================================================================
    public override void PreDraw()
    {
        var vp = ImGui.GetMainViewport();
        Position = vp.Pos;
        Size     = vp.Size;

        // NoInputs passes all mouse/keyboard events straight through to the game.
        // We NEED this during normal play so clicks don't get eaten by the overlay.
        // We REMOVE it in edit mode so the panel can receive mouse events for dragging.
        if (IsEditMode)
            Flags &= ~ImGuiWindowFlags.NoInputs;  // remove NoInputs — allow mouse
        else
            Flags |= ImGuiWindowFlags.NoInputs;   // restore NoInputs — pass through
    }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        var config = Plugin.Configuration;
        var engine = Plugin.Engine;

        if (!config.CooldownTrackerEnabled)
            return;

        // In edit mode, show a preview panel so you can position it without being in combat
        if (IsEditMode)
        {
            DrawEditModePreview(config);
            return;
        }

        // Normal mode — only show during active combat with an enabled timeline loaded
        if (!engine.IsRunning)
            return;

        var timeline = config.Timelines.FirstOrDefault(t => t.Id == config.SelectedTimelineId);
        if (timeline == null || !timeline.Enabled)
            return;

        var upcoming   = GetUpcoming(timeline, engine, config);
        var vp         = ImGui.GetMainViewport();
        var screenSize = vp.Size;
        float panelX   = config.CooldownTrackerX * screenSize.X;
        float panelY   = config.CooldownTrackerY * screenSize.Y;
        float timerH   = config.CooldownTrackerShowTimer ? TimerBarHeight + Padding : 0f;
        float panelH   = Padding + timerH + Math.Max(1, upcoming.Count) * RowHeight + Padding;

        var panelPos  = new Vector2(panelX, panelY);
        var panelSize = new Vector2(PanelWidth, panelH);
        var dl        = ImGui.GetWindowDrawList();

        DrawBackground(dl, panelPos, panelSize, config.CooldownTrackerBgAlpha);

        float y = panelY + Padding;
        if (config.CooldownTrackerShowTimer)
        {
            DrawTimer(dl, panelPos.X, ref y, engine.CurrentTime);
            y += Padding;
        }
        DrawUpcomingList(dl, panelPos.X, ref y, upcoming, engine.CurrentTime, config);
    }

    // =========================================================================
    // EDIT MODE PREVIEW — shown when repositioning, even outside combat
    // =========================================================================
    private void DrawEditModePreview(Configuration config)
    {
        var vp         = ImGui.GetMainViewport();
        var screenSize = vp.Size;
        float panelX   = config.CooldownTrackerX * screenSize.X;
        float panelY   = config.CooldownTrackerY * screenSize.Y;
        float panelH   = Padding + TimerBarHeight + Padding + config.CooldownTrackerEntryCount * RowHeight + Padding;

        var panelPos  = new Vector2(panelX, panelY);
        var panelSize = new Vector2(PanelWidth, panelH);
        var dl        = ImGui.GetWindowDrawList();

        // Slightly more opaque background so it's clearly visible while positioning
        DrawBackground(dl, panelPos, panelSize, Math.Min(1f, config.CooldownTrackerBgAlpha + 0.2f));

        // Bright orange border to indicate edit mode
        var editBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.6f, 0.1f, 0.9f));
        dl.AddRect(panelPos, panelPos + panelSize, editBorder, 6f, ImDrawFlags.None, 2f);

        // "MOVE ME" label at top
        var labelCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.6f, 0.1f, 1f));
        dl.AddText(new Vector2(panelX + Padding, panelY + Padding), labelCol, "✥ DRAG TO REPOSITION");

        float y = panelY + Padding + TimerBarHeight;

        // Draw Separator
        var sepCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, 0.8f));
        dl.AddLine(new Vector2(panelX + Padding, y), new Vector2(panelX + PanelWidth - Padding, y), sepCol, 1f);
        y += Padding;

        // Placeholder rows
        string[] sampleTexts = ["Reprisal", "Dark Missionary", "Reprisal", "Heart of Light",
                                 "Rampart", "Reprisal", "Dark Missionary", "Feint"];
        string[] sampleTimes = ["0:45", "1:15", "1:55", "2:30", "3:10", "3:45", "4:20", "5:00"];

        var dimCol  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.6f, 0.6f));
        var textCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.7f, 0.65f, 0.6f));

        for (int i = 0; i < config.CooldownTrackerEntryCount; i++)
        {
            float rowY = y + i * RowHeight;
            dl.AddText(new Vector2(panelX + Padding, rowY + 2), dimCol, sampleTimes[i % sampleTimes.Length]);
            dl.AddText(new Vector2(panelX + Padding + 38f, rowY + 2), textCol, sampleTexts[i % sampleTexts.Length]);
        }

        // ---- Handle drag input (only active in edit mode because NoInputs is off) ----
        var mouse = ImGui.GetMousePos();
        bool overPanel = mouse.X >= panelX && mouse.X <= panelX + PanelWidth
                      && mouse.Y >= panelY && mouse.Y <= panelY + panelH;

        if (overPanel && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            isDragging = true;
            dragOffset = mouse - panelPos;
        }

        if (isDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var newPos = mouse - dragOffset;
                config.CooldownTrackerX = Math.Clamp(newPos.X / screenSize.X, 0f, 1f);
                config.CooldownTrackerY = Math.Clamp(newPos.Y / screenSize.Y, 0f, 1f);
            }
            else
            {
                isDragging = false;
                config.Save();
            }
        }
    }

    // =========================================================================
    // GATHER UPCOMING ENTRIES
    // =========================================================================
    private List<TimelineEntry> GetUpcoming(FightTimeline timeline, TimelineEngine engine, Configuration config)
    {
        var currentTime = engine.CurrentTime;

        // Filter to entries that haven't triggered yet (trigger time still in the future)
        // Optionally filter by player role if CooldownTrackerRoleFilter is on
        var query = timeline.Entries
            .Where(e => e.Enabled && e.TriggerTime > currentTime);

        if (config.CooldownTrackerRoleFilter && Plugin.ClientState?.LocalPlayer != null)
        {
            var roleId = Plugin.ClientState.LocalPlayer.ClassJob.Value.Role;
            query = query.Where(e => e.TargetRole == TargetRole.All ||
                (e.TargetRole == TargetRole.Tank   && roleId == 1) ||
                (e.TargetRole == TargetRole.Healer && roleId == 4) ||
                (e.TargetRole == TargetRole.DPS    && (roleId == 2 || roleId == 3)));
        }

        return query
            .OrderBy(e => e.TriggerTime)
            .Take(config.CooldownTrackerEntryCount)
            .ToList();
    }

    // =========================================================================
    // DRAG TO REPOSITION
    // =========================================================================
    private void HandleDrag(Configuration config, Vector2 panelPos, Vector2 panelSize, Vector2 screenSize)
    {
        var mouse = ImGui.GetMousePos();

        bool mouseOverPanel = mouse.X >= panelPos.X && mouse.X <= panelPos.X + panelSize.X
                           && mouse.Y >= panelPos.Y && mouse.Y <= panelPos.Y + panelSize.Y;

        // Start drag on left-click inside panel
        if (mouseOverPanel && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            isDragging = true;
            dragOffset = mouse - panelPos;
        }

        if (isDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                // Move panel to follow mouse, keeping offset so it doesn't snap to corner
                var newPos = mouse - dragOffset;
                config.CooldownTrackerX = Math.Clamp(newPos.X / screenSize.X, 0f, 1f);
                config.CooldownTrackerY = Math.Clamp(newPos.Y / screenSize.Y, 0f, 1f);
            }
            else
            {
                // Mouse released — stop dragging and save position
                isDragging = false;
                config.Save();
            }
        }
    }

    // =========================================================================
    // DRAW BACKGROUND
    // =========================================================================
    private static void DrawBackground(ImDrawListPtr dl, Vector2 pos, Vector2 size, float alpha)
    {
        // Dark semi-transparent rounded rectangle
        var bg     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.05f, 0.08f, alpha));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, alpha * 0.8f));

        dl.AddRectFilled(pos, pos + size, bg, 6f);
        dl.AddRect(pos, pos + size, border, 6f, ImDrawFlags.None, 1f);
    }

    // =========================================================================
    // DRAW FIGHT TIMER
    // =========================================================================
    private static void DrawTimer(ImDrawListPtr dl, float x, ref float y, float currentTime)
    {
        int minutes = (int)(currentTime / 60f);
        int seconds = (int)(currentTime % 60f);
        string timerText = $"{minutes}:{seconds:00}";

        var timerCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.9f, 1.0f, 1f));
        // Center the timer text within the panel
        dl.AddText(new Vector2(x + Padding, y), timerCol, $"⏱ {timerText}");
        y += TimerBarHeight;

        // Thin separator line
        var sepCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, 0.8f));
        dl.AddLine(new Vector2(x + Padding, y), new Vector2(x + PanelWidth - Padding, y), sepCol, 1f);
    }

    // =========================================================================
    // DRAW UPCOMING CALLOUT LIST
    // =========================================================================
    private static void DrawUpcomingList(ImDrawListPtr dl, float x, ref float y,
        List<TimelineEntry> upcoming, float currentTime, Configuration config)
    {
        if (upcoming.Count == 0)
        {
            var doneCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 0.8f));
            dl.AddText(new Vector2(x + Padding, y), doneCol, "No more callouts");
            return;
        }

        for (int i = 0; i < upcoming.Count; i++)
        {
            var entry     = upcoming[i];
            float timeLeft = entry.TriggerTime - currentTime;

            // Time label — M:SS format
            int m = (int)(entry.TriggerTime / 60f);
            int s = (int)(entry.TriggerTime % 60f);
            string timeLabel = $"{m}:{s:00}";

            // "in Xs" countdown for the next entry only
            string countdownLabel = i == 0 ? $"  -{(int)timeLeft}s" : "";

            // Color: first entry is bright (imminent), rest fade progressively
            float brightness = i == 0 ? 1.0f : Math.Max(0.4f, 1.0f - i * 0.18f);

            // Use entry's custom color if set, otherwise white/grey by brightness
            Vector4 entryColor;
            if (entry.Color != null)
                entryColor = new Vector4(entry.Color[0], entry.Color[1], entry.Color[2], brightness);
            else
                entryColor = new Vector4(brightness, brightness, brightness * 0.9f, 1f);

            var col      = ImGui.ColorConvertFloat4ToU32(entryColor);
            var dimCol   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.6f, brightness));
            var urgentCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.4f, 0.4f, 1f));

            float rowY = y + i * RowHeight;

            // Highlight row background if next callout is imminent (< pre-alert window)
            if (i == 0 && timeLeft <= entry.PreAlertSeconds)
            {
                var highlightColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.2f, 0.05f, 0.5f));
                dl.AddRectFilled(
                    new Vector2(x + 2, rowY - 1),
                    new Vector2(x + PanelWidth - 2, rowY + RowHeight - 1),
                    highlightColor, 3f);
            }

            // Time label (dimmer)
            dl.AddText(new Vector2(x + Padding, rowY + 2), dimCol, timeLabel);

            // Callout text — truncate if too long
            string calloutText = entry.CalloutText.Length > 16
                ? entry.CalloutText[..16] + "…"
                : entry.CalloutText;
            dl.AddText(new Vector2(x + Padding + 38f, rowY + 2), col, calloutText);

            // Countdown "in Xs" label for next callout — red if < 3s
            if (i == 0 && countdownLabel.Length > 0)
            {
                var cdColor = timeLeft < 3f ? urgentCol : dimCol;
                dl.AddText(new Vector2(x + PanelWidth - 48f, rowY + 2), cdColor, $"-{(int)timeLeft}s");
            }
        }

        y += upcoming.Count * RowHeight;
    }
}
