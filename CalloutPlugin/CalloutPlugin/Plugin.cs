// =============================================================================
// Plugin.cs — Callout Plugin Entrypoint
// =============================================================================
//
// Fight timeline callout tool. Fires visual alerts at user-defined times
// during combat encounters. Auto-detects combat start via ICondition.
//
// Commands:
//   /callout        — Toggle the timeline editor window
//   /callout start  — Manually start the timer (for testing)
//   /callout stop   — Manually stop the timer
// =============================================================================

using System;
using System.Linq;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using CalloutPlugin.Data;
using CalloutPlugin.Windows;

namespace CalloutPlugin;

public sealed class Plugin : IDalamudPlugin
{
    // ---- Dalamud Services ----
    private readonly IDalamudPluginInterface PluginInterface;
    private readonly ICommandManager CommandManager;
    public readonly IPluginLog Log;
    public readonly IClientState ClientState;

    // ---- Our Systems ----
    public readonly WindowSystem WindowSystem = new("CalloutPlugin");
    public Configuration Configuration { get; init; }
    public TimelineEngine Engine { get; init; }

    /// <summary>
    /// A large pre-baked font used for callout overlay text.
    /// 
    /// WHY: ImGui fonts are rasterized (baked) at a fixed pixel size when the atlas
    /// is built. Stretching them with SetWindowFontScale() blurs them, because you're
    /// just scaling up a small bitmap. 
    /// 
    /// The fix is to use a font that was baked at a large size to begin with.
    /// Dalamud's IFontAtlas lets us request the game's own Axis font at any size.
    /// We ask for it at the user's configured size — sharp because it's native.
    /// 
    /// IFontHandle is Dalamud's wrapper around an ImFont* — we Push() it before
    /// drawing text and Pop() it after, just like ImGui's PushFont/PopFont.
    /// </summary>
    public IFontHandle AlertFont { get; private set; }

    /// <summary>
    /// Rebuilds the alert font at a new size. Called from the settings UI when
    /// the user changes AlertFontSize. Disposes the old handle first.
    /// </summary>
    public void RebuildAlertFont()
    {
        AlertFont.Dispose();
        AlertFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, Configuration.AlertFontSize));
        Log.Info($"Alert font rebuilt at {Configuration.AlertFontSize}px.");
    }

    // ---- Windows ----
    private readonly MainWindow MainWindow;
    private readonly AlertOverlay AlertOverlay;
    public readonly TimelineView TimelineView;
    public readonly CooldownTracker CooldownTracker;
    public readonly FflogsImportWindow FflogsImportWindow;

    private const string CommandName = "/callout";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IClientState clientState,
        ICondition condition,
        IFramework framework)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Log = pluginLog;
        ClientState = clientState;

        // Load config
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // Create the timeline engine
        Engine = new TimelineEngine(condition, clientState, framework, pluginLog);

        // Build a large sharp font for the alert overlay.
        // NewGameFontHandle requests FFXIV's built-in "Axis" font at a specific size.
        // Axis is the main UI font in the game — it looks clean and familiar to players.
        // We use the user's configured size (default 36px) — sharp because it's native.
        AlertFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, Configuration.AlertFontSize));

        // Auto-load the selected timeline if one was saved
        if (Configuration.SelectedTimelineId != null)
        {
            var selected = Configuration.Timelines
                .FirstOrDefault(t => t.Id == Configuration.SelectedTimelineId);
            if (selected != null)
                Engine.LoadTimeline(selected);
        }

        // Create windows
        MainWindow        = new MainWindow(this);
        AlertOverlay      = new AlertOverlay(this);
        TimelineView      = new TimelineView(this);
        CooldownTracker   = new CooldownTracker(this);
        FflogsImportWindow = new FflogsImportWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AlertOverlay);
        WindowSystem.AddWindow(TimelineView);
        WindowSystem.AddWindow(CooldownTracker);
        WindowSystem.AddWindow(FflogsImportWindow);

        // Register commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Callout Plugin editor. Use '/callout start' to test, '/callout stop' to stop."
        });

        // Hook Dalamud UI
        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

        // Subscribe to territory change for auto-loading timelines
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Info("CalloutPlugin loaded!");
    }

    // =========================================================================
    // DISPOSE
    // =========================================================================
    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        AlertOverlay.Dispose();
        TimelineView.Dispose();
        CooldownTracker.Dispose();
        Engine.Dispose();

        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        AlertFont.Dispose();

        Log.Info("CalloutPlugin unloaded.");
    }

    // =========================================================================
    // COMMAND HANDLER
    // =========================================================================
    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();

        switch (trimmed)
        {
            case "start":
                // Bug fix: ensure the selected timeline is loaded into the engine
                // before starting. Previously, if you were already in the zone when
                // you typed /callout start, the engine had no active timeline and
                // would run the timer but never fire any callouts.
                if (Configuration.SelectedTimelineId != null)
                {
                    var selected = Configuration.Timelines
                        .FirstOrDefault(t => t.Id == Configuration.SelectedTimelineId);
                    if (selected != null)
                    {
                        Engine.LoadTimeline(selected);
                        Log.Info($"Loaded timeline \"{selected.Name}\" for manual start.");
                    }
                    else
                    {
                        Log.Warning("Selected timeline ID not found in config — no timeline loaded.");
                    }
                }
                else
                {
                    Log.Warning("No timeline selected. Select one in the Callout editor first (/callout).");
                }
                Engine.ManualStart();
                break;

            case "stop":
                Engine.ManualStop();
                break;

            case "timeline":
                TimelineView.IsOpen = !TimelineView.IsOpen;
                break;

            default:
                ToggleMainUI();
                break;
        }
    }

    // =========================================================================
    // TERRITORY CHANGE — Auto-load timelines by duty
    // =========================================================================
    private void OnTerritoryChanged(ushort territoryTypeId)
    {
        if (!Configuration.AutoLoadTimelines)
            return;

        // Look for a timeline that matches this territory
        var match = Configuration.Timelines
            .FirstOrDefault(t => t.Enabled && t.TerritoryTypeId == territoryTypeId);

        if (match != null)
        {
            Engine.LoadTimeline(match);
            Configuration.SelectedTimelineId = match.Id;
            Log.Info($"Auto-loaded timeline \"{match.Name}\" for territory {territoryTypeId}");
        }
    }

    // =========================================================================
    // UI
    // =========================================================================
    private void DrawUI() => WindowSystem.Draw();
    public void ToggleMainUI() => MainWindow.IsOpen = !MainWindow.IsOpen;

    /// <summary>
    /// Returns the TerritoryType ID of the zone the player is currently in.
    /// This is read directly from Dalamud's IClientState service each time it's called.
    /// The UI uses this to show the player exactly what ID they're standing in,
    /// so they can copy it straight into a timeline's Territory ID field.
    /// Returns 0 if not logged in.
    /// </summary>
    public ushort CurrentTerritoryId => ClientState.TerritoryType;
}
