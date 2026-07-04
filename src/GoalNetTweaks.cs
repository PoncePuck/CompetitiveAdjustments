using System;
using System.Collections.Generic;
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

        // ── Goal frame bundle ─────────────────────────────────────────────────
        private static bool        _bundleLoadAttempted;
        private static string      _loadedBundlePath;
        private static long        _loadedBundleWriteTicksUtc;
        private static GameObject  _framePrefab;
        private static GameObject  _arenaPrefab;
        private static GameObject  _collidersPrefab;
        private static GameObject  _arenaAndCollidersPrefab;
        private static GameObject  _arenaInstance;
        private static GameObject  _collidersInstance;
        // Rounded barrier collision is a standalone object under the (unscaled) rink root,
        // NOT a child of the scaled arena instance, so a non-uniform arena scale cannot
        // shear it. Tracked here so it can be torn down with the arena.
        private static Transform   _barrierStandalone;
        private static readonly List<Collider> _disabledOriginalColliders = new List<Collider>();
        private static readonly Dictionary<int, GameObject> _goalFrames = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, List<Renderer>> _hiddenGoalRenderers = new Dictionary<int, List<Renderer>>();
        private static readonly List<Renderer> _hiddenArenaRenderers = new List<Renderer>();
        private static int _hiddenArenaRootId;

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
        private static float _syncedArenaRotX = 0f;
        private static float _syncedArenaRotY = 0f;
        private static float _syncedArenaRotZ = 0f;
        private static bool _loggedArenaPrefabMissing;
        private static bool _loggedArenaRootMissing;
        private static bool _loggedArenaSpawned;
        private static bool _loggedArenaColliderFallback;
        private static bool _usingArenaVisualColliderFallback;
        private static bool _loggedArenaRendererMatches;
        private static bool _arenaAppearanceSynced;
        private static readonly HashSet<int> _syncedGoalFrameAppearances = new HashSet<int>();
        // Pairs populated by SyncArenaVisualAppearance; used by LiveSyncArenaSourceTextures
        // to propagate per-frame texture/color changes from the (hidden) source renderers to
        // our custom clones every tick, without touching smoothness or metallic.
        private static readonly List<(Renderer dst, Renderer src)> _arenaRendererPairs
            = new List<(Renderer, Renderer)>();
        private static int _arenaRootInstanceId;
        private static Material _arenaColliderDebugMaterial;
        private static Mesh _debugCubeMesh;
        private static Mesh _debugSphereMesh;
        private static Mesh _debugCapsuleMesh;
        private static bool _colliderLayersSynced;

        // Hash of the last-applied config values. When this matches the current values
        // the Runner skips the expensive FindObjectsByType / collider work entirely and
        // only runs the cheap LiveSyncArenaSourceTextures pass.
        private static int _lastRefreshHash;
        private static bool _forceNextRefresh = true; // always run on first call

        private static int ComputeRefreshHash(
            bool enabled, float thicknessScale, float scaleX, float scaleY, float scaleZ, float backOffset,
            bool arenaEnabled, float arenaScaleX, float arenaScaleY, float arenaScaleZ,
            float aOffX, float aOffY, float aOffZ, float aRotX, float aRotY, float aRotZ)
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
                h = h * 397 ^ aRotX.GetHashCode();
                h = h * 397 ^ aRotY.GetHashCode();
                h = h * 397 ^ aRotZ.GetHashCode();
                return h;
            }
        }

        private static string GetPreferredBundlePath(string modDir)
        {
            string bundlePathGoalframe = Path.Combine(modDir, "assets", "goalframe");
            // Check both capitalisation variants — Linux filesystems are case-sensitive
            // and the build copies the bundle as "compassets" (all lowercase).
            string bundlePathCompAssets = Path.Combine(modDir, "assets", "CompAssets");
            if (!File.Exists(bundlePathCompAssets))
                bundlePathCompAssets = Path.Combine(modDir, "assets", "compassets");

            bool hasGoalframe = File.Exists(bundlePathGoalframe);
            bool hasCompAssets = File.Exists(bundlePathCompAssets);

            if (!hasGoalframe && !hasCompAssets)
                return null;
            if (hasGoalframe && !hasCompAssets)
                return bundlePathGoalframe;
            if (!hasGoalframe && hasCompAssets)
                return bundlePathCompAssets;

            DateTime goalframeTime;
            DateTime compAssetsTime;
            try { goalframeTime = File.GetLastWriteTimeUtc(bundlePathGoalframe); }
            catch { goalframeTime = DateTime.MinValue; }
            try { compAssetsTime = File.GetLastWriteTimeUtc(bundlePathCompAssets); }
            catch { compAssetsTime = DateTime.MinValue; }

            return compAssetsTime >= goalframeTime ? bundlePathCompAssets : bundlePathGoalframe;
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
            float arenaOffsetZ,
            float arenaRotX,
            float arenaRotY,
            float arenaRotZ)
        {
            _hasSyncedTweaks = true;
            _forceNextRefresh = true;
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
            _syncedArenaRotX = arenaRotX;
            _syncedArenaRotY = arenaRotY;
            _syncedArenaRotZ = arenaRotZ;
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
        /// IMPORTANT axis mapping: the arena prefab is laid flat with a 90-degree X
        /// rotation (ArenaRotX defaults to 90), which rotates the prefab's local Y
        /// onto world Z and local Z onto world Y.  So the config's ArenaScaleY scales
        /// the world-Z (length) axis and ArenaScaleZ scales world-Y (vertical).  That
        /// is why the world-Z scale below is sourced from ArenaScaleY, NOT ArenaScaleZ.
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
                // world-Z (length) = config ArenaScaleY due to the 90deg arena rotation.
                scaleZ = cfg.ArenaScaleY > 0f ? cfg.ArenaScaleY : 1f;
                return true;
            }

            if (!_hasSyncedTweaks || !_syncedEnableArenaTweaks) return false;
            scaleX = _syncedArenaScaleX > 0f ? _syncedArenaScaleX : 1f;
            // world-Z (length) = config ArenaScaleY due to the 90deg arena rotation.
            scaleZ = _syncedArenaScaleY > 0f ? _syncedArenaScaleY : 1f;
            return true;
        }

        // Extra inset applied on top of the arena scale so spawned players/pucks sit
        // comfortably inside the rink. The custom arena prefab is a little larger than
        // the vanilla rink the spawn/puck markers were authored against, so the raw
        // proportional spread lands a touch too wide without this.
        private const float SpawnFitInset = 1.0f;

        // b1117 enlarged the base rink ~1.5x (horizontal plane only) vs the bundled
        // arena prefab. Hardcoded correction so ArenaScale 1.0 == base-game size.
        // Applied to the arena's two HORIZONTAL axes (width + length) and to the
        // minimap and spawn scaling so they all track together; the vertical/height
        // axis (config ArenaScaleZ -> world Y) is deliberately excluded.
        public const float ArenaBaseScaleCorrection = 1.5f;

        /// <summary>
        /// Maps a vanilla spawn position (player body or puck) to the equivalent spot
        /// in the arena-scaled rink. Scales world X/Z about the rink centre (world
        /// origin) by the effective arena scale, with a small inset (see
        /// <see cref="SpawnFitInset"/>). Y is left unchanged so things stay on the ice.
        /// Returns the position unchanged when arena tweaks are inactive (vanilla rink
        /// or a vanilla server), so those cases are untouched.
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
            _forceNextRefresh = true; // each player spawn must re-apply arena/goal state
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
                // ChunkSyncClient.Enable() is only invoked from
                // ApplyNetworkBoundsPatches, which early-returns once chunks
                // are active -- so a CMM-not-ready failure on the initial
                // Enable would otherwise never retry. Poll here at 1Hz; the
                // call is a fast no-op once registration succeeds.
                DashFallMod.Net.ChunkSyncClient.TickRegistrationRetry();
            }
        }

        private static void EnsureRunner()
        {
            if (_runnerSpawned) return;
            _runnerSpawned = true;
            try
            {
                var go = new GameObject("GoalNetTweaksRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Runner>();
            }
            catch { _runnerSpawned = false; }
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
            float frameRotX = cfg.GoalFrameRotX, frameRotY = cfg.GoalFrameRotY, frameRotZ = cfg.GoalFrameRotZ;
            float frameOffX = cfg.GoalFrameOffsetX, frameOffY = cfg.GoalFrameOffsetY, frameOffZ = cfg.GoalFrameOffsetZ;
            float frameScale = Mathf.Max(0.01f, cfg.GoalFrameScale);
            bool arenaEnabled    = clientUnsynced ? false : (useSynced ? _syncedEnableArenaTweaks  : cfg.EnableArenaTweaks);
            float arenaScaleX    = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleX : cfg.ArenaScaleX);
            float arenaScaleY    = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleY : cfg.ArenaScaleY);
            float arenaScaleZ    = Mathf.Max(0.1f, useSynced ? _syncedArenaScaleZ : cfg.ArenaScaleZ);
            float arenaOffsetX   = useSynced ? _syncedArenaOffsetX : cfg.ArenaOffsetX;
            float arenaOffsetY   = useSynced ? _syncedArenaOffsetY : cfg.ArenaOffsetY;
            float arenaOffsetZ   = useSynced ? _syncedArenaOffsetZ : cfg.ArenaOffsetZ;
            float arenaRotX      = useSynced ? _syncedArenaRotX : cfg.ArenaRotX;
            float arenaRotY      = useSynced ? _syncedArenaRotY : cfg.ArenaRotY;
            float arenaRotZ      = useSynced ? _syncedArenaRotZ : cfg.ArenaRotZ;
            int currentHash = ComputeRefreshHash(
                enabled, thicknessScale, scaleX, scaleY, scaleZ, goalBackOffset,
                arenaEnabled, arenaScaleX, arenaScaleY, arenaScaleZ,
                arenaOffsetX, arenaOffsetY, arenaOffsetZ, arenaRotX, arenaRotY, arenaRotZ);
            unchecked
            {
                // Fold the frame knobs in so tuning them via /reload triggers a refresh.
                currentHash = currentHash * 397 ^ frameRotX.GetHashCode();
                currentHash = currentHash * 397 ^ frameRotY.GetHashCode();
                currentHash = currentHash * 397 ^ frameRotZ.GetHashCode();
                currentHash = currentHash * 397 ^ frameOffX.GetHashCode();
                currentHash = currentHash * 397 ^ frameOffY.GetHashCode();
                currentHash = currentHash * 397 ^ frameOffZ.GetHashCode();
                currentHash = currentHash * 397 ^ frameScale.GetHashCode();
            }

            bool configChanged = _forceNextRefresh || currentHash != _lastRefreshHash;
            _forceNextRefresh = false;
            _lastRefreshHash = currentHash;

            // Always propagate live texture/color changes from hidden source renderers.
            LiveSyncArenaSourceTextures();

            // Skip expensive FindObjectsByType / collider work when nothing has changed.
            if (!configChanged && !sendOnArenaSyncEvent) return;

            const float barrierScaleX = 0.8f, barrierScaleY = 1f, barrierScaleZ = 0.8f;

            Dictionary<string, object> message;
            if (arenaEnabled) {
                message = new Dictionary<string, object> {
                    { "ArenaScaleX", arenaScaleX * barrierScaleX },
                    { "ArenaScaleY", arenaScaleY * barrierScaleZ },
                    { "ArenaScaleZ", arenaScaleZ * barrierScaleY },
                    { "ArenaOffsetX", arenaOffsetX },
                    { "ArenaOffsetY", arenaOffsetY },
                    { "ArenaOffsetZ", arenaOffsetZ },
                };
            }
            else {
                message = new Dictionary<string, object> {
                    { "ArenaScaleX", 1f },
                    { "ArenaScaleY", 1f },
                    { "ArenaScaleZ", 1f },
                    { "ArenaOffsetX", 0 },
                    { "ArenaOffsetY", 0 },
                    { "ArenaOffsetZ", 0 },
                };
            }

            EventManager.TriggerEvent("Event_CompetitiveAdjustments_OnArenaSync", message);

            // Skip expensive FindObjectsByType / collider work when nothing has changed.
            if (!configChanged) return;

            SyncArenaVisuals(
                arenaEnabled,
                arenaScaleX,
                arenaScaleY,
                arenaScaleZ,
                arenaOffsetX,
                arenaOffsetY,
                arenaOffsetZ,
                arenaRotX,
                arenaRotY,
                arenaRotZ,
                barrierScaleX,
                barrierScaleY,
                barrierScaleZ);

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

                if (!_goalBaseScale.ContainsKey(rootId))
                    _goalBaseScale[rootId] = t.localScale;
                if (!_goalBasePosition.ContainsKey(rootId))
                    _goalBasePosition[rootId] = t.localPosition;

                var baseScale = _goalBaseScale[rootId];
                var basePosition = _goalBasePosition[rootId];
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

                // ── Custom goal frame ─────────────────────────────────────────────
                TryLoadFrameBundle();

                _goalFrames.TryGetValue(rootId, out var frameObj);

                if (enabled && _framePrefab != null)
                {
                    if (frameObj == null)
                    {
                        frameObj = UnityEngine.Object.Instantiate(_framePrefab);
                        frameObj.name = "CustomGoalFrame";
                        frameObj.transform.SetParent(t, worldPositionStays: false);
                        frameObj.transform.localPosition = Vector3.zero;
                        frameObj.transform.localRotation = Quaternion.identity;
                        frameObj.transform.localScale = new Vector3(100f, 100f, 100f);

                        SyncCustomFrameAppearance(goal, frameObj.transform);
                        _syncedGoalFrameAppearances.Add(rootId);
                        DisableCustomFrameNetPieces(frameObj);
                        _goalFrames[rootId] = frameObj;
                    }
                    else if (!_syncedGoalFrameAppearances.Contains(rootId))
                    {
                        SyncCustomFrameAppearance(goal, frameObj.transform);
                        _syncedGoalFrameAppearances.Add(rootId);
                    }

                    // Apply the live-tunable frame alignment every refresh so /reload
                    // updates orientation/position/scale without recreating the frame.
                    // The prefab mesh does not match the b1117 goal, so these knobs line
                    // it up (RotY defaults to 180; scale defaults to the 100x Blender-unit
                    // correction).
                    frameObj.transform.localRotation = Quaternion.Euler(frameRotX, frameRotY, frameRotZ);
                    frameObj.transform.localPosition = new Vector3(frameOffX, frameOffY, frameOffZ);
                    frameObj.transform.localScale = new Vector3(frameScale, frameScale, frameScale);

                    HideOriginalGoalFrameRenderers(goal, rootId, frameObj.transform);
                }
                else if (!enabled && frameObj != null)
                {
                    UnityEngine.Object.Destroy(frameObj);
                    _goalFrames.Remove(rootId);
                    _syncedGoalFrameAppearances.Remove(rootId);
                    RestoreOriginalGoalFrameRenderers(rootId);
                }
                else if (!enabled)
                {
                    RestoreOriginalGoalFrameRenderers(rootId);
                }
            }
        }

        // ── Goal frame bundle loader ──────────────────────────────────────────
        private static void TryLoadFrameBundle()
        {
            if (_bundleLoadAttempted) return;
            _bundleLoadAttempted = true;
            try
            {
                string modDir     = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string bundlePath = GetPreferredBundlePath(modDir);

                AssetBundle bundle = null;

                if (bundlePath != null)
                {
                    Debug.Log($"[COMPADJUST] Loading arena bundle from: {bundlePath}");
                    _loadedBundlePath = bundlePath;
                    try
                    {
                        _loadedBundleWriteTicksUtc = File.GetLastWriteTimeUtc(bundlePath).Ticks;
                    }
                    catch
                    {
                        _loadedBundleWriteTicksUtc = 0;
                    }
                    bundle = AssetBundle.LoadFromFile(bundlePath);
                }

                // If bundle not loaded from disk (path missing or file unreadable), scan
                // already-loaded bundles — covers dedicated servers where the workshop bundle
                // file isn't present but the compassets bundle was already loaded by Tweaks.
                if (bundle == null)
                {
                    foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles())
                    {
                        if (loaded == null) continue;
                        var names = loaded.GetAllAssetNames();
                        bool hasFrame = Array.IndexOf(names, "assets/frame.prefab") >= 0;
                        bool hasArena = Array.IndexOf(names, "assets/arena.prefab") >= 0;
                        if (hasFrame || hasArena)
                        {
                            bundle = loaded;
                            CompetitiveAdjustments.ConfigManager.Log("Reusing already-loaded bundle that contains frame/arena assets.");
                            break;
                        }
                    }
                }

                if (bundle == null)
                {
                    // Last resort: use prefabs that Tweaks.PluginCore already extracted before unloading.
                    var cachedFrame = CompetitivePuckTweaks.src.PluginCore.BundledFramePrefab;
                    var cachedArena = CompetitivePuckTweaks.src.PluginCore.BundledArenaPrefab;
                    if (cachedFrame != null || cachedArena != null)
                    {
                        _framePrefab            = cachedFrame;
                        _arenaAndCollidersPrefab = cachedArena;
                        if (cachedFrame != null) CompetitiveAdjustments.ConfigManager.Log("Goal frame prefab loaded from Tweaks cache.");
                        if (cachedArena != null) CompetitiveAdjustments.ConfigManager.Log("Unified ArenaAndColliders prefab loaded from Tweaks cache.");
                        return;
                    }

                    if (bundlePath == null)
                    {
                        string bundlePathGoalframe = Path.Combine(modDir, "assets", "goalframe");
                        string bundlePathCompAssets = Path.Combine(modDir, "assets", "CompAssets");
                        Debug.Log($"[COMPADJUST] Goal frame bundle not found at: {bundlePathGoalframe} or {bundlePathCompAssets}");
                    }
                    else
                    {
                        CompetitiveAdjustments.ConfigManager.LogWarning("AssetBundle.LoadFromFile returned null for goalframe.");
                    }
                    return;
                }

                _framePrefab = bundle.LoadAsset<GameObject>("frame");

                // ── Unified ArenaAndColliders prefab (preferred) ──────────────
                _arenaAndCollidersPrefab = bundle.LoadAsset<GameObject>("ArenaAndColliders")
                    ?? bundle.LoadAsset<GameObject>("arenaandcolliders")
                    ?? bundle.LoadAsset<GameObject>("Assets/ArenaAndColliders.prefab");

                if (_arenaAndCollidersPrefab == null)
                {
                    foreach (var assetName in bundle.GetAllAssetNames())
                    {
                        if (string.IsNullOrEmpty(assetName)) continue;
                        if (!assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                        if (assetName.IndexOf("arenaandcollider", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        _arenaAndCollidersPrefab = bundle.LoadAsset<GameObject>(assetName);
                        if (_arenaAndCollidersPrefab != null)
                        {
                            Debug.Log($"[COMPADJUST] Loaded unified arena+colliders prefab by asset scan: {assetName}");
                            break;
                        }
                    }
                }

                // ── Legacy split prefabs (fallback) ──────────────────────────
                _arenaPrefab = bundle.LoadAsset<GameObject>("arena");
                _collidersPrefab = bundle.LoadAsset<GameObject>("Colliders")
                    ?? bundle.LoadAsset<GameObject>("colliders")
                    ?? bundle.LoadAsset<GameObject>("CollidersFixed")
                    ?? bundle.LoadAsset<GameObject>("collidersfixed")
                    ?? bundle.LoadAsset<GameObject>("Assets/Colliders.prefab")
                    ?? bundle.LoadAsset<GameObject>("assets/colliders.prefab")
                    ?? bundle.LoadAsset<GameObject>("Assets/CollidersFixed.prefab")
                    ?? bundle.LoadAsset<GameObject>("assets/collidersfixed.prefab")
                    ?? bundle.LoadAsset<GameObject>("Assets/Colliders.fbx")
                    ?? bundle.LoadAsset<GameObject>("assets/colliders.fbx");

                if (_collidersPrefab == null)
                {
                    foreach (var assetName in bundle.GetAllAssetNames())
                    {
                        if (string.IsNullOrEmpty(assetName)) continue;
                        bool isPrefab = assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
                        bool looksLikeColliderPrefab = assetName.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isPrefab || !looksLikeColliderPrefab) continue;
                        // Don't double-count the unified prefab as a standalone colliders prefab
                        if (assetName.IndexOf("arenaandcollider", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        _collidersPrefab = bundle.LoadAsset<GameObject>(assetName);
                        if (_collidersPrefab != null)
                        {
                            Debug.Log($"[COMPADJUST] Loaded collider prefab by asset scan: {assetName}");
                            break;
                        }
                    }
                }
                if (_framePrefab == null)
                {
                    string names = string.Join(", ", bundle.GetAllAssetNames());
                    Debug.LogWarning($"[COMPADJUST] 'frame' prefab not found in goalframe bundle. Assets in bundle: [{names}]");
                }
                else
                {
                    CompetitiveAdjustments.ConfigManager.Log("Goal frame prefab loaded from bundle.");
                }

                if (_arenaAndCollidersPrefab != null)
                {
                    CompetitiveAdjustments.ConfigManager.Log("Unified ArenaAndColliders prefab loaded from bundle.");
                }
                else
                {
                    if (_arenaPrefab != null)
                        CompetitiveAdjustments.ConfigManager.Log("Legacy arena prefab loaded from bundle.");
                    if (_collidersPrefab != null)
                        CompetitiveAdjustments.ConfigManager.Log("Legacy colliders prefab loaded from bundle.");
                    if (_arenaPrefab == null && _collidersPrefab == null)
                    {
                        string names = string.Join(", ", bundle.GetAllAssetNames());
                        Debug.LogWarning($"[COMPADJUST] No arena/collider prefabs found in bundle. Assets: [{names}]");
                    }
                }

                // Unload bundle data, keep the prefab in memory.
                bundle.Unload(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] GoalFrameLoader error: {ex.Message}");
            }
        }

        private static void RefreshFrameBundleIfChanged()
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string preferredBundlePath = GetPreferredBundlePath(modDir);

            if (string.IsNullOrEmpty(preferredBundlePath) || !File.Exists(preferredBundlePath))
                return;

            long currentWriteTicksUtc;
            try
            {
                currentWriteTicksUtc = File.GetLastWriteTimeUtc(preferredBundlePath).Ticks;
            }
            catch
            {
                return;
            }

            bool bundlePathChanged = !string.Equals(_loadedBundlePath, preferredBundlePath, StringComparison.OrdinalIgnoreCase);
            bool bundleTimeChanged = currentWriteTicksUtc != 0 && currentWriteTicksUtc != _loadedBundleWriteTicksUtc;

            if (!bundlePathChanged && !bundleTimeChanged)
                return;

            Debug.Log($"[COMPADJUST] Detected updated arena bundle on disk; reloading frame/arena/collider prefabs (path='{preferredBundlePath}').");

            _bundleLoadAttempted = false;
            _framePrefab = null;
            _arenaPrefab = null;
            _collidersPrefab = null;
            _arenaAndCollidersPrefab = null;

            if (_arenaInstance != null)
            {
                UnityEngine.Object.Destroy(_arenaInstance);
                _arenaInstance = null;
            }

            if (_collidersInstance != null)
            {
                UnityEngine.Object.Destroy(_collidersInstance);
                _collidersInstance = null;
            }

            DestroyBarrierStandalone();

            _loggedArenaPrefabMissing = false;
            _loggedArenaSpawned = false;
            _loggedArenaColliderFallback = false;
            _usingArenaVisualColliderFallback = false;
            _arenaAppearanceSynced = false;

            RestoreOriginalArenaColliders();

            _loadedBundlePath = preferredBundlePath;
            _loadedBundleWriteTicksUtc = currentWriteTicksUtc;
            TryLoadFrameBundle();
        }

        private static Renderer FindBestCustomFrameRenderer(Transform customFrameRoot)
        {
            if (customFrameRoot == null) return null;

            Renderer best = null;
            float bestScore = float.MinValue;

            foreach (var renderer in customFrameRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                float score = 0f;
                string n = renderer.name ?? string.Empty;

                if (n.IndexOf("post", StringComparison.OrdinalIgnoreCase) >= 0) score += 120f;
                if (n.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0) score += 100f;
                if (n.IndexOf("goal", StringComparison.OrdinalIgnoreCase) >= 0) score += 60f;
                if (n.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120f;
                if (n.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120f;
                if (n.IndexOf("net", StringComparison.OrdinalIgnoreCase) >= 0) score -= 200f;
                if (n.IndexOf("cloth", StringComparison.OrdinalIgnoreCase) >= 0) score -= 200f;

                var mats = renderer.sharedMaterials;
                if (mats != null && mats.Length > 0) score += 10f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = renderer;
                }
            }

            return best;
        }

        private static bool ApproxEqual(Vector3 a, Vector3 b)
            => Mathf.Approximately(a.x, b.x)
            && Mathf.Approximately(a.y, b.y)
            && Mathf.Approximately(a.z, b.z);

        private static void SyncCustomFrameAppearance(Goal goal, Transform customFrameRoot)
        {
            var sourceRenderer = FindOriginalGoalFrameRenderer(goal, customFrameRoot);
            if (sourceRenderer == null) return;

            var sourceSharedMaterials = sourceRenderer.sharedMaterials;

            // Do NOT copy the source renderer's MaterialPropertyBlock. The baked goal frame
            // renderer's block may contain smoothness/gloss overrides tuned for static
            // lightmaps — copying it suppresses gloss on our dynamic custom renderers.

            foreach (var renderer in customFrameRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                var clonedMats = CreateMirroredMaterials(sourceRenderer);
                if (clonedMats != null && clonedMats.Length > 0)
                {
                    renderer.materials = clonedMats;

                    // Some goal shaders use direct color properties instead of blocks.
                    for (int i = 0; i < clonedMats.Length; i++)
                    {
                        var srcMat = sourceSharedMaterials[Mathf.Min(i, sourceSharedMaterials.Length - 1)];
                        var dstMat = clonedMats[i];
                        if (srcMat == null || dstMat == null) continue;
                        CopyColorPropertyIfPresent(srcMat, dstMat, "_BaseColor");
                        CopyColorPropertyIfPresent(srcMat, dstMat, "_Color");
                        CopyColorPropertyIfPresent(srcMat, dstMat, "_MainColor");
                        CopyColorPropertyIfPresent(srcMat, dstMat, "_TintColor");
                        CopyColorPropertyIfPresent(srcMat, dstMat, "_TeamColor");
                    }
                }

                // Force probe sampling and shadows, same as arena renderers.
                renderer.lightProbeUsage      = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                renderer.renderingLayerMask   = sourceRenderer.renderingLayerMask;
                renderer.shadowCastingMode    = ShadowCastingMode.On;
                renderer.receiveShadows       = true;
            }
        }

        private static Renderer FindOriginalGoalFrameRenderer(Goal goal, Transform customFrameRoot)
        {
            var goalRoot = goal.transform;
            var netRoot = goal.NetCloth != null ? goal.NetCloth.transform : null;

            Renderer best = null;
            float bestScore = float.MinValue;

            foreach (var renderer in goalRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (customFrameRoot != null && (renderer.transform == customFrameRoot || renderer.transform.IsChildOf(customFrameRoot))) continue;
                if (netRoot != null && (renderer.transform == netRoot || renderer.transform.IsChildOf(netRoot))) continue;

                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                float score = 0f;
                string n = renderer.name;

                if (n.IndexOf("post", StringComparison.OrdinalIgnoreCase) >= 0) score += 100f;
                if (n.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0) score += 80f;
                if (n.IndexOf("goal", StringComparison.OrdinalIgnoreCase) >= 0) score += 40f;
                if (n.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120f;
                if (n.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0) score -= 80f;

                var mat = renderer.material;
                var color = ReadBestColor(mat);
                if (color.HasValue)
                {
                    float saturation = Mathf.Max(color.Value.r, Mathf.Max(color.Value.g, color.Value.b))
                                     - Mathf.Min(color.Value.r, Mathf.Min(color.Value.g, color.Value.b));
                    score += saturation * 20f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = renderer;
                }
            }

            return best;
        }

        private static Color? ReadBestColor(Material mat)
        {
            if (mat == null) return null;
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
            if (mat.HasProperty("_MainColor")) return mat.GetColor("_MainColor");
            if (mat.HasProperty("_TintColor")) return mat.GetColor("_TintColor");
            if (mat.HasProperty("_TeamColor")) return mat.GetColor("_TeamColor");
            return null;
        }

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

        private static void CopyFloatPropertyIfPresent(Material src, Material dst, string prop)
        {
            if (src == null || dst == null) return;
            if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;
            dst.SetFloat(prop, src.GetFloat(prop));
        }

        private static Material[] CreateMirroredMaterials(Renderer sourceRenderer)
        {
            if (sourceRenderer == null) return null;

            var sourceMaterials = sourceRenderer.sharedMaterials;
            if (sourceMaterials == null || sourceMaterials.Length == 0) return null;

            var mirrored = new Material[sourceMaterials.Length];

            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                var sourceMaterial = sourceMaterials[i];
                if (sourceMaterial == null) continue;

                var clone = new Material(sourceMaterial);

                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_BaseMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_MainTex");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_BumpMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_NormalMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_MaskMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_MetallicGlossMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_OcclusionMap");
                CopyTexturePropertyIfPresent(sourceMaterial, clone, "_EmissionMap");

                CopyColorPropertyIfPresent(sourceMaterial, clone, "_BaseColor");
                CopyColorPropertyIfPresent(sourceMaterial, clone, "_Color");
                CopyColorPropertyIfPresent(sourceMaterial, clone, "_MainColor");
                CopyColorPropertyIfPresent(sourceMaterial, clone, "_TintColor");
                CopyColorPropertyIfPresent(sourceMaterial, clone, "_EmissionColor");
                CopyColorPropertyIfPresent(sourceMaterial, clone, "_TeamColor");

                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_Smoothness");
                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_Glossiness");
                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_Metallic");
                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_Cutoff");
                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_Surface");
                CopyFloatPropertyIfPresent(sourceMaterial, clone, "_AlphaClip");

                // Force shadow receiving and specular/glossiness on regardless of what
                // the base game material has baked in — custom arena assets need these.
                clone.DisableKeyword("_RECEIVE_SHADOWS_OFF");
                clone.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                clone.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                clone.DisableKeyword("_GLOSSYREFLECTIONS_OFF");

                mirrored[i] = clone;
            }

            return mirrored;
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

        private static string DetermineArenaPartKey(string rendererNameOrPath)
        {
            if (string.IsNullOrEmpty(rendererNameOrPath)) return string.Empty;

            string value = rendererNameOrPath;
            if (value.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0) return "glass";

            // Window panes are glass: route them to the reflective glass treatment.
            // Exclude framing tokens so metal window bars/frames are not treated as
            // transparent glass (they fall through to the structure/no-match path).
            if (value.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("bar", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("frame", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("mullion", StringComparison.OrdinalIgnoreCase) < 0)
                return "glass";

            bool hasBarrier = value.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("rail", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasStructure = value.IndexOf("pillar", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("rafter", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("support", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasTop = value.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBottom = value.IndexOf("bottom", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBorder = value.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasIce = value.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasStructure) return "structure";

            if (hasIce && hasTop) return "ice_top";
            if (hasIce && hasBottom) return "ice_bottom";
            if (hasIce) return "ice";

            if (hasBarrier && hasTop && hasBorder) return "barrier_top_border";
            if (hasBarrier && hasBottom && hasBorder) return "barrier_bottom_border";
            if (hasBarrier) return "barrier";

            if (value.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            if (value.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            if (value.IndexOf("rail", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            return string.Empty;
        }

        private static bool AreCompatibleArenaParts(string targetPart, string candidatePart)
        {
            if (string.IsNullOrEmpty(targetPart) || string.IsNullOrEmpty(candidatePart)) return false;
            if (string.Equals(targetPart, candidatePart, StringComparison.OrdinalIgnoreCase)) return true;

            if ((targetPart == "ice_top" || targetPart == "ice_bottom" || targetPart == "ice")
                && (candidatePart == "ice_top" || candidatePart == "ice_bottom" || candidatePart == "ice"))
                return true;

            if ((targetPart == "barrier_top_border" || targetPart == "barrier_bottom_border" || targetPart == "barrier")
                && (candidatePart == "barrier_top_border" || candidatePart == "barrier_bottom_border" || candidatePart == "barrier"))
                return true;

            if (targetPart == "structure" && candidatePart == "structure")
                return true;

            return false;
        }

        private static float ComputeArenaTokenOverlapScore(string targetText, string candidateText)
        {
            if (string.IsNullOrEmpty(targetText) || string.IsNullOrEmpty(candidateText)) return 0f;

            float score = 0f;
            string[] tokens = { "top", "bottom", "border", "glass", "ice", "barrier", "board", "rail", "pillar", "rafter", "beam", "support" };
            for (int i = 0; i < tokens.Length; i++)
            {
                bool targetHas = targetText.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0;
                bool candidateHas = candidateText.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0;
                if (targetHas && candidateHas) score += 120f;
            }

            return score;
        }

        private static Renderer FindBestArenaSourceRenderer(Transform arenaRoot, Transform customArenaRoot, Renderer targetRenderer)
        {
            if (arenaRoot == null || targetRenderer == null) return null;

            string targetName = targetRenderer.name ?? string.Empty;
            string targetPath = GetRelativeTransformPath(customArenaRoot, targetRenderer.transform);
            string targetPart = DetermineArenaPartKey(targetName + "/" + targetPath);
            Renderer best = null;
            float bestScore = float.MinValue;

            foreach (var candidate in arenaRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (candidate == null) continue;
                if (customArenaRoot != null && (candidate.transform == customArenaRoot || candidate.transform.IsChildOf(customArenaRoot))) continue;

                var mats = candidate.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                float score = 0f;
                string candidateName = candidate.name ?? string.Empty;
                string candidatePath = GetRelativeTransformPath(arenaRoot, candidate.transform);
                string candidatePart = DetermineArenaPartKey(candidateName + "/" + candidatePath);

                if (string.Equals(candidateName, targetName, StringComparison.OrdinalIgnoreCase)) score += 300f;
                if (!string.IsNullOrEmpty(targetPath) && string.Equals(candidatePath, targetPath, StringComparison.OrdinalIgnoreCase)) score += 600f;
                if (!string.IsNullOrEmpty(targetPath) && candidatePath.EndsWith(targetPath, StringComparison.OrdinalIgnoreCase)) score += 250f;

                if (!string.IsNullOrEmpty(targetPart))
                {
                    if (string.Equals(candidatePart, targetPart, StringComparison.OrdinalIgnoreCase))
                        score += 1100f;
                    else if (AreCompatibleArenaParts(targetPart, candidatePart))
                        score += 550f;
                    else if (!string.IsNullOrEmpty(candidatePart))
                        score -= 800f;
                    else
                        score -= 150f;
                }

                score += ComputeArenaTokenOverlapScore(targetName + "/" + targetPath, candidateName + "/" + candidatePath);

                if (candidateName.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0) score -= 250f;
                if (candidateName.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0) score -= 250f;
                if (candidateName.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0) score -= 80f;

                var candidateCount = mats.Length;
                var targetCount = targetRenderer.sharedMaterials != null ? targetRenderer.sharedMaterials.Length : 0;
                if (candidateCount == targetCount) score += 75f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (bestScore < 150f)
                return null;

            return best;
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

        private static void HideOriginalGoalFrameRenderers(Goal goal, int rootId, Transform customFrameRoot)
        {
            if (_hiddenGoalRenderers.ContainsKey(rootId)) return;

            var hidden = new List<Renderer>();
            var goalRoot = goal.transform;
            var netRoot = goal.NetCloth != null ? goal.NetCloth.transform : null;

            foreach (var renderer in goalRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (customFrameRoot != null && (renderer.transform == customFrameRoot || renderer.transform.IsChildOf(customFrameRoot))) continue;
                if (netRoot != null && (renderer.transform == netRoot || renderer.transform.IsChildOf(netRoot))) continue;

                renderer.enabled = false;
                hidden.Add(renderer);
            }

            _hiddenGoalRenderers[rootId] = hidden;
        }

        private static void RestoreOriginalGoalFrameRenderers(int rootId)
        {
            if (!_hiddenGoalRenderers.TryGetValue(rootId, out var hidden)) return;

            foreach (var renderer in hidden)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }

            _hiddenGoalRenderers.Remove(rootId);
        }

        private static void DisableCustomFrameNetPieces(GameObject frameObj)
        {
            foreach (var renderer in frameObj.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                string name = renderer.name;
                if (name.IndexOf("net", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("cloth", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    renderer.enabled = false;
                }
            }
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
