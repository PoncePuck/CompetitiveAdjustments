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
        /// Server to client: your build is too old, raise the popup. A separate name rather
        /// than a direction flag on the one above, so neither side has to guess which way a
        /// message was travelling before it can parse it.
        /// </summary>
        internal const string RejectMessageName = "PPKB/BuildRejected";

        /// <summary>
        /// How long a client may stay silent, AFTER it is fully joined, before it is treated
        /// as too old to speak.
        ///
        /// This started at twenty seconds, which was buying safety with the wrong currency.
        /// The thing that makes a fast verdict wrong is judging a client that has not had a
        /// fair chance to answer yet, and wall-clock is a poor proxy for that: it waits just
        /// as long for a client that joined instantly as for one still loading. The join gate
        /// in TickServer measures the real condition instead, so the clock here only has to
        /// cover the round trip plus a hitch or two once the player is actually in.
        ///
        /// A current client answers within a frame or two of IsConnectedClient, so five
        /// seconds is many times the honest worst case, and with PollSeconds below the notice
        /// lands about five seconds after the player appears rather than twenty.
        /// </summary>
        private const float GraceSeconds = 5f;

        /// <summary>
        /// Re-notify an unchanged stale client no more often than this. Borrowed from
        /// Ruleset's 900 s. A player who cannot act on it immediately should not have it
        /// repeated at them all match.
        /// </summary>
        private const float RenotifySeconds = 900f;

        /// <summary>
        /// Sweep cadence. Also the slop on both ends of GraceSeconds, since the clock cannot
        /// start until a sweep sees the player and the verdict cannot land until the next one
        /// after it expires. Half a second keeps that slop small enough not to matter now
        /// that the grace itself is short.
        /// </summary>
        private const float PollSeconds = 0.5f;

        /// <summary>
        /// Whisper (false) or post publicly with the player's name (true), the way Ruleset
        /// does. Public is more effective and less kind; left off.
        ///
        /// static readonly rather than const so flipping it is a one-character edit. As a
        /// const the compiler folds it away and the unused branch becomes CS0162 unreachable
        /// code, which this project builds warning-free and would otherwise have to suppress.
        ///
        /// ONE THING TO KNOW BEFORE FLIPPING IT. The Replay Mod records chat, so this notice
        /// lands in replay files. Whispered, the send goes through RpcTarget.Group with one
        /// real client id, which never routes to the server's own LocalSendRpcTarget, so a
        /// dedicated server does not record its own nag and only the warned player's replay
        /// carries it. Broadcast to ConnectedClientsIds on a LISTEN HOST is different: the
        /// host's own id is in that list, which is exactly why the sweep below has to skip
        /// NetworkManager.ServerClientId, so the notice would be baked into the host's replay
        /// of the match. Harmless, but it is somebody else's name in a file they keep.
        /// </summary>
        private static readonly bool WarnPublicly = false;

        private const string ChatColor = "#c8a04a";

        // Server state, all keyed by client id and all cleared on disconnect.
        //
        // Doubles, because these are absolute times on a machine that stays up for weeks.
        // Time.unscaledTime is a float, and past about 97 days of uptime `now + PollSeconds`
        // stops being distinguishable from `now`, at which point _nextPoll never advances and
        // the sweep below degrades from twice a second to every frame. Halving PollSeconds
        // brought that day forward, which is what made it worth fixing rather than noting.
        private static readonly Dictionary<ulong, double> _firstSeen = new Dictionary<ulong, double>();
        private static readonly Dictionary<ulong, int> _reportedBuild = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, double> _lastNotified = new Dictionary<ulong, double>();

        private static double _nextPoll;

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
            double now = Time.unscaledTimeAsDouble;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            var ids = nm.ConnectedClientsIds;
            if (ids == null) return;

            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];

                // The host's own client. It IS the server; it cannot disagree with itself.
                if (id == NetworkManager.ServerClientId) continue;

                // THE JOIN GATE, and the reason the grace period could drop from twenty
                // seconds to five. A connection exists long before the player does: the
                // client is still loading the level and its mod has not necessarily ticked
                // yet, so a clock started here measures our impatience rather than their
                // silence. Waiting for the Player object with a real username is the same
                // guard Ruleset uses before it says anything (Ruleset.cs:2860), and it is a
                // far better proxy for "has had a fair chance to answer" than any duration.
                //
                // It also means a client that connects and drops during the load is never
                // accused of anything, because it never reaches this line.
                if (!IsFullyJoined(id)) continue;

                // Polling rather than OnClientConnectedCallback on purpose: the clock starts
                // from the first sweep that sees a JOINED client, so a client already present
                // when the handlers were re-registered (which Runner.Update does whenever the
                // CustomMessagingManager is replaced) still gets a full grace period rather
                // than being judged on a callback that fired before we were listening.
                if (!_firstSeen.ContainsKey(id))
                {
                    _firstSeen[id] = now;
                    continue;
                }

                bool reported = _reportedBuild.TryGetValue(id, out int build);
                if (reported && build >= SharedConstants.MIN_SUPPORTED_CLIENT_BUILD) continue;

                // Silent, but not for long enough to conclude anything yet.
                if (!reported && now - _firstSeen[id] < GraceSeconds) continue;

                if (_lastNotified.TryGetValue(id, out double last) && now - last < RenotifySeconds) continue;
                _lastNotified[id] = now;

                Notify(nm, id, reported, build);

                // Only a client that answered can be told anything it will understand.
                if (reported) SendPopupTrigger(nm, id, build);
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

        /// <summary>
        /// True once the connection has become an actual player with a name.
        ///
        /// Everything is wrapped, because this runs on a sweep and PlayerManager is exactly
        /// the kind of thing that is null during a level transition. Any failure reads as
        /// "not joined yet", which delays a verdict rather than inventing one, and that is
        /// the right way for this particular check to fail.
        /// </summary>
        private static bool IsFullyJoined(ulong clientId)
        {
            try
            {
                var pm = PlayerManager.Instance;
                if (pm == null) return false;

                var player = pm.GetPlayerByClientId(clientId);
                if (player == null) return false;

                var name = player.Username.Value;
                return !string.IsNullOrEmpty(name.ToString());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tells a client that speaks our protocol to raise its own out-of-date popup.
        ///
        /// Only reachable for a client that REPORTED a build, which is the same thing as
        /// saying it is new enough to understand this message. A client detected by silence
        /// cannot be sent anything it could read, by definition, so chat remains the only
        /// thing that reaches it and this is skipped.
        ///
        /// Today MIN_SUPPORTED_CLIENT_BUILD equals the first build that reports at all, so
        /// this path is empty. It becomes the main path the first time the minimum is raised,
        /// which is exactly when a popup beats a line of chat somebody scrolled past.
        /// </summary>
        private static void SendPopupTrigger(NetworkManager nm, ulong clientId, int theirBuild)
        {
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            try
            {
                using (var w = new FastBufferWriter(sizeof(int) * 2, Unity.Collections.Allocator.Temp))
                {
                    w.WriteValueSafe(theirBuild);
                    w.WriteValueSafe(SharedConstants.MIN_SUPPORTED_CLIENT_BUILD);
                    cmm.SendNamedMessage(RejectMessageName, clientId, w, NetworkDelivery.Reliable);
                }
            }
            catch (Exception e)
            {
                ConfigManager.LogWarning($"Could not send the build-rejected popup to {clientId}: {e.Message}");
            }
        }

        /// <summary>
        /// CLIENT SIDE. The server has told us our build is too old; show the popup at once
        /// rather than leaving the player to notice a grey line in chat.
        /// </summary>
        internal static void OnBuildRejectedMsg(ulong senderId, FastBufferReader reader)
        {
            // Server to client only. A peer client sending this would be able to fake an
            // out-of-date popup on someone else's screen, which is harmless but silly.
            var nm = NetworkManager.Singleton;
            if (senderId != NetworkManager.ServerClientId) return;
            if (nm != null && nm.IsServer) return;   // host talking to itself

            try
            {
                int ourBuild, minimum;
                reader.ReadValueSafe(out ourBuild);
                reader.ReadValueSafe(out minimum);

                ConfigManager.LogWarning(
                    $"The server rejected this build: we are {ourBuild}, it requires {minimum} or newer. " +
                    "Raising the out-of-date popup.");

                DashFallMod.Client.DashFallClientRunner.RaiseServerRejectedPopup(ourBuild, minimum);
            }
            catch (Exception e)
            {
                ConfigManager.LogWarning($"OnBuildRejectedMsg: {e.Message}");
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
