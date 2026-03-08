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
    private int newEntryRole = 0;
    private bool newEntryTTS = false;

    // Inline entry editor state — tracks which entry row is currently expanded for editing
    private string? editingEntryId = null;
    private float   editMinutes    = 0;
    private float   editSeconds    = 0;
    private string  editText       = "";
    private float   editPreAlert   = 5f;
    private float   editDuration   = 3f;
    private Vector4 editColor      = new(1f, 0.85f, 0.2f, 1f); // scratch color while editing

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

        // Timeline View toggle button (always visible, top-right area)
        ImGui.SameLine(ImGui.GetWindowWidth() - 110);
        var tvOpen = Plugin.TimelineView.IsOpen;
        if (tvOpen) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.6f, 1f));
        else        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.25f, 0.3f, 1f));
        if (ImGui.SmallButton("Timeline View"))
            Plugin.TimelineView.IsOpen = !Plugin.TimelineView.IsOpen;
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open/close the visual timeline view.");

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
            if (ImGui.BeginTabItem("FF Logs Import"))
            {
                // Show a simple launch button rather than embedding the full
                // import UI here — it opens its own dedicated window with
                // more space for the cast events table.
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), "FF Logs Cooldown Importer");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Paste a FF Logs report URL to fetch a player's cast events and " +
                    "convert their cooldown usage into a CalloutPlugin timeline.");
                ImGui.Spacing();
                ImGui.Bullet(); ImGui.SameLine();
                ImGui.TextWrapped("Requires a free FF Logs v1 API key from fflogs.com/profile");
                ImGui.Bullet(); ImGui.SameLine();
                ImGui.TextWrapped("Works with any public report — paste the URL from your browser");
                ImGui.Spacing();
                if (ImGui.Button("Open FF Logs Importer Window", new Vector2(240, 30)))
                    Plugin.FflogsImportWindow.IsOpen = true;
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
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            timeline.Enabled = enabled;
            config.Save();
            // If this timeline is currently active in the engine, reload it
            // immediately so the engine sees the new Enabled state right now.
            // LoadTimeline calls Reset() which stops the stopwatch and clears
            // all fired-entry tracking, so no stale alerts will linger.
            if (Plugin.Engine.ActiveTimelineName == timeline.Name)
                Plugin.Engine.LoadTimeline(timeline);
        }
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

        // If the timeline itself is disabled, grey out the entire entry list so it's
        // visually clear that nothing will fire — even if individual entries are checked.
        if (!timeline.Enabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), $"Entries ({timeline.Entries.Count}) — TIMELINE IS DISABLED, no callouts will fire");
            ImGui.BeginDisabled();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), $"Entries ({timeline.Entries.Count})");
        }

        ImGui.BeginChild("EntryList", new Vector2(0, 0), false);

        if (timeline.Entries.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "  Time      Callout                      Pre   Dur");
            ImGui.Separator();
        }

        string? entryToDelete = null;
        bool needsSort = false;
        foreach (var entry in timeline.Entries)
        {
            ImGui.PushID(entry.Id);

            bool isEditing = editingEntryId == entry.Id;

            // ---- Enabled checkbox (always visible) ----
            var entryEnabled = entry.Enabled;
            if (ImGui.Checkbox("##en", ref entryEnabled)) { entry.Enabled = entryEnabled; config.Save(); }
            ImGui.SameLine();

            if (isEditing)
            {
                // ================================================================
                // EXPANDED EDIT ROW
                // ================================================================

                // Time fields
                ImGui.SetNextItemWidth(40);
                ImGui.InputFloat("##emin", ref editMinutes, 0, 0, "%.0f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Minutes");
                ImGui.SameLine();
                ImGui.TextDisabled(":");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(48);
                ImGui.InputFloat("##esec", ref editSeconds, 0, 0, "%.1f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Seconds");
                ImGui.SameLine();

                // Callout text
                ImGui.SetNextItemWidth(140);
                ImGui.InputText("##etxt", ref editText, 128);
                ImGui.SameLine();

                // Role dropdown
                ImGui.SetNextItemWidth(72);
                int editRole = (int)entry.TargetRole;
                if (ImGui.Combo("##erole", ref editRole, "All\0Tank\0Healer\0DPS\0"))
                {
                    entry.TargetRole = (TargetRole)editRole;
                    config.Save();
                }
                ImGui.SameLine();

                // TTS toggle
                bool hasTTS = (entry.AlertTypes & AlertType.Sound) != 0;
                if (ImGui.Checkbox("TTS##e", ref hasTTS))
                {
                    if (hasTTS) entry.AlertTypes |= AlertType.Sound;
                    else        entry.AlertTypes &= ~AlertType.Sound;
                    config.Save();
                }
                ImGui.SameLine();

                // Per-entry color picker — small inline swatch that opens a popup
                // ColorButton renders a colored square; clicking it opens ColorPicker4.
                // We use NoAlpha since the overlay alpha is driven by the fade system.
                ImGui.ColorButton("##ecol", editColor,
                    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoPicker,
                    new Vector2(18, 18));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to change callout color.\nRight-click to reset to global default.");
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    ImGui.OpenPopup("##ecolpicker");
                // Right-click resets to the global default color
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    var d = config.DefaultAlertColor;
                    editColor = new Vector4(d[0], d[1], d[2], d[3]);
                }
                if (ImGui.BeginPopup("##ecolpicker"))
                {
                    ImGui.ColorPicker4("##ecolpick4", ref editColor,
                        ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.PickerHueBar);
                    ImGui.EndPopup();
                }
                ImGui.SameLine();

                // Pre-alert and duration (second line indented)
                ImGui.SetNextItemWidth(50);
                ImGui.InputFloat("Pre##epre", ref editPreAlert, 0, 0, "%.0f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pre-alert seconds (countdown start)");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(50);
                ImGui.InputFloat("Dur##edur", ref editDuration, 0, 0, "%.0f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Display duration after trigger (seconds)");
                ImGui.SameLine();

                // Save button — writes all edited fields back to the entry
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.5f, 0.15f, 1f));
                if (ImGui.SmallButton("Save"))
                {
                    entry.TriggerTime     = (editMinutes * 60f) + editSeconds;
                    entry.CalloutText     = editText;
                    entry.PreAlertSeconds = editPreAlert;
                    entry.DisplayDuration = editDuration;

                    // Save color — if it matches the global default exactly, store null
                    // so the entry uses the global setting rather than a redundant override.
                    var d = config.DefaultAlertColor;
                    bool isDefault = MathF.Abs(editColor.X - d[0]) < 0.001f
                                  && MathF.Abs(editColor.Y - d[1]) < 0.001f
                                  && MathF.Abs(editColor.Z - d[2]) < 0.001f;
                    entry.Color = isDefault ? null : new[] { editColor.X, editColor.Y, editColor.Z, 1f };

                    needsSort      = true;
                    config.Save();
                    editingEntryId = null;
                }
                ImGui.PopStyleColor();
                ImGui.SameLine();

                // Cancel — discard edits and collapse
                if (ImGui.SmallButton("Cancel"))
                    editingEntryId = null;
                ImGui.SameLine();

                // Delete
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                if (ImGui.SmallButton("X")) entryToDelete = entry.Id;
                ImGui.PopStyleColor();
            }
            else
            {
                // ================================================================
                // COLLAPSED READ-ONLY ROW  (click the row to expand for editing)
                // ================================================================

                // Role dropdown — always editable even when collapsed
                ImGui.SetNextItemWidth(75);
                int currentRole = (int)entry.TargetRole;
                if (ImGui.Combo("##role", ref currentRole, "All\0Tank\0Healer\0DPS\0"))
                {
                    entry.TargetRole = (TargetRole)currentRole;
                    config.Save();
                }
                ImGui.SameLine();

                // TTS toggle — always editable even when collapsed
                bool hasTTS = (entry.AlertTypes & AlertType.Sound) != 0;
                if (ImGui.Checkbox("TTS##c", ref hasTTS))
                {
                    if (hasTTS) entry.AlertTypes |= AlertType.Sound;
                    else        entry.AlertTypes &= ~AlertType.Sound;
                    config.Save();
                }
                ImGui.SameLine();

                // Color swatch — shows entry color (or global default if none set).
                // Read-only on the collapsed row; open Edit to change it.
                var swatchArr = entry.Color ?? config.DefaultAlertColor;
                var swatchVec = new Vector4(swatchArr[0], swatchArr[1], swatchArr[2], 1f);
                // Dim the swatch if the entry is disabled so it matches the greyed-out text
                if (!entry.Enabled) swatchVec = swatchVec with { W = 0.35f };
                ImGui.ColorButton("##cswatch", swatchVec,
                    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoTooltip,
                    new Vector2(12, 12));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(entry.Color == null ? "Using global default color" : "Custom color (edit to change)");
                ImGui.SameLine();

                // Time + text display — clicking opens the editor
                var rowColor = entry.Enabled
                    ? new Vector4(1f, 1f, 1f, 1f)
                    : new Vector4(0.5f, 0.5f, 0.5f, 1f);
                var textColor = entry.Enabled
                    ? new Vector4(1f, 0.85f, 0.2f, 1f)
                    : new Vector4(0.5f, 0.4f, 0.2f, 1f);

                ImGui.TextColored(rowColor, $"{entry.FormattedTime,6}");
                ImGui.SameLine();
                ImGui.TextColored(textColor, entry.CalloutText);
                ImGui.SameLine(ImGui.GetWindowWidth() - 150);
                ImGui.TextDisabled($"{entry.PreAlertSeconds:F0}s / {entry.DisplayDuration:F0}s");
                ImGui.SameLine(ImGui.GetWindowWidth() - 60);

                // Edit button — expands the row and populates scratch fields
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.35f, 0.55f, 1f));
                if (ImGui.SmallButton("Edit"))
                {
                    editingEntryId = entry.Id;
                    editMinutes    = (float)Math.Floor(entry.TriggerTime / 60f);
                    editSeconds    = entry.TriggerTime % 60f;
                    editText       = entry.CalloutText;
                    editPreAlert   = entry.PreAlertSeconds;
                    editDuration   = entry.DisplayDuration;
                    // Load the entry's color if it has one, otherwise start from the global default
                    var c = entry.Color ?? Plugin.Configuration.DefaultAlertColor;
                    editColor = new Vector4(c[0], c[1], c[2], c[3]);
                }
                ImGui.PopStyleColor();
                ImGui.SameLine();

                // Delete
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                if (ImGui.SmallButton("X")) entryToDelete = entry.Id;
                ImGui.PopStyleColor();
            }

            ImGui.PopID();
        }
        if (entryToDelete != null) { timeline.Entries.RemoveAll(e => e.Id == entryToDelete); config.Save(); }
        if (needsSort) { timeline.Entries.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime)); config.Save(); }
        ImGui.EndChild();

        // Close the BeginDisabled block we opened when the timeline is disabled
        if (!timeline.Enabled)
            ImGui.EndDisabled();
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

        // ---- COOLDOWN TRACKER ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Cooldown Tracker HUD");
        ImGui.Separator();
        ImGui.Spacing();

        var ctEnabled = config.CooldownTrackerEnabled;
        if (ImGui.Checkbox("Enable cooldown tracker overlay", ref ctEnabled))
        { config.CooldownTrackerEnabled = ctEnabled; config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shows upcoming callouts as a small sidebar during combat.\nDrag the panel in-game to reposition it.");

        if (config.CooldownTrackerEnabled)
        {
            ImGui.Spacing();

            // Entry count
            ImGui.SetNextItemWidth(120);
            var ctCount = config.CooldownTrackerEntryCount;
            if (ImGui.SliderInt("Entries shown##ctcount", ref ctCount, 1, 8))
            { config.CooldownTrackerEntryCount = ctCount; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How many upcoming callouts to display at once.");

            // Background opacity
            ImGui.SetNextItemWidth(120);
            var ctAlpha = config.CooldownTrackerBgAlpha;
            if (ImGui.SliderFloat("Background opacity##ctalpha", ref ctAlpha, 0f, 1f, "%.2f"))
            { config.CooldownTrackerBgAlpha = ctAlpha; config.Save(); }

            // Role filter
            var ctRole = config.CooldownTrackerRoleFilter;
            if (ImGui.Checkbox("Filter by my role", ref ctRole))
            { config.CooldownTrackerRoleFilter = ctRole; config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only show callouts matching your current job role.\nTurn off to see all callouts regardless of role.");

            // Show timer
            var ctTimer = config.CooldownTrackerShowTimer;
            if (ImGui.Checkbox("Show fight timer in tracker", ref ctTimer))
            { config.CooldownTrackerShowTimer = ctTimer; config.Save(); }

            // ---- Reposition button ----
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            bool editMode = Plugin.CooldownTracker.IsEditMode;
            if (editMode)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.15f, 0.55f, 0.15f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f,  0.7f,  0.2f,  1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.1f,  0.4f,  0.1f,  1f));
                if (ImGui.Button("Done Repositioning", new Vector2(160, 0)))
                    Plugin.CooldownTracker.IsEditMode = false;
                ImGui.PopStyleColor(3);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.1f, 1f), "Drag the orange panel");
            }
            else
            {
                if (ImGui.Button("Reposition HUD", new Vector2(120, 0)))
                    Plugin.CooldownTracker.IsEditMode = true;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Shows a preview panel on screen with an orange border.\nDrag it to reposition, then click 'Done Repositioning'.");
            }
        }

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
        var selectedTimeline = config.Timelines.FirstOrDefault(t => t.Id == config.SelectedTimelineId);

        // =========================================================
        // SECTION 1: TERRITORY
        // =========================================================
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "[1] TERRITORY");
        ImGui.Separator();

        var currentTerritory = Plugin.CurrentTerritoryId;
        ImGui.Text($"  Current Territory ID: {currentTerritory}");
        ImGui.SameLine();
        if (selectedTimeline != null)
        {
            if (ImGui.SmallButton("Use This ID"))
            {
                selectedTimeline.TerritoryTypeId = currentTerritory;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Sets \"{selectedTimeline.Name}\" territory to {currentTerritory}");
        }
        else
        {
            ImGui.TextDisabled("(select a timeline to copy this ID)");
        }

        // =========================================================
        // SECTION 2: ENGINE STATE
        // =========================================================
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "[2] ENGINE STATE");
        ImGui.Separator();

        // Stopwatch running?
        var isRunning = Plugin.Engine.IsRunning;
        ImGui.Text("  Timer Running:   ");
        ImGui.SameLine();
        ImGui.TextColored(
            isRunning ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f),
            isRunning ? "YES" : "NO");

        ImGui.Text($"  Fight Time:      {Plugin.Engine.CurrentTime:F2}s");

        // Which timeline does the engine currently have loaded?
        var engineTimelineName = Plugin.Engine.ActiveTimelineName;
        var engineEnabled = Plugin.Engine.ActiveTimelineEnabled;   // new property — see below
        ImGui.Text("  Loaded Timeline: ");
        ImGui.SameLine();
        if (engineTimelineName == null)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "(none — callouts WILL NOT fire)");
        else
            ImGui.Text(engineTimelineName);

        // Is the engine's loaded timeline actually enabled?
        ImGui.Text("  Timeline Enabled (engine sees): ");
        ImGui.SameLine();
        ImGui.TextColored(
            engineEnabled ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
            engineEnabled ? "YES — will fire callouts" : "NO  — callouts BLOCKED");

        // Would combat start currently fire?
        var wouldStart = engineTimelineName != null && engineEnabled;
        ImGui.Text("  Combat start would fire:       ");
        ImGui.SameLine();
        ImGui.TextColored(
            wouldStart ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
            wouldStart ? "YES" : "NO (no timeline, or timeline disabled)");

        // =========================================================
        // SECTION 3: SELECTED TIMELINE
        // =========================================================
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "[3] SELECTED TIMELINE (UI)");
        ImGui.Separator();

        if (selectedTimeline == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  None selected — pick one in the Timelines tab.");
        }
        else
        {
            ImGui.Text($"  Name:            {selectedTimeline.Name}");

            // Enabled flag on the config object (what the checkbox controls)
            ImGui.Text("  Enabled (config): ");
            ImGui.SameLine();
            ImGui.TextColored(
                selectedTimeline.Enabled ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                selectedTimeline.Enabled ? "YES" : "NO");

            // Engine vs config agreement check
            var engineMatchesConfig = engineTimelineName == selectedTimeline.Name && engineEnabled == selectedTimeline.Enabled;
            ImGui.Text("  Engine/config in sync: ");
            ImGui.SameLine();
            ImGui.TextColored(
                engineMatchesConfig ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.7f, 0.3f, 1f),
                engineMatchesConfig ? "YES" : "MISMATCH — try selecting timeline again");

            // Territory match
            var idMatches = selectedTimeline.TerritoryTypeId == currentTerritory;
            ImGui.Text($"  Territory (timeline): {selectedTimeline.TerritoryTypeId}  |  Territory (current): {currentTerritory}  ");
            ImGui.SameLine();
            ImGui.TextColored(
                idMatches ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                idMatches ? "MATCH" : "NO MATCH");

            if (!idMatches)
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                    "  ⚠ Territory mismatch — auto-load won't trigger. Use 'Start Test' or fix the ID above.");

            // =========================================================
            // SECTION 4: ENTRIES
            // =========================================================
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "[4] ENTRIES");
            ImGui.Separator();

            var totalEntries   = selectedTimeline.Entries.Count;
            var enabledEntries = selectedTimeline.Entries.Count(e => e.Enabled);
            ImGui.Text($"  Total: {totalEntries}   Enabled: {enabledEntries}   Disabled: {totalEntries - enabledEntries}");

            if (totalEntries == 0)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  ⚠ No entries in this timeline!");
            else if (enabledEntries == 0)
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "  ⚠ All entries are individually disabled.");
            else if (!selectedTimeline.Enabled)
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                    $"  ⚠ {enabledEntries} entries ready, but the TIMELINE is disabled — nothing will fire.");

            // =========================================================
            // SECTION 5: NEXT CALLOUT (only when running)
            // =========================================================
            if (Plugin.Engine.IsRunning)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.8f, 1f), "[5] NEXT CALLOUT");
                ImGui.Separator();

                if (!selectedTimeline.Enabled)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  Timeline disabled — no callouts will fire.");
                }
                else if (enabledEntries == 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  No enabled entries.");
                }
                else
                {
                    var currentTime = Plugin.Engine.CurrentTime;
                    var next = selectedTimeline.Entries
                        .Where(e => e.Enabled && e.TriggerTime > currentTime)
                        .OrderBy(e => e.TriggerTime)
                        .FirstOrDefault();

                    if (next != null)
                        ImGui.Text($"  \"{next.CalloutText}\"  at {next.FormattedTime}  (in {next.TriggerTime - currentTime:F1}s)");
                    else
                        ImGui.TextDisabled("  No more entries after current fight time.");
                }
            }
        }
    }
}
