using HarmonyLib;
using System;
using UnityEngine;

namespace CompetitivePuckTweaks.src {
    public class StickOnBodyCollisions {
        private const float STICK_FORCE_SOUND_THRESHOLD = 17.5f;
        private const int STICK_LAYER = 6;

        // Property rather than `static readonly` so flipping the master flag
        // (EnableCompAdjust) or the per-feature flag (StickBodyCollision) at
        // runtime takes effect on the next patched call.  A field initializer
        // would freeze the value at class-init time, before the user has had
        // a chance to load their config edits.
        private static bool _disablePatch =>
            CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision != true;

        private static readonly LockDictionary<Rigidbody, LayerMask> _currentlyIgnoredStickCollisions = [];

        [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
        public class PlayerBodyV2_OnNetworkPostSpawn_Patch {
            [HarmonyPostfix]
            public static void Postfix(PlayerBodyV2 __instance) {
                if (_disablePatch)
                    return;

                __instance.Rigidbody.mass = float.MaxValue;
            }
        }

        [HarmonyPatch(typeof(PlayerBodyV2), "Server_OnCollisionDeferred")]
        public class PlayerBodyV2_Server_OnCollisionDeferred_Patch {
            [HarmonyPrefix]
            public static bool Prefix(GameObject gameObject, float force) {
                if (_disablePatch)
                    return true;

                if (gameObject.layer == STICK_LAYER && force < STICK_FORCE_SOUND_THRESHOLD)
                    return false;

                return true;
            }
        }

        [HarmonyPatch(typeof(Stick), "FixedUpdate")]
        public class Stick_FixedUpdate_Patch {
            [HarmonyPrefix]
            public static void Postfix(Stick __instance) {
                if (_disablePatch || !__instance.Player)
                    return;

                try {
                    float stickToBodyDistance = Vector3.Distance(__instance.Rigidbody.transform.position, __instance.PlayerBody.Rigidbody.transform.position);

                    if (_currentlyIgnoredStickCollisions.TryGetValue(__instance.Rigidbody, out LayerMask layerMask)) {
                        if (stickToBodyDistance < 2.52f) {
                            CompetitiveAdjustments.ConfigManager.Log($"Stick is UNstuck ! Distance to PlayerBody ({__instance.Player.Username.Value}) : {stickToBodyDistance}");
                            __instance.Rigidbody.excludeLayers = layerMask;
                            _currentlyIgnoredStickCollisions.Remove(__instance.Rigidbody);
                        }
                        return;
                    }

                    if (stickToBodyDistance > 2.68f) {
                        CompetitiveAdjustments.ConfigManager.Log($"Stick is stuck ! Distance to PlayerBody ({__instance.Player.Username.Value}) : {stickToBodyDistance}");
                        _currentlyIgnoredStickCollisions.Add(__instance.Rigidbody, __instance.Rigidbody.excludeLayers);
                        __instance.Rigidbody.excludeLayers = -1; // Everything == -1;
                    }
                }
                catch (Exception ex) {
                    CompetitiveAdjustments.ConfigManager.LogError($"Error in {nameof(StickOnBodyCollisions)}.{nameof(Stick_FixedUpdate_Patch)}.\n{ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Stick), nameof(Stick.OnNetworkDespawn))]
        public class Stick_OnNetworkDespawn_Patch {
            [HarmonyPrefix]
            public static bool Prefix(Stick __instance) {
                if (__instance.Rigidbody && __instance.Rigidbody != null)
                    _currentlyIgnoredStickCollisions.Remove(__instance.Rigidbody);
                return true;
            }
        }
    }
}
