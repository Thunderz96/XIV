// =============================================================================
// WtfDigManifest.cs — Bundled Fight & Mechanic Registry
// =============================================================================
//
// Each mechanic has a list of named "strats" — different callout pictures for
// the same mechanic (e.g. "Toxic" vs "Hector" strats for the same boss attack).
// The user picks one active strat per fight in the Settings UI; all mechanics
// in that fight flip to that strat's image automatically.
//
// JSON structure:
//   {
//     "id": "74/m12s",
//     "mechanics": [
//       {
//         "slug":        "toxic-act1",       ← internal ID
//         "label":       "Toxic Act 1",      ← shown in the UI
//         "triggerTime": 45.0,               ← seconds into fight (0 = manual)
//         "strats": [
//           { "name": "Toxic",  "image": "p1-toxic-act1-dps-zoomed.webp"  },
//           { "name": "Hector", "image": "p1-hector-act1-dps-zoomed.webp" }
//         ]
//       }
//     ]
//   }
//
// Full URL = https://wtfdig.info/{fight.id}/{strat.image}
// The first strat in the list is the default shown until the user changes it.
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace StratOverlay.Data;

// ---- JSON model types ----

/// <summary>
/// One named strat option for a mechanic.
/// name  = displayed in the UI (e.g. "Toxic", "Hector")
/// image = exact filename on wtfdig (e.g. "p1-toxic-act1-dps-zoomed.webp")
/// </summary>
public record WtfDigStrat(string Name, string Image);

/// <summary>
/// One mechanic. Has multiple named strats — different images for the same mechanic.
/// The active strat for the fight is selected globally in Settings, not per-mechanic.
/// If no strat with the active name exists for this mechanic, the first strat is used.
/// </summary>
public record WtfDigMechanic(
    string            Slug,
    string            Label,
    float             TriggerTime,
    List<WtfDigStrat> Strats);

public record WtfDigFight(
    string               Id,
    string               Name,
    int                  TerritoryTypeId,
    List<WtfDigMechanic> Mechanics);

public record WtfDigManifestData(List<WtfDigFight> Fights);

// ---- Loader ----

public static class WtfDigManifest
{
    private static WtfDigManifestData? _data;

    public static void ClearCache() => _data = null;

    /// <summary>
    /// Loads and returns the manifest. Cached after first call.
    /// </summary>
    public static WtfDigManifestData Load()
    {
        if (_data != null) return _data;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("WtfDigManifest.json")
            ?? throw new System.InvalidOperationException(
                "WtfDigManifest.json not found as embedded resource.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _data = JsonSerializer.Deserialize<WtfDigManifestData>(json, options)
            ?? throw new System.InvalidOperationException("Failed to deserialize WtfDigManifest.json.");

        return _data;
    }

    /// <summary>
    /// Builds the full URL for a mechanic image.
    /// e.g. BuildUrl("74/m12s", "p1-toxic-act1-dps-zoomed.webp")
    ///      → https://wtfdig.info/74/m12s/p1-toxic-act1-dps-zoomed.webp
    /// </summary>
    public static string BuildUrl(string fightId, string filename)
        => $"https://wtfdig.info/{fightId}/{filename}";
}
