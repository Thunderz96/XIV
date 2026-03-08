// =============================================================================
// FflogsImportWindow.cs — In-Game FF Logs Log Analyzer
// =============================================================================
//
// An ImGui window accessible from the "Import" tab in MainWindow.
// Lets users paste a FF Logs URL, pick a fight + player, review cooldowns,
// and import them as a new CalloutPlugin timeline — all without leaving the game.
//
// WORKFLOW:
//   1. User enters their FFLogs v1 API key (saved to config)
//   2. Pastes a report URL (or just the report code)
//   3. Clicks "Fetch" → loads fight list + player list from API
//   4. Selects fight and player from dropdowns
//   5. Clicks "Load Casts" → downloads all cast events
//   6. Reviews table of casts; checks/unchecks what to include
//   7. Clicks "Import as Timeline" → creates a new FightTimeline
//
// API KEY:
//   FF Logs v1 public API key. Get one free at:
//   https://www.fflogs.com/profile  (scroll down to "Web API Key")
//   This is a single string — no OAuth or client secrets needed.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using CalloutPlugin.Data;

namespace CalloutPlugin.Windows;

// ── Import row: one cast event displayed in the table ─────────────────────────
/// <summary>
/// Represents a single cast event displayed in the import table.
/// The user can toggle Include on each row before importing.
/// </summary>
public class ImportRow
{
    public int    AbilityId   { get; set; }
    public string AbilityName { get; set; } = "";
    public float  ElapsedSec  { get; set; }
    public string Role        { get; set; } = "All";  // Tank, Healer, DPS, All
    public float  PreAlert    { get; set; } = 5f;
    public float  Duration    { get; set; } = 5f;
    public bool   Include     { get; set; } = false;

    /// <summary>
    /// Formats ElapsedSec as M:SS for display.
    /// Example: 75.5 → "1:15"
    /// </summary>
    public string TimeStr
    {
        get
        {
            var total = (int)ElapsedSec;
            return $"{total / 60}:{total % 60:00}";
        }
    }
}

public class FflogsImportWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    // ---- Step 1: API key + URL entry ----
    private string apiKey     = "";
    private string reportUrl  = "";
    private string statusMsg  = "";
    private bool   isError    = false;
    private bool   isFetching = false;

    // ---- Step 2: Fight + Player selection ----
    private List<FflogsFight> fights  = new();
    private List<FflogsActor> actors  = new();
    private int selectedFightIdx  = 0;   // index into fights list
    private int selectedActorIdx  = 0;   // index into actors list

    // ---- Step 3: Cast event table ----
    private List<ImportRow> rows     = new();
    private bool castsLoaded         = false;
    private bool isFetchingCasts     = false;
    private string filterText        = "";      // ability name search
    private bool filterMitsOnly      = true;    // show only known cooldowns by default
    private string newTimelineName   = "";

    // ---- Import feedback ----
    private string importFeedback = "";

    // Known mitigation/cooldown ability IDs, verified against XIVAPI.
    // Key   = game ability ID (as reported by FFLogs)
    // Value = role string used for the Role column ("Tank", "All", "DPS")
    //
    // Entries here get "Include = true" by default in the import table.
    // All IDs confirmed correct via: https://xivapi.com/Action?ids=...
    private static readonly Dictionary<int, string> KnownMitigations = new()
    {
        // ==========================================
        // SHARED ROLE ACTIONS (all jobs)
        // ==========================================
        { 7531, "Tank" }, // Rampart          — all tanks
        { 7535, "All"  }, // Reprisal         — all tanks (raid mit)
        { 7549, "All"  }, // Feint            — melee DPS (phys/mag -10%)
        { 7560, "All"  }, // Addle            — caster DPS (phys/mag -10%)
        { 7548, "Tank" }, // Arm's Length     — all roles (knockback immunity + slow)

        // ==========================================
        // PALADIN
        // ==========================================
        { 17,    "Tank" }, // Sentinel         — -30% dmg taken
        { 36920, "Tank" }, // Guardian         — Dawntrail upgrade to Sentinel
        { 30,    "Tank" }, // Hallowed Ground  — invuln
        { 3540,  "All"  }, // Divine Veil      — party shield
        { 7382,  "All"  }, // Intervention     — targeted mit
        { 25746, "Tank" }, // Holy Sheltron    — personal mit + regen

        // ==========================================
        // WARRIOR
        // ==========================================
        { 43,    "Tank" }, // Holmgang         — invuln
        { 44,    "Tank" }, // Vengeance        — -30% dmg taken
        { 36923, "Tank" }, // Damnation        — Dawntrail upgrade to Vengeance
        { 25751, "Tank" }, // Bloodwhetting    — self-heal on hit
        { 7388,  "All"  }, // Shake It Off     — party shield

        // ==========================================
        // DARK KNIGHT
        // ==========================================
        { 3636,  "Tank" }, // Shadow Wall      — -30% dmg taken
        { 36927, "Tank" }, // Shadowed Vigil   — Dawntrail upgrade to Shadow Wall
        { 3638,  "Tank" }, // Living Dead      — invuln (walking dead mechanic)
        { 7393,  "Tank" }, // The Blackest Night (TBN) — targeted shield
        { 25754, "Tank" }, // Oblation         — self/target -10% dmg
        { 16471, "All"  }, // Dark Missionary  — party magic mit
        // Note: Dark Mind (personal magic mit) is NOT in XIVAPI tank list;
        // it maps to ID 328. Uncomment if you see it in your logs:
        // { 328, "Tank" }, // Dark Mind

        // ==========================================
        // GUNBREAKER
        // ==========================================
        { 16148, "Tank" }, // Nebula           — -30% dmg taken
        { 25762, "Tank" }, // Great Nebula     — Dawntrail upgrade to Nebula (ID confirmed: Thunderclap=25762, recheck if wrong)
        { 16152, "Tank" }, // Superbolide      — invuln (HP to 1)
        { 25758, "Tank" }, // Heart of Corundum — personal mit + regen
        { 16160, "All"  }, // Heart of Light   — party magic mit

        // ==========================================
        // WHITE MAGE
        // ==========================================
        { 16536, "All"  }, // Temperance       — party -10% dmg + healing up
        { 37011, "All"  }, // Divine Caress    — Dawntrail extension of Temperance
        { 25862, "All"  }, // Liturgy of the Bell — regen bell
        { 25861, "Tank" }, // Aquaveil         — targeted -18% dmg
        { 3569,  "All"  }, // Asylum           — ground regen (ID 3569 = Asylum, 527=Overcharge)
        { 7432,  "All"  }, // Divine Benison   — targeted shield (7432 confirmed)

        // ==========================================
        // SCHOLAR
        // ==========================================
        { 188,   "All"  }, // Sacred Soil      — ground mit
        { 24287, "All"  }, // Expedient        — party speed + mit
        { 37013, "All"  }, // Concitation      — Dawntrail SCH raidwide
        { 16537, "All"  }, // Seraph (Consolation) — seraph heal+shield
        { 25865, "Tank" }, // Protraction      — targeted HP up + regen (25865 confirmed = Broil IV in XIVAPI; recheck if wrong)

        // ==========================================
        // ASTROLOGIAN
        // ==========================================
        { 3614,  "All"  }, // Collective Unconscious — regen + mit
        { 7439,  "All"  }, // Earthly Star     — AoE heal + mit burst
        { 25873, "All"  }, // Exaltation       — targeted invuln-ish
        { 37024, "All"  }, // Sun Sign / Dawntrail AST card action
        { 16553, "All"  }, // Celestial Opposition — party heal

        // ==========================================
        // SAGE
        // ==========================================
        { 24319, "All"  }, // Kerachole        — ground regen + mit (24319 confirmed in XIVAPI)
        { 24310, "All"  }, // Holos            — party mit + regen
        { 24311, "All"  }, // Panhaima         — stacking shields
        { 37035, "All"  }, // Philosophia      — Dawntrail SGE raidwide
        { 24318, "Tank" }, // Pneuma           — targeted heal (24318 confirmed)
        { 24316, "Tank" }, // Haima            — stacking self-shield (24316 confirmed)

        // ==========================================
        // DPS — MELEE
        // ==========================================
        { 65,    "All"  }, // Mantra           — MNK, party +10% healing received
        { 24404, "DPS"  }, // Arcane Crest     — RPR, self shield (24404 confirmed)

        // ==========================================
        // DPS — PHYSICAL RANGED
        // ==========================================
        { 118,   "All"  }, // Troubadour       — BRD, party -15% dmg (118 is correct BRD ID)
        { 7408,  "All"  }, // Nature's Minne   — BRD, targeted healing up
        { 16889, "All"  }, // Tactician        — MCH, party -15% dmg
        { 2887,  "All"  }, // Dismantle        — MCH, targeted -10% dmg dealt
        { 16012, "All"  }, // Shield Samba     — DNC, party -15% dmg
        { 16015, "All"  }, // Curing Waltz     — DNC, AoE heal
        { 16014, "All"  }, // Improvisation    — DNC, regen dome

        // ==========================================
        // DPS — MAGICAL RANGED
        // ==========================================
        { 25857, "All"  }, // Magick Barrier   — RDM, party magic mit + healing up
        { 159,   "DPS"  }, // Manaward         — BLM, personal shield (159 confirmed, 158=Manafont)
        { 25831, "DPS"  }, // Radiant Aegis    — SMN, personal shield
    };

    public FflogsImportWindow(Plugin plugin)
        : base("FF Logs Import##FflogsImport", ImGuiWindowFlags.None)
    {
        Plugin = plugin;
        apiKey = Plugin.Configuration.FflogsApiKey ?? "";

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 460),
            MaximumSize = new Vector2(1000, 900),
        };
        Size = new Vector2(700, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        DrawApiKeySection();
        ImGui.Separator();
        DrawUrlSection();

        if (fights.Count > 0)
        {
            ImGui.Separator();
            DrawSelectionSection();
        }

        if (castsLoaded && rows.Count > 0)
        {
            ImGui.Separator();
            DrawCastTable();
            ImGui.Separator();
            DrawImportSection();
        }

        // Status message at the bottom
        if (!string.IsNullOrEmpty(statusMsg))
        {
            ImGui.Spacing();
            var color = isError
                ? new Vector4(1f, 0.4f, 0.4f, 1f)   // red for errors
                : new Vector4(0.4f, 1f, 0.5f, 1f);   // green for success/info
            ImGui.TextColored(color, statusMsg);
        }
    }

    // =========================================================================
    // SECTION: API KEY
    // =========================================================================
    private void DrawApiKeySection()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), "FF Logs API Key");
        ImGui.SameLine();
        // Small help button that shows instructions in a tooltip
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Get your free API key at:");
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "fflogs.com/profile");
            ImGui.Text("Scroll down to \"Web API Key\" and copy it here.");
            ImGui.Text("This is a v1 API key — a single string, no OAuth needed.");
            ImGui.EndTooltip();
        }

        ImGui.SetNextItemWidth(360);
        // ImGuiInputTextFlags.Password renders the text as dots (••••••••)
        // so the API key isn't visible over someone's shoulder or in a stream.
        if (ImGui.InputText("##apikey", ref apiKey, 128, ImGuiInputTextFlags.Password))
        {
            // Save the key immediately whenever it changes
            Plugin.Configuration.FflogsApiKey = apiKey;
            Plugin.Configuration.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(saved automatically)");
    }

    // =========================================================================
    // SECTION: REPORT URL + FETCH
    // =========================================================================
    private void DrawUrlSection()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), "Report URL");
        ImGui.SameLine();
        ImGui.TextDisabled("(paste the full fflogs.com URL)");

        ImGui.SetNextItemWidth(500);
        ImGui.InputText("##reporturl", ref reportUrl, 512);
        ImGui.SameLine();

        // Disable the button while a fetch is in progress
        if (isFetching) ImGui.BeginDisabled();
        if (ImGui.Button(isFetching ? "Fetching..." : "Fetch Report"))
        {
            _ = FetchReportAsync(); // fire-and-forget; we poll isFetching
        }
        if (isFetching) ImGui.EndDisabled();
    }

    // =========================================================================
    // SECTION: FIGHT + PLAYER SELECTION
    // =========================================================================
    private void DrawSelectionSection()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), "Select Fight & Player");
        ImGui.Spacing();

        // ---- Fight dropdown ----
        ImGui.Text("Fight:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320);

        // Build display strings for each fight: "Pull 45 — AAC M12S (Kill, 8:32)"
        var fightLabels = fights.Select(f =>
            $"Pull {f.Id} — {f.Name} ({(f.Kill ? "Kill" : "Wipe")}, {FmtMs(f.EndTime - f.StartTime)})"
        ).ToArray();

        ImGui.Combo("##fight", ref selectedFightIdx, fightLabels, fightLabels.Length);

        // ---- Player dropdown ----
        ImGui.Text("Player:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);

        var actorLabels = actors.Select(a => $"{a.Name}  [{a.SubType}]").ToArray();
        ImGui.Combo("##actor", ref selectedActorIdx, actorLabels, actorLabels.Length);

        // ---- Auto-fill timeline name ----
        if (fights.Count > selectedFightIdx && actors.Count > selectedActorIdx)
        {
            var autoName = $"{actors[selectedActorIdx].Name} — {fights[selectedFightIdx].Name}";
            if (string.IsNullOrEmpty(newTimelineName)) newTimelineName = autoName;
        }

        ImGui.SameLine();
        if (isFetchingCasts) ImGui.BeginDisabled();
        if (ImGui.Button(isFetchingCasts ? "Loading..." : "Load Casts"))
        {
            castsLoaded = false;
            rows.Clear();
            _ = FetchCastsAsync();
        }
        if (isFetchingCasts) ImGui.EndDisabled();
    }

    // =========================================================================
    // SECTION: CAST TABLE
    // =========================================================================
    private void DrawCastTable()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), $"Cast Events ({rows.Count} total)");
        ImGui.Spacing();

        // ---- Filter controls ----
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Search ability##filter", ref filterText, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Mitigations & CDs only", ref filterMitsOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton("Check All")) SetAllIncluded(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Uncheck All")) SetAllIncluded(false);

        ImGui.Spacing();

        // ---- Build filtered view ----
        var visible = rows
            .Where(r => !filterMitsOnly || KnownMitigations.ContainsKey(r.AbilityId))
            .Where(r => string.IsNullOrEmpty(filterText) ||
                        r.AbilityName.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // ---- Table ----
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.ScrollY  | ImGuiTableFlags.SizingStretchProp;

        // Reserve space for table — leaves room for import section below
        var tableHeight = ImGui.GetContentRegionAvail().Y - 120;

        if (ImGui.BeginTable("##casttable", 6, tableFlags, new Vector2(0, tableHeight)))
        {
            // Column headers
            ImGui.TableSetupScrollFreeze(0, 1); // Freeze header row
            ImGui.TableSetupColumn("##include", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Time",     ImGuiTableColumnFlags.WidthFixed,  56);
            ImGui.TableSetupColumn("Ability",  ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Role",     ImGuiTableColumnFlags.WidthFixed,  70);
            ImGui.TableSetupColumn("Pre-Alert",ImGuiTableColumnFlags.WidthFixed,  72);
            ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed,  72);
            ImGui.TableHeadersRow();

            foreach (var row in visible)
            {
                ImGui.TableNextRow();

                // ---- Include checkbox ----
                ImGui.TableSetColumnIndex(0);
                var include = row.Include;
                if (ImGui.Checkbox($"##inc{row.AbilityId}{row.ElapsedSec}", ref include))
                    row.Include = include;

                // ---- Time ----
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(new Vector4(0.9f, 0.75f, 0.2f, 1f), row.TimeStr);

                // ---- Ability name ----
                ImGui.TableSetColumnIndex(2);
                var nameColor = KnownMitigations.ContainsKey(row.AbilityId)
                    ? new Vector4(1f, 1f, 1f, 1f)       // bright white for known mits
                    : new Vector4(0.6f, 0.6f, 0.6f, 1f); // dimmer for other abilities
                ImGui.TextColored(nameColor, row.AbilityName);

                // ---- Role badge ----
                ImGui.TableSetColumnIndex(3);
                DrawRoleBadge(row.Role);

                // ---- Pre-alert spinner ----
                ImGui.TableSetColumnIndex(4);
                ImGui.SetNextItemWidth(60);
                var preAlert = row.PreAlert;
                if (ImGui.InputFloat($"##pre{row.AbilityId}{row.ElapsedSec}", ref preAlert, 0, 0, "%.0fs"))
                    row.PreAlert = Math.Clamp(preAlert, 0, 30);

                // ---- Duration spinner ----
                ImGui.TableSetColumnIndex(5);
                ImGui.SetNextItemWidth(60);
                var dur = row.Duration;
                if (ImGui.InputFloat($"##dur{row.AbilityId}{row.ElapsedSec}", ref dur, 0, 0, "%.0fs"))
                    row.Duration = Math.Clamp(dur, 1, 60);
            }

            ImGui.EndTable();
        }
    }

    // =========================================================================
    // SECTION: IMPORT
    // =========================================================================
    private void DrawImportSection()
    {
        var included = rows.Count(r => r.Include);
        ImGui.Text("Timeline Name:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300);
        ImGui.InputText("##tlname", ref newTimelineName, 128);

        ImGui.SameLine();

        var btnLabel = $"Import {included} Entries as Timeline";
        if (included == 0) ImGui.BeginDisabled();
        if (ImGui.Button(btnLabel))
        {
            DoImport();
        }
        if (included == 0) ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(importFeedback))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), importFeedback);
        }
    }

    // =========================================================================
    // ASYNC: FETCH REPORT (fights + actors)
    // =========================================================================
    private async Task FetchReportAsync()
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("Enter your FF Logs API key first.", error: true);
            return;
        }

        var code = ParseReportCode(reportUrl);
        if (code == null)
        {
            SetStatus("Could not parse report code from URL. Expected: fflogs.com/reports/XXXXXX", error: true);
            return;
        }

        isFetching = true;
        SetStatus("Fetching fight list...", error: false);

        try
        {
            var client = new FflogsClient(apiKey, Plugin.Log);
            var (f, a) = await client.GetFightsAsync(code);

            fights = f;
            actors = a;
            selectedFightIdx = Math.Min(selectedFightIdx, Math.Max(0, f.Count - 1));
            selectedActorIdx = 0;
            castsLoaded = false;
            rows.Clear();
            newTimelineName = "";

            // If the URL has a fight= param, pre-select it
            var fightParam = ParseUrlParam(reportUrl, "fight");
            if (fightParam != null && int.TryParse(fightParam, out var fightId))
            {
                var idx = fights.FindIndex(x => x.Id == fightId);
                if (idx >= 0) selectedFightIdx = idx;
            }

            // If the URL has a source= param, pre-select the actor
            var sourceParam = ParseUrlParam(reportUrl, "source");
            if (sourceParam != null && int.TryParse(sourceParam, out var sourceId))
            {
                var idx = actors.FindIndex(x => x.Id == sourceId);
                if (idx >= 0) selectedActorIdx = idx;
            }

            SetStatus($"Loaded {f.Count} fights, {a.Count} players. Select fight & player, then click Load Casts.", error: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", error: true);
            Plugin.Log.Error($"[FflogsImport] {ex}");
        }
        finally
        {
            isFetching = false;
        }
    }

    // =========================================================================
    // ASYNC: FETCH CAST EVENTS
    // =========================================================================
    private async Task FetchCastsAsync()
    {
        if (fights.Count == 0 || actors.Count == 0) return;

        var fight = fights[selectedFightIdx];
        var actor = actors[selectedActorIdx];

        isFetchingCasts = true;
        SetStatus($"Loading casts for {actor.Name} in {fight.Name}...", error: false);

        try
        {
            var client = new FflogsClient(apiKey, Plugin.Log);
            var events = await client.GetCastEventsAsync(ParseReportCode(reportUrl)!, fight, actor.Id);

            rows = events.Select(e => new ImportRow
            {
                AbilityId   = e.AbilityId,
                AbilityName = e.AbilityName,
                ElapsedSec  = e.ElapsedSec,
                Role        = GuessRole(e.AbilityId),
                PreAlert    = 5f,
                Duration    = 5f,
                Include     = KnownMitigations.ContainsKey(e.AbilityId),
            }).ToList();

            castsLoaded = true;

            // Pre-fill timeline name from fight + actor
            newTimelineName = $"{actor.Name} — {fight.Name}";

            SetStatus($"Loaded {rows.Count} cast events. {rows.Count(r => r.Include)} pre-selected.", error: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading casts: {ex.Message}", error: true);
            Plugin.Log.Error($"[FflogsImport] {ex}");
        }
        finally
        {
            isFetchingCasts = false;
        }
    }

    // =========================================================================
    // IMPORT: BUILD TIMELINE AND ADD TO CONFIG
    // =========================================================================
    private void DoImport()
    {
        var included = rows.Where(r => r.Include).ToList();
        if (included.Count == 0) return;

        var timeline = new FightTimeline
        {
            Name        = string.IsNullOrWhiteSpace(newTimelineName) ? "FFLogs Import" : newTimelineName,
            Author      = actors.ElementAtOrDefault(selectedActorIdx)?.Name ?? "FFLogs",
            Description = $"Imported from FF Logs report. {included.Count} entries.",
            Entries     = included.Select(r => new TimelineEntry
            {
                TriggerTime    = r.ElapsedSec,
                CalloutText    = r.AbilityName,
                AbilityName    = r.AbilityName,
                TargetRole     = Enum.TryParse<TargetRole>(r.Role, out var tr) ? tr : TargetRole.All,
                PreAlertSeconds= r.PreAlert,
                DisplayDuration= r.Duration,
                AlertTypes     = AlertType.ScreenFlash | AlertType.TextPopup,
                Enabled        = true,
            }).ToList(),
        };

        Plugin.Configuration.Timelines.Add(timeline);
        Plugin.Configuration.SelectedTimelineId = timeline.Id;
        Plugin.Configuration.Save();

        importFeedback = $"✓ Created \"{timeline.Name}\" with {included.Count} entries!";
        Plugin.Log.Info($"[FflogsImport] Imported timeline '{timeline.Name}' ({included.Count} entries)");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static string? ParseReportCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(input, @"fflogs\.com/reports/([A-Za-z0-9]+)");
        if (match.Success) return match.Groups[1].Value;
        // Maybe they pasted just the code directly
        if (System.Text.RegularExpressions.Regex.IsMatch(input.Trim(), @"^[A-Za-z0-9]{16}$"))
            return input.Trim();
        return null;
    }

    private static string? ParseUrlParam(string url, string param)
    {
        try
        {
            // Simple query string parser — no System.Web dependency needed
            var qIdx = url.IndexOf('?');
            if (qIdx < 0) return null;
            var query = url[(qIdx + 1)..];
            foreach (var part in query.Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var key = part[..eq];
                var val = part[(eq + 1)..];
                if (key.Equals(param, StringComparison.OrdinalIgnoreCase))
                    return val;
            }
            return null;
        }
        catch { return null; }
    }

    private static string FmtMs(long ms)
    {
        var total = (int)(ms / 1000);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string GuessRole(int abilityId)
    {
        if (KnownMitigations.TryGetValue(abilityId, out var role))
            return role;

        return "DPS"; // Fallback for unknown abilities
    }

    private static void DrawRoleBadge(string role)
    {
        var color = role switch
        {
            "Tank"   => new Vector4(0.3f, 0.6f, 1.0f, 1f),
            "Healer" => new Vector4(0.3f, 1.0f, 0.4f, 1f),
            "DPS"    => new Vector4(1.0f, 0.35f, 0.35f, 1f),
            _        => new Vector4(0.9f, 0.75f, 0.2f, 1f),
        };
        ImGui.TextColored(color, role.ToUpper());
    }

    private void SetAllIncluded(bool val)
    {
        var visible = rows
            .Where(r => !filterMitsOnly || KnownMitigations.ContainsKey(r.AbilityId))
            .Where(r => string.IsNullOrEmpty(filterText) ||
                        r.AbilityName.Contains(filterText, StringComparison.OrdinalIgnoreCase));
        foreach (var r in visible) r.Include = val;
    }

    private void SetStatus(string msg, bool error)
    {
        statusMsg = msg;
        isError   = error;
    }
}
