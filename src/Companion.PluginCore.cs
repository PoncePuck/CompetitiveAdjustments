using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using HarmonyLib;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using DashFallMod.Client;

namespace CompetitiveCompanion
{
    public class PluginCore
    {
        private const string CMM_SYNC_CONFIG = "CPT_sync_config";
        private const string CMM_SYNC_REQUEST = "CPT_request_sync";

        public Harmony CCHarmony = new Harmony("CCHarmony");
        public static Mesh torsoMesh;
        public static Mesh groinMesh;
        public static float torsoMeshScale = 200f;
        public static DashFallClientConfig config;
        public static Material transparentSubMaterial;
        public static Material visorMaterial;
        private static float _lastSyncRequestTime = -999f;
        private bool EventListenersPresent = false;

        /// <summary>
        /// Core plugin enable function.
        /// </summary>
        /// <returns>bool corresponding to success or failure of enable</returns>
        public bool OnEnable()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] This mod is only intended for clients.");
                return false;
            }
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Enabling CC version {CompetitiveAdjustments.SharedConstants.COMPANION_VERSION}...");
            try
            {
                // Use DashFall's unified client config for all companion-side client values.
                config = DashFallConfigLoader.ClientConfig ?? new DashFallClientConfig();

                try
                {
                    DefinePlayerMesh();
                }
                catch (Exception meshEx)
                {
                    Debug.LogWarning($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Custom mesh load failed; continuing without custom meshes: {meshEx.Message}");
                }

                try
                {
                    GetCustomMaterials();
                }
                catch (Exception matEx)
                {
                    Debug.LogWarning($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Custom material load failed (non-fatal): {matEx.Message}");
                }
                HarmonyPatchHelper.PatchNamespaces(CCHarmony, "CompetitiveCompanion");
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Harmony patching complete.");

                EventManager.AddEventListener("Event_Client_OnClientStarted", LoadSyncHandler);
                EventManager.AddEventListener("Event_OnClientStarted", LoadSyncHandler);
                EventListenersPresent = true;

                // Try immediately in case the startup event already fired before this plugin enabled.
                LoadSyncHandler();

                return true;
            }
            catch (Exception e)
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Failed to enable: {e}");
                return false;
            }
        }

        /// <summary>
        /// Core plugin disable function.
        /// </summary>
        /// <returns>bool corresponding to success or failure of disable</returns>
        public bool OnDisable()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] This mod is only intended for clients.");
                return false;
            }
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Disabling...");
            try
            {
                if (torsoMesh != null)
                {
                    UnityEngine.Object.Destroy(torsoMesh);
                    torsoMesh = null;
                }
                if (groinMesh != null)
                {
                    UnityEngine.Object.Destroy(groinMesh);
                    groinMesh = null;
                }

                CCHarmony.UnpatchSelf();

                if (EventListenersPresent)
                {
                    EventManager.RemoveEventListener("Event_Client_OnClientStarted", LoadSyncHandler);
                    EventManager.RemoveEventListener("Event_OnClientStarted", LoadSyncHandler);
                }
                UnloadSyncHandler();

                return true;
            }
            catch (Exception e)
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Failed to disable: {e}");
                return false;
            }
        }

        public static void LoadSyncHandler(Dictionary<string, object> message = null)
        {
            try
            {
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
                {
                    Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Cannot register handler: NetworkManager or CMM is null");
                    return;
                }
                
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(CMM_SYNC_CONFIG, ReceiveMessage);
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Registered config sync message handler");
                RequestConfigSyncFromServer("LoadSyncHandler");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Failed to register config sync handler: {e}");
            }
        }

        public static void RequestConfigSyncFromServer(string reason = "manual")
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsClient) return;
                var cmm = nm.CustomMessagingManager;
                if (cmm == null) return;

                float now = Time.unscaledTime;
                if (now - _lastSyncRequestTime < 0.5f) return;
                _lastSyncRequestTime = now;

                using (var writer = new FastBufferWriter(1, Unity.Collections.Allocator.Temp))
                {
                    writer.WriteValueSafe((byte)1);
                    cmm.SendNamedMessage(CMM_SYNC_REQUEST, NetworkManager.ServerClientId, writer);
                }

                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Requested config sync from server ({reason}).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Failed to request config sync: {e.Message}");
            }
        }

        public static void UnloadSyncHandler(Dictionary<string, object> message = null)
        {
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm != null)
            {
                try { cmm.UnregisterNamedMessageHandler(CMM_SYNC_CONFIG); }
                catch (Exception e) { Debug.LogWarning($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] UnloadSyncHandler unregister failed: {e.Message}"); }
            }
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Unregistered config sync message handler");
            ResetPuckScale();
        }

        public static void ResetPuckScale()
        {
            if (config == null) return;
            // Reset the uniform master AND every per-axis multiplier, otherwise a
            // wide/flat puck from the last server would persist (ComposeScale would
            // still apply PuckScaleX/Y/Z on top of the reset uniform 1).
            PluginCore.config.PuckScale = 1f;
            PluginCore.config.PuckScaleX = 1f;
            PluginCore.config.PuckScaleY = 1f;
            PluginCore.config.PuckScaleZ = 1f;
        }

        public static void DefinePlayerMesh()
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
                }
            }
            AssetBundle groinBundle = AssetBundle.LoadFromFile(Path.Combine(path, "groin"));

            if (assetBundle == null)
            {
                if (groinBundle != null) groinBundle.Unload(false);
                throw new InvalidOperationException("Required torso mesh asset bundle is missing or unreadable.");
            }

            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] [BundleLoad] Assets: {string.Join(", ", assetBundle.GetAllAssetNames())}");

            Mesh importTorsoMesh = LoadPreferredTorsoMesh(assetBundle);
            Mesh importGroinMesh = groinBundle != null
                ? groinBundle.LoadAsset("assets/shrunk_groin.blend", typeof(Mesh)) as Mesh
                : null;

            if (importTorsoMesh == null)
            {
                assetBundle.Unload(false);
                if (groinBundle != null) groinBundle.Unload(false);
                throw new InvalidOperationException("Torso mesh asset could not be loaded from bundle.");
            }

            torsoMesh = UnityEngine.Object.Instantiate(importTorsoMesh);
            if (torsoMesh.isReadable)
            {
                torsoMesh.Optimize();
                torsoMesh.RecalculateNormals();
                torsoMesh.RecalculateTangents();
                torsoMesh.RecalculateBounds();
            }
            float maxExtent = Mathf.Max(
                torsoMesh.bounds.extents.x,
                torsoMesh.bounds.extents.y,
                torsoMesh.bounds.extents.z);
            torsoMeshScale = maxExtent > 1e-4f
                ? CompetitivePuckTweaks.src.PluginCore.kTargetTorsoHalfWidth / maxExtent
                : 200f;
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] PlayerTorso mesh defined with {torsoMesh.vertexCount} vertices. isReadable={torsoMesh.isReadable} torsoMeshScale={torsoMeshScale:F2}");

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
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] PlayerGroin mesh defined with {groinMesh.vertexCount} vertices.");
            }
            else
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Groin bundle not found, skipping groin mesh.");
            }

            assetBundle.Unload(false);
            if (groinBundle != null) groinBundle.Unload(false);
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
                    Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using torso mesh asset '{preferredAssets[i]}'.");
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
                    Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using discovered torso mesh asset '{name}'.");
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
                        Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using discovered torso mesh object '{meshName}'.");
                        return mesh;
                    }

                    if (candidate == null)
                        candidate = mesh;
                }

                if (candidate != null)
                {
                    Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using fallback torso mesh object '{candidate.name}'.");
                    return candidate;
                }
            }

            var prefabFallbackMesh = TryFindTorsoMeshInBundlePrefabs(bundle);
            if (prefabFallbackMesh != null)
            {
                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using torso mesh discovered from prefab references '{prefabFallbackMesh.name}'.");
                return prefabFallbackMesh;
            }

            Debug.LogWarning($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] No torso mesh found. Bundle assets: {string.Join(", ", names)}");
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
                    if (mesh != null) { Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using torso mesh from torso prefab: '{mesh.name}'."); return mesh; }
                }
                var smrs = torsoPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++)
                {
                    var mesh = smrs[i] != null ? smrs[i].sharedMesh : null;
                    if (mesh != null) { Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Using torso mesh (skinned) from torso prefab: '{mesh.name}'."); return mesh; }
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

        public static void GetCustomMaterials()
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "assets");

            AssetBundle materialBundle = AssetBundle.LoadFromFile(Path.Combine(path, "material"));

            if (materialBundle == null)
                throw new InvalidOperationException("Custom material asset bundle is missing or unreadable.");

            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Materials in bundle: {string.Join(", ", materialBundle.GetAllAssetNames())}");

            Material customMaterial = materialBundle.LoadAsset("assets/transparentsub.mat", typeof(Material)) as Material;
            if (customMaterial == null)
            {
                materialBundle.Unload(false);
                throw new InvalidOperationException("Custom material asset is missing in bundle.");
            }
            transparentSubMaterial = UnityEngine.Object.Instantiate(customMaterial);

            materialBundle.Unload(false);
        }

        private static void ReceiveMessage(ulong senderId, FastBufferReader messagePayload)
        {
            if (config == null)
            {
                config = DashFallConfigLoader.ClientConfig ?? new DashFallClientConfig();
            }

            try
            {
                CompetitivePuckTweaks.src.ConfigSyncPackage receivedPackage = new CompetitivePuckTweaks.src.ConfigSyncPackage();
                messagePayload.ReadValueSafe(out receivedPackage);
                config.PuckScale = receivedPackage.PuckScale;
                config.PuckScaleX = receivedPackage.PuckScaleX;
                config.PuckScaleY = receivedPackage.PuckScaleY;
                config.PuckScaleZ = receivedPackage.PuckScaleZ;
                config.ButterflyPadOffset = receivedPackage.LegPadOffset;
                DashFallConfigLoader.SaveClientConfig(config);

                // Unpack CompTweaks bool flags into the central config so the UI can display them
                CompetitivePuckTweaks.src.ConfigSyncPackage.UnpackBools(
                    receivedPackage.BoolFlags,
                    CompetitiveAdjustments.ConfigManager.Config.CompTweaks);

                // Unpack CompAdjust config (torso scale, enable flags) so visuals match the server
                var df = CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;
                CompetitivePuckTweaks.src.ConfigSyncPackage.UnpackDashfall(receivedPackage, df);
                CompetitivePuckTweaks.src.PluginCore.RefreshTorsoVisualsForClient();
                // Refresh player clip brushes with the newly-applied collider shape
                if (DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowPlayerClipBrushes == true)
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyPlayer(true);

                // Refresh ball mode and free blade for existing objects
                CompetitiveAdjustments.BallModeHelper.RefreshAllPucks();
                CompetitivePuckTweaks.src.StickAngleRefs.RefreshFreeBladeForAllPlayers();

                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Synced server config (PuckScale={receivedPackage.PuckScale}, LegPadOffset={receivedPackage.LegPadOffset}, flags=0x{receivedPackage.BoolFlags:X4}, torsoScale={receivedPackage.TorsoScaleX:F2},{receivedPackage.TorsoScaleY:F2},{receivedPackage.TorsoScaleZ:F2})");
                
                // Apply the puck scale to any existing pucks immediately
                if (PuckManager.Instance != null)
                {
                    List<Puck> pucks = PuckManager.Instance.GetPucks();
                    if (pucks != null)
                    {
                        UnityEngine.Vector3 puckScale = CompetitivePuckTweaks.src.PuckPatch.GetSyncedPuckScaleVector();
                        Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Applying puck scale {puckScale} to {pucks.Count} existing pucks");
                        foreach (Puck puck in pucks)
                        {
                            if (puck == null) continue;
                            puck.transform.localScale = puckScale;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Error receiving config sync: {e}");
            }
        }

        public static void Log(string message)
        {
            Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] " + message);
        }

    }
}