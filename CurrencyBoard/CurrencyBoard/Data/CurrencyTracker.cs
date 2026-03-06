// =============================================================================
// CurrencyTracker.cs — Reads REAL Currency Data from the Game
// =============================================================================
//
// This is the "data layer" of our plugin. It uses FFXIVClientStructs to
// read directly from the game's memory — specifically the InventoryManager,
// which is the game's internal object that tracks all your items and currencies.
//
// HOW CURRENCIES WORK IN FFXIV:
// Most "currencies" in FFXIV are actually just items in a special inventory.
// Gil, Tomestones, Seals, etc. all have item IDs just like any gear or potion.
// The InventoryManager has a method called GetInventoryItemCount() that tells
// you how many of a given item ID you have. We use that for everything.
//
// UNSAFE CODE:
// Because FFXIVClientStructs maps directly to the game's C++ memory layout,
// we need "unsafe" code blocks. This is similar to working with raw pointers
// in C++ — you're directly reading memory addresses. The "unsafe" keyword in
// C# tells the compiler "I know what I'm doing, let me use pointers."
// =============================================================================

using System;
using System.Collections.Generic;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace CurrencyBoard.Data;

// =============================================================================
// CURRENCY DEFINITIONS — What currencies exist and how to read them
// =============================================================================

/// <summary>
/// Defines a currency we want to track. This is the "template" — it describes
/// a currency type. CurrencyInfo (below) is the "live data" for that currency.
/// </summary>
public record CurrencyDefinition
{
    /// <summary>The game's internal item ID for this currency.</summary>
    public required uint ItemId { get; init; }

    /// <summary>Display name shown in our UI.</summary>
    public required string Name { get; init; }

    /// <summary>Category for grouping (e.g., "Common", "Tomestones").</summary>
    public required string Category { get; init; }

    /// <summary>Maximum you can hold (0 = no limit we care about).</summary>
    public long MaxAmount { get; init; } = 0;

    /// <summary>Weekly cap (0 = no weekly cap).</summary>
    public long WeeklyCap { get; init; } = 0;

    /// <summary>Game icon ID for display.</summary>
    public uint IconId { get; init; } = 0;

    /// <summary>
    /// If true, this currency is read specially (e.g., Gil isn't a normal
    /// inventory item — it uses its own field on InventoryManager).
    /// </summary>
    public bool IsSpecial { get; init; } = false;
}

/// <summary>
/// Live data snapshot for a single currency — updated each refresh.
/// </summary>
public record struct CurrencyInfo
{
    public uint ItemId { get; init; }
    public string Name { get; init; }
    public long CurrentAmount { get; init; }
    public long MaxAmount { get; init; }
    public long WeeklyCap { get; init; }
    public long WeeklyAmount { get; init; }
    public uint IconId { get; init; }
    public string Category { get; init; }

    public readonly bool HasWeeklyCap => WeeklyCap > 0;
    public readonly float WeeklyProgress => WeeklyCap > 0
        ? Math.Clamp((float)WeeklyAmount / WeeklyCap, 0f, 1f)
        : 0f;
    public readonly float CapProgress => MaxAmount > 0
        ? Math.Clamp((float)CurrentAmount / MaxAmount, 0f, 1f)
        : 0f;
}

// =============================================================================
// CURRENCY TRACKER
// =============================================================================

public class CurrencyTracker : IDisposable
{
    private readonly IClientState ClientState;
    private readonly IFramework Framework;
    private readonly IPluginLog Log;

    // Our cached currency data — the UI reads from this.
    private readonly List<CurrencyInfo> currencies = new();

    // Refresh timer
    private const float RefreshIntervalSeconds = 2.0f;
    private DateTime lastRefreshTime = DateTime.MinValue;

    // =========================================================================
    // CURRENCY REGISTRY — All the currencies we track
    // =========================================================================
    // This is the master list. To add a new currency, just add an entry here!
    // Item IDs come from the game's data files. You can look them up on sites
    // like Garland Tools (garlandtools.org) or Teamcraft.
    //
    // FOR PYTHON DEVELOPERS: This is like a list of dictionaries defining each currency.
    // FOR C++ DEVELOPERS: This is like a static const array of structs.
    // =========================================================================
    /// <summary>
    /// Public access to the master currency list — used by ConfigWindow
    /// to build the toggle checkboxes.
    /// </summary>
    public static IReadOnlyList<CurrencyDefinition> AllCurrencyDefinitions => TrackedCurrencies;

    /// <summary>
    /// Returns all item IDs from the master list. Used by Configuration
    /// to initialize the "enabled" set when the user first opens settings.
    /// </summary>
    public static uint[] GetAllCurrencyIds()
    {
        var ids = new uint[TrackedCurrencies.Length];
        for (var i = 0; i < TrackedCurrencies.Length; i++)
            ids[i] = TrackedCurrencies[i].ItemId;
        return ids;
    }

    private static readonly CurrencyDefinition[] TrackedCurrencies =
    [
        // ----- COMMON -----
        new CurrencyDefinition
        {
            ItemId = 1,
            Name = "Gil",
            Category = "Common",
            MaxAmount = 999_999_999,
            IconId = 65002,
            IsSpecial = true,  // Gil uses a special read method
        },

        // ----- TOMESTONES -----
        // These are the endgame currencies you earn from duties.
        // Item IDs change each expansion — these are for Dawntrail (7.x).
        new CurrencyDefinition
        {
            ItemId = 28,  // Allagan Tomestone of Poetics
            Name = "Tomestone of Poetics",
            Category = "Tomestones",
            MaxAmount = 2000,
            IconId = 65086,
        },
        new CurrencyDefinition
        {
            ItemId = 46,  // Allagan Tomestone of Aesthetics (Dawntrail current uncapped)
            Name = "Tomestone of Aesthetics",
            Category = "Tomestones",
            MaxAmount = 2000,
            IconId = 65086,
        },
        new CurrencyDefinition
        {
            ItemId = 47,  // Allagan Tomestone of Heliometry (Dawntrail weekly-capped)
            Name = "Tomestone of Heliometry",
            Category = "Tomestones",
            MaxAmount = 2000,
            WeeklyCap = 450,
            IconId = 65086,
        },

        // ----- GRAND COMPANY -----
        new CurrencyDefinition
        {
            ItemId = 20,  // Storm Seals
            Name = "Storm Seals",
            Category = "Grand Company",
            MaxAmount = 90000,
            IconId = 65024,
        },
        new CurrencyDefinition
        {
            ItemId = 21,  // Serpent Seals
            Name = "Serpent Seals",
            Category = "Grand Company",
            MaxAmount = 90000,
            IconId = 65025,
        },
        new CurrencyDefinition
        {
            ItemId = 22,  // Flame Seals
            Name = "Flame Seals",
            Category = "Grand Company",
            MaxAmount = 90000,
            IconId = 65026,
        },

        // ----- PVP -----
        new CurrencyDefinition
        {
            ItemId = 25,  // Wolf Marks
            Name = "Wolf Marks",
            Category = "PvP",
            MaxAmount = 20000,
            IconId = 65032,
        },
        new CurrencyDefinition
        {
            ItemId = 36656, // Trophy Crystals
            Name = "Trophy Crystals",
            Category = "PvP",
            MaxAmount = 20000,
            IconId = 65032,
        },

        // ----- CRAFTING / GATHERING -----
        new CurrencyDefinition
        {
            ItemId = 25199, // White Crafters' Scrip
            Name = "White Crafters' Scrip",
            Category = "Scrips",
            MaxAmount = 4000,
            IconId = 65073,
        },
        new CurrencyDefinition
        {
            ItemId = 33913, // Purple Crafters' Scrip
            Name = "Purple Crafters' Scrip",
            Category = "Scrips",
            MaxAmount = 4000,
            IconId = 65073,
        },
        new CurrencyDefinition
        {
            ItemId = 25200, // White Gatherers' Scrip
            Name = "White Gatherers' Scrip",
            Category = "Scrips",
            MaxAmount = 4000,
            IconId = 65074,
        },
        new CurrencyDefinition
        {
            ItemId = 33914, // Purple Gatherers' Scrip
            Name = "Purple Gatherers' Scrip",
            Category = "Scrips",
            MaxAmount = 4000,
            IconId = 65074,
        },

        // ----- OTHER -----
        new CurrencyDefinition
        {
            ItemId = 27,  // Allied Seals
            Name = "Allied Seals",
            Category = "Hunt",
            MaxAmount = 4000,
            IconId = 65034,
        },
        new CurrencyDefinition
        {
            ItemId = 10307, // Centurio Seals
            Name = "Centurio Seals",
            Category = "Hunt",
            MaxAmount = 4000,
            IconId = 65034,
        },
        new CurrencyDefinition
        {
            ItemId = 26533, // Sacks of Nuts
            Name = "Sacks of Nuts",
            Category = "Hunt",
            MaxAmount = 4000,
            IconId = 65034,
        },
    ];

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public CurrencyTracker(
        IDataManager dataManager,
        IClientState clientState,
        IFramework framework,
        IPluginLog log)
    {
        ClientState = clientState;
        Framework = framework;
        Log = log;

        Framework.Update += OnFrameworkUpdate;
        Log.Debug("CurrencyTracker initialized.");
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    public IReadOnlyList<CurrencyInfo> GetCurrencies() => currencies;
    public bool HasData => currencies.Count > 0;

    // =========================================================================
    // FRAMEWORK UPDATE
    // =========================================================================
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (ClientState.LocalPlayer == null)
        {
            currencies.Clear();
            return;
        }

        // Time-based refresh — only update every N seconds
        var now = DateTime.UtcNow;
        if ((now - lastRefreshTime).TotalSeconds < RefreshIntervalSeconds)
            return;

        lastRefreshTime = now;
        RefreshCurrencyData();
    }

    // =========================================================================
    // DATA REFRESH — Reads real values from game memory
    // =========================================================================
    /// <summary>
    /// Reads currency amounts directly from the game's InventoryManager.
    /// 
    /// This uses "unsafe" code because we're reading raw game memory via
    /// FFXIVClientStructs. The InventoryManager is a singleton — there's
    /// exactly one instance in the game process, and Instance() gives us
    /// a pointer to it.
    /// 
    /// FOR C++ DEVELOPERS: This is exactly like calling a static method
    ///   that returns a pointer: InventoryManager* mgr = InventoryManager::GetInstance();
    /// FOR PYTHON DEVELOPERS: Think of it as accessing a global game object,
    ///   but we need special "unsafe" syntax because we're reading memory directly.
    /// </summary>
    private unsafe void RefreshCurrencyData()
    {
        try
        {
            // Get the game's InventoryManager singleton.
            // This is the central object that knows about ALL your items/currencies.
            var inventoryManager = InventoryManager.Instance();

            // Safety check — if the pointer is null, the game isn't ready yet.
            if (inventoryManager == null)
                return;

            currencies.Clear();

            foreach (var def in TrackedCurrencies)
            {
                long amount;

                if (def.IsSpecial && def.ItemId == 1)
                {
                    // Gil is special — it's stored directly on the InventoryManager
                    // as a property, not as a regular inventory item.
                    amount = inventoryManager->GetGil();
                }
                else
                {
                    // For all other currencies, use GetInventoryItemCount().
                    // This searches all relevant inventories for items with this ID.
                    // Parameters: itemId, isHq (false for currencies),
                    //             checkEquipped (false), checkArmory (false)
                    amount = inventoryManager->GetInventoryItemCount(def.ItemId);
                }

                currencies.Add(new CurrencyInfo
                {
                    ItemId = def.ItemId,
                    Name = def.Name,
                    CurrentAmount = amount,
                    MaxAmount = def.MaxAmount,
                    WeeklyCap = def.WeeklyCap,
                    WeeklyAmount = 0,  // TODO: Weekly tracking requires additional work
                    IconId = def.IconId,
                    Category = def.Category,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error refreshing currency data: {ex.Message}");
        }
    }

    // =========================================================================
    // DISPOSE
    // =========================================================================
    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Log.Debug("CurrencyTracker disposed.");
    }
}
