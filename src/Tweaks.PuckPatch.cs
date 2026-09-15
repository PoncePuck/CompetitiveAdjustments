using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;

namespace CompetitivePuckTweaks.src {
    [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
    public class PuckPatch {
        // Throttle for the "heal missed sync" fan-out below. Multiple puck
        // spawns in quick succession (warmup, multi-puck modes) used to send
        // a ManualSync per puck per client; once per second is plenty since
        // clients already pull sync via CMM_SYNC_REQUEST on their own.
        private static float _nextServerFanoutAt;
        private const float ServerFanoutCooldown = 1f;

        [HarmonyPostfix]
        public static void Postfix(Puck __instance) {
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
            if (nm != null && nm.IsServer) {
                float now = Time.unscaledTime;
                if (now >= _nextServerFanoutAt) {
                    _nextServerFanoutAt = now + ServerFanoutCooldown;
                    foreach (ulong clientId in nm.ConnectedClientsIds) {
                        if (clientId == NetworkManager.ServerClientId) continue;
                        PluginCore.ManualSync(clientId);
                    }
                }
            }
            else if (nm != null && nm.IsClient) {
                CompetitiveCompanion.PluginCore.RequestConfigSyncFromServer("PuckSpawn");
            }
        }

        // Captured once per puck INSTANCE, the first time ApplyPuckPhysics
        // sees it, so CatchGenerosity's counter-scale below stays correct
        // across repeated calls to this idempotent method (phase changes
        // re-call it on the SAME puck instance) rather than counter-scaling
        // an already-counter-scaled value on the second call onward.
        // Cleared in PuckDespawnPatch so this cannot grow across a long
        // server session.
        internal static readonly Dictionary<int, UnityEngine.Vector3> _originalStickColliderScale =
            new Dictionary<int, UnityEngine.Vector3>();

        // Cached PhysicsMaterial instances, built once and reused, rather than
        // a new UnityEngine.PhysicsMaterial allocated every single spawn.
        private static UnityEngine.PhysicsMaterial _iceMaterial;
        private static UnityEngine.PhysicsMaterial _stickContactMaterial;

        /// <summary>
        /// Apply every tuned puck property (scale, max speed, stick tensor, drag,
        /// mass, contact-modification flags and PuckIDs registration). Idempotent,
        /// so it is safe to call both at spawn (OnNetworkPostSpawn) and again on
        /// every phase change via PuckManager.Server_SpawnPucksForPhase. The
        /// private maxSpeed / stickTensor fields are written through Traverse so
        /// this can run outside the Harmony patch's ref-parameter context.
        ///
        /// The block below the original scale/speed/tensor/drag/mass lines is
        /// the PuckModifier integration — every new CompTweaksConfig field
        /// added for it. Each one is gated on its own no-op sentinel (-1 for
        /// "leave vanilla alone", 0 or 1 for whichever is mechanically inert
        /// depending on the field — see each field's own comment in
        /// ServerConfig.cs), so a server that has never imported a puck
        /// preset sees these lines do nothing at all, every single spawn.
        /// </summary>
        public static void ApplyPuckPhysics(Puck puck) {
            if (puck == null) return;

            UnityEngine.Vector3 puckScale = GetSyncedPuckScaleVector();
            puck.transform.localScale = puckScale;
            PluginCore.Dbg($"Puck scaled to {puckScale}");

            Traverse.Create(puck).Field("maxSpeed").SetValue(PluginCore.config.PuckMaxSpeed);
            Traverse.Create(puck).Field("stickTensor").SetValue(new UnityEngine.Vector3(
                PluginCore.config.PuckStickTensorX, PluginCore.config.PuckStickTensorY, PluginCore.config.PuckStickTensorZ));
            Traverse.Create(puck).Field("defaultTensor").SetValue(new UnityEngine.Vector3(
                PluginCore.config.PuckStickFreeTensorX, PluginCore.config.PuckStickFreeTensorY, PluginCore.config.PuckStickFreeTensorZ));

            // Derive mass/inertia from scale rather than the flat PuckMass
            // field, mirroring PuckModifier's own deriveMassFromScale /
            // deriveInertiaFromScale. Volume factor (product of all three
            // axis scale factors) is the physically correct mass multiplier
            // under non-uniform scaling for a constant-density assumption;
            // applied to the tensor fields too when inertia derivation is on,
            // since Rigidbody.inertiaTensor is not something this mod (or
            // PuckModifier) sets directly anywhere — both go through the
            // tensor fields already being set above.
            float volumeFactor = puckScale.x * puckScale.y * puckScale.z;
            puck.Rigidbody.mass = PluginCore.config.PuckDeriveMassFromScale
                ? PluginCore.config.PuckMass * volumeFactor
                : PluginCore.config.PuckMass;

            if (PluginCore.config.PuckDeriveInertiaFromScale && volumeFactor > 0.0001f) {
                Traverse.Create(puck).Field("stickTensor").SetValue(new UnityEngine.Vector3(
                    PluginCore.config.PuckStickTensorX, PluginCore.config.PuckStickTensorY,
                    PluginCore.config.PuckStickTensorZ) * volumeFactor);
                Traverse.Create(puck).Field("defaultTensor").SetValue(new UnityEngine.Vector3(
                    PluginCore.config.PuckStickFreeTensorX, PluginCore.config.PuckStickFreeTensorY,
                    PluginCore.config.PuckStickFreeTensorZ) * volumeFactor);
            }

            puck.Rigidbody.linearDamping = PluginCore.config.PuckDrag;
            if (PluginCore.config.PuckAngularDrag >= 0f)
                puck.Rigidbody.angularDamping = PluginCore.config.PuckAngularDrag;
            puck.StickCollider.hasModifiableContacts = true;
            puck.IceCollider.hasModifiableContacts = true;

            // --- PuckModifier integration: catch generosity ---
            // World-space size of the stick-catch hitbox held steady at
            // CatchGenerosity times its ORIGINAL size, independent of how
            // small/large PuckScale/PuckThicknessScale make the puck LOOK.
            // Division, not multiplication, because Unity scales COMPOUND
            // through a parent-child hierarchy: as the parent (puck root)
            // shrinks, the child's local scale must grow by the same factor
            // to keep the compound (world) result unchanged — identical
            // reasoning to PuckModifier's own version of this.
            if (!Mathf.Approximately(PluginCore.config.PuckCatchGenerosity, 1f)
                && puck.StickCollider.transform != puck.transform) {
                int puckId = puck.GetInstanceID();
                UnityEngine.Vector3 originalStickScale;
                if (!_originalStickColliderScale.TryGetValue(puckId, out originalStickScale)) {
                    originalStickScale = puck.StickCollider.transform.localScale;
                    _originalStickColliderScale[puckId] = originalStickScale;
                }

                UnityEngine.Vector3 safeScale = new UnityEngine.Vector3(
                    Mathf.Max(0.01f, puckScale.x), Mathf.Max(0.01f, puckScale.y), Mathf.Max(0.01f, puckScale.z));
                float generosity = PluginCore.config.PuckCatchGenerosity;

                puck.StickCollider.transform.localScale = new UnityEngine.Vector3(
                    originalStickScale.x * generosity / safeScale.x,
                    originalStickScale.y * generosity / safeScale.y,
                    originalStickScale.z * generosity / safeScale.z);
            }

            // --- PuckModifier integration: goal net caps, spin, grounded check, center of mass ---
            if (PluginCore.config.PuckGoalNetLinearDamp >= 0f)
                Traverse.Create(puck).Field("goalNetLinearVelocityMaximumMagnitude")
                    .SetValue(PluginCore.config.PuckGoalNetLinearDamp);
            if (PluginCore.config.PuckGoalNetAngularDamp >= 0f)
                Traverse.Create(puck).Field("goalNetAngularVelocityMaximumMagnitude")
                    .SetValue(PluginCore.config.PuckGoalNetAngularDamp);

            if (PluginCore.config.PuckMaxShotSpin >= 0f)
                Traverse.Create(puck).Field("maxAngularSpeed").SetValue(PluginCore.config.PuckMaxShotSpin);

            if (PluginCore.config.PuckMaxAngularVelocity >= 0f)
                puck.Rigidbody.maxAngularVelocity = PluginCore.config.PuckMaxAngularVelocity;

            // World-space radius, so it must track scale the same way
            // PuckModifier's own version does — a shrunken puck otherwise
            // stops detecting the ice at all.
            if (PluginCore.config.PuckGroundedCheckRadius >= 0f)
                Traverse.Create(puck).Field("groundedCheckSphereRadius")
                    .SetValue(PluginCore.config.PuckGroundedCheckRadius * puckScale.x);

            // groundedCenterOfMass is a full Vector3 on Puck (default (0,
            // -0.01, 0)) — only the Y component is exposed as a config field,
            // matching PuckModifier's own CenterOfMassY, so X/Z are preserved
            // from whatever the field already holds rather than zeroed.
            var currentCom = Traverse.Create(puck).Field("groundedCenterOfMass").GetValue<UnityEngine.Vector3>();
            Traverse.Create(puck).Field("groundedCenterOfMass").SetValue(new UnityEngine.Vector3(
                currentCom.x, PluginCore.config.PuckCenterOfMassY, currentCom.z));

            // --- PuckModifier integration: bounciness / friction ---
            // There is no friction coefficient anywhere in vanilla's own C#
            // (confirmed — this codebase's own README already notes it) —
            // both live on the collider's PhysicsMaterial, same as
            // PuckModifier's own approach. Split ice and stick-contact
            // materials for the same reason PuckModifier's own history
            // settled on splitting them: a single shared bounciness value
            // made every stick touch elastic whenever board bounce was
            // tuned up to stop boards killing momentum too fast.
            if (PluginCore.config.PuckIceFriction >= 0f || PluginCore.config.PuckBounciness >= 0f) {
                if (_iceMaterial == null) {
                    _iceMaterial = new UnityEngine.PhysicsMaterial("PuckModifier_Ice");
                    // Explicit rather than left at Unity's own default combine
                    // (Average for both) — Minimum on friction means the
                    // LOWER of the two colliders' friction values wins a
                    // contact, Average on bounce splits the difference. Same
                    // combine choice PuckModifier's own, already-working
                    // version of this uses.
                    _iceMaterial.frictionCombine = UnityEngine.PhysicsMaterialCombine.Minimum;
                    _iceMaterial.bounceCombine = UnityEngine.PhysicsMaterialCombine.Average;
                }
                if (PluginCore.config.PuckIceFriction >= 0f) {
                    _iceMaterial.dynamicFriction = PluginCore.config.PuckIceFriction;
                    _iceMaterial.staticFriction = PluginCore.config.PuckIceFriction * 1.5f;
                }
                if (PluginCore.config.PuckBounciness >= 0f)
                    _iceMaterial.bounciness = PluginCore.config.PuckBounciness;
                puck.IceCollider.sharedMaterial = _iceMaterial;
            }

            if (PluginCore.config.PuckStickContactBounciness >= 0f || PluginCore.config.PuckIceFriction >= 0f) {
                if (_stickContactMaterial == null) {
                    _stickContactMaterial = new UnityEngine.PhysicsMaterial("PuckModifier_StickContact");
                    _stickContactMaterial.frictionCombine = UnityEngine.PhysicsMaterialCombine.Minimum;
                    _stickContactMaterial.bounceCombine = UnityEngine.PhysicsMaterialCombine.Average;
                }
                // Friction is shared between the ice and stick-contact
                // materials — only bounciness is deliberately split between
                // them, same as our own mod's actual behavior. There is no
                // separate "stick friction" field in PuckModifier's own
                // format either.
                if (PluginCore.config.PuckIceFriction >= 0f) {
                    _stickContactMaterial.dynamicFriction = PluginCore.config.PuckIceFriction;
                    _stickContactMaterial.staticFriction = PluginCore.config.PuckIceFriction * 1.5f;
                }
                if (PluginCore.config.PuckStickContactBounciness >= 0f)
                    _stickContactMaterial.bounciness = PluginCore.config.PuckStickContactBounciness;
                puck.StickCollider.sharedMaterial = _stickContactMaterial;
            }

            int stickColId = puck.StickCollider.GetInstanceID();
            int iceColId = puck.IceCollider.GetInstanceID();
            if (!PluginCore.PuckIDs.Contains(stickColId)) PluginCore.PuckIDs.Add(stickColId);
            if (!PluginCore.PuckIDs.Contains(iceColId)) PluginCore.PuckIDs.Add(iceColId);

            if (CompetitiveAdjustments.BallModeHelper.IsBallModeEnabled)
                CompetitiveAdjustments.BallModeHelper.TransformPuckToBall(puck);

            if (PluginCore.config.EnableMidStickCollider) {
                foreach (Stick stick in UnityEngine.Object.FindObjectsByType<Stick>(FindObjectsSortMode.None)) {
                    Physics.IgnoreCollision(puck.StickCollider, stick.GetComponent<BoxCollider>());
                    Physics.IgnoreCollision(puck.IceCollider, stick.GetComponent<BoxCollider>());
                }
            }
        }

        /// <summary>
        /// Get the final per-axis puck scale Vector3 from synced client config
        /// (CompetitiveCompanion.PluginCore), or fall back to server config if the
        /// client config is not available. The uniform PuckScale is kept as a
        /// master multiplier on top of the per-axis PuckScaleX/Y/Z values, so
        /// final scale = PuckScale * (PuckScaleX, PuckScaleY, PuckScaleZ).
        /// This is the single source of truth for puck scale; every other
        /// application site reads through it so server and clients agree.
        /// </summary>
        public static UnityEngine.Vector3 GetSyncedPuckScaleVector() {
            try {
                // Try to get from synced client config, which receives updates from server via CMM
                var companionConfig = CompetitiveCompanion.PluginCore.config;
                if (companionConfig != null && companionConfig.PuckScale > 0.01f)
                    return ComposeScale(
                        companionConfig.PuckScale,
                        companionConfig.PuckScaleX,
                        companionConfig.PuckScaleY,
                        companionConfig.PuckScaleZ);
            }
            catch { }

            // Fall back to server config if client config is not available
            return ComposeScale(
                PluginCore.config.PuckScale,
                PluginCore.config.PuckScaleX,
                PluginCore.config.PuckScaleY,
                PluginCore.config.PuckScaleZ);
        }

        /// <summary>
        /// Combine the uniform master scale with the per-axis multipliers. A
        /// per-axis value at or below zero is treated as 1 so a half-populated
        /// or legacy config can never collapse the puck to a zero-size sliver.
        /// Per-axis scale applies in ball mode too, so the ball can be stretched
        /// into an ellipsoid; BallModeHelper resizes the sphere collider to track
        /// the squashed shape (see UpdateBallColliderRadius).
        /// </summary>
        private static UnityEngine.Vector3 ComposeScale(float uniform, float x, float y, float z) {
            if (x <= 0f) x = 1f;
            if (y <= 0f) y = 1f;
            if (z <= 0f) z = 1f;
            return new UnityEngine.Vector3(uniform * x, uniform * y, uniform * z);
        }
    }


    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public class HeightDragTweak {
        [HarmonyPostfix]
        public static void Postfix(Puck __instance) {
            if (!PluginCore.config.PuckDragSpeedDependence) return;
            float delta = __instance.Rigidbody.linearVelocity.magnitude - PluginCore.config.PuckNominalSpeed;
            float newDrag = PluginCore.config.PuckDrag * (1 + PluginCore.config.PuckDragFactor * delta * delta * delta);
            __instance.Rigidbody.linearDamping = Mathf.Max(PluginCore.config.PuckDrag, newDrag);

            if (PluginCore.config.PuckHeightDependentDrag) {
                if (__instance.Rigidbody.position.y > PluginCore.config.PuckHeightLimit && __instance.Rigidbody.linearVelocity.y > 0) {
                    float overheight = __instance.Rigidbody.position.y - PluginCore.config.PuckHeightLimit;
                    float heightDrag = PluginCore.config.PuckHeightDragFactor * overheight;
                    __instance.Rigidbody.AddForce(new UnityEngine.Vector3(0f, -heightDrag, 0f), ForceMode.VelocityChange);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkDespawn")]
    public class PuckDespawnPatch {
        [HarmonyPrefix]
        public static void Prefix(Puck __instance) {
            if (PluginCore.PuckIDs.Contains(__instance.StickCollider.GetInstanceID())) { PluginCore.PuckIDs.Remove(__instance.StickCollider.GetInstanceID()); }
            if (PluginCore.PuckIDs.Contains(__instance.IceCollider.GetInstanceID())) { PluginCore.PuckIDs.Remove(__instance.IceCollider.GetInstanceID()); }
            CompetitiveAdjustments.BallModeHelper.OnPuckDespawned(__instance);

            // PuckModifier integration — see its own comment on
            // ApplyPuckPhysics for why this exists at all.
            PuckPatch._originalStickColliderScale.Remove(__instance.GetInstanceID());
        }
    }
}