// =============================================================================
// TimelineView.cs — Visual Timeline Window
// =============================================================================
//
// A separate floating window that renders all timeline entries as a scrollable
// horizontal canvas. Think of it like a DAW (Digital Audio Workstation) track
// view — time flows left to right, each role gets its own row, and a playhead
// moves across the canvas in real time during combat.
//
// RENDERING:
// We use ImGui's DrawList (GetWindowDrawList()) which lets us draw raw lines,
// rectangles, and text at arbitrary pixel positions inside an ImGui window.
// This is different from normal ImGui widgets — we're essentially painting
// directly onto a canvas inside the window.
//
// COORDINATE SYSTEM:
// Everything is relative to the top-left corner of the canvas area.
// Time → X axis:  pixelX = canvasOrigin.X + (triggerTime * pixelsPerSecond) - scrollOffset
// Role → Y axis:  each role gets a fixed-height horizontal track
//
// TRACKS (top to bottom):
//   Row 0: All
//   Row 1: Tank
//   Row 2: Healer
//   Row 3: DPS
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using CalloutPlugin.Data;

namespace CalloutPlugin.Windows;

public class TimelineView : Window, IDisposable
{
    private readonly Plugin Plugin;

    // How wide (in pixels) the left-side label column is
    private const float LabelColumnWidth = 52f;

    // Ruler height at the top of the canvas
    private const float RulerHeight = 20f;

    // Small padding between tracks
    private const float TrackPadding = 2f;

    // Track order — matches TargetRole enum values
    private static readonly (TargetRole Role, string Label)[] Tracks =
    [
        (TargetRole.All,    "All"),
        (TargetRole.Tank,   "Tank"),
        (TargetRole.Healer, "Heal"),
        (TargetRole.DPS,    "DPS"),
    ];

    public TimelineView(Plugin plugin)
        : base("Timeline View##CalloutTimeline",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin = plugin;
        IsOpen = false; // opened manually from main window

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(9999, 9999),
        };
        Size = new Vector2(900, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        var config  = Plugin.Configuration;
        var engine  = Plugin.Engine;

        var timeline = config.Timelines.Count > 0
            ? config.Timelines.Find(t => t.Id == config.SelectedTimelineId)
            : null;

        // ---- Top toolbar ----
        DrawToolbar(config, timeline);

        ImGui.Separator();

        if (timeline == null)
        {
            ImGui.TextDisabled("No timeline selected. Pick one in the main Callout Plugin window.");
            return;
        }

        if (timeline.Entries.Count == 0)
        {
            ImGui.TextDisabled("This timeline has no entries yet.");
            return;
        }

        // ---- Canvas ----
        DrawCanvas(config, engine, timeline);
    }

    // =========================================================================
    // TOOLBAR — zoom, options
    // =========================================================================
    private void DrawToolbar(Configuration config, FightTimeline? timeline)
    {
        // Timeline name
        var name = timeline?.Name ?? "(none selected)";
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), name);
        ImGui.SameLine();

        // Zoom slider
        ImGui.SetNextItemWidth(120);
        var zoom = config.TimelinePixelsPerSecond;
        if (ImGui.SliderFloat("Zoom##tlzoom", ref zoom, 2f, 40f, "%.0f px/s"))
        {
            config.TimelinePixelsPerSecond = zoom;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Horizontal zoom: pixels per second.\nMore = zoomed in, fewer = more of the fight visible.");

        ImGui.SameLine();

        // Track height slider
        ImGui.SetNextItemWidth(90);
        var th = config.TimelineTrackHeight;
        if (ImGui.SliderFloat("Height##tlheight", ref th, 16f, 64f, "%.0f"))
        {
            config.TimelineTrackHeight = th;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Height of each role track in pixels.");

        ImGui.SameLine();

        // Toggle buttons
        // C# properties can't be passed by ref directly, so we copy to a local,
        // pass that, then write back if changed. ToggleButton returns true on click.
        var showLabels    = config.TimelineShowLabels;
        var showPreAlert  = config.TimelineShowPreAlertBlocks;
        var showPlayhead  = config.TimelineShowPlayhead;
        if (ToggleButton("Labels",    ref showLabels,    config)) config.TimelineShowLabels         = showLabels;
        ImGui.SameLine();
        if (ToggleButton("Pre-alert", ref showPreAlert,  config)) config.TimelineShowPreAlertBlocks = showPreAlert;
        ImGui.SameLine();
        if (ToggleButton("Playhead",  ref showPlayhead,  config)) config.TimelineShowPlayhead       = showPlayhead;

        // Color pickers (small, inline)
        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();
        SmallColorPicker("All##tlcAll",    config.TimelineColorAll,    config);
        ImGui.SameLine();
        SmallColorPicker("Tnk##tlcTank",   config.TimelineColorTank,   config);
        ImGui.SameLine();
        SmallColorPicker("Hel##tlcHeal",   config.TimelineColorHealer, config);
        ImGui.SameLine();
        SmallColorPicker("DPS##tlcDPS",    config.TimelineColorDPS,    config);
    }

    // =========================================================================
    // CANVAS — the main drawing area
    // =========================================================================
    private void DrawCanvas(Configuration config, TimelineEngine engine, FightTimeline timeline)
    {
        var drawList   = ImGui.GetWindowDrawList();
        var canvasPos  = ImGui.GetCursorScreenPos();   // top-left of our drawing area
        var canvasSize = ImGui.GetContentRegionAvail(); // remaining space in window

        float pps        = config.TimelinePixelsPerSecond;
        float trackH     = config.TimelineTrackHeight;
        float totalTrackH = (trackH + TrackPadding) * Tracks.Length;

        // Work out how long the timeline is (last trigger time + a small margin)
        float maxTime = 10f;
        foreach (var e in timeline.Entries)
            if (e.TriggerTime > maxTime) maxTime = e.TriggerTime;
        maxTime += 10f;

        float contentWidth  = LabelColumnWidth + maxTime * pps;
        float contentHeight = RulerHeight + totalTrackH;

        // Create an invisible dummy widget the full content size so ImGui
        // gives us scrollbars automatically. We then draw on top of it.
        ImGui.BeginChild("##tlcanvas", canvasSize, false,
            ImGuiWindowFlags.HorizontalScrollbar);

        // Re-grab these INSIDE the child window
        canvasPos  = ImGui.GetCursorScreenPos();
        drawList   = ImGui.GetWindowDrawList();

        // Reserve space for our custom drawing
        ImGui.Dummy(new Vector2(contentWidth, contentHeight));

        float scrollX = ImGui.GetScrollX();

        // ---- Background ----
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.10f, 1f));
        drawList.AddRectFilled(canvasPos,
            new Vector2(canvasPos.X + contentWidth, canvasPos.Y + contentHeight), bgColor);

        // ---- Track backgrounds (alternating rows) ----
        for (int i = 0; i < Tracks.Length; i++)
        {
            float trackY = canvasPos.Y + RulerHeight + i * (trackH + TrackPadding);
            var rowColor = (i % 2 == 0)
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.14f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.10f, 0.12f, 1f));
            drawList.AddRectFilled(
                new Vector2(canvasPos.X, trackY),
                new Vector2(canvasPos.X + contentWidth, trackY + trackH),
                rowColor);

            // Track label in the left column
            var labelColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.65f, 1f));
            drawList.AddText(
                new Vector2(canvasPos.X + 4, trackY + trackH * 0.5f - 7f),
                labelColor, Tracks[i].Label);
        }

        // ---- Ruler (time axis) ----
        DrawRuler(drawList, canvasPos, contentWidth, pps, maxTime);

        // ---- Vertical grid lines (every 10s minor, every 60s major) ----
        DrawGridLines(drawList, canvasPos, contentWidth, contentHeight, pps, maxTime);

        // ---- Entries ----
        foreach (var entry in timeline.Entries)
        {
            if (!entry.Enabled) continue;
            DrawEntry(drawList, canvasPos, config, entry, trackH, pps);
        }

        // ---- Playhead ----
        if (config.TimelineShowPlayhead && engine.IsRunning)
        {
            float px = canvasPos.X + LabelColumnWidth + engine.CurrentTime * pps;
            var phColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));
            drawList.AddLine(
                new Vector2(px, canvasPos.Y),
                new Vector2(px, canvasPos.Y + contentHeight),
                phColor, 2f);
            // Small triangle at top
            drawList.AddTriangleFilled(
                new Vector2(px - 5, canvasPos.Y),
                new Vector2(px + 5, canvasPos.Y),
                new Vector2(px,     canvasPos.Y + 10),
                phColor);
        }

        ImGui.EndChild();
    }

    // =========================================================================
    // RULER
    // =========================================================================
    private static void DrawRuler(ImDrawListPtr dl, Vector2 origin, float width,
                                   float pps, float maxTime)
    {
        var rulerBg  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 1f));
        var tickCol  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.55f, 1f));
        var labelCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.9f, 1f));

        dl.AddRectFilled(origin, new Vector2(origin.X + width, origin.Y + RulerHeight), rulerBg);

        // Decide tick interval based on zoom — every 5s when zoomed out, every 1s when zoomed in
        float tickInterval = pps >= 15f ? 1f : pps >= 6f ? 5f : 10f;

        for (float t = 0; t <= maxTime; t += tickInterval)
        {
            float x = origin.X + LabelColumnWidth + t * pps;
            bool isMajor = (t % 60f < 0.01f);
            float tickH  = isMajor ? RulerHeight : RulerHeight * 0.5f;
            dl.AddLine(new Vector2(x, origin.Y + RulerHeight - tickH),
                       new Vector2(x, origin.Y + RulerHeight), tickCol, isMajor ? 2f : 1f);

            // Label — show M:SS for major ticks (60s) or just seconds for minor
            if (isMajor || pps >= 6f)
            {
                var label = FormatTime(t);
                dl.AddText(new Vector2(x + 2, origin.Y + 2), labelCol, label);
            }
        }
    }

    // =========================================================================
    // GRID LINES
    // =========================================================================
    private static void DrawGridLines(ImDrawListPtr dl, Vector2 origin, float width,
                                       float height, float pps, float maxTime)
    {
        var minor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.22f, 1f));
        var major = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, 1f));

        float tickInterval = pps >= 15f ? 1f : pps >= 6f ? 5f : 10f;

        for (float t = tickInterval; t <= maxTime; t += tickInterval)
        {
            float x    = origin.X + LabelColumnWidth + t * pps;
            bool isMaj = (t % 60f < 0.01f);
            dl.AddLine(new Vector2(x, origin.Y + RulerHeight),
                       new Vector2(x, origin.Y + height),
                       isMaj ? major : minor, isMaj ? 1.5f : 1f);
        }
    }

    // =========================================================================
    // ENTRY BLOCK
    // =========================================================================
    private void DrawEntry(ImDrawListPtr dl, Vector2 origin, Configuration config,
                            TimelineEntry entry, float trackH, float pps)
    {
        // Work out which track row this entry lives on
        int trackIndex = entry.TargetRole switch
        {
            TargetRole.Tank   => 1,
            TargetRole.Healer => 2,
            TargetRole.DPS    => 3,
            _                 => 0,
        };

        float trackY  = origin.Y + RulerHeight + trackIndex * (trackH + TrackPadding);
        float triggerX = origin.X + LabelColumnWidth + entry.TriggerTime * pps;

        // Pick color based on role
        float[] roleColorArr = entry.TargetRole switch
        {
            TargetRole.Tank   => config.TimelineColorTank,
            TargetRole.Healer => config.TimelineColorHealer,
            TargetRole.DPS    => config.TimelineColorDPS,
            _                 => config.TimelineColorAll,
        };
        var roleColor = new Vector4(roleColorArr[0], roleColorArr[1], roleColorArr[2], 1f);
        uint colSolid  = ImGui.ColorConvertFloat4ToU32(roleColor);
        uint colDim    = ImGui.ColorConvertFloat4ToU32(roleColor with { W = 0.3f });
        uint colBorder = ImGui.ColorConvertFloat4ToU32(roleColor with { W = 0.8f });

        float blockTop    = trackY + TrackPadding;
        float blockBottom = trackY + trackH - TrackPadding;

        // ---- Pre-alert window block ----
        if (config.TimelineShowPreAlertBlocks && entry.PreAlertSeconds > 0)
        {
            float preX = triggerX - entry.PreAlertSeconds * pps;
            dl.AddRectFilled(
                new Vector2(preX, blockTop),
                new Vector2(triggerX, blockBottom),
                colDim, 2f);
            dl.AddRect(
                new Vector2(preX, blockTop),
                new Vector2(triggerX, blockBottom),
                colBorder, 2f, ImDrawFlags.None, 1f);
        }

        // ---- Display-duration block (after trigger) ----
        if (entry.DisplayDuration > 0)
        {
            float durX = triggerX + entry.DisplayDuration * pps;
            dl.AddRectFilled(
                new Vector2(triggerX, blockTop),
                new Vector2(durX, blockBottom),
                ImGui.ColorConvertFloat4ToU32(roleColor with { W = 0.15f }), 2f);
        }

        // ---- Trigger marker (bright vertical line + diamond) ----
        dl.AddLine(
            new Vector2(triggerX, blockTop),
            new Vector2(triggerX, blockBottom),
            colSolid, 2.5f);

        // Diamond at the trigger point (mid-height)
        float midY = (blockTop + blockBottom) * 0.5f;
        float d    = 4f;
        dl.AddQuadFilled(
            new Vector2(triggerX,     midY - d),
            new Vector2(triggerX + d, midY),
            new Vector2(triggerX,     midY + d),
            new Vector2(triggerX - d, midY),
            colSolid);

        // ---- Label ----
        if (config.TimelineShowLabels)
        {
            var labelCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));
            dl.AddText(
                new Vector2(triggerX + 6f, blockTop + (trackH - TrackPadding * 2 - 13f) * 0.5f),
                labelCol, entry.CalloutText);
        }

        // ---- Tooltip on hover ----
        var mousePos = ImGui.GetMousePos();
        float hoverLeft  = config.TimelineShowPreAlertBlocks
            ? triggerX - entry.PreAlertSeconds * pps : triggerX - 4f;
        float hoverRight = triggerX + Math.Max(entry.DisplayDuration * pps, 8f);

        if (mousePos.X >= hoverLeft && mousePos.X <= hoverRight &&
            mousePos.Y >= blockTop  && mousePos.Y <= blockBottom)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(roleColor, entry.CalloutText);
            ImGui.TextDisabled($"Trigger: {entry.FormattedTime}");
            ImGui.TextDisabled($"Pre-alert: {entry.PreAlertSeconds:F0}s  |  Display: {entry.DisplayDuration:F0}s");
            ImGui.TextDisabled($"Role: {entry.TargetRole}");
            ImGui.EndTooltip();
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>Formats seconds as M:SS.</summary>
    private static string FormatTime(float seconds)
    {
        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return m > 0 ? $"{m}:{s:00}" : $"{s}s";
    }

    /// <summary>A small labeled toggle button. Returns true if clicked.</summary>
    private static bool ToggleButton(string label, ref bool value, Configuration config)
    {
        if (value)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1f));
        else
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1f));

        bool clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor();

        if (clicked)
        {
            value = !value;
            config.Save();
        }
        return clicked;
    }

    /// <summary>
    /// A tiny inline color picker swatch. Uses a small colored button that opens
    /// a full ColorEdit popup when clicked.
    /// </summary>
    private static void SmallColorPicker(string label, float[] color, Configuration config)
    {
        var v = new Vector4(color[0], color[1], color[2], color[3]);
        ImGui.PushStyleColor(ImGuiCol.Button,        v with { W = 0.9f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, v with { W = 1.0f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  v with { W = 1.0f });

        // Tiny colored square that opens the popup
        if (ImGui.SmallButton($"  ##{label}"))
            ImGui.OpenPopup($"##colorpop_{label}");
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(label.Split('#')[0]); // show label without ## suffix

        if (ImGui.BeginPopup($"##colorpop_{label}"))
        {
            if (ImGui.ColorPicker4(label, ref v,
                ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.PickerHueBar))
            {
                color[0] = v.X; color[1] = v.Y; color[2] = v.Z; color[3] = v.W;
                config.Save();
            }
            ImGui.EndPopup();
        }
    }
}
