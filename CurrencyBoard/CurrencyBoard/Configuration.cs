// =============================================================================
// Configuration.cs — User Settings (Persisted to Disk)
// =============================================================================
//
// This class holds all user-configurable settings for the plugin. Dalamud
// automatically serializes this to JSON and saves it to the plugin's config
// directory. When the plugin loads, it reads this file back in.
//
// HOW IT WORKS:
// 1. Your plugin calls PluginInterface.GetPluginConfig() on startup
// 2. Dalamud reads the saved JSON file and deserializes it into this class
// 3. When you call Save(), Dalamud serializes this class back to JSON
//
// FOR PYTHON DEVELOPERS: This is like a config dict that auto-saves to a
//   JSON file, similar to: json.dump(config, open("config.json", "w"))
// FOR C++ DEVELOPERS: This is a serializable struct with automatic file I/O.
//
// IMPORTANT: All properties you want saved MUST be public and have both
//   get and set accessors. Private fields won't be serialized.
// =============================================================================

using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CurrencyBoard;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // =========================================================================
    // DISPLAY SETTINGS
    // =========================================================================

    public bool IsMainWindowVisible { get; set; } = true;
    public float WindowOpacity { get; set; } = 0.9f;
    public bool CompactMode { get; set; } = false;
    public bool LockWindowPosition { get; set; } = false;

    // =========================================================================
    // CURRENCY SELECTION
    // =========================================================================

    /// <summary>
    /// Which currencies to show in the HUD, keyed by item ID.
    /// 
    /// NULL means "hasn't been configured yet" — in that case we show
    /// everything by default. Once the user opens settings and toggles
    /// anything, this gets populated and saved.
    /// 
    /// We use a HashSet (like a Python set) for O(1) lookups.
    /// </summary>
    public HashSet<uint>? EnabledCurrencies { get; set; } = null;

    /// <summary>
    /// Whether to show currencies that have a 0 balance.
    /// Off by default — hides clutter from currencies you don't use.
    /// </summary>
    public bool ShowZeroBalanceCurrencies { get; set; } = false;

    /// <summary>
    /// Whether to show weekly-capped currencies with progress bars.
    /// </summary>
    public bool ShowWeeklyCaps { get; set; } = true;

    /// <summary>
    /// Whether to show reputation/beast tribe standings.
    /// </summary>
    public bool ShowReputation { get; set; } = true;

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    /// <summary>
    /// Checks whether a given currency (by item ID) is enabled for display.
    /// If the user hasn't configured anything yet (EnabledCurrencies is null),
    /// ALL currencies are shown by default.
    /// </summary>
    public bool IsCurrencyEnabled(uint itemId)
    {
        // Null = no preferences set yet = show everything
        if (EnabledCurrencies == null)
            return true;

        return EnabledCurrencies.Contains(itemId);
    }

    /// <summary>
    /// Toggles a currency on or off. If this is the first toggle ever,
    /// we initialize the set with ALL known currency IDs first (so toggling
    /// one off doesn't hide everything else).
    /// </summary>
    public void SetCurrencyEnabled(uint itemId, bool enabled, uint[] allKnownIds)
    {
        // First time configuring — start with everything enabled
        if (EnabledCurrencies == null)
        {
            EnabledCurrencies = new HashSet<uint>(allKnownIds);
        }

        if (enabled)
            EnabledCurrencies.Add(itemId);
        else
            EnabledCurrencies.Remove(itemId);
    }

    // =========================================================================
    // INTERNAL
    // =========================================================================

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
