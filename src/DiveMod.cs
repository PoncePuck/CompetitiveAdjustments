// File: DiveMod.cs
// Handles all dive-related functionality including legacy double-extend detection

using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using DashfallCfg = CompetitiveAdjustments.DashfallConfig;

namespace DashFallMod
{
    public static class DiveMod
    {
        // Dive tracking
        internal static readonly Dictionary<ulong, float> DiveStartedAt = new Dictionary<ulong, float>();
        internal static readonly HashSet<ulong> DiveToggled = new HashSet<ulong>();
        
        // Legacy double-extend detection (only when no dive bind)
        internal static readonly Dictionary<ulong, float> LeftAt = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> RightAt = new Dictionary<ulong, float>();
        internal const float LegacyDoubleExtendWindow = 0.25f;
        
        private static DashfallCfg Config => ConfigManager.Config;
        
        private static bool IsFaceOff() => GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.FaceOff;
        
        public static void TryStartDiveNow(PlayerInput input)
        {
            var player = input?.Player; if (player == null) return;
            var body = player.PlayerBody; if (body == null) return;
            if (!body.IsGrounded || body.HasFallen || IsFaceOff()) return;
            
            var id = player.NetworkObjectId;
            if (DiveToggled.Contains(id)) return;
            
            if (player.Role == PlayerRole.Attacker && body.Stamina.Value < Config.MinStaminaForDive_Skater) return;
            
            DiveToggled.Add(id);
            DiveStartedAt[id] = Time.time;
            if (!IsFaceOff()) body.Stamina.Value = 0f;
            
            var move = input.MoveInput.ServerValue;
            Vector3 dir = (Mathf.Abs(move.y) > Config.MinAxis ? body.transform.forward * Mathf.Sign(move.y) : Vector3.zero)
                        + (Mathf.Abs(move.x) > Config.MinAxis ? body.transform.right * Mathf.Sign(move.x) : Vector3.zero);
            if (dir == Vector3.zero) dir = body.transform.forward;
            dir.Normalize();
            
            body.Rigidbody.AddForce(dir * Config.DiveVelocity, ForceMode.VelocityChange);
            body.Rigidbody.AddTorque(Vector3.Cross(Vector3.up, dir).normalized * Config.DiveTorque, ForceMode.VelocityChange);
            
            EventManager.TriggerEvent("Event_OnDive",
                new Dictionary<string, object> { { "player", player }, { "duration", int.MaxValue.ToString() } });
            EventManager.TriggerEvent("oomtm450_ruleset",
                new Dictionary<string, object> { { "dive", player.SteamId.Value.ToString() }, { "duration", int.MaxValue.ToString() } });
            
            // reset legacy timestamps so it won't instantly retrigger
            LeftAt[id] = RightAt[id] = -999f;
        }
        
        public static void ClearDiveState(Player player)
        {
            if (player == null) return;
            var id = player.NetworkObjectId;
            
            if (DiveToggled.Remove(id) | DiveStartedAt.Remove(id))
            {
                EventManager.TriggerEvent("Event_OnDive",
                    new Dictionary<string, object> { { "player", player }, { "duration", int.MinValue.ToString() } });
                EventManager.TriggerEvent("oomtm450_ruleset",
                    new Dictionary<string, object> { { "dive", player.SteamId.Value.ToString() }, { "duration", int.MinValue.ToString() } });
            }
        }
        
        /// <summary>Check if a player is currently in dive state (for HUD)</summary>
        public static bool IsPlayerDiving(ulong networkObjectId)
        {
            return DiveToggled.Contains(networkObjectId);
        }
    }
    
    // ===== Legacy dive detection REMOVED - client mod always sends dive action =====
    // Double-extend (Q+E) no longer triggers dive - must use dedicated dive keybind
    
    // ===== Auto-clear if no fall after a short window (lets you dive again) =====
    // ===== Also applies dive drag when fallen =====
    [HarmonyPatch(typeof(PlayerBodyV2), "FixedUpdate")]
    public static class Body_DiveAutoClear_Patch
    {
        private static DashfallCfg Config => ConfigManager.Config;
        
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            var player = __instance.Player; if (player == null) return;
            var id = player.NetworkObjectId;
            
            if (!DiveMod.DiveToggled.Contains(id)) return;
            if (!DiveMod.DiveStartedAt.TryGetValue(id, out var t0)) return;
            
            // === DIVE FALLEN DRAG ===
            // Apply extra drag when player has fallen after diving
            if (Config.EnableDiveFallenDrag && __instance.HasFallen)
            {
                __instance.Movement.AmbientDrag = Config.DiveFallenDragAmount;
            }
            
            float elapsed = Time.time - t0;
            if (elapsed < Config.DiveAutoClearIfNoFallSeconds) return;
            
            // never fell → clear toggle so dive can be used again
            if (__instance.IsGrounded && !__instance.HasFallen)
            {
                DiveMod.DiveToggled.Remove(id);
                DiveMod.DiveStartedAt.Remove(id);
                
                EventManager.TriggerEvent("Event_OnDive",
                    new Dictionary<string, object> { { "player", player }, { "duration", int.MinValue.ToString() } });
                EventManager.TriggerEvent("oomtm450_ruleset",
                    new Dictionary<string, object> { { "dive", player.SteamId.Value.ToString() }, { "duration", int.MinValue.ToString() } });
            }
        }
    }
    
    // ===== Clear toggle when we stand up (fall -> stand) =====
    [HarmonyPatch(typeof(PlayerBodyV2), nameof(PlayerBodyV2.OnStandUp))]
    public static class OnStandUp_Postfix_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            var p = __instance.Player; if (p == null) return;
            DiveMod.ClearDiveState(p);
        }
    }

    // ===== Clean up per-player dictionaries when a player disconnects =====
    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkDespawn")]
    public static class PlayerBody_OnNetworkDespawn_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance)
        {
            var player = __instance.Player;
            if (player == null) return;
            var id = player.NetworkObjectId;

            // Dive state
            DiveMod.DiveToggled.Remove(id);
            DiveMod.DiveStartedAt.Remove(id);
            DiveMod.LeftAt.Remove(id);
            DiveMod.RightAt.Remove(id);

            // Twist cooldown
            TwistMod.CleanupPlayer(id);

            // Dash tracking
            DashMod.WasSlidingLastFrame.Remove(id);
            DashMod.LastSlideEndAt.Remove(id);
            DashMod.LastStandingDashAt.Remove(id);
            DashMod.LastJumpAt.Remove(id);
            DashMod.LastDashAt.Remove(id);

            // Goalie stance + velocity-extend per-body and broadcast bookkeeping.
            // These are keyed by the body's InstanceID / the body's NetworkObjectId,
            // not the player's, so they are not covered by the player-id removals above.
            try { Stances.OnBodyDespawned(__instance); } catch { }
            try { GoalieDashExtend.OnBodyDespawned(__instance); } catch { }
        }
    }
}
