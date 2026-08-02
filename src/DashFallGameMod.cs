// File: DashFallGameMod.cs
// Main entry point for the DashFall mod
// Orchestrates all sub-modules: Dash, Dive, Twist, SlideInfluence

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using PoncePuck.Keybinds;
using DashFallMod;

public sealed class DashFallGameMod
{
    private Harmony _harmony;
    
    // Config is accessed directly via DashFallMod.ConfigManager.Config
    
    public DashFallGameMod() { }
    
    public bool OnEnable()
    {
        try
        {
            bool isHeadless = Application.isBatchMode ||
                              SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            bool isServerRuntime = Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer;

            // Only server/headless should create/read CompetitiveAdjustments.json.
            if (isHeadless || isServerRuntime)
            {
                ConfigManager.EnsureConfig();
                ConfigManager.ReloadConfig();
            }
            
            ServerBridge.Hook("DashFall");
            ServerBridge.OnAction += OnClientAction;
            
            _harmony = new Harmony("net.poncepuck.dashfall");
            _harmony.PatchAll(typeof(DashFallGameMod));
            HarmonyPatchHelper.PatchNamespaces(_harmony, "DashFallMod", "PoncePuck.Keybinds", "PonceMods.Shared");
            
            // b1117 renamed the spawn broadcast to Event_Everyone_OnPlayerBodySpawned,
            // and role changes are now delivered through the combined
            // Event_Everyone_OnPlayerGameStateChanged (phase/team/role) event.
            EventManager.AddEventListener("Event_Everyone_OnPlayerBodySpawned", OnBodySpawned);
            EventManager.AddEventListener("Event_Everyone_OnPlayerGameStateChanged", OnRoleChanged);

            // Arena state belongs to the LEVEL, not to the players standing on it. This
            // used to come online only when the first PlayerBodyV2 spawned, which left a
            // dedicated server with no arena tick and no level-spawn hook until then, so
            // the rink sat at vanilla size between a level load and the first spawn.
            GoalNetTweaks.EnsureRunner();

            // Initialize client immediately if not headless
            
            if (!isHeadless)
            {
                InitializeClientComponents();
            }
            
            ConfigManager.Dbg("Enabled - Modules: Dash, Dive, Twist, SlideInfluence");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[COMPADJUST] FATAL ERROR in OnEnable: {ex}");
            return false;
        }
    }
    
    private static void InitializeClientComponents()
    {
        try
        {
            var go = new GameObject("DashFallClientRunner");
            go.AddComponent<DashFallMod.Client.DashFallClientRunner>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[COMPADJUST] Failed to initialize client: {ex}");
        }
    }
    
    public bool OnDisable()
    {
        ServerBridge.OnAction -= OnClientAction;
        ServerBridge.Unhook();
        _harmony?.UnpatchSelf();

        EventManager.RemoveEventListener("Event_Everyone_OnPlayerBodySpawned", OnBodySpawned);
        EventManager.RemoveEventListener("Event_Everyone_OnPlayerGameStateChanged", OnRoleChanged);

        // Tear down CMM handlers our subsystems registered via the per-frame
        // EnsureCMMRegistered polls. Without this, a plugin disable/re-enable
        // cycle leaves the old handlers attached against the previous CMM.
        try { GoalieDashExtend.Disable(); } catch (Exception e) { ConfigManager.Dbg("GoalieDashExtend.Disable failed: " + e.Message); }
        try { Stances.Disable(); } catch (Exception e) { ConfigManager.Dbg("Stances.Disable failed: " + e.Message); }

        // Unregister from ModMenuHub
        try
        {
            PonceMods.Shared.ModMenuHub.UnregisterMod("DashFall");
            ConfigManager.Dbg("Unregistered from ModMenuHub");
        }
        catch (System.Exception e) { ConfigManager.Dbg("ModMenuHub.UnregisterMod failed: " + e.Message); }

        // Destroy the client runner
        var clientRunner = UnityEngine.Object.FindFirstObjectByType<DashFallMod.Client.DashFallClientRunner>();
        if (clientRunner != null)
        {
            UnityEngine.Object.Destroy(clientRunner.gameObject);
        }

        // Pair for the EnsureRunner() in OnEnable. The arena runner is
        // DontDestroyOnLoad and owns the level-spawn hook and the chunked position
        // patches, so leaving it running meant a disabled mod kept resizing the rink
        // and kept a non-vanilla wire encoding installed on a server.
        try { GoalNetTweaks.ShutdownRunner(); } catch (Exception e) { ConfigManager.Dbg("GoalNetTweaks.ShutdownRunner failed: " + e.Message); }

        ConfigManager.Dbg("Disabled");
        return true;
    }
    
    private void OnBodySpawned(Dictionary<string, object> msg)
    {
        var body = msg?["playerBody"] as PlayerBodyV2;
        if (body != null) DashMod.EnableDash(body);
    }
    
    private void OnRoleChanged(Dictionary<string, object> msg)
    {
        if (msg == null) return;
        var player = msg["player"] as Player;
        if (player?.PlayerBody == null) return;

        // Event_Everyone_OnPlayerGameStateChanged fires on any game-state change
        // (phase / team / role). Preserve the old Event_OnPlayerRoleChanged
        // semantics by acting only when the role actually changed.
        if (msg.TryGetValue("oldGameState", out var oldObj) && msg.TryGetValue("newGameState", out var newObj)
            && oldObj is PlayerGameState oldState && newObj is PlayerGameState newState
            && oldState.Role == newState.Role)
            return;

        DashMod.EnableDash(player.PlayerBody);

        // A goalie that switches to a skater stops being ticked by the goalie leg
        // systems (UpdateStances / UpdateVelocityExtend both bail on non-goalies)
        // before they can broadcast their cleared state, which can strand a stale
        // stance or extended pose on remote clients. Release explicitly on the
        // transition away from goalie. Fires on server (notifies clients) and on
        // clients (clears local state); a body still goalie is left untouched.
        if (player.Role != PlayerRole.Goalie)
        {
            try { Stances.ReleaseStance(player.PlayerBody); } catch { }
            try { GoalieDashExtend.ReleaseBody(player.PlayerBody); } catch { }
        }
    }
    
    // ===== Bridge actions =====
    private static void OnClientAction(ulong clientId, string action, bool isDown)
    {
        if (!isDown) return; // edge trigger on down
        var p = FindPlayerByClientId(clientId);
        if (p == null || p.PlayerBody == null) return;
        
        var cfg = ConfigManager.Config;
        bool isGoalie = p.Role == PlayerRole.Goalie;
        
        if (action == "dashleft" || action == "dashright")
        {
            // Skater dash disabled — goalies use base game dash
        }
        else if (action == "goaliestandingdashleft" || action == "goaliestandingdashright")
        {
            // Goalie standing dash from custom keybinds - check if feature is enabled
            if (isGoalie && cfg.GoalieStandingDashEnabled)
            {
                DashMod.ProcessGoalieStandingDash(p.PlayerBody, action == "goaliestandingdashleft");
            }
        }
        else if (action == "dive")
        {
            bool allowed = isGoalie ? cfg.GoalieDiveEnabled : cfg.SkaterDiveEnabled;
            if (allowed)
            {
                DiveMod.TryStartDiveNow(p.PlayerInput);
            }
        }
        else if (action == "twistleft" || action == "twistright")
        {
            bool allowed = isGoalie ? cfg.GoalieTwistWhileSlidingEnabled : cfg.EnableTwistWhileSliding;
            if (allowed)
            {
                TwistMod.ProcessTwistFromBridge(p.PlayerBody, action == "twistleft");
            }
        }
    }
    
    private static Player FindPlayerByClientId(ulong clientId)
    {
        foreach (var pi in UnityEngine.Object.FindObjectsByType<PlayerInput>(UnityEngine.FindObjectsSortMode.None))
            if (pi != null && pi.Player != null && pi.Player.OwnerClientId == clientId) return pi.Player;
        return null;
    }
}
// ServerBridge is now in DashFall.ServerBridge.cs
