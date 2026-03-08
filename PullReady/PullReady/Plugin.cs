// =============================================================================
// Plugin.cs — PullReady Entrypoint
// =============================================================================
//
// Registers services, commands, and windows with Dalamud.
//
// Commands:
//   /pullready     — Toggle the main checklist window
//   /prsettings    — Open the settings window
// =============================================================================

using System;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using PullReady.Windows;

namespace PullReady;

public sealed class Plugin : IDalamudPlugin
{
    // ---- Dalamud Services ----
    // These are injected by Dalamud's IoC container when the plugin is loaded.
    // [PluginService] tells Dalamud to automatically fill these in.
    private readonly IDalamudPluginInterface PluginInterface;
    private readonly ICommandManager CommandManager;

    // These are public so windows can reach them without circular references.
    public readonly IPluginLog    Log;
    public readonly IClientState  ClientState;
    public readonly IPartyList    PartyList;
    public readonly IGameInventory GameInventory;
    public readonly IObjectTable  ObjectTable;
    public readonly ICondition    Condition;
    public readonly IChatGui      ChatGui;

    // ---- Config + Windows ----
    public Configuration       Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("PullReady");
    public readonly PullReadyWindow MainWindow;
    public readonly ConfigWindow    ConfigWindow;

    private const string CmdMain     = "/pullready";
    private const string CmdSettings = "/prsettings";

    // =========================================================================
    // CONSTRUCTOR — called once when Dalamud loads the plugin
    // =========================================================================
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IClientState clientState,
        IPartyList partyList,
        IGameInventory gameInventory,
        IObjectTable objectTable,
        ICondition condition,
        IChatGui chatGui)
    {
        PluginInterface = pluginInterface;
        CommandManager  = commandManager;
        Log             = pluginLog;
        ClientState     = clientState;
        PartyList       = partyList;
        GameInventory   = gameInventory;
        ObjectTable     = objectTable;
        Condition       = condition;
        ChatGui         = chatGui;

        // Load saved config (or create a fresh one)
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // Create windows
        MainWindow   = new PullReadyWindow(this);
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        // Register slash commands
        CommandManager.AddHandler(CmdMain, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Toggle the PullReady checklist window."
        });
        CommandManager.AddHandler(CmdSettings, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open PullReady settings."
        });

        // Hook Dalamud UI draw + settings button
        PluginInterface.UiBuilder.Draw        += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMain;

        // Auto-open + auto-check on zone-in
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Info("PullReady loaded!");
    }

    // =========================================================================
    // DISPOSE — called when Dalamud unloads the plugin (logout, /xlplugins, etc.)
    // =========================================================================
    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CmdMain);
        CommandManager.RemoveHandler(CmdSettings);

        PluginInterface.UiBuilder.Draw        -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMain;

        ClientState.TerritoryChanged -= OnTerritoryChanged;

        Log.Info("PullReady unloaded.");
    }

    // =========================================================================
    // COMMANDS
    // =========================================================================

    private void OnMainCommand(string command, string args)     => ToggleMain();
    private void OnSettingsCommand(string command, string args) => OpenConfig();

    // =========================================================================
    // TERRITORY CHANGE — auto-open and auto-run checks on zone-in
    // =========================================================================

    /// <summary>
    /// Fires whenever the player changes zones.
    /// If AutoOpenOnZoneIn is enabled, opens the window and runs a fresh check.
    ///
    /// We use ICondition to determine if we actually entered a DUTY (instanced
    /// content) vs. just walking between open-world areas.
    /// BoundByDuty = inside a duty instance (raid, trial, dungeon, etc.)
    /// </summary>
    private void OnTerritoryChanged(ushort territoryId)
    {
        if (!Configuration.AutoOpenOnZoneIn)
            return;

        // Only auto-open inside duties, not open-world zones
        // Note: BoundByDuty fires a tiny bit AFTER the zone-in event.
        // We use a short delay via the territory change itself — if the zone
        // changed AND we're inside a duty, open up.
        //
        // In practice the condition flag is set before this event fires, so
        // Condition[ConditionFlag.BoundByDuty] is already true here.
        if (Condition[ConditionFlag.BoundByDuty])
        {
            MainWindow.IsOpen = true;
            MainWindow.RunChecks();   // Auto-refresh the results
            Log.Debug($"PullReady: zone-in to {territoryId}, auto-checked.");
        }
    }

    // =========================================================================
    // UI
    // =========================================================================

    private void DrawUI()   => WindowSystem.Draw();
    private void ToggleMain() => MainWindow.IsOpen   = !MainWindow.IsOpen;
    private void OpenConfig() => ConfigWindow.IsOpen = true;
}
