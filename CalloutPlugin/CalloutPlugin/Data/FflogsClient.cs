// =============================================================================
// FflogsClient.cs — FF Logs v1 API Client
// =============================================================================
//
// Uses the FF Logs public v1 REST API to fetch cast events from a report.
// The v1 API only requires a single API key (not OAuth) which users can get
// for free at: https://www.fflogs.com/profile  (scroll to "Web API Key")
//
// API OVERVIEW:
//   Base URL:  https://www.fflogs.com/v1/
//   Auth:      ?api_key=YOUR_KEY  (appended to every request)
//
// ENDPOINTS WE USE:
//   GET /report/fights/{code}
//     → Returns all fight pulls in a report + actor list (player names/IDs)
//
//   GET /report/events/casts/{code}
//     → Returns all cast events for a fight, optionally filtered by source
//     → Paginated via the "nextPageTimestamp" field in the response
//
// PARSING:
//   Events use UNIX timestamps in milliseconds. We subtract the fight's
//   startTime to get elapsed seconds from pull start.
//
// THREADING:
//   All HTTP calls are async (Task-based). The UI kicks them off and polls
//   status — never blocking the game thread.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Dalamud.Plugin.Services;

namespace CalloutPlugin.Data;

/// <summary>
/// Represents one fight pull within a report.
/// </summary>
public class FflogsFight
{
    public int    Id        { get; set; }
    public string Name      { get; set; } = "";
    public long   StartTime { get; set; }  // ms from report start
    public long   EndTime   { get; set; }  // ms from report start
    public bool   Kill      { get; set; }
}

/// <summary>
/// Represents a player/actor who appears in the report.
/// </summary>
public class FflogsActor
{
    public int    Id      { get; set; }
    public string Name    { get; set; } = "";
    public string Type    { get; set; } = "";  // "Player", "NPC", "Pet"
    public string SubType { get; set; } = "";  // Job abbreviation, e.g. "DarkKnight"
}

/// <summary>
/// One cast event from the log — a player used an ability at a specific time.
/// </summary>
public class FflogsCastEvent
{
    public long   Timestamp   { get; set; }   // ms from report start
    public int    AbilityId   { get; set; }   // game ability ID
    public string AbilityName { get; set; } = "";
    public float  ElapsedSec  { get; set; }   // computed: ms from FIGHT start → seconds
}

/// <summary>
/// Thin wrapper around the FF Logs v1 REST API.
/// </summary>
public class FflogsClient : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://www.fflogs.com/v1/"),
        Timeout     = TimeSpan.FromSeconds(15),
    };

    private readonly IPluginLog Log;
    private readonly string     ApiKey;

    public FflogsClient(string apiKey, IPluginLog log)
    {
        ApiKey = apiKey;
        Log    = log;
    }

    // =========================================================================
    // STEP 1: FETCH FIGHT LIST + ACTOR LIST
    // =========================================================================

    /// <summary>
    /// Fetches all fights and actors in a report.
    /// Returns (fights, actors) or throws on failure.
    /// </summary>
    public async Task<(List<FflogsFight> Fights, List<FflogsActor> Actors)> GetFightsAsync(string reportCode)
    {
        var url  = $"report/fights/{reportCode}?api_key={ApiKey}";
        var json = await GetJsonAsync(url);

        var fights = new List<FflogsFight>();
        var actors = new List<FflogsActor>();

        // Parse fights array
        if (json["fights"] is JsonArray fightArr)
        {
            foreach (var f in fightArr)
            {
                if (f == null) continue;
                fights.Add(new FflogsFight
                {
                    Id        = f["id"]?.GetValue<int>()    ?? 0,
                    Name      = f["name"]?.GetValue<string>() ?? "Unknown",
                    StartTime = f["start_time"]?.GetValue<long>() ?? 0,
                    EndTime   = f["end_time"]?.GetValue<long>()   ?? 0,
                    Kill      = f["kill"]?.GetValue<bool>()       ?? false,
                });
            }
        }

        // Parse friendlies array (these are the player actors)
        if (json["friendlies"] is JsonArray friendlyArr)
        {
            foreach (var a in friendlyArr)
            {
                if (a == null) continue;
                var type = a["type"]?.GetValue<string>() ?? "";
                // Skip non-player entries like "Unknown", "Limit Break", NPC bosses
                if (type != "DarkKnight" && type != "Warrior"   && type != "Paladin"   &&
                    type != "Gunbreaker" && type != "WhiteMage"  && type != "Scholar"   &&
                    type != "Astrologian"&& type != "Sage"        && type != "Monk"      &&
                    type != "Dragoon"    && type != "Ninja"       && type != "Samurai"   &&
                    type != "Reaper"     && type != "Viper"       && type != "Bard"      &&
                    type != "Machinist"  && type != "Dancer"      && type != "BlackMage"  &&
                    type != "Summoner"   && type != "RedMage"     && type != "Pictomancer")
                    continue;

                actors.Add(new FflogsActor
                {
                    Id      = a["id"]?.GetValue<int>()      ?? 0,
                    Name    = a["name"]?.GetValue<string>()  ?? "Unknown",
                    Type    = "Player",
                    SubType = type,
                });
            }
        }

        Log.Debug($"[FFLogs] Report: {fights.Count} fights, {actors.Count} players");
        return (fights, actors);
    }

    // =========================================================================
    // STEP 2: FETCH CAST EVENTS
    // =========================================================================

    /// <summary>
    /// Fetches all cast events for one source player in one fight.
    /// Handles pagination automatically.
    /// </summary>
    public async Task<List<FflogsCastEvent>> GetCastEventsAsync(
        string reportCode, FflogsFight fight, int sourceActorId)
    {
        var allEvents = new List<FflogsCastEvent>();
        long? nextPageTimestamp = null;

        do
        {
            // Build URL — start/end are ms from REPORT start (not fight start)
            var start = nextPageTimestamp ?? fight.StartTime;
            var url   = $"report/events/casts/{reportCode}" +
                        $"?start={start}&end={fight.EndTime}" +
                        $"&sourceid={sourceActorId}" +
                        $"&api_key={ApiKey}";

            var json = await GetJsonAsync(url);

            // Parse events array
            if (json["events"] is JsonArray evtArr)
            {
                foreach (var e in evtArr)
                {
                    if (e == null) continue;
                    if (e["type"]?.GetValue<string>() != "cast") continue;

                    var ts = e["timestamp"]?.GetValue<long>() ?? 0;
                    var abilId = e["ability"]?["guid"]?.GetValue<int>() ?? 0;
                    var abilName = e["ability"]?["name"]?.GetValue<string>() ?? $"#{abilId}";

                    // Skip auto-attacks and very low IDs (usually internal)
                    if (abilId <= 7) continue;

                    allEvents.Add(new FflogsCastEvent
                    {
                        Timestamp   = ts,
                        AbilityId   = abilId,
                        AbilityName = abilName,
                        ElapsedSec  = (ts - fight.StartTime) / 1000f,
                    });
                }
            }

            // Check for next page
            nextPageTimestamp = json["nextPageTimestamp"]?.GetValue<long?>();

        } while (nextPageTimestamp.HasValue && nextPageTimestamp < fight.EndTime);

        Log.Debug($"[FFLogs] Fetched {allEvents.Count} cast events for source {sourceActorId}");
        return allEvents;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private async Task<JsonObject> GetJsonAsync(string relativeUrl)
    {
        var response = await Http.GetAsync(relativeUrl);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"FFLogs API returned {(int)response.StatusCode}: {response.ReasonPhrase}");

        var body = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(body);

        if (node is not JsonObject obj)
            throw new Exception("FFLogs returned unexpected JSON structure.");

        // v1 API returns { "status": 400, "error": "..." } on failure
        if (obj["status"] is JsonNode statusNode && statusNode.GetValue<int>() >= 400)
            throw new Exception($"FFLogs error: {obj["error"]?.GetValue<string>() ?? "unknown"}");

        return obj;
    }

    public void Dispose() { }
}
