// GoalieDashExtend.cs - Pure velocity-based goalie leg kick system
// Lateral velocity determines which leg extends and HOW FAR (gradual extension)

using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace DashFallMod
{
    /// <summary>
    /// Pure velocity-based leg kick system with GRADUAL extension:
    /// - Lateral velocity determines which leg extends (opposite to movement direction)
    /// - Speed determines HOW FAR the leg extends (smooth 0-1 curve)
    /// - At 0 lateral speed = butterfly position (no extend)
    /// - At max speed = full ButterflyExtended position
    /// - Smooth interpolation in between for natural acceleration/deceleration
    /// </summary>
    public static class GoalieDashExtend
    {
        // ============ CONFIGURATION ============
        // Speed range for extension curve (m/s lateral velocity)
        private const float DEFAULT_MIN_SPEED_FOR_EXTEND = 0.1f;
        private const float DEFAULT_MAX_SPEED_FOR_EXTEND = 1f;
        
        // Extension curve power (1 = linear, <1 = fast start/slow end, >1 = slow start/fast end)
        private const float DEFAULT_EXTENSION_CURVE_POWER = 1.5f;
        
        // How fast the extension lerps to target (higher = snappier)
        private const float EXTENSION_LERP_SPEED = 12f;
        
        // Client-side lerp speed (faster than server to track received values closely)
        private const float CLIENT_LERP_SPEED = 25f;
        
        // Tween duration for drop animation (same as game's default)
        private const float TWEEN_DURATION = 0.15f;
        // ========================================
        
        // Track current extension amount per leg pad (0-1)
        private static Dictionary<int, float> _currentExtension = new Dictionary<int, float>();
        
        // Track target extension per leg pad (0-1)
        private static Dictionary<int, float> _targetExtension = new Dictionary<int, float>();
        
        // Cache leg pad positions for interpolation (idle, butterfly, extended)
        private static Dictionary<int, (Vector3 idle, Vector3 butterfly, Vector3 extended)> _legPositions = 
            new Dictionary<int, (Vector3, Vector3, Vector3)>();
        
        // Track body -> leg pads mapping
        private static Dictionary<int, List<(PlayerLegPad pad, bool isLeft)>> _bodyLegPads = 
            new Dictionary<int, List<(PlayerLegPad, bool)>>();
        
        // Track which legs we're actively controlling
        private static HashSet<int> _controlledLegs = new HashSet<int>();
        
        // Track drop animation progress per leg (0-1, for idle->butterfly transition)
        private static Dictionary<int, float> _dropAnimProgress = new Dictionary<int, float>();
        
        // Track if a leg is in drop animation
        private static HashSet<int> _inDropAnimation = new HashSet<int>();
        
        // Track start position for drop animation (actual position when drop begins)
        private static Dictionary<int, Vector3> _dropStartPosition = new Dictionary<int, Vector3>();
        
        // Track legs that are in a game-native extend (player pressed Q/E) - yield control
        private static HashSet<int> _gameNativeExtend = new HashSet<int>();
        
        // Track legs that need the game tween killed on next update frame
        // (because game creates the tween AFTER our postfix runs in OnStateChanged)
        private static HashSet<int> _pendingTweenKill = new HashSet<int>();
        
        // Cache the last position we set for each controlled pad
        // Used by the Update prefix to re-apply after DOTween overwrites
        private static Dictionary<int, Vector3> _lastSetPosition = new Dictionary<int, Vector3>();
        
        // Reflection to access private fields
        private static FieldInfo _localPositionField;
        private static FieldInfo _localPositionTweenField;
        private static FieldInfo _positionsField;
        
        // Flag set during DashLeft/DashRight execution to distinguish dash extend from Q/E extend
        [ThreadStatic] internal static bool IsDashExtending;
        
        // CMM for client sync
        private const string CMM_VELOCITY_EXTEND = "DashFall/VelocityExtend3";
        private static CustomMessagingManager _cmm;
        private static bool _cmmRegistered = false;

        // Server-side broadcast throttle. Prior code sent every fixed step
        // (~50 Hz) which generated a lot of reliable traffic per goalie. We
        // still send Reliable (terminal states must latch on clients) but
        // skip frames whose values have not changed meaningfully since the
        // last send. A forced flush fires whenever a value crosses the snap
        // thresholds (0 / 1) so the final state is always delivered.
        private const float SendIntervalSeconds = 1f / 20f;
        private const float SendDeltaThreshold = 0.02f;
        private struct SendState { public float lastLeft, lastRight, lastTime; }
        private static readonly Dictionary<ulong, SendState> _sendState = new Dictionary<ulong, SendState>();
        
        private static bool _enabled = false;
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    Debug.Log($"[GoalieDashExtend] Enabled = {value}");
                    if (!_enabled)
                    {
                        ClearAll();
                    }
                }
            }
        }
        
        static GoalieDashExtend()
        {
            // Cache reflection fields
            _localPositionField = typeof(PlayerLegPad).GetField("localPosition", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            _localPositionTweenField = typeof(PlayerLegPad).GetField("localPositionTween",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _positionsField = typeof(PlayerLegPad).GetField("positions",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }
        
        public static void EnsureCMMRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;
            
            if (_cmm != cmm)
            {
                if (_cmm != null && _cmmRegistered)
                {
                    try { _cmm.UnregisterNamedMessageHandler(CMM_VELOCITY_EXTEND); } catch { }
                }
                _cmm = cmm;
                _cmmRegistered = false;
            }
            
            if (!_cmmRegistered && !nm.IsServer)
            {
                try
                {
                    _cmm.RegisterNamedMessageHandler(CMM_VELOCITY_EXTEND, OnVelocityExtendMessage);
                    _cmmRegistered = true;
                }
                catch (Exception e) { Debug.LogWarning("[GoalieDashExtend] Register CMM handler failed: " + e.Message); }
            }
        }
        
        private static void ClientDbg(string msg)
        {
            if (Client.DashFallConfigLoader.ClientConfig?.EnableClientDebug == true)
                Debug.Log(msg);
        }
        
        /// <summary>
        /// Client receives velocity extend state update with extension amounts
        /// </summary>
        private static void OnVelocityExtendMessage(ulong senderId, FastBufferReader reader)
        {
            if (!_enabled) return;
            
            try
            {
                reader.ReadValueSafe(out ulong bodyNetId);
                reader.ReadValueSafe(out float leftExtension);
                reader.ReadValueSafe(out float rightExtension);
                
                var nm = NetworkManager.Singleton;
                if (nm == null) return;
                if (nm.SpawnManager == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(bodyNetId, out var netObj)) return;
                
                var body = netObj.GetComponent<PlayerBodyV2>();
                if (body == null) return;
                
                // Apply the extension amounts locally for visual sync
                ApplyExtensionTargets(body, leftExtension, rightExtension);
            }
            catch (Exception e)
            {
                ClientDbg($"[GoalieDashExtend] Error in OnVelocityExtendMessage: {e.Message}");
            }
        }
        
        /// <summary>
        /// Notify clients about extension amounts. Reliable in every case so
        /// clients can't miss a terminal state. Throttled to ~20 Hz by default
        /// but flushed immediately whenever a value crosses the snap thresholds
        /// (0 / 1) or moves more than SendDeltaThreshold from the last sent
        /// value, so clients still see smooth transitions.
        /// </summary>
        private static void NotifyClientsExtension(ulong bodyNetId, float leftExtension, float rightExtension)
        {
            if (_cmm == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            // On a listen-server (host), ConnectedClientsIds includes the host's own
            // client ID 0.  SendNamedMessageToAll skips the local client, so sending
            // when only the host is "connected" produces 'clientIds is empty' errors.
            int remoteClients = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
            if (remoteClients <= 0) return;

            _sendState.TryGetValue(bodyNetId, out var s);
            float now = Time.unscaledTime;
            bool crossedSnap =
                Crossed(s.lastLeft, leftExtension) || Crossed(s.lastRight, rightExtension);
            bool bigDelta =
                Mathf.Abs(s.lastLeft - leftExtension) >= SendDeltaThreshold
                || Mathf.Abs(s.lastRight - rightExtension) >= SendDeltaThreshold;
            bool dueByTime = now - s.lastTime >= SendIntervalSeconds;

            // Reliable delivery already latched the previous identical values on every
            // connected client, so the time-based heartbeat only benefits late joiners.
            // A resting (0,0) goalie needs no resend at all (clients default to no
            // extend), so suppress the heartbeat while the value is unchanged AND at
            // rest. Without this guard a standing goalie re-broadcast (0,0) reliably at
            // SendIntervalSeconds (20 Hz) forever, once per non-sliding goalie.
            bool unchanged = leftExtension == s.lastLeft && rightExtension == s.lastRight;
            bool atRest = leftExtension == 0f && rightExtension == 0f;
            if (unchanged && atRest) return;

            if (!crossedSnap && !bigDelta && !dueByTime) return;

            try
            {
                using (var writer = new FastBufferWriter(16, Allocator.Temp))
                {
                    writer.WriteValueSafe(bodyNetId);
                    writer.WriteValueSafe(leftExtension);
                    writer.WriteValueSafe(rightExtension);
                    _cmm.SendNamedMessageToAll(CMM_VELOCITY_EXTEND, writer, NetworkDelivery.Reliable);
                }
                _sendState[bodyNetId] = new SendState { lastLeft = leftExtension, lastRight = rightExtension, lastTime = now };
            }
            catch (Exception e) { Debug.LogWarning("[GoalieDashExtend] Velocity-extend send failed: " + e.Message); }
        }

        // Snap thresholds inside Mathf.Lerp clamps in ClientUpdateLegs / UpdateLegPositions
        // are 0.01 and 0.99. A value transitioning across either threshold must always
        // be delivered so clients latch the terminal state.
        private static bool Crossed(float a, float b)
        {
            return (a < 0.01f) != (b < 0.01f) || (a > 0.99f) != (b > 0.99f);
        }

        /// <summary>
        /// Read the per-leg extensions exactly as the broadcast block in
        /// UpdateVelocityExtend would send them, including the yield to Stances, so the
        /// value recorded into a replay tick matches the value that went out live on the
        /// same tick. Used by the replay recorder postfix in ReplayLegPads.cs.
        /// </summary>
        public static void TryGetBodyExtensions(PlayerBodyV2 body, out float left, out float right)
        {
            left = 0f;
            right = 0f;
            if (!_enabled || body == null) return;

            foreach (var (pad, isLeft) in GetLegPads(body))
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();
                // Same reason as the live broadcast: a stance-owned leg can still hold a
                // stale non-zero _currentExtension from a prior slide, and recording it
                // would flare the idle leg on playback.
                if (Stances.IsControlledByStance(padId)) continue;
                if (!_currentExtension.TryGetValue(padId, out float ext)) continue;
                if (isLeft) left = ext; else right = ext;
            }
        }

        /// <summary>
        /// Replay playback entry point. Deliberately routes through the unmodified live
        /// throttle: a replay body has a fresh NetworkObjectId so _sendState has no entry
        /// and the first send always passes, and thereafter replay ticks are 0.0667s
        /// apart against a 0.05s interval, so every tick passes anyway.
        /// </summary>
        public static void ServerBroadcastExtension(ulong bodyNetId, float left, float right)
            => NotifyClientsExtension(bodyNetId, left, right);

        /// <summary>
        /// Listen-server local apply, mirroring what OnVelocityExtendMessage does for a
        /// remote client. SendNamedMessageToAll skips the host's own client.
        /// </summary>
        public static void ClientApplyExtension(PlayerBodyV2 body, float left, float right)
        {
            if (!_enabled || body == null) return;
            ApplyExtensionTargets(body, left, right);
        }

        /// <summary>
        /// Get or cache the leg pads for a body
        /// </summary>
        private static List<(PlayerLegPad pad, bool isLeft)> GetLegPads(PlayerBodyV2 body)
        {
            int bodyId = body.GetInstanceID();
            
            if (_bodyLegPads.TryGetValue(bodyId, out var cached))
            {
                // Validate cache is still good
                if (cached.Count > 0 && cached[0].pad != null)
                    return cached;
            }
            
            // Build cache
            var legPads = new List<(PlayerLegPad, bool)>();
            var mesh = body.GetComponentInChildren<PlayerMesh>();
            if (mesh != null)
            {
                foreach (var pad in mesh.GetComponentsInChildren<PlayerLegPad>())
                {
                    if (pad != null)
                    {
                        bool isLeft = DetermineIsLeftLeg(pad, body);
                        legPads.Add((pad, isLeft));
                    }
                }
            }
            
            _bodyLegPads[bodyId] = legPads;
            return legPads;
        }
        
        /// <summary>
        /// Determine if a leg pad is the left or right leg
        /// </summary>
        private static bool DetermineIsLeftLeg(PlayerLegPad legPad, PlayerBodyV2 body)
        {
            Vector3 localPos = body.transform.InverseTransformPoint(legPad.transform.position);
            return localPos.x < 0;
        }
        
        /// <summary>
        /// Calculate extension amount from lateral speed (0-1 curve)
        /// </summary>
        private static float CalculateExtension(float lateralSpeed)
        {
            float absSpeed = Mathf.Abs(lateralSpeed);
            float minSpeed = Mathf.Max(0f, ConfigManager.Config?.GoalieDashExtendMinSpeedForExtend ?? DEFAULT_MIN_SPEED_FOR_EXTEND);
            float maxSpeed = Mathf.Max(minSpeed + 0.001f, ConfigManager.Config?.GoalieDashExtendMaxSpeedForExtend ?? DEFAULT_MAX_SPEED_FOR_EXTEND);
            float curvePower = Mathf.Max(0.01f, ConfigManager.Config?.GoalieDashExtendCurvePower ?? DEFAULT_EXTENSION_CURVE_POWER);
            
            if (absSpeed < minSpeed)
                return 0f;
            
            if (absSpeed >= maxSpeed)
                return 1f;
            
            // Normalize to 0-1 range
            float t = (absSpeed - minSpeed) / (maxSpeed - minSpeed);
            
            // Apply curve
            return Mathf.Pow(t, curvePower);
        }
        
        /// <summary>
        /// Main update - called every FixedUpdate for each goalie body
        /// Calculates extension amounts based on velocity and applies them
        /// </summary>
        public static void UpdateVelocityExtend(PlayerBodyV2 body)
        {
            if (!_enabled || body == null) return;
            if (body.Player == null) return;
            if (body.Player.Role != PlayerRole.Goalie) return;
            if (!body.IsSliding.Value) 
            {
                // Server-side: explicitly clear client extension targets on stand-up.
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                {
                    var netObj = body.GetComponent<NetworkObject>();
                    if (netObj != null)
                        NotifyClientsExtension(netObj.NetworkObjectId, 0f, 0f);
                }

                // Not sliding - release control of all legs, let game handle them
                ReleaseAllLegs(body);
                return;
            }
            
            // Get velocity
            Vector3 velocity = body.Rigidbody.linearVelocity;
            velocity.y = 0;
            
            // Get lateral and forward components
            Vector3 right = body.transform.right;
            right.y = 0;
            right.Normalize();
            Vector3 forward = body.transform.forward;
            forward.y = 0;
            forward.Normalize();
            
            float lateralSpeed = Vector3.Dot(velocity, right);
            float forwardSpeed = Mathf.Abs(Vector3.Dot(velocity, forward));
            float absLateralSpeed = Mathf.Abs(lateralSpeed);
            
            // Calculate extension amounts
            // Moving left (negative) = RIGHT leg extends
            // Moving right (positive) = LEFT leg extends
            float leftExtension = 0f;
            float rightExtension = 0f;
            
            // Only extend if lateral movement is significant compared to forward
            // Lateral must be at least 50% of forward speed to trigger
            float minSpeedForExtend = Mathf.Max(0f, ConfigManager.Config?.GoalieDashExtendMinSpeedForExtend ?? DEFAULT_MIN_SPEED_FOR_EXTEND);
            bool lateralDominant = absLateralSpeed > minSpeedForExtend && 
                                   (forwardSpeed < 0.5f || absLateralSpeed > forwardSpeed * 0.5f);
            
            if (lateralDominant)
            {
                if (lateralSpeed > 0)
                {
                    // Moving right - LEFT leg extends
                    leftExtension = CalculateExtension(lateralSpeed);
                }
                else
                {
                    // Moving left - RIGHT leg extends
                    rightExtension = CalculateExtension(lateralSpeed);
                }
            }
            
            // Apply the extension targets
            ApplyExtensionTargets(body, leftExtension, rightExtension);
            
            // Update leg positions with lerp
            UpdateLegPositions(body);
            
            // Notify clients (server only) - send actual current extensions (post-lerp, smooth)
            // NOT raw targets, so clients get the server's already-smoothed values
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                var netObj = body.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    var legPads = GetLegPads(body);
                    float leftCurrent = 0f, rightCurrent = 0f;
                    foreach (var (pad, isLeft) in legPads)
                    {
                        if (pad == null) continue;
                        int padId = pad.GetInstanceID();
                        // Never broadcast an extension for a leg the stance system owns
                        // (the half-butterfly idle leg). Its _currentExtension can be a
                        // stale non-zero value left over from a prior slide, which would
                        // tell remote clients to flare the idle leg outward even though
                        // the server holds it idle. Leaving it at 0 sends "no extend".
                        if (Stances.IsControlledByStance(padId)) continue;
                        if (_currentExtension.TryGetValue(padId, out float ext))
                        {
                            if (isLeft) leftCurrent = ext;
                            else rightCurrent = ext;
                        }
                    }
                    NotifyClientsExtension(netObj.NetworkObjectId, leftCurrent, rightCurrent);
                }
            }
        }
        
        /// <summary>
        /// Release control of all legs for a body (when not sliding)
        /// Let the game handle leg positions naturally
        /// </summary>
        private static void ReleaseAllLegs(PlayerBodyV2 body)
        {
            var legPads = GetLegPads(body);
            
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                
                int padId = pad.GetInstanceID();
                _controlledLegs.Remove(padId);
                _targetExtension.Remove(padId);
                _currentExtension.Remove(padId);
                _dropAnimProgress.Remove(padId);
                _inDropAnimation.Remove(padId);
                _dropStartPosition.Remove(padId);
                _gameNativeExtend.Remove(padId);
                _pendingTweenKill.Remove(padId);
            }
        }
        
        /// <summary>
        /// Set target extension amounts for legs - pure velocity, no game extend tracking
        /// </summary>
        private static void ApplyExtensionTargets(PlayerBodyV2 body, float leftExtension, float rightExtension)
        {
            var legPads = GetLegPads(body);
            
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                
                int padId = pad.GetInstanceID();
                float targetExt = isLeft ? leftExtension : rightExtension;
                
                _targetExtension[padId] = targetExt;
                
                if (!_currentExtension.ContainsKey(padId))
                    _currentExtension[padId] = (pad.State == PlayerLegPadState.Idle) ? 0f : targetExt;
                
                CacheLegPositions(pad, padId);
                
                // Skip pads in game-native extend - fully yield to the game
                if (_gameNativeExtend.Contains(padId))
                    continue;
                
                // Skip pads controlled by Stances (half butterfly idle leg)
                if (Stances.IsControlledByStance(padId))
                {
                    _controlledLegs.Remove(padId);
                    _lastSetPosition.Remove(padId);
                    continue;
                }
                
                if (targetExt > 0.01f || _currentExtension[padId] > 0.01f)
                {
                    _controlledLegs.Add(padId);
                }
                else
                {
                    _controlledLegs.Remove(padId);
                    _lastSetPosition.Remove(padId);
                }
            }
        }
        
        /// <summary>
        /// Cache all three positions: idle, butterfly, extended
        /// </summary>
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
                        idlePos = idleTrans.localPosition;
                    if (positions.TryGetValue(PlayerLegPadState.Butterfly, out var butterflyTrans))
                        butterflyPos = butterflyTrans.localPosition;
                    if (positions.TryGetValue(PlayerLegPadState.ButterflyExtended, out var extendedTrans))
                        extendedPos = extendedTrans.localPosition;
                    
                    _legPositions[padId] = (idlePos, butterflyPos, extendedPos);
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Update leg positions with smooth lerp toward target
        /// </summary>
        private static void UpdateLegPositions(PlayerBodyV2 body)
        {
            if (_localPositionField == null) return;
            
            var legPads = GetLegPads(body);
            float dt = Time.fixedDeltaTime;
            
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                
                int padId = pad.GetInstanceID();
                
                if (!_targetExtension.TryGetValue(padId, out float target))
                    target = 0f;
                if (!_currentExtension.TryGetValue(padId, out float current))
                    current = 0f;
                
                // Skip pads in game-native extend - game controls them
                if (_gameNativeExtend.Contains(padId))
                    continue;
                
                // Skip pads controlled by Stances (half butterfly idle leg)
                if (Stances.IsControlledByStance(padId))
                    continue;
                
                // Handle drop animation (idle -> butterfly/extended)
                if (_inDropAnimation.Contains(padId))
                {
                    // Kill game's tween if it's running - it would fight our position
                    KillGameTween(pad);
                    
                    if (!_dropAnimProgress.TryGetValue(padId, out float progress))
                        progress = 0f;
                    
                    // Advance progress (based on TWEEN_DURATION of 0.15s)
                    progress += dt / TWEEN_DURATION;
                    
                    if (progress >= 1f)
                    {
                        // Animation complete
                        progress = 1f;
                        _inDropAnimation.Remove(padId);
                        _dropStartPosition.Remove(padId);
                    }
                    if (!_legPositions.TryGetValue(padId, out var dropPositions))
                        continue;
                    
                    // Target = the velocity-extended position (between butterfly and extended based on velocity)
                    // This is where the pad should END UP after the drop
                    Vector3 targetPos = Vector3.Lerp(dropPositions.butterfly, dropPositions.extended, target);
                    
                    // Start from where the pad actually was when the drop began
                    // Must use the private localPosition field (NOT transform.localPosition which includes raycast Y)
                    Vector3 startPos = _dropStartPosition.TryGetValue(padId, out var cached) 
                        ? cached : GetPadLocalPosition(pad);
                    
                    // Lerp directly from standing position to the velocity-extended target
                    Vector3 dropPos = Vector3.Lerp(startPos, targetPos, progress);
                    
                    try
                    {
                        _localPositionField.SetValue(pad, dropPos);
                        _lastSetPosition[padId] = dropPos;
                    }
                    catch { }
                    
                    // Update current extension to match target during animation
                    _currentExtension[padId] = target;
                    continue;
                }
                
                // Normal velocity extension update (not in drop animation)
                // Lerp current toward target
                float newCurrent = Mathf.Lerp(current, target, dt * EXTENSION_LERP_SPEED);
                
                // Snap to 0 or 1 if very close
                if (newCurrent < 0.01f) newCurrent = 0f;
                if (newCurrent > 0.99f) newCurrent = 1f;
                
                _currentExtension[padId] = newCurrent;
                
                // Only control if we're in the controlled set
                if (!_controlledLegs.Contains(padId))
                    continue;
                
                // Kill game's DOTween every frame while we have control
                // The game's tween continuously writes to localPosition and fights our values
                if (_pendingTweenKill.Remove(padId))
                    KillGameTween(pad);
                KillGameTween(pad);
                
                if (!_legPositions.TryGetValue(padId, out var positions))
                    continue;
                
                // Target position based on velocity extension amount
                Vector3 velocityTargetPos = Vector3.Lerp(positions.butterfly, positions.extended, newCurrent);
                
                // Read the pad's current private localPosition field (NOT transform.localPosition
                // which has raycast Y and is in a different space)
                Vector3 currentPadPos = GetPadLocalPosition(pad);
                
                // Always lerp FROM the pad's current actual position TOWARD the velocity target
                // This means the pad never snaps - it smoothly moves from wherever it is
                Vector3 newPos = Vector3.Lerp(currentPadPos, velocityTargetPos, dt * EXTENSION_LERP_SPEED);
                
                try
                {
                    _localPositionField.SetValue(pad, newPos);
                    _lastSetPosition[padId] = newPos;
                }
                catch { }
            }
        }
        
        /// <summary>
        /// Check if a leg is being controlled by velocity system
        /// </summary>
        public static bool IsControlled(int padId)
        {
            return _controlledLegs.Contains(padId);
        }
        
        /// <summary>
        /// Get current extension amount for a leg (0-1)
        /// </summary>
        public static float GetCurrentExtension(int padId)
        {
            return _currentExtension.TryGetValue(padId, out float ext) ? ext : 0f;
        }
        
        /// <summary>
        /// Read the pad's private localPosition field (the game's internal position, NOT transform.localPosition)
        /// transform.localPosition includes raycast Y which is in a different space and causes flying
        /// </summary>
        private static Vector3 GetPadLocalPosition(PlayerLegPad pad)
        {
            if (_localPositionField != null)
            {
                try { return (Vector3)_localPositionField.GetValue(pad); }
                catch { }
            }
            return Vector3.zero;
        }
        
        /// <summary>
        /// Called when leg pad state changes - we intercept to create our own drop animation
        /// ONLY for legs that have velocity extension active
        /// </summary>
        public static void OnStateChange(PlayerLegPad legPad, PlayerLegPadState oldState, PlayerLegPadState newState)
        {
            if (!_enabled || legPad == null) return;
            
            int padId = legPad.GetInstanceID();
            
            // Detect game-native extend: Butterfly -> ButterflyExtended
            // Player pressed Q/E to extend - fully yield control to the game.
            // We only fix the starting position so the game's tween begins from where
            // our velocity system had the pad, instead of the default butterfly pos.
            if (oldState == PlayerLegPadState.Butterfly && newState == PlayerLegPadState.ButterflyExtended)
            {
                _gameNativeExtend.Add(padId);
                _controlledLegs.Remove(padId);
                _lastSetPosition.Remove(padId);
                _inDropAnimation.Remove(padId);
                _dropAnimProgress.Remove(padId);
                _dropStartPosition.Remove(padId);
                _pendingTweenKill.Remove(padId);
                
                // The game's OnStateChanged (which just ran) set:
                //   localPosition = positions[Butterfly].localPosition  (default butterfly)
                // then created a tween FROM that toward positions[ButterflyExtended].
                // If velocity had the pad somewhere else, override localPosition so the
                // game's tween starts from the velocity position instead.
                // NOTE: The tween uses a getter (() => localPosition) so changing the field
                // before the tween's first evaluation makes it start from our value.
                if (_currentExtension.TryGetValue(padId, out float ext) && ext > 0.01f)
                {
                    if (_legPositions.TryGetValue(padId, out var pos))
                    {
                        Vector3 currentVelocityPos = Vector3.Lerp(pos.butterfly, pos.extended, ext);
                        try { _localPositionField?.SetValue(legPad, currentVelocityPos); } catch { }
                    }
                }
                return;
            }
            
            // Detect game-native extend ending: ButterflyExtended -> Butterfly
            // Player released Q/E extend - game tweens to default butterfly, but velocity
            // system may want the leg somewhere else. Kill game tween and take back control.
            if (oldState == PlayerLegPadState.ButterflyExtended && newState == PlayerLegPadState.Butterfly)
            {
                _gameNativeExtend.Remove(padId);
                
                // If velocity system has a target extension, kill the game tween and
                // smoothly return to the velocity position instead of default butterfly
                if (_targetExtension.TryGetValue(padId, out float targetExt) && targetExt > 0.01f)
                {
                    KillGameTween(legPad);
                    _pendingTweenKill.Add(padId); // Game creates tween after postfix
                    
                    // Capture where the pad is right now (full extended position)
                    Vector3 currentPos = GetPadLocalPosition(legPad);
                    _dropStartPosition[padId] = currentPos;
                    _dropAnimProgress[padId] = 0f;
                    _inDropAnimation.Add(padId);
                    _controlledLegs.Add(padId);
                }
                // else: no velocity extension active, let game tween to default butterfly
                return;
            }
            
            // If leaving ButterflyExtended to anything else, clear native extend tracking
            if (oldState == PlayerLegPadState.ButterflyExtended)
            {
                _gameNativeExtend.Remove(padId);
            }
            
            // Handle Idle -> Butterfly/Extended transition (the drop animation)
            if (oldState == PlayerLegPadState.Idle && 
                (newState == PlayerLegPadState.Butterfly || newState == PlayerLegPadState.ButterflyExtended))
            {
                // Only intercept if this leg has velocity extension (>0)
                if (!_targetExtension.TryGetValue(padId, out float targetExt) || targetExt <= 0.01f)
                    return; // Let game handle normal drop animation
                
                CacheLegPositions(legPad, padId);
                if (!_legPositions.TryGetValue(padId, out var positions))
                    return;
                
                // Kill game's position tween only - game still handles rotation
                KillGameTween(legPad);
                
                // Capture the pad's current private localPosition field
                // NOT transform.localPosition (which includes raycast Y and would cause flying)
                Vector3 actualStartPos = GetPadLocalPosition(legPad);
                
                // Start drop animation from actual position to velocity-extended position
                _dropStartPosition[padId] = actualStartPos;
                _dropAnimProgress[padId] = 0f;
                _inDropAnimation.Add(padId);
                _controlledLegs.Add(padId);
                
                // Set initial position to actual current position (no jump)
                try
                {
                    _localPositionField.SetValue(legPad, actualStartPos);
                    _lastSetPosition[padId] = actualStartPos;
                }
                catch { }
            }
        }
        
        /// <summary>
        /// Kill the game's localPositionTween using DOTween's API directly
        /// (Kill is an extension method on TweenExtensions, NOT an instance method - reflection fails)
        /// </summary>
        private static void KillGameTween(PlayerLegPad legPad)
        {
            if (_localPositionTweenField == null) return;
            
            try
            {
                var tween = _localPositionTweenField.GetValue(legPad) as Tween;
                if (tween != null && tween.IsActive())
                {
                    tween.Kill(false);
                }
                _localPositionTweenField.SetValue(legPad, null);
            }
            catch { }
        }
        
        /// <summary>
        /// Try to get the controlled position for a pad (used by Update prefix)
        /// Returns true if this pad is controlled and has a cached position
        /// </summary>
        public static bool TryGetControlledPosition(int padId, out Vector3 position)
        {
            if (_controlledLegs.Contains(padId) && _lastSetPosition.TryGetValue(padId, out position))
                return true;
            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Called by Stances on client when a leg becomes stance-idle.
        /// Clears any velocity-extension transient state for this pad to prevent
        /// one-frame flare from stale drop/extend data.
        /// </summary>
        public static void ClearLegForStance(int padId)
        {
            _controlledLegs.Remove(padId);
            _inDropAnimation.Remove(padId);
            _dropAnimProgress.Remove(padId);
            _dropStartPosition.Remove(padId);
            _pendingTweenKill.Remove(padId);
            _gameNativeExtend.Remove(padId);
            _lastSetPosition.Remove(padId);
            _targetExtension[padId] = 0f;
            _currentExtension[padId] = 0f;
        }
        
        /// <summary>
        /// Client-side update: uses extension values received from the server via CMM.
        /// No independent velocity calculation - server values are authoritative and already smooth.
        /// </summary>
        public static void ClientUpdateLegs(PlayerBodyV2 body)
        {
            if (!_enabled || body == null) return;
            if (body.Player == null) return;
            if (body.Player.Role != PlayerRole.Goalie) return;
            
            // If not sliding, release active control but keep last target/current extension.
            // This preserves momentum entry animation when CMM arrives before IsSliding replicates.
            if (!body.IsSliding.Value)
            {
                var legPadsForRelease = GetLegPads(body);
                foreach (var (pad, isLeft) in legPadsForRelease)
                {
                    if (pad == null) continue;
                    int padId = pad.GetInstanceID();
                    _controlledLegs.Remove(padId);
                    _dropAnimProgress.Remove(padId);
                    _inDropAnimation.Remove(padId);
                    _dropStartPosition.Remove(padId);
                    _gameNativeExtend.Remove(padId);
                    _pendingTweenKill.Remove(padId);
                    _lastSetPosition.Remove(padId);
                }
                return;
            }
            
            if (_localPositionField == null) return;
            
            var legPads = GetLegPads(body);
            float dt = Time.fixedDeltaTime;
            
            foreach (var (pad, isLeft) in legPads)
            {
                if (pad == null) continue;
                int padId = pad.GetInstanceID();
                
                // Skip pads in game-native extend
                if (_gameNativeExtend.Contains(padId))
                    continue;
                
                // Skip pads controlled by Stances (half butterfly idle leg)
                if (Stances.IsControlledByStance(padId))
                {
                    ClearLegForStance(padId);
                    continue;
                }
                
                // Skip if not controlled (CMM handler adds to _controlledLegs)
                if (!_controlledLegs.Contains(padId))
                    continue;
                
                if (!_targetExtension.TryGetValue(padId, out float target))
                    target = 0f;
                if (!_currentExtension.TryGetValue(padId, out float current))
                    current = 0f;
                
                // Fast lerp toward server's smooth value
                float newCurrent = Mathf.Lerp(current, target, dt * CLIENT_LERP_SPEED);
                if (newCurrent < 0.01f) newCurrent = 0f;
                if (newCurrent > 0.99f) newCurrent = 1f;
                _currentExtension[padId] = newCurrent;
                
                CacheLegPositions(pad, padId);
                if (!_legPositions.TryGetValue(padId, out var positions))
                    continue;
                
                // Compute position directly from extension value
                Vector3 pos = Vector3.Lerp(positions.butterfly, positions.extended, newCurrent);
                
                // Kill game tween so it doesn't fight us
                KillGameTween(pad);
                
                try
                {
                    _localPositionField.SetValue(pad, pos);
                    _lastSetPosition[padId] = pos;
                }
                catch { }
            }
        }
        
        public static void ClearAll()
        {
            _currentExtension.Clear();
            _targetExtension.Clear();
            _legPositions.Clear();
            _bodyLegPads.Clear();
            _controlledLegs.Clear();
            _dropAnimProgress.Clear();
            _inDropAnimation.Clear();
            _dropStartPosition.Clear();
            _gameNativeExtend.Clear();
            _pendingTweenKill.Clear();
            _lastSetPosition.Clear();
            _sendState.Clear();
        }

        /// <summary>
        /// Tear down the CMM handler and reset broadcast bookkeeping. Called from
        /// DashFallGameMod.OnDisable so the handler does not linger after a plugin
        /// disable/enable cycle.
        /// </summary>
        public static void Disable()
        {
            if (_cmm != null && _cmmRegistered)
            {
                try { _cmm.UnregisterNamedMessageHandler(CMM_VELOCITY_EXTEND); } catch { }
            }
            _cmm = null;
            _cmmRegistered = false;
            ClearAll();
        }
        
        /// <summary>
        /// Force-clear velocity extension for one body and tell clients to drop it.
        /// Used on role change: a goalie that becomes a skater stops being ticked by
        /// UpdateVelocityExtend (which bails on non-goalies) before it can broadcast
        /// the (0,0) clear, which would otherwise strand a stale extended pose on
        /// remote clients.
        /// </summary>
        public static void ReleaseBody(PlayerBodyV2 body)
        {
            if (body == null) return;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                var netObj = body.GetComponent<NetworkObject>();
                if (netObj != null) NotifyClientsExtension(netObj.NetworkObjectId, 0f, 0f);
            }
            ReleaseAllLegs(body);
        }

        /// <summary>
        /// Drop body- and NetworkObjectId-keyed bookkeeping when a body despawns.
        /// Leg-pad-keyed state is cleared separately by OnLegPadDestroyed.
        /// </summary>
        public static void OnBodyDespawned(PlayerBodyV2 body)
        {
            if (body == null) return;
            _bodyLegPads.Remove(body.GetInstanceID());
            var netObj = body.GetComponent<NetworkObject>();
            if (netObj != null) _sendState.Remove(netObj.NetworkObjectId);
        }

        public static void OnLegPadDestroyed(PlayerLegPad legPad)
        {
            if (legPad != null)
            {
                int padId = legPad.GetInstanceID();
                _currentExtension.Remove(padId);
                _targetExtension.Remove(padId);
                _legPositions.Remove(padId);
                _controlledLegs.Remove(padId);
                _dropAnimProgress.Remove(padId);
                _inDropAnimation.Remove(padId);
                _dropStartPosition.Remove(padId);
                _gameNativeExtend.Remove(padId);
                _pendingTweenKill.Remove(padId);
                _lastSetPosition.Remove(padId);
            }
        }
        
        /// <summary>
        /// Block state changes that would interfere with our velocity control
        /// </summary>
        public static bool ShouldBlockStateChange(PlayerLegPad legPad, PlayerLegPadState currentState, PlayerLegPadState newState)
        {
            if (!_enabled || legPad == null) return false;
            
            int padId = legPad.GetInstanceID();

            // Always suppress dash-triggered butterfly-extended visual, even at low velocity.
            if (IsDashExtending &&
                currentState == PlayerLegPadState.Butterfly &&
                newState == PlayerLegPadState.ButterflyExtended)
                return true;
            
            // Never block if this leg is in a game-native extend
            if (_gameNativeExtend.Contains(padId))
                return false;
            
            if (!_controlledLegs.Contains(padId))
                return false;
            
            // Block Extended -> Butterfly if we have velocity extension
            if (currentState == PlayerLegPadState.ButterflyExtended && newState == PlayerLegPadState.Butterfly)
            {
                if (_currentExtension.TryGetValue(padId, out float ext) && ext > 0.01f)
                    return true;
            }
            
            return false;
        }
    }
    
    /// <summary>
    /// Harmony patches for velocity-based leg extension
    /// </summary>
    [HarmonyPatch]
    public static class GoalieDashExtendPatches
    {
        /// <summary>
        /// Prefix - block unwanted state changes and capture old state
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), nameof(PlayerLegPad.State), MethodType.Setter)]
        [HarmonyPrefix]
        public static bool State_Prefix(PlayerLegPad __instance, ref PlayerLegPadState value, out PlayerLegPadState __state)
        {
            __state = __instance.State; // Capture old state for postfix
            
            if (!GoalieDashExtend.Enabled) return true;
            
            if (GoalieDashExtend.ShouldBlockStateChange(__instance, __state, value))
                return false;
            
            return true;
        }
        
        /// <summary>
        /// Postfix - intercept state changes to create our own tween for drop animation.
        /// Only fires when the state actually changed (prefix didn't block it).
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), nameof(PlayerLegPad.State), MethodType.Setter)]
        [HarmonyPostfix]
        public static void State_Postfix(PlayerLegPad __instance, PlayerLegPadState value, PlayerLegPadState __state)
        {
            if (!GoalieDashExtend.Enabled) return;
            
            // If the state didn't actually change, our prefix blocked it - skip
            if (__instance.State == __state) return;
            
            // Call OnStateChange with old and new state
            GoalieDashExtend.OnStateChange(__instance, __state, value);
        }
        
        /// <summary>
        /// Clean up when leg pad destroyed
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), "OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix(PlayerLegPad __instance)
        {
            GoalieDashExtend.OnLegPadDestroyed(__instance);
        }
        
        // Cache the reflection field for the Update prefix
        private static FieldInfo _updateLocalPositionField = typeof(PlayerLegPad).GetField("localPosition", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        /// <summary>
        /// Prefix on PlayerLegPad.Update - re-apply our position AFTER DOTween has run.
        /// 
        /// Frame order:
        /// 1. FixedUpdate: Our code calculates and sets localPosition
        /// 2. DOTween Update: Overwrites localPosition with its tween value 
        /// 3. PlayerLegPad.Update (THIS PREFIX): We overwrite localPosition AGAIN with our cached value
        /// 4. PlayerLegPad.Update body: Reads localPosition and writes to transform.localPosition
        /// 
        /// This ensures our position wins even if the tween kill fails.
        /// </summary>
        [HarmonyPatch(typeof(PlayerLegPad), "Update")]
        [HarmonyPrefix]
        public static void Update_Prefix(PlayerLegPad __instance)
        {
            if (!GoalieDashExtend.Enabled) return;
            if (_updateLocalPositionField == null) return;
            
            int padId = __instance.GetInstanceID();
            
            // Yield to Stances if it controls this leg
            if (Stances.IsControlledByStance(padId)) return;
            
            if (GoalieDashExtend.TryGetControlledPosition(padId, out Vector3 pos))
            {
                try { _updateLocalPositionField.SetValue(__instance, pos); } catch { }
            }
        }
    }
    
    /// <summary>
    /// Suppress dash extend visuals: prevent DashLeft/DashRight from extending leg pads
    /// via IsExtendedLeft/Right NetworkVariable when velocity system controls them.
    /// Server-side: blocks the state change via IsDashExtending flag.
    /// Client-side: reverts the NetworkVariable so the change never replicates.
    /// </summary>
    [HarmonyPatch]
    public static class DashExtendSuppressPatches
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerBodyV2), nameof(PlayerBodyV2.DashLeft));
            yield return AccessTools.Method(typeof(PlayerBodyV2), nameof(PlayerBodyV2.DashRight));
        }
        
        [HarmonyPrefix]
        public static void Prefix()
        {
            GoalieDashExtend.IsDashExtending = true;
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance, MethodBase __originalMethod)
        {
            if (!GoalieDashExtend.Enabled) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (__instance.Player == null || __instance.Player.Role != PlayerRole.Goalie) return;
            if (!__instance.IsSliding.Value) return;

            // Always revert dash-extend network var while sliding goalies to avoid
            // default dash extend visual sneaking through and causing jitter.
            bool isDashLeft = __originalMethod.Name.Contains("Left");
            if (isDashLeft)
                __instance.IsExtendedRight.Value = false;
            else
                __instance.IsExtendedLeft.Value = false;
        }

        // Runs after Postfix AND after the original throws -- the Postfix's
        // try/finally cannot clear IsDashExtending if vanilla DashLeft/Right
        // throws, because Postfixes are skipped on original-throw.
        [HarmonyFinalizer]
        public static void Finalizer()
        {
            GoalieDashExtend.IsDashExtending = false;
        }
    }
}
