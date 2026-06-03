// Stances.cs - Goalie stance system
// Activates ONLY when extend inputs are pressed WITHOUT the slide key:
//   One extend alone  → half butterfly (one leg down, one leg standing)
//   Both extends      → stance butterfly (both legs down, like butterfly)
// If the player is holding slide (normal butterfly), stances DON'T interfere.
// Seamless transitions between standing ↔ half butterfly ↔ stance butterfly ↔ normal butterfly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace DashFallMod
{
    public static class Stances
    {
        // ============ CONFIGURATION ============
        private const float IDLE_LERP_SPEED = 15f;
        private const float CLIENT_LERP_SPEED = 20f;
        private const float POST_BUTTERFLY_LOCKOUT = 0.1f;
        // ========================================

        // Per-body tracking: which extend sides are held in stance mode
        private static Dictionary<int, (bool extendLeft, bool extendRight)> _stanceState =
            new Dictionary<int, (bool, bool)>();

        // Track when slide was last held per body (for post-butterfly lockout)
        private static Dictionary<int, float> _slideStartTime = new Dictionary<int, float>();

        // Track if slide was pressed while stances were active (force release)
        private static HashSet<int> _forceReleaseUntilClear = new HashSet<int>();

        // Track which leg pads are forced to idle by stances (fully controlled — position overridden)
        private static HashSet<int> _stanceControlledLegs = new HashSet<int>();

        // Legs transitioning to idle — game's native 0.15s tween is playing, we don't override position
        private static Dictionary<int, float> _pendingControlledLegs = new Dictionary<int, float>();

        // Last set position for controlled legs (used by Update prefix to re-apply after DOTween)
        private static Dictionary<int, Vector3> _lastSetPosition = new Dictionary<int, Vector3>();

        // Last set rotation for controlled legs (used by Update prefix to re-apply after DOTween)
        private static Dictionary<int, Quaternion> _lastSetRotation = new Dictionary<int, Quaternion>();

        // Leg position cache (idle, butterfly, extended)
        private static Dictionary<int, (Vector3 idle, Vector3 butterfly, Vector3 extended)> _legPositions =
            new Dictionary<int, (Vector3, Vector3, Vector3)>();

        // Leg rotation cache (idle rotation for the idle-locked leg)
        private static Dictionary<int, Quaternion> _legIdleRotation =
            new Dictionary<int, Quaternion>();

        // Body -> leg pads mapping
        private static Dictionary<int, List<(PlayerLegPad pad, bool isLeft)>> _bodyLegPads =
            new Dictionary<int, List<(PlayerLegPad, bool)>>();

        // Track bodies that had HandleInputs override this frame (for postfix restore)
        private static HashSet<int> _activeThisFrame = new HashSet<int>();

        // CMM for client sync
        private const string CMM_STANCE = "DashFall/HalfButterfly";
        private static CustomMessagingManager _cmm;
        private static bool _cmmRegistered = false;

        // Last broadcast state per body. Stance state is two bools, edge-triggered,
        // so we keep Reliable delivery but skip the send whenever nothing changed
        // since the previous tick (prior code resent every server fixed step).
        // A low-frequency heartbeat (StanceHeartbeatSeconds) re-broadcasts the
        // current state anyway, so a client that joins mid-stance receives it
        // within the heartbeat window instead of only on the next state change.
        private const float StanceHeartbeatSeconds = 1f;
        private struct LastSent { public bool left, right; public float time; }
        private static readonly Dictionary<ulong, LastSent> _lastBroadcast
            = new Dictionary<ulong, LastSent>();

        // Reflection fields
        private static FieldInfo _localPositionField;
        private static FieldInfo _localPositionTweenField;
        private static FieldInfo _localRotationField;
        private static FieldInfo _localRotationTweenField;
        private static FieldInfo _positionsField;

        private static bool _enabled = false;
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    Debug.Log($"[Stances] Enabled = {value}");
                    if (!_enabled) ClearAll();
                }
            }
        }

        static Stances()
        {
            _localPositionField = typeof(PlayerLegPad).GetField("localPosition",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _localPositionTweenField = typeof(PlayerLegPad).GetField("localPositionTween",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _localRotationField = typeof(PlayerLegPad).GetField("localRotation",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _localRotationTweenField = typeof(PlayerLegPad).GetField("localRotationTween",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _positionsField = typeof(PlayerLegPad).GetField("positions",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // =====================================================
        // CMM Registration & Client Sync
        // =====================================================

        public static void EnsureCMMRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            if (_cmm != cmm)
            {
                if (_cmm != null && _cmmRegistered)
                    try { _cmm.UnregisterNamedMessageHandler(CMM_STANCE); } catch { }
                _cmm = cmm;
                _cmmRegistered = false;
            }

            if (!_cmmRegistered && !nm.IsServer)
            {
                try
                {
                    _cmm.RegisterNamedMessageHandler(CMM_STANCE, OnStanceMessage);
                    _cmmRegistered = true;
                }
                catch { }
            }
        }

        private static void NotifyClients(ulong bodyNetId, bool extendLeft, bool extendRight)
        {
            if (_cmm == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            // On a listen-server (host), ConnectedClientsIds includes the host's own
            // client ID 0.  SendNamedMessageToAll skips the local client, so sending
            // when only the host is "connected" produces 'clientIds is empty' errors.
            int remoteClients = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
            if (remoteClients <= 0) return;

            // Dedupe + heartbeat: skip resends when nothing changed AND the
            // heartbeat window hasn't elapsed. Without the heartbeat, a client
            // that joins mid-stance would never see the existing state.
            float now = Time.unscaledTime;
            if (_lastBroadcast.TryGetValue(bodyNetId, out var prev)
                && prev.left == extendLeft && prev.right == extendRight
                && now - prev.time < StanceHeartbeatSeconds)
                return;

            try
            {
                using (var writer = new FastBufferWriter(10, Allocator.Temp))
                {
                    writer.WriteValueSafe(bodyNetId);
                    writer.WriteValueSafe(extendLeft);
                    writer.WriteValueSafe(extendRight);
                    _cmm.SendNamedMessageToAll(CMM_STANCE, writer, NetworkDelivery.Reliable);
                }
                _lastBroadcast[bodyNetId] = new LastSent { left = extendLeft, right = extendRight, time = now };
            }
            catch (Exception e) { Debug.LogWarning("[Stances] NotifyClients send failed: " + e.Message); }
        }

        private static void OnStanceMessage(ulong senderId, FastBufferReader reader)
        {
            if (!_enabled) return;

            try
            {
                reader.ReadValueSafe(out ulong bodyNetId);
                reader.ReadValueSafe(out bool extendLeft);
                reader.ReadValueSafe(out bool extendRight);

                var nm = NetworkManager.Singleton;
                if (nm == null) return;
                if (nm.SpawnManager == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(bodyNetId, out var netObj)) return;

                var body = netObj.GetComponent<PlayerBodyV2>();
                if (body == null) return;

                int bodyId = body.GetInstanceID();
                _stanceState[bodyId] = (extendLeft, extendRight);
            }
            catch (Exception e) { Debug.LogWarning("[Stances] OnStanceMessage decode failed: " + e.Message); }
        }

        // =====================================================
        // HandleInputs Integration (server-side)
        // =====================================================

        /// <summary>
        /// Called from HandleInputs prefix. Updates stance leg control with proper
        /// transitions: legs entering stance get set to Idle, legs leaving stance
        /// while still sliding get set to Butterfly (since OnIsSlidingChanged won't re-fire).
        /// </summary>
        public static void UpdateStanceLegs(PlayerBodyV2 body, bool extendLeft, bool extendRight)
        {
            int bodyId = body.GetInstanceID();
            _stanceState[bodyId] = (extendLeft, extendRight);

            var legPads = GetLegPads(body);
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();

                // Determine if this leg should be forced idle:
                // ExtendLeft held → left leg butterfly → RIGHT leg idle
                // ExtendRight held → right leg butterfly → LEFT leg idle
                // Both extends → no idle legs (both butterfly)
                // No extends → no idle legs (releasing)
                bool shouldBeIdle = false;
                if (extendLeft && !extendRight) shouldBeIdle = !isLeft; // right leg idle
                else if (!extendLeft && extendRight) shouldBeIdle = isLeft; // left leg idle

                bool isPending = _pendingControlledLegs.ContainsKey(padId);
                bool isControlled = _stanceControlledLegs.Contains(padId);

                if (shouldBeIdle)
                {
                    if (!isPending && !isControlled)
                    {
                        // Drop any velocity-extend state for this leg, mirroring the
                        // client's ClientUpdateLegs entry. Without this the server keeps
                        // a stale non-zero _currentExtension for the now-idle leg (the
                        // forced slide means IsSliding never drops, so ReleaseAllLegs
                        // never zeroes it) and then broadcasts it as a phantom extension,
                        // flaring the idle leg outward on remote clients while the server
                        // holds it idle. Only reproduces with both systems + sliding.
                        GoalieDashExtend.ClearLegForStance(padId);

                        // Leg is newly entering stance — start "pending" phase.
                        // We DON'T add to _stanceControlledLegs yet, so Update_Prefix
                        // won't override position and the game tween plays naturally.
                        CacheLegPositions(pad, padId);
                        _pendingControlledLegs[padId] = Time.unscaledTime;
                        if (pad.State != PlayerLegPadState.Idle)
                        {
                            // State isn't Idle yet — kill existing tween and set State=Idle
                            // to create the game's native 0.15s tween to idle.
                            KillGameTween(pad);
                            pad.State = PlayerLegPadState.Idle;
                        }
                        // If State is already Idle, an existing Butterfly→Idle tween may
                        // be playing — let it continue undisturbed.
                    }
                    // else: already pending or controlled — no action needed
                }
                else
                {
                    // Leg should NOT be idle — release from any stance tracking
                    if (isPending)
                    {
                        _pendingControlledLegs.Remove(padId);
                        if (body.IsSliding.Value)
                            pad.State = PlayerLegPadState.Butterfly;
                    }
                    else if (isControlled)
                    {
                        _stanceControlledLegs.Remove(padId);
                        _lastSetPosition.Remove(padId);
                        _lastSetRotation.Remove(padId);
                        if (body.IsSliding.Value)
                            pad.State = PlayerLegPadState.Butterfly;
                    }

                    // Robust full-butterfly transition: when both extends are held,
                    // ensure both legs are explicitly in butterfly state.
                    if (extendLeft && extendRight &&
                        pad.State != PlayerLegPadState.Butterfly)
                    {
                        pad.State = PlayerLegPadState.Butterfly;
                    }
                }
            }
        }

        /// <summary>
        /// Release all stance control, transitioning legs cleanly.
        /// Called when exiting stance mode entirely.
        /// </summary>
        public static void ReleaseStance(PlayerBodyV2 body, bool forceButterfly = false)
        {
            int bodyId = body.GetInstanceID();
            bool wasActive = _stanceState.TryGetValue(bodyId, out var prev) &&
                            (prev.extendLeft || prev.extendRight);
            _stanceState[bodyId] = (false, false);

            if (!wasActive) return;

            // Server-side: explicitly notify clients that stance mode ended.
            // Without this, clients can keep stale half-butterfly state visually
            // because UpdateStances exits early when both extends are false.
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                var netObj = body.GetComponent<NetworkObject>();
                if (netObj != null)
                    NotifyClients(netObj.NetworkObjectId, false, false);
            }

            var legPads = GetLegPads(body);
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();

                bool wasPending = _pendingControlledLegs.Remove(padId);
                bool wasControlled = _stanceControlledLegs.Remove(padId);

                if (wasPending || wasControlled)
                {
                    _lastSetPosition.Remove(padId);
                    _lastSetRotation.Remove(padId);
                    if (forceButterfly || body.IsSliding.Value)
                        pad.State = PlayerLegPadState.Butterfly;
                }
            }
        }

        /// <summary>
        /// Mark body as having active stance override (called from HandleInputs prefix)
        /// </summary>
        public static void MarkActive(int bodyId) => _activeThisFrame.Add(bodyId);

        /// <summary>
        /// Check and clear active mark (called from HandleInputs postfix)
        /// </summary>
        public static bool IsMarkedActive(int bodyId) => _activeThisFrame.Remove(bodyId);

        // =====================================================
        // Public Queries
        // =====================================================

        /// <summary>
        /// Check if a specific leg pad is controlled by stances (forced idle)
        /// </summary>
        public static bool IsControlledByStance(int padId) =>
            _stanceControlledLegs.Contains(padId) || _pendingControlledLegs.ContainsKey(padId);

        /// <summary>
        /// Try to get the controlled position for a pad (used by Update prefix)
        /// </summary>
        public static bool TryGetControlledPosition(int padId, out Vector3 position)
        {
            if (_stanceControlledLegs.Contains(padId) && _lastSetPosition.TryGetValue(padId, out position))
                return true;
            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Try to get the controlled rotation for a pad (used by Update prefix)
        /// </summary>
        public static bool TryGetControlledRotation(int padId, out Quaternion rotation)
        {
            if (_stanceControlledLegs.Contains(padId) && _lastSetRotation.TryGetValue(padId, out rotation))
                return true;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// Get current stance state for a body
        /// </summary>
        public static (bool extendLeft, bool extendRight) GetState(PlayerBodyV2 body)
        {
            int bodyId = body.GetInstanceID();
            if (_stanceState.TryGetValue(bodyId, out var state))
                return state;
            return (false, false);
        }

        /// <summary>
        /// Check if any stance is active (at least one extend held in stance mode)
        /// </summary>
        public static bool IsStanceActive(PlayerBodyV2 body)
        {
            var state = GetState(body);
            return state.extendLeft || state.extendRight;
        }

        /// <summary>
        /// Check if half butterfly specifically is active (exactly one extend held)
        /// </summary>
        public static bool IsHalfButterflyActive(PlayerBodyV2 body)
        {
            var state = GetState(body);
            return (state.extendLeft || state.extendRight) && !(state.extendLeft && state.extendRight);
        }

        // =====================================================
        // Main Server Update (called from FixedUpdate postfix)
        // =====================================================

        /// <summary>
        /// Server-side update: applies idle leg position overrides for controlled legs.
        /// Leg membership in _stanceControlledLegs is managed by HandleInputs prefix.
        /// </summary>
        public static void UpdateStances(PlayerBodyV2 body)
        {
            if (!_enabled || body == null) return;
            if (body.Player == null) return;
            if (body.Player.Role != PlayerRole.Goalie) return;

            int bodyId = body.GetInstanceID();
            if (!_stanceState.TryGetValue(bodyId, out var state)) return;

            bool extLeft = state.extendLeft;
            bool extRight = state.extendRight;

            if (!extLeft && !extRight) return;
            if (!body.IsSliding.Value) return;

            var legPads = GetLegPads(body);

            // Graduate pending legs whose game tween has completed
            if (_pendingControlledLegs.Count > 0)
            {
                foreach (var (pad, isLeft) in legPads)
                {
                    if (pad == null) continue;
                    int padId = pad.GetInstanceID();
                    if (!_pendingControlledLegs.TryGetValue(padId, out float startTime)) continue;

                    CacheLegPositions(pad, padId);
                    if (!_legPositions.TryGetValue(padId, out var pos)) continue;

                    Vector3 currentPos = GetPadLocalPosition(pad);
                    float distSq = (currentPos - pos.idle).sqrMagnitude;
                    float elapsed = Time.unscaledTime - startTime;

                    // Graduate when near idle position OR after 0.3s (game tween is 0.15s)
                    if (distSq < 0.0001f || elapsed > 0.3f)
                    {
                        _pendingControlledLegs.Remove(padId);
                        _stanceControlledLegs.Add(padId);
                        _lastSetPosition[padId] = currentPos;
                        // Also cache idle rotation at graduation so Update_Prefix
                        // can immediately hold it — prevents a 1-frame gap where
                        // a game rotation tween can leave the pad stuck mid-rotation.
                        if (_legIdleRotation.TryGetValue(padId, out var idleRot))
                            _lastSetRotation[padId] = idleRot;
                    }
                }
            }

            // Apply idle position overrides on fully controlled legs
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();
                if (!_stanceControlledLegs.Contains(padId)) continue;

                CacheLegPositions(pad, padId);
                if (!_legPositions.TryGetValue(padId, out var positions)) continue;

                KillGameTween(pad);

                Vector3 currentPos = GetPadLocalPosition(pad);
                Vector3 targetPos = positions.idle;
                Vector3 newPos = Vector3.Lerp(currentPos, targetPos, Time.fixedDeltaTime * IDLE_LERP_SPEED);

                try
                {
                    _localPositionField.SetValue(pad, newPos);
                    _lastSetPosition[padId] = newPos;
                }
                catch { }

                // Also hold rotation at idle to keep visual and collider orientation in sync
                if (_localRotationField != null && _legIdleRotation.TryGetValue(padId, out var idleRot))
                {
                    try
                    {
                        _localRotationField.SetValue(pad, idleRot);
                        _lastSetRotation[padId] = idleRot;
                    }
                    catch { }
                }
            }

            // Notify clients
            var netObj = body.GetComponent<NetworkObject>();
            if (netObj != null) NotifyClients(netObj.NetworkObjectId, extLeft, extRight);
        }

        /// <summary>
        /// Client-side update: overrides idle leg position from CMM state.
        /// </summary>
        public static void ClientUpdateLegs(PlayerBodyV2 body)
        {
            if (!_enabled || body == null) return;
            if (body.Player == null) return;
            if (body.Player.Role != PlayerRole.Goalie) return;

            int bodyId = body.GetInstanceID();
            if (!_stanceState.TryGetValue(bodyId, out var state)) return;

            bool extLeft = state.extendLeft;
            bool extRight = state.extendRight;

            if (!extLeft && !extRight)
            {
                ReleaseAllLegsClient(body);
                return;
            }

            if (!body.IsSliding.Value)
            {
                ReleaseAllLegsClient(body);
                return;
            }

            if (_localPositionField == null) return;

            var legPads = GetLegPads(body);
            float dt = Time.fixedDeltaTime;

            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();

                // Mirror server logic: only one extend = one idle leg, both = none idle
                bool shouldBeIdle = false;
                if (extLeft && !extRight) shouldBeIdle = !isLeft;
                else if (!extLeft && extRight) shouldBeIdle = isLeft;

                if (!shouldBeIdle)
                {
                    bool wasPending = _pendingControlledLegs.Remove(padId);
                    bool wasControlled = _stanceControlledLegs.Remove(padId);
                    _lastSetPosition.Remove(padId);
                    _lastSetRotation.Remove(padId);

                    // Client visual sync: when transitioning half -> full butterfly,
                    // force released idle leg back to butterfly immediately.
                    if ((wasPending || wasControlled) && body.IsSliding.Value)
                        pad.State = PlayerLegPadState.Butterfly;

                    // Also force full-butterfly state when both extends are active,
                    // even if this leg was not tracked as pending/controlled due timing.
                    if (extLeft && extRight && body.IsSliding.Value &&
                        pad.State != PlayerLegPadState.Butterfly)
                        pad.State = PlayerLegPadState.Butterfly;
                    continue;
                }

                bool isPending = _pendingControlledLegs.ContainsKey(padId);
                bool isControlled = _stanceControlledLegs.Contains(padId);

                if (!isPending && !isControlled)
                {
                    // Enter the SAME pending phase the server uses in UpdateStanceLegs
                    // rather than snapping straight to the idle pose. The pad collider
                    // the puck reacts to is server-authoritative and the puck position is
                    // replicated, so the rendered pad must track the server pad. Snapping
                    // here raised the client-drawn pad to idle up to ~0.3s before the
                    // server pad tweened there, so on remote clients the puck looked like
                    // it hit a pad drawn somewhere else. On a host (server and client on
                    // one machine) there is no gap, which is why it only ever looked right
                    // there. Letting the game's native 0.15s tween play, exactly like the
                    // server, keeps the rendered pad tracking the authoritative pad within
                    // network latency. GoalieDashExtend yields the whole time because
                    // IsControlledByStance is true for pending legs too.
                    GoalieDashExtend.ClearLegForStance(padId); // drop stale velocity-extend state
                    CacheLegPositions(pad, padId);
                    _pendingControlledLegs[padId] = Time.unscaledTime;
                    if (pad.State != PlayerLegPadState.Idle)
                    {
                        KillGameTween(pad);
                        pad.State = PlayerLegPadState.Idle; // game plays its 0.15s tween to idle
                    }
                    continue;
                }

                if (isPending)
                {
                    // Check for graduation
                    CacheLegPositions(pad, padId);
                    if (!_legPositions.TryGetValue(padId, out var pendPos)) continue;
                    Vector3 cur = GetPadLocalPosition(pad);
                    float distSq = (cur - pendPos.idle).sqrMagnitude;
                    float elapsed = Time.unscaledTime - _pendingControlledLegs[padId];
                    if (distSq < 0.0001f || elapsed > 0.3f)
                    {
                        _pendingControlledLegs.Remove(padId);
                        _stanceControlledLegs.Add(padId);
                        _lastSetPosition[padId] = cur;
                        if (_legIdleRotation.TryGetValue(padId, out var gradIdleRot))
                            _lastSetRotation[padId] = gradIdleRot;
                    }
                    continue;
                }

                // Fully controlled — apply lerp
                CacheLegPositions(pad, padId);
                if (!_legPositions.TryGetValue(padId, out var positions)) continue;

                KillGameTween(pad);

                Vector3 currentPos = GetPadLocalPosition(pad);
                Vector3 targetPos = positions.idle;
                Vector3 newPos = Vector3.Lerp(currentPos, targetPos, dt * CLIENT_LERP_SPEED);

                try
                {
                    _localPositionField.SetValue(pad, newPos);
                    _lastSetPosition[padId] = newPos;
                }
                catch { }

                // Also hold rotation at idle to keep visual and collider orientation in sync
                if (_localRotationField != null && _legIdleRotation.TryGetValue(padId, out var idleRot))
                {
                    try
                    {
                        _localRotationField.SetValue(pad, idleRot);
                        _lastSetRotation[padId] = idleRot;
                    }
                    catch { }
                }
            }
        }

        // =====================================================
        // Helper Methods
        // =====================================================

        private static List<(PlayerLegPad pad, bool isLeft)> GetLegPads(PlayerBodyV2 body)
        {
            int bodyId = body.GetInstanceID();

            if (_bodyLegPads.TryGetValue(bodyId, out var cached))
            {
                if (cached.Count > 0 && cached[0].pad != null)
                    return cached;
            }

            var legPads = new List<(PlayerLegPad, bool)>();
            var mesh = body.GetComponentInChildren<PlayerMesh>();
            if (mesh != null)
            {
                foreach (var pad in mesh.GetComponentsInChildren<PlayerLegPad>())
                {
                    if (pad != null)
                    {
                        bool isLeftLeg = body.transform.InverseTransformPoint(pad.transform.position).x < 0;
                        legPads.Add((pad, isLeftLeg));
                    }
                }
            }

            _bodyLegPads[bodyId] = legPads;
            return legPads;
        }

        private static void CacheLegPositions(PlayerLegPad pad, int padId)
        {
            if (_legPositions.ContainsKey(padId)) return;
            if (_positionsField == null) return;

            try
            {
                var positions = _positionsField.GetValue(pad) as IDictionary<PlayerLegPadState, Transform>;
                if (positions != null)
                {
                    Vector3 idlePos = Vector3.zero;
                    Vector3 butterflyPos = Vector3.zero;
                    Vector3 extendedPos = Vector3.zero;

                    if (positions.TryGetValue(PlayerLegPadState.Idle, out var idleTrans))
                    {
                        idlePos = idleTrans.localPosition;
                        _legIdleRotation[padId] = idleTrans.localRotation;
                    }
                    if (positions.TryGetValue(PlayerLegPadState.Butterfly, out var butterflyTrans))
                        butterflyPos = butterflyTrans.localPosition;
                    if (positions.TryGetValue(PlayerLegPadState.ButterflyExtended, out var extendedTrans))
                        extendedPos = extendedTrans.localPosition;

                    _legPositions[padId] = (idlePos, butterflyPos, extendedPos);
                }
            }
            catch { }
        }

        private static Vector3 GetPadLocalPosition(PlayerLegPad pad)
        {
            if (_localPositionField != null)
            {
                try { return (Vector3)_localPositionField.GetValue(pad); }
                catch { }
            }
            return Vector3.zero;
        }

        private static void KillGameTween(PlayerLegPad legPad)
        {
            // Kill position tween
            if (_localPositionTweenField != null)
            {
                try
                {
                    var tween = _localPositionTweenField.GetValue(legPad) as Tween;
                    if (tween != null && tween.IsActive()) tween.Kill(false);
                    _localPositionTweenField.SetValue(legPad, null);
                }
                catch { }
            }
            // Kill rotation tween — keeps pad orientation in sync with position control.
            if (_localRotationTweenField != null)
            {
                try
                {
                    var tween = _localRotationTweenField.GetValue(legPad) as Tween;
                    if (tween != null && tween.IsActive()) tween.Kill(false);
                    _localRotationTweenField.SetValue(legPad, null);
                }
                catch { }
            }
        }

        // Snap localRotation to the target state's rotation from the positions dict.
        private static void SnapRotationToState(PlayerLegPad legPad, PlayerLegPadState targetState)
        {
            if (_localRotationField == null || _positionsField == null) return;
            try
            {
                var positions = _positionsField.GetValue(legPad) as IDictionary;
                if (positions == null) return;
                object entry = positions[targetState];
                if (entry is Transform t)
                    _localRotationField.SetValue(legPad, t.localRotation);
            }
            catch { }
        }

        private static void ReleaseAllLegsClient(PlayerBodyV2 body)
        {
            var legPads = GetLegPads(body);
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();
                bool wasControlled = _stanceControlledLegs.Remove(padId);
                bool wasPending   = _pendingControlledLegs.Remove(padId);
                _lastSetPosition.Remove(padId);
                _lastSetRotation.Remove(padId);

                // Transition the leg back to Butterfly so it doesn't briefly flare to
                // ButterflyExtended when GoalieDashExtend picks it up the same frame.
                if ((wasControlled || wasPending) && body.IsSliding.Value)
                    pad.State = PlayerLegPadState.Butterfly;
            }
        }

        /// <summary>
        /// Block state changes that would interfere with stance control.
        /// </summary>
        public static bool ShouldBlockStateChange(PlayerLegPad legPad, PlayerLegPadState currentState, PlayerLegPadState newState)
        {
            if (!_enabled || legPad == null) return false;

            int padId = legPad.GetInstanceID();
            // Block on both pending and fully controlled legs
            if (!_stanceControlledLegs.Contains(padId) && !_pendingControlledLegs.ContainsKey(padId))
                return false;

            if (newState == PlayerLegPadState.Butterfly || newState == PlayerLegPadState.ButterflyExtended)
                return true;

            return false;
        }

        /// <summary>
        /// Record the start time when butterfly (slide) begins.
        /// Only records once per slide session (not every frame).
        /// </summary>
        public static void RecordSlideStart(int bodyId, bool isSliding)
        {
            if (isSliding)
            {
                if (!_slideStartTime.ContainsKey(bodyId))
                    _slideStartTime[bodyId] = Time.unscaledTime;
            }
            else
            {
                _slideStartTime.Remove(bodyId);
            }
        }

        /// <summary>
        /// Check if the game's native extend should be suppressed (within 0.2s of butterfly start).
        /// Only applies while in butterfly (slide held).
        /// </summary>
        public static bool ShouldSuppressExtendInButterfly(int bodyId)
        {
            if (_slideStartTime.TryGetValue(bodyId, out float startTime))
                return (Time.unscaledTime - startTime) < POST_BUTTERFLY_LOCKOUT;
            return false;
        }

        // ----- Force-release after ctrl tap during stances -----

        public static void MarkForceRelease(int bodyId)
        {
            _forceReleaseUntilClear.Add(bodyId);
        }

        public static bool IsForceReleased(int bodyId)
        {
            return _forceReleaseUntilClear.Contains(bodyId);
        }

        public static void ClearForceRelease(int bodyId)
        {
            _forceReleaseUntilClear.Remove(bodyId);
        }

        public static void ClearAll()
        {
            _stanceState.Clear();
            _pendingControlledLegs.Clear();
            _stanceControlledLegs.Clear();
            _lastSetPosition.Clear();
            _lastSetRotation.Clear();
            _legPositions.Clear();
            _legIdleRotation.Clear();
            _bodyLegPads.Clear();
            _activeThisFrame.Clear();
            _slideStartTime.Clear();
            _forceReleaseUntilClear.Clear();
            _lastBroadcast.Clear();
        }

        /// <summary>
        /// Tear down the CMM handler and clear all state. Called from
        /// DashFallGameMod.OnDisable so the handler does not linger after
        /// a plugin disable/enable cycle.
        /// </summary>
        public static void Disable()
        {
            if (_cmm != null && _cmmRegistered)
            {
                try { _cmm.UnregisterNamedMessageHandler(CMM_STANCE); } catch { }
            }
            _cmm = null;
            _cmmRegistered = false;
            ClearAll();
        }

        public static void OnLegPadDestroyed(PlayerLegPad legPad)
        {
            if (legPad != null)
            {
                int padId = legPad.GetInstanceID();
                _pendingControlledLegs.Remove(padId);
                _stanceControlledLegs.Remove(padId);
                _lastSetPosition.Remove(padId);
                _lastSetRotation.Remove(padId);
                _legPositions.Remove(padId);
                _legIdleRotation.Remove(padId);
            }
        }

        /// <summary>
        /// Drop body- and NetworkObjectId-keyed bookkeeping when a body despawns.
        /// Leg-pad-keyed state is cleared separately by OnLegPadDestroyed; these maps
        /// would otherwise linger until a full session teardown calls ClearAll.
        /// </summary>
        public static void OnBodyDespawned(PlayerBodyV2 body)
        {
            if (body == null) return;
            int bodyId = body.GetInstanceID();
            _stanceState.Remove(bodyId);
            _bodyLegPads.Remove(bodyId);
            _slideStartTime.Remove(bodyId);
            _forceReleaseUntilClear.Remove(bodyId);
            var netObj = body.GetComponent<NetworkObject>();
            if (netObj != null) _lastBroadcast.Remove(netObj.NetworkObjectId);
        }
    }

    // =====================================================
    // Harmony Patches for Stances
    // =====================================================

    /// <summary>
    /// Patch HandleInputs to intercept extend inputs when the player is NOT holding slide.
    /// When slide is held → normal butterfly, don't interfere at all.
    /// When slide is NOT held + extends pressed → stance mode:
    ///   - Force slide on, suppress extends
    ///   - Pre-mark controlled legs with transitions
    /// </summary>
    [HarmonyPatch(typeof(PlayerBodyV2), "HandleInputs")]
    public static class HandleInputs_HalfButterfly_Patch
    {
        [ThreadStatic] private static bool _origSlideServer;
        [ThreadStatic] private static bool _origExtendLeftServer;
        [ThreadStatic] private static bool _origExtendRightServer;
        [ThreadStatic] private static bool _origSlideClient;
        [ThreadStatic] private static bool _origExtendLeftClient;
        [ThreadStatic] private static bool _origExtendRightClient;

        [HarmonyPrefix]
        public static void Prefix(PlayerBodyV2 __instance, global::PlayerInput playerInput)
        {
            if (!Stances.Enabled) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (__instance.Player == null) return;
            if (__instance.Player.Role != PlayerRole.Goalie) return;

            // Host path can observe client values before server values settle for the frame.
            bool slideHeld = playerInput.SlideInput.ServerValue || playerInput.SlideInput.ClientValue;
            bool extendLeft = playerInput.ExtendLeftInput.ServerValue || playerInput.ExtendLeftInput.ClientValue;
            bool extendRight = playerInput.ExtendRightInput.ServerValue || playerInput.ExtendRightInput.ClientValue;

            // If player is holding slide → normal butterfly, stances don't interfere
            if (slideHeld)
            {
                int bodyId = __instance.GetInstanceID();
                Stances.RecordSlideStart(bodyId, true);

                // If stances were active, mark force-release so they don't
                // re-engage when ctrl is released while extends are still held
                if (Stances.IsStanceActive(__instance))
                    Stances.MarkForceRelease(bodyId);

                Stances.ReleaseStance(__instance, true);

                // Suppress extends briefly after entering butterfly
                if (Stances.ShouldSuppressExtendInButterfly(bodyId))
                {
                    playerInput.ExtendLeftInput.ServerValue = false;
                    playerInput.ExtendRightInput.ServerValue = false;
                    playerInput.ExtendLeftInput.ClientValue = false;
                    playerInput.ExtendRightInput.ClientValue = false;
                }
                return;
            }

            // Not sliding — clear slide start tracker
            Stances.RecordSlideStart(__instance.GetInstanceID(), false);

            // No extends held → not in stance mode, and clears force-release flag
            if (!extendLeft && !extendRight)
            {
                Stances.ClearForceRelease(__instance.GetInstanceID());
                Stances.ReleaseStance(__instance);
                return;
            }

            // If force-release is active, don't enter stances until all extends are released.
            // Also ensure stances are fully released (defensive — clears stale state).
            if (Stances.IsForceReleased(__instance.GetInstanceID()))
            {
                Stances.ReleaseStance(__instance);
                return;
            }

            // Not grounded → don't enter stances (mirrors game's IsSliding gate on IsGrounded)
            // This prevents stale stance state from mid-air extend presses
            if (!__instance.IsGrounded)
            {
                Stances.ReleaseStance(__instance);
                return;
            }

            // Extend(s) held WITHOUT slide → STANCE MODE
            // Save original inputs for postfix restore
            _origSlideServer = playerInput.SlideInput.ServerValue;
            _origExtendLeftServer = playerInput.ExtendLeftInput.ServerValue;
            _origExtendRightServer = playerInput.ExtendRightInput.ServerValue;
            _origSlideClient = playerInput.SlideInput.ClientValue;
            _origExtendLeftClient = playerInput.ExtendLeftInput.ClientValue;
            _origExtendRightClient = playerInput.ExtendRightInput.ClientValue;

            // Force slide on so the game enters/maintains sliding
            playerInput.SlideInput.ServerValue = true;
            playerInput.SlideInput.ClientValue = true;

            // Suppress extends so game doesn't set IsExtendedLeft/Right
            playerInput.ExtendLeftInput.ServerValue = false;
            playerInput.ExtendRightInput.ServerValue = false;
            playerInput.ExtendLeftInput.ClientValue = false;
            playerInput.ExtendRightInput.ClientValue = false;

            // Update which legs are controlled (with transition logic)
            // after forcing slide so leg state transitions are applied reliably.
            Stances.UpdateStanceLegs(__instance, extendLeft, extendRight);

            Stances.MarkActive(__instance.GetInstanceID());
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance, global::PlayerInput playerInput)
        {
            if (!Stances.IsMarkedActive(__instance.GetInstanceID())) return;

            // Restore original input values
            playerInput.SlideInput.ServerValue = _origSlideServer;
            playerInput.ExtendLeftInput.ServerValue = _origExtendLeftServer;
            playerInput.ExtendRightInput.ServerValue = _origExtendRightServer;
            playerInput.SlideInput.ClientValue = _origSlideClient;
            playerInput.ExtendLeftInput.ClientValue = _origExtendLeftClient;
            playerInput.ExtendRightInput.ClientValue = _origExtendRightClient;
        }
    }

    /// <summary>
    /// Leg pad patches for Stances - block state changes and re-apply position.
    /// </summary>
    [HarmonyPatch]
    public static class StancesLegPadPatches
    {
        /// <summary>
        /// Block Butterfly/ButterflyExtended state changes on stance-controlled (idle) legs.
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), nameof(PlayerLegPad.State), MethodType.Setter)]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.HigherThanNormal)]
        public static bool State_Prefix(PlayerLegPad __instance, PlayerLegPadState value)
        {
            if (!Stances.Enabled) return true;

            if (Stances.ShouldBlockStateChange(__instance, __instance.State, value))
                return false;

            return true;
        }

        // Cache fields for Update prefix
        private static FieldInfo _updateLocalPositionField = typeof(PlayerLegPad).GetField("localPosition",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _updateLocalRotationField = typeof(PlayerLegPad).GetField("localRotation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _updateTweenField = typeof(PlayerLegPad).GetField("localPositionTween",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _updateRotTweenField = typeof(PlayerLegPad).GetField("localRotationTween",
            BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Re-apply idle position AND rotation after DOTween overwrites.
        /// Also kills both tweens the game may have created since last FixedUpdate.
        /// Runs AFTER GoalieDashExtend's Update prefix so Stances gets final word.
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), "Update")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.LowerThanNormal)]
        public static void Update_Prefix(PlayerLegPad __instance)
        {
            if (!Stances.Enabled) return;
            if (_updateLocalPositionField == null) return;

            int padId = __instance.GetInstanceID();
            if (Stances.TryGetControlledPosition(padId, out Vector3 pos))
            {
                // Kill position tween
                if (_updateTweenField != null)
                {
                    try
                    {
                        var tween = _updateTweenField.GetValue(__instance) as Tween;
                        if (tween != null && tween.IsActive()) tween.Kill(false);
                        _updateTweenField.SetValue(__instance, null);
                    }
                    catch { }
                }
                // Kill rotation tween too
                if (_updateRotTweenField != null)
                {
                    try
                    {
                        var tween = _updateRotTweenField.GetValue(__instance) as Tween;
                        if (tween != null && tween.IsActive()) tween.Kill(false);
                        _updateRotTweenField.SetValue(__instance, null);
                    }
                    catch { }
                }
                // Reapply position
                try { _updateLocalPositionField.SetValue(__instance, pos); } catch { }
                // Reapply rotation if we have a cached value
                if (_updateLocalRotationField != null &&
                    Stances.TryGetControlledRotation(padId, out Quaternion rot))
                {
                    try { _updateLocalRotationField.SetValue(__instance, rot); } catch { }
                }
            }
        }

        /// <summary>
        /// Clean up when leg pad is destroyed.
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), "OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix(PlayerLegPad __instance)
        {
            Stances.OnLegPadDestroyed(__instance);
        }
    }
}
