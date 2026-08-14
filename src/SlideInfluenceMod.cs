// File: SlideInfluenceMod.cs
// Allows players to influence their movement while sliding:
// - Dash keys (Q/E or bound) for lateral (left/right) influence
// - Movement keys (W/S) for forward/backward influence

using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using DashfallCfg = CompetitiveAdjustments.DashfallConfig;
using UnityEngine;

namespace DashFallMod
{
    /// <summary>
    /// Patch FixedUpdate to apply continuous slide influence
    /// </summary>
    [HarmonyPatch(typeof(PlayerBodyV2), "FixedUpdate")]
    public static class SlideInfluence_FixedUpdate_Patch
    {
        private static DashfallCfg Config => ConfigManager.Config;
        
        // Track if we've already ticked GoalieDashExtend this frame
        private static int _lastTickFrame = -1;
        
        // Track if we've ensured server config is loaded (do once, not per frame)
        private static bool _serverConfigEnsured = false;
        
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance)
        {
            bool isServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            
            // On dedicated servers, ensure config is loaded ONCE at startup before running server-side systems
            if (isServer && !_serverConfigEnsured)
            {
                _serverConfigEnsured = true;
                try { DashFallMod.ConfigManager.ReloadConfig(); } catch { }
            }
            
            // Client-side: update leg visuals from received data.
            // Stances runs FIRST so it can claim and clear the half-butterfly idle leg
            // (via ClearLegForStance) before GoalieDashExtend looks at it. This mirrors
            // the server, where stance control is established in the HandleInputs prefix
            // before UpdateVelocityExtend runs. With the old order GoalieDashExtend could
            // briefly apply a stale extension to the idle leg before stances claimed it.
            if (!isServer)
            {
                Stances.ClientUpdateLegs(__instance);
                GoalieDashExtend.ClientUpdateLegs(__instance);
                return;
            }
            
            // Ensure CMM handlers are registered once per frame (not once per
            // player body). On dedicated servers DashFallClientRunner doesn't
            // exist, so without this poll _cmm stays null and NotifyClients
            // silently drops all messages to clients.
            int currentFrame = Time.frameCount;
            if (_lastTickFrame != currentFrame)
            {
                _lastTickFrame = currentFrame;
                GoalieDashExtend.EnsureCMMRegistered();
                Stances.EnsureCMMRegistered();
            }
            
            var player = __instance.Player;
            if (player == null) return;

            // Replay bodies are RigidbodyConstraints.FreezeAll and are moved by
            // transform.DOMove, so UpdateVelocityExtend can only ever read zero velocity,
            // and vanilla skips HandleInputs for them so Stances never seeds _stanceState.
            // Left to run, the server would call NotifyClientsExtension(netId, 0, 0) at
            // 50 Hz on top of the 15 Hz pose ReplayLegPads replays, and because
            // `unchanged` is false right after a non-zero send that clear is NOT
            // suppressed, so the pads would flicker instead of holding the recorded pose.
            // The recorded pose reaches the same client dictionaries through the CMM
            // channels instead.
            if (player.IsReplay.Value)
            {
                // A host renders its own replay; a dedicated server has nothing to draw.
                if (NetworkManager.Singleton.IsClient)
                {
                    Stances.ClientUpdateLegs(__instance);
                    GoalieDashExtend.ClientUpdateLegs(__instance);
                }
                return;
            }

            var id = player.NetworkObjectId;
            bool isSliding = __instance.IsSliding.Value;
            
            // Track slide end for dash cooldown
            bool wasSliding = DashMod.WasSlidingLastFrame.TryGetValue(id, out var ws) && ws;
            DashMod.WasSlidingLastFrame[id] = isSliding;
            
            if (wasSliding && !isSliding)
            {
                // Player just stopped sliding - record the time
                DashMod.LastSlideEndAt[id] = Time.time;
            }
            
            // === VELOCITY-BASED GOALIE LEG EXTEND ===
            // Extends/retracts legs based on lateral velocity - instant response
            GoalieDashExtend.UpdateVelocityExtend(__instance);
            
            // === GOALIE STANCES (Half Butterfly) ===
            Stances.UpdateStances(__instance);

            // On a listen server the host is both server and client on the same machine.
            // CustomMessagingManager.SendNamedMessageToAll does NOT self-deliver to the host
            // client, so ClientUpdateLegs never receives the CMM stance state.
            // Mirror the server-updated state into the client-side dictionaries manually.
            bool isListenServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
            if (isListenServer)
            {
                Stances.ClientUpdateLegs(__instance);
            }
            
            // If not sliding, nothing more to do
            if (!isSliding)
            {
                return;
            }
            
            // === SLIDE INFLUENCE ===
            {
                bool isGoalie = player.Role == PlayerRole.Goalie;
                bool allowed = isGoalie ? Config.GoalieSlideInfluenceEnabled : Config.EnableSlideInfluence;
                
                if (allowed)
                {
                    ApplySlideInfluence(__instance, player);
                }
                else
                {
                    // Says WHY, once, instead of leaving "DI just doesn't work" to inference.
                    // EnableDashfall is the likeliest answer and the least visible one: Config
                    // here is DashfallEffective, so a server with the section master off hands
                    // back the all-features-disabled sentinel and every flag below reads false
                    // no matter what the operator wrote next to it in the file.
                    DiagOnce("gate-off",
                        $"Slide influence is OFF for a {(isGoalie ? "goalie" : "skater")}: "
                        + $"EnableDashfall={CompetitiveAdjustments.ConfigManager.Config?.EnableDashfall} "
                        + $"EnableSlideInfluence={Config.EnableSlideInfluence} "
                        + $"GoalieSlideInfluenceEnabled={Config.GoalieSlideInfluenceEnabled}");
                }
            }
        }
        
        // One-shot server-side diagnostics. Slide influence has five independent ways to do
        // nothing (section master, per-role flag, stamina floor, speed cap, and the keybind
        // never reaching the server) and until now every one of them was silent, so the whole
        // feature could only be reported as "DI doesn't work". Each distinct reason prints
        // once per process; a server that is working prints one "active" line and nothing more.
        private static readonly HashSet<string> _diagged = new HashSet<string>();

        private static void DiagOnce(string key, string message)
        {
            if (!_diagged.Add(key)) return;
            Debug.Log($"[COMPADJUST] {message}");
        }

        private static void ApplySlideInfluence(PlayerBodyV2 body, Player player)
        {
            var input = player.PlayerInput;
            if (input == null) return;

            var id = player.NetworkObjectId;
            ulong clientId = player.OwnerClientId;

            // Read the binds first, so the refusals below can say whether the player was even
            // asking for influence. A refusal nobody triggered is not worth a log line.
            bool slideForwardHeld = PoncePuck.Keybinds.ServerBridge.IsActionHeld(clientId, "slideinfluenceforward");
            bool slideBackwardHeld = PoncePuck.Keybinds.ServerBridge.IsActionHeld(clientId, "slideinfluencebackward");
            bool slideLeftHeldEarly = PoncePuck.Keybinds.ServerBridge.IsActionHeld(clientId, "slideinfluenceleft");
            bool slideRightHeldEarly = PoncePuck.Keybinds.ServerBridge.IsActionHeld(clientId, "slideinfluenceright");
            bool anyHeld = slideForwardHeld || slideBackwardHeld || slideLeftHeldEarly || slideRightHeldEarly;

            if (!anyHeld)
            {
                // Separates "the server never heard the key" from "the server refused it".
                // IsBound reports what the client declared in PPKB/Hello, so a client that
                // has the bind but whose PPKB/Action never arrives shows bound=True here.
                DiagOnce("no-input",
                    $"Sliding player {clientId} with no slide-influence key held. Declared to this "
                    + $"server: known={PoncePuck.Keybinds.ServerBridge.IsKnown(clientId)} "
                    + $"left={PoncePuck.Keybinds.ServerBridge.IsBound(clientId, "slideinfluenceleft")} "
                    + $"right={PoncePuck.Keybinds.ServerBridge.IsBound(clientId, "slideinfluenceright")} "
                    + $"fwd={PoncePuck.Keybinds.ServerBridge.IsBound(clientId, "slideinfluenceforward")} "
                    + $"back={PoncePuck.Keybinds.ServerBridge.IsBound(clientId, "slideinfluencebackward")}. "
                    + "known=False means no PPKB/Hello ever arrived; bound=False means the key is "
                    + "unbound in the client's keybind config for this role.");
            }

            // Stamina check - need minimum stamina to use influence
            if (body.Stamina.Value < Config.SlideInfluenceMinStamina)
            {
                if (anyHeld)
                    DiagOnce("stamina",
                        $"Slide influence refused: stamina {body.Stamina.Value:F2} is under "
                        + $"SlideInfluenceMinStamina {Config.SlideInfluenceMinStamina:F2}.");
                return;
            }

            // Check current horizontal speed
            Vector3 vel = body.Rigidbody.linearVelocity;
            Vector3 horizontalVel = new Vector3(vel.x, 0, vel.z);
            float currentSpeed = horizontalVel.magnitude;

            // Speed cap check
            if (currentSpeed >= Config.SlideInfluenceMaxSpeed)
            {
                if (anyHeld)
                    DiagOnce("speed",
                        $"Slide influence refused: speed {currentSpeed:F2} m/s is at or over "
                        + $"SlideInfluenceMaxSpeed {Config.SlideInfluenceMaxSpeed:F2}.");
                return;
            }

            Vector3 totalForce = Vector3.zero;

            // --- Forward/Backward influence (client keybinds only) ---

            if (slideForwardHeld || slideBackwardHeld)
            {
                Vector3 forwardDir = body.transform.forward;
                forwardDir.y = 0;
                forwardDir.Normalize();
                
                if (slideForwardHeld)
                    totalForce += forwardDir * Config.SlideInfluenceForce;
                if (slideBackwardHeld)
                    totalForce -= forwardDir * Config.SlideInfluenceForce;
            }
            
            // --- Lateral influence (client keybinds only) ---
            bool slideLeftHeld = slideLeftHeldEarly;
            bool slideRightHeld = slideRightHeldEarly;

            if (slideLeftHeld)
            {
                Vector3 leftDir = -body.transform.right;
                leftDir.y = 0;
                leftDir.Normalize();
                totalForce += leftDir * Config.SlideInfluenceForce;
            }
            
            if (slideRightHeld)
            {
                Vector3 rightDir = body.transform.right;
                rightDir.y = 0;
                rightDir.Normalize();
                totalForce += rightDir * Config.SlideInfluenceForce;
            }
            
            // Apply combined force (smooth, continuous) and drain stamina
            if (totalForce.sqrMagnitude > 0.01f)
            {
                DiagOnce("active",
                    $"Slide influence ACTIVE for client {clientId}: force {totalForce.magnitude:F2} "
                    + $"at SlideInfluenceForce {Config.SlideInfluenceForce:F2}.");
                body.Rigidbody.AddForce(totalForce, ForceMode.Force);

                // Drain stamina (cost per second * fixedDeltaTime)
                float staminaCost = Config.SlideInfluenceStaminaCostPerSecond * Time.fixedDeltaTime;
                body.Stamina.Value = Mathf.Max(0f, body.Stamina.Value - staminaCost);
            }
        }
    }
}
