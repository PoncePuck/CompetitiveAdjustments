// DashFall.ServerBridge.cs - Server-side message handler for client keybinds
// Receives PPKB/Hello and PPKB/Action messages from clients

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PoncePuck.Keybinds
{
    /// <summary>
    /// Stores which features are enabled on the server for each role
    /// </summary>
    public class ServerFeatures
    {
        public bool SkaterDashEnabled = true;
        public bool SkaterDiveEnabled = true;
        public bool SkaterSlideInfluenceEnabled = true;
        public bool SkaterTwistEnabled = true;
        public bool GoalieDiveEnabled = true;
        public bool GoalieSlideInfluenceEnabled = false;
        public bool GoalieTwistEnabled = false;
        public bool GoalieStandingDashEnabled = true;
        public bool GoalieDashExtendEnabled = true;
        public bool GoalieStancesEnabled = true;
        public bool SprintShoulderTrailEnabled = true;
        
        /// <summary>Pack features into a ushort for network transmission</summary>
        public ushort ToUShort()
        {
            ushort b = 0;
            if (SkaterDashEnabled) b |= 1;
            if (SkaterDiveEnabled) b |= 2;
            if (SkaterSlideInfluenceEnabled) b |= 4;
            if (SkaterTwistEnabled) b |= 8;
            if (GoalieDiveEnabled) b |= 16;
            if (GoalieSlideInfluenceEnabled) b |= 32;
            if (GoalieTwistEnabled) b |= 64;
            if (GoalieStandingDashEnabled) b |= 128;
            if (GoalieDashExtendEnabled) b |= 256;
            if (GoalieStancesEnabled) b |= 512;
            if (SprintShoulderTrailEnabled) b |= 1024;
            return b;
        }
        
        /// <summary>Legacy: Pack features into a byte (for backwards compat)</summary>
        public byte ToByte() => (byte)(ToUShort() & 0xFF);
        
        /// <summary>Unpack features from a ushort</summary>
        public static ServerFeatures FromUShort(ushort b)
        {
            return new ServerFeatures
            {
                SkaterDashEnabled = (b & 1) != 0,
                SkaterDiveEnabled = (b & 2) != 0,
                SkaterSlideInfluenceEnabled = (b & 4) != 0,
                SkaterTwistEnabled = (b & 8) != 0,
                GoalieDiveEnabled = (b & 16) != 0,
                GoalieSlideInfluenceEnabled = (b & 32) != 0,
                GoalieTwistEnabled = (b & 64) != 0,
                GoalieStandingDashEnabled = (b & 128) != 0,
                GoalieDashExtendEnabled = (b & 256) != 0,
                GoalieStancesEnabled = (b & 512) != 0,
                SprintShoulderTrailEnabled = (b & 1024) != 0
            };
        }
        
        /// <summary>Legacy: Unpack features from a byte</summary>
        public static ServerFeatures FromByte(byte b) => FromUShort(b);
    }
    
    public static class ServerBridge
    {
        private static bool _hooked;
        private static GameObject _host;
        private static CustomMessagingManager _cmm;

        // per-client declared binds & held states
        private static readonly Dictionary<ulong, HashSet<string>> _declared =
            new Dictionary<ulong, HashSet<string>>();
        private static readonly Dictionary<ulong, HashSet<string>> _held =
            new Dictionary<ulong, HashSet<string>>();
            
        // Client-side: features received from server
        public static ServerFeatures ReceivedFeatures { get; private set; } = new ServerFeatures();
        public static bool HasReceivedFeatures { get; private set; } = false;
        public static event Action OnFeaturesReceived;

        // Client-side: full server config mirror state (admin config editor).
        // HasReceivedFullConfig flips true once the PPKB/ConfigFull broadcast has
        // been reassembled into ConfigManager.Config so the editor renders true
        // live values.  Mirrors HasReceivedFeatures / OnFeaturesReceived.
        public static bool HasReceivedFullConfig { get; private set; } = false;
        public static event Action OnFullConfigReceived;

        // Client-side: admin editor unlock state, set from PPKB/AdminAuthResult.
        public static bool AdminUnlocked { get; private set; } = false;
        public static string AdminAuthReason { get; private set; } = "";
        public static event Action<bool, string> OnAdminAuthResult;

        // Server-side: client ids that passed an editor auth check this session.
        // Cleared on disconnect; an inbound PPKB/AdminConfigSet from a client not
        // in this set is rejected (server is the only authority).
        private static readonly HashSet<ulong> _authedClients = new HashSet<ulong>();

        // Reassemblers for the chunked string transport (config JSON exceeds the
        // single-message transport cap).  One per receive direction, keyed by
        // sender id so concurrent transfers from different peers stay separate.
        private static readonly StringReassembler _configFullRx = new StringReassembler(); // client side
        private static readonly StringReassembler _configSetRx = new StringReassembler();   // server side

        // event if a mod wants real-time notification
        public static event Action<ulong, string, bool> OnAction; // clientId, action, isDown

        public static void Hook(string ownerTag)
        {
            if (_hooked) return;
            _hooked = true;

            _host = new GameObject("PPKB_ServerBridgeHost_" + ownerTag);
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<Runner>();
        }

        public static void Unhook()
        {
            _hooked = false;
            try
            {
                if (_cmm != null)
                {
                    _cmm.UnregisterNamedMessageHandler("PPKB/Hello");
                    _cmm.UnregisterNamedMessageHandler("PPKB/Action");
                    _cmm.UnregisterNamedMessageHandler("PPKB/Features");
                    _cmm.UnregisterNamedMessageHandler("PPKB/GoalTweaks");
                    _cmm.UnregisterNamedMessageHandler("PPKB/AdminAuth");
                    _cmm.UnregisterNamedMessageHandler("PPKB/AdminAuthResult");
                    _cmm.UnregisterNamedMessageHandler("PPKB/AdminConfigSet");
                    _cmm.UnregisterNamedMessageHandler("PPKB/ConfigFull");
                    _cmm.UnregisterNamedMessageHandler("PPKB/ConfigReq");
                    _cmm = null;
                }
            }
            catch { }
            try { if (_host != null) UnityEngine.Object.Destroy(_host); } catch { }
            _host = null;
            _declared.Clear();
            _held.Clear();
            _authedClients.Clear();
            _configFullRx.ClearAll();
            _configSetRx.ClearAll();
            OnAction = null;
            HasReceivedFeatures = false;
            ReceivedFeatures = new ServerFeatures();
            HasReceivedFullConfig = false;
            AdminUnlocked = false;
            AdminAuthReason = "";
            OnFullConfigReceived = null;
            OnAdminAuthResult = null;
            DashFallMod.GoalNetTweaks.ClearSyncedTweaks();
            OnFeaturesReceived = null;
        }
        
        /// <summary>
        /// Reset client-side feature state (call when disconnecting)
        /// </summary>
        public static void ResetClientFeatures()
        {
            HasReceivedFeatures = false;
            ReceivedFeatures = new ServerFeatures();
            HasReceivedFullConfig = false;
            AdminUnlocked = false;
            AdminAuthReason = "";
            _configFullRx.ClearAll();
        }

        public static bool IsKnown(ulong clientId)
        {
            return _declared.ContainsKey(clientId);
        }

        public static bool IsBound(ulong clientId, string action)
        {
            HashSet<string> set;
            if (!_declared.TryGetValue(clientId, out set) || set == null) return false;
            string canon = Canon(action);
            return set.Contains(canon);
        }

        public static bool IsActionHeld(ulong clientId, string action)
        {
            HashSet<string> set;
            if (!_held.TryGetValue(clientId, out set) || set == null)
            {
                // Only log occasionally to avoid spam
                return false;
            }
            string canon = Canon(action);
            bool result = set.Contains(canon);
            return result;
        }
        
        // Debug method to check state
        public static void LogState()
        {
            // Debug helper - can be called from console if needed
        }

        // Every PPKB handler is registered on every role (see Runner.Update), because
        // a host is both ends at once and the registration happens before we know
        // which role this process will end up in.  That means the four server->client
        // handlers are also live on a server, where a client can reach them just by
        // sending their name: NGO dispatches purely on the name hash.
        //
        // The transport supplies senderId, so it cannot be forged from the payload.
        // Only NetworkManager.ServerClientId (0) is ever the authority: a remote
        // client is always >= 1, while the host's own loopback send is 0 and must
        // keep working (the admin editor unlock depends on it).  Gating on IsServer
        // instead would break that loopback, which is why this checks the sender.
        private static bool FromServer(ulong senderId, string msgName)
        {
            if (senderId == NetworkManager.ServerClientId) return true;
            Debug.LogWarning($"[COMPADJUST] Rejected {msgName} from client {senderId}: server->client message, clients may not send it.");
            return false;
        }

        private static string Canon(string a)
        {
            if (string.IsNullOrEmpty(a)) return "";
            a = a.Trim().ToLowerInvariant();
            if (a == "dash-left" || a == "dash_l" || a == "dashl") return "dashleft";
            if (a == "dash-right" || a == "dash_r" || a == "dashr") return "dashright";
            if (a == "twist-left" || a == "twist_l" || a == "twistl") return "twistleft";
            if (a == "twist-right" || a == "twist_r" || a == "twistr") return "twistright";
            if (a == "spawn_puck" || a == "spawn-puck" || a == "sp") return "spawnpuck";
            return a;
        }

        // Mono host to poll for NetworkManager & (re)register handlers safely
        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                var nm = NetworkManager.Singleton;
                if (nm == null) return;

                // Re-register if needed
                if (_cmm != nm.CustomMessagingManager)
                {
                    TryUnregister();
                    _cmm = nm.CustomMessagingManager;
                    if (_cmm != null)
                    {
                        _cmm.RegisterNamedMessageHandler("PPKB/Hello", OnHello);
                        _cmm.RegisterNamedMessageHandler("PPKB/Action", OnActionMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/Features", OnFeaturesMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/GoalTweaks", OnGoalTweaksMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/AdminAuth", OnAdminAuthMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/AdminAuthResult", OnAdminAuthResultMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/AdminConfigSet", OnAdminConfigSetMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/ConfigFull", OnConfigFullMsg);
                        _cmm.RegisterNamedMessageHandler("PPKB/ConfigReq", OnConfigReqMsg);
                        nm.OnClientDisconnectCallback += OnClientLeft;
                        if (DashFallMod.ConfigManager.Config.EnableDebugLogs)
                            DashFallMod.ConfigManager.Dbg($"Registered CMM handlers. IsServer={nm.IsServer} IsHost={nm.IsHost} IsClient={nm.IsClient}");
                    }
                }
            }

            private void OnDestroy() { TryUnregister(); }

            private static void TryUnregister()
            {
                try
                {
                    var nm = NetworkManager.Singleton;
                    if (_cmm != null)
                    {
                        _cmm.UnregisterNamedMessageHandler("PPKB/Hello");
                        _cmm.UnregisterNamedMessageHandler("PPKB/Action");
                        _cmm.UnregisterNamedMessageHandler("PPKB/Features");
                        _cmm.UnregisterNamedMessageHandler("PPKB/GoalTweaks");
                        _cmm.UnregisterNamedMessageHandler("PPKB/AdminAuth");
                        _cmm.UnregisterNamedMessageHandler("PPKB/AdminAuthResult");
                        _cmm.UnregisterNamedMessageHandler("PPKB/AdminConfigSet");
                        _cmm.UnregisterNamedMessageHandler("PPKB/ConfigFull");
                        _cmm.UnregisterNamedMessageHandler("PPKB/ConfigReq");
                    }
                    if (nm != null) nm.OnClientDisconnectCallback -= OnClientLeft;
                }
                catch { }
                _cmm = null;
            }

            private static void OnClientLeft(ulong clientId)
            {
                _declared.Remove(clientId);
                _held.Remove(clientId);
                _authedClients.Remove(clientId);
                _configSetRx.Clear(clientId);
            }
        }
        
        // ---------- Client-side feature message handler ----------
        private static void OnFeaturesMsg(ulong senderId, FastBufferReader reader)
        {
            if (!FromServer(senderId, "PPKB/Features")) return;
            try
            {
                ushort featureFlags;
                reader.ReadValueSafe(out featureFlags);
                ReceivedFeatures = ServerFeatures.FromUShort(featureFlags);
                HasReceivedFeatures = true;
                OnFeaturesReceived?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] OnFeaturesMsg exception: {e}");
            }
        }

        /// <summary>
        /// Layout guard for PPKB/GoalTweaks. Bump it whenever the layout OR THE MEANING
        /// of any value in it changes.
        ///
        /// The payload is positional: a fixed run of values with no field names. That is
        /// fine until the meaning of a slot changes without the layout changing, which has
        /// now happened twice in a day (the arena scale axes were renamed to world axes,
        /// and the rotation triple was removed). Two builds either side of such a change
        /// agree on how to read every byte and disagree on what the bytes mean, and that
        /// is far worse than a parse error: the rink comes out scaled on the wrong axes,
        /// and because the chunked position grid is sized from the rink's length, the two
        /// ends size it differently and players teleport.
        ///
        /// A mismatch now costs the arena resize on that connection, which is a visible
        /// but harmless "server looks vanilla", instead of a silently wrong world.
        /// </summary>
        private const int ArenaSyncWireVersion = unchecked((int)0xCA000002);

        private static int _warnedArenaSyncVersion;

        private static void WarnArenaSyncVersionOnce(int received)
        {
            if (_warnedArenaSyncVersion == received) return;
            _warnedArenaSyncVersion = received;

            Debug.LogWarning(
                $"[COMPADJUST] Ignoring the server's arena sync: it was sent by an incompatible " +
                $"CompetitiveAdjustments build (wire 0x{received:X8}, expected 0x{ArenaSyncWireVersion:X8}). " +
                "The rink will stay vanilla-sized here rather than risk a mismatched arena. " +
                "Update the mod on BOTH the server and the client.");
        }

        private static void OnGoalTweaksMsg(ulong senderId, FastBufferReader reader)
        {
            // The wire-version check below is a compatibility check, not an
            // authorization check: the constant ships in the DLL, so anyone can
            // send it.  Without this guard a client could hand the server arena
            // scales that SetSyncedTweaks writes straight into the live config.
            if (!FromServer(senderId, "PPKB/GoalTweaks")) return;
            try
            {
                int wireVersion;
                reader.ReadValueSafe(out wireVersion);
                if (wireVersion != ArenaSyncWireVersion)
                {
                    WarnArenaSyncVersionOnce(wireVersion);
                    return;
                }

                bool enabled;
                float thicknessScale;
                float scaleX;
                float scaleY;
                float scaleZ;
                float goalBackOffset;
                bool arenaEnabled;
                float arenaScaleX;
                float arenaScaleY;
                float arenaScaleZ;
                float arenaOffsetX;
                float arenaOffsetY;
                float arenaOffsetZ;

                reader.ReadValueSafe(out enabled);
                reader.ReadValueSafe(out thicknessScale);
                reader.ReadValueSafe(out scaleX);
                reader.ReadValueSafe(out scaleY);
                reader.ReadValueSafe(out scaleZ);
                reader.ReadValueSafe(out goalBackOffset);
                reader.ReadValueSafe(out arenaEnabled);
                reader.ReadValueSafe(out arenaScaleX);
                reader.ReadValueSafe(out arenaScaleY);
                reader.ReadValueSafe(out arenaScaleZ);
                reader.ReadValueSafe(out arenaOffsetX);
                reader.ReadValueSafe(out arenaOffsetY);
                reader.ReadValueSafe(out arenaOffsetZ);

                DashFallMod.GoalNetTweaks.SetSyncedTweaks(
                    enabled,
                    thicknessScale,
                    scaleX,
                    scaleY,
                    scaleZ,
                    goalBackOffset,
                    arenaEnabled,
                    arenaScaleX,
                    arenaScaleY,
                    arenaScaleZ,
                    arenaOffsetX,
                    arenaOffsetY,
                    arenaOffsetZ);
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] OnGoalTweaksMsg exception: {e}");
            }
        }
        
        /// <summary>
        /// Server calls this to send feature flags to a specific client
        /// </summary>
        public static void SendFeaturesToClient(ulong clientId, ServerFeatures features)
        {
            if (_cmm == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            
            try
            {
                using (var writer = new FastBufferWriter(2, Unity.Collections.Allocator.Temp))
                {
                    writer.WriteValueSafe(features.ToUShort());
                    _cmm.SendNamedMessage("PPKB/Features", clientId, writer);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] SendFeaturesToClient exception: {e}");
            }
        }

        private static ServerFeatures BuildCurrentFeaturesOrNull()
        {
            try { DashFallMod.ConfigManager.EnsureConfig(); }
            catch (Exception e) { CompetitiveAdjustments.ConfigManager.LogWarning("EnsureConfig threw: " + e.Message); }

            var cfg = DashFallMod.ConfigManager.Config;
            if (cfg == null)
            {
                CompetitiveAdjustments.ConfigManager.LogError("BuildCurrentFeatures: Config is null!");
                return null;
            }

            var compAdjust = DashFallMod.ConfigManager.CompAdjustEffective;
            if (compAdjust == null)
            {
                CompetitiveAdjustments.ConfigManager.LogError("BuildCurrentFeatures: CompAdjust is null!");
                return null;
            }

            return new ServerFeatures
            {
                SkaterDashEnabled = cfg.SkaterDashEnabled,
                SkaterDiveEnabled = cfg.SkaterDiveEnabled,
                SkaterSlideInfluenceEnabled = cfg.EnableSlideInfluence,
                SkaterTwistEnabled = cfg.EnableTwistWhileSliding,
                GoalieDiveEnabled = cfg.GoalieDiveEnabled,
                GoalieSlideInfluenceEnabled = cfg.GoalieSlideInfluenceEnabled,
                GoalieTwistEnabled = cfg.GoalieTwistWhileSlidingEnabled,
                GoalieStandingDashEnabled = cfg.GoalieStandingDashEnabled,
                GoalieDashExtendEnabled = cfg.GoalieDashExtendEnabled,
                GoalieStancesEnabled = cfg.GoalieStancesEnabled,
                SprintShoulderTrailEnabled = compAdjust.SprintShoulderTrailEnabled
            };
        }

        public static void BroadcastFeaturesToAllClients()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var features = BuildCurrentFeaturesOrNull();
            if (features == null) return;

            var pm = PlayerManager.Instance;
            var players = pm != null ? pm.GetPlayers() : null;
            if (players == null || players.Count == 0)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("No players to send features to");
                return;
            }

            foreach (var player in players)
                SendFeaturesToClient(player.OwnerClientId, features);
        }

        public static void SendInitialStateToClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var features = BuildCurrentFeaturesOrNull();
            if (features != null) SendFeaturesToClient(clientId, features);

            SendGoalTweaksToClient(clientId);
            SendConfigFullToClient(clientId);
        }

        public static void SendGoalTweaksToClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _cmm == null) return;

            var cfg = DashFallMod.ConfigManager.CompAdjustEffective;
            if (cfg == null)
            {
                CompetitiveAdjustments.ConfigManager.LogError("SendGoalTweaksToClient: CompAdjust is null!");
                return;
            }

            try
            {
                using (var writer = new FastBufferWriter(160, Unity.Collections.Allocator.Temp))
                {
                    writer.WriteValueSafe(ArenaSyncWireVersion);
                    writer.WriteValueSafe(cfg.EnableGoalNetTweaks);
                    writer.WriteValueSafe(cfg.GoalThicknessScale);
                    writer.WriteValueSafe(cfg.GoalSizeScaleX);
                    writer.WriteValueSafe(cfg.GoalSizeScaleY);
                    writer.WriteValueSafe(cfg.GoalSizeScaleZ);
                    writer.WriteValueSafe(cfg.GoalBackOffset);
                    writer.WriteValueSafe(cfg.EnableArenaTweaks);
                    // WORLD axes, in world order: X width, Y height, Z length. The config
                    // fields have meant exactly this since ConfigVersion 16; before that
                    // Y and Z were swapped here too. Anyone changing this order or its
                    // meaning must bump ArenaSyncWireVersion.
                    writer.WriteValueSafe(cfg.ArenaScaleX);
                    writer.WriteValueSafe(cfg.ArenaScaleY);
                    writer.WriteValueSafe(cfg.ArenaScaleZ);
                    writer.WriteValueSafe(cfg.ArenaOffsetX);
                    writer.WriteValueSafe(cfg.ArenaOffsetY);
                    writer.WriteValueSafe(cfg.ArenaOffsetZ);
                    _cmm.SendNamedMessage("PPKB/GoalTweaks", clientId, writer);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] SendGoalTweaksToClient exception: {e}");
            }
        }

        public static void BroadcastGoalTweaksToAllClients()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _cmm == null) return;

            var pm = PlayerManager.Instance;
            var players = pm != null ? pm.GetPlayers() : null;
            if (players == null) return;

            foreach (var player in players)
                SendGoalTweaksToClient(player.OwnerClientId);
        }

        // ========================================================================
        // Admin config editor channels
        //   PPKB/AdminAuth        client -> server : string password
        //   PPKB/AdminAuthResult  server -> client : bool granted, string reason
        //   PPKB/AdminConfigSet   client -> server : chunked full-config JSON
        //   PPKB/ConfigFull       server -> client : chunked full-config JSON
        // The two config channels carry the SerializeForWire() shape, which never
        // contains the Admin credential block.
        // ========================================================================

        // Max characters per chunk for the chunked string transport.  Each chunk
        // is one named message of [byte total][byte index][string], which
        // serializes to chunkChars*2 + 6 bytes.  NGO's non-fragmented reliable
        // named messages are capped near the MTU (~1280 bytes), so 500 chars
        // (~1006 bytes) stays safely under it.  1500 did NOT: it produced a 3006
        // byte message and threw "Writing past the end of the buffer, size is
        // 3006 bytes but remaining capacity is 1280 bytes", so the full config
        // never reached clients.  Splitting on a char boundary is safe because
        // the payload is ASCII JSON (no surrogate pairs).
        private const int ConfigChunkChars = 500;

        // Byte size to allocate a FastBufferWriter for a string of the given
        // char count: 2 bytes/char + 4-byte length prefix + slack.
        private static int StringWireCap(int charCount) => charCount * 2 + 16;

        // ---- Client -> server senders --------------------------------------

        public static void SendAdminAuth(string password)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || _cmm == null) return;
            try
            {
                string pw = password ?? "";
                int cap = StringWireCap(pw.Length);
                using (var w = new FastBufferWriter(cap, Unity.Collections.Allocator.Temp, cap))
                {
                    w.WriteValueSafe(pw);
                    _cmm.SendNamedMessage("PPKB/AdminAuth", NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
                }
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] SendAdminAuth exception: {e}"); }
        }

        public static void SendAdminConfigSet(string wireJson)
        {
            if (_cmm == null) return;
            if (!CompetitiveAdjustments.AdminAuth.AssertNoCredentials(wireJson)) return;
            SendStringInParts("PPKB/AdminConfigSet", NetworkManager.ServerClientId, wireJson);
        }

        // ---- Server -> client senders --------------------------------------

        private static void SendAdminAuthResult(ulong clientId, bool granted, string reason)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _cmm == null) return;
            try
            {
                string r = reason ?? "";
                int cap = StringWireCap(r.Length) + 1; // +1 for the bool
                using (var w = new FastBufferWriter(cap, Unity.Collections.Allocator.Temp, cap))
                {
                    w.WriteValueSafe(granted);
                    w.WriteValueSafe(r);
                    _cmm.SendNamedMessage("PPKB/AdminAuthResult", clientId, w, NetworkDelivery.Reliable);
                }
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] SendAdminAuthResult exception: {e}"); }
        }

        public static void SendConfigFullToClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _cmm == null) return;

            var cfg = CompetitiveAdjustments.ConfigManager.Config;
            if (cfg == null)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning($"ConfigFull to client {clientId} skipped: live config is null.");
                return;
            }

            string json = cfg.SerializeForWire();
            if (!CompetitiveAdjustments.AdminAuth.AssertNoCredentials(json)) return; // never leak creds (already logged)
            SendStringInParts("PPKB/ConfigFull", clientId, json);
        }

        public static void BroadcastConfigFullToAllClients()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || _cmm == null) return;

            var pm = PlayerManager.Instance;
            var players = pm != null ? pm.GetPlayers() : null;
            if (players == null) return;

            foreach (var player in players)
                SendConfigFullToClient(player.OwnerClientId);
        }

        // Client -> server: ask the server to (re)send the full config.  The
        // initial Hello-time push can be missed during connection setup, so the
        // client retries this until HasReceivedFullConfig (see DashFallClientRunner).
        public static void RequestConfigFull()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || _cmm == null || nm.IsServer) return; // host already has it
            try
            {
                using (var w = new FastBufferWriter(8, Unity.Collections.Allocator.Temp))
                {
                    w.WriteValueSafe((byte)1);
                    _cmm.SendNamedMessage("PPKB/ConfigReq", NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
                }
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] RequestConfigFull exception: {e}"); }
        }

        // Server handler for a client's config request: push the (credential-free)
        // full config back to just that client.
        private static void OnConfigReqMsg(ulong senderId, FastBufferReader reader)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            try
            {
                SendConfigFullToClient(senderId);
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] OnConfigReqMsg exception: {e}"); }
        }

        // Splits an ASCII JSON payload into [byte total][byte index][string chunk]
        // parts over a single named message.  Reassembled by StringReassembler.
        private static void SendStringInParts(string msgName, ulong target, string payload)
        {
            if (_cmm == null) return;
            payload = payload ?? "";
            int total = Mathf.Max(1, Mathf.CeilToInt(payload.Length / (float)ConfigChunkChars));
            if (total > 255)
            {
                Debug.LogError($"[COMPADJUST] SendStringInParts: payload too large ({payload.Length} chars) for {msgName}.");
                return;
            }

            for (int i = 0; i < total; i++)
            {
                int start = i * ConfigChunkChars;
                int len = Mathf.Min(ConfigChunkChars, payload.Length - start);
                string chunk = len > 0 ? payload.Substring(start, len) : "";
                int cap = StringWireCap(chunk.Length) + 2; // +2 for total/index bytes
                try
                {
                    using (var w = new FastBufferWriter(cap, Unity.Collections.Allocator.Temp, cap))
                    {
                        w.WriteValueSafe((byte)total);
                        w.WriteValueSafe((byte)i);
                        w.WriteValueSafe(chunk);
                        _cmm.SendNamedMessage(msgName, target, w, NetworkDelivery.Reliable);
                    }
                }
                catch (Exception e) { Debug.LogError($"[COMPADJUST] SendStringInParts({msgName}) exception: {e}"); }
            }
        }

        // ---- Server-side handlers ------------------------------------------

        private static void OnAdminAuthMsg(ulong senderId, FastBufferReader reader)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            try
            {
                string password;
                reader.ReadValueSafe(out password); // never logged

                bool granted = CompetitiveAdjustments.AdminAuth.IsAllowed(senderId, password);
                if (granted)
                {
                    _authedClients.Add(senderId);
                    SendAdminAuthResult(senderId, true, "Unlocked.");
                }
                else
                {
                    _authedClients.Remove(senderId);
                    var admin = CompetitiveAdjustments.ConfigManager.Config?.Admin;
                    bool pwPathOn = admin != null && !string.IsNullOrEmpty(admin.EditorPassword);
                    string reason;
                    if (string.IsNullOrEmpty(password))
                        // Empty password is the auto-unlock probe (allowlist check),
                        // not a wrong-password attempt; keep the message neutral.
                        reason = pwPathOn ? "Enter the admin password to unlock." : "Not authorized (admin only).";
                    else
                        reason = "Incorrect password.";
                    SendAdminAuthResult(senderId, false, reason);
                }
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] OnAdminAuthMsg exception: {e}"); }
        }

        private static void OnAdminConfigSetMsg(ulong senderId, FastBufferReader reader)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            try
            {
                byte total, index;
                string chunk;
                reader.ReadValueSafe(out total);
                reader.ReadValueSafe(out index);
                reader.ReadValueSafe(out chunk);

                string json = _configSetRx.Feed(senderId, total, index, chunk);
                if (json == null) return; // awaiting more parts

                // Defense in depth: re-validate authority, never trust the client UI lock.
                if (!_authedClients.Contains(senderId))
                {
                    Debug.LogWarning($"[COMPADJUST] Rejected PPKB/AdminConfigSet from un-authed client {senderId}.");
                    SendAdminAuthResult(senderId, false, "Not authorized.");
                    return;
                }

                CompetitiveAdjustments.ConfigApplyService.ApplyServerConfigEdit(json);
                SendAdminAuthResult(senderId, true, "Applied.");
                CompetitiveAdjustments.ConfigManager.Log($"Admin config edit applied from client {senderId}.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] OnAdminConfigSetMsg exception: {e}");
                SendAdminAuthResult(senderId, false, "Apply failed.");
            }
        }

        // ---- Client-side handlers ------------------------------------------

        private static void OnAdminAuthResultMsg(ulong senderId, FastBufferReader reader)
        {
            if (!FromServer(senderId, "PPKB/AdminAuthResult")) return;
            try
            {
                bool granted;
                string reason;
                reader.ReadValueSafe(out granted);
                reader.ReadValueSafe(out reason);

                // Sticky-true: an explicit grant unlocks; a later non-grant (e.g.
                // a failed apply status) carries its reason but does not re-lock
                // an editor the server already trusts.
                if (granted) AdminUnlocked = true;
                AdminAuthReason = reason ?? "";
                OnAdminAuthResult?.Invoke(granted, AdminAuthReason);
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] OnAdminAuthResultMsg exception: {e}"); }
        }

        private static void OnConfigFullMsg(ulong senderId, FastBufferReader reader)
        {
            // Without this, any client could push a whole config document at the
            // server: LoadFromJson replaces the live Dashfall/CompAdjust/CompTweaks
            // sections and runs SyncFeatureStates when IsServer, bypassing both the
            // admin gate and the ClampRuntimeLimits that ApplyServerConfigEdit does.
            if (!FromServer(senderId, "PPKB/ConfigFull")) return;
            try
            {
                byte total, index;
                string chunk;
                reader.ReadValueSafe(out total);
                reader.ReadValueSafe(out index);
                reader.ReadValueSafe(out chunk);

                string json = _configFullRx.Feed(senderId, total, index, chunk);
                if (json == null) return; // awaiting more parts

                // Display/edit mirror only: LoadFromJson skips server-authoritative
                // apply hooks unless this process is the server.
                CompetitiveAdjustments.ConfigManager.LoadFromJson(json);
                HasReceivedFullConfig = true;
                OnFullConfigReceived?.Invoke();
            }
            catch (Exception e) { Debug.LogError($"[COMPADJUST] OnConfigFullMsg exception: {e}"); }
        }

        // Reassembles a [total][index][chunk] part sequence into the full string,
        // keyed by sender id so concurrent transfers from different peers (and
        // separate server/client receive directions) never cross-contaminate.
        private sealed class StringReassembler
        {
            private readonly Dictionary<ulong, string[]> _parts = new Dictionary<ulong, string[]>();
            private readonly Dictionary<ulong, int> _received = new Dictionary<ulong, int>();

            // Returns the full string once all parts have arrived, else null.
            public string Feed(ulong sender, byte total, byte index, string chunk)
            {
                if (total == 0 || index >= total) return null;

                _parts.TryGetValue(sender, out var arr);
                // A fresh transfer always starts at index 0; on that first chunk
                // drop any stale partial left over from an earlier interrupted
                // transfer (e.g. a retry of a send whose first chunks were missed)
                // so old and new chunks can never be merged.
                if (index == 0 || arr == null || arr.Length != total)
                {
                    arr = new string[total];
                    _parts[sender] = arr;
                    _received[sender] = 0;
                }

                if (arr[index] == null) _received[sender] = _received[sender] + 1;
                arr[index] = chunk ?? "";

                if (_received[sender] < total) return null;

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < total; i++) sb.Append(arr[i] ?? "");
                _parts.Remove(sender);
                _received.Remove(sender);
                return sb.ToString();
            }

            public void Clear(ulong sender) { _parts.Remove(sender); _received.Remove(sender); }
            public void ClearAll() { _parts.Clear(); _received.Clear(); }
        }

        // ---------- Message handlers ----------
        // Hard upper bound on the number of action names a single Hello can
        // declare. Real clients send well under a dozen; anything above this
        // is malformed or hostile, and we want to bail before allocating /
        // reading megabytes worth of strings.
        private const int MaxHelloActionCount = 256;

        private static void OnHello(ulong clientId, FastBufferReader reader)
        {
            try
            {
                int count = 0;
                reader.ReadValueSafe(out count);

                if (count < 0 || count > MaxHelloActionCount)
                {
                    Debug.LogWarning($"[COMPADJUST] OnHello from {clientId}: rejected action count {count} (limit {MaxHelloActionCount}).");
                    return;
                }

                var set = new HashSet<string>();
                for (int i = 0; i < count; i++)
                {
                    string a;
                    reader.ReadValueSafe(out a);
                    if (!string.IsNullOrEmpty(a)) set.Add(Canon(a));
                }

                // If the client re-sends the same declaration (common on a
                // spammy keybind hub), skip the SetEquals == true path: the
                // re-broadcast is pure CPU/bandwidth waste, and the spammy
                // case is exactly the one we want to deny amplification on.
                bool sameAsPrior = _declared.TryGetValue(clientId, out var prior)
                                   && prior != null
                                   && prior.SetEquals(set);
                if (sameAsPrior) return;

                _declared[clientId] = set;
                // Reset held set when new declaration arrives
                _held[clientId] = new HashSet<string>();

                // Send server features only to the client that said hello. Earlier
                // versions broadcast to every connected client on every Hello, which
                // produced N^2 traffic and was trivially amplifiable by a spammy
                // client. The hello sender is the only one that needs the state.
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    SendInitialStateToClient(clientId);
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] OnHello exception: {e}");
            }
        }

        private static void OnActionMsg(ulong clientId, FastBufferReader reader)
        {
            try
            {
                string action; 
                byte phase;
                reader.ReadValueSafe(out action);
                reader.ReadValueSafe(out phase);
                action = Canon(action);

                HashSet<string> held;
                if (!_held.TryGetValue(clientId, out held) || held == null) 
                { 
                    held = new HashSet<string>(); 
                    _held[clientId] = held; 
                }

                bool isDown = (phase == 0);
                if (isDown) 
                    held.Add(action); 
                else 
                    held.Remove(action);

                var evt = OnAction; 
                if (evt != null) 
                    evt(clientId, action, isDown);
            }
            catch (Exception e) 
            { 
                Debug.LogError($"[COMPADJUST] OnActionMsg exception: {e}");
            }
        }
    }
}
