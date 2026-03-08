// =============================================================================
// PullReadyWindow.cs — Main HUD overlay showing readiness status
// =============================================================================
//
// This is the ImGui window the player sees. It's split into three sections:
//   1. Banner — big green/yellow/red "READY" / "MARGINAL" / "NOT READY" header
//   2. YOU — self checks (food time, gear, items)
//   3. PARTY — one row per party member with their food status
//
// The window is opened via /pullready or automatically on zone-in.
// The player clicks "Re-check" to refresh the snapshot.
//
// PARTY CHAT PING BUTTON:
//   When enabled in settings, a "Ping Party" button appears.
//   It sends ONE /party message listing members with low food.
//   It only appears (and only works) if EnablePartyChatPing is true in config.
// =============================================================================

using System;
using System.Linq;
using System.Numerics;

using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace PullReady.Windows;

public class PullReadyWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // The last computed snapshot. Null until the player runs a check.
    private ReadinessSnapshot? snapshot;

    // Colors used for the banner and result rows
    private static readonly Vector4 ColorGreen  = new(0.2f, 1.0f, 0.3f, 1.0f);
    private static readonly Vector4 ColorYellow = new(1.0f, 0.85f, 0.1f, 1.0f);
    private static readonly Vector4 ColorRed    = new(1.0f, 0.25f, 0.25f, 1.0f);
    private static readonly Vector4 ColorGrey   = new(0.6f, 0.6f, 0.6f, 1.0f);

    public PullReadyWindow(Plugin plugin)
        : base("PullReady##main",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;

        // Reasonable default size — wide enough for party member names
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(600, 900),
        };
        Size = new Vector2(380, 400);
    }

    public void Dispose() { }

    // =========================================================================
    // Called by Dalamud every frame when the window is open.
    // =========================================================================
    public override void Draw()
    {
        // ---- TOP ACTION BAR ----
        if (ImGui.Button("Re-check"))
            RunChecks();

        ImGui.SameLine();

        // Show "last checked X seconds ago" if we have a snapshot
        if (snapshot != null)
        {
            int secsAgo = (int)(DateTime.Now - snapshot.CheckedAt).TotalSeconds;
            ImGui.TextDisabled($"Last checked {secsAgo}s ago");
        }
        else
        {
            ImGui.TextDisabled("Click Re-check to run checks.");
        }

        ImGui.SameLine();

        // Settings button — opens the config window
        if (ImGui.Button("Settings"))
            plugin.ConfigWindow.IsOpen = true;

        ImGui.Separator();

        // If no snapshot yet, just show a prompt
        if (snapshot == null)
        {
            ImGui.TextWrapped("Click \"Re-check\" above to check readiness before pulling.");
            return;
        }

        // ---- BANNER ----
        DrawBanner(snapshot.Overall);
        ImGui.Spacing();

        // ---- SELF CHECKS ----
        ImGui.TextColored(ColorGrey, "YOU");
        ImGui.Separator();

        if (snapshot.SelfChecks.Count == 0)
        {
            ImGui.TextDisabled("  (no checks enabled for self)");
        }
        else
        {
            foreach (var result in snapshot.SelfChecks)
                DrawCheckRow(result);
        }

        ImGui.Spacing();

        // ---- PARTY CHECKS ----
        if (plugin.Configuration.CheckPartyFood)
        {
            ImGui.TextColored(ColorGrey, "PARTY");
            ImGui.Separator();

            if (snapshot.PartyChecks.Count == 0)
            {
                ImGui.TextDisabled("  (not in a party)");
            }
            else
            {
                foreach (var result in snapshot.PartyChecks)
                    DrawCheckRow(result);
            }

            ImGui.Spacing();
        }

        // ---- PARTY CHAT PING BUTTON ----
        if (plugin.Configuration.EnablePartyChatPing)
        {
            DrawPingButton();
        }
    }

    // =========================================================================
    // DRAW HELPERS
    // =========================================================================

    /// <summary>
    /// Draws the large colored banner at the top of the window.
    /// Green = READY TO PULL, Yellow = MARGINAL, Red = NOT READY.
    /// </summary>
    private void DrawBanner(OverallStatus overall)
    {
        (string text, Vector4 color) = overall switch
        {
            OverallStatus.Ready    => ("✓  READY TO PULL",  ColorGreen),
            OverallStatus.Marginal => ("⚠  MARGINAL",       ColorYellow),
            OverallStatus.NotReady => ("✗  NOT READY",      ColorRed),
            _                      => ("?  UNKNOWN",         ColorGrey),
        };

        // Center the banner text
        float windowWidth  = ImGui.GetContentRegionAvail().X;
        float textWidth    = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX((windowWidth - textWidth) * 0.5f);

        ImGui.TextColored(color, text);
    }

    /// <summary>
    /// Draws one row in the checklist: [icon]  [name]  [detail]
    /// Color of the icon and name depends on Pass/Warn/Fail/Skip.
    /// </summary>
    private static void DrawCheckRow(CheckResult result)
    {
        if (result.Status == CheckStatus.Skip)
            return;

        (string icon, Vector4 color) = result.Status switch
        {
            CheckStatus.Pass => ("✓", ColorGreen),
            CheckStatus.Warn => ("⚠", ColorYellow),
            CheckStatus.Fail => ("✗", ColorRed),
            _                => ("-", ColorGrey),
        };

        // Icon (colored)
        ImGui.TextColored(color, icon);
        ImGui.SameLine();

        // Name (colored to match)
        ImGui.TextColored(color, result.Name);

        // Detail (grey, smaller — shown to the right)
        if (!string.IsNullOrEmpty(result.Detail))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(result.Detail);
        }
    }

    /// <summary>
    /// Draws the "Ping Party" button and sends a /party message if clicked.
    /// Only fires if there are actually party members with low food — avoids
    /// spamming the chat when everyone is fine.
    /// </summary>
    private void DrawPingButton()
    {
        ImGui.Separator();

        if (ImGui.Button("Ping Party About Food"))
            SendPartyFoodPing();

        ImGui.SameLine();
        ImGui.TextDisabled($"(warns if < {plugin.Configuration.PingThresholdMinutes} min left)");
    }

    // =========================================================================
    // LOGIC
    // =========================================================================

    /// <summary>
    /// Runs all checks and stores the result in <see cref="snapshot"/>.
    /// Called when the user clicks "Re-check".
    /// </summary>
    public void RunChecks()
    {
        snapshot = CheckEngine.Run(
            plugin.ObjectTable,
            plugin.PartyList,
            plugin.GameInventory,
            plugin.Configuration);
    }

    /// <summary>
    /// Sends a /party chat message listing members whose food is running low.
    /// Uses IChatGui to print the message as if the player typed it.
    ///
    /// Only sends if at least one member is below the PingThresholdMinutes.
    /// If everyone is fine, sends a short "All good" message instead.
    /// </summary>
    private void SendPartyFoodPing()
    {
        if (snapshot == null) return;

        // Find party members with Warn or Fail status
        var flagged = snapshot.PartyChecks
            .Where(r => r.Status == CheckStatus.Warn || r.Status == CheckStatus.Fail)
            .ToList();

        if (flagged.Count == 0)
        {
            // Everyone is fine — send a reassuring message
            plugin.ChatGui.Print(
                new Dalamud.Game.Text.SeStringHandling.SeString(
                    new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(
                        "[PullReady] Party food: All good! ✓")));
        }
        else
        {
            // List the flagged members
            var names = string.Join(", ", flagged.Select(r => $"{r.Name} ({r.Detail})"));
            plugin.ChatGui.Print(
                new Dalamud.Game.Text.SeStringHandling.SeString(
                    new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(
                        $"[PullReady] Low food: {names}")));
        }
    }
}
