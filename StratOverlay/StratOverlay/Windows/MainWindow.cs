// =============================================================================
// MainWindow.cs — Full Configuration UI (M4)
// =============================================================================
//
// Three-tab layout:
//   [Strats]   — Timeline list, variant manager, entry list + editor
//   [Settings] — Overlay position, size, opacity, fade
//   [Debug]    — Live timer, cache status, event log
//
// DESIGN NOTES FOR A NOVICE:
//   ImGui is an "immediate mode" UI — you describe what to draw every frame,
//   not build a retained widget tree. This means:
//     - State you want to remember (e.g. which entry is selected) lives as
//       private fields on the class, NOT inside the draw functions.
//     - Every conditional widget (buttons that open popups, etc.) is checked
//       every frame; if the user didn't click it, it simply doesn't fire.
//     - child windows / BeginChild let us create scrollable sub-panels inside
//       a window — very useful for long lists.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using StratOverlay.Data;

namespace StratOverlay.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    // ---- Selection state (which timeline / variant / entry is selected) ----
    private int    _selectedTimelineIdx = -1;  // index into Config.Timelines list
    private int    _selectedVariantIdx  = -1;  // index into selected timeline's Variants
    private int    _selectedEntryIdx    = -1;  // index into selected timeline's Entries

    // ---- Inline edit buffers (ImGui needs char[] buffers for text input) ----
    private string _editTimelineName   = "";
    private string _editTimelineDesc   = "";
    private string _editTimelineAuthor = "";
    private string _editTimelineTerrId = "";

    private string _editVariantName    = "";
    private string _editVariantNote    = "";

    private string _editEntryLabel     = "";
    private string _editEntryTime      = "";
    private string _editEntryNote      = "";
    private string _editEntryImgPath   = "";

    // ---- Debug log ring buffer ----
    private readonly List<string> _debugLog = new();
    private const int MaxDebugLines = 100;

    // ---- Debug tab: URL test tool ----
    private string _testUrl        = "https://wtfdig.info/74/m9s/limit-cut-all.png";
    private string _testUrlResult  = "";
    private bool   _testUrlPending = false;

    // =========================================================================
    // CONSTRUCTOR / DISPOSE
    // =========================================================================

    public MainWindow(Plugin plugin) : base("Strat Overlay##StratMainWindow",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 500),
            MaximumSize = new Vector2(1400, 1000),
        };

        // Subscribe to engine events for the debug log
        Plugin.Engine.OnStratTriggered += (_, e) =>
            AddDebugLine($"[TRIGGER] \"{e.Entry.Label}\" at {e.FightTime:F1}s");
        Plugin.Engine.OnCombatStarted  += (_, _) =>
            AddDebugLine("[COMBAT] Started");
        Plugin.Engine.OnCombatEnded    += (_, _) =>
            AddDebugLine("[COMBAT] Ended");
        Plugin.Engine.OnVariantChanged += (_, id) =>
            AddDebugLine($"[VARIANT] Changed to id={id}");
    }

    public void Dispose() { /* events unregistered when lambdas go out of scope */ }

    private void AddDebugLine(string msg)
    {
        _debugLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_debugLog.Count > MaxDebugLines)
            _debugLog.RemoveAt(0);
    }

    // =========================================================================
    // DRAW — entry point called every frame when window is open
    // =========================================================================

    public override void Draw()
    {
        var cfg    = Plugin.Configuration;
        var engine = Plugin.Engine;

        // ---- Dynamic title showing active timeline ----
        string activeName = engine.ActiveTimelineName ?? "none";
        WindowName = engine.IsRunning
            ? $"Strat Overlay  ▶  {activeName}##StratMainWindow"
            : $"Strat Overlay##StratMainWindow";

        if (ImGui.BeginTabBar("##StratMainTabs"))
        {
            if (ImGui.BeginTabItem("Strats"))   { DrawStratsTab();   ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { DrawSettingsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Debug"))    { DrawDebugTab();    ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // =========================================================================
    // TAB: STRATS
    // =========================================================================
    // Layout: [Timeline List | Variant List] [Entry List | Entry Editor]
    //          Left panel ~30%                Right panel ~70%
    // =========================================================================

    private void DrawStratsTab()
    {
        var cfg     = Plugin.Configuration;
        float fullW = ImGui.GetContentRegionAvail().X;
        float fullH = ImGui.GetContentRegionAvail().Y;

        // ---- LEFT PANEL: Timeline list + Variant list ----
        float leftW = fullW * 0.30f;
        if (ImGui.BeginChild("##LeftPanel", new Vector2(leftW, fullH), true))
        {
            DrawTimelineList(cfg);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawVariantList(cfg);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // ---- RIGHT PANEL: Entry list + Entry editor ----
        float rightW = fullW - leftW - 8f;
        if (ImGui.BeginChild("##RightPanel", new Vector2(rightW, fullH), true))
        {
            DrawEntryList(cfg);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawEntryEditor(cfg);
        }
        ImGui.EndChild();
    }

    // ---- Timeline List ----

    private void DrawTimelineList(Configuration cfg)
    {
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Timelines");
        ImGui.Spacing();

        // Sync _selectedTimelineIdx to the configured SelectedTimelineId on first draw
        if (_selectedTimelineIdx == -1 && cfg.SelectedTimelineId != null)
        {
            _selectedTimelineIdx = cfg.Timelines
                .FindIndex(t => t.Id == cfg.SelectedTimelineId);
        }

        float listH = 150f;
        if (ImGui.BeginChild("##TimelineList", new Vector2(0, listH), false))
        {
            for (int i = 0; i < cfg.Timelines.Count; i++)
            {
                var tl  = cfg.Timelines[i];
                bool sel = i == _selectedTimelineIdx;
                string tlLabel = tl.IsBuiltIn
                    ? $"🔒 {tl.Name}##tl{i}"
                    : $"{tl.Name}##tl{i}";
                if (ImGui.Selectable(tlLabel, sel))
                {
                    SelectTimeline(cfg, i);
                }
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(tl.Description))
                    ImGui.SetTooltip(tl.Description);
            }
        }
        ImGui.EndChild();

        // ---- Add / Delete ----
        if (ImGui.Button("+ New"))
        {
            var newTl = new StratTimeline { Name = "New Timeline" };
            cfg.Timelines.Add(newTl);
            SelectTimeline(cfg, cfg.Timelines.Count - 1);
            cfg.Save();
        }
        ImGui.SameLine();
        var selectedTlForDelete = _selectedTimelineIdx >= 0 && _selectedTimelineIdx < cfg.Timelines.Count
            ? cfg.Timelines[_selectedTimelineIdx] : null;
        bool canDelete = selectedTlForDelete != null && !selectedTlForDelete.IsBuiltIn;
        if (!canDelete) ImGui.BeginDisabled();
        if (ImGui.Button("Delete") && canDelete)
        {
            cfg.Timelines.RemoveAt(_selectedTimelineIdx);
            _selectedTimelineIdx = -1;
            _selectedVariantIdx  = -1;
            _selectedEntryIdx    = -1;
            cfg.SelectedTimelineId = null;
            cfg.Save();
        }
        if (!canDelete) ImGui.EndDisabled();
        if (!canDelete && selectedTlForDelete?.IsBuiltIn == true)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("🔒 Built-in strats cannot be deleted (reset in Settings).");
        }

        // ---- Edit selected timeline's name / territory ----
        if (_selectedTimelineIdx >= 0 && _selectedTimelineIdx < cfg.Timelines.Count)
        {
            var tl = cfg.Timelines[_selectedTimelineIdx];
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##tlName", ref _editTimelineName, 128))
            { tl.Name = _editTimelineName; cfg.Save(); }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##tlAuthor", ref _editTimelineAuthor, 64))
            { tl.Author = _editTimelineAuthor; cfg.Save(); }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("TerritoryID##tlTerrId", ref _editTimelineTerrId, 12))
            {
                if (int.TryParse(_editTimelineTerrId, out int tid))
                { tl.TerritoryTypeId = tid; cfg.Save(); }
            }

            bool enabled = tl.Enabled;
            if (ImGui.Checkbox("Enabled##tlEnabled", ref enabled))
            { tl.Enabled = enabled; cfg.Save(); }

            ImGui.SameLine();

            // Load / PreWarm buttons
            if (ImGui.Button("Load & Prewarm"))
            {
                Plugin.Engine.LoadTimeline(tl);
                Plugin.ImageCache.PreWarm(tl);
                cfg.SelectedTimelineId = tl.Id;
                cfg.Save();
                AddDebugLine($"[LOAD] Loaded \"{tl.Name}\" and started prewarm.");
            }
        }
    }

    private void SelectTimeline(Configuration cfg, int idx)
    {
        _selectedTimelineIdx = idx;
        _selectedVariantIdx  = -1;
        _selectedEntryIdx    = -1;

        if (idx >= 0 && idx < cfg.Timelines.Count)
        {
            var tl = cfg.Timelines[idx];
            cfg.SelectedTimelineId = tl.Id;
            _editTimelineName   = tl.Name;
            _editTimelineDesc   = tl.Description;
            _editTimelineAuthor = tl.Author;
            _editTimelineTerrId = tl.TerritoryTypeId.ToString();
            cfg.Save();
        }
    }

    // ---- Variant List ----

    private void DrawVariantList(Configuration cfg)
    {
        if (_selectedTimelineIdx < 0 || _selectedTimelineIdx >= cfg.Timelines.Count) return;
        var tl = cfg.Timelines[_selectedTimelineIdx];

        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.9f, 1f), "Variants");
        ImGui.Spacing();

        for (int i = 0; i < tl.Variants.Count; i++)
        {
            var v   = tl.Variants[i];
            bool sel = i == _selectedVariantIdx;

            // Mark the active variant with a ★ indicator
            bool isActive = v.Id == tl.ActiveVariantId;
            string label  = isActive ? $"★ {v.Name}##var{i}" : $"  {v.Name}##var{i}";

            if (ImGui.Selectable(label, sel))
            {
                _selectedVariantIdx = i;
                _editVariantName    = v.Name;
                _editVariantNote    = v.Note;
            }
        }

        ImGui.Spacing();

        if (ImGui.Button("+ Variant"))
        {
            tl.Variants.Add(new StratVariant { Name = "New Variant" });
            cfg.Save();
        }

        // Only allow deletion of non-default variants
        ImGui.SameLine();
        if (ImGui.Button("Delete##var")
            && _selectedVariantIdx > 0   // 0 = default, never deletable
            && _selectedVariantIdx < tl.Variants.Count)
        {
            tl.Variants.RemoveAt(_selectedVariantIdx);
            _selectedVariantIdx = -1;
            cfg.Save();
        }

        // Edit name of selected (non-default) variant
        if (_selectedVariantIdx > 0 && _selectedVariantIdx < tl.Variants.Count)
        {
            var v = tl.Variants[_selectedVariantIdx];
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##varName", ref _editVariantName, 64))
            { v.Name = _editVariantName; cfg.Save(); }

            // Set as active variant button
            if (ImGui.Button("Set Active##setActiveVar"))
            {
                Plugin.Engine.SetVariant(v.Id);
                tl.ActiveVariantId = v.Id;
                cfg.Save();
            }
        }
    }

    // ---- Entry List ----

    private void DrawEntryList(Configuration cfg)
    {
        if (_selectedTimelineIdx < 0 || _selectedTimelineIdx >= cfg.Timelines.Count)
        {
            ImGui.TextDisabled("← Select a timeline first.");
            return;
        }
        var tl = cfg.Timelines[_selectedTimelineIdx];

        ImGui.TextColored(new Vector4(0.8f, 0.9f, 0.5f, 1f), "Entries");
        ImGui.SameLine();
        ImGui.TextDisabled($"({tl.Entries.Count})");
        ImGui.Spacing();

        // Column headers
        ImGui.TextDisabled("  Time   | Label                      | Strat");
        ImGui.Separator();

        float listH = 160f;
        if (ImGui.BeginChild("##EntryList", new Vector2(0, listH), false))
        {
            for (int i = 0; i < tl.Entries.Count; i++)
            {
                var e   = tl.Entries[i];
                bool sel = i == _selectedEntryIdx;

                // ---- Selectable row (time + label) ----
                string prefix = e.Enabled ? "" : "(off) ";
                string rowLabel = $"{prefix}{e.FormattedTime,6} | {e.Label,-24}##entry{i}";
                if (ImGui.Selectable(rowLabel, sel, ImGuiSelectableFlags.None,
                    new Vector2(ImGui.GetContentRegionAvail().X - 130f, 0)))
                    SelectEntry(cfg, i);

                // ---- Inline strat dropdown — only if this entry has multiple strat images ----
                // Filter out the "default" alias key (it's a duplicate of the first real strat)
                var stratKeys = e.Images.Keys
                    .Where(k => k != "default")
                    .ToArray();

                if (stratKeys.Length > 1)
                {
                    ImGui.SameLine();

                    // Find current index in the dropdown
                    string currentKey = e.ActiveStratKey == "default" || !stratKeys.Contains(e.ActiveStratKey)
                        ? stratKeys[0]
                        : e.ActiveStratKey;
                    int stratIdx = Array.IndexOf(stratKeys, currentKey);
                    if (stratIdx < 0) stratIdx = 0;

                    ImGui.SetNextItemWidth(120f);
                    if (ImGui.Combo($"##strat{i}", ref stratIdx, stratKeys, stratKeys.Length))
                    {
                        e.ActiveStratKey = stratKeys[stratIdx];
                        cfg.Save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Which strat image to show for this mechanic");
                }
                else if (stratKeys.Length == 1)
                {
                    // Only one strat — show it as a dim label, no dropdown needed
                    ImGui.SameLine();
                    ImGui.TextDisabled(stratKeys[0]);
                }
            }
        }
        ImGui.EndChild();

        // ---- Add / Delete / Move ----
        if (ImGui.Button("+ Entry"))
        {
            tl.Entries.Add(new StratEntry { Label = "New Entry" });
            SelectEntry(cfg, tl.Entries.Count - 1);
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete##entry")
            && _selectedEntryIdx >= 0 && _selectedEntryIdx < tl.Entries.Count)
        {
            tl.Entries.RemoveAt(_selectedEntryIdx);
            _selectedEntryIdx = Math.Min(_selectedEntryIdx, tl.Entries.Count - 1);
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("↑##entryUp") && _selectedEntryIdx > 0)
        {
            var tmp = tl.Entries[_selectedEntryIdx];
            tl.Entries[_selectedEntryIdx]     = tl.Entries[_selectedEntryIdx - 1];
            tl.Entries[_selectedEntryIdx - 1] = tmp;
            _selectedEntryIdx--;
            cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("↓##entryDown")
            && _selectedEntryIdx >= 0 && _selectedEntryIdx < tl.Entries.Count - 1)
        {
            var tmp = tl.Entries[_selectedEntryIdx];
            tl.Entries[_selectedEntryIdx]     = tl.Entries[_selectedEntryIdx + 1];
            tl.Entries[_selectedEntryIdx + 1] = tmp;
            _selectedEntryIdx++;
            cfg.Save();
        }
    }

    private void SelectEntry(Configuration cfg, int idx)
    {
        _selectedEntryIdx = idx;
        if (idx < 0) return;

        var tl = cfg.Timelines[_selectedTimelineIdx];
        if (idx >= tl.Entries.Count) return;

        var e = tl.Entries[idx];
        _editEntryLabel = e.Label;
        _editEntryTime  = e.TriggerTime.ToString("F1");
        _editEntryNote  = e.Note;

        // Image path for the active variant (or default)
        var tl2 = cfg.Timelines[_selectedTimelineIdx];
        var img = tl2.ResolveImage(e);
        _editEntryImgPath = img?.Path ?? "";
    }

    // ---- Entry Editor ----

    private void DrawEntryEditor(Configuration cfg)
    {
        if (_selectedTimelineIdx < 0 || _selectedTimelineIdx >= cfg.Timelines.Count) return;
        if (_selectedEntryIdx    < 0) { ImGui.TextDisabled("← Select an entry to edit."); return; }

        var tl = cfg.Timelines[_selectedTimelineIdx];
        if (_selectedEntryIdx >= tl.Entries.Count) return;

        var e = tl.Entries[_selectedEntryIdx];

        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), $"Edit Entry: {e.Label}");
        ImGui.Spacing();

        // ---- Label ----
        ImGui.Text("Label:");
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("##entLabel", ref _editEntryLabel, 64))
        { e.Label = _editEntryLabel; cfg.Save(); }

        ImGui.SameLine();
        bool enabled = e.Enabled;
        if (ImGui.Checkbox("Enabled##entEnabled", ref enabled))
        { e.Enabled = enabled; cfg.Save(); }

        // ---- Trigger Time ----
        ImGui.Text("Trigger time (seconds):");
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputText("##entTime", ref _editEntryTime, 12))
        {
            if (float.TryParse(_editEntryTime, out float t))
            { e.TriggerTime = t; cfg.Save(); }
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"= {e.FormattedTime}");

        // ---- Pre-show + Duration ----
        float preShow = e.PreShowSeconds;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Pre-show (s)##preShow", ref preShow, 0f, 10f))
        { e.PreShowSeconds = preShow; cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("How early to show the image before the mechanic fires.");

        float dur = e.DisplayDuration;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Duration (s)##dur", ref dur, 0f, 30f))
        { e.DisplayDuration = dur; cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("0 = show until next trigger or /strat stop.");

        // ---- Role Filter ----
        int roleIdx = (int)e.RoleFilter;
        string[] roles = { "All", "Tank", "Healer", "DPS" };
        ImGui.SetNextItemWidth(120f);
        if (ImGui.Combo("Role filter##role", ref roleIdx, roles, roles.Length))
        { e.RoleFilter = (TargetRole)roleIdx; cfg.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Image section: per-variant ----
        DrawEntryImageSection(cfg, tl, e);

        // ---- Note ----
        ImGui.Spacing();
        ImGui.Text("Caption / Note:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##entNote", ref _editEntryNote, 256))
        { e.Note = _editEntryNote; cfg.Save(); }
    }

    // ---- Per-variant image section ----

    private void DrawEntryImageSection(Configuration cfg, StratTimeline tl, StratEntry e)
    {
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.9f, 1f), "Images");
        ImGui.Spacing();

        // For community strats, show which strat image is currently active
        // and let the user see/edit each strat's image separately
        bool isCommunity = tl.IsBuiltIn;

        if (isCommunity)
        {
            // Show a read-only table: one row per strat, with its image path and cache status
            ImGui.TextDisabled("Images are pulled from wtfdig.info. Switch strategy using the buttons at the top.");
            ImGui.Spacing();

            // Identify the active strat for this entry
            string? activeStrat = e.ActiveStratKey;

            foreach (var (key, img) in e.Images)
            {
                if (key == "default") continue; // skip the auto-generated fallback alias

                bool isActive = key == (activeStrat ?? e.Images.Keys.FirstOrDefault());
                bool isReady  = Plugin.ImageCache.IsReady(img);

                // Indicator dot: green = loaded, yellow = loading, grey = not started
                string dot    = isReady ? "●" : "○";
                var    dotCol = isReady
                    ? new Vector4(0.3f, 1f, 0.3f, 1f)
                    : new Vector4(0.6f, 0.6f, 0.2f, 1f);

                // Active strat row gets a highlight
                if (isActive)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));

                ImGui.TextColored(dotCol, dot);
                ImGui.SameLine();
                ImGui.Text(isActive ? $"[{key}] ← active" : $"[{key}]");
                ImGui.SameLine();

                string shown = img.Path.Length > 55 ? "..." + img.Path[^52..] : img.Path;
                ImGui.TextDisabled(shown);

                if (isActive) ImGui.PopStyleColor();

                // Refresh button for individual strat image
                ImGui.SameLine();
                if (ImGui.SmallButton($"↺##ref_{key}"))
                    Plugin.ImageCache.ForceRefresh(img);
            }
        }
        else
        {
            // Non-community timeline: original per-variant edit UI
            foreach (var variant in tl.Variants)
            {
                bool hasImg = e.Images.TryGetValue(variant.Id, out var img);
                string displayPath = hasImg ? img!.Path : "(none)";
                string shown = displayPath.Length > 50 ? "..." + displayPath[^47..] : displayPath;

                bool isReady = hasImg && Plugin.ImageCache.IsReady(img!);
                string dot   = isReady ? "●" : (hasImg ? "○" : " ");
                var dotCol   = isReady
                    ? new Vector4(0.3f, 1f, 0.3f, 1f)
                    : new Vector4(0.6f, 0.6f, 0.6f, 1f);

                ImGui.TextColored(dotCol, dot);
                ImGui.SameLine();
                ImGui.Text($"[{variant.Name}]");
                ImGui.SameLine();
                ImGui.TextDisabled(shown);
                ImGui.SameLine();

                if (ImGui.SmallButton($"Edit##img_{variant.Id}"))
                {
                    _editEntryImgPath      = img?.Path ?? "";
                    _editingImageVariantId = variant.Id;
                }

                if (hasImg)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"↺##ref_{variant.Id}"))
                        Plugin.ImageCache.ForceRefresh(img!);
                }
            }

            // Inline image path editor
            if (_editingImageVariantId != null)
            {
                var variant = tl.Variants.Find(v => v.Id == _editingImageVariantId);
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                    $"Editing image for [{variant?.Name ?? _editingImageVariantId}]:");

                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 120f);
                ImGui.InputText("##imgPath", ref _editEntryImgPath, 512);
                ImGui.SameLine();

                if (ImGui.Button("Save##saveImg"))
                {
                    if (!e.Images.TryGetValue(_editingImageVariantId, out var imgEntry))
                    {
                        imgEntry = new StratImage();
                        e.Images[_editingImageVariantId] = imgEntry;
                    }
                    imgEntry.Path   = _editEntryImgPath;
                    imgEntry.Source = _editEntryImgPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? ImageSource.Url : ImageSource.LocalFile;

                    _editingImageVariantId = null;
                    cfg.Save();
                    Plugin.ImageCache.ForceRefresh(imgEntry);
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancelImg"))
                    _editingImageVariantId = null;

                ImGui.Spacing();
                ImGui.TextDisabled("Paste a URL (https://...) or a local file path.");
            }
        }
    }

    // Extra state for the inline image editor
    private string? _editingImageVariantId = null;

    // =========================================================================
    // TAB: SETTINGS
    // =========================================================================

    private void DrawSettingsTab()
    {
        var cfg = Plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Overlay Position");
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Edit Mode button — lives HERE, not on the game screen ----
        var overlayWin = Plugin.OverlayWindow;
        if (overlayWin.EditMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.8f, 0.4f, 0.1f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.5f, 0.1f, 1.0f));
            if (ImGui.Button("✎ Done Editing Overlay Position"))
            {
                overlayWin.EditMode = false;
                Plugin.Configuration.Save();
            }
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "← Drag the overlay panel to reposition  |  Scroll wheel to resize");
        }
        else
        {
            if (ImGui.Button("✎ Edit Overlay Position"))
                overlayWin.EditMode = true;
            ImGui.SameLine();
            ImGui.TextDisabled("Click to drag/resize the overlay on screen.");
        }
        ImGui.Spacing();

        // ---- Free-position toggle ----
        bool freePos = cfg.OverlayFreePosition;
        if (ImGui.Checkbox("Free positioning (drag & drop)##freePos", ref freePos))
        { cfg.OverlayFreePosition = freePos; cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("Uncheck to use anchor corners instead.");

        if (cfg.OverlayFreePosition)
        {
            float fx = cfg.OverlayFreeX, fy = cfg.OverlayFreeY;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat("X##freeX", ref fx, 1f, 10f, "%.0f"))
            { cfg.OverlayFreeX = fx; cfg.Save(); }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat("Y##freeY", ref fy, 1f, 10f, "%.0f"))
            { cfg.OverlayFreeY = fy; cfg.Save(); }
        }
        else
        {
            // Anchor
            string[] anchors = { "Top-Left", "Top-Right", "Bottom-Left", "Bottom-Right" };
            int anchor = cfg.OverlayAnchor;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.Combo("Anchor corner##anchor", ref anchor, anchors, anchors.Length))
            { cfg.OverlayAnchor = anchor; cfg.Save(); }
            ImGui.SameLine();
            ImGui.TextDisabled("Where images stack from.");

            float offX = cfg.OverlayOffsetX;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.SliderFloat("Offset X (px)##offX", ref offX, 0f, 600f))
            { cfg.OverlayOffsetX = offX; cfg.Save(); }

            float offY = cfg.OverlayOffsetY;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.SliderFloat("Offset Y (px)##offY", ref offY, 0f, 400f))
            { cfg.OverlayOffsetY = offY; cfg.Save(); }
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Image Appearance");
        ImGui.Separator();
        ImGui.Spacing();

        float imgW = cfg.OverlayImageWidth;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("Image width (px)##imgW", ref imgW, 100f, 900f))
        { cfg.OverlayImageWidth = imgW; cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("Height auto-scales from aspect ratio.");

        float bgAlpha = cfg.OverlayBgAlpha;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat("Background opacity##bgAlpha", ref bgAlpha, 0f, 1f))
        { cfg.OverlayBgAlpha = bgAlpha; cfg.Save(); }

        float fade = cfg.OverlayFadeDuration;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Fade-in (s)##fade", ref fade, 0f, 2f))
        { cfg.OverlayFadeDuration = fade; cfg.Save(); }

        bool showCaption = cfg.OverlayShowCaption;
        if (ImGui.Checkbox("Show entry caption below image", ref showCaption))
        { cfg.OverlayShowCaption = showCaption; cfg.Save(); }

        bool showVariantLabel = cfg.OverlayShowVariantLabel;
        if (ImGui.Checkbox("Show active variant watermark", ref showVariantLabel))
        { cfg.OverlayShowVariantLabel = showVariantLabel; cfg.Save(); }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Auto-Load");
        ImGui.Separator();
        ImGui.Spacing();

        bool autoLoad = cfg.AutoLoadTimelines;
        if (ImGui.Checkbox("Auto-load timeline on zone entry", ref autoLoad))
        { cfg.AutoLoadTimelines = autoLoad; cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("Requires TerritoryID set on the timeline.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Community Strats");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Built-in strat timelines sourced from wtfdig.info. Marked with 🔒 in the list.");
        ImGui.Spacing();

        int builtInCount = cfg.Timelines.FindAll(t => t.IsBuiltIn).Count;
        ImGui.Text($"Built-in timelines: {builtInCount}");
        ImGui.SameLine();

        if (ImGui.Button("Reset / Regenerate Community Strats"))
        {
            cfg.CommunityStratsGenerated = false;
            Data.CommunityStrats.Generate(cfg, Plugin.Log);
            _selectedTimelineIdx = -1;
            _selectedVariantIdx  = -1;
            _selectedEntryIdx    = -1;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Re-creates all 🔒 timelines from the manifest.");
    }

    // =========================================================================
    // TAB: DEBUG
    // =========================================================================

    private void DrawDebugTab()
    {
        var engine = Plugin.Engine;
        var cfg    = Plugin.Configuration;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Engine State");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text($"Timer running:  {(engine.IsRunning ? "YES" : "no")}");
        ImGui.Text($"Fight time:     {engine.CurrentTime:F2}s");
        ImGui.Text($"Active timeline:{engine.ActiveTimelineName ?? "(none)"}");
        ImGui.Text($"Current zone:   {Plugin.CurrentTerritoryId}");

        ImGui.Spacing();

        // Manual start/stop buttons for testing without being in a real duty
        if (engine.IsRunning)
        {
            if (ImGui.Button("Manual Stop"))  engine.ManualStop();
        }
        else
        {
            if (ImGui.Button("Manual Start"))
            {
                var tl = cfg.Timelines.Find(t => t.Id == cfg.SelectedTimelineId);
                if (tl != null)
                {
                    engine.LoadTimeline(tl);
                    Plugin.ImageCache.PreWarm(tl);
                }
                engine.ManualStart();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cycle Variant")) engine.CycleVariant();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Image Cache");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("(Cache lives in pluginConfigs/StratOverlay/cache/)");
        ImGui.Spacing();

        // Show cache-ready status for entries in the selected timeline
        var selectedTl = cfg.Timelines.Find(t => t.Id == cfg.SelectedTimelineId);
        if (selectedTl != null)
        {
            int ready = 0, total = 0;
            foreach (var entry in selectedTl.Entries)
            foreach (var (_, img) in entry.Images)
            {
                total++;
                if (Plugin.ImageCache.IsReady(img)) ready++;
            }
            ImGui.Text($"Selected timeline: {ready}/{total} images cached/ready.");

            if (ImGui.Button("Force Prewarm Now"))
            {
                Plugin.ImageCache.PreWarm(selectedTl);
                AddDebugLine($"[PREWARM] Kicked off prewarm for \"{selectedTl.Name}\"");
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "URL Download Test");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Use this to verify a wtfdig or custom URL is reachable.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80f);
        ImGui.InputText("##testUrl", ref _testUrl, 512);
        ImGui.SameLine();

        if (_testUrlPending)
        {
            ImGui.TextDisabled("Testing...");
        }
        else if (ImGui.Button("Test URL"))
        {
            _testUrlResult  = "";
            _testUrlPending = true;
            var urlCopy     = _testUrl;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                _testUrlResult  = await Plugin.ImageCache.TestDownloadAsync(urlCopy);
                _testUrlPending = false;
                AddDebugLine($"[URL TEST] {urlCopy} → {_testUrlResult}");
            });
        }

        if (!string.IsNullOrEmpty(_testUrlResult))
        {
            bool ok = _testUrlResult.StartsWith("OK");
            ImGui.TextColored(ok ? new Vector4(0.3f,1f,0.3f,1f) : new Vector4(1f,0.3f,0.3f,1f),
                _testUrlResult);
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.85f, 0.3f, 1f), "Event Log");
        ImGui.Separator();
        ImGui.Spacing();

        // Scrollable log of recent engine events
        if (ImGui.BeginChild("##DebugLog", new Vector2(0, 0), true))
        {
            foreach (var line in _debugLog)
                ImGui.TextUnformatted(line);

            // Auto-scroll to bottom when new lines arrive
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10f)
                ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }
}
