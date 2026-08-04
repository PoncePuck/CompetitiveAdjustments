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

            Physics.IgnoreLayerCollision(6, 6, config.DisableStickCollision);
            Physics.IgnoreLayerCollision(6, 8, !(CompetitiveAdjustments.ConfigManager.CompAdjustEffective?.StickBodyCollision == true));
            Time.fixedDeltaTime = config.FixedDeltaTime;
            Physics.defaultSolverIterations = config.SolverIterations;

            // Keep runtime pucks aligned with current config (e.g. /reload or config edits).
            RescaleAllExistingPucks();
            CompetitiveAdjustments.BallModeHelper.RefreshAllPucks();

            // Re-apply free blade / high sticking to existing players on /reload.
            StickAngleRefs.RefreshFreeBladeForAllPlayers();
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