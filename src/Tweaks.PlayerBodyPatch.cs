using HarmonyLib;
using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace CompetitivePuckTweaks.src
{
    internal static class TorsoDebugBrush
    {
        private const string BrushName = "__clipBrush";
        private static Material _mat;

        internal static void Sync(MeshCollider mc)
        {
            if (mc == null) return;

            bool enabled = DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowPlayerClipBrushes == true;

            var existing = mc.transform.Find(BrushName);
            if (!enabled)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            if (existing == null)
            {
                var go = new GameObject(BrushName);
                existing = go.transform;
                existing.SetParent(mc.transform, false);
                go.AddComponent<MeshFilter>();
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            existing.gameObject.SetActive(true);
            var mf = existing.GetComponent<MeshFilter>();
            var mr = existing.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;

            mr.enabled = true;
            mr.sharedMaterial = GetMat();
            mf.sharedMesh = mc.sharedMesh;
            existing.localPosition = Vector3.zero;
            existing.localRotation = Quaternion.identity;
            existing.localScale = Vector3.one;
        }

        private static Material GetMat()
        {
            if (_mat != null) return _mat;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            _mat = new Material(shader);
            _mat.color = new Color(1f, 0.5f, 0.1f, 0.3f); // orange tint to distinguish from arena brushes
            return _mat;
        }
    }

    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
    public class PlayerBodyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance, ref float ___slideTurnMultiplier,
         ref float ___stopDrag, ref float ___balanceRecoveryTime, ref PlayerMesh ___playerMesh, ref float ___slideDrag, ref float ___tackleForceMultiplier,
         ref float ___tackleForceThreshold, ref float ___tackleSpeedThreshold) {
            if (__instance.Player.IsReplay.Value) return;

            // Spawn positioning is server-authoritative -- positions replicate via the
            // SynchronizedObjectManager, not a NetworkTransform, so a client-side write
            // here would just be overwritten by the next sync. The body is despawned and
            // respawned at its PlayerPosition dot on EVERY spawn (warm-up/practice Play
            // spawn and every face-off), so this runs for all phases.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) {
                // Scale the spawn so players land at the proportional spot in a resized
                // rink instead of outside a shrunk one. The shared helper (also used for
                // warm-up pucks) scales world X/Z about the rink centre by the effective
                // arena scale with a small inset, and is a no-op when arena tweaks are off.
                __instance.transform.position = DashFallMod.GoalNetTweaks.ScaleSpawnPositionWithArena(__instance.transform.position);

                if (GameManager.Instance.Phase == GamePhase.FaceOff) {
                    if (__instance.Player.PlayerPosition.Name == "C") {
                        if (__instance.Player.Team == PlayerTeam.Blue) __instance.transform.position += new UnityEngine.Vector3(0, 0, PluginCore.config.CenterSpawnOffset);
                        else __instance.transform.position -= new UnityEngine.Vector3(0, 0, PluginCore.config.CenterSpawnOffset);
                    }
                }
            }

            ___slideTurnMultiplier = PluginCore.config.SlideTurnMultiplier;
            ___stopDrag = PluginCore.config.StopDrag;
            ___balanceRecoveryTime = PluginCore.config.BalanceRecoveryTime;
            ___tackleForceMultiplier = PluginCore.config.TackleForceMultiplier;
            ___tackleForceThreshold = PluginCore.config.TackleForceThreshold;
            ___tackleSpeedThreshold = PluginCore.config.TackleSpeedThreshold;
            __instance.GetComponent<CapsuleCollider>().radius *= PluginCore.config.TorsoColliderRadiusFactor;
            __instance.GetComponent<SphereCollider>().radius *= PluginCore.config.HeadColliderRadiusFactor;

            bool isGoalie = __instance.name.Contains("Goalie");

            // The custom skater torso used to be swapped in here, mesh and collider both,
            // from the CompAssets bundle. That bundle is gone, so there is nothing to swap
            // and the vanilla torso stands as shipped. EnableSmallerModels went the same
            // way: it swapped shrunken torso/groin meshes, and the position offsets it
            // paired with would only shift a vanilla mesh out of place on its own. Both
            // config flags are kept so existing config files still load.

            ___playerMesh.PlayerTorso.GetComponentInChildren<MeshCollider>().material.bounciness = PluginCore.config.PlayerColliderBounciness;
            ___playerMesh.PlayerGroin.GetComponentInChildren<MeshCollider>().material.bounciness = PluginCore.config.PlayerColliderBounciness;
            ___playerMesh.PlayerHead.GetComponentInChildren<SphereCollider>().material.bounciness = PluginCore.config.PlayerColliderBounciness;

            if (isGoalie) return;

            if (PluginCore.config.EnablePuckThroughBodies && !__instance.Player.IsReplay.Value) {
                ___playerMesh.PlayerGroin.GetComponentInChildren<MeshCollider>().excludeLayers |= (1 << LayerMask.NameToLayer("Puck"));
                ___playerMesh.PlayerTorso.GetComponentInChildren<MeshCollider>().excludeLayers |= (1 << LayerMask.NameToLayer("Puck"));
                ___playerMesh.PlayerHead.GetComponentInChildren<SphereCollider>().excludeLayers |= (1 << LayerMask.NameToLayer("Puck"));
            }

            if (PluginCore.config.EnablePuckThroughGroin && !__instance.Player.IsReplay.Value) {
                ___playerMesh.PlayerGroin.GetComponentInChildren<MeshCollider>().excludeLayers |= (1 << LayerMask.NameToLayer("Puck"));
            }

            ___slideDrag = PluginCore.config.SlideDrag;

            if (PluginCore.config.ThinSkaterBodies) {
                // Multiply against the prefab's baseline scale rather than overwriting it.
                // b897 prefabs ship PlayerTorso/PlayerGroin at (0.4, 0.4, 0.4); the pre-b897
                // assignment-based form blew the body up to (factor, 1, factor) which made the
                // stick get stuck on the oversized torso.
                float factor = PluginCore.config.SkaterThinningFactor;
                var groinMc = ___playerMesh.PlayerGroin.GetComponentInChildren<MeshCollider>();
                if (groinMc != null) {
                    var s = groinMc.transform.localScale;
                    groinMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);
                }
                var torsoMc = ___playerMesh.PlayerTorso.GetComponentInChildren<MeshCollider>();
                if (torsoMc != null) {
                    var s = torsoMc.transform.localScale;
                    torsoMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerBodyV2), "FixedUpdate")]
    public class PlayerBodyFixedUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance)
        {
            // OnFall() must run server-side only. In b1117 HasFallen became a
            // NetworkVariable<bool> with server write-authority and OnFall() writes
            // HasFallen.Value with no internal guard (vanilla only ever calls it
            // inside FixedUpdate's `if (IsServer)` block). Calling it on a client
            // throws "Client is not allowed to write to this NetworkVariable" every
            // fall. In b897 HasFallen was a plain bool so the unguarded call was
            // harmless. The server write replicates HasFallen to clients as before.
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            // The trigger is deliberately wider than vanilla's (we fall from the
            // intermediate band, before the skater is fully sideways), but the
            // !HasFallen.Value latch is not optional: OnFall() is not idempotent.
            // It kills and re-allocates the balance-recovery DOTween on every
            // call, so without the latch it re-runs every FixedUpdate for as long
            // as the skater sits in that band, pinning KeepUpright.Balance near 0
            // instead of letting it ramp back to 1 over balanceRecoveryTime.
            if (__instance.HasFallen.Value) return;
            if (!__instance.IsUpright && !__instance.IsSideways) __instance.OnFall();
        }
    }

    [HarmonyPatch(typeof(PlayerBodyV2), "DashLeft")]
    public class PlayerBodyDashLeftPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerBodyV2 __instance, ref bool ___canDash, ref NetworkVariable<bool> ___IsSliding, ref float ___dashVelocity,
        ref NetworkVariable<bool> ___IsStopping, ref float ___dashStaminaDrain)
        {
            if (__instance.Player.IsReplay.Value) return true;       
            if (!___canDash) return false;
            if (Mathf.Abs(__instance.transform.worldToLocalMatrix.MultiplyVector(__instance.Rigidbody.linearVelocity).x) > PluginCore.config.GoalieDashSpeedLimit) ___dashVelocity *= 0.5f;
            else ___dashVelocity = 6f;
            if (!PluginCore.config.EnableGoalieMicrodash) return true;
            if (___IsSliding.Value) return true;
            if (___IsStopping.Value) return true;
            if (__instance.IsJumping) return true;
            if (!(__instance.Rigidbody.linearVelocity.magnitude > 3.2f) && __instance.Stamina.Value > 0.5f * ___dashStaminaDrain)
            {

                __instance.Rigidbody.AddForce(-__instance.transform.right * ___dashVelocity * 0.55f, ForceMode.VelocityChange);
                __instance.Stamina.Value -= PluginCore.config.MicrodashStamCostFraction * ___dashStaminaDrain;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerBodyV2), "DashRight")]
    public class PlayerBodyDashRightPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerBodyV2 __instance, ref bool ___canDash, ref NetworkVariable<bool> ___IsSliding, ref float ___dashVelocity, ref NetworkVariable<bool> ___IsStopping, ref float ___dashStaminaDrain)
        {
            if (__instance.Player.IsReplay.Value) return true;
            if (!___canDash) return false;
            if (Mathf.Abs(__instance.transform.worldToLocalMatrix.MultiplyVector(__instance.Rigidbody.linearVelocity).x) > PluginCore.config.GoalieDashSpeedLimit) ___dashVelocity *= 0.5f;
            else ___dashVelocity = 6f;
            if (!PluginCore.config.EnableGoalieMicrodash) return true;            
            if (___IsSliding.Value) return true;
            if (___IsStopping.Value) return true;
            if (__instance.IsJumping) return true;
            if (!(__instance.Rigidbody.linearVelocity.magnitude > 3.2f) && __instance.Stamina.Value > 0.5f * ___dashStaminaDrain)
            {
                __instance.Rigidbody.AddForce(__instance.transform.right * ___dashVelocity * 0.55f, ForceMode.VelocityChange);
                __instance.Stamina.Value -= PluginCore.config.MicrodashStamCostFraction * ___dashStaminaDrain;
                return false;
            }
            return true;

        }
    }

    /// <summary>
    /// Client-side debug visualization for arena and player collider meshes.
    /// Called from the settings UI toggles; no per-frame cost when disabled.
    /// </summary>
    internal static class ClientClipBrushes
    {
        /// <summary>Toggle arena collider mesh visualization on/off.</summary>
        internal static void ApplyArena(bool show)
        {
            // Delegates to GoalNetTweaks.SyncArenaColliderDebugBrushes which handles all
            // collider types (Box/Sphere/Capsule/Mesh) on the custom arena instance.
            // IsArenaColliderDebugEnabled() now also checks ShowArenaClipBrushes.
            DashFallMod.GoalNetTweaks.RefreshArenaColliderBrushes();
        }

        /// <summary>Toggle player collider mesh visualization on/off. Refreshes TorsoDebugBrush on all bodies.</summary>
        internal static void ApplyPlayer(bool show)
        {
            // TorsoDebugBrush.Sync already checks ShowPlayerClipBrushes from config.
            // Force a refresh on all current bodies.
            var bodies = UnityEngine.Object.FindObjectsByType<PlayerBodyV2>(FindObjectsSortMode.None);
            foreach (var body in bodies)
            {
                if (body == null) continue;
                var torsoGo = body.PlayerMesh?.PlayerTorso;
                if (torsoGo == null) continue;
                var mc = torsoGo.GetComponentInChildren<MeshCollider>();
                if (mc != null) TorsoDebugBrush.Sync(mc);
            }
        }
    }
}