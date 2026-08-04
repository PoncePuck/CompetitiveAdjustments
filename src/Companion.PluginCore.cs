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
        public static DashFallClientConfig config;
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

        private static bool _warnedConfigSyncSize;

        private static void WarnConfigSyncSizeOnce(int actualBytes)
        {
            if (_warnedConfigSyncSize) return;
            _warnedConfigSyncSize = true;
            Debug.LogWarning(
                $"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Ignoring config sync: payload is {actualBytes} bytes, " +
                $"expected {CompetitivePuckTweaks.src.ConfigSyncPackage.WireSizeBytes}. The server is running a different " +
                "CompetitiveAdjustments build. Puck scale, leg pad offset and the CompTweaks toggles will stay at local " +
                "defaults here rather than be set from misread values. Update the mod on BOTH the server and the client.");
        }

        private static void ReceiveMessage(ulong senderId, FastBufferReader messagePayload)
        {
            if (config == null)
            {
                config = DashFallConfigLoader.ClientConfig ?? new DashFallClientConfig();
            }

            // Positional payload, no field names: a server on a different build
            // does not produce a parse error, it produces plausible numbers read
            // out of the wrong slots.  Refuse the whole message rather than apply
            // a half-wrong config, and say plainly what the operator has to do.
            int available = messagePayload.Length - messagePayload.Position;
            if (available != CompetitivePuckTweaks.src.ConfigSyncPackage.WireSizeBytes)
            {
                WarnConfigSyncSizeOnce(available);
                return;
            }

            try
            {
                CompetitivePuckTweaks.src.ConfigSyncPackage receivedPackage = new CompetitivePuckTweaks.src.ConfigSyncPackage();
                messagePayload.ReadValueSafe(out receivedPackage);
                config.PuckScale = receivedPackage.PuckScale;
                config.PuckScaleX = receivedPackage.PuckScaleX;
                config.PuckScaleY = receivedPackage.PuckScaleY;
                config.PuckScaleZ = receivedPackage.PuckScaleZ;
                // The pad offset goes straight to the CompTweaks mirror below, which is the
                // field the leg pad patch actually reads. It used to be copied onto the
                // client config as well, where nothing ever read it back.
                //
                // The puck fields above are [NonSerialized], so this save persists the
                // player's own preferences and deliberately does NOT persist the server's
                // puck shape. Writing that to disk meant the next launch started with the
                // last server's puck before any sync had happened.
                DashFallConfigLoader.SaveClientConfig(config);

                // Mirror into CompTweaks as well: that is the field the leg pad
                // patch reads on both roles, and it is what keeps the client's pad
                // collider on the same marker position the server is using.
                var ctMirror = CompetitiveAdjustments.ConfigManager.Config?.CompTweaks;
                if (ctMirror != null) ctMirror.ButterflyPadOffset = receivedPackage.LegPadOffset;
                // Pads that already ran Awake were positioned before this arrived.
                DashFallMod.PlayerLegPadPatch.ReapplyAll();

                // Unpack CompTweaks bool flags into the central config so the UI can display them
                CompetitivePuckTweaks.src.ConfigSyncPackage.UnpackBools(
                    receivedPackage.BoolFlags,
                    CompetitiveAdjustments.ConfigManager.Config.CompTweaks);

                // Unpack CompAdjust config so client visuals match the server
                var df = CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;
                CompetitivePuckTweaks.src.ConfigSyncPackage.UnpackDashfall(receivedPackage, df);
                // Refresh player clip brushes with the newly-applied collider shape
                if (DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowPlayerClipBrushes == true)
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyPlayer(true);

                // Refresh ball mode and free blade for existing objects
                CompetitiveAdjustments.BallModeHelper.RefreshAllPucks();
                CompetitivePuckTweaks.src.StickAngleRefs.RefreshFreeBladeForAllPlayers();

                Debug.Log($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Synced server config (PuckScale={receivedPackage.PuckScale}, LegPadOffset={receivedPackage.LegPadOffset}, flags=0x{receivedPackage.BoolFlags:X4})");
                
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