// =============================================================================
// Plugin.cs — The Main Entrypoint for CurrencyBoard
// =============================================================================
//
// This is the FIRST file Dalamud looks at when loading your plugin. It scans
// your DLL for a class that implements "IDalamudPlugin" and initializes it.
//
// WHAT HAPPENS WHEN YOUR PLUGIN LOADS:
// 1. Dalamud finds this class (because it implements IDalamudPlugin)
// 2. Dalamud calls the constructor, automatically providing ("injecting") any
//    Dalamud services you list as constructor parameters
// 3. Your constructor sets everything up (registers commands, creates windows)
// 4. When the plugin is unloaded, Dalamud calls Dispose() to clean up
//
// FOR PYTHON DEVELOPERS: Think of this like your __init__.py / main entry point.
// FOR C++ DEVELOPERS: This is like your main() + class constructor/destructor.
// =============================================================================

// "using" statements = Python's "import" or C++'s "#include"
// They tell the compiler which libraries/namespaces we want to use.
using System;
using System.Numerics;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using CurrencyBoard.Data;
using CurrencyBoard.Windows;

// A "namespace" is like a Python module or C++ namespace — it groups related
// code together and prevents naming conflicts with other plugins.
namespace CurrencyBoard;

// =============================================================================
// THE PLUGIN CLASS
// =============================================================================
// "sealed" = this class cannot be inherited from (a safety/performance hint)
// ": IDalamudPlugin" = this class implements the IDalamudPlugin interface,
//   which is how Dalamud knows "this is a plugin entrypoint"
// IDalamudPlugin also inherits IDisposable, so we MUST have a Dispose() method
// =============================================================================
public sealed class Plugin : IDalamudPlugin
{
    // =========================================================================
    // DALAMUD SERVICES (Dependency Injection)
    // =========================================================================
    // These are provided automatically by Dalamud when it creates our plugin.
    // We just declare them as constructor parameters and Dalamud fills them in.
    //
    // Think of it like this:
    //   Python: self.command_manager = dalamud.get_service("CommandManager")
    //   C++:    auto commandManager = dalamud->GetService<ICommandManager>();
    //   C# DI:  Just put it in the constructor and it appears!
    // =========================================================================

    /// <summary>
    /// Core plugin interface — gives us access to config directories, 
    /// plugin metadata, and UI registration.
    /// </summary>
    private readonly IDalamudPluginInterface PluginInterface;

    /// <summary>
    /// Lets us register slash commands (like /currboard).
    /// </summary>
    private readonly ICommandManager CommandManager;

    /// <summary>
    /// Logging service — prints debug messages to the Dalamud console.
    /// Similar to Python's logging module or C++'s std::cout for debugging.
    /// </summary>
    private readonly IPluginLog Log;

    // =========================================================================
    // OUR CUSTOM OBJECTS
    // =========================================================================

    /// <summary>
    /// The WindowSystem manages all of our ImGui windows. Dalamud's UI is built
    /// on ImGui (Immediate Mode GUI) — instead of defining UI elements once,
    /// you redraw them every frame. The WindowSystem handles this loop for us.
    /// </summary>
    public readonly WindowSystem WindowSystem = new("CurrencyBoard");

    /// <summary>
    /// User settings that persist between game sessions (saved as JSON on disk).
    /// </summary>
    public Configuration Configuration { get; init; }

    /// <summary>
    /// Reads currency data from the game. This is our "data layer" —
    /// it handles all communication with the game, and our UI just reads from it.
    /// </summary>
    public CurrencyTracker CurrencyTracker { get; init; }

    /// <summary>
    /// The main HUD overlay window that displays currency information.
    /// </summary>
    private readonly MainWindow MainWindow;

    /// <summary>
    /// The settings/configuration window, opened via /currboard config.
    /// </summary>
    private readonly ConfigWindow ConfigWindow;

    // The slash command string — defined as a constant so we only type it once.
    // "const" in C# is like "constexpr" in C++ or a module-level CONSTANT in Python.
    private const string CommandName = "/currboard";

    // =========================================================================
    // CONSTRUCTOR — Called once when Dalamud loads your plugin
    // =========================================================================
    // Notice the parameters: Dalamud automatically provides these services.
    // This pattern is called "Dependency Injection" (DI). You don't need to
    // create these objects yourself — just ask for them and they appear.
    // =========================================================================
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IDataManager dataManager,
        IClientState clientState,
        IFramework framework)
    {
        // Store the injected services so we can use them later.
        // In Python this would be like: self.plugin_interface = plugin_interface
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Log = pluginLog;

        // Load saved settings from disk, or create defaults if none exist.
        // GetPluginConfig() reads from Dalamud's plugin config directory.
        // The "??" operator means "if null, use this instead" — like Python's "or".
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // Initialize our data tracker — this reads currency info from the game.
        CurrencyTracker = new CurrencyTracker(dataManager, clientState, framework, pluginLog);

        // Create our windows (UI elements drawn by ImGui).
        MainWindow = new MainWindow(this);
        ConfigWindow = new ConfigWindow(this);

        // Register windows with the WindowSystem so they get drawn each frame.
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        // Register our slash command: /currboard
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            // This help text shows up when players type /xlhelp
            HelpMessage = "Toggle the Currency Board. Use '/currboard config' to open settings."
        });

        // Hook into Dalamud's draw loop — this is what actually makes our 
        // windows appear on screen. Every frame, Dalamud calls our Draw method.
        PluginInterface.UiBuilder.Draw += DrawUI;

        // Hook into the "open config" button in the plugin installer UI.
        // When a user clicks the gear icon next to our plugin name, this fires.
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        // Hook into the "open main UI" action — this is what Dalamud calls when
        // a user clicks the plugin name itself in the installer. Without this,
        // Dalamud shows a warning that we have no main UI callback.
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

        // Open the main window by default on plugin load.
        MainWindow.IsOpen = true;

        Log.Info("CurrencyBoard loaded successfully!");
    }

    // =========================================================================
    // DISPOSE — Called when Dalamud unloads your plugin (cleanup)
    // =========================================================================
    // This is CRITICAL. If you don't clean up properly, you'll leak resources,
    // crash the game, or interfere with other plugins.
    //
    // FOR C++ DEVELOPERS: This is like your destructor (~Plugin).
    // FOR PYTHON DEVELOPERS: This is like __del__ but actually reliable.
    // =========================================================================
    public void Dispose()
    {
        // Unregister our windows from the draw loop
        WindowSystem.RemoveAllWindows();

        // Dispose our window objects (they may hold textures or other resources)
        MainWindow.Dispose();
        ConfigWindow.Dispose();

        // Clean up our data tracker
        CurrencyTracker.Dispose();

        // Remove our slash command so it doesn't linger after unload
        CommandManager.RemoveHandler(CommandName);

        // Unhook from Dalamud's draw events
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;

        Log.Info("CurrencyBoard unloaded.");
    }

    // =========================================================================
    // COMMAND HANDLER — Called when a player types /currboard in chat
    // =========================================================================
    // "args" contains anything typed after the command name.
    // e.g., "/currboard config" → args = "config"
    // e.g., "/currboard"        → args = ""
    // =========================================================================
    private void OnCommand(string command, string args)
    {
        // Trim whitespace and convert to lowercase for easier comparison.
        // This is defensive programming — handles "/currboard  CONFIG " etc.
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "config":
            case "settings":
                // Toggle the config window
                ToggleConfigUI();
                break;

            default:
                // Toggle the main window (no args, or unrecognized args)
                ToggleMainUI();
                break;
        }
    }

    // =========================================================================
    // UI METHODS
    // =========================================================================

    /// <summary>
    /// Called every frame by Dalamud's UI system. This is where all ImGui
    /// drawing happens. The WindowSystem handles calling Draw() on each
    /// registered window automatically.
    /// </summary>
    private void DrawUI() => WindowSystem.Draw();

    /// <summary>
    /// Toggles the main HUD window on/off.
    /// "IsOpen" is a property on every Dalamud Window — set it to true to show,
    /// false to hide. The WindowSystem checks this each frame.
    /// </summary>
    public void ToggleMainUI() => MainWindow.IsOpen = !MainWindow.IsOpen;

    /// <summary>
    /// Toggles the configuration window on/off.
    /// </summary>
    public void ToggleConfigUI() => ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
}
