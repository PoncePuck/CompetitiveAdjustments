// ClientVersionCheck.cs - Tell a player their COMPADJUST is out of date, from the server.
//
// WHY THIS EXISTS, and why the client-side check could never have done it.
//
// A player joined a modded server on a build from before 2026-07-30. Every message we sent
// them died in the reader:
//
//   [ERROR] [COMPADJUST] Error receiving config sync: System.OverflowException: Reading past
//                        the end of the buffer  at ConfigSyncPackage.NetworkSerialize
//   [ERROR] [COMPADJUST] OnGoalTweaksMsg exception: System.OverflowException
//
// so they applied none of the server's geometry and reported the goal offset as broken. Two
// lines above that, in the same log:
//
//   [INFO] [COMPADJUST] Version check: id=3689734278 ... installed=True needsUpdate=False
//
// Steam said the install was current. It was months old. The DLL-stamp check in
// DashFall.VersionPopup can only notice a file replaced AFTER launch, so it was silent too,
// correctly: nothing had changed since launch. Both client-side signals are blind to a build
// that was already stale when the process started, which is the only case that generates bug
// reports.
//
// The server is not blind to it. It can watch whether the client speaks the current protocol
// at all, which is a fact about the client rather than a fact about Steam's bookkeeping.
//
// THE SIGNAL IS SILENCE. A current client sends PPKB/ClientVersion on connect. An old one
// does not, because the message did not exist when it was built, and no message we invent can
// ever be understood by a build that predates it. So absence is the detection, and a grace
// timer is the cost of using it: "has not spoken yet" and "cannot speak" look identical until
// enough time has passed.
//
// HOW THIS DIFFERS FROM THE RULESET MOD, which solves the same problem:
//
//   Ruleset (Ruleset.cs:2749-2765, 2851-2865) has the CLIENT compare the server's version
//   against a hardcoded list of past versions, decide for itself that it is old, and then ask
//   the server to announce it. That is sound, and it has one blind spot: a client old enough
//   to predate that code never asks, so the oldest clients, the ones that actually break, stay
//   invisible. It is the same blind spot our own client-side check has, in a different place.
//
//   Taking the absence as the signal instead is oomtm450's own suggestion, and it inverts
//   that: the older the client, the more certainly it is caught, because the detection needs
//   nothing from it.
//
// Borrowed from them deliberately: not kicking (their DisconnectClient call is commented out
// at Ruleset.cs:2848, and they were right), rate-limiting the notice per client, and the
// wording that tells the player to unsubscribe and resubscribe rather than just "update",
// because Steam reporting the item as current is the whole problem.
//
// Diverged deliberately: the notice is WHISPERED to the player rather than posted publicly
// with their name on it. Ruleset broadcasts it to everyone. Naming and shaming does get
// results, but it is a social decision rather than a technical one, and it is not ours to
// make by default. WarnPublicly flips it if that is wanted.

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace CompetitiveAdjustments
{
    internal static class ClientVersionCheck
    {
        /// <summary>Client to server, once per connection, carrying SharedConstants.MOD_BUILD.</summary>
        internal const string MessageName = "PPKB/ClientVersion";

        /// <summary>
        /// How long a client may stay silent before it is treated as too old to speak.
        ///
        /// This is the whole false-positive surface, so it is generous. A current client sends
        /// within a frame or two of its first connected Update; twenty seconds is far past any
        /// plausible scene load, asset load or bad connection, and the cost of waiting is that
        /// a genuinely stale player is told twenty seconds later. The cost of being too eager
        /// is telling a correctly-updated player their mod is broken, which is worse: it is
        /// the kind of wrong that gets the whole warning ignored.
        /// </summary>
        private const float GraceSeconds = 20f;

        /// <summary>
        /// Re-notify an unchanged stale client no more often than this. Borrowed from
        /// Ruleset's 900 s. A player who cannot act on it immediately should not have it
        /// repeated at them all match.
        /// </summary>
        private const float RenotifySeconds = 900f;

        /// <summary>Sweep cadence. Nothing here is urgent to the frame.</summary>
        private const float PollSeconds = 1f;

        /// <summary>
        /// Whisper (false) or post publicly with the player's name (true), the way Ruleset
        /// does. Public is more effective and less kind; left off.
        ///
        /// static readonly rather than const so flipping it is a one-character edit. As a
        /// const the compiler folds it away and the unused branch becomes CS0162 unreachable
        /// code, which this project builds warning-free and would otherwise have to suppress.
        /// </summary>
        private static readonly bool WarnPublicly = false;

        private const string ChatColor = "#c8a04a";

        // Server state, all keyed by client id and all cleared on disconnect.
        private static readonly Dictionary<ulong, float> _firstSeen = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, int> _reportedBuild = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, float> _lastNotified = new Dictionary<ulong, float>();

        private static float _nextPoll;

        // Client state. Reset whenever we are not a connected client, so a reconnect re-sends.
        private static bool _announced;

        /// <summary>
        /// Pumped from ServerBridge.Runner.Update, which already ticks on every role and
        /// already owns the CustomMessagingManager this depends on.
        /// </summary>
        internal static void Tick(NetworkManager nm)
        {
            if (nm == null) return;

            TickClient(nm);
            if (nm.IsServer) TickServer(nm);
        }

        // ---------- client side ----------

        private static void TickClient(NetworkManager nm)
        {
            // A host is its own server and is trivially in sync with itself; announcing would
            // just be a message to nobody. IsConnectedClient is false until the connection is
            // actually approved, which is what makes this fire once rather than every frame
            // from the moment the NetworkManager exists.
            if (!nm.IsClient || nm.IsServer || !nm.IsConnectedClient)
            {
                if (!nm.IsConnectedClient) _announced = false;
                return;
            }

            if (_announced) return;

            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            try
            {
                using (var w = new FastBufferWriter(sizeof(int), Unity.Collections.Allocator.Temp))
                {
                    w.WriteValueSafe(SharedConstants.MOD_BUILD);
                    cmm.SendNamedMessage(MessageName, NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
                }

                // Set only on success. A send that threw must be retried next tick, or a
                // transient failure would present as a permanently stale client.
                _announced = true;
            }
            catch (Exception e)
            {
                ConfigManager.LogWarning($"Could not announce client build to the server: {e.Message}");
            }
        }

        // ---------- server side ----------

        /// <summary>Handler for PPKB/ClientVersion. Registered alongside the other PPKB handlers.</summary>
        internal static void OnClientVersionMsg(ulong senderId, FastBufferReader reader)
        {
            // Unauthenticated and unauthenticatable by construction: the point is to hear from
            // clients we have never verified. That is safe because the only thing a forged
            // value can buy is SILENCE, i.e. a stale client claiming to be current and thereby
            // suppressing its own warning. Nothing here is read into config or trusted for
            // anything else, so the worst outcome is the status quo before this file existed.
            try
            {
                int build;
                reader.ReadValueSafe(out build);
                _reportedBuild[senderId] = build;

                if (build < SharedConstants.MIN_SUPPORTED_CLIENT_BUILD)
                    ConfigManager.Log($"Client {senderId} reports build {build}, below the minimum " +
                                      $"{SharedConstants.MIN_SUPPORTED_CLIENT_BUILD}.");
            }
            catch (Exception e)
            {
                // A malformed payload is itself evidence of a build that disagrees with us
                // about this message, so it is left in _firstSeen with nothing recorded and
                // the grace sweep picks it up as silent.
                ConfigManager.LogWarning($"OnClientVersionMsg from {senderId}: {e.Message}");
            }
        }

        private static void TickServer(NetworkManager nm)
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            var ids = nm.ConnectedClientsIds;
            if (ids == null) return;

            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];

                // The host's own client. It IS the server; it cannot disagree with itself.
                if (id == NetworkManager.ServerClientId) continue;

                // Polling rather than OnClientConnectedCallback on purpose: this starts the
                // clock from the first sweep that SEES a client, so a client already connected
                // when the handlers were re-registered (which Runner.Update does whenever the
                // CustomMessagingManager is replaced) is still given a full grace period
                // rather than being judged on a callback that fired before we were listening.
                if (!_firstSeen.ContainsKey(id))
                {
                    _firstSeen[id] = now;
                    continue;
                }

                bool reported = _reportedBuild.TryGetValue(id, out int build);
                if (reported && build >= SharedConstants.MIN_SUPPORTED_CLIENT_BUILD) continue;

                // Silent, but not for long enough to conclude anything yet.
                if (!reported && now - _firstSeen[id] < GraceSeconds) continue;

                if (_lastNotified.TryGetValue(id, out float last) && now - last < RenotifySeconds) continue;
                _lastNotified[id] = now;

                Notify(nm, id, reported, build);
            }
        }

        private static void Notify(NetworkManager nm, ulong clientId, bool reported, int build)
        {
            string reason = reported
                ? $"reports build {build}, below the minimum {SharedConstants.MIN_SUPPORTED_CLIENT_BUILD}"
                : $"did not report a build within {GraceSeconds:0}s, so it predates the check entirely";

            // The operator line matters as much as the player line. Half of diagnosing this
            // has been getting a log off the player's machine; now the SERVER knows, and an
            // admin can answer "why does the rink look wrong for one guy" from their own console.
            ConfigManager.LogWarning(
                $"Client {clientId} is running an out-of-date {SharedConstants.MOD_NAME}: {reason}. " +
                "It will not apply this server's arena or goal geometry, and its config sync will " +
                "fail to parse. Told the player to reinstall.");

            // Vanilla chat, and it has to be. Every PPKB/* channel is exactly what this client
            // cannot read; that is how we know it is stale. Chat is the only thing that reaches it.
            //
            // The wording is specific about unsubscribing because Steam reports these installs
            // as up to date, so "update the mod" reads as already done and gets ignored.
            string text =
                $"Your {SharedConstants.MOD_NAME} mod is OUT OF DATE, so this server's rink and goal " +
                "settings are not being applied for you. Steam may still show it as up to date. " +
                "Unsubscribe from COMPADJUST in the Steam Workshop, fully close the game, resubscribe, " +
                "then relaunch.";

            try
            {
                var chat = NetworkBehaviourSingleton<ChatManager>.Instance;
                if (chat == null) return;

                if (WarnPublicly)
                {
                    chat.Server_SendChatMessage(text, ChatColor, ToArray(nm.ConnectedClientsIds));
                    return;
                }

                chat.Server_SendChatMessage(text, ChatColor, new[] { clientId });
            }
            catch (Exception e)
            {
                ConfigManager.LogWarning($"Could not deliver the out-of-date notice to {clientId}: {e.Message}");
            }
        }

        private static ulong[] ToArray(IReadOnlyList<ulong> ids)
        {
            if (ids == null) return new ulong[0];
            var copy = new ulong[ids.Count];
            for (int i = 0; i < ids.Count; i++) copy[i] = ids[i];
            return copy;
        }

        /// <summary>
        /// Called from ServerBridge's existing OnClientLeft. Without this the three
        /// dictionaries grow for the process lifetime on a long-running dedicated server, and
        /// worse, a reconnecting client would inherit its old _lastNotified and stay quiet.
        /// </summary>
        internal static void ForgetClient(ulong clientId)
        {
            _firstSeen.Remove(clientId);
            _reportedBuild.Remove(clientId);
            _lastNotified.Remove(clientId);
        }
    }
}
