// DashFall.ClientRunner.cs - Main client-side runner

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner : MonoBehaviour
    {
        private static DashFallClientRunner _instance;

        // UI elements (menu buttons now handled by ModMenuHub)
        private UITK.VisualElement _lastRoot;
        private UITK.UIDocument _doc;
        private UIManager _cachedUIManager; // Cache to avoid expensive lookups

        private float _nextUIProbeAt = 0f;
        private bool _buttonsWiredForThisRoot;

        // Cursor state for panel
        private bool _savedCursorState = false;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible = false;

        // Capture mode
        private bool _isCapturing = false;

        // Reflection fields for menu buttons
        private static readonly FieldInfo _fiMainSettings =
            typeof(UIMainMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fiPauseSettings =
            typeof(UIPauseMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);

        void Awake()
        {
            // Don't run on dedicated servers
            if (Application.isBatchMode || IsHeadlessServer())
            {
                Destroy(gameObject);
                return;
            }

            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            // Load configs
            _skater = DashFallConfigLoader.LoadSkaterConfig();
            _goalie = DashFallConfigLoader.LoadGoalieConfig();
            // _clientConfig = DashFallConfigLoader.LoadClientConfig(); // TODO: Add client config support

            // Note: GoalieDashExtend.Enabled is now controlled by server config
            // It will be updated when server features are received

            RebuildLookups();
            ResetInputActions();

            // Subscribe to server features received event to refresh UI and apply settings
            PoncePuck.Keybinds.ServerBridge.OnFeaturesReceived += OnServerFeaturesReceived;
            // Admin config editor: refresh the SERVER tab when the full config
            // mirror or the auth/apply status changes.
            PoncePuck.Keybinds.ServerBridge.OnFullConfigReceived += OnFullConfigReceived;
            PoncePuck.Keybinds.ServerBridge.OnAdminAuthResult += OnAdminAuthResult;

            EventManager.AddEventListener("Event_Everyone_OnLevelSpawned", OnLevelSpawnedForMinimap);
            if (Unity.Netcode.NetworkManager.Singleton != null)
                Unity.Netcode.NetworkManager.Singleton.OnClientStopped += OnClientStopped;
            DashFallMod.GoalNetTweaks.OnTweaksSynced += OnTweaksSynced;

        }

        private void OnServerFeaturesReceived()
        {
            // Apply server feature settings
            var features = PoncePuck.Keybinds.ServerBridge.ReceivedFeatures;
            if (features != null)
            {
                GoalieDashExtend.Enabled = features.GoalieDashExtendEnabled;
                Stances.Enabled = features.GoalieStancesEnabled;
            }
            
            // Refresh the UI to show enabled/disabled states
            if (_dfPanel != null && _dfPanel.style.display == UITK.DisplayStyle.Flex)
            {
                RefreshActionsUI();
            }
        }

        private void OnLevelSpawnedForMinimap(System.Collections.Generic.Dictionary<string, object> _)
        {
            StartCoroutine(ApplyMinimapScaleCoroutine());
            // Re-apply arena clip brushes after level geometry loads (if the setting is on).
            var clientCfg = DashFallConfigLoader.ClientConfig;
            if (clientCfg != null && clientCfg.ShowArenaClipBrushes)
                StartCoroutine(ApplyArenaClipBrushesNextFrame());
        }

        private IEnumerator ApplyArenaClipBrushesNextFrame()
        {
            yield return null; // wait one frame for arena geometry to be fully spawned
            yield return null; // second frame: arena colliders and custom arena instance are ready
            CompetitivePuckTweaks.src.ClientClipBrushes.ApplyArena(true);
        }

        private void OnTweaksSynced()
        {
            StartCoroutine(ApplyMinimapScaleCoroutine());
        }

        private void OnClientStopped(bool isHost)
        {
            // Unsubscribe immediately so any in-flight OnTweaksSynced calls
            // (fired before Object.Destroy runs OnDestroy) can't re-apply patches.
            DashFallMod.GoalNetTweaks.OnTweaksSynced -= OnTweaksSynced;
            ResetMinimapScale();
            GoalNetTweaks.RemoveNetworkBoundsPatches();
            // Clear the cached server config so that when the client joins the next
            // server (which may be vanilla), RefreshAll() doesn't re-apply the old
            // server's EnableArenaTweaks=true via _hasSyncedTweaks.
            GoalNetTweaks.ClearSyncedTweaks();
            // Drop per-body InstanceID-keyed state. PlayerBodyV2.OnNetworkDespawn
            // normally clears these per-player on disconnect, but a hard session
            // teardown skips OnNetworkDespawn and these IDs are never reused
            // across Unity sessions anyway, so wipe the slates to be safe.
            try { Stances.ClearAll(); } catch (Exception e) { ConfigManager.Dbg("Stances.ClearAll failed: " + e.Message); }
            try { GoalieDashExtend.ClearAll(); } catch (Exception e) { ConfigManager.Dbg("GoalieDashExtend.ClearAll failed: " + e.Message); }
        }

        /// <summary>Re-applies or resets the minimap scale immediately. Call after toggling EnableMinimapTweaks.</summary>
        public static void RefreshMinimap()
        {
            if (_instance == null) return;
            _instance.ApplyMinimapScaleNow();
        }

        // Find UIMinimap including inactive instances (e.g. pause menu hides it)
        private static UIMinimap FindUIMinimap() =>
            UnityEngine.Object.FindObjectsByType<UIMinimap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault();

        private void ResetMinimapScale()
        {
            var uiMinimap = FindUIMinimap();
            if (uiMinimap == null) return;

            var minimapField = typeof(UIMinimap).GetField("minimap", BindingFlags.NonPublic | BindingFlags.Instance);
            var contentField = typeof(UIMinimap).GetField("content", BindingFlags.NonPublic | BindingFlags.Instance);
            if (minimapField == null || contentField == null) return;

            var minimapEl = minimapField.GetValue(uiMinimap) as UITK.VisualElement;
            var contentEl = contentField.GetValue(uiMinimap) as UITK.VisualElement;
            if (minimapEl == null || contentEl == null) return;

            float baseScale = SettingsManager.MinimapScale;
            minimapEl.style.scale = new UnityEngine.Vector2(baseScale, baseScale);
            contentEl.style.scale = new UnityEngine.Vector2(1f, 1f);
            CompetitiveAdjustments.ConfigManager.Log("Minimap scale reset to default.");
        }

        // Synchronous: applies or resets minimap scale right now, no yield.
        // Used by RefreshMinimap (settings toggle) so it takes effect this frame.
        private void ApplyMinimapScaleNow()
        {
            if (DashFallConfigLoader.ClientConfig?.EnableMinimapTweaks == false)
            {
                ResetMinimapScale();
                return;
            }

            // Pull the arena scale from the effective source: local config when hosting,
            // synced values when joined to a modded server, or "vanilla rink" (returns
            // false) when joined to a vanilla server.  Without this gate the local
            // config was applied to vanilla servers, stretching the minimap to match
            // an arena that does not exist on that server.
            if (!GoalNetTweaks.TryGetEffectiveArenaScale(out float scaleX, out float scaleY))
            {
                ResetMinimapScale();
                return;
            }

            if (Mathf.Approximately(scaleX, 1f) && Mathf.Approximately(scaleY, 1f))
            {
                ResetMinimapScale();
                return;
            }

            var uiMinimap = FindUIMinimap();
            if (uiMinimap == null)
            {
                CompetitiveAdjustments.ConfigManager.Log("ApplyMinimapScale: UIMinimap not in scene yet");
                return;
            }

            var minimapField = typeof(UIMinimap).GetField("minimap", BindingFlags.NonPublic | BindingFlags.Instance);
            var contentField = typeof(UIMinimap).GetField("content", BindingFlags.NonPublic | BindingFlags.Instance);
            if (minimapField == null || contentField == null)
            {
                CompetitiveAdjustments.ConfigManager.Log("ApplyMinimapScale: field(s) not found on UIMinimap");
                return;
            }

            var minimapEl = minimapField.GetValue(uiMinimap) as UITK.VisualElement;
            var contentEl = contentField.GetValue(uiMinimap) as UITK.VisualElement;
            if (minimapEl == null || contentEl == null)
            {
                CompetitiveAdjustments.ConfigManager.Log("ApplyMinimapScale: minimap or content VisualElement is null");
                return;
            }

            float arenaDefaultX = 0.80f;
            float arenaDefaultY = 0.80f;
            float widthFactor  = scaleX * arenaDefaultX;
            float heightFactor = scaleY * arenaDefaultY;
            float baseScale = SettingsManager.MinimapScale;
            minimapEl.style.scale = new UnityEngine.Vector2(baseScale * widthFactor, baseScale * heightFactor);
            contentEl.style.scale = new UnityEngine.Vector2(1f / widthFactor, 1f / heightFactor);
            Debug.Log($"[COMPADJUST] Minimap scale: {baseScale * widthFactor:F3}x{baseScale * heightFactor:F3}, dots counter: {1f/widthFactor:F3}x{1f/heightFactor:F3}");
        }

        // Coroutine version: yields one frame first so level-spawn geometry is ready,
        // then delegates to the synchronous method.
        private IEnumerator ApplyMinimapScaleCoroutine()
        {
            yield return null; // let UIMinimapController.Event_Everyone_OnLevelSpawned run first
            ApplyMinimapScaleNow();
        }

        private static bool IsHeadlessServer()
        {
            // Check multiple indicators of dedicated server
            return SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }
        
        void OnDisable()
        {
            // Unregister when disabled (mod toggle in game settings)
            try
            {
                PonceMods.Shared.ModMenuHub.UnregisterMod("DashFall");
                ConfigManager.Dbg("OnDisable - unregistered from ModMenuHub");
            }
            catch (Exception e) { ConfigManager.Dbg("OnDisable failed: " + e.Message); }
        }

        void OnDestroy()
        {
            try
            {
                PoncePuck.Keybinds.ServerBridge.OnFeaturesReceived -= OnServerFeaturesReceived;
                PoncePuck.Keybinds.ServerBridge.OnFullConfigReceived -= OnFullConfigReceived;
                PoncePuck.Keybinds.ServerBridge.OnAdminAuthResult -= OnAdminAuthResult;
                EventManager.RemoveEventListener("Event_Everyone_OnLevelSpawned", OnLevelSpawnedForMinimap);
                if (Unity.Netcode.NetworkManager.Singleton != null)
                    Unity.Netcode.NetworkManager.Singleton.OnClientStopped -= OnClientStopped;
                DashFallMod.GoalNetTweaks.OnTweaksSynced -= OnTweaksSynced;

                // CleanupHUD(); //TODO: Re-enable when HUD is ready

                _dfPanel?.RemoveFromHierarchy();
                _dfBackdrop?.RemoveFromHierarchy();
                _captureOverlay?.RemoveFromHierarchy();

                // Unregister from ModMenuHub
                PonceMods.Shared.ModMenuHub.UnregisterMod("DashFall");
                PonceMods.Shared.ModMenuHub.Cleanup("DashFall");

                _dfPanel = null;
                _dfBackdrop = null;
                _captureOverlay = null;
                _buttonsWiredForThisRoot = false;
            }
            catch (Exception e) { ConfigManager.Dbg("OnDestroy failed: " + e.Message); }

            ClearInputActions();
        }

        void Update()
        {
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && _cmm != nm.CustomMessagingManager)
                {
                    _cmm = nm.CustomMessagingManager;
                    _helloSent = false; // Reset when CMM changes
                }
                
                // Send Hello when connected and CMM available
                if (nm != null && nm.IsConnectedClient && _cmm != null && !_helloSent)
                {
                    if (Time.unscaledTime >= _nextHelloRetry)
                    {
                        _nextHelloRetry = Time.unscaledTime + 2f; // Retry every 2 seconds if needed
                        SendHelloToServer();
                        _helloSent = true;
                        RefreshRole();
                        EnsurePositionSelectHook();
                    }
                }

                // Only while the player is actually viewing the SERVER tab,
                // re-request the full config until it arrives (the one-shot Hello
                // push can be missed). Gated to the open tab so idle clients never
                // poll for a config they are not looking at.
                if (nm != null && nm.IsConnectedClient && !nm.IsServer && _cmm != null
                    && IsServerTabOpen()
                    && !PoncePuck.Keybinds.ServerBridge.HasReceivedFullConfig
                    && Time.unscaledTime >= _nextConfigReqRetry)
                {
                    _nextConfigReqRetry = Time.unscaledTime + 2f;
                    PoncePuck.Keybinds.ServerBridge.RequestConfigFull();
                }
                
                // Periodically refresh role detection
                if (nm != null && nm.IsConnectedClient)
                {
                    RefreshRole();
                }
                
                // Update HUD state
                // UpdateHUD(); // TODO: Re-enable when HUD is ready

                // Fire HOLD actions repeatedly while held
                UpdateHoldActions();
                
                // Ensure CMM handlers are registered (idempotent no-ops once bound).
                GoalieDashExtend.EnsureCMMRegistered();
                Stances.EnsureCMMRegistered();
                
                // Handle ESC key to fully close DashFall panel
                if (_dfPanel != null && _dfPanel.style.display == UITK.DisplayStyle.Flex && !_isCapturing)
                {
                    var kb = UnityEngine.InputSystem.Keyboard.current;
                    if (kb != null && kb.escapeKey.wasPressedThisFrame)
                    {
                        // Save config and close completely
                        DashFallConfigLoader.SaveSkaterConfig(_skater);
                        DashFallConfigLoader.SaveGoalieConfig(_goalie);
                        RebuildLookups();
                        ResetInputActions();
                        FullCloseDashFallPanel();
                    }
                }

                // Probe UI periodically
                if (Time.unscaledTime >= _nextUIProbeAt)
                {
                    _nextUIProbeAt = Time.unscaledTime + 0.5f;
                    TryWireButtonIfNeeded();
                }
            }
            catch (System.OverflowException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] Error in Update: {ex.Message}");
            }
        }

        // ===== UI Button Creation =====
        private void TryWireButtonIfNeeded()
        {
            try
            {
                // Cache UIManager lookup
                if (_cachedUIManager == null)
                    _cachedUIManager = UnityEngine.Object.FindFirstObjectByType<UIManager>(UnityEngine.FindObjectsInactive.Include);
                _doc = _cachedUIManager != null ? _cachedUIManager.UIDocument : UnityEngine.Object.FindFirstObjectByType<UITK.UIDocument>(UnityEngine.FindObjectsInactive.Include);
                var root = _doc != null ? _doc.rootVisualElement : null;
                if (root == null) return;

                if (_lastRoot != root)
                {
                    _lastRoot = root;
                    _buttonsWiredForThisRoot = false;

                    // Rebuild elements on new root
                    _dfBackdrop?.RemoveFromHierarchy();
                    _dfPanel?.RemoveFromHierarchy();
                    _captureOverlay?.RemoveFromHierarchy();
                    _hudRoot?.RemoveFromHierarchy();
                    _dfBackdrop = null;
                    _dfPanel = null;
                    _captureOverlay = null;
                    _hudRoot = null;
                    
                    // Rebuild HUD on new root
                    BuildHUD();
                }

                if (!_buttonsWiredForThisRoot) TryWireButtonsOnce(root);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Error wiring button: {e.Message}");
            }
        }

        private static void CopyClasses(UITK.VisualElement from, UITK.VisualElement to)
        {
            if (from == null || to == null) return;
            foreach (var cls in from.GetClasses()) to.AddToClassList(cls);
        }

        private UITK.Button MakeSiblingButton(UITK.Button reference, string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            CopyClasses(reference, b);
            b.name = text.Replace(" ", "_") + "_DashFall";
            b.pickingMode = UITK.PickingMode.Position;

            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            b.style.width = reference.style.width;
            b.style.minWidth = reference.style.minWidth;
            b.style.maxWidth = reference.style.maxWidth;
            b.style.height = reference.style.height;
            b.style.minHeight = reference.style.minHeight;
            b.style.maxHeight = reference.style.maxHeight;
            b.style.marginTop = 8f;
            b.style.paddingTop = 8f;
            b.style.paddingBottom = 8f;
            b.style.paddingLeft = 15f;

            // Add hover effect
            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                b.style.backgroundColor = Color.white;
                b.style.color = Color.black;
            });
            b.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                b.style.color = Color.white;
            });

            return b;
        }

        private void TryWireButtonsOnce(UITK.VisualElement root)
        {
            if (root == null || _buttonsWiredForThisRoot) return;

            // Register with ModMenuHub instead of creating our own buttons
            PonceMods.Shared.ModMenuHub.RegisterMod(
                "DashFall",
                "COMPADJUST",
                OpenDashFallPanel,
                20 // Priority - lower = higher in list
            );
            
            // Initialize ModMenuHub (first mod to call this becomes owner)
            PonceMods.Shared.ModMenuHub.Initialize("DashFall");

            _buttonsWiredForThisRoot = true;
            ConfigManager.Dbg("Registered with ModMenuHub.");
        }

        // Cursor state management
        private void SaveCursorState()
        {
            if (_savedCursorState) return;
            _prevLockState = UnityEngine.Cursor.lockState;
            _prevCursorVisible = UnityEngine.Cursor.visible;
            _savedCursorState = true;
        }

        private void RestoreCursorState()
        {
            if (!_savedCursorState) return;
            UnityEngine.Cursor.lockState = _prevLockState;
            UnityEngine.Cursor.visible = _prevCursorVisible;
            _savedCursorState = false;
        }
    }
}
