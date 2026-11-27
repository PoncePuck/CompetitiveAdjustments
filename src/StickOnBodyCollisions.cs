using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CompetitivePuckTweaks.src {
    public class StickOnBodyCollisions {
        private const float STICK_FORCE_SOUND_THRESHOLD = 17.5f;
        public const int STICK_LAYER = 6;

        // How often the stick / body ignore pairs are re-applied from the stick tick, as a
        // safety net for spawn-order races (a body or role that was not registered yet when
        // the spawn / role-change hooks fired) and for pairs Unity drops when a collider is
        // disabled and enabled again.  The hooks do most of the work.
        private const float RECONCILE_INTERVAL_SECONDS = 0.5f;
        private static float _nextReconcileAt;

        // Property rather than `static readonly` so flipping the master flag
        // (EnableCompAdjust) or the per-feature flag (StickBodyCollision) at
        // runtime takes effect on the next patched call.  A field initializer
        // would freeze the value at class-init time, before the user has had
        // a chance to load their config edits.
        private static bool _disablePatch =>
            CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision != true;

        private static readonly LockDictionary<Rigidbody, LayerMask> _currentlyIgnoredStickCollisions = [];

        // ---- DisableGoalieStickBodyCollision -------------------------------------------
        //
        // StickBodyCollision opens the stick layer against the body layers for everybody, so
        // "goalie stick only" cannot be done with a layer rule.  It is done per collider pair
        // instead: every collider of a goalie's stick is told to ignore every collider of every
        // OTHER player's body.  The pairs we set are remembered so they can be undone (role
        // change, config flipped off) without touching pairs that other features set on purpose.

        private sealed class GoalieIgnoreState {
            public readonly List<Collider> Bodies = new List<Collider>();
            public int StickColliderCount;
        }

        private static readonly Dictionary<Stick, GoalieIgnoreState> _goalieIgnoredBodies = new Dictionary<Stick, GoalieIgnoreState>();

        private static bool GoalieStickIgnoreEnabled {
            get {
                var cfg = CompetitiveAdjustments.ConfigManager.CompAdjustEffective;
                return cfg != null && cfg.StickBodyCollision && cfg.DisableGoalieStickBodyCollision;
            }
        }

        /// <summary>
        /// Brings the goalie-stick ignore pairs in line with the current config, roles and
        /// spawned players.  Cheap when the feature is off and nothing was ever ignored.
        /// Safe to call from any hook, as often as needed.
        /// </summary>
        public static void RefreshGoalieStickBodyCollisions() {
            try {
                bool enabled = GoalieStickIgnoreEnabled;
                if (!enabled && _goalieIgnoredBodies.Count == 0)
                    return;

                if (PlayerManager.Instance == null)
                    return;

                // Forget sticks that were destroyed without going through the despawn hook.
                List<Stick> dead = null;
                foreach (var key in _goalieIgnoredBodies.Keys) {
                    if (key == null)
                        (dead ??= new List<Stick>()).Add(key);
                }
                if (dead != null) {
                    foreach (var key in dead)
                        _goalieIgnoredBodies.Remove(key);
                }

                foreach (Player player in PlayerManager.Instance.GetSpawnedPlayers()) {
                    if (player == null || player.PlayerBody == null)
                        continue;

                    Stick stick = player.PlayerBody.Stick;
                    if (stick == null)
                        continue;

                    if (enabled && player.Role == PlayerRole.Goalie)
                        IgnoreOtherPlayersBodies(stick);
                    else
                        RestoreStickBodyCollisions(stick);
                }
            }
            catch (Exception ex) {
                CompetitiveAdjustments.ConfigManager.LogError($"Error in {nameof(StickOnBodyCollisions)}.{nameof(RefreshGoalieStickBodyCollisions)}.\n{ex}");
            }
        }

        /// <summary>Event listener: a player's team / role / phase changed.</summary>
        public static void OnPlayerGameStateChanged(Dictionary<string, object> _) {
            RefreshStickBodyIgnores();
        }

        private static void IgnoreOtherPlayersBodies(Stick stick) {
            if (!_goalieIgnoredBodies.TryGetValue(stick, out var state)) {
                state = new GoalieIgnoreState();
                _goalieIgnoredBodies[stick] = state;
            }

            Collider[] stickCols = stick.GetComponentsInChildren<Collider>(true);
            state.StickColliderCount = stickCols.Length;
            state.Bodies.RemoveAll(c => c == null);

            PlayerBody ownBody = stick.PlayerBody;

            foreach (Player other in PlayerManager.Instance.GetSpawnedPlayers()) {
                if (other == null)
                    continue;

                PlayerBody otherBody = other.PlayerBody;
                if (otherBody == null || otherBody == ownBody)
                    continue;

                // Every pass re-applies every pair rather than skipping the ones already
                // recorded: Unity drops a pair when either collider is disabled and enabled
                // again, and ignoring a pair twice is harmless.  The list is only what
                // RestoreStickBodyCollisions has to undo.
                foreach (Collider bodyCol in StickPatch.CollectBodyColliders(otherBody)) {
                    if (bodyCol == null)
                        continue;

                    bool applied = false;
                    foreach (Collider stickCol in stickCols)
                        applied |= StickPatch.SetIgnore(stickCol, bodyCol, true);

                    if (applied && !state.Bodies.Contains(bodyCol))
                        state.Bodies.Add(bodyCol);
                }
            }
        }

        private static void RestoreStickBodyCollisions(Stick stick) {
            if (!_goalieIgnoredBodies.TryGetValue(stick, out var state))
                return;

            Collider[] stickCols = stick.GetComponentsInChildren<Collider>(true);
            foreach (Collider bodyCol in state.Bodies) {
                if (bodyCol == null)
                    continue;
                foreach (Collider stickCol in stickCols)
                    StickPatch.SetIgnore(stickCol, bodyCol, false);
            }

            _goalieIgnoredBodies.Remove(stick);
        }

        /// <summary>
        /// Single entry point for every hook: re-arms each stick's ignore of its OWN body
        /// (StickBodyCollision) and applies / undoes the goalie-stick rule
        /// (DisableGoalieStickBodyCollision).  Cheap, idempotent, safe to call often.
        /// </summary>
        public static void RefreshStickBodyIgnores() {
            try {
                if (CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision == true && PlayerManager.Instance != null) {
                    foreach (Player player in PlayerManager.Instance.GetSpawnedPlayers()) {
                        if (player == null || player.PlayerBody == null)
                            continue;
                        StickPatch.UnapplySelfStickOnBodyCollisions(player.PlayerBody.Stick);
                    }
                }
            }
            catch (Exception ex) {
                CompetitiveAdjustments.ConfigManager.LogError($"Error in {nameof(StickOnBodyCollisions)}.{nameof(RefreshStickBodyIgnores)}.\n{ex}");
            }

            RefreshGoalieStickBodyCollisions();
        }

        [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
        public class Stick_OnNetworkPostSpawn_BodyIgnore_Patch {
            [HarmonyPostfix]
            public static void Postfix() {
                RefreshStickBodyIgnores();
            }
        }

        [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
        public class PlayerBodyV2_OnNetworkPostSpawn_BodyIgnore_Patch {
            [HarmonyPostfix]
            public static void Postfix() {
                RefreshStickBodyIgnores();
            }
        }

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

                if (Time.time >= _nextReconcileAt) {
                    _nextReconcileAt = Time.time + RECONCILE_INTERVAL_SECONDS;
                    RefreshStickBodyIgnores();
                }

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

                    if (stickToBodyDistance > 2.675f) {
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
                _goalieIgnoredBodies.Remove(__instance);
                return true;
            }
        }
    }
}
