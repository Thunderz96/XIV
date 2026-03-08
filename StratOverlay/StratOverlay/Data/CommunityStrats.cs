// =============================================================================
// CommunityStrats.cs — Auto-generate built-in strat timelines from wtfdig
// =============================================================================
//
// Reads WtfDigManifest.json and creates one StratTimeline per fight.
// Each mechanic becomes one StratEntry, with one image per named strat.
//
// STRAT SELECTION:
//   The user picks an active strat per fight (e.g. "Hector" for M12S) in
//   the Settings tab. That selection is stored in cfg.ActiveStratPerFight.
//   At display time, StratEngine calls GetActiveImage(entry, fightId, cfg)
//   to resolve which image to show — it picks the strat name's key, falling
//   back to the first available strat if no match.
//
// TIMELINE VARIANTS:
//   Each unique strat name across all mechanics becomes a StratVariant on
//   the timeline. The "active variant" concept drives which image shows —
//   selecting "Hector" as the variant = showing Hector's image for every
//   mechanic in that fight.
//
// TRIGGER BEHAVIOUR:
//   - triggerTime > 0 → TriggerType.Timeline, fires at that time
//   - triggerTime == 0 → TriggerType.Manual, only shown on demand
// =============================================================================

using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace StratOverlay.Data;

public static class CommunityStrats
{
    /// <summary>
    /// Generates built-in timelines on first launch. No-op if already generated.
    /// </summary>
    public static bool GenerateIfNeeded(Configuration cfg, IPluginLog log)
    {
        if (cfg.CommunityStratsGenerated) return false;
        Generate(cfg, log);
        return true;
    }

    /// <summary>
    /// (Re)generates all built-in community timelines from the manifest.
    /// Safe to call any time — removes old built-ins first.
    /// </summary>
    public static void Generate(Configuration cfg, IPluginLog log)
    {
        cfg.Timelines.RemoveAll(t => t.IsBuiltIn);

        var manifest = WtfDigManifest.Load();
        int created  = 0;

        foreach (var fight in manifest.Fights)
        {
            if (fight.Mechanics.Count == 0) continue;

            var timeline = new StratTimeline
            {
                Id              = $"community::{fight.Id}",
                Name            = fight.Name,
                Author          = "wtfdig.info (community)",
                TerritoryTypeId = fight.TerritoryTypeId,
                IsBuiltIn       = true,
                Enabled         = true,
            };

            // Collect every unique strat name that appears across all mechanics
            // in this fight. Each unique name becomes one selectable variant.
            // Order is preserved — first strat listed anywhere becomes the default.
            var stratNames = fight.Mechanics
                .SelectMany(m => m.Strats.Select(s => s.Name))
                .Distinct()
                .ToList();

            // Always ensure at least a "Default" variant exists
            if (stratNames.Count == 0) stratNames.Add("Default");

            foreach (var name in stratNames)
                timeline.Variants.Add(new StratVariant { Id = name, Name = name });

            // One entry per mechanic
            foreach (var mech in fight.Mechanics)
            {
                var triggerType = mech.TriggerTime > 0f
                    ? TriggerType.Timeline
                    : TriggerType.Manual;

                var entry = new StratEntry
                {
                    Label           = mech.Label,
                    TriggerType     = triggerType,
                    TriggerTime     = mech.TriggerTime,
                    PreShowSeconds  = 3f,
                    DisplayDuration = 10f,
                    Enabled         = true,
                    RoleFilter      = TargetRole.All,
                };

                // One image per strat. Key = strat name (e.g. "Hector").
                // The engine looks up Images[activeStratName], falling back to
                // Images["default"] if that strat has no image for this mechanic.
                foreach (var strat in mech.Strats)
                {
                    entry.Images[strat.Name] = new StratImage
                    {
                        Source = ImageSource.WtfDig,
                        // Full relative path: "{fightId}/{filename}"
                        // ImageCache builds: https://wtfdig.info/{fightId}/{filename}
                        Path = $"{fight.Id}/{strat.Image}",
                    };
                }

                // "default" fallback = first strat's image
                if (mech.Strats.Count > 0 && !entry.Images.ContainsKey("default"))
                {
                    entry.Images["default"] = entry.Images[mech.Strats[0].Name];
                }

                timeline.Entries.Add(entry);
            }

            cfg.Timelines.Insert(0, timeline);
            created++;
        }

        cfg.CommunityStratsGenerated = true;
        cfg.Save();
        log.Info($"[CommunityStrats] Generated {created} built-in timelines.");
    }

    /// <summary>
    /// Resolves which image to display for this entry.
    /// Uses entry.ActiveStratKey, falling back to "default", then any first image.
    /// Call this from StratEngine at display time.
    /// </summary>
    public static StratImage? GetActiveImage(StratEntry entry)
    {
        // Try the entry's own chosen strat key
        if (!string.IsNullOrEmpty(entry.ActiveStratKey)
            && entry.Images.TryGetValue(entry.ActiveStratKey, out var img))
            return img;

        // Fall back to "default" (always the first strat in the manifest)
        if (entry.Images.TryGetValue("default", out var fallback))
            return fallback;

        // Last resort: first available image
        foreach (var v in entry.Images.Values) return v;
        return null;
    }
}
