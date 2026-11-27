using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace CompetitivePuckTweaks.src {
    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    [HarmonyPriority(Priority.High)]
    public class StickPatch {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance, ref GameObject ___shaftHandle, ref float ___shaftHandleProportionalGain) {
            if (__instance == null) {
                PluginCore.LogError($"Stick null on network post spawn");
                return;
            }

            ___shaftHandleProportionalGain = PluginCore.config.ShaftHandleProportionalGain;

            BoxCollider boxCollider = null;

            StickMesh newStickMesh = __instance.gameObject.GetComponentInChildren<StickMesh>();

            if (newStickMesh == null) {
                PluginCore.LogError($"StickMesh is null!");
                return;
            }

            // find meshcolliders
            MeshCollider[] newMeshColliders = newStickMesh.transform.GetComponentsInChildren<MeshCollider>();

            foreach (MeshCollider mC in newMeshColliders) {
                mC.hasModifiableContacts = true;
                PluginCore.StickMeshes.Add(mC.GetInstanceID(), __instance);
            }

            __instance.Rigidbody.mass = PluginCore.config.StickMass;

            UnapplySelfStickOnBodyCollisions(__instance);

            if (!PluginCore.config.DisableShaftCollision)
                return;

            if (PluginCore.config.EnableMidStickCollider) {
                boxCollider = __instance.gameObject.AddComponent<BoxCollider>();
                boxCollider.size = new UnityEngine.Vector3(0.029f, 0.14f, 1.21f);
                boxCollider.center += new UnityEngine.Vector3(0, 0, 0.145f);
                boxCollider.hasModifiableContacts = true;
                PluginCore.StickMeshes.Add(boxCollider.GetInstanceID(), __instance);

                foreach (Puck puck in UnityEngine.Object.FindObjectsByType<Puck>(FindObjectsSortMode.None)) {
                    Physics.IgnoreCollision(puck.IceCollider, boxCollider);
                    Physics.IgnoreCollision(puck.StickCollider, boxCollider);
                }
            }

            foreach (Stick stick in UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsSortMode.None)) {
                StickMesh oldStickMesh = stick.gameObject.GetComponentInChildren<StickMesh>();
                BoxCollider oldHandleCollider = stick.GetComponent<BoxCollider>();

                Physics.IgnoreCollision(newStickMesh.BladeCollider, oldStickMesh.ShaftCollider);
                Physics.IgnoreCollision(oldStickMesh.BladeCollider, newStickMesh.ShaftCollider);

                if (PluginCore.config.EnableMidStickCollider) {
                    Physics.IgnoreCollision(oldStickMesh.ShaftCollider, boxCollider);
                    Physics.IgnoreCollision(newStickMesh.ShaftCollider, boxCollider);
                    Physics.IgnoreCollision(newStickMesh.ShaftCollider, oldHandleCollider);
                    Physics.IgnoreCollision(oldStickMesh.ShaftCollider, oldHandleCollider);
                }

                foreach (MeshCollider collider in oldStickMesh.gameObject.GetComponentsInChildren<MeshCollider>()) {
                    foreach (MeshCollider newCollider in newMeshColliders) {
                        if ((collider.gameObject.tag.Contains("Stick Blade") && newCollider.gameObject.tag.Contains("Stick Shaft")) ||
                            (newCollider.gameObject.tag.Contains("Stick Blade") && collider.gameObject.tag.Contains("Stick Shaft")) ||
                            (collider.gameObject.tag.Contains("Stick Shaft") && newCollider.gameObject.tag.Contains("Stick Shaft"))) {
                            Physics.IgnoreCollision(collider, newCollider);
                        }
                        if (PluginCore.config.EnableMidStickCollider) {
                            if (collider.gameObject.tag.Contains("Stick Shaft")) {
                                Physics.IgnoreCollision(collider, boxCollider);
                                Physics.IgnoreCollision(collider, oldHandleCollider);
                            }
                            if (newCollider.gameObject.tag.Contains("Stick Shaft")) {
                                Physics.IgnoreCollision(newCollider, boxCollider);
                                Physics.IgnoreCollision(newCollider, oldHandleCollider);
                            }
                        }
                    }
                }

                PluginCore.Dbg($"Collision ignorance updated.");
            }
        }

        /// <summary>
        /// Every collider that makes up a player's body: whatever sits under the PlayerBody,
        /// plus everything under the PlayerMesh and its torso / groin / head parts.  ANY
        /// collider type counts.  The head, for one, is a SphereCollider, which the old
        /// MeshCollider-only lists never reached; with ThinSkaterBodies on, the stick layer is
        /// opened against exactly these mesh-side colliders (layer 9), so a missed one is a
        /// stick that still collides.
        /// </summary>
        public static HashSet<Collider> CollectBodyColliders(PlayerBody body) {
            var result = new HashSet<Collider>();
            if (body == null) return result;

            result.UnionWith(body.GetComponentsInChildren<Collider>(true));

            PlayerMesh mesh = body.PlayerMesh;
            if (mesh == null) return result;

            result.UnionWith(mesh.GetComponentsInChildren<Collider>(true));
            if (mesh.PlayerTorso != null) result.UnionWith(mesh.PlayerTorso.GetComponentsInChildren<Collider>(true));
            if (mesh.PlayerGroin != null) result.UnionWith(mesh.PlayerGroin.GetComponentsInChildren<Collider>(true));
            if (mesh.PlayerHead != null) result.UnionWith(mesh.PlayerHead.GetComponentsInChildren<Collider>(true));

            return result;
        }

        /// <summary>
        /// Physics.IgnoreCollision for one pair, but only when both colliders are live.  Unity
        /// does not keep the pair for a disabled collider or an inactive object (it logs an
        /// error instead), and it drops the pair when a collider is later disabled and enabled
        /// again.  Callers therefore re-run this periodically rather than once.
        /// Returns whether the call was made.
        /// </summary>
        public static bool SetIgnore(Collider a, Collider b, bool ignore) {
            if (a == null || b == null || a == b) return false;
            if (!a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy)
                return false;
            Physics.IgnoreCollision(a, b, ignore);
            return true;
        }

        private static readonly HashSet<int> _loggedBodies = new HashSet<int>();

        // One debug dump per body, so a stick that still collides can be traced to the exact
        // collider (type, layer, whether it sits under the PlayerBody).  Debug logs only.
        private static void LogBodyCollidersOnce(PlayerBody body, HashSet<Collider> colliders) {
            if (!_loggedBodies.Add(body.GetInstanceID())) return;
            foreach (Collider c in colliders) {
                if (c == null) continue;
                PluginCore.Dbg($"[StickBodyIgnore] {body.name}: {c.GetType().Name} '{c.name}' layer={LayerMask.LayerToName(c.gameObject.layer)} " +
                               $"enabled={c.enabled} active={c.gameObject.activeInHierarchy} underBody={c.transform.IsChildOf(body.transform)}");
            }
        }

        public static void UnapplySelfStickOnBodyCollisions(Stick stick) {
            // Own-body ignore: runs regardless of DisableShaftCollision.
            // The stick layer is opened against the body layers globally when StickBodyCollision
            // is on (6-8, or 6-9 with ThinSkaterBodies); this prevents self-hit.
            // Safe to call for any stick, any number of times: a stick without an assigned body
            // has nothing to ignore yet, and re-applying a pair is harmless.  Re-running it is
            // also the repair for pairs Unity dropped (collider re-enabled, shape rebuilt).
            if (stick == null || stick.PlayerBody == null) return;
            if (CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision != true) return;

            var stickCols = stick.GetComponentsInChildren<Collider>(true);
            var bodyCols = CollectBodyColliders(stick.PlayerBody);
            LogBodyCollidersOnce(stick.PlayerBody, bodyCols);

            foreach (var sc in stickCols) {
                foreach (var bc in bodyCols)
                    SetIgnore(sc, bc, true);
            }
        }
    }

    [HarmonyPatch(typeof(Stick), nameof(Stick.Server_Teleport))]
    [HarmonyPriority(Priority.High)]
    public class StickTeleportPatch {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance, Vector3 position, Quaternion rotation) {
            if (__instance == null) {
                PluginCore.LogError($"Stick null on Server_Teleport");
                return;
            }

            StickPatch.UnapplySelfStickOnBodyCollisions(__instance);
        }
    }

    [HarmonyPatch(typeof(Stick), nameof(Stick.OnNetworkDespawn))]
    public class StickDespawnPatch {
        [HarmonyPrefix]
        public static bool Prefix(Stick __instance) {
            // `return true` means "skip our own bookkeeping, let vanilla run", the
            // same idiom as the UsePhysicsModificationEvents line below.  This used
            // to `return false`, which in Harmony means "do not run the original at
            // all", so enabling BananaMode suppressed the whole vanilla despawn:
            // Event_Everyone_OnStickDespawned never fired (leaving the replay
            // recorder and minimap holding destroyed Sticks), the PlayerReference
            // OnValueChanged handler was never unsubscribed, and NGO's own
            // OnNetworkDespawn bookkeeping never ran.  BananaMode has no other
            // effect anywhere in the mod, so that leak was all it did.
            if (PluginCore.config.BananaMode) return true;
            if (!PluginCore.config.UsePhysicsModificationEvents) return true;
            if (__instance == null) return true;

            // BoxCollider may or may not be attached. The mid-stick collider is
            // only added in the spawn postfix when EnableMidStickCollider was
            // true at that time, so flipping the config later means the
            // BoxCollider doesn't exist on this stick. Just guard for null.
            BoxCollider thisBoxCol = __instance.GetComponent<BoxCollider>();
            if (thisBoxCol != null && PluginCore.StickMeshes.ContainsKey(thisBoxCol.GetInstanceID()))
                PluginCore.StickMeshes.Remove(thisBoxCol.GetInstanceID());

            StickMesh thisStickMesh = __instance.gameObject.GetComponentInChildren<StickMesh>();
            if (thisStickMesh != null) {
                foreach (MeshCollider col in thisStickMesh.GetComponentsInChildren<MeshCollider>())
                    if (col != null && PluginCore.StickMeshes.ContainsKey(col.GetInstanceID()))
                        PluginCore.StickMeshes.Remove(col.GetInstanceID());
            }
            return true;
        }
    }
}
