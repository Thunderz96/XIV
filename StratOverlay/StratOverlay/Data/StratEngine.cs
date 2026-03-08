// =============================================================================
// StratEngine.cs — Runtime Engine: Timeline + BossAction + Status Triggers (M5)
// =============================================================================
//
// Three trigger systems now run in parallel:
//
//   1. TIMELINE  — same as M1; fires at a fixed elapsed fight time
//
//   2. BOSSACTION — hooks two game signals:
//        CastStart    → fires when the cast bar first appears
//                       (IObjectTable scan on framework update — simpler &
//                        reliable; polls cast data each frame while running)
//        AbilityUse   → fires when the ability actually resolves/hits
//                       (hooks the network ActionEffect packet via
//                        IGameInteropProvider, same approach as ACT plugins)
//
//   3. STATUS (OnStatusApplied) — hooks the StatusManager update packet
//        to detect when a specific status ID is applied to any party member.
//
// EXPIRY TRACKING (new in M5):
//   The engine now also tracks *active* triggered entries and fires
//   OnStratExpired when their DisplayDuration elapses. The overlay window
//   subscribes to this as a belt-and-suspenders complement to its own
//   per-display Stopwatch.
//
// THREADING:
//   - Framework.Update runs on the game thread — safe for IObjectTable, ICondition
//   - ActionEffect and Status hooks are called on game threads (not UI thread)
//     but we only read/write HashSets and fire events, which is safe enough
//     for our use case (worst case: a missed trigger, never a crash).
//   - IPlayerState is safe from any thread in Dalamud API 14.
//
// DEPRECATION FIX (M5):
//   Replaced IClientState.LocalPlayer (obsolete) with IPlayerState for role
//   detection. IPlayerState is injected via Plugin and passed to the engine.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

using StratOverlay.Data;

namespace StratOverlay;

// =============================================================================
// EVENT ARGS
// =============================================================================

/// <summary>Fired when a strat entry should be shown on screen.</summary>
public class StratTriggerEventArgs : EventArgs
{
    public required StratEntry Entry         { get; init; }
    public required StratImage ResolvedImage { get; init; }
    public required float      FightTime     { get; init; }
}

/// <summary>Fired when an active display has exceeded its DisplayDuration.</summary>
public class StratExpiredEventArgs : EventArgs
{
    public required StratEntry Entry { get; init; }
}

// =============================================================================
// ACTIVE ENTRY TRACKER — engine-side expiry tracking
// =============================================================================

/// <summary>
/// Records an entry the engine has fired so we can expire it after its
/// DisplayDuration elapses and raise OnStratExpired.
/// </summary>
internal class ActiveEntry
{
    public required StratEntry Entry    { get; init; }
    public          Stopwatch  Age      { get; } = Stopwatch.StartNew();
}

// =============================================================================
// ENGINE
// =============================================================================

public class StratEngine : IDisposable
{
    // ---- Services ----
    private readonly ICondition           Condition;
    private readonly IPlayerState         PlayerState;   // replaces obsolete LocalPlayer
    private readonly IFramework           Framework;
    private readonly IObjectTable         ObjectTable;
    private readonly IGameInteropProvider Interop;
    private readonly IPluginLog           Log;

    // ---- Fight state ----
    private readonly Stopwatch fightStopwatch = new();
    private StratTimeline?     activeTimeline;

    // ---- Trigger dedup: entry IDs fired this combat ----
    private readonly HashSet<string> firedEntryIds = new();

    // ---- Active entries for expiry tracking ----
    private readonly List<ActiveEntry> activeEntries = new();

    // ---- Cast tracking for CastStart triggers ----
    // Key: game object ID of a casting actor → ability ID currently being cast
    // Populated by IObjectTable scan; cleared when cast ends.
    private readonly Dictionary<ulong, uint> activeCasts = new();

    // ---- ActionEffect hook (for AbilityUse triggers) ----
    // The ActionEffect1 signature fires when a single-target ability resolves.
    // ActionEffect8/16/24/32 fire for AoE packets with multiple targets.
    // We hook ActionEffect1 as a representative for boss abilities (bosses
    // rarely use true single-target actions, but the cast-based ID is reliable).
    //
    // Signature found via Dalamud plugin samples / Universalis codebase.
    // Byte signature: "40 55 53 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 4C 8B EA"
    //
    // IMPORTANT: We ONLY read the sourceId and actionId from the packet.
    // We do NOT write to game memory or call back into the original function
    // with modified data. This is a pure read-only hook.
    // =============================================================================

    // The delegate type must match the exact calling convention of the hooked function.
    // Parameters: sourceId = game object ID of the caster,
    //             actionId = the action/ability ID that resolved.
    private delegate void ActionEffectDelegate(
        ulong sourceId, uint actionId, IntPtr targetList, IntPtr effectList, uint targetCount);

    private Hook<ActionEffectDelegate>? _actionEffectHook;

    // ---- Status hook (for StatusApplied triggers) ----
    // Hooks the function called when a status effect is applied to any actor.
    // Signature: "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 8B EA"
    private delegate void StatusAppliedDelegate(
        IntPtr actorPtr, uint statusId, ushort stackCount, float duration, uint sourceId);

    private Hook<StatusAppliedDelegate>? _statusAppliedHook;

    // =========================================================================
    // PUBLIC PROPERTIES
    // =========================================================================

    public bool    IsRunning         => fightStopwatch.IsRunning;
    public float   CurrentTime       => (float)fightStopwatch.Elapsed.TotalSeconds;
    public string? ActiveTimelineName => activeTimeline?.Name;

    // =========================================================================
    // EVENTS
    // =========================================================================

    public event EventHandler<StratTriggerEventArgs>? OnStratTriggered;
    public event EventHandler<StratExpiredEventArgs>? OnStratExpired;
    public event EventHandler?                        OnCombatStarted;
    public event EventHandler?                        OnCombatEnded;
    public event EventHandler<string>?                OnVariantChanged;

    // =========================================================================
    // CONSTRUCTOR / DISPOSE
    // =========================================================================

    public StratEngine(
        ICondition           condition,
        IPlayerState         playerState,
        IFramework           framework,
        IObjectTable         objectTable,
        IGameInteropProvider interop,
        IPluginLog           log)
    {
        Condition   = condition;
        PlayerState = playerState;
        Framework   = framework;
        ObjectTable = objectTable;
        Interop     = interop;
        Log         = log;

        // Hook ActionEffect — fires when an ability resolves in the game world.
        // We use TryCreate so a bad signature just skips hooking rather than crash.
        TryHookActionEffect();
        TryHookStatusApplied();

        Framework.Update += OnFrameworkUpdate;
        Log.Info("[StratEngine] Initialised (M5: BossAction + Status hooks active).");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;

        _actionEffectHook?.Disable();
        _actionEffectHook?.Dispose();

        _statusAppliedHook?.Disable();
        _statusAppliedHook?.Dispose();

        fightStopwatch.Stop();
        Log.Info("[StratEngine] Disposed.");
    }

    // =========================================================================
    // HOOK SETUP
    // =========================================================================

    private void TryHookActionEffect()
    {
        try
        {
            // Signature for ActionEffect1 packet handler — reads sourceId + actionId
            _actionEffectHook = Interop.HookFromSignature<ActionEffectDelegate>(
                "40 55 53 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 4C 8B EA",
                OnActionEffect);
            _actionEffectHook.Enable();
            Log.Info("[StratEngine] ActionEffect hook installed.");
        }
        catch (Exception ex)
        {
            // Hook failed (sig mismatch after a game patch). BossAction/AbilityUse
            // triggers will not fire, but Timeline triggers still work fine.
            Log.Warning($"[StratEngine] ActionEffect hook failed: {ex.Message}. " +
                        "BossAction/AbilityUse triggers disabled until sig is updated.");
        }
    }

    private void TryHookStatusApplied()
    {
        try
        {
            _statusAppliedHook = Interop.HookFromSignature<StatusAppliedDelegate>(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 8B EA",
                OnStatusApplied);
            _statusAppliedHook.Enable();
            Log.Info("[StratEngine] StatusApplied hook installed.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[StratEngine] StatusApplied hook failed: {ex.Message}. " +
                        "OnStatusApplied triggers disabled until sig is updated.");
        }
    }

    // =========================================================================
    // TIMELINE LOADING
    // =========================================================================

    /// <summary>
    /// Loads a timeline and resets all trigger state.
    /// Does NOT start the stopwatch — that happens on combat start or ManualStart.
    /// </summary>
    public void LoadTimeline(StratTimeline timeline)
    {
        activeTimeline = timeline;
        Reset();
        Log.Info($"[StratEngine] Loaded \"{timeline.Name}\" " +
                 $"(variant: \"{timeline.GetActiveVariant().Name}\", " +
                 $"entries: {timeline.Entries.Count})");
    }

    /// <summary>Unloads the timeline and stops everything.</summary>
    public void UnloadTimeline()
    {
        activeTimeline = null;
        fightStopwatch.Stop();
        firedEntryIds.Clear();
        activeEntries.Clear();
        activeCasts.Clear();
        Log.Info("[StratEngine] Timeline unloaded.");
    }

    // =========================================================================
    // VARIANT SWITCHING
    // =========================================================================

    public void SetVariant(string variantId)
    {
        if (activeTimeline == null) return;
        var variant = activeTimeline.Variants.Find(v => v.Id == variantId);
        if (variant == null)
        {
            Log.Warning($"[StratEngine] Variant '{variantId}' not found.");
            return;
        }
        activeTimeline.ActiveVariantId = variantId;
        OnVariantChanged?.Invoke(this, variantId);
        Log.Info($"[StratEngine] Variant → \"{variant.Name}\"");
    }

    public void CycleVariant()
    {
        if (activeTimeline == null || activeTimeline.Variants.Count <= 1) return;
        var idx     = activeTimeline.Variants.FindIndex(v => v.Id == activeTimeline.ActiveVariantId);
        var nextIdx = (idx + 1) % activeTimeline.Variants.Count;
        SetVariant(activeTimeline.Variants[nextIdx].Id);
    }

    // =========================================================================
    // MANUAL CONTROL
    // =========================================================================

    public void ManualStart()
    {
        if (activeTimeline == null)
        {
            Log.Warning("[StratEngine] ManualStart: no timeline loaded.");
            return;
        }
        Reset();
        fightStopwatch.Start();
        OnCombatStarted?.Invoke(this, EventArgs.Empty);
        Log.Info("[StratEngine] Manual start.");
    }

    public void ManualStop()
    {
        fightStopwatch.Stop();
        OnCombatEnded?.Invoke(this, EventArgs.Empty);
        Log.Info("[StratEngine] Manual stop.");
    }

    // =========================================================================
    // FRAMEWORK UPDATE — game thread, every frame
    // =========================================================================

    private bool _wasInCombat = false;

    private void OnFrameworkUpdate(IFramework _)
    {
        bool inCombat = Condition[ConditionFlag.InCombat];

        // ---- Combat start ----
        if (inCombat && !_wasInCombat)
        {
            _wasInCombat = true;
            if (activeTimeline != null && activeTimeline.Enabled)
            {
                Reset();
                fightStopwatch.Start();
                OnCombatStarted?.Invoke(this, EventArgs.Empty);
                Log.Info($"[StratEngine] Combat started — \"{activeTimeline.Name}\".");
            }
        }

        // ---- Combat end ----
        if (!inCombat && _wasInCombat)
        {
            _wasInCombat = false;
            fightStopwatch.Stop();
            activeCasts.Clear();
            OnCombatEnded?.Invoke(this, EventArgs.Empty);
            Log.Info("[StratEngine] Combat ended.");
        }

        if (!fightStopwatch.IsRunning || activeTimeline == null || !activeTimeline.Enabled)
            return;

        float currentTime = CurrentTime;

        // ---- 1. Timeline triggers ----
        ScanTimelineTriggers(currentTime);

        // ---- 2. CastStart triggers (IObjectTable poll) ----
        ScanCastStartTriggers();

        // ---- 3. Expiry: fire OnStratExpired for timed-out active entries ----
        ScanExpiredEntries();
    }

    // =========================================================================
    // TIMELINE TRIGGER SCAN
    // =========================================================================

    private void ScanTimelineTriggers(float currentTime)
    {
        foreach (var entry in activeTimeline!.Entries)
        {
            if (!entry.Enabled)                             continue;
            if (entry.TriggerType != TriggerType.Timeline) continue;
            if (firedEntryIds.Contains(entry.Id))          continue;
            if (!PassesRoleFilter(entry))                   continue;

            float fireAt = entry.TriggerTime - entry.PreShowSeconds;
            if (currentTime < fireAt) continue;

            FireEntry(entry, currentTime);
        }
    }

    // =========================================================================
    // CASTSTART TRIGGER SCAN (IObjectTable poll)
    // =========================================================================
    // Every frame we walk IObjectTable looking for BattleNpc actors that are
    // currently casting. If an actor's CastActionId matches a BossAction entry
    // with CastPhase.OnCastStart, we fire it.
    //
    // We track previous frame's cast state so we only fire on the FIRST frame
    // a cast appears (not every frame it continues).
    // =========================================================================

    private void ScanCastStartTriggers()
    {
        // Collect current casts from the object table
        var currentCasts = new Dictionary<ulong, uint>();

        foreach (var obj in ObjectTable)
        {
            // Only care about BattleNpc (enemy npcs) — object kind 2
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            var bnpc = obj as IBattleNpc;
            if (bnpc == null) continue;

            // IsCasting is true when the cast bar is visible; CastActionId is the ID
            if (bnpc.IsCasting && bnpc.CastActionId != 0)
                currentCasts[bnpc.GameObjectId] = bnpc.CastActionId;
        }

        // Find casts that are NEW this frame (weren't in activeCasts last frame)
        foreach (var (objId, abilityId) in currentCasts)
        {
            if (activeCasts.ContainsKey(objId)) continue; // already seen

            // New cast — check if any entry wants this ability ID on CastStart
            CheckBossActionEntries(abilityId, CastPhase.OnCastStart);
        }

        // Update state for next frame
        activeCasts.Clear();
        foreach (var kv in currentCasts)
            activeCasts[kv.Key] = kv.Value;
    }

    // =========================================================================
    // ACTION EFFECT HOOK — fires when an ability resolves (AbilityUse)
    // =========================================================================

    private void OnActionEffect(
        ulong sourceId, uint actionId, IntPtr targetList, IntPtr effectList, uint targetCount)
    {
        // Always call the original function first — we must not skip it
        _actionEffectHook!.Original(sourceId, actionId, targetList, effectList, targetCount);

        // Only process when we have a timeline running
        if (activeTimeline == null || !fightStopwatch.IsRunning) return;

        CheckBossActionEntries(actionId, CastPhase.OnAbilityUse);
    }

    // =========================================================================
    // STATUS APPLIED HOOK — fires when a status effect is applied
    // =========================================================================

    private void OnStatusApplied(
        IntPtr actorPtr, uint statusId, ushort stackCount, float duration, uint sourceId)
    {
        // Always call original
        _statusAppliedHook!.Original(actorPtr, statusId, stackCount, duration, sourceId);

        if (activeTimeline == null || !fightStopwatch.IsRunning) return;

        // For OnStatusApplied triggers we match on AbilityId being used as
        // a status ID (reusing the field to avoid adding more model complexity).
        foreach (var entry in activeTimeline.Entries)
        {
            if (!entry.Enabled)                               continue;
            if (entry.TriggerType != TriggerType.BossAction) continue;
            if (entry.CastPhase   != CastPhase.OnStatusApplied) continue;
            if (firedEntryIds.Contains(entry.Id))             continue;
            if (entry.AbilityId   != statusId)                continue;
            if (!PassesRoleFilter(entry))                     continue;

            FireEntry(entry, CurrentTime);
        }
    }

    // =========================================================================
    // BOSS ACTION HELPER
    // =========================================================================

    /// <summary>
    /// Checks all BossAction entries for a matching ability ID and cast phase,
    /// and fires any matches that haven't been fired yet.
    /// </summary>
    private void CheckBossActionEntries(uint abilityId, CastPhase phase)
    {
        if (activeTimeline == null) return;

        foreach (var entry in activeTimeline.Entries)
        {
            if (!entry.Enabled)                               continue;
            if (entry.TriggerType != TriggerType.BossAction) continue;
            if (entry.CastPhase   != phase)                   continue;
            if (firedEntryIds.Contains(entry.Id))             continue;
            if (entry.AbilityId   != abilityId)               continue;
            if (!PassesRoleFilter(entry))                     continue;

            FireEntry(entry, CurrentTime);
        }
    }

    // =========================================================================
    // EXPIRY SCAN
    // =========================================================================

    /// <summary>
    /// Checks all active entries and fires OnStratExpired for any whose
    /// DisplayDuration has elapsed. Duration 0 = show forever (no expiry).
    /// </summary>
    private void ScanExpiredEntries()
    {
        for (int i = activeEntries.Count - 1; i >= 0; i--)
        {
            var ae = activeEntries[i];
            if (ae.Entry.DisplayDuration <= 0f) continue; // show forever

            if (ae.Age.Elapsed.TotalSeconds >= ae.Entry.DisplayDuration)
            {
                activeEntries.RemoveAt(i);
                OnStratExpired?.Invoke(this, new StratExpiredEventArgs { Entry = ae.Entry });
                Log.Debug($"[StratEngine] Expired: \"{ae.Entry.Label}\"");
            }
        }
    }

    // =========================================================================
    // SHARED FIRE HELPER
    // =========================================================================

    /// <summary>
    /// Marks an entry as fired, adds it to the active tracking list,
    /// and raises OnStratTriggered. Called by all three trigger paths.
    /// </summary>
    private void FireEntry(StratEntry entry, float fightTime)
    {
        // Community (built-in) timelines use per-entry ActiveStratKey to pick
        // which named strat image to show (e.g. "Toxic" vs "Hector").
        // Custom timelines use the timeline-wide ActiveVariantId instead.
        StratImage? image = activeTimeline!.IsBuiltIn
            ? CommunityStrats.GetActiveImage(entry)
            : activeTimeline.ResolveImage(entry);

        if (image == null)
        {
            // Entry has no image — still mark fired to avoid spam
            firedEntryIds.Add(entry.Id);
            return;
        }

        firedEntryIds.Add(entry.Id);
        activeEntries.Add(new ActiveEntry { Entry = entry });

        OnStratTriggered?.Invoke(this, new StratTriggerEventArgs
        {
            Entry         = entry,
            ResolvedImage = image,
            FightTime     = fightTime,
        });

        Log.Debug($"[StratEngine] Fired \"{entry.Label}\" " +
                  $"(type:{entry.TriggerType} phase:{entry.CastPhase} t:{fightTime:F1}s)");
    }

    // =========================================================================
    // ROLE FILTER HELPER
    // =========================================================================

    /// <summary>
    /// Returns true if the local player's role matches the entry's RoleFilter,
    /// or if the filter is set to All. Uses IPlayerState (not obsolete LocalPlayer).
    /// </summary>
    private bool PassesRoleFilter(StratEntry entry)
    {
        if (entry.RoleFilter == TargetRole.All) return true;

        // IPlayerState.ClassJob is a Lumina RowRef<ClassJob>; .RowId gives the uint job ID
        var jobId = PlayerState.ClassJob.RowId;
        if (jobId == 0) return true; // not in a job (shouldn't happen in combat)

        // Look up role from the ClassJob sheet via IDataManager would be ideal,
        // but to avoid injecting IDataManager we use a static role lookup table
        // covering all current combat jobs.
        byte role = GetJobRole(jobId);

        return entry.RoleFilter switch
        {
            TargetRole.Tank   => role == 1,
            TargetRole.Healer => role == 4,
            TargetRole.DPS    => role == 2 || role == 3,
            _                 => true,
        };
    }

    // =========================================================================
    // JOB ROLE TABLE
    // =========================================================================
    // Static lookup of Job ID → role byte (1=Tank, 2=Melee DPS, 3=Ranged DPS, 4=Healer)
    // Job IDs from FFXIV ClassJob sheet. Updated through Dawntrail (7.x).
    // =========================================================================

    private static byte GetJobRole(uint jobId) => jobId switch
    {
        // ---- Tanks ----
        1  => 1,  // GLA
        3  => 1,  // MRD
        19 => 1,  // PLD
        21 => 1,  // WAR
        32 => 1,  // DRK
        37 => 1,  // GNB

        // ---- Healers ----
        6  => 4,  // CNJ
        24 => 4,  // WHM
        28 => 4,  // SCH
        33 => 4,  // AST
        40 => 4,  // SGE

        // ---- Melee DPS ----
        2  => 2,  // PGL
        4  => 2,  // LNC
        7  => 2,  // THM (used as MNK base in some contexts)
        20 => 2,  // MNK
        22 => 2,  // DRG
        29 => 2,  // NIN
        30 => 2,  // ROG
        34 => 2,  // SAM
        39 => 2,  // RPR
        41 => 2,  // VPR

        // ---- Ranged/Magic DPS ----
        5  => 3,  // ARC
        23 => 3,  // BRD
        25 => 3,  // BLM
        26 => 3,  // SMN
        27 => 3,  // SCH (pre-split, treated as ranged DPS)
        31 => 3,  // MCH
        35 => 3,  // RDM
        36 => 3,  // BLU
        38 => 3,  // DNC
        42 => 3,  // PCT

        // Unknown/crafters/gatherers → treat as DPS to be safe
        _  => 3,
    };

    // =========================================================================
    // RESET
    // =========================================================================

    private void Reset()
    {
        fightStopwatch.Reset();
        firedEntryIds.Clear();
        activeEntries.Clear();
        activeCasts.Clear();
    }
}
