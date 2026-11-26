// ShotOnBodyFatigue.cs
// A puck that hits a player's body costs that player stamina.
//
// Blocking a shot with your body should hurt. When a puck collides with a skater's
// body (torso, groin or head) above a minimum speed, the player loses stamina, and
// the amount grows a little with how fast the puck was going:
//
//   puck speed below MinPuckSpeed   ->  nothing, a pass or a dump-in is not a block
//   MinPuckSpeed .. MaxPuckSpeed    ->  StaminaAtMinSpeed rising linearly to StaminaAtMaxSpeed
//   puck speed above MaxPuckSpeed   ->  StaminaAtMaxSpeed (the amount stops growing)
//
// "Puck speed" is the speed the puck had going INTO the contact, not the relative
// speed between puck and body. A skater running into a puck that is barely moving
// does not pay for it; a slap shot into a skater standing still does.
//
// Server only. Stamina is a server-written NetworkVariable, so clients just see the
// new value replicate and nothing here needs to be synced.
//
// Sticks never count: the puck has to hit the body. Goalies are left out by default,
// since a save would otherwise be a stamina cost on every shot they face.
//
// The collision callback lives on a MonoBehaviour attached to each puck rather than
// on a Harmony patch, for the same reason PuckAdvancedPhysicsBehaviour is one:
// OnCollisionEnter has to be a callback Unity invokes on the puck's own GameObject.
//
// Config lives in CompTweaksConfig (see the field block at the bottom of this file).

using System;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace CompetitivePuckTweaks.src {
    public static class ShotOnBodyFatigue {
        // One puck striking one skater usually reports several contacts in the same
        // physics step (torso and groin, or torso and head), and every one of them
        // would read the same pre-impact speed. Without this the same block would be
        // charged two or three times.
        internal const float SameBodyCooldownSeconds = 0.4f;

        private static CompetitiveAdjustments.CompTweaksConfig Cfg =>
            CompetitiveAdjustments.ConfigManager.CompTweaksEffective;

        internal static bool Enabled {
            get {
                var cfg = Cfg;
                return cfg != null && cfg.EnableShotOnBodyFatigue;
            }
        }

        /// <summary>
        /// How much stamina a puck moving at <paramref name="puckSpeed"/> costs a player
        /// it hits. 0 below the minimum speed. Pure function of the speed and the config,
        /// so it can be called from anywhere to preview a value.
        /// </summary>
        public static float ComputeDrain(float puckSpeed, CompetitiveAdjustments.CompTweaksConfig cfg) {
            if (cfg == null || float.IsNaN(puckSpeed))
                return 0f;

            // Sanitised at the point of use. Mathf.Max does not filter NaN (the comparison
            // it is built on is false for NaN either way), so Finite() runs first.
            float min = Mathf.Max(0f, cfg.ShotOnBodyFatigueMinPuckSpeed);
            float max = cfg.ShotOnBodyFatigueMaxPuckSpeed;
            if (max <= min)
                max = min + 0.01f;

            float atMin = Mathf.Max(0f, cfg.ShotOnBodyFatigueStaminaAtMinSpeed);
            float atMax = Mathf.Max(0f, cfg.ShotOnBodyFatigueStaminaAtMaxSpeed);

            if (puckSpeed < min) return 0f;

            // InverseLerp clamps to [0, 1], so a puck faster than MaxPuckSpeed costs
            // exactly StaminaAtMaxSpeed instead of extrapolating past it.
            float t = Mathf.InverseLerp(min, max, puckSpeed);
            return Mathf.Lerp(atMin, atMax, t);
        }

        /// <summary>
        /// Takes the stamina off <paramref name="body"/> for a puck hitting it at
        /// <paramref name="puckSpeed"/>. Returns true if any was taken.
        /// </summary>
        internal static bool TryDrain(PlayerBodyV2 body, float puckSpeed, CompetitiveAdjustments.CompTweaksConfig cfg) {
            if (body == null || cfg == null) return false;

            var player = body.Player;
            if (player == null) return false;

            // Replay bodies are puppets of a recorded game, not players with stamina.
            if (player.IsReplay.Value) return false;

            if (player.Role == PlayerRole.Goalie && !cfg.ShotOnBodyFatigueAffectGoalies) return false;

            float drain = ComputeDrain(puckSpeed, cfg);
            if (drain <= 0f) return false;

            float before = body.Stamina.Value;
            body.Stamina.Value = Mathf.Max(0f, before - drain);

            if (cfg.ShotOnBodyFatigueDropsPlayerOnLowStam && puckSpeed >= cfg.ShotOnBodyFatigueLowStamMinShotSpeedThreshold && body.Stamina.Value < cfg.ShotOnBodyFatigueLowStamThreshold) {
                if (!player.PlayerBody.HasSlipped && !player.PlayerBody.HasFallen.Value)
                    player.PlayerBody.OnSlip();
            }

            CompetitiveAdjustments.ConfigManager.Dbg(
                $"ShotOnBodyFatigue: puck at {puckSpeed:F1} m/s hit {player.Username.Value}, " +
                $"stamina {before:F3} -> {body.Stamina.Value:F3} (-{drain:F3})");
            return true;
        }

        /// <summary>
        /// The player body a puck collision was with, or null if it was anything else
        /// (ice, boards, goal, a stick, another puck).
        /// </summary>
        internal static PlayerBodyV2 FindBody(Collision collision) {
            Collider other = collision.collider;
            if (other == null) return null;

            // The stick is its own object, not part of the body. Rule it out first so a
            // stick that ever ends up parented near a body cannot be mistaken for one.
            if (other.GetComponentInParent<Stick>() != null) return null;

            // The torso, groin and head colliders can sit on child objects of the body.
            // Unity reports the Rigidbody they belong to, which is the body's own, so
            // this finds it without depending on how deep the collider is.
            Rigidbody rb = collision.rigidbody;
            if (rb != null) {
                var viaRigidbody = rb.GetComponent<PlayerBodyV2>();
                if (viaRigidbody != null) return viaRigidbody;
            }

            return other.GetComponentInParent<PlayerBodyV2>();
        }

        /// <summary>
        /// Puts the collision listener on a puck. Server only, since that is the only
        /// place the stamina can be written. Safe to call repeatedly.
        /// </summary>
        public static void EnsureAttached(Puck puck) {
            if (puck == null)
                return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return;

            if (puck.gameObject.GetComponent<ShotOnBodyFatigueBehaviour>() == null)
                puck.gameObject.AddComponent<ShotOnBodyFatigueBehaviour>();
        }

        // Attached to every puck on spawn, whether or not the feature is on. The flag is
        // read when a collision happens, so turning the feature on in a live server needs
        // no respawn.
        [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
        public static class Puck_OnNetworkPostSpawn_Patch {
            [HarmonyPostfix]
            public static void Postfix(Puck __instance) {
                try { EnsureAttached(__instance); }
                catch (Exception ex) {
                    CompetitiveAdjustments.ConfigManager.LogWarning($"ShotOnBodyFatigue attach failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Listens for the puck hitting a player's body and hands the hit to
    /// <see cref="ShotOnBodyFatigue"/>.
    /// </summary>
    public sealed class ShotOnBodyFatigueBehaviour : MonoBehaviour {
        private Puck _puck;

        // Puck.Speed as it was entering this physics step. FixedUpdate runs before the
        // physics step and OnCollisionEnter after it, so reading the speed inside the
        // callback risks measuring the shot AFTER the body took the speed off it.
        // Sampled here instead, so the value is the one the puck arrived with.
        private float _speedBeforeStep;

        // Time of the last charge against each body, by body instance id.
        private readonly LockDictionary<int, float> _lastChargeAt = new LockDictionary<int, float>();

        private void Awake() {
            _puck = GetComponent<Puck>();
        }

        private void OnDisable() {
            _lastChargeAt.Clear();
        }

        private void FixedUpdate() {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            if (_puck != null)
                _speedBeforeStep = _puck.Speed;
        }

        private void OnCollisionEnter(Collision collision) {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!ShotOnBodyFatigue.Enabled) return;

            try {
                PlayerBodyV2 body = ShotOnBodyFatigue.FindBody(collision);
                if (body == null)
                    return;

                int bodyId = body.GetInstanceID();
                float now = Time.time;
                if (_lastChargeAt.TryGetValue(bodyId, out float last) &&
                    now - last < ShotOnBodyFatigue.SameBodyCooldownSeconds)
                    return;

                float puckSpeed = _speedBeforeStep;
                var cfg = CompetitiveAdjustments.ConfigManager.CompTweaksEffective;

                if (ShotOnBodyFatigue.TryDrain(body, puckSpeed, cfg))
                    _lastChargeAt[bodyId] = now;
            }
            catch (Exception ex) {
                CompetitiveAdjustments.ConfigManager.LogWarning($"ShotOnBodyFatigue collision failed: {ex.Message}");
            }
        }
    }
}
