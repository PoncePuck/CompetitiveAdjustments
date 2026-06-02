using HarmonyLib;
using UnityEngine;

namespace CompetitivePuckTweaks.src
{
    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    [HarmonyPriority(Priority.High)]
    public class StickPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance, ref GameObject ___shaftHandle, ref float ___shaftHandleProportionalGain)
        {
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

            foreach (MeshCollider mC in newMeshColliders)
            {
                mC.hasModifiableContacts = true;
                PluginCore.StickMeshes.Add(mC.GetInstanceID(), __instance);
            }

            __instance.Rigidbody.mass = PluginCore.config.StickMass;

            // Own-body ignore: runs regardless of DisableShaftCollision.
            // Layer 6-8 is enabled globally when StickBodyCollision is on; this prevents self-hit.
            if (CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision == true) {
                var stickCols = __instance.GetComponentsInChildren<Collider>();
                var bodyCols  = __instance.PlayerBody.GetComponentsInChildren<Collider>();

                foreach (var sc in stickCols) {
                    foreach (var bc in bodyCols) {
                        if (sc != null && bc != null)
                            Physics.IgnoreCollision(sc, bc, true);
                    }
                }
            }

            if (!PluginCore.config.DisableShaftCollision)
                return;

            if (PluginCore.config.EnableMidStickCollider)
            {
                boxCollider = __instance.gameObject.AddComponent<BoxCollider>();
                boxCollider.size = new UnityEngine.Vector3(0.029f, 0.14f, 1.21f);
                boxCollider.center += new UnityEngine.Vector3(0, 0, 0.145f);
                boxCollider.hasModifiableContacts = true;
                PluginCore.StickMeshes.Add(boxCollider.GetInstanceID(), __instance);

                foreach (Puck puck in UnityEngine.Object.FindObjectsByType<Puck>(FindObjectsSortMode.None))
                {
                    Physics.IgnoreCollision(puck.IceCollider, boxCollider);
                    Physics.IgnoreCollision(puck.StickCollider, boxCollider);
                }
            }

            foreach (Stick stick in UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsSortMode.None))
            {
                StickMesh oldStickMesh = stick.gameObject.GetComponentInChildren<StickMesh>();
                BoxCollider oldHandleCollider = stick.GetComponent<BoxCollider>();

                Physics.IgnoreCollision(newStickMesh.BladeCollider, oldStickMesh.ShaftCollider);
                Physics.IgnoreCollision(oldStickMesh.BladeCollider, newStickMesh.ShaftCollider);

                if (PluginCore.config.EnableMidStickCollider)
                {
                    Physics.IgnoreCollision(oldStickMesh.ShaftCollider, boxCollider);
                    Physics.IgnoreCollision(newStickMesh.ShaftCollider, boxCollider);
                    Physics.IgnoreCollision(newStickMesh.ShaftCollider, oldHandleCollider);
                    Physics.IgnoreCollision(oldStickMesh.ShaftCollider, oldHandleCollider);
                }

                foreach (MeshCollider collider in oldStickMesh.gameObject.GetComponentsInChildren<MeshCollider>())
                {
                    foreach (MeshCollider newCollider in newMeshColliders)
                    {
                        if ((collider.gameObject.tag.Contains("Stick Blade") && newCollider.gameObject.tag.Contains("Stick Shaft")) ||
                            (newCollider.gameObject.tag.Contains("Stick Blade") && collider.gameObject.tag.Contains("Stick Shaft")) ||
                            (collider.gameObject.tag.Contains("Stick Shaft") && newCollider.gameObject.tag.Contains("Stick Shaft")))
                        {
                            Physics.IgnoreCollision(collider, newCollider);
                        }
                        if (PluginCore.config.EnableMidStickCollider)
                        {
                            if (collider.gameObject.tag.Contains("Stick Shaft"))
                            {
                                Physics.IgnoreCollision(collider, boxCollider);
                                Physics.IgnoreCollision(collider, oldHandleCollider);
                            }
                            if (newCollider.gameObject.tag.Contains("Stick Shaft"))
                            {
                                Physics.IgnoreCollision(newCollider, boxCollider);
                                Physics.IgnoreCollision(newCollider, oldHandleCollider);
                            }
                        }
                    }
                }

                PluginCore.Dbg($"Collision ignorance updated.");
            }
        }
    }

    [HarmonyPatch(typeof(Stick), nameof(Stick.OnNetworkDespawn))]
    public class StickDespawnPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Stick __instance)
        {
            if (PluginCore.config.BananaMode) return false;
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
            if (thisStickMesh != null)
            {
                foreach (MeshCollider col in thisStickMesh.GetComponentsInChildren<MeshCollider>())
                    if (col != null && PluginCore.StickMeshes.ContainsKey(col.GetInstanceID()))
                        PluginCore.StickMeshes.Remove(col.GetInstanceID());
            }
            return true;
        }
    }
}