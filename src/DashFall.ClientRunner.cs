// DashFall.ClientRunner.cs - Main client-side runner

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner : MonoBehaviour
    {
        private static DashFallClientRunner _instance;

        /// <summary>
        /// True when the local client has the config panel up, from a static context.
        /// Exists for the chat-suppression Harmony patch, which runs on UIManager and has no
        /// instance of this runner to ask. Safe before the runner exists and on a dedicated
        /// server, where _instance is never assigned.
        /// </summary>
        public static bool IsPanelOpenStatic =>
            _instance != null && _instance.IsDashFallPanelOpen;

        // UI elements. There are no on-screen entry points any more: the panel is opened
        // and closed with F4 only (see TickPanelHotkeys), so nothing here has to survive
        // a menu rebuild except the UIDocument root the panel is parented to.
        private UITK.VisualElement _lastRoot;
        private UITK.UIDocument _doc;
        private UIManager _cachedUIManager; // Cache to avoid expensive lookups

        private float _nextUIProbeAt = 0f;

        // Cursor state for panel
        private bool _savedCursorState = false;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible = false;

        // Capture mode
        private bool _isCapturing = false;

        // Vanilla MinimapScale baked into the last applied/reset minimap scale, so Update
        // can detect a mid-session change of the vanilla minimap-size slider and re-apply.
        private float _lastAppliedMinimapBaseScale = float.NaN;

        // The NetworkManager instance we subscribed OnClientStopped to. Bound lazily in
        // Update because Singleton can be null when the runner is created at mod-enable.
        private Unity.Netcode.NetworkManager _clientStoppedSubscribedNm;

        // Vanilla UIMinimap private VisualElements, resolved once at type init (the fields are
        // type-level, so caching is safe and avoids re-reflecting on every spawn/sync).
        private static readonly FieldInfo _fiMinimapEl =
            typeof(UIMinimap).GetField("minimap", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _fiMinimapContentEl =
            typeof(UIMinimap).GetField("content", BindingFlags.NonPublic | BindingFlags.Instance);
        private static bool _warnedMinimapReflectionFailed;

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
            // OnClientStopped is bound lazily in Update (TryBindClientStopped): the runner is
            // created at mod-enable, when NetworkManager.Singleton may not exist yet, so a
            // one-shot null-gated subscribe here would silently skip disconnect cleanup.
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
            
            // Refresh the UI to show enabled/disabled states. Gated on the logical open flag rather
            // than the panel's display, which disagree while a rebind overlay has the panel hidden.
            // The extra _isCapturing check is not redundant with it: rebuilding the rows mid-capture
            // would detach the very BIND button _captureButton still points at, so the refresh is
            // deferred to the end of the capture instead.
            if (IsDashFallPanelOpen && !_isCapturing)
            {
                RefreshActionsUI();
            }

            // Joined a server running COMPADJUST: warn now if this client is out of date.
            OnJoinedModdedServer();
        }

        private void OnLevelSpawnedForMinimap(System.Collections.Generic.Dictionary<string, object> _)
        {
            // One-shot scene-hierarchy dump for planning the base-game arena+hangar resize.
            CompetitiveAdjustments.ArenaHierarchyDiag.ResetOneShot();
            StartCoroutine(DumpArenaHierarchyNextFrame());
            StartCoroutine(ApplyMinimapScaleCoroutine());
            // Re-apply arena clip brushes after level geometry loads (if the setting is on).
            var clientCfg = DashFallConfigLoader.ClientConfig;
            if (clientCfg != null && clientCfg.ShowArenaClipBrushes)
                StartCoroutine(ApplyArenaClipBrushesNextFrame());
        }

        private System.Collections.IEnumerator DumpArenaHierarchyNextFrame()
        {
            for (int i = 0; i < 5; i++) yield return null; // let level geometry finish spawning
            CompetitiveAdjustments.ArenaHierarchyDiag.DumpOnce();
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
            // The session is over, so per-connection chunk state is finally safe to drop.
            // RemoveNetworkBoundsPatches deliberately keeps the chunk table and the peer
            // capability set while a connection is live, because dropping either mid-session
            // strands already-chunked objects or silently reverts peers to the saturating
            // encoding. This is the point where neither concern applies.
            DashFallMod.Net.WrapSync.ClearAll();
            // Clear the cached server config so that when the client joins the next
            // server (which may be vanilla), RefreshAll() doesn't re-apply the old
            // server's EnableArenaTweaks=true via _hasSyncedTweaks.
            GoalNetTweaks.ClearSyncedTweaks();
            // Same reason as ClearSyncedTweaks above: the next server has to prove its own
            // settings, and without this the retry loop would consider the job already done
            // and the client would carry the last server's FreeBlade/ball mode/wrap status.
            CompetitiveCompanion.PluginCore.ResetConfigSyncState();
            // Drop per-body InstanceID-keyed state. PlayerBodyV2.OnNetworkDespawn
            // normally clears these per-player on disconnect, but a hard session
            // teardown skips OnNetworkDespawn and these IDs are never reused
            // across Unity sessions anyway, so wipe the slates to be safe.
            try { Stances.ClearAll(); } catch (Exception e) { ConfigManager.Dbg("Stances.ClearAll failed: " + e.Message); }
            try { GoalieDashExtend.ClearAll(); } catch (Exception e) { ConfigManager.Dbg("GoalieDashExtend.ClearAll failed: " + e.Message); }
        }

        // Lazily (re)binds OnClientStopped to the current NetworkManager. Safe to call every
        // frame: it no-ops while already bound to the same instance (or both null). Uses Unity's
        // fake-null semantics so a destroyed prior instance compares == null and is not touched.
        private void TryBindClientStopped(Unity.Netcode.NetworkManager nm)
        {
            if (nm == _clientStoppedSubscribedNm) return;
            if (_clientStoppedSubscribedNm != null)
                _clientStoppedSubscribedNm.OnClientStopped -= OnClientStopped;
            _clientStoppedSubscribedNm = nm;
            if (nm != null)
                nm.OnClientStopped += OnClientStopped;
        }

        /// <summary>Re-applies the minimap scale immediately, after an arena resize or a change to the vanilla minimap-size slider.</summary>
        public static void RefreshMinimap()
        {
            if (_instance == null) return;
            _instance.ApplyMinimapScaleNow();
        }

        // Find UIMinimap including inactive instances (e.g. pause menu hides it)
        private static UIMinimap FindUIMinimap() =>
            UnityEngine.Object.FindObjectsByType<UIMinimap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault();

        // Resolves the vanilla UIMinimap's private "minimap" (frame) and "content" (dots)
        // VisualElements via the cached FieldInfos. Returns false (and warns once) if a game
        // update renamed/retyped the fields, so callers fall back to the vanilla minimap.
        private static bool TryGetMinimapElements(UIMinimap uiMinimap, out UITK.VisualElement minimapEl, out UITK.VisualElement contentEl)
        {
            minimapEl = null;
            contentEl = null;
            if (_fiMinimapEl == null || _fiMinimapContentEl == null)
            {
                WarnMinimapReflectionOnce("UIMinimap.minimap/content field(s) not found");
                return false;
            }
            minimapEl = _fiMinimapEl.GetValue(uiMinimap) as UITK.VisualElement;
            contentEl = _fiMinimapContentEl.GetValue(uiMinimap) as UITK.VisualElement;
            if (minimapEl == null || contentEl == null)
            {
                WarnMinimapReflectionOnce("UIMinimap.minimap/content VisualElement is null");
                return false;
            }
            return true;
        }

        private static void WarnMinimapReflectionOnce(string detail)
        {
            CompetitiveAdjustments.ConfigManager.Log("Minimap rescaling unavailable: " + detail);
            if (_warnedMinimapReflectionFailed) return;
            _warnedMinimapReflectionFailed = true;
            Debug.LogWarning($"[COMPADJUST] Minimap rescaling disabled ({detail}) -- a game update likely changed UIMinimap. Using the vanilla minimap.");
        }

        private void ResetMinimapScale()
        {
            var uiMinimap = FindUIMinimap();
            if (uiMinimap == null) return;
            if (!TryGetMinimapElements(uiMinimap, out var minimapEl, out var contentEl)) return;

            float baseScale = SettingsManager.MinimapScale;
            _lastAppliedMinimapBaseScale = baseScale;
            minimapEl.style.scale = new UnityEngine.Vector2(baseScale, baseScale);
            contentEl.style.scale = new UnityEngine.Vector2(1f, 1f);
            // Restore vanilla bounds in case a prior scaled apply enlarged them (arena tweaks
            // turned off mid-session, or joined a vanilla server after a modded one).
            var level = UnityEngine.Object.FindObjectsByType<Level>(FindObjectsSortMode.None).FirstOrDefault();
            if (level != null) uiMinimap.Bounds = level.Bounds;
            CompetitiveAdjustments.ConfigManager.Log("Minimap scale reset to default.");
        }

        // Synchronous: applies or resets minimap scale right now, no yield.
        // Used by RefreshMinimap, which GoalNetTweaks calls after an arena resize, so the
        // map matches the new rink in the same frame the rink changes.
        // Returns true once the scale has been applied or reset (or definitively
        // no-opped); false only while the UIMinimap is not yet in the scene, so the
        // coroutine's retry loop knows to try again next frame.
        private bool ApplyMinimapScaleNow()
        {
            // No opt-out. The minimap normalises dots by UIMinimap.Bounds, which follows the
            // arena scale, so leaving it vanilla on a resized rink puts every dot in the
            // wrong place. TryGetEffectiveArenaScale below already returns false on a
            // vanilla rink and on a vanilla server, which resets to the stock minimap, so
            // this is a no-op exactly when there is nothing to correct.

            // Pull the arena scale from the effective source: local config when hosting,
            // synced values when joined to a modded server, or "vanilla rink" (returns
            // false) when joined to a vanilla server.  Without this gate the local
            // config was applied to vanilla servers, stretching the minimap to match
            // an arena that does not exist on that server.  The returned scales are the
            // ground-plane axes: scaleX = rink width (world X), scaleZ = rink depth/length
            // (world Z).  World Y (vertical) is irrelevant to a top-down map.
            if (!GoalNetTweaks.TryGetEffectiveArenaScale(out float scaleX, out float scaleZ))
            {
                ResetMinimapScale();
                return true;
            }

            var uiMinimap = FindUIMinimap();
            if (uiMinimap == null)
            {
                CompetitiveAdjustments.ConfigManager.Log("ApplyMinimapScale: UIMinimap not in scene yet");
                return false;
            }

            if (!TryGetMinimapElements(uiMinimap, out var minimapEl, out var contentEl))
                return true; // reflection broke; the vanilla minimap is the safe fallback

            // Hybrid arena resize: the real base rink is scaled directly, so players/pucks
            // sit at scaled WORLD positions, but Level.Bounds keeps its Awake-time vanilla
            // value. UIMinimap.WorldPositionToMinimapPosition plots each dot as
            // worldPos / Bounds.size * contentPixels, so with vanilla bounds the dots land at
            // arenaScale x too far and overflow the frame. The old fix ballooned the minimap
            // element itself and counter-scaled the dots -- which made the on-screen map grow
            // with the arena. Instead keep the map at the user's chosen size and scale the
            // BOUNDS the game normalizes against (width by world-X, length by world-Z) so dots
            // land correctly on a normal-sized minimap. UIMinimap.Bounds is a public field.
            float baseScale = SettingsManager.MinimapScale;
            _lastAppliedMinimapBaseScale = baseScale;
            minimapEl.style.scale = new UnityEngine.Vector2(baseScale, baseScale);
            contentEl.style.scale = new UnityEngine.Vector2(1f, 1f);

            var level = UnityEngine.Object.FindObjectsByType<Level>(FindObjectsSortMode.None).FirstOrDefault();
            if (level != null)
            {
                var vb = level.Bounds; // vanilla baseline (the mod never modifies Level.Bounds)
                uiMinimap.Bounds = new UnityEngine.Bounds(
                    new UnityEngine.Vector3(vb.center.x * scaleX, vb.center.y, vb.center.z * scaleZ),
                    new UnityEngine.Vector3(vb.size.x * scaleX, vb.size.y, vb.size.z * scaleZ));
                Debug.Log($"[COMPADJUST] Minimap bounds scaled to size {uiMinimap.Bounds.size} (arena {scaleX:F2}x{scaleZ:F2}); map element stays at user size {baseScale:F2}.");
            }
            return true;
        }

        // Coroutine version: yields one frame first so level-spawn geometry is ready,
        // then retries for up to ~1s in case the UIMinimap element is created a few
        // frames late (slow UI document load / frame-timing race).
        private IEnumerator ApplyMinimapScaleCoroutine()
        {
            yield return null; // let UIMinimapController.Event_Everyone_OnLevelSpawned run first
            for (int i = 0; i < 60; i++)
            {
                if (ApplyMinimapScaleNow()) yield break;
                yield return null;
            }
        }

        private static bool IsHeadlessServer()
        {
            // Check multiple indicators of dedicated server
            return SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }
        
        void OnDisable()
        {
            // The panel used to be reachable from the shared mod-menu hub, which put it away
            // for us when the mod was toggled off in the game settings. F4 is the only entry
            // point now, and a disabled component stops ticking Update, so without this the
            // panel would stay on screen with no key left to close it.
            try
            {
                // A rebind in flight owns the keyboard, and CloseDashFallPanel answers that by
                // cancelling the capture instead of closing, so retire the capture first.
                if (_isCapturing) CancelChordCapture();

                if (IsDashFallPanelOpen)
                {
                    // Route through the real close instead of hiding the elements by hand. The
                    // panel now raises the game's mouse-required flag while it is up and only
                    // CloseDashFallPanel lowers it again; left raised with no panel on screen it
                    // also costs the player their stick, because StickAnglePatch reads that flag.
                    CloseDashFallPanel();
                    ConfigManager.Dbg("OnDisable - closed the config panel and restored input");
                }
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
                if (_clientStoppedSubscribedNm != null)
                    _clientStoppedSubscribedNm.OnClientStopped -= OnClientStopped;
                _clientStoppedSubscribedNm = null;
                DashFallMod.GoalNetTweaks.OnTweaksSynced -= OnTweaksSynced;

                // CleanupHUD(); //TODO: Re-enable when HUD is ready

                // Picker lists live on the UIDocument root, not inside the panel, so removing the
                // panel does not take them with it. Without this, toggling the mod off with a
                // trigger list open strands it over the game for the rest of the session with no
                // key left to dismiss it.
                DashFallTheme.CloseAllPickers();

                _dfPanel?.RemoveFromHierarchy();
                _dfBackdrop?.RemoveFromHierarchy();
                _captureOverlay?.RemoveFromHierarchy();

                _dfPanel = null;
                _dfBackdrop = null;
                _captureOverlay = null;
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

                // (Re)bind the disconnect handler lazily; Singleton may have been null at Awake
                // or changed across sessions. Without this, OnClientStopped cleanup (minimap
                // reset + ClearSyncedTweaks so a later vanilla server is treated as vanilla)
                // could silently never run.
                TryBindClientStopped(nm);

                // Re-apply the minimap if the vanilla minimap-size slider changed while arena
                // tweaks are active. No vanilla settings-changed event exists to hook, so we
                // poll the cheap scalar; gated to an active session and to frames where it
                // actually differs, so FindUIMinimap runs only on the rare changed frame.
                if (nm != null && nm.IsConnectedClient
                    && !float.IsNaN(_lastAppliedMinimapBaseScale)
                    && !Mathf.Approximately(SettingsManager.MinimapScale, _lastAppliedMinimapBaseScale)
                    && FindUIMinimap() != null)
                {
                    ApplyMinimapScaleNow();
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

                // Re-request the CPT_sync_config package until it actually arrives.
                // Unlike the ConfigFull retry below this is NOT gated on any UI: the
                // package carries FreeBlade, ball mode, puck scale, the spin limiter and
                // the server-wrapping status, all of which are wrong until it lands, and
                // the player has no way to know they are missing it. The one-shot request
                // in Companion.LoadSyncHandler fires on Event_OnClientStarted, before the
                // connection exists, so on a join where the server's own push is also
                // missed the client sat on local defaults until some unrelated puck spawn
                // happened to ask again (86 seconds, in the log that produced this).
                if (nm != null && nm.IsConnectedClient && !nm.IsServer && _cmm != null
                    && !CompetitiveCompanion.PluginCore.HasReceivedConfigSync
                    && Time.unscaledTime >= _nextConfigSyncRetry)
                {
                    _nextConfigSyncRetry = Time.unscaledTime + 2f;
                    CompetitiveCompanion.PluginCore.RequestConfigSyncFromServer("retry");
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
                
                // F4 opens and closes the config panel; ESC closes it.
                TickPanelHotkeys();

                // Probe UI periodically
                if (Time.unscaledTime >= _nextUIProbeAt)
                {
                    _nextUIProbeAt = Time.unscaledTime + 0.5f;
                    TryWireButtonIfNeeded();
                }

                // Out-of-date Workshop version popup (one-shot Steam check, then show/keep).
                TickVersionPopup();
            }
            catch (System.OverflowException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] Error in Update: {ex.Message}");
            }
        }

        /// <summary>
        /// F4 toggles the config panel, ESC closes it. Both are hardcoded.
        ///
        /// This is polled from Update rather than registered anywhere on purpose. On a
        /// dedicated server the plugin's OnEnable runs BEFORE NetworkManager exists, while on
        /// a host it runs after, so anything wired once at enable time binds on one role and
        /// silently no-ops on the other (that is what killed CPT_request_sync and FreeBlade).
        /// A per-frame Keyboard.current read needs no NetworkManager, no CustomMessagingManager
        /// and no UIDocument, so it cannot half-register. It also cannot run where there is no
        /// UI at all: Awake destroys this whole component on batch mode or a null graphics
        /// device, so the runner only exists on a joined client or a listen host.
        /// </summary>
        private void TickPanelHotkeys()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            // The out-of-date warning is modal and owns ESC (TickVersionPopup dismisses on it),
            // so neither key does anything while it is up. Without this F4 would open the panel
            // underneath a surface the player is being told to act on.
            if (_versionBackdrop != null) return;

            // A rebind in progress owns the whole keyboard. F4 has to stay bindable like any
            // other key, and the capture routine handles its own ESC-to-cancel.
            if (_isCapturing) return;

            // Ask the panel whether it is open instead of reading its display. The two disagree
            // in both directions: a rebind overlay hides the panel while it is still logically
            // open, and a UIDocument root swap nulls the element out from under us.
            bool panelOpen = IsDashFallPanelOpen;

            // Only act on our own key, and only when the game is not eating keystrokes. Chat
            // focus is checked through the mod's existing AnyTextInputFocused so the panel
            // hotkey agrees with the bind dispatcher about what "typing" means.
            if (kb.f4Key.wasPressedThisFrame && !AnyTextInputFocused())
            {
                // No SavePanelEdits here: ToggleDashFallPanel commits the keybinds on its own
                // close branch. Saving here as well would rewrite both config files and rebuild
                // the input actions twice on a single keypress.

                // The panel is built against the UIDocument root that the 0.5s probe resolves,
                // so a press in the window right after a root swap would be a silent no-op.
                // Probe now instead of losing the press.
                if (!panelOpen && _doc == null) TryWireButtonIfNeeded();

                ToggleDashFallPanel();
                ConfigManager.Dbg(panelOpen ? "F4 - closing the config panel" : "F4 - opening the config panel");
                return;
            }

            if (!panelOpen) return;

            if (kb.escapeKey.wasPressedThisFrame)
            {
                SavePanelEdits();
                FullCloseDashFallPanel();
                ConfigManager.Dbg("ESC - closing the config panel");
                return;
            }

            // Hold the mouse-required flag rather than re-asserting the cursor. The cursor was
            // never re-locking itself: ApplicationManager.SetMouseVisibility is the only writer of
            // Cursor.lockState in the game and it only ever reacts to this flag changing. What was
            // actually happening is that UIManager recomputed the flag off whenever one of its own
            // views opened or closed, and the cursor followed. Fixing the flag fixes the cursor,
            // and it also stops the skater receiving the keystrokes meant for the panel.
            HoldPlayerInputSuppressed();
        }

        // Writes the keybind edits made in the panel and rebuilds the dispatch tables from
        // them. Every close path runs this; opening does not, so an abandoned session that
        // ends in a crash leaves the on-disk config alone.
        private void SavePanelEdits()
        {
            DashFallConfigLoader.SaveSkaterConfig(_skater);
            DashFallConfigLoader.SaveGoalieConfig(_goalie);
            RebuildLookups();
            ResetInputActions();
        }

        // Resolves the UIDocument root the panel, capture overlay, HUD and version popup are
        // all parented to, and rebuilds them when the game swaps roots (menu to in-game, and
        // back). Named after the menu buttons it used to create; those are gone with the hub,
        // but the root probe is still what everything else depends on.
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
                    // None of the panel's elements survive the swap, so close it before they go.
                    // Open state is tracked in a flag rather than on the element, and the cursor
                    // snapshot and mouse-required flag taken on open are only handed back by the
                    // close path, so tearing the panel down silently would strand both.
                    if (_isCapturing) CancelChordCapture();
                    if (IsDashFallPanelOpen) CloseDashFallPanel();

                    _lastRoot = root;

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

            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Error resolving the UI root: {e.Message}");
            }
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
