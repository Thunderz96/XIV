// =============================================================================
// ConfigWindow.cs — Settings with Currency Picker
// =============================================================================
//
// Users can now toggle individual currencies on/off. The currency list is
// organized by category with "Select All / Deselect All" buttons per category.
//
// IMGUI TAB BARS:
// We use ImGui.BeginTabBar / BeginTabItem to split settings into tabs:
//   Tab 1: Display settings (opacity, compact mode, etc.)
//   Tab 2: Currency picker (checkboxes for each currency)
// This keeps the config window clean as we add more settings over time.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Interface.Windowing;

using Dalamud.Bindings.ImGui;

using CurrencyBoard.Data;

namespace CurrencyBoard.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    public ConfigWindow(Plugin plugin)
        : base("Currency Board Settings##CurrencyBoardConfig",
               ImGuiWindowFlags.NoCollapse)
    {
        Plugin = plugin;

        Size = new Vector2(420, 450);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        var config = Plugin.Configuration;
        var changed = false;

        // ----- TAB BAR -----
        // TabBars let us split the config into logical sections.
        // Each tab is like a page — only one is visible at a time.
        if (ImGui.BeginTabBar("ConfigTabs"))
        {
            // ============================================================
            // TAB 1: DISPLAY SETTINGS
            // ============================================================
            if (ImGui.BeginTabItem("Display"))
            {
                ImGui.Spacing();

                // -- Window Opacity --
                var opacity = config.WindowOpacity;
                if (ImGui.SliderFloat("Window Opacity", ref opacity, 0.3f, 1.0f, "%.1f"))
                {
                    config.WindowOpacity = opacity;
                    changed = true;
                }

                // -- Compact Mode --
                var compact = config.CompactMode;
                if (ImGui.Checkbox("Compact Mode", ref compact))
                {
                    config.CompactMode = compact;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Single-line display without progress bars.");

                // -- Lock Window --
                var locked = config.LockWindowPosition;
                if (ImGui.Checkbox("Lock Window Position", ref locked))
                {
                    config.LockWindowPosition = locked;
                    changed = true;
                }

                // -- Show Weekly Caps --
                var weeklyCaps = config.ShowWeeklyCaps;
                if (ImGui.Checkbox("Show Weekly Cap Bars", ref weeklyCaps))
                {
                    config.ShowWeeklyCaps = weeklyCaps;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show progress bars for weekly-capped currencies like Tomestones of Heliometry.");

                // -- Show Zero Balance --
                var showZero = config.ShowZeroBalanceCurrencies;
                if (ImGui.Checkbox("Show Zero-Balance Currencies", ref showZero))
                {
                    config.ShowZeroBalanceCurrencies = showZero;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show currencies even when you have 0. Otherwise only currencies with a balance (or a cap) are shown.");

                ImGui.EndTabItem();
            }

            // ============================================================
            // TAB 2: CURRENCY PICKER
            // ============================================================
            if (ImGui.BeginTabItem("Currencies"))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f),
                    "Choose which currencies to display:");
                ImGui.Spacing();

                // Quick actions: Enable All / Disable All
                if (ImGui.Button("Enable All"))
                {
                    config.EnabledCurrencies = null; // null = everything shown
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("Disable All"))
                {
                    config.EnabledCurrencies = new HashSet<uint>();
                    changed = true;
                }
                ImGui.Separator();

                // ----- SCROLLABLE LIST OF CURRENCIES -----
                ImGui.BeginChild("CurrencyPickerList", new Vector2(0, -5), false);

                // Get the master list of all currencies, grouped by category.
                // LINQ's GroupBy works like Python's itertools.groupby — it
                // collects items that share the same key into groups.
                var allCurrencyIds = CurrencyTracker.GetAllCurrencyIds();
                var grouped = CurrencyTracker.AllCurrencyDefinitions
                    .GroupBy(c => c.Category);

                foreach (var group in grouped)
                {
                    // Category header as a collapsing tree node.
                    // TreeNodeEx with DefaultOpen means it starts expanded.
                    var flags = ImGuiTreeNodeFlags.DefaultOpen;

                    if (ImGui.TreeNodeEx(group.Key, flags))
                    {
                        // Per-category quick toggles
                        var categoryIds = group.Select(c => c.ItemId).ToArray();
                        var allEnabled = categoryIds.All(id => config.IsCurrencyEnabled(id));
                        var noneEnabled = categoryIds.All(id => !config.IsCurrencyEnabled(id));

                        // "All" checkbox for this category — uses a "mixed" state
                        // if some are enabled and some aren't.
                        // ImGui doesn't have a native tri-state checkbox, so we
                        // use a small button approach instead.
                        ImGui.SameLine(ImGui.GetWindowWidth() - 130);
                        ImGui.PushID(group.Key);
                        if (ImGui.SmallButton(allEnabled ? "Deselect All" : "Select All"))
                        {
                            var enable = !allEnabled;
                            foreach (var id in categoryIds)
                                config.SetCurrencyEnabled(id, enable, allCurrencyIds);
                            changed = true;
                        }
                        ImGui.PopID();

                        // Individual currency checkboxes
                        foreach (var currency in group)
                        {
                            var isEnabled = config.IsCurrencyEnabled(currency.ItemId);
                            if (ImGui.Checkbox(currency.Name, ref isEnabled))
                            {
                                config.SetCurrencyEnabled(
                                    currency.ItemId, isEnabled, allCurrencyIds);
                                changed = true;
                            }

                            // Show max cap as a tooltip hint
                            if (ImGui.IsItemHovered() && currency.MaxAmount > 0)
                            {
                                var tip = $"Cap: {currency.MaxAmount:N0}";
                                if (currency.WeeklyCap > 0)
                                    tip += $" (Weekly: {currency.WeeklyCap:N0})";
                                ImGui.SetTooltip(tip);
                            }
                        }

                        ImGui.TreePop();
                    }
                }

                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // ----- FOOTER -----
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("CurrencyBoard v0.1.0");

        // Save if anything changed
        if (changed)
            config.Save();
    }
}
