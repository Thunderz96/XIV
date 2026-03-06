// =============================================================================
// MainWindow.cs — The Main HUD Overlay (Updated for Real Data)
// =============================================================================
//
// Now that CurrencyTracker reads real game data, this window displays actual
// currency amounts with progress bars showing how close you are to caps.
// =============================================================================

using System;
using System.Numerics;

using Dalamud.Interface.Windowing;

using Dalamud.Bindings.ImGui;

using CurrencyBoard.Data;

namespace CurrencyBoard.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    public MainWindow(Plugin plugin)
        : base("Currency Board##CurrencyBoardMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 120),
            MaximumSize = new Vector2(600, 900),
        };

        Size = new Vector2(320, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        var currencies = Plugin.CurrencyTracker.GetCurrencies();

        if (!Plugin.CurrencyTracker.HasData)
        {
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), "Waiting for character data...");
            return;
        }

        // ----- HEADER -----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Currency Board");
        ImGui.SameLine(ImGui.GetWindowWidth() - 30);
        if (ImGui.SmallButton("⚙"))
            Plugin.ToggleConfigUI();

        ImGui.Separator();
        ImGui.Spacing();

        // ----- CURRENCY LIST -----
        // Begin a scrollable child region so the list doesn't overflow
        // the window. The "true" parameter adds a scrollbar.
        ImGui.BeginChild("CurrencyList", new Vector2(0, -25), false, ImGuiWindowFlags.None);

        var lastCategory = "";
        var compact = Plugin.Configuration.CompactMode;

        foreach (var currency in currencies)
        {
            // Skip currencies the user has disabled in settings
            if (!Plugin.Configuration.IsCurrencyEnabled(currency.ItemId))
                continue;

            // Skip currencies with 0 balance (unless user wants to see them,
            // or the currency has a cap — capped currencies are always relevant)
            if (currency.CurrentAmount == 0 && !Plugin.Configuration.ShowZeroBalanceCurrencies
                && currency.MaxAmount == 0)
                continue;

            // ----- CATEGORY HEADER -----
            if (currency.Category != lastCategory)
            {
                if (lastCategory != "")
                    ImGui.Spacing();

                // Collapsing header lets users fold categories they don't need.
                // TreeNodeEx gives us a clickable section header.
                // The "DefaultOpen" flag means it starts expanded.
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), currency.Category);
                ImGui.Separator();
                lastCategory = currency.Category;
            }

            // ----- DRAW THE CURRENCY ROW -----
            if (compact)
                DrawCompactEntry(currency);
            else
                DrawDetailedEntry(currency);
        }

        ImGui.EndChild();

        // ----- FOOTER -----
        ImGui.Separator();
        ImGui.TextDisabled("/currboard config for settings");
    }

    // =========================================================================
    // DETAILED VIEW — Name, amount, and progress bar
    // =========================================================================
    private void DrawDetailedEntry(CurrencyInfo currency)
    {
        // Currency name
        ImGui.Text(currency.Name);

        // Amount text, right-aligned on the same line
        var amountText = currency.MaxAmount > 0
            ? $"{FormatAmount(currency.CurrentAmount)} / {FormatAmount(currency.MaxAmount)}"
            : FormatAmount(currency.CurrentAmount);
        var textWidth = ImGui.CalcTextSize(amountText).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - textWidth - 20);

        // Color the amount based on how close to cap
        if (currency.MaxAmount > 0 && currency.CapProgress >= 0.9f)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), amountText); // Red = near cap!
        else
            ImGui.Text(amountText);

        // Progress bar toward total cap (if applicable)
        if (currency.MaxAmount > 0)
        {
            var barColor = GetCapColor(currency.CapProgress);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
            ImGui.ProgressBar(currency.CapProgress, new Vector2(-1, 14), "");
            ImGui.PopStyleColor();
        }

        // Weekly cap progress bar (if applicable)
        if (currency.HasWeeklyCap && Plugin.Configuration.ShowWeeklyCaps)
        {
            var weeklyColor = GetWeeklyColor(currency.WeeklyProgress);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, weeklyColor);
            var weeklyText = $"Weekly: {currency.WeeklyAmount}/{currency.WeeklyCap}";
            ImGui.ProgressBar(currency.WeeklyProgress, new Vector2(-1, 12), weeklyText);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
    }

    // =========================================================================
    // COMPACT VIEW — Single line per currency
    // =========================================================================
    private void DrawCompactEntry(CurrencyInfo currency)
    {
        // Compact: just name and amount on one line
        ImGui.Text(currency.Name);

        var amountText = FormatAmount(currency.CurrentAmount);
        if (currency.MaxAmount > 0)
            amountText += $" / {FormatAmount(currency.MaxAmount)}";

        var textWidth = ImGui.CalcTextSize(amountText).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - textWidth - 20);

        // Color red if near cap
        if (currency.MaxAmount > 0 && currency.CapProgress >= 0.9f)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), amountText);
        else
            ImGui.Text(amountText);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Color for the total cap progress bar.
    /// Green when low → Yellow when moderate → Red when near cap.
    /// Near-cap is RED because you want to SPEND before you waste earnings.
    /// </summary>
    private static Vector4 GetCapColor(float progress)
    {
        if (progress >= 0.9f)
            return new Vector4(0.9f, 0.2f, 0.2f, 1f);  // Red = danger, spend!
        if (progress >= 0.7f)
            return new Vector4(0.9f, 0.7f, 0.2f, 1f);  // Yellow = getting full
        return new Vector4(0.3f, 0.7f, 0.3f, 1f);       // Green = plenty of room
    }

    /// <summary>
    /// Color for the weekly cap progress bar.
    /// Here the logic is INVERTED from total cap:
    /// Red when LOW (you haven't done your weeklies!),
    /// Green when FULL (you've capped for the week!).
    /// </summary>
    private static Vector4 GetWeeklyColor(float progress)
    {
        if (progress >= 1.0f)
            return new Vector4(0.2f, 0.8f, 0.2f, 1f);  // Green = capped!
        if (progress >= 0.5f)
            return new Vector4(0.9f, 0.9f, 0.2f, 1f);  // Yellow = halfway
        return new Vector4(0.8f, 0.3f, 0.3f, 1f);       // Red = get to work!
    }

    private static string FormatAmount(long amount) => amount.ToString("N0");
}
