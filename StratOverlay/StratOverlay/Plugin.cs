// =============================================================================
// Plugin.cs — StratOverlay Entry Point
// =============================================================================
//
// Standalone Dalamud plugin that displays strat images on screen during FFXIV
// combat. Works alongside CalloutPlugin but has zero dependency on it.
//
// Commands:
//   /strat                  — Toggle the config window
//   /strat start            — Manually start the fight timer (testing)
//   /strat stop             — Manually stop the timer
//   /strat variant next     — Cycle to the next strategy variant
//   /strat variant <name>   — Switch to a named variant (case-insensitive)
// =============================================================================

using System;
using System.Linq;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using StratOverlay.Data;
using StratOverlay.Windows;

namespace StratOverlay;

public sealed class Plugin : IDalamudPlugin
{
    // ---- Dalamud Services ----
    private readonly IDalamudPluginInterface PluginInterface;
    private readonly ICommandManager         CommandManager;
    public  readonly IPluginLog              Log;
    public  readonly IClientState            ClientState;
    public  readonly IPlayerState            PlayerState;


    // ---- Our Systems ----
    public readonly WindowSystem  WindowSystem = new("StratOverlay");
    public          Configuration Configuration { get; init; }
    public          StratEngine   Engine        { get; init; }
    public          ImageCache    ImageCache    { get; init; }

    // ---- Windows ----
    private readonly MainWindow         MainWindow;
    // Public so MainWindow's Settings tab can toggle edit mode directly
    public  readonly StratOverlayWindow OverlayWindow;

    private const string CommandName = "/strat";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager         commandManager,
        IPluginLog              pluginLog,
        IClientState            clientState,
        IPlayerState            playerState,
        ICondition              condition,
        IFramework              framework,
        IObjectTable            objectTable,
        IGameInteropProvider    gameInterop,
        ITextureProvider        textureProvider)
    {
        PluginInterface = pluginInterface;
        CommandManager  = commandManager;
        Log             = pluginLog;
        ClientState     = clientState;
        PlayerState     = playerState;

        // Load persisted config
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // Clear any stale LoadFailed flags that may have been incorrectly
        // persisted by older versions of the plugin. LoadFailed is runtime-only
        // state — it must always start as false so images can be retried.
        foreach (var tl in Configuration.Timelines)
            foreach (var entry in tl.Entries)
                foreach (var img in entry.Images.Values)
                    img.LoadFailed = false;

        // Generate built-in community strat timelines on first launch
        CommunityStrats.GenerateIfNeeded(Configuration, pluginLog);

        // Create the engine (registers Framework.Update + game hooks internally)
        Engine = new StratEngine(condition, playerState, framework, objectTable, gameInterop, pluginLog);

        // Create the image cache — pass the plugin's config directory so cached
        // files land in %AppData%\XIVLauncher\pluginConfigs\StratOverlay\cache\
        ImageCache = new ImageCache(textureProvider, pluginLog,
            pluginInterface.GetPluginConfigDirectory());

        // Auto-load the selected timeline if one was saved from a previous session
        if (Configuration.SelectedTimelineId != null)
        {
            var selected = Configuration.Timelines
                .FirstOrDefault(t => t.Id == Configuration.SelectedTimelineId);
            if (selected != null)
            {
                Engine.LoadTimeline(selected);
                ImageCache.PreWarm(selected); // kick off background downloads
            }
        }

        // Create windows
        MainWindow    = new MainWindow(this);
        OverlayWindow = new StratOverlayWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(OverlayWindow);

        // Register slash command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Toggle the Strat Overlay config window. " +
                "'/strat start' to test, '/strat variant next' to cycle strategies."
        });

        // Hook Dalamud UI draw + settings shortcut
        PluginInterface.UiBuilder.Draw         += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUI;

        // Auto-load timelines when changing zones
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Info("StratOverlay loaded!");
    }

    // =========================================================================
    // DISPOSE
    // =========================================================================
    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        OverlayWindow.Dispose();
        Engine.Dispose();
        ImageCache.Dispose();

        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        Log.Info("StratOverlay unloaded.");
    }

    // =========================================================================
    // COMMAND HANDLER
    // =========================================================================
    private void OnCommand(string command, string args)
    {
        var parts   = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var primary = parts.Length > 0 ? parts[0] : "";

        switch (primary)
        {
            case "start":
                // Ensure the selected timeline is loaded before starting
                EnsureTimelineLoaded();
                Engine.ManualStart();
                break;

            case "stop":
                Engine.ManualStop();
                break;

            case "variant":
                // /strat variant next  — cycle through variants
                // /strat variant <name> — switch by name (case-insensitive)
                if (parts.Length < 2) { ToggleMainUI(); break; }

                if (parts[1] == "next")
                {
                    Engine.CycleVariant();
                }
                else
                {
                    // Find variant by name (join remaining parts to support names with spaces)
                    var variantName = string.Join(" ", parts[1..]);
                    var timeline    = GetSelectedTimeline();
                    var variant     = timeline?.Variants
                        .Find(v => v.Name.ToLowerInvariant() == variantName);

                    if (variant != null)
                        Engine.SetVariant(variant.Id);
                    else
                        Log.Warning($"[StratOverlay] Variant \"{variantName}\" not found.");
                }
                break;

            default:
                // /strat with no args toggles the main window
                ToggleMainUI();
                break;
        }
    }

    // =========================================================================
    // TERRITORY CHANGE — Auto-load timelines by duty
    // =========================================================================
    private void OnTerritoryChanged(ushort territoryTypeId)
    {
        if (!Configuration.AutoLoadTimelines) return;

        var match = Configuration.Timelines
            .FirstOrDefault(t => t.Enabled && t.TerritoryTypeId == territoryTypeId);

        if (match != null)
        {
            Engine.LoadTimeline(match);
            ImageCache.PreWarm(match);
            Configuration.SelectedTimelineId = match.Id;
            Log.Info($"[StratOverlay] Auto-loaded \"{match.Name}\" for territory {territoryTypeId}.");
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>Returns the currently selected StratTimeline, or null.</summary>
    public StratTimeline? GetSelectedTimeline()
        => Configuration.Timelines
            .FirstOrDefault(t => t.Id == Configuration.SelectedTimelineId);

    /// <summary>Zone the player is currently in.</summary>
    public ushort CurrentTerritoryId => ClientState.TerritoryType;

    private void EnsureTimelineLoaded()
    {
        if (Configuration.SelectedTimelineId == null) return;
        var selected = GetSelectedTimeline();
        if (selected != null)
            Engine.LoadTimeline(selected);
    }

    // =========================================================================
    // UI
    // =========================================================================
    private void DrawUI()     => WindowSystem.Draw();
    public  void ToggleMainUI() => MainWindow.IsOpen = !MainWindow.IsOpen;
}
