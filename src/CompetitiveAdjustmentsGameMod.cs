// CompetitiveAdjustmentsGameMod.cs
// Single IPuckMod entry point for the combined CompetitiveAdjustments mod.
// Detects headless server vs windowed client and routes to the appropriate sub-mods:
//   Server  → CompetitivePuckTweaks (physics/gameplay tuning)
//   Client  → CompetitiveCompanion (visual companion)
//   Both    → DashFall (dash/dive/twist – it self-gates its own client components)

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class CompetitiveAdjustmentsGameMod : IPuckPlugin
{
    private static CompetitiveAdjustmentsGameMod _instance;
    private Harmony _lifecycleHarmony;
    private DashFallGameMod _dashFall;
    private CompetitivePuckTweaks.src.PluginCore _tweaks;
    private CompetitiveCompanion.PluginCore _companion;

    private static bool IsHeadless =>
        Application.isBatchMode ||
        SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    public bool OnEnable()
    {
        try
        {
            _instance = this;
            bool isServer = IsHeadless;
            Debug.Log($"[COMPADJUST] Enabling (isServer={isServer})…");

            // Initialize unified config before any sub-mods so the section
            // enable flags are available when deciding what to load.  The
            // ReloadConfig call below fires NotifyConfigReloaded which calls
            // ApplySubModEnables; that creates the sub-mods we want.  We
            // intentionally do NOT also create them inline here, otherwise
            // they would be double-instantiated.
            if (isServer)
            {
                CompetitiveAdjustments.ConfigManager.EnsureConfig();
                CompetitiveAdjustments.ConfigManager.ReloadConfig();
            }

            _lifecycleHarmony = new Harmony("CompetitiveAdjustments.Lifecycle");
            _lifecycleHarmony.CreateClassProcessor(typeof(StartHostPatch)).Patch();
            _lifecycleHarmony.CreateClassProcessor(typeof(StartServerPatch)).Patch();

            // Belt-and-braces for the client path (no ReloadConfig was called)
            // and the dedicated-server case where the NotifyConfigReloaded
            // hook might have fired before _instance was assigned.
            ApplySubModEnables();

            var cfg = CompetitiveAdjustments.ConfigManager.Config;
            CompetitiveAdjustments.ConfigManager.Log(
                $"Enabled.  Section enables: Dashfall={cfg.EnableDashfall} CompAdjust={cfg.EnableCompAdjust} CompTweaks={cfg.EnableCompTweaks}.");
            return true;
        }
        catch (Exception ex)
        {
            CompetitiveAdjustments.ConfigManager.LogError("FATAL in OnEnable: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Ensure each sub-mod is instantiated for the current role.  Section
    /// enable flags are NOT consulted here -- they are runtime feature gates
    /// applied via the Effective config accessors (DashfallEffective,
    /// CompAdjustEffective, CompTweaksEffective).  Loading the sub-mods
    /// unconditionally is necessary because DashFall.ServerBridge owns the
    /// PPKB/GoalTweaks sync that CompAdjust depends on, and the DashFallMod
    /// namespace hosts patches for both Dashfall and CompAdjust features.
    /// Gating sub-mod load on a section flag was cascading into other
    /// sections in unexpected ways.
    ///
    /// Role detection:
    /// - dedicated server: _tweaks only.
    /// - pre-host client:  _companion only.
    /// - hosting client:   _companion (from OnEnable) plus _tweaks (after
    ///                     StartHost fires EnsureServerRuntimeComponents).
    /// </summary>
    public void ApplySubModEnables()
    {
        try
        {
            bool isHeadless = IsHeadless;
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool isServerSide = isHeadless || (nm != null && nm.IsServer);

            if (_dashFall == null)
            {
                _dashFall = new DashFallGameMod();
                if (!_dashFall.OnEnable())
                    CompetitiveAdjustments.ConfigManager.LogWarning("DashFall sub-mod failed to enable on apply.");
            }

            if (isServerSide)
            {
                // Tweaks.OnEnable bails when NetworkManager.IsServer is false,
                // which is the case at plugin load for a hosting client.
                // Always call OnEnable here so a later StartHost retry
                // actually brings Tweaks online.  OnEnable is idempotent --
                // it early-returns when already enabled.
                if (_tweaks == null) _tweaks = new CompetitivePuckTweaks.src.PluginCore();
                _tweaks.OnEnable();
            }
            if (!isHeadless && _companion == null)
            {
                _companion = new CompetitiveCompanion.PluginCore();
                if (!_companion.OnEnable())
                    CompetitiveAdjustments.ConfigManager.LogWarning("Companion sub-mod failed to enable on apply.");
            }
        }
        catch (Exception ex)
        {
            CompetitiveAdjustments.ConfigManager.LogError("ApplySubModEnables failed: " + ex);
        }
    }

    internal static void NotifyConfigReloaded()
    {
        _instance?.ApplySubModEnables();
    }

    public bool OnDisable()
    {
        try
        {
            _lifecycleHarmony?.UnpatchSelf();
            _lifecycleHarmony = null;
            _dashFall?.OnDisable();
            _tweaks?.OnDisable();
            _companion?.OnDisable();
            _instance = null;
            CompetitiveAdjustments.ConfigManager.Log("Disabled.");
            return true;
        }
        catch (Exception ex)
        {
            CompetitiveAdjustments.ConfigManager.LogError("Error in OnDisable: " + ex);
            return false;
        }
    }

    private void EnsureServerRuntimeComponents()
    {
        try
        {
            CompetitiveAdjustments.ConfigManager.EnsureConfig();
            CompetitiveAdjustments.ConfigManager.ReloadConfig();
            // ReloadConfig fires NotifyConfigReloaded -> ApplySubModEnables,
            // which now creates _tweaks only when EnableCompTweaks is true
            // and the process is server-side (covers the host case via
            // NetworkManager.IsServer).  Call once more explicitly so this
            // path stays correct even if the ConfigManager hook is bypassed.
            ApplySubModEnables();
        }
        catch (Exception ex)
        {
            CompetitiveAdjustments.ConfigManager.LogError("Failed to enable server runtime components: " + ex);
        }
    }

    [HarmonyPatch(typeof(Unity.Netcode.NetworkManager), "StartHost")]
    private static class StartHostPatch
    {
        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (!__result) return;
            _instance?.EnsureServerRuntimeComponents();
        }
    }

    [HarmonyPatch(typeof(Unity.Netcode.NetworkManager), "StartServer")]
    private static class StartServerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (!__result) return;
            _instance?.EnsureServerRuntimeComponents();
        }
    }
}

/// <summary>
/// Shared helper – apply Harmony patches only for types whose namespace
/// starts with one of the supplied prefixes (avoids cross-mod patch bleed).
/// </summary>
internal static class HarmonyPatchHelper
{
    public static void PatchNamespaces(Harmony harmony, params string[] nsPrefixes)
    {
        // GetTypes() was outside the try. A single unloadable type throws
        // ReflectionTypeLoadException here and silently disables EVERY patch in the
        // namespace, with the caller still logging "Harmony patching complete".
        // Take whatever loaded and carry on.
        Type[] types;
        try
        {
            types = Assembly.GetExecutingAssembly().GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = Array.FindAll(ex.Types, t => t != null);
            UnityEngine.Debug.LogWarning(
                $"[COMPADJUST] {ex.Types.Length - types.Length} type(s) failed to load; "
                + "patching only what resolved. First loader error: "
                + (ex.LoaderExceptions != null && ex.LoaderExceptions.Length > 0
                    ? ex.LoaderExceptions[0].Message : "(none reported)"));
        }

        int patched = 0, failed = 0;
        foreach (var type in types)
        {
            if (type.Namespace == null) continue;
            foreach (var prefix in nsPrefixes)
            {
                if (type.Namespace == prefix || type.Namespace.StartsWith(prefix + "."))
                {
                    try
                    {
                        var applied = harmony.CreateClassProcessor(type).Patch();
                        if (applied != null && applied.Count > 0) patched++;
                    }
                    catch (Exception ex)
                    {
                        // This used to be a bare `catch { }` justified as "skip types with no
                        // patches". It also swallowed every patch whose target no longer
                        // resolves on the running build, which is precisely the failure a
                        // game update produces, and it left no trace anywhere in the log.
                        failed++;
                        UnityEngine.Debug.LogWarning(
                            $"[COMPADJUST] Harmony patch FAILED for {type.FullName}: {ex.Message}");
                    }
                    break;
                }
            }
        }

        if (failed > 0)
            UnityEngine.Debug.LogWarning(
                $"[COMPADJUST] Harmony namespace patch summary for [{string.Join(", ", nsPrefixes)}]: "
                + $"{patched} type(s) patched, {failed} FAILED. A failed patch means that feature "
                + "is silently absent on this game build.");
        else
            CompetitiveAdjustments.ConfigManager.Log(
                $"Harmony patched {patched} type(s) for [{string.Join(", ", nsPrefixes)}], 0 failures.");
    }
}
