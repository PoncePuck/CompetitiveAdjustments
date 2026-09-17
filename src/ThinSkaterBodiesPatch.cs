using CompetitivePuckTweaks.src;
using HarmonyLib;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace DashFallMod {
    // Must run on EVERY role, not just the server: it edits the torso/groin
    // MeshCollider transforms, which are purely local per-peer prefab state and
    // are never networked. Living in CompetitivePuckTweaks.src meant this was
    // only ever installed where Tweaks.PluginCore.OnEnable succeeds (a dedicated
    // server or listen host), so a remote client never got the patch and never
    // saw a thinned body at all. Same fix as PlayerLegPadPatch.
    public static class ThinSkaterBodiesPatch {
        private static readonly LockDictionary<PlayerBody, bool> _patchedPlayerBodies = new LockDictionary<PlayerBody, bool>();

        [HarmonyPatch(typeof(PlayerBody), "OnNetworkPostSpawn")]
        public static class ThinSkaterBodies_OnNetworkPostSpawn_Patch {
            [HarmonyPostfix]
            public static void Postfix(PlayerBody __instance, ref PlayerMesh ___playerMesh) {
                ApplyThinSkaterBody(__instance, ___playerMesh);
            }
        }

        [HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.OnNetworkDespawn))]
        public static class ThinSkaterBodies_OnNetworkDespawn_Patch {
            [HarmonyPrefix]
            public static bool Prefix(PlayerBody __instance) {
                _patchedPlayerBodies.Remove(__instance);
                return true;
            }
        }

        public static void ApplyThinSkaterBody(PlayerBody playerBody, PlayerMesh playerMesh, bool includeGoalie = false) {
            if (CompetitiveAdjustments.ConfigManager.CompTweaksEffective == null || !CompetitiveAdjustments.ConfigManager.CompTweaksEffective.ThinSkaterBodies)
                return;
            if (playerMesh == null)
                return;

            if (_patchedPlayerBodies.ContainsKey(playerBody))
                return;

            float factor = CompetitiveAdjustments.ConfigManager.CompTweaksEffective.SkaterThinningFactor;
            if (factor == 1f)
                return;

            if (!includeGoalie && playerBody.name.Contains("Goalie"))
                return;

            int puckLayer = LayerMask.NameToLayer("Puck");

            var capsule = playerBody.GetComponent<CapsuleCollider>();

            var groinMcs = playerMesh.PlayerGroin?.GetComponentsInChildren<MeshCollider>();
            foreach (MeshCollider groinMc in groinMcs) {
                if (groinMc == null)
                    continue;

                var s = groinMc.transform.localScale;
                groinMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);

                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) {
                    groinMc.convex = true;
                    groinMc.excludeLayers = capsule.excludeLayers;
                    groinMc.includeLayers = capsule.includeLayers;
                    if (!CompetitiveAdjustments.ConfigManager.CompTweaksEffective.EnablePuckThroughGroin) {
                        groinMc.excludeLayers &= ~(1 << puckLayer);
                        groinMc.includeLayers |= (1 << puckLayer);
                    }
                    else {
                        groinMc.includeLayers &= ~(1 << puckLayer);
                        groinMc.excludeLayers |= (1 << puckLayer);
                    }

                    if (CompetitiveAdjustments.ConfigManager.CompAdjustEffective.StickBodyCollision) {
                        groinMc.excludeLayers &= ~(1 << StickOnBodyCollisions.STICK_LAYER);
                        groinMc.includeLayers |= (1 << StickOnBodyCollisions.STICK_LAYER);
                    }
                    else {
                        groinMc.includeLayers &= ~(1 << StickOnBodyCollisions.STICK_LAYER);
                        groinMc.excludeLayers |= (1 << StickOnBodyCollisions.STICK_LAYER);
                    }

                    groinMc.excludeLayers &= ~(1 << groinMc.gameObject.layer);
                    groinMc.includeLayers |= (1 << groinMc.gameObject.layer);

                    if (capsule.material != null) {
                        if (groinMc.material == null)
                            groinMc.material = new PhysicsMaterial(groinMc.name + "_Mat");
                        groinMc.material.dynamicFriction = capsule.material.dynamicFriction;
                        groinMc.material.staticFriction = capsule.material.staticFriction;
                        groinMc.material.frictionCombine = capsule.material.frictionCombine;
                        groinMc.material.bounceCombine = capsule.material.bounceCombine;
                    }
                }
            }

            var torsoMcs = playerMesh.PlayerTorso?.GetComponentsInChildren<MeshCollider>();
            foreach (MeshCollider torsoMc in torsoMcs) {
                if (torsoMc == null)
                    continue;

                var s = torsoMc.transform.localScale;
                torsoMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);

                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) {
                    torsoMc.convex = true;
                    torsoMc.excludeLayers = capsule.excludeLayers;
                    torsoMc.includeLayers = capsule.includeLayers;
                    if (!CompetitiveAdjustments.ConfigManager.CompTweaksEffective.EnablePuckThroughBodies) {
                        torsoMc.excludeLayers &= ~(1 << puckLayer);
                        torsoMc.includeLayers |= (1 << puckLayer);
                    }
                    else {
                        torsoMc.includeLayers &= ~(1 << puckLayer);
                        torsoMc.excludeLayers |= (1 << puckLayer);
                    }

                    if (CompetitiveAdjustments.ConfigManager.CompAdjustEffective.StickBodyCollision) {
                        torsoMc.excludeLayers &= ~(1 << StickOnBodyCollisions.STICK_LAYER);
                        torsoMc.includeLayers |= (1 << StickOnBodyCollisions.STICK_LAYER);
                    }
                    else {
                        torsoMc.includeLayers &= ~(1 << StickOnBodyCollisions.STICK_LAYER);
                        torsoMc.excludeLayers |= (1 << StickOnBodyCollisions.STICK_LAYER);
                    }

                    torsoMc.excludeLayers &= ~(1 << torsoMc.gameObject.layer);
                    torsoMc.includeLayers |= (1 << torsoMc.gameObject.layer);

                    torsoMc.excludeLayers &= ~(1 << torsoMc.gameObject.layer);
                    torsoMc.includeLayers |= (1 << torsoMc.gameObject.layer);

                    if (capsule.material != null) {
                        if (torsoMc.material == null)
                            torsoMc.material = new PhysicsMaterial(torsoMc.name + "_Mat");
                        torsoMc.material.dynamicFriction = capsule.material.dynamicFriction;
                        torsoMc.material.staticFriction = capsule.material.staticFriction;
                        torsoMc.material.frictionCombine = capsule.material.frictionCombine;
                        torsoMc.material.bounceCombine = capsule.material.bounceCombine;
                    }
                }
            }

            capsule.excludeLayers |= (1 << capsule.gameObject.layer);
            capsule.includeLayers &= ~(1 << capsule.gameObject.layer);

            capsule.excludeLayers |= (1 << StickOnBodyCollisions.STICK_LAYER);
            capsule.includeLayers &= ~(1 << StickOnBodyCollisions.STICK_LAYER);

            capsule.excludeLayers |= (1 << puckLayer);
            capsule.includeLayers &= ~(1 << puckLayer);

            capsule.excludeLayers |= (1 << puckLayer);
            capsule.includeLayers &= ~(1 << puckLayer);

            int newCollidersLayer = -1;
            if (torsoMcs.Length != 0)
                newCollidersLayer = torsoMcs.First().gameObject.layer;
            else if (groinMcs.Length != 0)
                newCollidersLayer = groinMcs.First().gameObject.layer;

            if (newCollidersLayer != -1) {
                capsule.excludeLayers |= (1 << newCollidersLayer);
                capsule.includeLayers &= ~(1 << newCollidersLayer);
            }

            _patchedPlayerBodies.AddOrUpdate(playerBody, true);
        }

        public static void RefreshAllPlayers() {
            foreach (Player player in PlayerManager.Instance.GetSpawnedPlayers()) {
                try {
                    ApplyThinSkaterBody(player.PlayerBody, player.PlayerBody.PlayerMesh);
                }
                catch { }
            }
        }
    }
}
