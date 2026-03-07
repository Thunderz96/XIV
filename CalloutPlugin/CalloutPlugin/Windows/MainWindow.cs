// =============================================================================
// MainWindow.cs — Timeline Editor & Management UI
// =============================================================================
//
// Three-tab layout:
//   TIMELINES — Create, select, and edit fight timelines and their callout entries
//   SETTINGS  — Global display settings (font size, color, position, defaults)
//   DEBUG     — Live diagnostic info for troubleshooting
// =============================================================================

using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using CalloutPlugin.Data;

namespace CalloutPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    // New-entry scratch fields (not saved, just UI state)
    private float newEntryMinutes = 0;
    private float newEntrySeconds = 0;
    private string newEntryText = "";
    private int newEntryRole = 0; // Maps to the TargetRole enum
    private bool newEntryTTS = false;

    // Font size staging — we only rebuild the font atlas when the user releases
    // the slider, not on every pixel change (which would be very expensive).
    private float pendingFontSize = 36f;
    private bool fontSizeChanged = false;

    public MainWindow(Plugin plugin)
        : base("Callout Plugin##CalloutMain", ImGuiWindowFlags.None)
    {
        Plugin = plugin;
        pendingFontSize = plugin.Configuration.AlertFontSize;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 400),
            MaximumSize = new Vector2(950, 850),
        };
        Size = new Vector2(680, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // =========================================================================
    // DRAW — Top-level layout
    // =========================================================================
    public override void Draw()
    {
        var config = Plugin.Configuration;

        // ---- STATUS BAR (always visible above tabs) ----
        if (Plugin.Engine.IsRunning)
        {
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), $"LIVE — {Plugin.Engine.CurrentTime:F1}s");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"({Plugin.Engine.ActiveTimelineName ?? "No timeline"})");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.SmallButton("Stop")) Plugin.Engine.ManualStop();
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextDisabled("Not in combat");
            ImGui.SameLine();

            var hasTimeline = config.SelectedTimelineId != null &&
                              config.Timelines.Any(t => t.Id == config.SelectedTimelineId);
            if (!hasTimeline)
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton("Start Test");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Select a timeline on the left first.");
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.5f, 0.15f, 1f));
                if (ImGui.SmallButton("Start Test"))
                {
                    var selected = config.Timelines.First(t => t.Id == config.SelectedTimelineId);
                    Plugin.Engine.LoadTimeline(selected);
                    Plugin.Engine.ManualStart();
                }
                ImGui.PopStyleColor();
            }
        }

        ImGui.Separator();

        // ---- TABS ----
        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("Timelines"))
            {
                DrawTimelinesTab(config);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab(config);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug"))
            {
                DrawDebugTab(config);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // =========================================================================
    // TAB: TIMELINES
    // =========================================================================
    private void DrawTimelinesTab(Configuration config)
    {
        ImGui.BeginChild("TimelineList", new Vector2(200, 0), true);
        DrawTimelineList(config);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("EntryEditor", new Vector2(0, 0), true);
        DrawEntryEditor(config);
        ImGui.EndChild();
    }

    private void DrawTimelineList(Configuration config)
    {
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Timelines");
        ImGui.Separator();

        if (ImGui.Button("+ New Timeline", new Vector2(-1, 0)))
        {
            var newTimeline = new FightTimeline { Name = $"Timeline {config.Timelines.Count + 1}", Author = "Thunderz96" };
            config.Timelines.Add(newTimeline);
            config.SelectedTimelineId = newTimeline.Id;
            config.Save();
        }
        ImGui.Spacing();

        // Import / Export Buttons ---
        if (ImGui.Button("Copy to Clipboard"))
        {
            var selected = config.Timelines.FirstOrDefault(t => t.Id == config.SelectedTimelineId);
            if (selected != null)
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(selected, Newtonsoft.Json.Formatting.Indented);
                ImGui.SetClipboardText(json);
            }
        }
        if (ImGui.Button("Paste from Clipboard"))
        {
            try
            {
                var json = ImGui.GetClipboardText();
                var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<FightTimeline>(json);
                if (imported != null)
                {
                    imported.Id = Guid.NewGuid().ToString("N")[..8]; // ensure a brand new ID
                    config.Timelines.Add(imported);
                    config.SelectedTimelineId = imported.Id;
                    config.Save();
                }
            }
            catch { /* Ignore invalid clipboard data */ }
        }
        ImGui.Spacing();

        foreach (var timeline in config.Timelines)
        {
            var isSelected = config.SelectedTimelineId == timeline.Id;
            var label = timeline.Enabled ? timeline.Name : $"[OFF] {timeline.Name}";
            if (ImGui.Selectable(label, isSelected))
            {
                config.SelectedTimelineId = timeline.Id;
                Plugin.Engine.LoadTimeline(timeline);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (config.SelectedTimelineId != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Delete Selected", new Vector2(-1, 0)))
            {
                config.Timelines.RemoveAll(t => t.Id == config.SelectedTimelineId);
                config.SelectedTimelineId = null;
                Plugin.Engine.LoadTimeline(null);
                config.Save();
            }
            ImGui.PopStyleColor();
        }
    }

    private void DrawEntryEditor(Configuration config)
    {
        var timeline = config.Timelines.FirstOrDefault(t => t.Id == config.SelectedTimelineId);
        if (timeline == null) { ImGui.TextDisabled("Select or create a timeline to edit."); return; }

        var name = timeline.Name;
        if (ImGui.InputText("Name", ref name, 128)) { timeline.Name = name; config.Save(); }

        var enabled = timeline.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { timeline.Enabled = enabled; config.Save(); }
        ImGui.SameLine();

        var territoryId = (int)timeline.TerritoryTypeId;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Territory ID", ref territoryId))
        {
            timeline.TerritoryTypeId = (uint)Math.Max(0, territoryId);
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto-load this timeline in a specific duty.\n0 = manual only.\nUse the Debug tab to find your current territory ID.");

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Add Callout");

        ImGui.SetNextItemWidth(50); ImGui.InputFloat("Min", ref newEntryMinutes, 0, 0, "%.0f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60); ImGui.InputFloat("Sec", ref newEntrySeconds, 0, 0, "%.1f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150); ImGui.InputText("Text", ref newEntryText, 128);
        ImGui.SameLine();

        // --- Role and TTS inputs ---
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("Role", ref newEntryRole, "All\0Tank\0Healer\0DPS\0");
        ImGui.SameLine();
        ImGui.Checkbox("TTS", ref newEntryTTS);
        ImGui.SameLine();
        // --------------------------------

        if (ImGui.Button("Add") && newEntryText.Length > 0)
        {
            // Determine alert types based on the TTS checkbox
            var alertFlags = AlertType.ScreenFlash | AlertType.Countdown;
            if (newEntryTTS) alertFlags |= AlertType.Sound;

            timeline.Entries.Add(new TimelineEntry
            {
                TriggerTime = (newEntryMinutes * 60f) + newEntrySeconds,
                CalloutText = newEntryText,
                PreAlertSeconds = config.DefaultPreAlertSeconds,
                DisplayDuration = config.DefaultDisplayDuration,
                TargetRole = (TargetRole)newEntryRole,
                AlertTypes = alertFlags                
            });
            timeline.Entries.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));
            newEntryText = "";
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), $"Entries ({timeline.Entries.Count})");
        ImGui.BeginChild("EntryList", new Vector2(0, 0), false);

        if (timeline.Entries.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "  Time      Callout                      Pre   Dur");
            ImGui.Separator();
        }

        string? entryToDelete = null;
        foreach (var entry in timeline.Entries)
        {
            ImGui.PushID(entry.Id);
            var entryEnabled = entry.Enabled;
            if (ImGui.Checkbox("##en", ref entryEnabled)) { entry.Enabled = entryEnabled; config.Save(); }

            //Add a Role dropdown for existing entries! ---
            ImGui.SameLine();
            ImGui.SetNextItemWidth(75);
            int currentRole = (int)entry.TargetRole;
            if (ImGui.Combo("##role" + entry.Id, ref currentRole, "All\0Tank\0Healer\0DPS\0"))
            {
                entry.TargetRole = (TargetRole)currentRole;
                config.Save();
            }
            // ------------------------------------------------------

            // --- Add a TTS toggle for existing entries ---
            ImGui.SameLine();
            bool hasTTS = (entry.AlertTypes & AlertType.Sound) != 0;
            if (ImGui.Checkbox("TTS", ref hasTTS))
            {
                if (hasTTS) entry.AlertTypes |= AlertType.Sound;   // Turn on
                else entry.AlertTypes &= ~AlertType.Sound;         // Turn off
                config.Save();
            }
            // --------------------------------------------------

            ImGui.SameLine();
            ImGui.TextColored(entry.Enabled ? new Vector4(1,1,1,1) : new Vector4(.5f,.5f,.5f,1), $"{entry.FormattedTime,6}");
            ImGui.SameLine();
            ImGui.TextColored(entry.Enabled ? new Vector4(1f,0.85f,0.2f,1f) : new Vector4(.5f,.4f,.2f,1f), entry.CalloutText);
            ImGui.SameLine(ImGui.GetWindowWidth() - 120);
            ImGui.TextDisabled($"{entry.PreAlertSeconds:F0}s  {entry.DisplayDuration:F0}s");
            ImGui.SameLine(ImGui.GetWindowWidth() - 30);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            if (ImGui.SmallButton("X")) entryToDelete = entry.Id;
            ImGui.PopStyleColor();
            ImGui.PopID();
        }
        if (entryToDelete != null) { timeline.Entries.RemoveAll(e => e.Id == entryToDelete); config.Save(); }
        ImGui.EndChild();
    }

    // =========================================================================
    // TAB: SETTINGS
    // =========================================================================
    private void DrawSettingsTab(Configuration config)
    {
        ImGui.Spacing();

        // ---- ALERT APPEARANCE ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Alert Appearance");
        ImGui.Separator();
        ImGui.Spacing();

        // Font size — uses a staging variable so we only rebuild the atlas on release,
        // not on every pixel of slider movement (atlas rebuilds are expensive).
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Font Size##fontsize", ref pendingFontSize, 18f, 72f, "%.0f px"))
            fontSizeChanged = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Size of the callout text on screen.\nLarger = bigger and still sharp (uses native font rendering).\nChanges apply when you release the slider.");

        // Apply font size when slider is released
        if (fontSizeChanged && !ImGui.IsItemActive())
        {
            config.AlertFontSize = pendingFontSize;
            config.Save();
            Plugin.RebuildAlertFont();
            fontSizeChanged = false;
        }

        // Vertical position
        ImGui.SetNextItemWidth(200);
        var vpos = config.AlertVerticalPosition;
        if (ImGui.SliderFloat("Vertical Position##vpos", ref vpos, 0f, 1f, "%.2f"))
        {
            config.AlertVerticalPosition = vpos;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where on screen the alert appears.\n0.0 = top of screen, 0.5 = center, 1.0 = bottom.");

        // Alert color
        var color = new Vector4(
            config.DefaultAlertColor[0], config.DefaultAlertColor[1],
            config.DefaultAlertColor[2], config.DefaultAlertColor[3]);
        if (ImGui.ColorEdit4("Alert Color##alertcolor", ref color))
        {
            config.DefaultAlertColor = [color.X, color.Y, color.Z, color.W];
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Default color for callout text.\nIndividual timeline entries can override this.");

        ImGui.Spacing();
        ImGui.Spacing();

        // ---- CALLOUT DEFAULTS ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Callout Defaults");
        ImGui.Separator();
        ImGui.Spacing();

        // Pre-alert seconds
        ImGui.SetNextItemWidth(200);
        var preAlert = config.DefaultPreAlertSeconds;
        if (ImGui.SliderFloat("Default Pre-Alert##prealert", ref preAlert, 0f, 15f, "%.0f s"))
        {
            config.DefaultPreAlertSeconds = preAlert;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many seconds before the ability fires to start the countdown.\nExample: 5 = 'Reprisal in 5... 4... 3... 2... 1...'\nApplied to newly created entries.");

        // Display duration
        ImGui.SetNextItemWidth(200);
        var dispDur = config.DefaultDisplayDuration;
        if (ImGui.SliderFloat("Default Display Duration##dispdur", ref dispDur, 1f, 10f, "%.0f s"))
        {
            config.DefaultDisplayDuration = dispDur;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How long the alert stays on screen after triggering.\nApplied to newly created entries.");

        ImGui.Spacing();
        ImGui.Spacing();

        // ---- BEHAVIOUR ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Behaviour");
        ImGui.Separator();
        ImGui.Spacing();

        var autoLoad = config.AutoLoadTimelines;
        if (ImGui.Checkbox("Auto-load timelines on zone entry", ref autoLoad))
        {
            config.AutoLoadTimelines = autoLoad;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically loads a matching timeline when you enter a duty,\nbased on the Territory ID set on each timeline.");

        var showTimer = config.ShowFightTimer;
        if (ImGui.Checkbox("Show fight timer during combat", ref showTimer))
        {
            config.ShowFightTimer = showTimer;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Displays a small elapsed-time counter while in combat.\n(Timer display not yet implemented — coming soon.)");

        ImGui.Spacing();
        ImGui.Spacing();

        // ---- PREVIEW ----
        // Shows a mock alert using the current settings so the user can tune
        // font size, color, and position without needing to be in combat.
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Preview");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("The sample text below uses your current color setting.");
        ImGui.Spacing();
        ImGui.TextColored(
            new Vector4(config.DefaultAlertColor[0], config.DefaultAlertColor[1],
                        config.DefaultAlertColor[2], config.DefaultAlertColor[3]),
            ">>> REPRISAL <<<");
        ImGui.SameLine();
        ImGui.TextDisabled("  ← example callout (font size shown in-game only)");
    }

    // =========================================================================
    // TAB: DEBUG
    // =========================================================================
    private void DrawDebugTab(Configuration config)
    {
        ImGui.Spacing();

        // --- Current Territory ---
        var currentTerritory = Plugin.CurrentTerritoryId;
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "Current Territory ID:");
        ImGui.SameLine();
        ImGui.Text($"{currentTerritory}");
        ImGui.SameLine();

        var selectedTimeline = config.Timelines.FirstOrDefault(t => t.Id == config.SelectedTimelineId);
        if (selectedTimeline != null)
        {
            if (ImGui.SmallButton("Use This ID on Selected Timeline"))
            {
                selectedTimeline.TerritoryTypeId = currentTerritory;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Sets \"{selectedTimeline.Name}\" territory to {currentTerritory}");
        }
        else
        {
            ImGui.TextDisabled("(select a timeline to copy ID into it)");
        }

        ImGui.Spacing();

        // --- Engine State ---
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "Engine State:");
        ImGui.Text($"  Running:         {Plugin.Engine.IsRunning}");
        ImGui.Text($"  Fight Time:      {Plugin.Engine.CurrentTime:F2}s");
        var timelineName = Plugin.Engine.ActiveTimelineName;
        if (timelineName == null)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  Active Timeline: (none — callouts will not fire!)");
        else
            ImGui.Text($"  Active Timeline: {timelineName}");

        ImGui.Spacing();

        // --- Timeline Match Check ---
        if (selectedTimeline != null)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "Timeline Match Check:");
            var idMatches = selectedTimeline.TerritoryTypeId == currentTerritory;
            ImGui.Text($"  Selected timeline territory: {selectedTimeline.TerritoryTypeId}");
            ImGui.Text($"  Your current territory:      {currentTerritory}  ");
            ImGui.SameLine();
            ImGui.TextColored(
                idMatches ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                idMatches ? "MATCH ✓" : "NO MATCH ✗");

            if (!idMatches)
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                    "  ⚠ IDs don't match — auto-load won't trigger.\n    Click 'Use This ID' above, or use 'Start Test'.");

            ImGui.Spacing();

            // --- Entry Count ---
            var entryCount = selectedTimeline.Entries.Count;
            var enabledCount = selectedTimeline.Entries.Count(e => e.Enabled);
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "Entries:");
            ImGui.Text($"  Total: {entryCount}   Enabled: {enabledCount}");
            if (entryCount == 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  ⚠ No entries! Add callouts in the Timelines tab.");
            else if (enabledCount == 0)
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "  ⚠ All entries are disabled.");

            // --- Next Callout ---
            if (Plugin.Engine.IsRunning && enabledCount > 0)
            {
                var currentTime = Plugin.Engine.CurrentTime;
                var next = selectedTimeline.Entries
                    .Where(e => e.Enabled && e.TriggerTime > currentTime)
                    .OrderBy(e => e.TriggerTime)
                    .FirstOrDefault();

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "Next Callout:");
                if (next != null)
                    ImGui.Text($"  \"{next.CalloutText}\" at {next.FormattedTime} (in {next.TriggerTime - currentTime:F1}s)");
                else
                    ImGui.TextDisabled("  No more entries after current fight time.");
            }
        }
        else
        {
            ImGui.TextDisabled("Select a timeline in the Timelines tab to see match info.");
        }
    }
}
