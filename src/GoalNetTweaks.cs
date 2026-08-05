using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace DashFallMod
{
    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
    public static partial class GoalNetTweaks
    {
        // Cached base localScale per goal (by instance ID), captured on first encounter.
        private static readonly Dictionary<int, Vector3> _goalBaseScale = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> _goalBasePosition = new Dictionary<int, Vector3>();

        // Cached base CapsuleCollider radii on Goal Post Collider.
        private static readonly Dictionary<int, float> _capsuleBaseRadius = new Dictionary<int, float>();
        private static readonly List<Collider> _scaledArenaBoundaryColliders = new List<Collider>();
        private static readonly Dictionary<int, Vector3> _arenaBoxColliderBaseSize = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> _arenaBoxColliderBaseCenter = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _arenaCapsuleColliderBaseRadius = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _arenaCapsuleColliderBaseHeight = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _arenaCapsuleColliderBaseCenter = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _arenaSphereColliderBaseRadius = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _arenaSphereColliderBaseCenter = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> _arenaMeshColliderBaseScale = new Dictionary<int, Vector3>();

        // Lazy server config load flag.
        private static bool _serverConfigLoaded;

        private static bool _runnerSpawned;
        private static GameObject _runnerObject;
        private static Vector3 _loggedGoalCompensation = Vector3.one;

        private static bool _hasSyncedTweaks;

        /// <summary>
        /// True after the client has received PPKB/GoalTweaks from a server
        /// that runs this mod.  Exposed for CompAdjustEffective in
        /// ConfigManager so it can fall back to "all disabled" on vanilla
        /// servers regardless of stale local synced values.
        /// Always false on the server itself; servers should not gate on this.
        /// </summary>
        public static bool HasSyncedTweaks => _hasSyncedTweaks;
        private static bool _syncedEnableGoalNetTweaks;
        private static float _syncedGoalThicknessScale = 1f;
        private static float _syncedGoalSizeScaleX = 1f;
        private static float _syncedGoalSizeScaleY = 1f;
        private static float _syncedGoalSizeScaleZ = 1f;
        private static float _syncedGoalBackOffset = 0f;
        private static bool _syncedEnableArenaTweaks;
        private static float _syncedArenaScaleX = 1f;
        private static float _syncedArenaScaleY = 1f;
        private static float _syncedArenaScaleZ = 1f;
        private static float _syncedArenaOffsetX = 0f;
        private static float _syncedArenaOffsetY = 0f;
        private static float _syncedArenaOffsetZ = 0f;
        private static bool _loggedArenaRootMissing;
        // Pairs populated by SyncArenaVisualAppearance; used by LiveSyncArenaSourceTextures
        // to propagate per-frame texture/color changes from the (hidden) source renderers to
        // our custom clones every tick, without touching smoothness or metallic.
        private static readonly List<(Renderer dst, Renderer src)> _arenaRendererPairs
            = new List<(Renderer, Renderer)>();
        private static Material _arenaColliderDebugMaterial;
        private static Mesh _debugCubeMesh;
        private static Mesh _debugSphereMesh;
        private static Mesh _debugCapsuleMesh;

        // Hash of the last-applied config values. When this matches the current values
        // the Runner skips the expensive FindObjectsByType / collider work entirely and
        // only runs the cheap LiveSyncArenaSourceTextures pass.
        private static int _lastRefreshHash;
        // "Re-apply the arena and goal geometry, the scene may have changed." This is NOT
        // "the config changed": see _lastArenaSyncHash for why the two must stay apart.
        private static bool _forceNextRefresh = true; // always run on first call

        // ── Event_CompetitiveAdjustments_OnArenaSync broadcast bookkeeping ───────
        // Deliberately separate from _forceNextRefresh. Subscribers treat every
        // broadcast as "the arena just changed" and rebuild state from it: oomtm450's
        // Ruleset clears its _barriersLowered latch on entry and re-derives the barrier
        // collider's ABSOLUTE world Y from the values in the message.
        // Broadcasting from the PlayerBodyV2 spawn hook therefore
        // reset a dedicated server's barrier every time anyone joined or respawned, and
        // any broadcast that landed while the rink was momentarily back at vanilla scale
        // (see ResetCapturedBaselines) parked the barrier at a height computed for a rink
        // nobody is playing on. That is the "the barrier collider disappears, respawning
        // brings it back" report.
        //
        // A player spawning is not an arena change, so it no longer broadcasts. This
        // fires when the numbers actually move, or when something genuinely invalidated a
        // subscriber's state: a level spawn, a fresh sync from the server, or an explicit
        // re-announce (RefreshOnPregame).
        private static int _lastArenaSyncHash;
        private static bool _hasBroadcastArenaSync;
        private static bool _forceArenaSyncBroadcast;

        private static int ComputeArenaSyncHash(
            bool arenaEnabled, float width, float height, float length,
            float offsetX, float offsetY, float offsetZ)
        {
            unchecked
            {
                int h = arenaEnabled.GetHashCode();
                h = h * 397 ^ width.GetHashCode();
                h = h * 397 ^ height.GetHashCode();
                h = h * 397 ^ length.GetHashCode();
                h = h * 397 ^ offsetX.GetHashCode();
                h = h * 397 ^ offsetY.GetHashCode();
                h = h * 397 ^ offsetZ.GetHashCode();
                return h;
            }
        }

        private static int ComputeRefreshHash(
            bool enabled, float thicknessScale, float scaleX, float scaleY, float scaleZ, float backOffset,
            bool arenaEnabled, float arenaScaleX, float arenaScaleY, float arenaScaleZ,
            float aOffX, float aOffY, float aOffZ)
        {
            unchecked
            {
                int h = enabled.GetHashCode();
                h = h * 397 ^ thicknessScale.GetHashCode();
                h = h * 397 ^ scaleX.GetHashCode();
                h = h * 397 ^ scaleY.GetHashCode();
                h = h * 397 ^ scaleZ.GetHashCode();
                h = h * 397 ^ backOffset.GetHashCode();
                h = h * 397 ^ arenaEnabled.GetHashCode();
                h = h * 397 ^ arenaScaleX.GetHashCode();
                h = h * 397 ^ arenaScaleY.GetHashCode();
                h = h * 397 ^ arenaScaleZ.GetHashCode();
                h = h * 397 ^ aOffX.GetHashCode();
                h = h * 397 ^ aOffY.GetHashCode();
                h = h * 397 ^ aOffZ.GetHashCode();
                return h;
            }
        }

        // Every synced geometry number funnels through SetSyncedTweaks, so this is
        // the one place worth sanitizing.  A NaN or infinite localScale does not
        // just look wrong, it permanently poisons the Transform (and everything
        // parented to it) for the rest of the process, and no later good value
        // recovers it.  Bounds are deliberately generous: the job here is to reject
        // values that break the session, not to second-guess an operator's rink.
        private const float MinSyncedScale = 0.05f;
        private const float MaxSyncedScale = 20f;
        private const float MaxSyncedOffset = 500f;

        // RefreshAll runs at 1 Hz forever, so a value that stays bad would otherwise
        // reprint its warning every second for the rest of the session.
        private static readonly HashSet<string> _sanitizeWarned = new HashSet<string>();

        private static void WarnSanitizeOnce(string name, float bad, string replacement)
        {
            if (!_sanitizeWarned.Add(name)) return;
            CompetitiveAdjustments.ConfigManager.LogWarning($"{name} was {bad}; using {replacement} instead.");
        }

        /// <param name="fromWire">
        /// True when the value arrived over PPKB/GoalTweaks. Wire values come from a peer
        /// we do not control and get the full range clamp. The operator's own config gets
        /// only the NaN/Infinity rejection: those genuinely cannot be applied (a non-finite
        /// localScale poisons the Transform and every child of it for the rest of the
        /// process), but a merely large number is a choice we have no business overruling,
        /// and quietly clamping it is how a rink changes size with no edit behind it.
        /// </param>
        private static float ResolveScale(bool fromWire, float v, float fallback, string name)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                WarnSanitizeOnce(name, v, fallback.ToString(CultureInfo.InvariantCulture));
                return fallback;
            }
            if (!fromWire) return v;
            if (v < MinSyncedScale) { WarnSanitizeOnce(name, v, MinSyncedScale.ToString(CultureInfo.InvariantCulture)); return MinSyncedScale; }
            if (v > MaxSyncedScale) { WarnSanitizeOnce(name, v, MaxSyncedScale.ToString(CultureInfo.InvariantCulture)); return MaxSyncedScale; }
            return v;
        }

        private static float ResolveOffset(bool fromWire, float v, string name)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                WarnSanitizeOnce(name, v, "0");
                return 0f;
            }
            if (!fromWire) return v;
            if (v < -MaxSyncedOffset) { WarnSanitizeOnce(name, v, (-MaxSyncedOffset).ToString(CultureInfo.InvariantCulture)); return -MaxSyncedOffset; }
            if (v > MaxSyncedOffset) { WarnSanitizeOnce(name, v, MaxSyncedOffset.ToString(CultureInfo.InvariantCulture)); return MaxSyncedOffset; }
            return v;
        }

        public static void SetSyncedTweaks(
            bool enabled,
            float thicknessScale,
            float scaleX,
            float scaleY,
            float scaleZ,
            float goalBackOffset,
            bool arenaEnabled,
            float arenaScaleX,
            float arenaScaleY,
            float arenaScaleZ,
            float arenaOffsetX,
            float arenaOffsetY,
            float arenaOffsetZ)
        {
            // Deliberately NOT sanitized here. This method also writes the values into
            // ConfigManager.Config.CompAdjust, and on a listen-server host it runs against
            // the host's OWN numbers: the host's client sends PPKB/Hello, the server
            // answers itself, and NGO short-circuits that into a local handler call. So
            // clamping here silently rewrote the operator's live config, changed their
            // rink mid-session, and got persisted to disk the next time the admin editor
            // saved. Sanitizing happens in RefreshAll instead, at the point of use, where
            // it is also possible to tell an untrusted wire value from the operator's own.
            _hasSyncedTweaks = true;
            _forceNextRefresh = true;
            // The authority just stated the arena, so subscribers get told even if the
            // numbers match what we already had: this is the first thing a client learns
            // after connecting, and a subscriber that loaded after our last broadcast has
            // no state at all yet.
            _forceArenaSyncBroadcast = true;
            _syncedEnableGoalNetTweaks = enabled;
            _syncedGoalThicknessScale = thicknessScale;
            _syncedGoalSizeScaleX = scaleX;
            _syncedGoalSizeScaleY = scaleY;
            _syncedGoalSizeScaleZ = scaleZ;
            _syncedGoalBackOffset = goalBackOffset;
            _syncedEnableArenaTweaks = arenaEnabled;
            // Mirror into config so the UI server tab and minimap coroutine read the synced
            // values. CLIENT ONLY: there the config is just a display mirror of what the
            // server said, so overwriting it is the point. On a server the config IS the
            // authority, and this method still runs there because a listen host answers its
            // own PPKB/Hello through NGO's loopback. Writing back then feeds the server its
            // own broadcast: harmless when the values match, destructive when they do not,
            // because the broadcast is built from CompAdjustEffective, which collapses to
            // all-defaults when EnableCompAdjust is off. That would replace the operator's
            // saved ArenaScale with 1.0 in the live config, and the next admin-editor save
            // would persist the loss to disk.
            var nmRole = NetworkManager.Singleton;
            var ca = (nmRole != null && nmRole.IsServer)
                ? null
                : CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;
            if (ca != null)
            {
                ca.EnableGoalNetTweaks = enabled;
                ca.EnableArenaTweaks = arenaEnabled;
                ca.ArenaScaleX = arenaScaleX;
                ca.ArenaScaleY = arenaScaleY;
                ca.ArenaScaleZ = arenaScaleZ;
            }
            _syncedArenaScaleX = arenaScaleX;
            _syncedArenaScaleY = arenaScaleY;
            _syncedArenaScaleZ = arenaScaleZ;
            _syncedArenaOffsetX = arenaOffsetX;
            _syncedArenaOffsetY = arenaOffsetY;
            _syncedArenaOffsetZ = arenaOffsetZ;
            EnsureRunner();
            RefreshAll();
            try { OnTweaksSynced?.Invoke(); } catch { }
        }

        // Fired whenever synced tweaks are received from the server (or applied locally).
        // Client-side systems (minimap, HUD) subscribe to re-apply dependent state.
        public static event Action OnTweaksSynced;

        public static void ClearSyncedTweaks()
        {
            _hasSyncedTweaks = false;
            _serverConfigLoaded = false;
            _forceNextRefresh = true;
        }

        /// <summary>
        /// Returns the arena's WORLD ground-plane scale (world X = rink width,
        /// world Z = rink length/depth) that should drive client-side floor-plane
        /// logic (minimap, face-off spawn spread, clip brushes, etc.) on the current
        /// connection.  World Y (vertical) is deliberately not returned: no caller
        /// scales a top-down/ground-plane quantity by the vertical axis.
        ///
        /// Axis names are world axes as of ConfigVersion 16, so world Z comes from
        /// ArenaScaleZ. Older configs had Y and Z swapped and are migrated on load.
        ///
        /// On a host/server we use the local config; on a client joined to a modded
        /// server we use the synced values; on a client joined to a vanilla server
        /// (no sync ever arrived) we return false so callers treat the rink as
        /// vanilla rather than applying the user's local config to a vanilla rink.
        /// </summary>
        public static bool TryGetEffectiveArenaScale(out float scaleX, out float scaleZ)
        {
            scaleX = 1f;
            scaleZ = 1f;
            var nm = NetworkManager.Singleton;
            if (nm == null) return false;

            if (nm.IsServer)
            {
                // Effective so EnableCompAdjust=false also returns vanilla here.
                var cfg = CompetitiveAdjustments.ConfigManager.CompAdjustEffective;
                if (cfg == null || !cfg.EnableArenaTweaks) return false;
                scaleX = cfg.ArenaScaleX > 0f ? cfg.ArenaScaleX : 1f;
                scaleZ = cfg.ArenaScaleZ > 0f ? cfg.ArenaScaleZ : 1f;
                return true;
            }

            if (!_hasSyncedTweaks || !_syncedEnableArenaTweaks) return false;
            scaleX = _syncedArenaScaleX > 0f ? _syncedArenaScaleX : 1f;
            scaleZ = _syncedArenaScaleZ > 0f ? _syncedArenaScaleZ : 1f;
            return true;
        }

        // SpawnFitInset and ArenaBaseScaleCorrection lived here. Both existed to reconcile
        // the bundled arena prefab with the base rink it stood in for, and both are gone
        // with it: the hybrid resize scales the REAL base geometry, so 1.0 is base size by
        // construction and there is nothing left to correct. The ServerConfig
        // SpawnFitInset field is kept so existing config files still load.

        /// <summary>
        /// Kept as a stable no-op. The faceoff PlayerPosition and puck PuckPosition
        /// markers all live under the 'Level Default' scene root, which the arena resize
        /// scales directly, so the game already reads scaled marker world positions when
        /// it spawns players and pucks.
        /// </summary>
        public static Vector3 ScaleSpawnPositionWithArena(Vector3 pos)
        {
            // No-op under the hybrid arena resize. The faceoff PlayerPosition and puck
            // PuckPosition markers all live under the 'Level Default' scene root, which the
            // arena resize scales directly, so the game already reads scaled marker world
            // positions when it spawns players/pucks. Re-scaling here would double it.
            // Kept as a stable no-op so the call sites (PlayerBodyPatch, SmallPatches warmup)
            // don't need to change; SpawnFitInset is likewise retired.
            return pos;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            // Re-apply geometry only. A goal can be respawned with the level, and the
            // re-apply is idempotent from the captured baselines, so it is cheap to redo.
            // It deliberately does NOT announce an arena change: nothing about the arena
            // moved because a player spawned, and telling subscribers otherwise is what
            // made the Ruleset re-place the barrier collider on every respawn.
            _forceNextRefresh = true;
            EnsureRunner();
            RefreshAll();
        }

        private sealed class Runner : MonoBehaviour
        {
            private float _nextRefreshAt;
            private void Update()
            {
                if (Time.unscaledTime < _nextRefreshAt) return;
                _nextRefreshAt = Time.unscaledTime + 1f;
                RefreshAll();
                // Retry / re-scan the arena proxy independently of the config hash, so a
                // failed early attempt does not latch the bundled prefab in for good and
                // late-appearing geometry still gets picked up.
                TickArenaProxyRescan();
                // ChunkSyncClient.Enable() is only invoked from
                // ApplyNetworkBoundsPatches, which early-returns once chunks
                // are active -- so a CMM-not-ready failure on the initial
                // Enable would otherwise never retry. Poll here at 1Hz; the
                // call is a fast no-op once registration succeeds.
                DashFallMod.Net.ChunkSyncClient.TickRegistrationRetry();
            }
        }

        /// <summary>
        /// Brings the 1 Hz arena/goal tick and the level-spawn hook online.
        ///
        /// Called from DashFallGameMod.OnEnable, NOT only from the player-spawn postfix.
        /// It used to be reachable only from there and from SetSyncedTweaks, which meant a
        /// dedicated server had no arena tick and no 'Event_Everyone_OnLevelSpawned'
        /// listener until the first player body spawned: the rink sat at vanilla size
        /// between a level load and the first spawn, and anything reading rink geometry in
        /// that window (the Ruleset's barrier placement above all) got numbers for a rink
        /// nobody is playing on. Arena state belongs to the level, not to the players
        /// standing on it.
        ///
        /// Idempotent and safe to call before there is a network session: RefreshAll
        /// no-ops when FindArenaRoot finds nothing, and the broadcast is gated on a live
        /// NetworkManager.
        /// </summary>
        internal static void EnsureRunner()
        {
            if (_runnerSpawned) return;
            try
            {
                var go = new GameObject("GoalNetTweaksRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Runner>();
                // Held so ShutdownRunner can destroy exactly this object rather than
                // going looking for it, which would miss an inactive one.
                _runnerObject = go;
                _runnerSpawned = true;
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("GoalNetTweaks runner spawn failed: " + ex.Message);
                return;
            }

            // Separate try: losing the level hook because the runner threw (or the other
            // way round) is how one of these silently goes missing for a whole session.
            try
            {
                EventManager.AddEventListener("Event_Everyone_OnLevelSpawned", OnLevelSpawnedResetBaselines);
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning(
                    "Could not subscribe to Event_Everyone_OnLevelSpawned: " + ex.Message);
            }
        }

        /// <summary>
        /// Undoes EnsureRunner: hands the arena back to vanilla, drops the level-spawn
        /// hook, and destroys the 1 Hz runner.
        ///
        /// The runner is DontDestroyOnLoad, so without this a disabled mod keeps a live
        /// GameObject rewriting the rink once a second for the rest of the process. On a
        /// CLIENT that self-corrects, because ServerBridge.Unhook clears the synced
        /// tweaks and the next tick takes the clientUnsynced teardown branch. On a server
        /// that branch is unreachable (it is gated on !IsServer), so the rink stayed
        /// resized, the base renderers stayed hidden, and NetworkBoundsPatch kept the
        /// chunked position encoding installed under its own Harmony id, which
        /// DashFallGameMod's UnpatchSelf cannot remove. Every client joining after that
        /// got no sync and ran vanilla encoding against a server that was not.
        ///
        /// Safe to call when the runner was never started.
        /// </summary>
        internal static void ShutdownRunner()
        {
            // Vanilla geometry first, while the statics this depends on are still intact.
            //
            // Goals BEFORE the arena, for two reasons. The goal pass is entirely separate
            // from SyncArenaVisuals: goal transform scale/position, the 'Goal Post
            // Collider' capsule radii and the frame part transforms are written by
            // SyncGoals, and RestoreGoalBaselines is the only thing that puts them back.
            // Restoring the rink alone left the server simulating oversized, displaced
            // goals on a vanilla-sized rink, permanently, because the level-spawn
            // listener that is the other route to RestoreGoalBaselines gets removed
            // below. And the order matters: SyncArenaVisuals(false) calls
            // ArenaProxyVisual.Clear(), which re-enables the batched frame renderers, so
            // restoring afterwards would fight a proxy group that was just disposed.
            try
            {
                RestoreGoalBaselines();
                _goalBaseScale.Clear();
                _goalBasePosition.Clear();
                _capsuleBaseRadius.Clear();
                _framePartBaseScale.Clear();
                _framePartBasePosition.Clear();
                _loggedFrameComposition.Clear();
                ResetGoalFrontPinLogState();
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("Goal teardown on shutdown failed: " + ex.Message);
            }

            try
            {
                SyncArenaVisuals(false, 1f, 1f, 1f, 0f, 0f, 0f);
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("Arena teardown on shutdown failed: " + ex.Message);
            }

            try
            {
                EventManager.RemoveEventListener("Event_Everyone_OnLevelSpawned", OnLevelSpawnedResetBaselines);
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning(
                    "Could not unsubscribe from Event_Everyone_OnLevelSpawned: " + ex.Message);
            }

            try
            {
                if (_runnerObject != null) UnityEngine.Object.Destroy(_runnerObject);
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("GoalNetTweaks runner teardown failed: " + ex.Message);
            }

            _runnerObject = null;
            _runnerSpawned = false;
            _hasSyncedTweaks = false;
            _forceNextRefresh = true;
        }

        private static void OnLevelSpawnedResetBaselines(Dictionary<string, object> _) => ResetCapturedBaselines();

        /// <summary>
        /// Drops every transform baseline this feature has captured, so a freshly spawned
        /// level starts from vanilla instead of inheriting the previous one's numbers.
        ///
        /// Nearly all of the resize is expressed as "target = captured baseline x config",
        /// which is only correct while the baseline really is the vanilla value. The
        /// baselines are keyed by instance id and survive a scene change, so anything that
        /// gets re-measured while the previous level's scale is still applied bakes that
        /// scale in permanently, and the geometry stops lining up from the SECOND join
        /// onwards while the first one looked fine. Keying on instance id is not enough on
        /// its own: ids are only unique among live objects, and the level root in
        /// particular is re-resolved by a name search that can land on a different node in
        /// a custom scene.
        /// </summary>
        internal static void ResetCapturedBaselines()
        {
            ArenaProxyVisual.Clear();
            RestoreAllStrandedScenery();

            // RESTORE BEFORE FORGETTING. Every one of these baselines is "the vanilla
            // value", and the next pass re-measures whatever it finds. If the object is
            // still alive and still carrying the previous resize when its baseline is
            // dropped, that resize becomes the new "vanilla" and the config multiplies on
            // top of it: a 1.25x rink re-measures as 1.25 and comes out 1.5625. Objects
            // that really did die are unaffected, since restoring skips them.
            RestoreLevelDefaultScale();
            RestoreGoalBaselines();

            _goalBaseScale.Clear();
            _goalBasePosition.Clear();
            _capsuleBaseRadius.Clear();
            _framePartBaseScale.Clear();
            _framePartBasePosition.Clear();
            _loggedFrameComposition.Clear();
            ResetGoalFrontPinLogState();

            ResetLevelRootBaseline();

            _forceNextRefresh = true;

            // A level spawn genuinely invalidates a subscriber's arena state, so this is
            // one of the few places that SHOULD re-announce.
            _forceArenaSyncBroadcast = true;

            CompetitiveAdjustments.ConfigManager.Log("Level spawned: restored and dropped captured arena/goal baselines.");

            // Re-apply in the SAME frame rather than waiting for the next Runner tick.
            // Everything above has just put the level root back to its VANILLA scale, and
            // deferring left the rink vanilla-sized for up to a second (indefinitely on a
            // dedicated server, where the Runner did not exist until a player spawned).
            // The barrier collider's world Y is derived from the rink by the Ruleset, so a
            // broadcast landing inside that window placed it for the wrong rink and only a
            // later refresh put it back.
            RefreshAll();
        }

        /// <summary>
        /// Puts every live goal back on its captured baseline: the goal transform, the post
        /// collider radii, and any frame part the thickness pass wrote to.
        /// </summary>
        private static void RestoreGoalBaselines()
        {
            foreach (var goal in UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None))
            {
                if (goal == null) continue;

                Transform t = goal.transform;
                int rootId = t.GetInstanceID();

                if (_goalBaseScale.TryGetValue(rootId, out Vector3 baseScale)) t.localScale = baseScale;
                if (_goalBasePosition.TryGetValue(rootId, out Vector3 basePos)) t.localPosition = basePos;

                var postColliders = t.Find("Goal Post Collider");
                if (postColliders != null)
                {
                    foreach (var cap in postColliders.GetComponents<CapsuleCollider>())
                    {
                        if (cap != null && _capsuleBaseRadius.TryGetValue(cap.GetInstanceID(), out float radius))
                            cap.radius = radius;
                    }
                }

                foreach (var child in t.GetComponentsInChildren<Transform>(true))
                {
                    if (child == null) continue;
                    int id = child.GetInstanceID();
                    if (_framePartBaseScale.TryGetValue(id, out Vector3 partScale)) child.localScale = partScale;
                    if (_framePartBasePosition.TryGetValue(id, out Vector3 partPos)) child.localPosition = partPos;
                }
            }
        }

        // ── Event_CompetitiveAdjustments_OnArenaSync payload helpers ─────────────
        // Both of these exist to make the message safe to consume as-is, so a correct
        // Ruleset build needs no compensating code.
        //
        // The full contract (authoritative axis mapping, the ordering guarantee, and the
        // three open Ruleset-side bugs) lived in RULESET_INTEROP.md until it was removed.
        // It is still in history if it is ever needed again:
        //     git log --all --diff-filter=D -- RULESET_INTEROP.md
        //     git show 299ba83:RULESET_INTEROP.md

        // The height axis is no longer clamped. It used to be pinned to 1.0 because
        // ScaleLevelDefaultRoot held collision height at vanilla, which made 1.0 simply
        // true, and because the subscriber multiplies its lowered-barrier constant
        // (-20.4) by this value: with a NEGATIVE constant, a taller arena drove the
        // barrier collider further DOWN instead of leaving it alone.
        //
        // ArenaScaleZ now scales the level root's world Y for real, so the boards, glass
        // and ceiling colliders genuinely grow. Reporting 1.0 became a lie about a rink
        // that is physically taller, and a subscriber cannot compensate for something it
        // is not told. Sending the true value is the correct contract; guarding the sign
        // on -20.4 is the subscriber's side of it.

        // Culture-proof the numbers. The subscriber reads every value as
        // kvp.Value.ToString() and parses it with CultureInfo.InvariantCulture. Box
        // a float and that ToString() uses the SERVER's culture, so a fr-FR host
        // emits "1,25", which invariant parsing reads as the group-separated 125:
        // a 100x arena. Formatting invariantly on this side makes the round trip
        // independent of the host's locale, and a string is what their parse path
        // wants anyway.
        private static object Interop(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static void RefreshAll(bool sendOnArenaSyncEvent = false)
        {
            // Lazy-load config the first time we run as server.
            if (!_serverConfigLoaded
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsServer)
            {
                _serverConfigLoaded = true;
                try
                {
                    CompetitiveAdjustments.ConfigManager.EnsureConfig();
                    CompetitiveAdjustments.ConfigManager.ReloadConfig();
                }
                catch (Exception ex)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Lazy config load failed: " + ex.Message);
                }
            }

            // Effective on the host path so EnableCompAdjust=false collapses
            // arena/goal scaling to vanilla without rewriting every field check.
            // Synced path is unaffected because the server's broadcaster also
            // reads CompAdjustEffective when sending PPKB/GoalTweaks.
            var cfg = CompetitiveAdjustments.ConfigManager.CompAdjustEffective;
            var nm = NetworkManager.Singleton;
            bool useSynced = _hasSyncedTweaks && (nm != null && !nm.IsServer);
            // Distinguish "I am the host, local config is truth" (legitimate) from
            // "I am a client and the server has not sent PPKB/GoalTweaks" (vanilla
            // server -- must NOT apply local config).  Without this both cases
            // collapse to useSynced=false and read potentially polluted local config.
            bool clientUnsynced = nm != null && !nm.IsServer && !_hasSyncedTweaks;

            bool enabled         = clientUnsynced ? false : (useSynced ? _syncedEnableGoalNetTweaks       : cfg.EnableGoalNetTweaks);
            // Mathf.Max does NOT filter NaN: Mathf.Max(0.1f, NaN) returns NaN, because the
            // comparison it is built on is false for NaN either way. Every one of these
            // ends up in a Transform, where a single non-finite value is unrecoverable, so
            // Resolve* runs first and the historical floors stay exactly as they were.
            float thicknessScale = Mathf.Max(0.05f, ResolveScale(useSynced, useSynced ? _syncedGoalThicknessScale : cfg.GoalThicknessScale, 1f, "GoalThicknessScale"));
            float scaleX         = Mathf.Max(0.1f,  ResolveScale(useSynced, useSynced ? _syncedGoalSizeScaleX     : cfg.GoalSizeScaleX,     1f, "GoalSizeScaleX"));
            float scaleY         = Mathf.Max(0.1f,  ResolveScale(useSynced, useSynced ? _syncedGoalSizeScaleY     : cfg.GoalSizeScaleY,     1f, "GoalSizeScaleY"));
            float scaleZ         = Mathf.Max(0.1f,  ResolveScale(useSynced, useSynced ? _syncedGoalSizeScaleZ     : cfg.GoalSizeScaleZ,     1f, "GoalSizeScaleZ"));
            float goalBackOffset = ResolveOffset(useSynced, useSynced ? _syncedGoalBackOffset : cfg.GoalBackOffset, "GoalBackOffset");
            // Custom-frame alignment knobs. Host-config driven for live tuning; once the
            // correct values are found they are baked as the ServerConfig defaults so
            // clients (which read the same defaults) get them without extra sync wiring.
            bool arenaEnabled    = clientUnsynced ? false : (useSynced ? _syncedEnableArenaTweaks  : cfg.EnableArenaTweaks);
            float arenaWidth     = Mathf.Max(0.1f, ResolveScale(useSynced, useSynced ? _syncedArenaScaleX : cfg.ArenaScaleX, 1f, "ArenaScaleX"));   // world X
            float arenaHeight    = Mathf.Max(0.1f, ResolveScale(useSynced, useSynced ? _syncedArenaScaleY : cfg.ArenaScaleY, 1f, "ArenaScaleY"));   // world Y
            float arenaLength    = Mathf.Max(0.1f, ResolveScale(useSynced, useSynced ? _syncedArenaScaleZ : cfg.ArenaScaleZ, 1f, "ArenaScaleZ"));   // world Z
            float arenaOffsetX   = ResolveOffset(useSynced, useSynced ? _syncedArenaOffsetX : cfg.ArenaOffsetX, "ArenaOffsetX");
            float arenaOffsetY   = ResolveOffset(useSynced, useSynced ? _syncedArenaOffsetY : cfg.ArenaOffsetY, "ArenaOffsetY");
            float arenaOffsetZ   = ResolveOffset(useSynced, useSynced ? _syncedArenaOffsetZ : cfg.ArenaOffsetZ, "ArenaOffsetZ");
            int currentHash = ComputeRefreshHash(
                enabled, thicknessScale, scaleX, scaleY, scaleZ, goalBackOffset,
                arenaEnabled, arenaWidth, arenaHeight, arenaLength,
                arenaOffsetX, arenaOffsetY, arenaOffsetZ);
            unchecked
            {
                // Client-local, so it is not in ComputeRefreshHash: fold it in or
                // switching the arena visual mode never triggers a rebuild.
                currentHash = currentHash * 397 ^ (int)ResolveArenaVisualMode();
            }

            // Two separate questions, and collapsing them is what tied arena state to
            // player spawns. "Did the numbers move?" decides whether subscribers are told
            // and whether the visual layer is torn down and rebuilt. "Should we re-apply?"
            // is the weaker one, and a player spawn only ever answers that one.
            bool configChanged = currentHash != _lastRefreshHash;
            bool reapply = _forceNextRefresh || configChanged;
            _forceNextRefresh = false;
            _lastRefreshHash = currentHash;

            // Always propagate live texture/color changes from hidden source renderers.
            LiveSyncArenaSourceTextures();

            // Skip expensive FindObjectsByType / collider work when nothing has changed.
            if (!reapply && !sendOnArenaSyncEvent && !_forceArenaSyncBroadcast) return;

            if (reapply)
            {
                // Rebuild the visual layer only when the numbers it was built from actually
                // changed. It used to run on every forced refresh, so every player spawn
                // tore down and rebuilt every proxy group, which costs a frame of
                // vanilla-sized geometry and a lighting flash for everyone already in game.
                if (configChanged) InvalidateVisualState();

                SyncArenaVisuals(
                    arenaEnabled,
                    arenaWidth,
                    arenaHeight,
                    arenaLength,
                    arenaOffsetX,
                    arenaOffsetY,
                    arenaOffsetZ);

                // The minimap normalises dots by UIMinimap.Bounds, which tracks the arena
                // scale, but it only re-applies on level spawn, on OnTweaksSynced, or from
                // the settings toggle. OnTweaksSynced fires when a CLIENT receives a
                // broadcast, so a host editing its own config and reloading never triggered
                // any of the three and the minimap stayed at the previous scale until the
                // next map.
                try { DashFallMod.Client.DashFallClientRunner.RefreshMinimap(); }
                catch (Exception ex) { CompetitiveAdjustments.ConfigManager.Dbg("Minimap refresh failed: " + ex.Message); }

                SyncGoals(enabled, thicknessScale, scaleX, scaleY, scaleZ, goalBackOffset,
                          arenaEnabled, arenaWidth, arenaHeight, arenaLength);
            }

            // LAST, once the rink is already at its final size. This used to run FIRST,
            // and the ordering is not cosmetic: subscribers turn these numbers into
            // ABSOLUTE WORLD positions, and the objects they write are CHILDREN of the
            // level root we are about to scale. The Ruleset's LowerBarriers is the case in
            // point -- it assigns 'Barrier Collider'.transform.position.y, and Unity stores
            // that as local = world / parentScale. Broadcast before ScaleLevelDefaultRoot
            // runs and the write lands against a rink still at vanilla scale, then gets
            // multiplied by the arena height scale a moment later, so the barrier ends up
            // nowhere near the boards. Worst on the level-spawn path, where the rink has
            // just been put BACK to vanilla before being re-applied.
            BroadcastArenaSyncIfNeeded(
                sendOnArenaSyncEvent, arenaEnabled,
                arenaWidth, arenaHeight, arenaLength,
                arenaOffsetX, arenaOffsetY, arenaOffsetZ);
        }

        /// <summary>
        /// Announces the arena to Event_CompetitiveAdjustments_OnArenaSync subscribers,
        /// but only when there is something new to tell them. See the _lastArenaSyncHash
        /// note for why a spurious announcement is worse than a missing one.
        /// </summary>
        private static void BroadcastArenaSyncIfNeeded(
            bool forceSend, bool arenaEnabled,
            float arenaWidth, float arenaHeight, float arenaLength,
            float arenaOffsetX, float arenaOffsetY, float arenaOffsetZ)
        {
            // No session means no authoritative arena, and the level does not exist yet.
            // Subscribers would derive positions from a rink that is not in the scene, and
            // the level-spawn re-announce covers them the moment one is.
            if (!forceSend && NetworkManager.Singleton == null) return;

            int syncHash = ComputeArenaSyncHash(
                arenaEnabled, arenaWidth, arenaHeight, arenaLength,
                arenaOffsetX, arenaOffsetY, arenaOffsetZ);

            bool changed = !_hasBroadcastArenaSync || syncHash != _lastArenaSyncHash;
            if (!forceSend && !_forceArenaSyncBroadcast && !changed) return;

            _forceArenaSyncBroadcast = false;
            _hasBroadcastArenaSync = true;
            _lastArenaSyncHash = syncHash;

            Dictionary<string, object> message;
            if (arenaEnabled) {
                // Cross-mod contract: oomtm450's Ruleset mod consumes this event and
                // multiplies its VANILLA offside/icing/goal-line positions (and the
                // lowered barrier height) by these values. Send the REAL, resized-rink
                // scale/offset -- NOT the internal barrier-collider factors
                // (barrierScaleX/Y/Z) used for our own barrier clone. Those 0.8 factors
                // would shrink the Ruleset's zone lines ~20% even at ArenaScale 1.0 (its
                // "== 1" early-out never fires), desyncing its lines from the actual rink.
                // Every key names the WORLD axis it scales. The old ArenaScaleX/Y/Z keys
                // are gone: they were named after config fields rather than axes, and
                // because Y and Z were swapped in that naming they read as "Z scales the
                // height", which is precisely the ambiguity that cost the first consumer
                // of this event an evening. Offsets keep their names because they were
                // world axes all along and were never swapped.
                message = new Dictionary<string, object> {
                    { "ArenaScaleWorldX", Interop(arenaWidth) },
                    { "ArenaScaleWorldY", Interop(arenaHeight) },
                    { "ArenaScaleWorldZ", Interop(arenaLength) },
                    { "ArenaOffsetX", Interop(arenaOffsetX) },
                    { "ArenaOffsetY", Interop(arenaOffsetY) },
                    { "ArenaOffsetZ", Interop(arenaOffsetZ) },
                };
            }
            else {
                message = new Dictionary<string, object> {
                    { "ArenaScaleWorldX", Interop(1f) },
                    { "ArenaScaleWorldY", Interop(1f) },
                    { "ArenaScaleWorldZ", Interop(1f) },
                    { "ArenaOffsetX", Interop(0f) },
                    { "ArenaOffsetY", Interop(0f) },
                    { "ArenaOffsetZ", Interop(0f) },
                };
            }

            EventManager.TriggerEvent("Event_CompetitiveAdjustments_OnArenaSync", message);
        }

        /// <summary>
        /// Re-applies goal size, back offset, post-collider thickness and the base-frame
        /// proxy to every goal in the scene, from each goal's captured vanilla baseline.
        /// Idempotent, so a goal that respawned with the level picks its state back up on
        /// the next pass without needing the config to have changed.
        /// </summary>
        /// <summary>
        /// Expresses a world-axis scale in a transform's own local axes.
        ///
        /// The goals are yawed (the prefab sits at 180 on one end), so "divide local X by
        /// the arena's world X" is only right when local X still points along world X. For
        /// a 90 degree yaw, local X points along world Z and the components have to swap.
        /// Projecting each local axis onto the world axes picks the correct component
        /// automatically, and for the axis-aligned yaws these goals actually use it is
        /// exact: one term is 1 and the rest are 0.
        ///
        /// A goal rotated off-axis cannot be compensated by a diagonal scale at all (it
        /// would need a shear), so this degrades to the nearest axis-aligned answer rather
        /// than pretending otherwise.
        /// </summary>
        private static Vector3 WorldScaleInLocalAxes(Quaternion localRotation, Vector3 worldScale)
        {
            Vector3 lx = localRotation * Vector3.right;
            Vector3 ly = localRotation * Vector3.up;
            Vector3 lz = localRotation * Vector3.forward;

            return new Vector3(
                Mathf.Abs(lx.x) * worldScale.x + Mathf.Abs(lx.y) * worldScale.y + Mathf.Abs(lx.z) * worldScale.z,
                Mathf.Abs(ly.x) * worldScale.x + Mathf.Abs(ly.y) * worldScale.y + Mathf.Abs(ly.z) * worldScale.z,
                Mathf.Abs(lz.x) * worldScale.x + Mathf.Abs(lz.y) * worldScale.y + Mathf.Abs(lz.z) * worldScale.z);
        }

        /// <summary>
        /// The inverse question to WorldScaleInLocalAxes: given a scale authored in a
        /// transform's LOCAL axes, as GoalSizeScaleX/Y/Z are, what factor does it apply
        /// along each WORLD axis?
        ///
        /// The goal-line correction is derived in world units while the goal's size knobs
        /// are local, and the two goals differ by a 180 degree yaw, so the two have to be
        /// related properly. For the axis-aligned yaws these goals actually use this is
        /// exact and reduces to the identity map; the general form keeps a 90 degree yaw
        /// honest instead of silently reading depth off the width knob.
        /// </summary>
        private static Vector3 LocalScaleInWorldAxes(Quaternion localRotation, Vector3 localScale)
        {
            Vector3 lx = localRotation * Vector3.right;
            Vector3 ly = localRotation * Vector3.up;
            Vector3 lz = localRotation * Vector3.forward;

            return new Vector3(
                Mathf.Abs(lx.x) * localScale.x + Mathf.Abs(ly.x) * localScale.y + Mathf.Abs(lz.x) * localScale.z,
                Mathf.Abs(lx.y) * localScale.x + Mathf.Abs(ly.y) * localScale.y + Mathf.Abs(lz.y) * localScale.z,
                Mathf.Abs(lx.z) * localScale.x + Mathf.Abs(ly.z) * localScale.y + Mathf.Abs(lz.z) * localScale.z);
        }

        private static readonly Dictionary<int, int> _loggedGoalFrontPin = new Dictionary<int, int>();

        /// <summary>
        /// Re-reported whenever the numbers behind the pin change, because the difference
        /// between "the pin ran and moved the net 0.58 m" and "the pin silently measured
        /// nothing" is invisible in game on a rink you have not seen at vanilla scale.
        /// </summary>
        private static void LogGoalFrontPinOnce(
            int rootId, string name, float frontOffsetZ, float sZ, float gZ, float worldShiftZ, float localShiftZ)
        {
            int signature;
            unchecked { signature = frontOffsetZ.GetHashCode() * 397 ^ sZ.GetHashCode() * 31 ^ gZ.GetHashCode(); }
            if (_loggedGoalFrontPin.TryGetValue(rootId, out int previous) && previous == signature) return;
            _loggedGoalFrontPin[rootId] = signature;

            CompetitiveAdjustments.ConfigManager.Log(
                $"Goal '{name}' front pinned to the goal line: f={frontOffsetZ:F4} arenaZ={sZ:F3} goalDepth={gZ:F3}" +
                $" -> world shift {worldShiftZ:F4} m, local shift {localShiftZ:F4} m.");
        }

        private static readonly HashSet<int> _loggedGoalPinSkipped = new HashSet<int>();

        /// <summary>
        /// Says so when the pin was wanted but could not be expressed, so the case reads
        /// differently in the log from "the pin ran" and from "the pin was never needed".
        /// </summary>
        private static void LogGoalPinSkippedOnce(int rootId, string name, Transform parent)
        {
            if (!_loggedGoalPinSkipped.Add(rootId)) return;

            CompetitiveAdjustments.ConfigManager.LogWarning(
                $"Goal '{name}' could not be pinned to the goal line: parent '{(parent != null ? parent.name : "none")}' " +
                $"has a degenerate world scale {(parent != null ? parent.lossyScale.ToString() : "n/a")}, so there is no " +
                "usable world-to-local map. The goal keeps its vanilla pivot placement.");
        }

        private static bool _warnedLevelRootBaselineOffset;

        /// <summary>
        /// The pin's derivation cancels ArenaOffset exactly, but not the level root's own
        /// vanilla POSITION: the painted ice is proxy-drawn as world*scale + offset, about
        /// world origin, while the colliders follow the hierarchy and scale about the root.
        /// Those two maps differ by basePos*(scale - 1), so on a scene where the root's
        /// vanilla position is non-zero the paint and the collision already disagree with
        /// each other and no single goal position is flush with both.
        ///
        /// It is (0,0,0) on this game, which is why the pin is correct as written. This says
        /// so out loud if a scene ever arrives where it is not, rather than leaving a silent
        /// assumption to be rediscovered from a misaligned net.
        /// </summary>
        private static void WarnLevelRootBaselineOffsetOnce()
        {
            if (_warnedLevelRootBaselineOffset) return;
            if (Mathf.Abs(_levelRootBasePos.x) < 0.001f
                && Mathf.Abs(_levelRootBasePos.y) < 0.001f
                && Mathf.Abs(_levelRootBasePos.z) < 0.001f) return;

            _warnedLevelRootBaselineOffset = true;
            CompetitiveAdjustments.ConfigManager.LogWarning(
                $"'Level Default' has a non-zero vanilla position {_levelRootBasePos}. The painted ice is " +
                "proxy-drawn about world origin while colliders follow the hierarchy, so paint and collision " +
                "already differ by that position times (scale - 1) on this scene. The goal is pinned to the " +
                "paint; expect a residual gap against the collision geometry.");
        }

        /// <summary>
        /// Drops the pin's log-suppression state alongside the transform baselines.
        ///
        /// Only the log state. The measured offset itself lives on each goal's
        /// ArenaBaselineMarker and is vanilla-relative, so it must survive exactly as
        /// BasePosition does, and _goalFrameLayer is a process-wide constant.
        /// </summary>
        private static void ResetGoalFrontPinLogState()
        {
            _loggedGoalFrontPin.Clear();
            _loggedGoalPinSkipped.Clear();
            _loggedFrontOffsetFailure.Clear();
            _loggedGoalTriggerParentage.Clear();
            _warnedLevelRootBaselineOffset = false;
        }

        private static readonly HashSet<int> _loggedGoalTriggerParentage = new HashSet<int>();

        /// <summary>
        /// Reports where the scoring volume sits relative to the goal, once per goal.
        ///
        /// Puck's GoalTrigger holds a private serialized Goal reference and resolves nothing
        /// at runtime, so parentage is scene data that cannot be read off the assembly, and
        /// the pin's justification is precisely that the drawn net and the scoring volume
        /// agree. If the trigger is a CHILD it translates rigidly with the pin and keeps its
        /// exact vanilla relationship to the post plane, which is the intent. If it is not,
        /// it rides the level root instead and its volume is stretched by the arena scale
        /// with nothing compensating it, which would need its own fix rather than a silent
        /// translation here. One log line answers it on the first run.
        /// </summary>
        private static void LogGoalTriggerParentageOnce(Goal goal)
        {
            if (goal == null || _loggedGoalTriggerParentage.Contains(goal.transform.GetInstanceID())) return;

            try
            {
                Transform goalRoot = goal.transform;
                int reported = 0;

                // Inactive included: a trigger disabled at the moment we look is exactly the
                // case where silence would be misread as "no trigger exists".
                foreach (var trigger in UnityEngine.Object.FindObjectsByType<GoalTrigger>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (trigger == null) continue;

                    Transform tt = trigger.transform;
                    bool child = tt == goalRoot || tt.IsChildOf(goalRoot);
                    if (!child && Vector3.Distance(tt.position, goalRoot.position) > 5f) continue;

                    reported++;
                    var collider = trigger.GetComponent<Collider>();
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"Goal trigger '{tt.name}' is {(child ? "a CHILD of" : "NOT a child of")} goal '{goal.name}': " +
                        $"world {tt.position} (post-pin), root '{tt.root.name}', bounds " +
                        (collider != null ? collider.bounds.ToString() : "none") + ".");
                }

                if (reported == 0)
                {
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"No GoalTrigger found on or within 5 m of goal '{goal.name}'. Scoring does not ride this " +
                        "goal's transform, so the goal-line pin does not move it.");
                }

                // Only after something was actually said, so a scan that ran too early
                // retries on the next pass instead of latching silence.
                _loggedGoalTriggerParentage.Add(goalRoot.GetInstanceID());
            }
            catch { }
        }

        /// <param name="arenaEnabled">
        /// Whether the level root is currently carrying the arena resize. The goals are
        /// children of that root, so a resized rink multiplies their world size for free.
        /// Their world POSITION should keep doing that (a bigger rink puts the goals
        /// further apart, which is the point), but their SIZE should not: a regulation net
        /// stays a regulation net on a 1.5x sheet. The arena factor is therefore divided
        /// back out of the goal's local scale, leaving GoalSizeScaleX/Y/Z as the only thing
        /// that resizes a goal.
        /// </param>
        // DO NOT try to force a Cloth rebuild by cycling the net GameObject with
        // SetActive(false/true). It was tried on 2026-08-02 and it destroys the net:
        // recreating the actor that way loses the constraint setup, so every vertex
        // free-simulates and the mesh tears itself into a tangle around the frame. It also
        // fired on the first sync after load, not just on a live edit, so it broke the
        // normal case as well as the one it was aimed at.
        //
        // The problem it was aimed at is real but minor: Cloth captures its rest state
        // when the actor is created, and an enabled-cycle reuses that actor, so a goal
        // scale changed LIVE leaves the solver at the old proportions and the net skews.
        // It is cosmetic, it only follows an admin edit, and the next level load clears it
        // because the goals are rebuilt from scratch. Leave it alone unless a rebuild
        // method is found that provably keeps the constraints.

        private static void SyncGoals(
            bool enabled, float thicknessScale,
            float scaleX, float scaleY, float scaleZ, float goalBackOffset,
            bool arenaEnabled, float arenaWidth, float arenaHeight, float arenaLength)
        {
            // The level root's multiplier over its own vanilla baseline is exactly
            // (width, height, length): ScaleLevelDefaultRoot writes
            // Vector3.Scale(baseline, (w,h,l)).
            bool compensateArena = arenaEnabled
                && _scaledLevelRoot != null
                && !ApproxEqual(new Vector3(arenaWidth, arenaHeight, arenaLength), Vector3.one);
            var arenaWorld = new Vector3(arenaWidth, arenaHeight, arenaLength);

            foreach (var goal in UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None))
            {
                if (goal == null) continue;

                // ── Visual / size scaling ─────────────────────────────────────────
                // Goal has NetworkObject but NO NetworkTransform, so localScale is not
                // network-synced and we can write it freely.
                // We only change the transform when the current scale differs from the
                // target to avoid repeatedly disrupting the cloth simulation.
                var t = goal.transform;
                int rootId = t.GetInstanceID();

                // Off the object, not off a live measurement, for the same reason the level
                // root is: a goal we have already scaled must never be re-measured as if it
                // were vanilla. The dictionaries stay as the lookup SyncBaseGoalFrame reads,
                // but the marker is what they are filled from.
                var goalMarker = ArenaBaselineMarker.Resolve(t, out _);
                if (goalMarker == null) continue;

                _goalBaseScale[rootId] = goalMarker.BaseScale;
                _goalBasePosition[rootId] = goalMarker.BasePosition;

                var baseScale = goalMarker.BaseScale;
                var basePosition = goalMarker.BasePosition;
                var targetScale = enabled
                    ? new Vector3(baseScale.x * scaleX, baseScale.y * scaleY, baseScale.z * scaleZ)
                    : baseScale;

                // Divide the arena resize back out, so the goal keeps its vanilla world
                // size (times GoalSizeScale) no matter how big the rink is. Only for goals
                // actually parented under the root we scaled: a goal somewhere else in the
                // scene never picked the factor up and must not be shrunk by it.
                //
                // This is scale only. localPosition is left alone on purpose, so the parent
                // scale still carries the goals outward as the rink grows, which is what
                // keeps them at the ends of the sheet.
                if (compensateArena && t.root == _scaledLevelRoot)
                {
                    Vector3 local = WorldScaleInLocalAxes(t.localRotation, arenaWorld);
                    if (Mathf.Abs(local.x) > 1e-4f) targetScale.x /= local.x;
                    if (Mathf.Abs(local.y) > 1e-4f) targetScale.y /= local.y;
                    if (Mathf.Abs(local.z) > 1e-4f) targetScale.z /= local.z;

                    // Logged on change, because the whole compensation hangs on the goals
                    // really being parented under the root we scale. If that ever stops
                    // being true this line stops appearing, which is the difference
                    // between "the feature is off" and "the feature silently does nothing".
                    if (!ApproxEqual(_loggedGoalCompensation, local))
                    {
                        _loggedGoalCompensation = local;
                        CompetitiveAdjustments.ConfigManager.Log(
                            $"Goal size held at vanilla against arena scale {arenaWorld}: dividing goal local scale by {local}.");
                    }
                }

                // ── Pin the mouth of the net to the painted goal line ─────────────
                // The line is baked into the ice, which the transform resize cannot move at
                // all, so it is redrawn by the proxy as world*scale + offset. A front plane
                // at vanilla world Z F therefore lands at F*s.
                //
                // The pivot lands at p*s, but the pivot-to-front vector f does NOT scale,
                // because the block above deliberately holds the goal at vanilla world size
                // times GoalSizeScale. So the mouth ends up at p*s + f*g while the paint is
                // at (p + f)*s, and the gap is exactly f*(s - g) world units. That is the
                // whole bug: the goal SPAWN position tracks the rink correctly and the front
                // of the frame does not.
                //
                // ArenaOffset translates the paint and the goal alike and cancels out. The
                // level root's vanilla POSITION does not cancel, because the paint scales
                // about world origin while the colliders scale about the root; it is
                // (0,0,0) on this game, and WarnLevelRootBaselineOffsetOnce says so out loud
                // rather than letting the assumption sit silent.
                Vector3 flushLocal = Vector3.zero;
                {
                    bool onScaledRoot = compensateArena && t.root == _scaledLevelRoot;
                    float sZ = onScaledRoot ? arenaLength : 1f;
                    float gZ = enabled
                        ? LocalScaleInWorldAxes(t.localRotation, new Vector3(scaleX, scaleY, scaleZ)).z
                        : 1f;

                    // Exactly zero when the rink and the goal are both at vanilla depth,
                    // which is the safety property that matters: on default config the
                    // measurement is never even attempted and targetPosition below is
                    // bit-identical to what it was before this block existed.
                    if (!Mathf.Approximately(sZ, gZ)
                        && TryGetGoalFrontOffsetZ(goal, goalMarker, out float frontOffsetZ))
                    {
                        if (onScaledRoot) WarnLevelRootBaselineOffsetOnce();

                        var worldShift = new Vector3(0f, 0f, frontOffsetZ * (sZ - gZ));

                        // localPosition is parent space. InverseTransformVector rather than a
                        // scalar divide, so an intermediate parent between the goal and the
                        // level root (which the t.root test above permits) is handled too.
                        Transform parent = t.parent;
                        bool applied = true;
                        if (parent == null)
                        {
                            flushLocal = worldShift;
                        }
                        else
                        {
                            Vector3 parentScale = parent.lossyScale;
                            if (Mathf.Abs(parentScale.x) > 1e-4f
                                && Mathf.Abs(parentScale.y) > 1e-4f
                                && Mathf.Abs(parentScale.z) > 1e-4f)
                            {
                                flushLocal = parent.InverseTransformVector(worldShift);
                            }
                            else
                            {
                                // Degenerate parent scale: no usable world-to-local map, so
                                // the goal stays where it is rather than being divided by
                                // something near zero.
                                applied = false;
                            }
                        }

                        if (applied)
                            LogGoalFrontPinOnce(rootId, goal.name, frontOffsetZ, sZ, gZ, worldShift.z, flushLocal.z);
                        else
                            LogGoalPinSkippedOnce(rootId, goal.name, parent);
                    }
                }

                // The pin first, so flush becomes the new zero point, then GoalBackOffset on
                // top as a deliberate deviation from it. The knob keeps its meaning.
                var targetPosition = basePosition + flushLocal;
                if (enabled && !Mathf.Approximately(goalBackOffset, 0f))
                {
                    var pushDir = new Vector3(basePosition.x, 0f, basePosition.z);
                    if (pushDir.sqrMagnitude < 0.0001f)
                    {
                        // The pin is already in t.localPosition from the previous pass, so it
                        // has to come back out here or this fallback feeds on its own output.
                        pushDir = new Vector3(t.localPosition.x - flushLocal.x, 0f, t.localPosition.z - flushLocal.z);
                    }

                    if (pushDir.sqrMagnitude < 0.0001f)
                    {
                        pushDir = new Vector3(t.forward.x, 0f, t.forward.z);
                    }

                    pushDir = pushDir.sqrMagnitude > 0.0001f ? pushDir.normalized : Vector3.forward;
                    targetPosition += pushDir * goalBackOffset;
                }

                bool scaleChanged = !ApproxEqual(t.localScale, targetScale);
                bool positionChanged = !ApproxEqual(t.localPosition, targetPosition);

                if (scaleChanged || positionChanged)
                {
                    // Disable cloth before changing the scale to prevent the simulation
                    // treating the transform change as a physics impulse and exploding.
                    // Re-enable immediately after; the cloth settles naturally.
                    var cloth = goal.NetCloth;
                    bool hadCloth = cloth != null && cloth.enabled;
                    if (hadCloth) cloth.enabled = false;

                    t.localPosition = targetPosition;
                    t.localScale = targetScale;

                    if (hadCloth) cloth.enabled = true;
                }

                // ── Post collider thickness ───────────────────────────────────────
                // Goal Post Collider holds 3 CapsuleColliders for the physical posts.
                var postColliderT = t.Find("Goal Post Collider");
                if (postColliderT != null)
                {
                    foreach (var cap in postColliderT.GetComponents<CapsuleCollider>())
                    {
                        int id = cap.GetInstanceID();
                        if (!_capsuleBaseRadius.ContainsKey(id))
                            _capsuleBaseRadius[id] = cap.radius;
                        cap.radius = enabled
                            ? _capsuleBaseRadius[id] * thicknessScale
                            : _capsuleBaseRadius[id];
                    }
                }

                // ── Goal frame ────────────────────────────────────────────────────
                // The base frame is statically batched, so it needs the same proxy
                // treatment as the rink; see GoalFrameTweaks.cs.
                SyncBaseGoalFrame(goal, ResolveArenaVisualMode(), enabled, thicknessScale);

                // The one thing the pin depends on that cannot be read from the assembly.
                LogGoalTriggerParentageOnce(goal);
            }
        }


        private static bool ApproxEqual(Vector3 a, Vector3 b)
            => Mathf.Approximately(a.x, b.x)
            && Mathf.Approximately(a.y, b.y)
            && Mathf.Approximately(a.z, b.z);

        private static void CopyColorPropertyIfPresent(Material src, Material dst, string prop)
        {
            if (src == null || dst == null) return;
            if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;
            dst.SetColor(prop, src.GetColor(prop));
        }

        private static void CopyTexturePropertyIfPresent(Material src, Material dst, string prop)
        {
            if (src == null || dst == null) return;
            if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;
            dst.SetTexture(prop, src.GetTexture(prop));
            dst.SetTextureOffset(prop, src.GetTextureOffset(prop));
            dst.SetTextureScale(prop, src.GetTextureScale(prop));
        }

        private static string GetRelativeTransformPath(Transform root, Transform target)
        {
            if (root == null || target == null) return string.Empty;
            if (root == target) return string.Empty;

            var parts = new List<string>();
            var current = target;

            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            if (current != root)
                return target.name ?? string.Empty;

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool ShouldHideOriginalArenaRenderer(Renderer renderer, Transform arenaRoot)
        {
            if (renderer == null || arenaRoot == null) return false;

            string path = GetRelativeTransformPath(arenaRoot, renderer.transform);
            string text = (renderer.name ?? string.Empty) + "/" + path;

            if (text.IndexOf("ceiling", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("crowd", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("stand", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("scoreboard", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (text.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("pillar", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("rafter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("support", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("rink", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

    }

    [HarmonyPatch(typeof(BaseGameMode<BaseGameModeConfig>), "OnGameStateChanged")]
    public static class RefreshOnPregame {
        [HarmonyPrefix]
        public static bool Prefix(GameState oldGameState, GameState newGameState) {
            try {
                if (oldGameState.Phase == newGameState.Phase)
                    return true;

                if (newGameState.Phase == GamePhase.PreGame)
                    GoalNetTweaks.RefreshAll(true);
            }
            catch (Exception ex) {
                Debug.LogError($"[COMPADJUST] GoalNetTweaks RefreshOnPregame error: {ex.Message}");
            }

            return true;
        }
    }
}
