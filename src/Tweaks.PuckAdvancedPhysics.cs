// Tweaks.PuckAdvancedPhysics.cs
// PuckModifier integration, continued from Tweaks.PuckPatch.cs's ApplyPuckPhysics.
//
// Everything in THIS file is the per-tick / per-contact half of that
// integration — gravity shaping, air drag, incidental-contact dampening, and
// the vertical launch cap — as opposed to the one-time-per-spawn properties
// ApplyPuckPhysics sets. Each mechanic here is ported from PuckModifier's own
// working implementation, not written fresh, specifically because that mod's
// own iteration history already found and fixed real bugs in each one (an
// exploitable fixed launch ceiling, a proportional-scaling version of the
// same cap that could collapse a puck's horizontal speed to near zero in
// under two tenths of a second, a gravity/ice-friction coupling that made
// flight tuning silently affect glide) — reinventing these from the CompTweaks
// description alone would very plausibly reintroduce one of them.
//
// Every mechanic here is gated on its own CompTweaksConfig field sitting at
// its documented no-op default (see each field's own comment in
// ServerConfig.cs) — a server that has never imported a PuckModifier preset
// sees zero behavior change from this file existing at all.

using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;

namespace CompetitivePuckTweaks.src {
    /// <summary>
    /// Attaches PuckAdvancedPhysicsBehaviour to every puck on spawn — a
    /// SEPARATE Harmony postfix on the same method PuckPatch already patches
    /// (OnNetworkPostSpawn), rather than folding this into PuckPatch.Postfix
    /// itself, so this file's integration stays self-contained and does not
    /// need to touch Tweaks.PuckPatch.cs beyond what ApplyPuckPhysics already
    /// needed. Harmony supports multiple postfixes on one method; order
    /// between this and PuckPatch's own postfix does not matter here, since
    /// neither reads anything the other sets.
    /// </summary>
    [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
    public class PuckAdvancedPhysicsAttachPatch {
        [HarmonyPostfix]
        public static void Postfix(Puck __instance) {
            if (__instance == null) return;
            if (__instance.gameObject.GetComponent<PuckAdvancedPhysicsBehaviour>() == null)
                __instance.gameObject.AddComponent<PuckAdvancedPhysicsBehaviour>();
        }
    }

    /// <summary>
    /// Gravity shaping — a multiplier on Physics.gravity, applied only while
    /// the puck is airborne, ramped in by height above the puck's own last-
    /// grounded position rather than applied uniformly the instant IsGrounded
    /// goes false.
    ///
    /// GROUNDED GATE. Without this, AddForce here would ALSO apply while the
    /// puck rests on the ice — a downward force that increases the effective
    /// normal force against the ice, which increases friction deceleration
    /// via F=mu*N, entirely independent of PuckIceFriction. Every increase to
    /// PuckGravityMultiplier for flight tuning would silently also make the
    /// puck die faster on the ice, an unintended coupling between two
    /// settings that should be fully independent.
    ///
    /// TRAJECTORY GATE. Without this, the SAME multiplier applies whether the
    /// puck is a low pass that hopped a centimetre off a rut or a genuinely
    /// lofted, hanging chip — both are simply "not grounded", with no
    /// distinction between them. Ramped by height above THIS puck's own last-
    /// grounded position: near the ice the effective multiplier eases toward
    /// 1 (untouched, natural arc); well above it, toward the full configured
    /// value.
    /// </summary>
    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public class PuckGravityShapingPatch {
        // Per-puck "where was the ice under this puck last" baseline, keyed
        // by instance id. Self-calibrating rather than a hardcoded world-space
        // ice Y — works the same regardless of rink level or arena rescale
        // (CA's own ArenaTweaks feature), since it is built from wherever
        // THIS puck actually was the last time it was genuinely grounded.
        private static readonly LockDictionary<int, float> _lastGroundedY = new LockDictionary<int, float>();

        internal static void ForgetPuck(Puck puck) {
            if (puck == null) return;
            _lastGroundedY.Remove(puck.GetInstanceID());
        }

        [HarmonyPostfix]
        public static void Postfix(Puck __instance) {
            try { Apply(__instance); }
            catch (Exception ex) {
                CompetitiveAdjustments.ConfigManager.LogWarning($"PuckModifier gravity shaping failed: {ex.Message}");
            }
        }

        private static void Apply(Puck puck) {
            if (puck == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var rb = puck.Rigidbody;
            if (rb == null) return;

            int id = puck.GetInstanceID();
            if (puck.IsGrounded) _lastGroundedY[id] = puck.transform.position.y;

            float multiplier = PluginCore.config.PuckGravityMultiplier;

            // At 1.0 hand gravity back to PhysX, which integrates it per
            // substep and is strictly more accurate than anything applied
            // from here. Only take over when the value is actually modified.
            if (Mathf.Approximately(multiplier, 1f)) {
                if (!rb.useGravity) rb.useGravity = true;
                return;
            }

            if (puck.IsGrounded) {
                if (!rb.useGravity) rb.useGravity = true;
                return;
            }

            float lastGroundY;
            float heightAboveIce = _lastGroundedY.TryGetValue(id, out lastGroundY)
                ? Mathf.Max(0f, puck.transform.position.y - lastGroundY)
                : 0f;

            float passH = PluginCore.config.PuckGravityPassHeightThreshold;
            float loftH = Mathf.Max(passH + 0.01f, PluginCore.config.PuckGravityLoftHeightThreshold);

            float t = Mathf.Clamp01((heightAboveIce - passH) / (loftH - passH));
            float trajectoryFactor = t * t * (3f - 2f * t);

            float effectiveMultiplier = Mathf.Lerp(1f, multiplier, trajectoryFactor);

            if (rb.useGravity) rb.useGravity = false;
            rb.AddForce(Physics.gravity * effectiveMultiplier, ForceMode.Acceleration);
        }
    }

    /// <summary>
    /// Quadratic (v^2) air resistance, applied only while airborne, closed-
    /// form integrated so it can never reverse or overshoot a puck's velocity
    /// regardless of tick rate. Kept separate from PuckDrag/PuckDragSpeedDependence
    /// (Tweaks.PuckPatch.cs's HeightDragTweak): that pair is linear-in-speed
    /// and applies everywhere including on the ice; this is v^2 and airborne
    /// only, a different physical model for a different purpose. Running both
    /// is fine — they stack rather than conflict, since HeightDragTweak
    /// writes Rigidbody.linearDamping and this writes velocity directly.
    /// </summary>
    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public class PuckAirDragPatch {
        [HarmonyPostfix]
        public static void Postfix(Puck __instance) {
            try { Apply(__instance); }
            catch (Exception ex) {
                CompetitiveAdjustments.ConfigManager.LogWarning($"PuckModifier air drag failed: {ex.Message}");
            }
        }

        private static void Apply(Puck puck) {
            if (puck == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            float k = PluginCore.config.PuckAirDrag;
            if (k <= 0f) return;

            var rb = puck.Rigidbody;
            if (rb == null || rb.isKinematic) return;
            if (puck.IsGrounded) return;

            Vector3 velocity = rb.linearVelocity;
            float speed = velocity.magnitude;
            if (speed < 0.01f) return;

            Vector3 direction = velocity / speed;

            // A puck presents its edge when spinning flat and travelling
            // in-plane, and its face when tumbled flat-side-forward — the
            // face gets FlatFaceDragFactor times the drag.
            float faceFactor = PluginCore.config.PuckFlatFaceDragFactor;
            if (!Mathf.Approximately(faceFactor, 1f)) {
                float faceOn = Mathf.Abs(Vector3.Dot(direction, puck.transform.up));
                k *= Mathf.Lerp(1f, faceFactor, faceOn);
            }

            // Closed form: v(t) = v0 / (1 + k*v0*t). Exact and unconditionally
            // stable — explicit Euler on quadratic drag can overshoot and
            // reverse a puck's direction at entirely reachable speeds and
            // tick rates; this cannot.
            float dt = Time.fixedDeltaTime;
            float newSpeed = speed / (1f + k * speed * dt);

            rb.linearVelocity = direction * newSpeed;
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkDespawn")]
    public class PuckAdvancedPhysicsDespawnPatch {
        [HarmonyPrefix]
        public static void Prefix(Puck __instance) {
            PuckGravityShapingPatch.ForgetPuck(__instance);
        }
    }

    /// <summary>
    /// Incidental-contact dampening and the vertical launch cap — both live
    /// on this MonoBehaviour, not a Harmony patch, for the same reason
    /// PuckModifier's own PuckContactDampener is one: OnCollisionEnter/Stay
    /// need to actually be callbacks Unity invokes on this GameObject, not
    /// something reachable by patching a method on Puck itself.
    /// </summary>
    public sealed class PuckAdvancedPhysicsBehaviour : MonoBehaviour {
        private Rigidbody _rb;
        private Puck _puck;
        private Vector3 _velocityBeforeStep;

        // Updated continuously in FixedUpdate whenever the puck is actually
        // touching the ice — NOT reset per contact episode. Anchoring to a
        // per-episode start instead still lets an unlimited launch through
        // via repeated separate taps, each its own Enter-to-Exit episode —
        // confirmed by simulation in PuckModifier's own development: six taps
        // against an episode-scoped anchor reached 30 m/s against a 5 m/s cap.
        private Vector3 _velocityAtLastGrounded;

        // Re-rolled on the SAME lifecycle as _velocityAtLastGrounded above —
        // continuously while grounded, frozen the moment the puck leaves the
        // ice. A perfectly deterministic fixed ceiling turned into its own
        // exploit once players found it: whatever motion just cleared the
        // fixed value produced exactly the same arc every time. Randomizing
        // which fraction of the configured maximum applies for THIS flight,
        // but locking that choice in at takeoff rather than re-rolling every
        // tick, keeps one attempt internally consistent while varying attempt
        // to attempt.
        private float _effectiveMaxVerticalThisFlight;

        private void Awake() {
            _rb = GetComponent<Rigidbody>();
            _puck = GetComponent<Puck>();
        }

        private void FixedUpdate() {
            // Not present in PuckModifier's own version of this method, which
            // this was ported from — that one runs unguarded on every peer,
            // reading a kinematic (server-authoritative) rigidbody's velocity
            // on clients for no effect. Harmless there since the actual
            // gameplay-affecting logic (the collision handlers below) are
            // already server-gated — but cheap to guard here too, so this
            // does zero work at all on a client rather than merely zero
            // EFFECT.
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            if (_rb != null) _velocityBeforeStep = _rb.linearVelocity;
            if (_puck != null && _puck.IsGrounded) {
                _velocityAtLastGrounded = _velocityBeforeStep;
                float maxVertical = PluginCore.config.PuckMaxLaunchVerticalSpeed;
                _effectiveMaxVerticalThisFlight = maxVertical * UnityEngine.Random.Range(0.7f, 1.0f);
            }
        }

        private void OnCollisionEnter(Collision collision) {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (_rb == null) return;
            Stick stick;
            if (!collision.gameObject.TryGetComponent<Stick>(out stick)) return;

            ApplyIncidentalDampening(collision);
            ApplyVerticalLaunchCap();
        }

        // Re-evaluated on every tick a contact continues, not just once at
        // OnCollisionEnter — active stickhandling is mostly sustained
        // contact across many ticks, not a single instant touch, so running
        // this once only would leave most of what a player actually feels
        // while dangling completely undampened however gentle that specific
        // tick's motion is.
        private void OnCollisionStay(Collision collision) {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (_rb == null) return;
            Stick stick;
            if (!collision.gameObject.TryGetComponent<Stick>(out stick)) return;

            ApplyIncidentalDampening(collision);
            ApplyVerticalLaunchCap();
        }

        /// <summary>
        /// Scales down THIS TICK's velocity change if the current contact
        /// reads as an incidental brush rather than a deliberate hit. 0 =
        /// feature off (PuckMinDeliberateContactSpeed's own no-op default),
        /// in which case keepFraction stays 1 and this is a no-op write.
        /// </summary>
        private void ApplyIncidentalDampening(Collision collision) {
            float minDeliberate = PluginCore.config.PuckMinDeliberateContactSpeed;
            float keepFraction = 1f;

            if (minDeliberate > 0f) {
                // NOT relativeVelocity alone — that is the DIFFERENCE between
                // stick and puck speed, not either one's absolute speed. A
                // hard, deliberate redirect onto an already-moving puck can
                // have the stick swinging fast in roughly the SAME direction
                // the puck is already travelling, producing a LOW relative
                // velocity despite both moving fast — exactly backwards for
                // what should be the clearest case of a deliberate contact.
                // Taking the stick's own absolute speed too, whichever of the
                // two is higher, fixes that.
                float relativeSpeed = collision.relativeVelocity.magnitude;
                float stickSpeed = collision.rigidbody != null
                    ? collision.rigidbody.linearVelocity.magnitude
                    : 0f;
                float contactSpeed = Mathf.Max(relativeSpeed, stickSpeed);

                if (contactSpeed < minDeliberate) {
                    // Ramped, not a hard cutoff — a knife-edge here would make
                    // a contact right at the threshold decisive purely from
                    // tiny timing/speed noise.
                    float t = Mathf.Clamp01(contactSpeed / minDeliberate);
                    float smoothT = t * t * (3f - 2f * t);
                    keepFraction = Mathf.Lerp(PluginCore.config.PuckIncidentalContactDampening, 1f, smoothT);
                }
            }

            Vector3 velocityChange = _rb.linearVelocity - _velocityBeforeStep;
            _rb.linearVelocity = _velocityBeforeStep + (velocityChange * keepFraction);
        }

        /// <summary>
        /// PROPORTIONAL, not a Y-only clamp — clamping only Y and leaving
        /// horizontal untouched does not reduce the "energy" of a launch
        /// attempt, it just redirects where that energy goes, which reads as
        /// the puck shooting flat across the sky along whatever horizontal
        /// axis it already had speed on.
        ///
        /// Anchored to velocity the last time the puck was GROUNDED, not to
        /// velocity at the start of the current contact episode — see
        /// _velocityAtLastGrounded's own comment for the repeated-tap exploit
        /// that anchoring per-episode allowed.
        ///
        /// Only THIS TICK's own contribution gets scaled, never everything
        /// accumulated since last-grounded — scaling the whole accumulated
        /// change whenever the ceiling was exceeded seemed right and kept the
        /// repeated-tap fix working, but meant any later contact during a
        /// long flight, however gentle, that nudged vy even slightly over the
        /// ceiling would proportionally slash ALL of the puck's horizontal
        /// velocity too, including speed built up long before this specific
        /// touch. Confirmed by simulation during PuckModifier's own
        /// development: a sustained multi-tick contact compounded that
        /// reduction tick after tick, collapsing 35 m/s of horizontal speed
        /// to under 3 in eight ticks (0.16 seconds).
        ///
        /// 0 = feature off (PuckMaxLaunchVerticalSpeed's own no-op default).
        /// </summary>
        private void ApplyVerticalLaunchCap() {
            float maxVertical = _effectiveMaxVerticalThisFlight;
            if (maxVertical <= 0f) return;

            float ceiling = _velocityAtLastGrounded.y + maxVertical;
            if (_rb.linearVelocity.y <= ceiling) return;

            Vector3 tickDelta = _rb.linearVelocity - _velocityBeforeStep;

            if (tickDelta.y > 0.0001f) {
                float scale = Mathf.Clamp01((ceiling - _velocityBeforeStep.y) / tickDelta.y);
                _rb.linearVelocity = _velocityBeforeStep + tickDelta * scale;
            }
            else {
                _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, ceiling, _rb.linearVelocity.z);
            }

            if (_rb.linearVelocity.y > ceiling) {
                _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, ceiling, _rb.linearVelocity.z);
            }
        }
    }
}
