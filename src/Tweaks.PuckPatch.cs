using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;

namespace CompetitivePuckTweaks.src
{
    [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
    public class PuckPatch
    {
        // Throttle for the "heal missed sync" fan-out below. Multiple puck
        // spawns in quick succession (warmup, multi-puck modes) used to send
        // a ManualSync per puck per client; once per second is plenty since
        // clients already pull sync via CMM_SYNC_REQUEST on their own.
        private static float _nextServerFanoutAt;
        private const float ServerFanoutCooldown = 1f;

        [HarmonyPostfix]
        public static void Postfix(Puck __instance)
        {
            // Apply the full tuned puck physics. Extracted into ApplyPuckPhysics
            // so the PuckManager phase-spawn postfix can re-assert it when a game
            // starts; recycled pucks otherwise revert to vanilla mass/bounciness
            // and drop out of PuckIDs (feels right in a fresh warmup, breaks once
            // a game is played).
            ApplyPuckPhysics(__instance);

            // Timing guard: send latest config sync when a puck spawns on server.
            // This heals missed join-time sync and keeps client puck scale consistent.
            // Throttled to once per ServerFanoutCooldown to avoid an N pucks x M
            // clients burst on multi-puck spawns (warmup, etc.).
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                float now = Time.unscaledTime;
                if (now >= _nextServerFanoutAt)
                {
                    _nextServerFanoutAt = now + ServerFanoutCooldown;
                    foreach (ulong clientId in nm.ConnectedClientsIds)
                    {
                        if (clientId == NetworkManager.ServerClientId) continue;
                        PluginCore.ManualSync(clientId);
                    }
                }
            }
            else if (nm != null && nm.IsClient)
            {
                CompetitiveCompanion.PluginCore.RequestConfigSyncFromServer("PuckSpawn");
            }
        }

        /// <summary>
        /// Apply every tuned puck property (scale, max speed, stick tensor, drag,
        /// mass, contact-modification flags and PuckIDs registration). Idempotent,
        /// so it is safe to call both at spawn (OnNetworkPostSpawn) and again on
        /// every phase change via PuckManager.Server_SpawnPucksForPhase. The
        /// private maxSpeed / stickTensor fields are written through Traverse so
        /// this can run outside the Harmony patch's ref-parameter context.
        /// </summary>
        public static void ApplyPuckPhysics(Puck puck)
        {
            if (puck == null) return;

            float puckScale = GetSyncedPuckScale();
            puck.transform.localScale = new UnityEngine.Vector3(puckScale, puckScale, puckScale);
            PluginCore.Dbg($"Puck scaled to {puckScale}");

            Traverse.Create(puck).Field("maxSpeed").SetValue(PluginCore.config.PuckMaxSpeed);
            Traverse.Create(puck).Field("stickTensor").SetValue(new UnityEngine.Vector3(
                PluginCore.config.PuckStickTensorX, PluginCore.config.PuckStickTensorY, PluginCore.config.PuckStickTensorZ));

            puck.Rigidbody.linearDamping = PluginCore.config.PuckDrag;
            puck.Rigidbody.mass = PluginCore.config.PuckMass;
            puck.StickCollider.hasModifiableContacts = true;
            puck.IceCollider.hasModifiableContacts = true;

            int stickColId = puck.StickCollider.GetInstanceID();
            int iceColId = puck.IceCollider.GetInstanceID();
            if (!PluginCore.PuckIDs.Contains(stickColId)) PluginCore.PuckIDs.Add(stickColId);
            if (!PluginCore.PuckIDs.Contains(iceColId)) PluginCore.PuckIDs.Add(iceColId);

            if (CompetitiveAdjustments.BallModeHelper.IsBallModeEnabled)
                CompetitiveAdjustments.BallModeHelper.TransformPuckToBall(puck);

            if (PluginCore.config.EnableMidStickCollider)
            {
                foreach (Stick stick in UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsSortMode.None))
                {
                    Physics.IgnoreCollision(puck.StickCollider, stick.GetComponent<BoxCollider>());
                    Physics.IgnoreCollision(puck.IceCollider, stick.GetComponent<BoxCollider>());
                }
            }
        }

        /// <summary>
        /// Get puck scale from synced client config (CompetitiveCompanion.PluginCore),
        /// or fall back to server config if client config is not available.
        /// This ensures clients display the correct puck size synced from the server.
        /// </summary>
        private static float GetSyncedPuckScale()
        {
            try
            {
                // Try to get from synced client config, which receives updates from server via CMM
                var companionConfig = CompetitiveCompanion.PluginCore.config;
                if (companionConfig != null && companionConfig.PuckScale > 0.01f)
                    return companionConfig.PuckScale;
            }
            catch { }
            
            // Fall back to server config if client config is not available
            return PluginCore.config.PuckScale;
        }
    }


    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public class HeightDragTweak
    {
        [HarmonyPostfix]
        public static void Postfix(Puck __instance)
        {
            if (!PluginCore.config.PuckDragSpeedDependence) return;
            float delta = __instance.Rigidbody.linearVelocity.magnitude - PluginCore.config.PuckNominalSpeed;
            float newDrag = PluginCore.config.PuckDrag * (1 + PluginCore.config.PuckDragFactor * delta * delta * delta);
            __instance.Rigidbody.linearDamping = Mathf.Max(PluginCore.config.PuckDrag, newDrag);

            if (PluginCore.config.PuckHeightDependentDrag)
            {
                if (__instance.Rigidbody.position.y > PluginCore.config.PuckHeightLimit && __instance.Rigidbody.linearVelocity.y > 0)
                {
                    float overheight = __instance.Rigidbody.position.y - PluginCore.config.PuckHeightLimit;
                    float heightDrag = PluginCore.config.PuckHeightDragFactor * overheight;
                    __instance.Rigidbody.AddForce(new UnityEngine.Vector3(0f, -heightDrag, 0f), ForceMode.VelocityChange);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkDespawn")]
    public class PuckDespawnPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Puck __instance)
        {
            if (PluginCore.PuckIDs.Contains(__instance.StickCollider.GetInstanceID())) { PluginCore.PuckIDs.Remove(__instance.StickCollider.GetInstanceID()); }
            if (PluginCore.PuckIDs.Contains(__instance.IceCollider.GetInstanceID())) { PluginCore.PuckIDs.Remove(__instance.IceCollider.GetInstanceID()); }
            CompetitiveAdjustments.BallModeHelper.OnPuckDespawned(__instance);
        }
    }
}