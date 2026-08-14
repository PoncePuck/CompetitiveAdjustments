// DashFall.Input.cs - Client-side input handling for DashFall actions

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner : MonoBehaviour
    {
        // Action name constants for DashFall
        private static readonly string[] AllDashFallActions = new[]
        {
            "dive", "dashleft", "dashright", "twistleft", "twistright",
            "slideinfluenceleft", "slideinfluenceright", "slideinfluenceforward", "slideinfluencebackward"
        };

        // Double-tap actions
        private static readonly HashSet<string> DoubleTapActions = new HashSet<string>
        {
            "twistleft", "twistright"
        };
        private const float DoubleTapWindow = 0.3f;
        private readonly Dictionary<string, float> _lastTapTime = new Dictionary<string, float>();
        
        // Toggle state tracking (for TOGGLE mode)
        private readonly Dictionary<string, bool> _toggleStates = new Dictionary<string, bool>();
        
        // Hold state tracking (for HOLD mode - action fires repeatedly while held)
        private readonly HashSet<string> _holdActiveActions = new HashSet<string>();
        private readonly Dictionary<string, float> _holdLastFireTime = new Dictionary<string, float>();
        
        // Client-side cooldown tracking to respect server cooldowns
        private readonly Dictionary<string, float> _actionCooldowns = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _lastActionTime = new Dictionary<string, float>();
        
        // Cooldown values (matching server-side ModConfig defaults)
        private const float DashCooldown = 0.4f;      // SkaterBaseStandingDashCooldown
        private const float DiveCooldown = 0.5f;      // Reasonable dive cooldown
        private const float TwistCooldown = 0.15f;    // Quick twist cooldown
        private const float SlideInfluenceCooldown = 0.0f; // No cooldown for continuous DI

        // Chord maps
        private readonly Dictionary<KeyChord, List<string>> _skaterChordToActions = new Dictionary<KeyChord, List<string>>();
        private readonly Dictionary<KeyChord, List<string>> _goalieChordToActions = new Dictionary<KeyChord, List<string>>();

        // Input listeners
        private readonly List<InputAction> _allIA = new List<InputAction>();
        private readonly Dictionary<InputAction, List<KeyChord>> _iaToChords = new Dictionary<InputAction, List<KeyChord>>();
        private readonly Dictionary<KeyChord, double> _lastFireAt = new Dictionary<KeyChord, double>();
        private readonly HashSet<KeyChord> _downChords = new HashSet<KeyChord>();

        // Configs
        private SkaterKeybindConfig _skater;
        private GoalieKeybindConfig _goalie;

        private void RebuildLookups()
        {
            _skaterChordToActions.Clear();
            _goalieChordToActions.Clear();

            BindActionList(_skater.divekey, "dive", _skaterChordToActions);
            BindActionList(_skater.dashleftkey, "dashleft", _skaterChordToActions);
            BindActionList(_skater.dashrightkey, "dashright", _skaterChordToActions);
            BindActionList(_skater.twistleftkey, "twistleft", _skaterChordToActions);
            BindActionList(_skater.twistrightkey, "twistright", _skaterChordToActions);
            BindActionList(_skater.slideinfluenceleftkey, "slideinfluenceleft", _skaterChordToActions);
            BindActionList(_skater.slideinfluencerightkey, "slideinfluenceright", _skaterChordToActions);
            BindActionList(_skater.slideinfluenceforwardkey, "slideinfluenceforward", _skaterChordToActions);
            BindActionList(_skater.slideinfluencebackwardkey, "slideinfluencebackward", _skaterChordToActions);

            // Goalie bindings
            BindActionList(_goalie.divekey, "dive", _goalieChordToActions);
            BindActionList(_goalie.standingdashleftkey, "goaliestandingdashleft", _goalieChordToActions);
            BindActionList(_goalie.standingdashrightkey, "goaliestandingdashright", _goalieChordToActions);
            BindActionList(_goalie.twistleftkey, "twistleft", _goalieChordToActions);
            BindActionList(_goalie.twistrightkey, "twistright", _goalieChordToActions);
            BindActionList(_goalie.slideinfluenceleftkey, "slideinfluenceleft", _goalieChordToActions);
            BindActionList(_goalie.slideinfluencerightkey, "slideinfluenceright", _goalieChordToActions);
            BindActionList(_goalie.slideinfluenceforwardkey, "slideinfluenceforward", _goalieChordToActions);
            BindActionList(_goalie.slideinfluencebackwardkey, "slideinfluencebackward", _goalieChordToActions);
        }

        // Get the action type for a given action name based on current role
        private string GetActionType(string action, bool isGoalie)
        {
            if (isGoalie)
            {
                switch (action)
                {
                    case "dive": return _goalie.divekeytype ?? "PRESS";
                    case "goaliestandingdashleft": return _goalie.standingdashleftkeytype ?? "PRESS";
                    case "goaliestandingdashright": return _goalie.standingdashrightkeytype ?? "PRESS";
                    case "twistleft": return _goalie.twistleftkeytype ?? "PRESS";
                    case "twistright": return _goalie.twistrightkeytype ?? "PRESS";
                    case "slideinfluenceleft": return _goalie.slideinfluenceleftkeytype ?? "CONTINUOUS";
                    case "slideinfluenceright": return _goalie.slideinfluencerightkeytype ?? "CONTINUOUS";
                    case "slideinfluenceforward": return _goalie.slideinfluenceforwardkeytype ?? "CONTINUOUS";
                    case "slideinfluencebackward": return _goalie.slideinfluencebackwardkeytype ?? "CONTINUOUS";
                }
            }
            else
            {
                switch (action)
                {
                    case "dive": return _skater.divekeytype ?? "PRESS";
                    case "dashleft": return _skater.dashleftkeytype ?? "PRESS";
                    case "dashright": return _skater.dashrightkeytype ?? "PRESS";
                    case "twistleft": return _skater.twistleftkeytype ?? "PRESS";
                    case "twistright": return _skater.twistrightkeytype ?? "PRESS";
                    case "slideinfluenceleft": return _skater.slideinfluenceleftkeytype ?? "CONTINUOUS";
                    case "slideinfluenceright": return _skater.slideinfluencerightkeytype ?? "CONTINUOUS";
                    case "slideinfluenceforward": return _skater.slideinfluenceforwardkeytype ?? "CONTINUOUS";
                    case "slideinfluencebackward": return _skater.slideinfluencebackwardkeytype ?? "CONTINUOUS";
                }
            }
            return "PRESS";
        }

        // Check if action type is a "holdable" type (CONTINUOUS/TOGGLE vs PRESS/RELEASE/etc)
        private static bool IsHoldableActionType(string actionType)
        {
            return actionType == "CONTINUOUS" || actionType == "TOGGLE";
        }

        // Get the cooldown for a given action
        private static float GetActionCooldown(string action)
        {
            switch (action)
            {
                case "dashleft":
                case "dashright":
                case "goaliestandingdashleft":
                case "goaliestandingdashright":
                    return DashCooldown;
                case "dive":
                    return DiveCooldown;
                case "twistleft":
                case "twistright":
                    return TwistCooldown;
                case "slideinfluenceleft":
                case "slideinfluenceright":
                case "slideinfluenceforward":
                case "slideinfluencebackward":
                    return SlideInfluenceCooldown;
                default:
                    return 0.1f; // Default small cooldown
            }
        }

        // Check if action is on cooldown
        private bool IsActionOnCooldown(string action)
        {
            float cooldown = GetActionCooldown(action);
            if (cooldown <= 0f) return false;
            
            if (_lastActionTime.TryGetValue(action, out float lastTime))
            {
                return (Time.unscaledTime - lastTime) < cooldown;
            }
            return false;
        }

        // Mark action as used (start cooldown)
        private void MarkActionUsed(string action)
        {
            _lastActionTime[action] = Time.unscaledTime;
        }

        private void BindActionList(List<string> list, string action, Dictionary<KeyChord, List<string>> target)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var spec = list[i];
                if (!DashFallParsing.TryParseChord(spec, out KeyChord kc)) continue;

                try
                {
                    if (!target.TryGetValue(kc, out List<string> a))
                    {
                        a = new List<string>();
                        target[kc] = a;
                    }
                    if (!a.Contains(action)) a.Add(action);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[COMPADJUST] Failed to bind {spec} to {action}: {ex.Message}");
                }
            }
        }

        // Focus/pausing checks
        private static bool AnyTextInputFocused()
        {
            try
            {
                // Check game's UIChat focus (UI Toolkit TextField, not detected by EventSystem)
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                if (chat != null && chat.IsFocused) return true;

                var es = EventSystem.current;
                if (es == null) return false;
                var go = es.currentSelectedGameObject;
                if (go == null) return false;

                // Check for TMPro InputField (used by game chat)
                var tmp = go.GetComponent<TMPro.TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                
                // Check for standard UI InputField
                var old = go.GetComponent<UnityEngine.UI.InputField>();
                if (old != null && old.isFocused) return true;
            }
            catch { }
            return false;
        }

        private static bool IsLikelyPaused()
        {
            // Block if time is paused
            if (Time.timeScale <= 0f) return true;
            // Block if cursor is visible and unlocked (indicates menu/pause state)
            if (UnityEngine.Cursor.visible && UnityEngine.Cursor.lockState != CursorLockMode.Locked) return true;
            return false;
        }

        private static bool ShouldBlockBinds()
        {
            if (AnyTextInputFocused()) return true;

            try
            {
                // Use cached instance instead of expensive FindFirstObjectByType
                var inst = _instance;
                if (inst != null)
                {
                    if (inst._dfPanel != null && inst._dfPanel.style.display == UnityEngine.UIElements.DisplayStyle.Flex)
                        return true;
                    if (inst._isCapturing)
                        return true;
                }
            }
            catch { }

            return IsLikelyPaused();
        }

        // Modifier state helpers
        private static bool ModsSatisfied(KeyChord kc)
        {
            var kb = Keyboard.current;
            if (kb == null) return !kc.Ctrl && !kc.Shift && !kc.Alt;

            bool ctrl = (kb.leftCtrlKey != null && kb.leftCtrlKey.isPressed) || (kb.rightCtrlKey != null && kb.rightCtrlKey.isPressed);
            bool shift = (kb.leftShiftKey != null && kb.leftShiftKey.isPressed) || (kb.rightShiftKey != null && kb.rightShiftKey.isPressed);
            bool alt = (kb.leftAltKey != null && kb.leftAltKey.isPressed) || (kb.rightAltKey != null && kb.rightAltKey.isPressed);

            return (!kc.Ctrl || ctrl) && (!kc.Shift || shift) && (!kc.Alt || alt);
        }

        private static bool IsKeyDown(KeyCode k)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            // Letters
            if (k >= KeyCode.A && k <= KeyCode.Z)
            {
                int idx = (int)k - (int)KeyCode.A;
                var key = kb[(Key)((int)Key.A + idx)];
                return key != null && key.isPressed;
            }

            // Function keys
            if (k >= KeyCode.F1 && k <= KeyCode.F12)
            {
                int n = (int)k - (int)KeyCode.F1 + 1;
                var key = kb[Key.F1 + (n - 1)];
                return key != null && key.isPressed;
            }

            switch (k)
            {
                case KeyCode.Space: return kb.spaceKey?.isPressed ?? false;
                case KeyCode.Tab: return kb.tabKey?.isPressed ?? false;
                case KeyCode.Escape: return kb.escapeKey?.isPressed ?? false;
                case KeyCode.LeftShift: return kb.leftShiftKey?.isPressed ?? false;
                case KeyCode.RightShift: return kb.rightShiftKey?.isPressed ?? false;
                case KeyCode.LeftControl: return kb.leftCtrlKey?.isPressed ?? false;
                case KeyCode.RightControl: return kb.rightCtrlKey?.isPressed ?? false;
                case KeyCode.LeftAlt: return kb.leftAltKey?.isPressed ?? false;
                case KeyCode.RightAlt: return kb.rightAltKey?.isPressed ?? false;
                case KeyCode.UpArrow: return kb.upArrowKey?.isPressed ?? false;
                case KeyCode.DownArrow: return kb.downArrowKey?.isPressed ?? false;
                case KeyCode.LeftArrow: return kb.leftArrowKey?.isPressed ?? false;
                case KeyCode.RightArrow: return kb.rightArrowKey?.isPressed ?? false;
                case KeyCode.BackQuote: return kb.backquoteKey?.isPressed ?? false;
                case KeyCode.Minus: return kb.minusKey?.isPressed ?? false;
                case KeyCode.Equals: return kb.equalsKey?.isPressed ?? false;
                case KeyCode.LeftBracket: return kb.leftBracketKey?.isPressed ?? false;
                case KeyCode.RightBracket: return kb.rightBracketKey?.isPressed ?? false;
                case KeyCode.Semicolon: return kb.semicolonKey?.isPressed ?? false;
                case KeyCode.Quote: return kb.quoteKey?.isPressed ?? false;
                case KeyCode.Comma: return kb.commaKey?.isPressed ?? false;
                case KeyCode.Period: return kb.periodKey?.isPressed ?? false;
                case KeyCode.Slash: return kb.slashKey?.isPressed ?? false;
                case KeyCode.Backslash: return kb.backslashKey?.isPressed ?? false;
                case KeyCode.Alpha0: return kb.digit0Key?.isPressed ?? false;
                case KeyCode.Alpha1: return kb.digit1Key?.isPressed ?? false;
                case KeyCode.Alpha2: return kb.digit2Key?.isPressed ?? false;
                case KeyCode.Alpha3: return kb.digit3Key?.isPressed ?? false;
                case KeyCode.Alpha4: return kb.digit4Key?.isPressed ?? false;
                case KeyCode.Alpha5: return kb.digit5Key?.isPressed ?? false;
                case KeyCode.Alpha6: return kb.digit6Key?.isPressed ?? false;
                case KeyCode.Alpha7: return kb.digit7Key?.isPressed ?? false;
                case KeyCode.Alpha8: return kb.digit8Key?.isPressed ?? false;
                case KeyCode.Alpha9: return kb.digit9Key?.isPressed ?? false;
                case KeyCode.Keypad0: return kb.numpad0Key?.isPressed ?? false;
                case KeyCode.Keypad1: return kb.numpad1Key?.isPressed ?? false;
                case KeyCode.Keypad2: return kb.numpad2Key?.isPressed ?? false;
                case KeyCode.Keypad3: return kb.numpad3Key?.isPressed ?? false;
                case KeyCode.Keypad4: return kb.numpad4Key?.isPressed ?? false;
                case KeyCode.Keypad5: return kb.numpad5Key?.isPressed ?? false;
                case KeyCode.Keypad6: return kb.numpad6Key?.isPressed ?? false;
                case KeyCode.Keypad7: return kb.numpad7Key?.isPressed ?? false;
                case KeyCode.Keypad8: return kb.numpad8Key?.isPressed ?? false;
                case KeyCode.Keypad9: return kb.numpad9Key?.isPressed ?? false;
            }

            return Mouse.current != null && (
                   (k == KeyCode.Mouse0 && Mouse.current.leftButton.isPressed) ||
                   (k == KeyCode.Mouse1 && Mouse.current.rightButton.isPressed) ||
                   (k == KeyCode.Mouse2 && Mouse.current.middleButton.isPressed) ||
                   (k == KeyCode.Mouse3 && (Mouse.current.forwardButton?.isPressed ?? false)) ||
                   (k == KeyCode.Mouse4 && (Mouse.current.backButton?.isPressed ?? false)));
        }

        private static bool ChordStillHeld(KeyChord kc)
        {
            if (!ModsSatisfied(kc)) return false;
            for (int i = 0; i < kc.Keys.Length; i++)
                if (!IsKeyDown(kc.Keys[i])) return false;
            return true;
        }

        private void ResetInputActions()
        {
            ClearInputActions();

            var all = new HashSet<KeyChord>();
            foreach (var k in _skaterChordToActions.Keys) all.Add(k);
            foreach (var k in _goalieChordToActions.Keys) all.Add(k);

            foreach (var kc in all)
            {
                // Modifier-only chords
                if (kc.Keys.Length == 0 && (kc.Ctrl || kc.Shift || kc.Alt))
                {
                    var modPaths = new List<string>();
                    if (kc.Ctrl) { modPaths.Add("<Keyboard>/leftCtrl"); modPaths.Add("<Keyboard>/rightCtrl"); }
                    if (kc.Shift) { modPaths.Add("<Keyboard>/leftShift"); modPaths.Add("<Keyboard>/rightShift"); }
                    if (kc.Alt) { modPaths.Add("<Keyboard>/leftAlt"); modPaths.Add("<Keyboard>/rightAlt"); }

                    foreach (var path in modPaths.Distinct())
                    {
                        var ia = new InputAction(type: InputActionType.Button, binding: path);

                        ia.performed += _ =>
                        {
                            if (ShouldBlockBinds()) return;
                            if (!ChordStillHeld(kc)) return;

                            // Use skater binds by default if role not yet detected
                            bool goalie = _hasRoleCached ? IsGoalieNow() : false;
                            var map = goalie ? _goalieChordToActions : _skaterChordToActions;
                            if (_downChords.Contains(kc)) return;
                            _downChords.Add(kc);
                            if (map.TryGetValue(kc, out var acts) && acts.Count > 0)
                                SendActionsToServer(acts, true);
                        };

                        ia.canceled += _ =>
                        {
                            if (!_downChords.Contains(kc)) return;
                            if (ChordStillHeld(kc)) return;

                            _downChords.Remove(kc);

                            // Use skater binds by default if role not yet detected
                            bool goalie = _hasRoleCached ? IsGoalieNow() : false;
                            var map = goalie ? _goalieChordToActions : _skaterChordToActions;
                            if (map.TryGetValue(kc, out var acts) && acts.Count > 0)
                                if (!ShouldBlockBinds()) SendActionsToServer(acts, false);
                        };

                        ia.Enable();
                        _allIA.Add(ia);
                        _iaToChords[ia] = new List<KeyChord> { kc };
                    }
                    continue;
                }

                // Chords with non-modifier keys
                for (int keyIdx = 0; keyIdx < kc.Keys.Length; keyIdx++)
                {
                    if (!DashFallParsing.TryGetInputPath(kc.Keys[keyIdx], out var path)) continue;

                    var ia = new InputAction(type: InputActionType.Button, binding: path);

                    ia.performed += _ =>
                    {
                        // Use skater binds by default if role not yet detected
                        bool goalie = _hasRoleCached ? IsGoalieNow() : false;
                        if (ShouldBlockBinds()) return;
                        if (!ChordStillHeld(kc)) return;

                        var map = goalie ? _goalieChordToActions : _skaterChordToActions;
                        if (_downChords.Contains(kc)) return;
                        _downChords.Add(kc);
                        if (map.TryGetValue(kc, out var acts) && acts.Count > 0)
                        {
                            SendActionsToServer(acts, true);
                        }
                    };

                    ia.canceled += _ =>
                    {
                        if (!_downChords.Contains(kc)) return;
                        if (ChordStillHeld(kc)) return;

                        _downChords.Remove(kc);

                        // Use skater binds by default if role not yet detected
                        bool goalie = _hasRoleCached ? IsGoalieNow() : false;
                        var map = goalie ? _goalieChordToActions : _skaterChordToActions;
                        if (map.TryGetValue(kc, out var acts) && acts.Count > 0)
                            if (!ShouldBlockBinds()) SendActionsToServer(acts, false);
                    };

                    ia.Enable();
                    _allIA.Add(ia);
                    if (!_iaToChords.TryGetValue(ia, out var list))
                    {
                        list = new List<KeyChord>();
                        _iaToChords[ia] = list;
                    }
                    list.Add(kc);
                }
            }
        }

        private void ClearInputActions()
        {
            for (int i = 0; i < _allIA.Count; i++)
            {
                var ia = _allIA[i];
                try { ia.Disable(); } catch { }
                try { ia.Dispose(); } catch { }
            }
            _allIA.Clear();
            _iaToChords.Clear();
            _lastFireAt.Clear();
            _downChords.Clear();
            _lastTapTime.Clear();
            _sentDownActions.Clear();
            _toggleStates.Clear();
            _holdActiveActions.Clear();
            _holdLastFireTime.Clear();
        }

        // Track which actions we've actually sent isDown=true for
        // This prevents sending isDown=false for actions that were never sent down (e.g. first tap of double-tap)
        private readonly HashSet<string> _sentDownActions = new HashSet<string>();

        // Send actions to server via ServerBridge
        private void SendActionsToServer(List<string> actions, bool isDown)
        {
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm == null || !nm.IsConnectedClient) return;
                var cmm = nm.CustomMessagingManager;
                if (cmm == null) return;

                bool goalie = _hasRoleCached ? IsGoalieNow() : false;

                foreach (var action in actions)
                {
                    string actionType = GetActionType(action, goalie);
                    
                    // Handle different action types
                    switch (actionType)
                    {
                        case "PRESS":
                            // Fire on key down only, respects cooldown
                            if (!isDown) continue;
                            if (IsActionOnCooldown(action)) continue;
                            MarkActionUsed(action);
                            break;
                            
                        case "RELEASE":
                            // Fire on key up only, respects cooldown
                            if (isDown) continue;
                            if (IsActionOnCooldown(action))
                            {
                                if (ConfigManager.Config.EnableDebugLogs)
                                    ConfigManager.Dbg($"RELEASE blocked by cooldown: {action}");
                                continue;
                            }
                            if (ConfigManager.Config.EnableDebugLogs)
                                ConfigManager.Dbg($"RELEASE firing: {action}");
                            MarkActionUsed(action);
                            // Send as "down" to trigger the action on release
                            SendSingleAction(cmm, action, true);
                            continue;
                            
                        case "DOUBLE PRESS":
                            // Only fire on double-tap, respects cooldown
                            if (isDown)
                            {
                                float now = Time.unscaledTime;
                                if (_lastTapTime.TryGetValue(action, out var lastTap) && (now - lastTap) <= DoubleTapWindow)
                                {
                                    // Double-tap detected! Check cooldown before sending
                                    _lastTapTime[action] = 0f;
                                    if (IsActionOnCooldown(action)) continue;
                                    MarkActionUsed(action);
                                    // Fall through to send
                                }
                                else
                                {
                                    // First tap - record it but don't send yet
                                    _lastTapTime[action] = now;
                                    continue;
                                }
                            }
                            else
                            {
                                // Don't send on release for double press
                                continue;
                            }
                            break;
                            
                        case "HOLD":
                            // HOLD mode - UpdateHoldActions will fire repeatedly while held
                            // Don't send immediately, let UpdateHoldActions handle it with cooldown
                            if (isDown)
                            {
                                _holdActiveActions.Add(action);
                                _holdLastFireTime[action] = 0f; // Reset so first fire happens immediately
                            }
                            else
                            {
                                _holdActiveActions.Remove(action);
                                _holdLastFireTime.Remove(action);
                            }
                            continue; // Don't send here, UpdateHoldActions will handle it
                            
                        case "TOGGLE":
                            // Toggle on each press, respects cooldown
                            if (isDown)
                            {
                                if (IsActionOnCooldown(action)) continue;
                                MarkActionUsed(action);
                                bool currentState = _toggleStates.TryGetValue(action, out var state) && state;
                                bool newState = !currentState;
                                _toggleStates[action] = newState;
                                SendSingleAction(cmm, action, newState);
                                if (newState)
                                    _sentDownActions.Add(action);
                                else
                                    _sentDownActions.Remove(action);
                            }
                            continue; // We've already sent, skip normal sending
                            
                        case "CONTINUOUS":
                        default:
                            // Default behavior: send down/up based on key state (no cooldown for continuous)
                            break;
                    }
                    
                    // For release (isDown=false), only send if we actually sent down
                    if (!isDown && !_sentDownActions.Contains(action))
                    {
                        continue;
                    }
                    
                    // Track sent state
                    if (isDown)
                        _sentDownActions.Add(action);
                    else
                        _sentDownActions.Remove(action);
                    
                    SendSingleAction(cmm, action, isDown);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to send actions: {e.Message}");
            }
        }
        
        // Reliable, not Unreliable. These are EDGES, not samples: the server keeps the held
        // set in ServerBridge._held and nothing ever re-states it, so one lost packet is not
        // one lost frame of input, it is the whole press. A dropped "down" means a CONTINUOUS
        // bind such as slide influence does nothing for as long as the key is held, and a
        // dropped "up" leaves the action held on the server after the player let go.
        //
        // This could only ever go wrong on a dedicated server. On a listen host the send is
        // loopback and cannot be dropped whatever the delivery mode says, which is why the
        // same binds behave on a host and misbehave across a real connection.
        //
        // The volume is a handful of messages per key press, so reliability costs nothing
        // worth measuring here.
        private void SendSingleAction(Unity.Netcode.CustomMessagingManager cmm, string action, bool isDown)
        {
            // Create a fresh writer for each message
            using (var writer = new Unity.Netcode.FastBufferWriter(64, Unity.Collections.Allocator.Temp))
            {
                writer.WriteValueSafe(action);
                writer.WriteValueSafe((byte)(isDown ? 0 : 1));
                cmm.SendNamedMessage("PPKB/Action", Unity.Netcode.NetworkManager.ServerClientId, writer, Unity.Netcode.NetworkDelivery.Reliable);
            }
        }

        // Send hello message to server declaring our bound actions
        private void SendHelloToServer()
        {
            try
            {
                var cmm = Unity.Netcode.NetworkManager.Singleton?.CustomMessagingManager;
                if (cmm == null) return;

                var allActions = new HashSet<string>();
                foreach (var list in _skaterChordToActions.Values)
                    foreach (var a in list) allActions.Add(a);
                foreach (var list in _goalieChordToActions.Values)
                    foreach (var a in list) allActions.Add(a);

                // Calculate required buffer size (each string needs ~2 bytes overhead + length)
                int estimatedSize = 4; // For the count
                foreach (var a in allActions)
                {
                    estimatedSize += 4 + (a.Length * 2); // String overhead + UTF-16 bytes
                }
                // Add safety margin and clamp to reasonable limits
                int bufferSize = Math.Max(256, Math.Min(estimatedSize + 128, 4096));

                var writer = new Unity.Netcode.FastBufferWriter(bufferSize, Unity.Collections.Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(allActions.Count);
                    foreach (var action in allActions)
                        writer.WriteValueSafe(action);

                    cmm.SendNamedMessage("PPKB/Hello", Unity.Netcode.NetworkManager.ServerClientId, writer);
                }
                finally
                {
                    writer.Dispose();
                }
            }
            catch (System.OverflowException) { }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to send hello: {e.Message}");
            }
        }

        // Called from Update() to fire HOLD actions repeatedly while held
        private void UpdateHoldActions()
        {
            if (_holdActiveActions.Count == 0) return;
            if (ShouldBlockBinds()) return;
            
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm == null || !nm.IsConnectedClient) return;
                var cmm = nm.CustomMessagingManager;
                if (cmm == null) return;

                float now = Time.unscaledTime;
                
                // Make a copy to iterate safely
                var actionsToFire = new List<string>(_holdActiveActions);
                
                foreach (var action in actionsToFire)
                {
                    // Get the cooldown for this specific action
                    float cooldown = GetActionCooldown(action);
                    
                    // Check if enough time has passed since last fire
                    if (_holdLastFireTime.TryGetValue(action, out float lastFire))
                    {
                        if (now - lastFire < cooldown) continue;
                    }
                    
                    // Fire the action (always as "down" to trigger it)
                    _holdLastFireTime[action] = now;
                    SendSingleAction(cmm, action, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to update hold actions: {e.Message}");
            }
        }
    }
}
