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
        // collider's ABSOLUTE world Y from the values in the message (see
        // RULESET_INTEROP.md). Broadcasting from the PlayerBodyV2 spawn hook therefore
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
            // Mirror into config so the UI server tab and minimap coroutine read the synced values
            var ca = CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;
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
        // See RULESET_INTEROP.md. Both of these exist to make the message safe to
        // consume as-is, so a correct Ruleset build needs no compensating code.

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
            float thicknessScale = Mathf.Max(0.05f, useSynced ? _syncedGoalThicknessScale : cfg.GoalThicknessScale);
            float scaleX         = Mathf.Max(0.1f,  useSynced ? _syncedGoalSizeScaleX        : cfg.GoalSizeScaleX);
            float scaleY         = Mathf.Max(0.1f,  useSynced ? _syncedGoalSizeScaleY        : cfg.GoalSizeScaleY);
            float scaleZ         = Mathf.Max(0.1f,  useSynced ? _syncedGoalSizeScaleZ        : cfg.GoalSizeScaleZ);
            float goalBackOffset = useSynced ? _syncedGoalBackOffset : cfg.GoalBackOffset;
            // Custom-frame alignment knobs. Host-config driven for live tuning; once the
            // correct values are found they are baked as the ServerConfig defaults so
            // clients (which read the same defaults) get them without extra sync wiring.
            bool arenaEnabled    = clientUnsynced ? false : (useSynced ? _syncedEnableArenaTweaks  : cfg.EnableArenaTweaks);
            float arenaWidth     = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleX : cfg.ArenaScaleX);   // world X
            float arenaHeight    = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleY : cfg.ArenaScaleY);   // world Y
            float arenaLength    = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleZ : cfg.ArenaScaleZ);   // world Z
            float arenaOffsetX   = useSynced ? _syncedArenaOffsetX : cfg.ArenaOffsetX;
            float arenaOffsetY   = useSynced ? _syncedArenaOffsetY : cfg.ArenaOffsetY;
            float arenaOffsetZ   = useSynced ? _syncedArenaOffsetZ : cfg.ArenaOffsetZ;
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

                SyncGoals(enabled, thicknessScale, scaleX, scaleY, scaleZ, goalBackOffset);
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
        private static void SyncGoals(
            bool enabled, float thicknessScale,
            float scaleX, float scaleY, float scaleZ, float goalBackOffset)
        {
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

                var targetPosition = basePosition;
                if (enabled && !Mathf.Approximately(goalBackOffset, 0f))
                {
                    var pushDir = new Vector3(basePosition.x, 0f, basePosition.z);
                    if (pushDir.sqrMagnitude < 0.0001f)
                    {
                        pushDir = new Vector3(t.localPosition.x, 0f, t.localPosition.z);
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
