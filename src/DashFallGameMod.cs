// File: DashFallGameMod.cs
// Main entry point for the DashFall mod
// Orchestrates all sub-modules: Dash, Dive, Twist, SlideInfluence

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using PoncePuck.Keybinds;
using DashFallMod;

public sealed class DashFallGameMod
{
    private Harmony _harmony;
    
    // Config is accessed directly via DashFallMod.ConfigManager.Config
    
    public DashFallGameMod() { }
    
    public bool OnEnable()
    {
        try
        {
            bool isHeadless = Application.isBatchMode ||
                              SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            bool isServerRuntime = Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer;

            // Only server/headless should create/read CompetitiveAdjustments.json.
            if (isHeadless || isServerRuntime)
            {
                ConfigManager.EnsureConfig();
                ConfigManager.ReloadConfig();
            }
            
            ServerBridge.Hook("DashFall");
            ServerBridge.OnAction += OnClientAction;
            
            _harmony = new Harmony("net.poncepuck.dashfall");
            _harmony.PatchAll(typeof(DashFallGameMod));
            HarmonyPatchHelper.PatchNamespaces(_harmony, "DashFallMod", "PoncePuck.Keybinds", "PonceMods.Shared");

            // Wrapped-position decode. Installed once, here, for every role, and never
            // removed: it is inert unless a record arrives carrying wire bit 16384, which
            // vanilla cannot produce (max attainable ChangeMask is 8191). Installing it
            // permanently is what lets the SERVER be the only thing that arms, instead of
            // two switches that have to agree.
            DashFallMod.Net.WrapSyncDecode.Install();

            // Wrapped-position encode. SERVER ROLES ONLY.
            //
            // This used to install on every role, on the reasoning that the hooks are
            // no-ops off the server path so installing them everywhere was free. It is not
            // free. WrapSyncEncode prefixes the SynchronizedObjectData constructor, and
            // SyncRangePatch TRANSPILES that same constructor, live, the moment a synced
            // arena resize arrives. Being a no-op at runtime does not make a patch absent
            // at patch time: it still changes what Harmony has to compose on a struct
            // constructor that sits directly in the deserialization path.
            //
            // A pure client gains nothing from encode -- it never serializes object data --
            // so the only thing installing it there could do was cost. A listen host still
            // gets the hooks, because ServerBridge calls this again (it is idempotent) once
            // NetworkManager reports IsServer.
            if (isHeadless || isServerRuntime)
                DashFallMod.Net.WrapSyncEncode.Install();
            
            // b1117 renamed the spawn broadcast to Event_Everyone_OnPlayerBodySpawned,
            // and role changes are now delivered through the combined
            // Event_Everyone_OnPlayerGameStateChanged (phase/team/role) event.
            EventManager.AddEventListener("Event_Everyone_OnPlayerBodySpawned", OnBodySpawned);
            EventManager.AddEventListener("Event_Everyone_OnPlayerGameStateChanged", OnRoleChanged);

            // Arena state belongs to the LEVEL, not to the players standing on it. This
            // used to come online only when the first PlayerBodyV2 spawned, which left a
            // dedicated server with no arena tick and no level-spawn hook until then, so
            // the rink sat at vanilla size between a level load and the first spawn.
            GoalNetTweaks.EnsureRunner();

            // Initialize client immediately if not headless
            
            if (!isHeadless)
            {
                InitializeClientComponents();
            }
            
            ConfigManager.Dbg("Enabled - Modules: Dash, Dive, Twist, SlideInfluence");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[COMPADJUST] FATAL ERROR in OnEnable: {ex}");
            return false;
        }
    }
    
    private static void InitializeClientComponents()
    {
        try
        {
            var go = new GameObject("DashFallClientRunner");
            go.AddComponent<DashFallMod.Client.DashFallClientRunner>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[COMPADJUST] Failed to initialize client: {ex}");
        }
    }
    
    public bool OnDisable()
    {
        ServerBridge.OnAction -= OnClientAction;
        ServerBridge.Unhook();
        _harmony?.UnpatchSelf();

        EventManager.RemoveEventListener("Event_Everyone_OnPlayerBodySpawned", OnBodySpawned);
        EventManager.RemoveEventListener("Event_Everyone_OnPlayerGameStateChanged", OnRoleChanged);

        // Tear down CMM handlers our subsystems registered via the per-frame
        // EnsureCMMRegistered polls. Without this, a plugin disable/re-enable
        // cycle leaves the old handlers attached against the previous CMM.
        try { GoalieDashExtend.Disable(); } catch (Exception e) { ConfigManager.Dbg("GoalieDashExtend.Disable failed: " + e.Message); }
        try { Stances.Disable(); } catch (Exception e) { ConfigManager.Dbg("Stances.Disable failed: " + e.Message); }

        // This mod no longer registers with the shared mod-menu hub: the config panel is opened
        // with F4 (DashFallClientRunner.TickPanelHotkeys) and nothing else.
        //
        // src/ModMenuHub.cs is still compiled, but be clear about why, because the reason written
        // here first was wrong. It does NOT participate in any cross-assembly pruning on our
        // behalf: other assemblies only read _localCallbacks, and our permanently empty dictionary
        // answers exactly what an absent type would. Nothing calls Initialize or RegisterMod any
        // more, so our copy can never become the hub's primary runner, and the file is unreachable
        // from this assembly. It stays purely because the other Ponce mods ship the same file and
        // keeping the copies identical makes them easier to diff. It can be deleted whenever that
        // stops being worth 48KB.

        // Destroy the client runner
        var clientRunner = UnityEngine.Object.FindFirstObjectByType<DashFallMod.Client.DashFallClientRunner>();
        if (clientRunner != null)
        {
            UnityEngine.Object.Destroy(clientRunner.gameObject);
        }

        // Pair for the EnsureRunner() in OnEnable. The arena runner is
        // DontDestroyOnLoad and owns the level-spawn hook and the chunked position
        // patches, so leaving it running meant a disabled mod kept resizing the rink
        // and kept a non-vanilla wire encoding installed on a server.
        try { GoalNetTweaks.ShutdownRunner(); } catch (Exception e) { ConfigManager.Dbg("GoalNetTweaks.ShutdownRunner failed: " + e.Message); }

        ConfigManager.Dbg("Disabled");
        return true;
    }
    
    private void OnBodySpawned(Dictionary<string, object> msg)
    {
        var body = msg?["playerBody"] as PlayerBodyV2;
        if (body != null) DashMod.EnableDash(body);
        LogSpawnPose(body);
    }

    /// <summary>
    /// One-shot pose dump for the local player's body at spawn.
    ///
    /// This exists because an "I spawn in upside down" report produced a log with
    /// literally nothing in it: this codebase logs no rotation value anywhere, on any
    /// path, so there was no way to tell an inverted BODY from an inverted CAMERA, or
    /// either from a decode that never delivered a valid pose. Three investigations ran
    /// on inference alone. Sixteen fields of text at spawn ends that argument.
    ///
    /// Read it like this:
    ///   up.y near +1        upright.        up.y near -1  inverted.
    ///   kinematic=True      the body is driven from the network, so a wrong pose means
    ///                       a wrong or missing record, not local physics.
    ///   kinematic=False     the body simulates locally and the whole externally-driven
    ///                       model is wrong; that would be the real finding.
    ///   camLocalEuler       PlayerCamera rewrites its own localRotation every frame from
    ///                       the look angle, so if the body is upright and the view is not,
    ///                       the flip is in the camera's PARENT chain, printed here.
    /// </summary>
    private static void LogSpawnPose(PlayerBodyV2 body)
    {
        try
        {
            if (body == null || !body.IsOwner) return;   // local player only

            var t = body.transform;
            var rb = body.GetComponent<Rigidbody>();
            var cam = body.GetComponentInChildren<Camera>(true);

            string camInfo = "(none)";
            if (cam != null)
            {
                var chain = "";
                for (var p = cam.transform.parent; p != null; p = p.parent)
                    chain = p.name + "/" + chain;
                camInfo = $"parent='{chain}' localEuler={cam.transform.localEulerAngles} "
                        + $"worldEuler={cam.transform.eulerAngles} lossyScale={cam.transform.lossyScale}";
            }

            ConfigManager.Log(
                "SPAWN POSE (local): "
                + $"pos={t.position} euler={t.eulerAngles} up={t.up} "
                + $"lossyScale={t.lossyScale} "
                + $"kinematic={(rb != null ? rb.isKinematic.ToString() : "no-rb")} "
                + $"parent='{(t.parent != null ? t.parent.name : "(root)")}' "
                + $"| camera {camInfo}");

            // The spawn-instant sample is necessary but not sufficient: it fires on the
            // spawn event, BEFORE the first decoded network pose has been applied, so it
            // always reports the prefab default. It proved the body starts upright and is
            // kinematic; it cannot catch an inversion that arrives one network tick later.
            // Attach a watcher for that.
            if (t.GetComponent<UprightWatch>() == null)
                t.gameObject.AddComponent<UprightWatch>();
        }
        catch (Exception e)
        {
            ConfigManager.Dbg("LogSpawnPose failed: " + e.Message);
        }
    }

    /// <summary>
    /// Watches the local body's orientation and logs the transition into and out of
    /// "not upright", with the raw quaternion.
    ///
    /// The raw quaternion is the point. If an inverted body reads exactly
    /// (x=1, y=0, z=0, w=0) then it is 180 degrees about X, which is precisely what
    /// Puck's QuaternionCompressor.DecompressQuaternion(0) returns for an all-zero
    /// rotation field. That would mean the client is being handed a record with no
    /// rotation data and vanilla is decoding the hole into a flip, rather than anything
    /// rotating the body locally. Any other quaternion points somewhere else entirely.
    ///
    /// Self-limiting: samples at 5 Hz, logs only on state change plus a bounded number of
    /// periodic samples, and stops sampling once it has seen enough of a stable state.
    /// </summary>
    private sealed class UprightWatch : MonoBehaviour
    {
        private const float SampleInterval = 0.2f;
        private const int MaxTransitionLogs = 12;

        private float _next;
        private bool _wasUpright = true;
        private bool _primed;
        private int _logs;
        private float _started;

        private void Start() { _started = Time.time; }

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + SampleInterval;

            var t = transform;
            bool upright = t.up.y >= 0.5f;

            if (!_primed)
            {
                _primed = true;
                _wasUpright = upright;
                if (!upright) Report(t, "INVERTED ON FIRST SAMPLE");
                return;
            }

            if (upright != _wasUpright)
            {
                _wasUpright = upright;
                Report(t, upright ? "returned UPRIGHT" : "became INVERTED");
            }
        }

        private void Report(Transform t, string what)
        {
            if (_logs++ >= MaxTransitionLogs)
            {
                if (_logs == MaxTransitionLogs + 1)
                    ConfigManager.Log("Upright watch: further transitions suppressed (flapping).");
                return;
            }

            var q = t.rotation;
            var rb = GetComponent<Rigidbody>();
            ConfigManager.Log(
                $"UPRIGHT WATCH ({what}) at t+{Time.time - _started:F2}s: "
                + $"quat=({q.x:F4}, {q.y:F4}, {q.z:F4}, {q.w:F4}) "
                + $"euler={t.eulerAngles} up={t.up} pos={t.position} "
                + $"kinematic={(rb != null ? rb.isKinematic.ToString() : "no-rb")}");

            // Call out the one shape that identifies a decoded hole rather than a rotation.
            if (Mathf.Abs(q.x) > 0.99f && Mathf.Abs(q.w) < 0.01f
                && Mathf.Abs(q.y) < 0.01f && Mathf.Abs(q.z) < 0.01f)
                Debug.LogWarning(
                    "[COMPADJUST] The body rotation is EXACTLY 180 degrees about X, which is what "
                    + "DecompressQuaternion(0) returns for an all-zero rotation field. That points at "
                    + "a record arriving with no rotation data, not at anything rotating the body.");
        }
    }

    private void OnRoleChanged(Dictionary<string, object> msg)
    {
        if (msg == null) return;
        var player = msg["player"] as Player;
        if (player?.PlayerBody == null) return;

        // Event_Everyone_OnPlayerGameStateChanged fires on any game-state change
        // (phase / team / role). Preserve the old Event_OnPlayerRoleChanged
        // semantics by acting only when the role actually changed.
        if (msg.TryGetValue("oldGameState", out var oldObj) && msg.TryGetValue("newGameState", out var newObj)
            && oldObj is PlayerGameState oldState && newObj is PlayerGameState newState
            && oldState.Role == newState.Role)
            return;

        DashMod.EnableDash(player.PlayerBody);

        // A goalie that switches to a skater stops being ticked by the goalie leg
        // systems (UpdateStances / UpdateVelocityExtend both bail on non-goalies)
        // before they can broadcast their cleared state, which can strand a stale
        // stance or extended pose on remote clients. Release explicitly on the
        // transition away from goalie. Fires on server (notifies clients) and on
        // clients (clears local state); a body still goalie is left untouched.
        if (player.Role != PlayerRole.Goalie)
        {
            try { Stances.ReleaseStance(player.PlayerBody); } catch { }
            try { GoalieDashExtend.ReleaseBody(player.PlayerBody); } catch { }
        }
    }
    
    // ===== Bridge actions =====
    private static void OnClientAction(ulong clientId, string action, bool isDown)
    {
        if (!isDown) return; // edge trigger on down
        var p = FindPlayerByClientId(clientId);
        if (p == null || p.PlayerBody == null) return;
        
        var cfg = ConfigManager.Config;
        bool isGoalie = p.Role == PlayerRole.Goalie;
        
        if (action == "dashleft" || action == "dashright")
        {
            // Skater dash disabled — goalies use base game dash
        }
        else if (action == "goaliestandingdashleft" || action == "goaliestandingdashright")
        {
            // Goalie standing dash from custom keybinds - check if feature is enabled
            if (isGoalie && cfg.GoalieStandingDashEnabled)
            {
                DashMod.ProcessGoalieStandingDash(p.PlayerBody, action == "goaliestandingdashleft");
            }
        }
        else if (action == "dive")
        {
            bool allowed = isGoalie ? cfg.GoalieDiveEnabled : cfg.SkaterDiveEnabled;
            if (allowed)
            {
                DiveMod.TryStartDiveNow(p.PlayerInput);
            }
        }
        else if (action == "twistleft" || action == "twistright")
        {
            bool allowed = isGoalie ? cfg.GoalieTwistWhileSlidingEnabled : cfg.EnableTwistWhileSliding;
            if (allowed)
            {
                TwistMod.ProcessTwistFromBridge(p.PlayerBody, action == "twistleft");
            }
        }
    }
    
    private static Player FindPlayerByClientId(ulong clientId)
    {
        foreach (var pi in UnityEngine.Object.FindObjectsByType<PlayerInput>(UnityEngine.FindObjectsSortMode.None))
            if (pi != null && pi.Player != null && pi.Player.OwnerClientId == clientId) return pi.Player;
        return null;
    }
}
// ServerBridge is now in DashFall.ServerBridge.cs
