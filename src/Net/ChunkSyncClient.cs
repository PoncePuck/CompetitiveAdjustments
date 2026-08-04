using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Client-side half of the chunked-position sync system.
    ///
    /// Responsibilities:
    ///   1. Receive OWPMOD/Chunks reliable messages and apply them to the
    ///      <see cref="ChunkRegistry"/>.  Single updates carry a future
    ///      switch tickId so the offset doesn't take effect mid-handoff;
    ///      bulk snapshots (sent on late join) carry the instant-apply
    ///      sentinel and overwrite Current directly.
    ///   2. Stash the incoming RPC's tickId on <see cref="ChunkRegistry.CurrentDecodeTickId"/>
    ///      before the original RPC body runs the decode loop.
    ///   3. Reject-filter incoming positions whose decoded delta from the
    ///      last-known position exceeds the chunk size.  Safety net for the
    ///      rare cross-channel race where an unreliable position packet
    ///      beats its reliable chunk announce to the client.
    /// </summary>
    public static class ChunkSyncClient
    {
        private const float RejectThresholdMeters = 16f;

        // Stop dropping after this many in a row so a real teleport (or a
        // dropped announce that never recovers) doesn't freeze an object
        // forever.  Was 5 (~50 ms @ 100 Hz) but that wasn't enough headroom
        // on dedicated-server connections with ~100-200 ms RTT plus jitter:
        // the cross-channel race window between an unreliable position packet
        // beating its reliable chunk announce regularly exceeded 50 ms, the
        // filter accepted the wrong-chunk decode, and the visual snapped by
        // 32 m until the next correct packet.  20 ticks (~200 ms) covers
        // typical dedicated-server RTT plus a couple retransmits while still
        // being well under the human-noticeable freeze window on a legitimate
        // teleport.
        private const int MaxConsecutiveDrops = 20;

        private const string HarmonyId = "compadjust.chunksync.client";

        private static Harmony _harmony;
        private static bool _enabled;
        private static bool _cmmRegistered;

        private struct FilterState
        {
            public Vector3 LastDecoded;
            public int     ConsecutiveDrops;
            public bool    Initialized;
        }
        private static readonly Dictionary<ushort, FilterState> _filter
            = new Dictionary<ushort, FilterState>();

        /// <summary>
        /// Idempotent. First call installs Harmony patches and attempts to
        /// register the CMM handler / request a bulk snapshot. Subsequent
        /// calls skip the patch install (already done) but still retry the
        /// CMM registration + snapshot request if those failed earlier --
        /// otherwise a single CMM-was-null call would leave us permanently
        /// without late-join snapshots until the next session teardown.
        ///
        /// Note: the only normal caller is NetworkBoundsPatch.EnableOpenWorldPrecision,
        /// which is itself gated by ChunksActive. After the first successful
        /// Enable that gate stays true, so the retry block here would never
        /// fire on its own -- TickRegistrationRetry() must be polled to
        /// actually drive the retry.
        /// </summary>
        public static void Enable()
        {
            bool firstCall = !_enabled;
            if (firstCall)
            {
                _enabled = true;
                _filter.Clear();

                try
                {
                    if (_harmony == null) _harmony = new Harmony(HarmonyId);

                    var rpc = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
                    if (rpc != null)
                        _harmony.Patch(rpc, prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(RpcPrefix)));
                    else
                        Debug.LogWarning("[COMPADJUST] ChunkSyncClient: Server_SynchronizeObjectsRpc not found -- tickId stash disabled.");

                    var tick   = AccessTools.Method(typeof(SynchronizedObject), "OnClientTick");
                    var smooth = AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick");
                    if (tick != null)
                        _harmony.Patch(tick,   prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(OnClientTickPrefix)));
                    if (smooth != null)
                        _harmony.Patch(smooth, prefix: new HarmonyMethod(typeof(ChunkSyncClient), nameof(OnClientSmoothTickPrefix)));

                    EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[COMPADJUST] ChunkSyncClient patch install failed: " + ex.Message);
                    return;
                }
            }

            // Always retry registration + snapshot if they didn't take last
            // time. RegisterCmmHandler / RequestBulkSnapshot are themselves
            // null-safe and short-circuit when CMM is still unavailable.
            if (!_cmmRegistered)
            {
                RegisterCmmHandler();
                if (_cmmRegistered)
                    RequestBulkSnapshot();
            }

            if (firstCall)
            {
                CompetitiveAdjustments.ConfigManager.Log("ChunkSyncClient enabled (filter threshold " + RejectThresholdMeters
                          + " m, max " + MaxConsecutiveDrops + " consecutive drops).");
            }
        }

        /// <summary>
        /// Cheap poll used by an external ticker (GoalNetTweaks.Runner). Retries
        /// CMM registration when the first Enable() call landed before
        /// NetworkManager.CustomMessagingManager was ready. No-op once
        /// registered, or when chunked sync is disabled.
        /// </summary>
        public static void TickRegistrationRetry()
        {
            if (!_enabled) return;
            if (_cmmRegistered) return;
            RegisterCmmHandler();
            if (_cmmRegistered)
                RequestBulkSnapshot();
        }

        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;
            _filter.Clear();

            EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", OnDespawnedEvent);

            UnregisterCmmHandler();

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[COMPADJUST] ChunkSyncClient UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }
        }

        private static void RegisterCmmHandler()
        {
            if (_cmmRegistered) return;
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm == null) return;
            try
            {
                cmm.RegisterNamedMessageHandler(ChunkSyncServer.CmmName, OnChunkMessage);
                _cmmRegistered = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] ChunkSyncClient: failed to register CMM handler: " + ex.Message);
            }
        }

        private static void UnregisterCmmHandler()
        {
            if (!_cmmRegistered) return;
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm != null)
            {
                try { cmm.UnregisterNamedMessageHandler(ChunkSyncServer.CmmName); }
                catch { }
            }
            _cmmRegistered = false;
        }

        private static void OnDespawnedEvent(Dictionary<string, object> msg)
        {
            if (!(msg["synchronizedObject"] is SynchronizedObject obj)) return;
            ushort id = (ushort)obj.NetworkObjectId;
            _filter.Remove(id);
            // Mirror ChunkSyncServer's despawn cleanup so a recycled ushort id
            // (NetworkObjectId is ulong but we narrow it for the slot key)
            // doesn't inherit a stale chunk offset from the previous occupant.
            // On a pure client ChunkSyncServer never runs, so without this the
            // registry slot lingers: the first packet for the new object
            // decodes against the old chunk and the reject-filter "no slot"
            // branch never engages (slot lookup succeeds with stale data
            // instead of missing).
            ChunkRegistry.Remove(id);
        }

        public static void OnChunkMessage(ulong senderId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out byte type);

                var nm = NetworkManager.Singleton;
                bool isServer = nm != null && nm.IsServer;

                if (isServer)
                {
                    // Server-side: only honour bulk requests (type 2).  Position
                    // announces (types 0/1) only flow server -> client; ignore
                    // anything else that loops back on a host.
                    if (type == 2)
                        ChunkSyncServer.SendBulkTo(senderId);
                    return;
                }

                if (type == 0)
                {
                    ReadSingle(reader);
                }
                else if (type == 1)
                {
                    reader.ReadValueSafe(out ushort count);
                    for (int i = 0; i < count; i++)
                        ReadSingle(reader);
                    // "entries", not "slots": the server sends a slot that is mid-switch as
                    // two entries (Current, then the deferred Pending), so this count is
                    // >= the slot count and reading it as slots overstates the table.
                    CompetitiveAdjustments.ConfigManager.Log("ChunkSyncClient: applied bulk snapshot (" + count + " entries).");
                }
                else
                {
                    Debug.LogWarning("[COMPADJUST] ChunkSyncClient: unknown OWPMOD/Chunks type " + type + ".");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] ChunkSyncClient.OnChunkMessage decode failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Ask the server to ship its current slot table to us.  Sent right after
        /// our CMM handler is registered so we don't miss the server's
        /// scene-sync-complete broadcast (which can arrive before our handler is
        /// up).  Without this, stationary objects that spawned before we joined
        /// never receive a chunk announce -- hysteresis only fires on movement
        /// -- and the reject filter pins them to their spawn transform.
        /// </summary>
        private static void RequestBulkSnapshot()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            if (nm.IsServer) return; // server already has every slot locally
            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            try
            {
                var writer = new FastBufferWriter(1, Allocator.Temp, 16);
                try
                {
                    writer.WriteValueSafe((byte)2); // request bulk
                    cmm.SendNamedMessage(ChunkSyncServer.CmmName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
                }
                finally { writer.Dispose(); }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] ChunkSyncClient: failed to request bulk snapshot: " + ex.Message);
            }
        }

        private static void ReadSingle(FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out sbyte  cx);
            reader.ReadValueSafe(out sbyte  cz);
            reader.ReadValueSafe(out ushort switchTick);
            var chunk = new ChunkCoord(cx, cz);
            ChunkRegistry.ApplyAnnounce(id, chunk, switchTick);
            SeedFilterBaseline(id, chunk);
        }

        private static void SeedFilterBaseline(ushort id, ChunkCoord chunk)
        {
            if (_filter.TryGetValue(id, out var s) && s.Initialized) return;
            s.LastDecoded = new Vector3(
                chunk.X * ChunkRegistry.ChunkSizeMeters,
                0f,
                chunk.Z * ChunkRegistry.ChunkSizeMeters);
            s.Initialized = true;
            s.ConsecutiveDrops = 0;
            _filter[id] = s;
        }

        public static void RpcPrefix(ushort tickId)
        {
            ChunkRegistry.CurrentDecodeTickId = tickId;
            // Promote any due Pending before the original RPC body decodes
            // positions. Otherwise a stale Pending whose tick wrapped past
            // ~32k ticks flips ResolveAt back to the old Current, mis-decoding
            // positions for stationary objects until the next announce arrives.
            ChunkRegistry.PromoteAllIfDue(tickId);
        }

        public static void OnClientTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            if (!_enabled || __instance == null) return;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;

            FilterAndPossiblyReplace(__instance, ref position);
        }

        public static void OnClientSmoothTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            if (!_enabled || __instance == null) return;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;

            FilterAndPossiblyReplace(__instance, ref position);
        }

        private static void FilterAndPossiblyReplace(SynchronizedObject obj, ref Vector3 position)
        {
            ushort id = (ushort)obj.NetworkObjectId;

            if (ChunkRegistry.ChunksActive && !ChunkRegistry.TryGet(id, out _))
            {
                position = obj.transform.position;
                return;
            }

            _filter.TryGetValue(id, out var s);

            if (!s.Initialized)
            {
                s.LastDecoded = position;
                s.Initialized = true;
                s.ConsecutiveDrops = 0;
                _filter[id] = s;
                return;
            }

            float dx = Mathf.Abs(position.x - s.LastDecoded.x);
            float dz = Mathf.Abs(position.z - s.LastDecoded.z);

            if ((dx > RejectThresholdMeters || dz > RejectThresholdMeters)
                && s.ConsecutiveDrops < MaxConsecutiveDrops)
            {
                position = s.LastDecoded;
                s.ConsecutiveDrops++;
                _filter[id] = s;
                return;
            }

            s.LastDecoded = position;
            s.ConsecutiveDrops = 0;
            _filter[id] = s;
        }
    }
}
