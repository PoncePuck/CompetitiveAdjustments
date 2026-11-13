using System;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace CompetitivePuckTweaks.src {
    public class UtilObj {

        public void LoadListeners() {
            PluginCore.Log("Loading ModificationEvent...");
            Physics.ContactModifyEvent += this.ModificationEvent;
            PluginCore.Log("ModificationEvent loaded");
        }

        public void UnloadListeners() {
            Physics.ContactModifyEvent -= this.ModificationEvent;
            PluginCore.Log("ModificationEvent unloaded");
        }

        public void ModificationEvent(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs) {
            try {
                for (int j = 0; j < pairs.Count(); j++) {
                    ModifiableContactPair pair = pairs[j];

                    bool colIsPuck = PluginCore.PuckIDs.Contains(pair.colliderInstanceID);
                    bool otherColIsPuck = PluginCore.PuckIDs.Contains(pair.otherColliderInstanceID);
                    bool colIsStick = PluginCore.StickMeshes.Keys.Contains(pair.colliderInstanceID);
                    bool otherColIsStick = PluginCore.StickMeshes.Keys.Contains(pair.otherColliderInstanceID);

                    if ((colIsPuck && otherColIsStick) || (colIsStick && otherColIsPuck)) {
                        ModifiableMassProperties newMass = pair.massProperties;

                        if (colIsStick) {
                            newMass.inverseMassScale = PluginCore.config.StickOnPuckInverseMass;
                            newMass.inverseInertiaScale = PluginCore.config.StickOnPuckInverseMass;

                        }
                        if (otherColIsStick) {
                            newMass.otherInverseMassScale = PluginCore.config.StickOnPuckInverseMass;
                            newMass.otherInverseInertiaScale = PluginCore.config.StickOnPuckInverseMass;
                        }
                        pair.massProperties = newMass;
                    }
                    else if (colIsStick && otherColIsStick) {
                        for (int i = 0; i < pair.contactCount; i++) {
                            Stick stick = PluginCore.StickMeshes[pair.colliderInstanceID];
                            Stick otherStick = PluginCore.StickMeshes[pair.otherColliderInstanceID];

                            ModifiableMassProperties newMass = pair.massProperties;

                            float colZ = stick.transform.InverseTransformPoint(pair.GetPoint(i)).z;
                            float otherColZ = otherStick.transform.InverseTransformPoint(pair.GetPoint(i)).z;

                            if (colZ < 0f && otherColZ < 0f)
                                continue;

                            float stickMass = Mathf.Clamp((Mathf.Clamp(colZ, -1f, 0.5f) + 1f) * 1.25f, 0.001f, 1.7f);
                            float otherStickMass = Mathf.Clamp((Mathf.Clamp(otherColZ, -1f, 0.5f) + 1f) * 1.25f, 0.001f, 1.7f);

                            float newScale = stickMass;
                            float otherNewScale = otherStickMass;

                            newMass.inverseInertiaScale = newScale;
                            newMass.inverseMassScale = newScale;
                            newMass.otherInverseInertiaScale = otherNewScale;
                            newMass.otherInverseMassScale = otherNewScale;

                            pair.massProperties = newMass;
                        }
                    }
                        
                }
            }
            catch (Exception e) { PluginCore.Log($"Exception in modification event: {e.Message}"); }
        }
    }
}
