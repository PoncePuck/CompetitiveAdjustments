// File: DashMod.cs
// Handles all dash-related functionality including speed limiting

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using DashfallCfg = CompetitiveAdjustments.DashfallConfig;

namespace DashFallMod
{
    public static class DashMod
    {
        // Tracking dictionaries
        internal static readonly Dictionary<ulong, float> LastStandingDashAt = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> LastJumpAt = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> LastDashAt = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> LastSlideEndAt = new Dictionary<ulong, float>();
        
        // Track if player was sliding last frame (to detect slide end)
        internal static readonly Dictionary<ulong, bool> WasSlidingLastFrame = new Dictionary<ulong, bool>();
        
        // Dash path marking (don't swallow dashes coming from bridge)
        [ThreadStatic] internal static bool DashFromBridge;
        
        // Flag to indicate this is a goalie standing dash (not sliding dash)
        [ThreadStatic] internal static bool IsGoalieStandingDash;
        
        // Reflection fields
        private static readonly FieldInfo F_CanDash = AccessTools.Field(typeof(PlayerBodyV2), "canDash");
        private static readonly FieldInfo F_JumpVelocity = AccessTools.Field(typeof(PlayerBodyV2), "jumpVelocity");
        private static readonly FieldInfo F_JumpStaminaDrain = AccessTools.Field(typeof(PlayerBodyV2), "jumpStaminaDrain");
        
        private static DashfallCfg Config => ConfigManager.Config;
        
        private static bool IsFaceOff() => GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.FaceOff;
        
        public static void EnableDash(PlayerBodyV2 body)
        {
            F_CanDash?.SetValue(body, true);
        }
        
        public static void ProcessGoalieStandingDash(PlayerBodyV2 body, bool left)
        {
            DashFromBridge = true;
            IsGoalieStandingDash = true;
            try
            {
                if (left)
                    body.DashLeft();
                else
                    body.DashRight();
            }
            finally
            {
                DashFromBridge = false;
                IsGoalieStandingDash = false;
            }
        }
    }
    
    
    // ===== Dash Prefix + Postfix - Main dash logic =====
    // Merged into one class so __state can pass "vanilla actually ran" from
    // prefix to postfix; the postfix only stamps LastDashAt when the dash was
    // not short-circuited here.
    [HarmonyPatch]
    public static class Dash_Prefix_Patch
    {
        private static DashfallCfg Config => ConfigManager.Config;
        private static bool IsFaceOff() => GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.FaceOff;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerBodyV2), nameof(PlayerBodyV2.DashLeft));
            yield return AccessTools.Method(typeof(PlayerBodyV2), nameof(PlayerBodyV2.DashRight));
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance, bool __state)
        {
            if (!__state) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            var p = __instance.Player; if (p == null) return;
            DashMod.LastDashAt[p.NetworkObjectId] = Time.time;
        }

        [HarmonyPrefix]
        public static bool Prefix(PlayerBodyV2 __instance, MethodBase __originalMethod, out bool __state)
        {
            // __state = true => vanilla ran => postfix should stamp LastDashAt.
            __state = false;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) { __state = true; return true; }
            var player = __instance.Player; if (player == null) return false;

            bool left = __originalMethod.Name.Contains("Left");

            // --- role/state ---
            var role = player.Role;
            bool isAttacker = role == PlayerRole.Attacker;
            bool isGoalie = role == PlayerRole.Goalie;
            
            // SKATER DASH COMPLETELY DISABLED - only goalies can dash in this mod
            if (!isGoalie) return false;
            
            bool isSliding = __instance.IsSliding.Value;
            
            // NOTE: Goalie standing dash and vanilla dash are SEPARATE actions:
            // - Vanilla dash = Q/E while sliding (game's built-in dash)
            // - Standing dash = custom keybind while standing still (our mod feature)
            
            // If this is a goalie standing dash, it should only work when NOT sliding
            if (isGoalie && DashMod.IsGoalieStandingDash)
            {
                if (isSliding)
                {
                    return false; // Standing dash doesn't work while sliding
                }
                // Not sliding - continue to standing dash logic below
            }
            // If this is vanilla goalie dash, it should only work while sliding
            else if (isGoalie && !DashMod.DashFromBridge)
            {
                // This is vanilla goalie dash - only allow when sliding
                if (!isSliding)
                {
                    return false; // Vanilla dash only works while sliding for goalies
                }
                __state = true;
                return true; // Let vanilla handle the sliding dash
            }
            
            bool airborne = !__instance.IsGrounded;
            
            // global early-outs
            if (__instance.HasFallen.Value) return false;
            
            // Note: Goalie sliding dash is handled above (returns true for vanilla handling)
            // If we get here as goalie, we're doing standing dash (not sliding)
            
            if (isGoalie && DashMod.LastJumpAt.TryGetValue(player.NetworkObjectId, out var jt) && 
                (Time.time - jt) < 0.5f) return false;
            
            // --- cooldown/cost precompute ---
            
            // base cooldown (goalie only)
            float cd = Config.GoalieBaseStandingDashCooldown;
            
            // cooldown gate
            var now = Time.time;
            if (DashMod.LastStandingDashAt.TryGetValue(player.NetworkObjectId, out var last) && (now - last) < cd) return false;
            DashMod.LastStandingDashAt[player.NetworkObjectId] = now;
            
            // base cost (goalie uses hardcoded values)
            float baseCost = AccessTools.Field(typeof(PlayerBodyV2), "dashStaminaDrain") is FieldInfo f ? (float)f.GetValue(__instance) : 0.3f;
            float cost = baseCost;
            if (!airborne) cost *= 0.5f;
            if (Config.EnableGoalieScaling && isGoalie) cost *= Config.GoalieDashStaminaScale;
            
            // stamina gates
            if (__instance.Stamina.Value < 0.25f) return false;
            if (__instance.Stamina.Value < cost) return false;
            
            // --- force build ---
            Vector3 dir = __instance.transform.right * (left ? -1 : 1);
            float dashForce = 5f;
            if (Config.EnableGoalieScaling && isGoalie) dashForce *= Config.GoalieDashForceScale;
            
            // Goalies, grounded & not sliding: custom path
            if (!airborne && isGoalie && !isSliding)
            {
                __instance.Rigidbody.AddForce(dir * dashForce, ForceMode.VelocityChange);
                if (!IsFaceOff()) __instance.Stamina.Value = Mathf.Max(0f, __instance.Stamina.Value - cost);
                DashMod.LastDashAt[player.NetworkObjectId] = Time.time;
                return false; // skip vanilla
            }
            
            // Otherwise (air dashes, goalie while sliding, etc.), let vanilla run
            __state = true;
            return true;
        }
    }
}
