// ReplayLegPads.cs - Carry goalie leg pad pose through the vanilla replay system.
//
// The recorder stores exactly ten fields per body and none of them describe pad
// pose: a half butterfly and a velocity-extended pad both record as
// IsSliding=true, IsExtendedLeft=false, IsExtendedRight=false, because Stances
// zeroes the extend inputs before vanilla HandleInputs and DashExtendSuppressPatches
// reverts the NetworkVariables. Playback cannot re-derive the pose either: a replay
// body is RigidbodyConstraints.FreezeAll so UpdateVelocityExtend always reads zero
// velocity, and vanilla skips HandleInputs for replay bodies so Stances never seeds
// its state. So we record the two floats and two bools that are the entire input to
// both client appliers, and push them back through the existing CMM channels at
// playback time.
//
// This costs nothing in wire format or compatibility. ReplayRecorder.EventMap is a
// SortedList<int, List<(string, object)>> of boxed payloads that never leaves the
// server process: it is not serialized, not sent, and not written to disk, so there
// is no format to version. Server_ReplayEvent and Server_SkipEvent are switches with
// no default case, so vanilla silently ignores our event name. Our payload is
// per-tick absolute state rather than cumulative, so skipping it during the replay
// pre-roll loses nothing and no Server_SkipEvent patch is needed.
//
// Deliberately in the DashFallMod namespace, which DashFallGameMod patches
// unconditionally on every role. Both patched methods are IsServer-gated by vanilla,
// so on a pure client these patches exist and are never invoked.

using System;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace DashFallMod
{
    /// <summary>
    /// Boxed into ReplayRecorder.EventMap, which holds System.Object and never leaves
    /// the server process. Nothing here is serialized, so field layout is free to change.
    /// </summary>
    internal struct ReplayLegPadState
    {
        public ulong OwnerClientId;
        public float LeftExtension;
        public float RightExtension;
        public bool StanceLeft;
        public bool StanceRight;
    }

    [HarmonyPatch]
    internal static class ReplayLegPadPatches
    {
        internal const string EventName = "CA/LegPads";

        /// <summary>
        /// ReplayRecorder.Update calls Server_Tick() and only then Tick++, so a postfix
        /// appends into the same tick bucket vanilla just filled, immediately after that
        /// tick's PlayerBodyMove for the same player.
        /// </summary>
        [HarmonyPatch(typeof(ReplayRecorder), "Server_Tick")]
        [HarmonyPostfix]
        public static void Server_Tick_Postfix(ReplayRecorder __instance)
        {
            if (!GoalieDashExtend.Enabled && !Stances.Enabled) return;

            try
            {
                var pm = PlayerManager.Instance;
                if (pm == null) return;

                // GetSpawnedPlayers defaults to includeReplay:false, so a replay running
                // during a goal celebration cannot record its own ghosts back into the map.
                foreach (var p in pm.GetSpawnedPlayers())
                {
                    if (p == null) continue;
                    if (p.Role != PlayerRole.Goalie) continue;

                    var body = p.PlayerBody;
                    if (body == null) continue;

                    GoalieDashExtend.TryGetBodyExtensions(body, out float left, out float right);
                    var st = Stances.GetState(body);

                    __instance.Server_AddReplayEvent(EventName, new ReplayLegPadState
                    {
                        OwnerClientId = p.OwnerClientId,
                        LeftExtension = left,
                        RightExtension = right,
                        StanceLeft = st.extendLeft,
                        StanceRight = st.extendRight
                    });
                }
            }
            catch (Exception e) { Debug.LogWarning("[CA/Replay] leg pad record failed: " + e.Message); }
        }

        /// <summary>
        /// Postfix rather than prefix so vanilla has already applied this tick's
        /// PlayerBodyMove (IsSliding / IsExtendedLeft / IsExtendedRight, which drive the
        /// pad's vanilla state enum) before we push our pose on top of it.
        /// </summary>
        [HarmonyPatch(typeof(ReplayPlayer), "Server_ReplayEvent")]
        [HarmonyPostfix]
        public static void Server_ReplayEvent_Postfix(string eventName, object eventData)
        {
            if (eventName != EventName) return;

            // An escape from here would abort the foreach in ReplayPlayer.Server_Tick and
            // drop the rest of this tick's PlayerBodyMove and PuckMove events, so the
            // whole replay would stall on one bad frame. Swallow everything.
            try
            {
                if (!(eventData is ReplayLegPadState d)) return;

                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer) return;

                var pm = PlayerManager.Instance;
                if (pm == null) return;

                // Replay players own client id + 1337; the ghost may already be gone.
                var rp = pm.GetReplayPlayerByClientId(d.OwnerClientId);
                if (rp == null) return;

                var body = rp.PlayerBody;
                if (body == null) return;

                var netObj = body.GetComponent<NetworkObject>();
                if (netObj == null) return;

                GoalieDashExtend.ServerBroadcastExtension(netObj.NetworkObjectId, d.LeftExtension, d.RightExtension);
                Stances.ServerBroadcastStance(netObj.NetworkObjectId, d.StanceLeft, d.StanceRight);

                // SendNamedMessageToAll never self-delivers to the local client and both
                // CMM handlers are only registered when !IsServer, so a host watching its
                // own replay has to be fed directly.
                if (nm.IsClient)
                {
                    GoalieDashExtend.ClientApplyExtension(body, d.LeftExtension, d.RightExtension);
                    Stances.ClientApplyStance(body, d.StanceLeft, d.StanceRight);
                }
            }
            catch (Exception e) { Debug.LogWarning("[CA/Replay] leg pad apply failed: " + e.Message); }
        }
    }
}
