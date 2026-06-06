using UnityEngine;
using UnityEngine.Rendering;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using Unity.Netcode;


namespace CompetitivePuckTweaks.src
{

    public class PluginCore
    {
        private const string CMM_SYNC_CONFIG = "CPT_sync_config";
        private const string CMM_SYNC_REQUEST = "CPT_request_sync";

        private static PluginCore _active;
        public Harmony _harmony = new Harmony("_harmony");
        public static Mesh torsoMesh;
        public static Mesh groinMesh;
        // Returns the effective CompTweaks config: live values when
        // EnableCompTweaks is on, vanilla defaults when it is off.  Every
        // patch that reads PluginCore.config.X automatically respects the
        // master flag through this single accessor.
        public static CompetitiveAdjustments.CompTweaksConfig config => CompetitiveAdjustments.ConfigManager.CompTweaksEffective;
        // Raw config for the few UI / sync paths that need the user's saved
        // intent regardless of the master flag.
        public static CompetitiveAdjustments.CompTweaksConfig configRaw => CompetitiveAdjustments.ConfigManager.Config.CompTweaks;
        public static Dictionary<int, Stick> StickMeshes = new Dictionary<int, Stick>();
        public static List<int> PuckIDs = new List<int>();
        public static UtilObj utilObj = new UtilObj();
        private bool EventListenersPresent = false;
        private bool _enabled;
        private bool _physicsListenersLoaded;
        private bool _syncRequestHandlerRegistered;

        // Captured vanilla values for the globally-applied physics knobs so
        // OnDisable can restore them when the mod is toggled off at runtime.
        // Without this, fixedDeltaTime / solverIterations / layer-collision
        // masks stay on the modded values for the rest of the session.
        private bool _vanillaPhysicsCaptured;
        private float _vanillaFixedDeltaTime;
        private int _vanillaSolverIterations;
        private bool _vanillaIgnore_6_6;
        private bool _vanillaIgnore_6_8;

        /// <summary>
        /// Core plugin enable function
        /// </summary>
        /// <returns>bool status of enable success</returns>
        public bool OnEnable()
        {
            if (_enabled)
            {
                ApplyLiveConfigFull();
                return true;
            }

            PluginCore.Log($"CPT version {CompetitiveAdjustments.SharedConstants.TWEAKS_VERSION} is installed.");

            bool canRunServer = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                                (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer);
            if (!canRunServer)
            {
                PluginCore.Log($"Server runtime not active yet. Skipping CPT enable.");
                return false;
            }
            PluginCore.Log($"Enabling...");

            try
            {
                PluginCore.Log($"Using unified CompetitiveAdjustments config.");
                if (config.DisableShaftCollision == false) config.EnableMidStickCollider = false;
                
                if (config.UsePhysicsModificationEvents) utilObj = new UtilObj();

                HarmonyPatchHelper.PatchNamespaces(_harmony, "CompetitivePuckTweaks");

                if (config.UsePhysicsModificationEvents)
                {
                    utilObj.LoadListeners();
                    _physicsListenersLoaded = true;
                }

                PluginCore.Log($"{System.Linq.Enumerable.Count(_harmony.GetPatchedMethods())} harmony methods patched.");

                EnsurePlayerMeshesLoadedForCurrentConfig();

                if (!_vanillaPhysicsCaptured)
                {
                    _vanillaFixedDeltaTime = Time.fixedDeltaTime;
                    _vanillaSolverIterations = Physics.defaultSolverIterations;
                    _vanillaIgnore_6_6 = Physics.GetIgnoreLayerCollision(6, 6);
                    _vanillaIgnore_6_8 = Physics.GetIgnoreLayerCollision(6, 8);
                    _vanillaPhysicsCaptured = true;
                }

                if (config.DisableStickCollision) Physics.IgnoreLayerCollision(6, 6, true);
                Physics.IgnoreLayerCollision(6, 8, !(CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision == true));

                Time.fixedDeltaTime = config.FixedDeltaTime;
                Physics.defaultSolverIterations = config.SolverIterations;

                // 310 migration changed several event names; listen to both for compatibility.
                EventManager.AddEventListener("Event_OnClientConnected", SendSyncMessage);
                EventManager.AddEventListener("Event_Everyone_OnClientConnected", SendSyncMessage);
                EventListenersPresent = true;
                Log("Sync message listener added.");

                RegisterSyncRequestHandler();

                _enabled = true;
                _active = this;

                // Startup timing guard: if pucks already spawned before CPT finished enabling,
                // immediately enforce configured scale on all existing pucks.
                RescaleAllExistingPucks();

                return true;
            }
            catch (Exception e)
            {
                PluginCore.Log($"Failed to enable: {e}");
                return false;
            }
        }

        /// <summary>
        /// Core plugin disable function.
        /// </summary>
        /// <returns>bool corresponding to success or failure of disable</returns>
        public bool OnDisable()
        {
            PluginCore.Log($"Disabling...");
            try
            {
                _harmony.UnpatchSelf();
                if (_physicsListenersLoaded)
                {
                    utilObj.UnloadListeners();
                    _physicsListenersLoaded = false;
                }
                if (EventListenersPresent)
                {
                    EventManager.RemoveEventListener("Event_OnClientConnected", SendSyncMessage);
                    EventManager.RemoveEventListener("Event_Everyone_OnClientConnected", SendSyncMessage);
                }
                UnregisterSyncRequestHandler();
                EventListenersPresent = false;
                _enabled = false;
                if (_active == this) _active = null;

                // Restore the global physics knobs we trampled in OnEnable so a
                // mid-session toggle doesn't leave the game on the modded
                // simulation step / layer mask.
                if (_vanillaPhysicsCaptured)
                {
                    Time.fixedDeltaTime = _vanillaFixedDeltaTime;
                    Physics.defaultSolverIterations = _vanillaSolverIterations;
                    Physics.IgnoreLayerCollision(6, 6, _vanillaIgnore_6_6);
                    Physics.IgnoreLayerCollision(6, 8, _vanillaIgnore_6_8);
                    _vanillaPhysicsCaptured = false;
                }
                return true;
            }
            catch (Exception e)
            {
                PluginCore.Log($"Failed to disable: {e}");
                return false;
            }
        }

        /// <summary>
        /// Loads custom meshes (CURRENTLY DEPRECATED)
        /// </summary>
        // Prefabs extracted from the compassets bundle so GoalNetTweaks can use them even after
        // the bundle has been unloaded. Set once in DefinePlayerMeshes() and never cleared.
        internal static GameObject BundledArenaPrefab;
        internal static GameObject BundledFramePrefab;

        public static void DefinePlayerMeshes()
        {

            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "assets");

            AssetBundle assetBundle = AssetBundle.LoadFromFile(Path.Combine(path, "shrunk_torso"));
            if (assetBundle == null)
            {
                // Fallback for setups that packed torso into goalframe.
                assetBundle = AssetBundle.LoadFromFile(Path.Combine(path, "goalframe"));
                if (assetBundle == null)
                {
                    assetBundle = AssetBundle.LoadFromFile(Path.Combine(path, "CompAssets"));
                    if (assetBundle == null)
                        assetBundle = AssetBundle.LoadFromFile(Path.Combine(path, "compassets"));
                }
            }
            if (assetBundle != null)
                PluginCore.Log($"[BundleLoad] Loaded bundle. Assets: {string.Join(", ", assetBundle.GetAllAssetNames())}");

            AssetBundle groinBundle = AssetBundle.LoadFromFile(Path.Combine(path, "groin"));

            Mesh importTorsoMesh = null;
            Mesh importGroinMesh = null;

            try
            {
                importTorsoMesh = LoadPreferredTorsoMesh(assetBundle);
                importGroinMesh = groinBundle != null
                    ? groinBundle.LoadAsset("assets/shrunk_groin.blend", typeof(Mesh)) as Mesh
                    : null;

                if (groinBundle != null)
                    PluginCore.Log($"Assets in groinBundle: {string.Join(", ", groinBundle.GetAllAssetNames())}");

                if (importTorsoMesh != null)
                {
                    torsoMesh = UnityEngine.Object.Instantiate(importTorsoMesh);
                    ClearScaledColliderMesh(); // invalidate cached collider mesh whenever source changes
                    if (torsoMesh.isReadable)
                    {
                        torsoMesh.Optimize();
                        torsoMesh.RecalculateNormals();
                        torsoMesh.RecalculateTangents();
                        torsoMesh.RecalculateBounds();
                    }
                    // NOTE: vertex count may show correctly but triangles=0 for GPU-only (non-readable) meshes.
                    // Scale is applied via transform in PlayerBodyPatch, not by modifying vertex data.
                    ComputeTorsoMeshScale();
                    PluginCore.Log($"PlayerTorso mesh defined with {torsoMesh.vertexCount} vertices and {torsoMesh.subMeshCount} sub-meshes. isReadable={torsoMesh.isReadable} Bounds center={torsoMesh.bounds.center.ToString("F4")} extents={torsoMesh.bounds.extents.ToString("F4")}");
                }
                else
                {
                    torsoMesh = null;
                    PluginCore.Log("Torso mesh could not be loaded.");
                }

                if (importGroinMesh != null)
                {
                    groinMesh = UnityEngine.Object.Instantiate(importGroinMesh);
                    if (groinMesh.isReadable)
                    {
                        groinMesh.Optimize();
                        groinMesh.RecalculateNormals();
                        groinMesh.RecalculateTangents();
                        groinMesh.RecalculateBounds();
                    }
                    PluginCore.Log($"PlayerGroin mesh defined with {groinMesh.vertexCount} vertices and {groinMesh.triangles.Length / 3} triangles.");
                }
                else
                {
                    groinMesh = null;
                    PluginCore.Log("Groin mesh could not be loaded.");
                }
            }
            finally
            {
                // Cache frame/arena prefabs so GoalNetTweaks can use them after the bundle is unloaded.
                if (assetBundle != null)
                {
                    BundledFramePrefab = assetBundle.LoadAsset<GameObject>("frame");
                    BundledArenaPrefab = assetBundle.LoadAsset<GameObject>("ArenaAndColliders")
                        ?? assetBundle.LoadAsset<GameObject>("arenaandcolliders");
                    if (BundledArenaPrefab == null)
                    {
                        foreach (var assetName in assetBundle.GetAllAssetNames())
                        {
                            if (assetName.IndexOf("arenaandcollider", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                BundledArenaPrefab = assetBundle.LoadAsset<GameObject>(assetName);
                                if (BundledArenaPrefab != null) break;
                            }
                        }
                    }
                    assetBundle.Unload(false);
                }
                if (groinBundle != null) groinBundle.Unload(false);
            }
        }

        private static void EnsurePlayerMeshesLoadedForCurrentConfig()
        {
            bool useCustomSkaterTorsoModel = DashFallMod.ConfigManager.CompAdjustEffective.EnableCustomSkaterTorsoModel;
            if (!config.EnableSmallerModels && !useCustomSkaterTorsoModel)
                return;

            if (torsoMesh == null || (config.EnableSmallerModels && groinMesh == null))
                DefinePlayerMeshes();
        }

        private static Mesh LoadPreferredTorsoMesh(AssetBundle bundle)
        {
            if (bundle == null) return null;

            string[] preferredAssets =
            {
                "assets/meshes/new_torso.fbx",
                "assets/meshes/skater_torso.fbx",
                "assets/meshes/torso.fbx",
                "assets/meshes/shrunk_torso.fbx"
            };

            for (int i = 0; i < preferredAssets.Length; i++)
            {
                var mesh = bundle.LoadAsset(preferredAssets[i], typeof(Mesh)) as Mesh;
                if (mesh != null)
                {
                    PluginCore.Log($"Using torso mesh asset '{preferredAssets[i]}'.");
                    return mesh;
                }
            }

            var names = bundle.GetAllAssetNames();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i] ?? string.Empty;
                if (name.IndexOf("torso", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("goalie", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var mesh = bundle.LoadAsset(name, typeof(Mesh)) as Mesh;
                if (mesh != null)
                {
                    PluginCore.Log($"Using discovered torso mesh asset '{name}'.");
                    return mesh;
                }
            }

            var meshes = bundle.LoadAllAssets<Mesh>();
            if (meshes != null && meshes.Length > 0)
            {
                Mesh candidate = null;
                for (int i = 0; i < meshes.Length; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null) continue;

                    string meshName = mesh.name ?? string.Empty;
                    if (meshName.IndexOf("goalie", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (meshName.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    if (meshName.IndexOf("torso", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        PluginCore.Log($"Using discovered torso mesh object '{meshName}'.");
                        return mesh;
                    }

                    if (candidate == null)
                        candidate = mesh;
                }

                if (candidate != null)
                {
                    PluginCore.Log($"Using fallback torso mesh object '{candidate.name}'.");
                    return candidate;
                }
            }

            var prefabFallbackMesh = TryFindTorsoMeshInBundlePrefabs(bundle);
            if (prefabFallbackMesh != null)
            {
                PluginCore.Log($"Using torso mesh discovered from prefab references '{prefabFallbackMesh.name}'.");
                return prefabFallbackMesh;
            }

            PluginCore.Log($"No torso mesh found. Bundle assets: {string.Join(", ", names)}");
            return null;
        }

        private static Mesh TryFindTorsoMeshInBundlePrefabs(AssetBundle bundle)
        {
            if (bundle == null) return null;

            // Try the dedicated torso prefab first — any mesh in it is the right one.
            var torsoPrefab = bundle.LoadAsset<GameObject>("torso");
            if (torsoPrefab != null)
            {
                var mfs = torsoPrefab.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < mfs.Length; i++)
                {
                    var mesh = mfs[i] != null ? mfs[i].sharedMesh : null;
                    if (mesh != null) { PluginCore.Log($"Using torso mesh from torso prefab: '{mesh.name}'."); return mesh; }
                }
                var smrs = torsoPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++)
                {
                    var mesh = smrs[i] != null ? smrs[i].sharedMesh : null;
                    if (mesh != null) { PluginCore.Log($"Using torso mesh (skinned) from torso prefab: '{mesh.name}'."); return mesh; }
                }
            }

            // Fallback: look for a mesh explicitly named 'torso' inside frame/arena prefabs.
            string[] fallbackPrefabs = { "frame", "arena" };
            for (int p = 0; p < fallbackPrefabs.Length; p++)
            {
                var prefab = bundle.LoadAsset<GameObject>(fallbackPrefabs[p]);
                if (prefab == null) continue;

                var meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    var mesh = meshFilters[i] != null ? meshFilters[i].sharedMesh : null;
                    if (mesh == null) continue;
                    if ((mesh.name ?? string.Empty).IndexOf("torso", StringComparison.OrdinalIgnoreCase) >= 0)
                        return mesh;
                }

                var skinnedMeshes = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < skinnedMeshes.Length; i++)
                {
                    var mesh = skinnedMeshes[i] != null ? skinnedMeshes[i].sharedMesh : null;
                    if (mesh == null) continue;
                    if ((mesh.name ?? string.Empty).IndexOf("torso", StringComparison.OrdinalIgnoreCase) >= 0)
                        return mesh;
                }
            }

            return null;
        }

        /// <summary>
        /// Logs a message formatted with mod name
        /// </summary>
        /// <param name="message">Message to be logged</param>
        public static void Log(string message)
        {
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] " + message);
        }

        /// <summary>
        /// Logs a message formatted with mod name
        /// </summary>
        /// <param name="message">Message to be logged</param>
        public static void LogError(string message) {
            Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] " + message);
        }

        public static void Dbg(string message) {
            if (CompetitiveAdjustments.ConfigManager.Config.Dashfall.EnableDebugLogs)
                Log(message);
        }

        /// <summary>
        /// Sends named custom message for syncing client config with server
        /// </summary>
        /// <param name="message">Input dictionary with connection information</param>
        public void SendSyncMessage(Dictionary<string, object> message)
        {
            if (!TryGetClientId(message, out ulong targetId))
            {
                Log("Config sync skipped: clientId missing from event payload.");
                return;
            }
            ManualSync(targetId);
        }

        public static void ManualSync(ulong targetId)
        {
            Dbg($"Sending config sync message to client {targetId}...");

            var customMessagingManager = NetworkManager.Singleton?.CustomMessagingManager;
            if (customMessagingManager == null)
            {
                Log("Config sync skipped: CustomMessagingManager is null.");
                return;
            }

            ConfigSyncPackage messageContent = new ConfigSyncPackage(config, CompetitiveAdjustments.ConfigManager.CompAdjustEffective);
            using (var writer = new FastBufferWriter(1024, Unity.Collections.Allocator.Temp))
            {
                writer.WriteValueSafe(messageContent);
                customMessagingManager.SendNamedMessage(CMM_SYNC_CONFIG, targetId, writer);
                Dbg($"Config sync sent to client {targetId}");
            }
        }

        private void RegisterSyncRequestHandler()
        {
            if (_syncRequestHandlerRegistered) return;
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm == null) return;

            try
            {
                cmm.RegisterNamedMessageHandler(CMM_SYNC_REQUEST, OnSyncRequestReceived);
                _syncRequestHandlerRegistered = true;
                Log("Registered config sync request handler.");
            }
            catch (Exception e)
            {
                Log($"Failed to register sync request handler: {e.Message}");
            }
        }

        private void UnregisterSyncRequestHandler()
        {
            if (!_syncRequestHandlerRegistered) return;
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm == null) return;

            try
            {
                cmm.UnregisterNamedMessageHandler(CMM_SYNC_REQUEST);
            }
            catch { }

            _syncRequestHandlerRegistered = false;
        }

        private void OnSyncRequestReceived(ulong senderId, FastBufferReader reader)
        {
            ManualSync(senderId);
        }

        private static bool TryGetClientId(Dictionary<string, object> message, out ulong clientId)
        {
            clientId = 0;
            if (message == null) return false;
            if (!message.TryGetValue("clientId", out object raw) || raw == null) return false;

            try
            {
                clientId = Convert.ToUInt64(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ApplyLiveConfigInstance()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            if (config.UsePhysicsModificationEvents && !_physicsListenersLoaded)
            {
                utilObj.LoadListeners();
                _physicsListenersLoaded = true;
            }
            else if (!config.UsePhysicsModificationEvents && _physicsListenersLoaded)
            {
                utilObj.UnloadListeners();
                _physicsListenersLoaded = false;
            }

            ApplyLiveConfig();
        }

        public static void ApplyLiveConfigFull()
        {
            if (_active != null)
                _active.ApplyLiveConfigInstance();
            else
                ApplyLiveConfig();
        }

        public static void ApplyLiveConfig()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;

            if (config.DisableShaftCollision == false)
                config.EnableMidStickCollider = false;

            EnsurePlayerMeshesLoadedForCurrentConfig();
            Physics.IgnoreLayerCollision(6, 6, config.DisableStickCollision);
            Physics.IgnoreLayerCollision(6, 8, !(CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision == true));
            Time.fixedDeltaTime = config.FixedDeltaTime;
            Physics.defaultSolverIterations = config.SolverIterations;

            // Keep runtime pucks aligned with current config (e.g. /reload or config edits).
            RescaleAllExistingPucks();
            CompetitiveAdjustments.BallModeHelper.RefreshAllPucks();

            // Re-apply free blade / high sticking to existing players on /reload.
            StickAngleRefs.RefreshFreeBladeForAllPlayers();

            // Re-apply or restore custom torso meshes and sync debug brushes on live players.
            RefreshPlayerTorsoStates();
        }

        // Stores the original torso MeshFilter sharedMesh per MeshFilter instance ID so we
        // can restore it when EnableCustomSkaterTorsoModel is turned off at runtime.
        internal static readonly Dictionary<int, Mesh> OriginalTorsoMeshes = new Dictionary<int, Mesh>();
        internal static readonly Dictionary<int, Mesh> OriginalTorsoColliderMeshes = new Dictionary<int, Mesh>();
        internal static readonly Dictionary<int, int> OriginalTorsoColliderLayers = new Dictionary<int, int>();

        /// <summary>
        /// Refreshes torso visual and collider state on all live skater bodies without
        /// the IsServer guard — safe to call from client-side UI toggles.
        /// </summary>
        public static void RefreshTorsoVisualsForClient() => RefreshPlayerTorsoStates();

        // Visual scale applied to the 'torso' child transform so the cm-scale (or any-scale) FBX mesh
        // appears at the correct world size. Auto-computed at load time from the mesh bounds so it
        // works regardless of whether Blender scale was applied before export.
        // Target: the torso half-width in "Player Torso" local space should match a reasonable
        // hockey player width (~0.84m in parent-local space, which becomes ~0.34m in world space
        // due to the parent's ~0.4 world scale).
        internal const float kTargetTorsoHalfWidth = 0.84f;
        internal static float torsoMeshScale = 200f;

        // Uniform scale (used for legacy single-axis paths and cache comparison fallback)
        internal static float EffectiveTorsoMeshScale =>
            torsoMeshScale * (DashFallMod.ConfigManager.CompAdjustEffective?.CustomTorsoScaleX ?? 1f);

        internal static Vector3 EffectiveTorsoMeshScaleV3
        {
            get
            {
                var cfg = DashFallMod.ConfigManager.CompAdjustEffective;
                return new Vector3(
                    torsoMeshScale * (cfg?.CustomTorsoScaleX ?? 1f),
                    torsoMeshScale * (cfg?.CustomTorsoScaleY ?? 1f),
                    torsoMeshScale * (cfg?.CustomTorsoScaleZ ?? 1f));
            }
        }

        internal static Vector3 EffectiveTorsoOffset => Vector3.zero;

        // Mesh/scale for VISUAL purposes — always use the Companion's client-loaded mesh so the visual
        // toggle works even when the server has EnableCustomSkaterTorsoModel=false.
        internal static Mesh VisualTorsoMesh =>
            CompetitiveCompanion.PluginCore.torsoMesh ?? torsoMesh;
        internal static float VisualTorsoMeshScale =>
            CompetitiveCompanion.PluginCore.torsoMesh != null
                ? CompetitiveCompanion.PluginCore.torsoMeshScale
                : torsoMeshScale;
        internal static Vector3 VisualTorsoMeshScaleV3
        {
            get
            {
                var df = DashFallMod.ConfigManager.CompAdjustEffective;
                float s = VisualTorsoMeshScale;
                return new Vector3(
                    s * (df?.CustomTorsoScaleX ?? 1f),
                    s * (df?.CustomTorsoScaleY ?? 1f),
                    s * (df?.CustomTorsoScaleZ ?? 1f));
            }
        }

        // True when the server or client wants the custom torso VISUAL suppressed.
        internal static bool TorsoVisualDisabled =>
            (DashFallMod.ConfigManager.CompAdjustEffective?.DisableCustomTorsoVisual == true) ||
            !(DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowCustomTorsoMesh ?? true);


        internal static void ComputeTorsoMeshScale()
        {
            if (torsoMesh == null) return;
            float maxExtent = Mathf.Max(
                torsoMesh.bounds.extents.x,
                torsoMesh.bounds.extents.y,
                torsoMesh.bounds.extents.z);
            torsoMeshScale = maxExtent > 1e-4f ? kTargetTorsoHalfWidth / maxExtent : 200f;
            Log($"[TorsoScale] mesh max-extent={maxExtent:F6} → torsoMeshScale={torsoMeshScale:F2}");
        }

        // Pre-scaled collider mesh: the MeshCollider lives on the parent "Player Torso" transform
        // which does NOT have the torsoMeshScale localScale applied (that scale is only on the child
        // "torso" MeshFilter object). We bake the scale + Y-180 rotation into a separate mesh so the
        // physics shape matches the visual in world space.
        private static Mesh _scaledTorsoColliderMesh;
        private static Vector3 _scaledTorsoColliderMeshScale = Vector3.zero;

        internal static Mesh GetOrBuildScaledColliderMesh()
        {
            Vector3 effectiveScale = EffectiveTorsoMeshScaleV3;
            if (_scaledTorsoColliderMesh != null && _scaledTorsoColliderMeshScale == effectiveScale)
                return _scaledTorsoColliderMesh;
            ClearScaledColliderMesh();
            if (torsoMesh == null || !torsoMesh.isReadable) return null;

            // Bake the same transform used on the visual child: per-axis scale + rotation=Y180
            var matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 180f, 0f), effectiveScale);
            Vector3[] srcVerts = torsoMesh.vertices;
            Vector3[] dstVerts = new Vector3[srcVerts.Length];
            for (int i = 0; i < srcVerts.Length; i++)
                dstVerts[i] = matrix.MultiplyPoint3x4(srcVerts[i]);

            _scaledTorsoColliderMesh = new Mesh();
            _scaledTorsoColliderMesh.name = torsoMesh.name + "_colliderScaled";
            _scaledTorsoColliderMeshScale = effectiveScale;
            _scaledTorsoColliderMesh.vertices = dstVerts;
            _scaledTorsoColliderMesh.triangles = torsoMesh.triangles;
            _scaledTorsoColliderMesh.RecalculateBounds();
            Log($"[TorsoCollider] Built collider mesh: {_scaledTorsoColliderMesh.vertexCount} verts, bounds={_scaledTorsoColliderMesh.bounds}");
            return _scaledTorsoColliderMesh;
        }

        public static void ClearScaledColliderMesh()
        {
            if (_scaledTorsoColliderMesh != null)
            {
                UnityEngine.Object.Destroy(_scaledTorsoColliderMesh);
                _scaledTorsoColliderMesh = null;
            }
            _scaledTorsoColliderMeshScale = Vector3.zero;
        }

        private static void RefreshPlayerTorsoStates()
        {
            try
            {
                bool useCustom = DashFallMod.ConfigManager.CompAdjustEffective?.EnableCustomSkaterTorsoModel == true;
                var playerMeshField = typeof(PlayerBodyV2).GetField("playerMesh",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var bodies = UnityEngine.Object.FindObjectsByType<PlayerBodyV2>(FindObjectsSortMode.None);
                foreach (var body in bodies)
                {
                    if (body == null) continue;
                    bool isGoalie = body.name.Contains("Goalie");
                    if (isGoalie) continue;

                    PlayerMesh playerMesh = playerMeshField != null
                        ? playerMeshField.GetValue(body) as PlayerMesh
                        : null;
                    if (playerMesh == null) continue;

                    var torsoGo = playerMesh.PlayerTorso;
                    if (torsoGo == null) continue;

                    var mf = torsoGo.GetComponentInChildren<MeshFilter>();
                    var mc = torsoGo.GetComponentInChildren<MeshCollider>();

                    int mfId = mf != null ? mf.GetInstanceID() : -1;
                    int mcId = mc != null ? mc.GetInstanceID() : -1;

                    // ── COLLIDER (server config: EnableCustomSkaterTorsoModel) ──────────────
                    if (mc != null)
                    {
                        if (!OriginalTorsoColliderMeshes.ContainsKey(mcId))
                            OriginalTorsoColliderMeshes[mcId] = mc.sharedMesh;
                        if (!OriginalTorsoColliderLayers.ContainsKey(mcId))
                            OriginalTorsoColliderLayers[mcId] = mc.gameObject.layer;

                        if (useCustom && torsoMesh != null)
                        {
                            var colliderMesh = GetOrBuildScaledColliderMesh();
                            if (colliderMesh != null)
                            {
                                mc.convex = true;
                                mc.isTrigger = false;
                                mc.sharedMesh = colliderMesh;
                                // CRITICAL: Keep the original layer (not player body layer 8).
                                // The physics matrix excludes player body layer from puck collisions.
                                // The original layer allows puck collision to work correctly.
                                // (Layer remains unchanged from game default)
                                mc.enabled = false;
                                mc.enabled = true;
                                Dbg($"[TorsoRefresh] Custom collider. convex={mc.convex} layer={mc.gameObject.layer} bounds={mc.bounds}");
                                TorsoDebugBrush.Sync(mc);
                            }
                        }
                        else if (OriginalTorsoColliderMeshes.TryGetValue(mcId, out var origCol))
                        {
                            // Server disabled custom model — restore original collider and layer.
                            mc.convex = false;
                            mc.sharedMesh = origCol;
                            if (OriginalTorsoColliderLayers.TryGetValue(mcId, out int origLayer))
                                mc.gameObject.layer = origLayer;
                            mc.enabled = false;
                            mc.enabled = true;
                            TorsoDebugBrush.Sync(mc);
                            Dbg($"[TorsoRefresh] Restored original collider for {body.name}.");
                        }
                    }

                    // ── VISUAL (client config: ShowCustomTorsoMesh, server override: DisableCustomTorsoVisual) ─
                    // Never touch mr.enabled — the game's MeshRendererHider handles local player visibility.
                    if (mf != null)
                    {
                        if (!OriginalTorsoMeshes.ContainsKey(mfId))
                            OriginalTorsoMeshes[mfId] = mf.sharedMesh;

                        Mesh vm = VisualTorsoMesh;
                        if (vm != null && !TorsoVisualDisabled)
                        {
                            mf.sharedMesh = vm;
                            mf.transform.localScale = VisualTorsoMeshScaleV3;
                            mf.transform.localPosition = EffectiveTorsoOffset;
                            mf.transform.localRotation = Quaternion.Euler(0, 180, 0);
                        }
                        else if (OriginalTorsoMeshes.TryGetValue(mfId, out var origMesh))
                        {
                            // Client disabled custom visual — restore original so game makes it transparent.
                            mf.sharedMesh = origMesh;
                            mf.transform.localScale = Vector3.one;
                            mf.transform.localPosition = Vector3.zero;
                            mf.transform.localRotation = Quaternion.identity;
                        }
                        Dbg($"[TorsoRefresh] body={body.name} useCustom={useCustom} visualDisabled={TorsoVisualDisabled}");
                    }
                }
            }
            catch (Exception e)
            {
                Log($"RefreshPlayerTorsoStates failed: {e.Message}");
            }
        }
        private static void RescaleAllExistingPucks()
        {
            try
            {
                if (PuckManager.Instance == null) return;

                List<Puck> pucks = PuckManager.Instance.GetPucks();
                if (pucks == null || pucks.Count == 0) return;

                Vector3 targetScale = CompetitivePuckTweaks.src.PuckPatch.GetSyncedPuckScaleVector();
                for (int i = 0; i < pucks.Count; i++)
                {
                    var puck = pucks[i];
                    if (puck == null) continue;
                    puck.transform.localScale = targetScale;
                }

                Log($"Applied puck scale {targetScale} to {pucks.Count} existing puck(s).");
            }
            catch (Exception e)
            {
                Log($"Failed to rescale existing pucks: {e.Message}");
            }
        }
    }
}