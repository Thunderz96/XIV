// =============================================================================
// ConfigWindow.cs — Settings UI for PullReady
// =============================================================================
//
// Lets the player configure:
//   - Which checks to enable/disable
//   - Food warning thresholds (in minutes) — for self and party
//   - Gear repair warning/fail percentages
//   - Party chat ping settings + threshold
//   - Custom inventory item watchlist (add/remove ItemId + qty)
//   - Whether to auto-open on zone-in
// =============================================================================

using System;
using System.Numerics;

using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace PullReady.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Temporary buffer for the "Add item" input fields
    private string newItemName     = "";
    private string newItemIdStr    = "";
    private int    newItemQty      = 1;
    private bool   newItemRequired = true;

    public ConfigWindow(Plugin plugin)
        : base("PullReady — Settings##config",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size        = new Vector2(480, 540);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 420),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        // ---- GENERAL ----
        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool autoOpen = cfg.AutoOpenOnZoneIn;
            if (ImGui.Checkbox("Auto-open when entering a duty", ref autoOpen))
            {
                cfg.AutoOpenOnZoneIn = autoOpen;
                cfg.Save();
            }
            ImGui.TextDisabled("  Opens the checklist window whenever you zone into a duty.");
        }

        ImGui.Spacing();

        // ---- CHECKS TO RUN ----
        if (ImGui.CollapsingHeader("Checks", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool selfFood = cfg.CheckSelfFood;
            if (ImGui.Checkbox("Check MY Well Fed status", ref selfFood))
            { cfg.CheckSelfFood = selfFood; cfg.Save(); }

            bool partyFood = cfg.CheckPartyFood;
            if (ImGui.Checkbox("Check PARTY Well Fed status", ref partyFood))
            { cfg.CheckPartyFood = partyFood; cfg.Save(); }

            bool repairs = cfg.CheckRepairs;
            if (ImGui.Checkbox("Check gear condition (repairs)", ref repairs))
            { cfg.CheckRepairs = repairs; cfg.Save(); }

            bool inv = cfg.CheckInventory;
            if (ImGui.Checkbox("Check inventory items (pots, food, etc.)", ref inv))
            { cfg.CheckInventory = inv; cfg.Save(); }
        }

        ImGui.Spacing();

        // ---- FOOD THRESHOLDS ----
        if (ImGui.CollapsingHeader("Food Thresholds", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Self food warn
            int selfWarn = cfg.FoodWarnMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Warn (self) if food < X minutes", ref selfWarn, 1, 5))
            {
                cfg.FoodWarnMinutes = Math.Max(1, selfWarn);
                cfg.Save();
            }
            ImGui.TextDisabled("  Shows yellow if YOUR food will fall off within this many minutes.");

            ImGui.Spacing();

            // Party food warn
            int partyWarn = cfg.PartyFoodWarnMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Warn (party) if food < X minutes", ref partyWarn, 1, 5))
            {
                cfg.PartyFoodWarnMinutes = Math.Max(1, partyWarn);
                cfg.Save();
            }
            ImGui.TextDisabled("  Shows yellow for party members whose food is running low.");
        }

        ImGui.Spacing();

        // ---- GEAR REPAIR ----
        if (ImGui.CollapsingHeader("Gear Repair", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int warnPct = cfg.RepairWarnPercent;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Warn below % condition", ref warnPct, 1, 5))
            {
                cfg.RepairWarnPercent = Math.Clamp(warnPct, 1, 100);
                cfg.Save();
            }

            int failPct = cfg.RepairFailPercent;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Fail below % condition", ref failPct, 1, 5))
            {
                cfg.RepairFailPercent = Math.Clamp(failPct, 0, cfg.RepairWarnPercent);
                cfg.Save();
            }
            ImGui.TextDisabled("  Condition is shown as 0-100%. Below Fail = gear is basically broken.");
        }

        ImGui.Spacing();

        // ---- PARTY CHAT PING ----
        if (ImGui.CollapsingHeader("Party Chat Ping", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool pingEnabled = cfg.EnablePartyChatPing;
            if (ImGui.Checkbox("Enable 'Ping Party' button", ref pingEnabled))
            { cfg.EnablePartyChatPing = pingEnabled; cfg.Save(); }
            ImGui.TextDisabled("  Adds a button that sends ONE /party message about food status.");

            if (cfg.EnablePartyChatPing)
            {
                int pingThresh = cfg.PingThresholdMinutes;
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("Only ping if food < X minutes", ref pingThresh, 1, 5))
                {
                    cfg.PingThresholdMinutes = Math.Max(1, pingThresh);
                    cfg.Save();
                }
                ImGui.TextDisabled("  The button only sends a message if someone's food is below this.");
            }
        }

        ImGui.Spacing();

        // ---- WATCHED ITEMS ----
        if (ImGui.CollapsingHeader("Inventory Items to Check"))
        {
            ImGui.TextDisabled("Add items by ItemID. Find IDs on XIVAPI or Garland Tools.");
            ImGui.Separator();

            // Existing items table
            if (ImGui.BeginTable("##watchedItems", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("ItemID",   ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Min Qty",  ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthFixed, 65);
                ImGui.TableSetupColumn("Remove",   ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                int toRemove = -1; // index to remove after loop (can't modify list mid-iteration)

                for (int i = 0; i < cfg.WatchedItems.Count; i++)
                {
                    var item = cfg.WatchedItems[i];
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Name);
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.ItemId.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.MinQuantity.ToString());
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.Required ? "Yes" : "No");

                    ImGui.TableSetColumnIndex(4);
                    if (ImGui.SmallButton($"Remove##{i}"))
                        toRemove = i;
                }

                ImGui.EndTable();

                if (toRemove >= 0)
                {
                    cfg.WatchedItems.RemoveAt(toRemove);
                    cfg.Save();
                }
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Add new item:");

            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Name##newName", ref newItemName, 64);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.InputText("ItemID##newId", ref newItemIdStr, 12);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("Qty##newQty", ref newItemQty, 0, 0);

            ImGui.SameLine();
            ImGui.Checkbox("Req##newReq", ref newItemRequired);

            ImGui.SameLine();
            if (ImGui.Button("Add"))
            {
                if (uint.TryParse(newItemIdStr, out uint parsedId) && !string.IsNullOrWhiteSpace(newItemName))
                {
                    cfg.WatchedItems.Add(new WatchedItem
                    {
                        Name        = newItemName.Trim(),
                        ItemId      = parsedId,
                        MinQuantity = Math.Max(1, newItemQty),
                        Required    = newItemRequired,
                    });
                    cfg.Save();

                    // Reset fields
                    newItemName  = "";
                    newItemIdStr = "";
                    newItemQty   = 1;
                    newItemRequired = true;
                }
            }
        }
    }
}
