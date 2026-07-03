using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.Netcode;
using HarmonyLib;

namespace CompetitivePuckTweaks.src
{

    [HarmonyPatch(typeof(StickPositioner), "Awake")]
    public class StickPositionerAwakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(StickPositioner __instance, ref float ___softCollisionForce, ref Vector3 ___bladeTargetFocusPointInitialLocalPosition, ref float ___outputMin, ref float ___outputMax, ref float ___proportionalGain, ref float ___integralGain, ref float ___derivativeGain, ref LayerMask ___raycastLayerMask)
        {
            ___softCollisionForce = PluginCore.config.SoftCollisionForce;
            ___bladeTargetFocusPointInitialLocalPosition += new Vector3(0, PluginCore.config.BladeTargetFocusPointOffsetY, 0);

            // Ensure the Barrier layer is included in the stick raycast mask so custom
            // arena boards block and push the stick exactly like the game's own boards.
            // (b1117 renamed the "Boards" collision layer to "Barrier".)
            int barrierLayer = LayerMask.NameToLayer("Barrier");
            if (barrierLayer >= 0)
                ___raycastLayerMask |= 1 << barrierLayer;

            if (PluginCore.config.EnableStickSpeedDecay) __instance.gameObject.AddComponent<FloatComponent>();
        }
    }

    [HarmonyPatch(typeof(StickPositioner), "FixedUpdate")]
    public class StickFixedUpdatePatch
    {
        // Per-instance saved reach so we restore to the game's actual default, not a hardcoded constant.
        private static readonly Dictionary<int, float> _savedReach = new Dictionary<int, float>();

        private static bool IsGoalieInButterflyOrSliding(StickPositioner instance)
        {
            var body = instance.PlayerBody;
            if (body == null) return false;

            if (body.IsSliding.Value) return true;

            var mesh = body.PlayerMesh;
            if (mesh == null) return false;

            foreach (var pad in mesh.GetComponentsInChildren<PlayerLegPad>())
            {
                if (pad == null) continue;
                if (pad.State == PlayerLegPadState.Butterfly || pad.State == PlayerLegPadState.ButterflyExtended)
                    return true;
            }

            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(StickPositioner __instance, ref float ___maximumReach)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (__instance.Player == null) return;

            var dfCfg = DashFallMod.ConfigManager.Config;
            bool reachReductionEnabled = dfCfg.GoalieSlidingReachReduction;
            bool isGoalie = __instance.Player.Role == PlayerRole.Goalie;
            int id = __instance.GetInstanceID();

            if (isGoalie && reachReductionEnabled && IsGoalieInButterflyOrSliding(__instance))
            {
                // Save original reach on first butterfly entry for this instance
                if (!_savedReach.ContainsKey(id))
                    _savedReach[id] = ___maximumReach;

                float reduced = _savedReach[id] * Mathf.Clamp(dfCfg.GoalieSlidingReachScale, 0.1f, 1f);
                ___maximumReach = reduced;
            }
            else if (_savedReach.TryGetValue(id, out float orig))
            {
                // Goalie left butterfly or reduction disabled — restore original reach
                ___maximumReach = orig;
                _savedReach.Remove(id);
            }
            // else: nothing to do — don't touch the game's field at all
        }

        [HarmonyPostfix]
        public static void Postfix(StickPositioner __instance, ref float ___outputMin, ref float ___outputMax, ref float ___maximumReach)
        {
            // Note: reach is handled entirely in Prefix (Prefix runs before the ShootRaycast call).

            float defaultValue = __instance.Player.Role == PlayerRole.Goalie ? PluginCore.config.GoaliePositionerOutputMax : PluginCore.config.StickPositionerOutputMax;

            if (!PluginCore.config.AlterStickPositionerOutput) return;
            else if (!PluginCore.config.EnableStickSpeedDecay)
            {
                ___outputMin = -defaultValue;
                ___outputMax = defaultValue;
                return;
            }

            // FloatComponent is added in the Awake patch only when EnableStickSpeedDecay
            // was true at spawn. If the config is flipped on after a stick has already
            // spawned (e.g. via /reload) the component is missing on that stick. Lazily
            // attach so the speed-decay path doesn't NRE on those sticks.
            FloatComponent runningAvg = __instance.gameObject.GetComponent<FloatComponent>()
                ?? __instance.gameObject.AddComponent<FloatComponent>();

            runningAvg.value += ((__instance.Stick.Rigidbody.GetPointVelocity(__instance.Stick.BladeHandlePosition) - __instance.PlayerBody.Rigidbody.linearVelocity).magnitude - runningAvg.value) / PluginCore.config.StickSpeedDecaySpan;

            if (runningAvg.value > PluginCore.config.StickSpeedDecayLimit && ___outputMax > PluginCore.config.StickSpeedDecayMin)
            {
                ___outputMin += PluginCore.config.StickSpeedDecayRate * (runningAvg.value - PluginCore.config.StickSpeedDecayLimit);
                ___outputMax = -___outputMin;
            }
            else if (___outputMax < defaultValue)
            {
                ___outputMin = Mathf.Min(___outputMin - 20f, defaultValue);
                ___outputMax = -___outputMin;
            }
        }
    }

    // John's board bounce adjustments patch

    // Harmony patch to reduce linear acceleration from StickPositioner.ApplySoftCollision
    // while preserving the torque generated by AddForceAtPosition. This uses the
    // "cancel at COM" method: after the original AddForceAtPosition runs we apply
    // an opposite force at the Rigidbody COM equal to a fraction of the contact
    // force, leaving the torque unchanged.

    [HarmonyPatch(typeof(StickPositioner), "ApplySoftCollision")]
    static class BoardBounceAdjustments_Patch
    {
        // Enable debug logging for adjustments (may be chatty)
        public static bool DebugLogging = false;
        
        [HarmonyPostfix]
        public static void Postfix(StickPositioner __instance, RaycastHit hit, Vector3 hitPosition)
        {
            try
            {
                if (__instance == null) return;
                // Only run on server (physics authority)
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

                // Get PlayerBody Rigidbody
                var playerBody = __instance.PlayerBody;
                if (playerBody == null) return;
                var rb = playerBody.Rigidbody;
                if (rb == null) return;

                // Read configurable reduction fraction from live config
                float reduction = Mathf.Clamp(PluginCore.config.JohnBoardBounceLinearReduction, 0f, 1f);
                if (reduction <= 0f) return; // nothing to do

                // Recompute the contact force used in ApplySoftCollision to match the original
                // F = hit.normal * d * (softCollisionForce * num)
                float maximumReach = 0f;
                GameObject raycastOrigin = null;
                float softCollisionForce = PluginCore.config.JohnBoardBounceDefaultForce;
                try
                {
                    var t = __instance.GetType();

                    var fi = t.GetField("maximumReach", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fi != null) maximumReach = (float)fi.GetValue(__instance);
                    else
                    {
                        var pi = t.GetProperty("maximumReach", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (pi != null) maximumReach = (float)pi.GetValue(__instance);
                    }

                    var fi2 = t.GetField("raycastOrigin", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fi2 != null) raycastOrigin = (GameObject)fi2.GetValue(__instance);
                    else
                    {
                        var pi2 = t.GetProperty("raycastOrigin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (pi2 != null) raycastOrigin = (GameObject)pi2.GetValue(__instance);
                    }

                    var fi3 = t.GetField("softCollisionForce", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fi3 != null) softCollisionForce = (float)fi3.GetValue(__instance);
                    else
                    {
                        var pi3 = t.GetProperty("softCollisionForce", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (pi3 != null) softCollisionForce = (float)pi3.GetValue(__instance);
                    }
                }
                catch { }

                // If maximumReach wasn't found via reflection, fall back to an estimate using hit.distance
                float d = maximumReach - hit.distance;
                // If raycastOrigin is present, use it to compute the original magnitude factor; otherwise estimate
                float magnitude = 0f;
                try
                {
                    if (raycastOrigin != null)
                    {
                        magnitude = Vector3.Cross(hit.normal, raycastOrigin.transform.forward).magnitude;
                    }
                    else
                    {
                        // fallback: approximate using hit.normal and player's forward
                        var pl = __instance.transform;
                        magnitude = Vector3.Cross(hit.normal, pl.forward).magnitude;
                    }
                }
                catch { magnitude = 0f; }



                float num = 1f - magnitude;
                float effectiveSoft = softCollisionForce;

                // Build the exact force vector used by ApplySoftCollision
                Vector3 F = hit.normal * d * (effectiveSoft * num);

                // Compute cancelling force to apply at COM
                Vector3 cancel = -reduction * F;

                // Apply cancellation at COM using same ForceMode (Acceleration) used by ApplySoftCollision
                try
                {
                    rb.AddForce(cancel, ForceMode.Acceleration);

                    try { if (DebugLogging) Debug.Log($"[BoardBounceAdjustments] Cancelled {reduction * 100f}% of soft collision linear force: cancel={cancel}"); } catch { }
                }
                catch (Exception e)
                {
                    try { Debug.LogException(e); } catch { }
                }
            }
            catch (Exception e)
            {
                try { Debug.LogException(e); } catch { }
            }
        }
    }
}