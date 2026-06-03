using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Compat shim for ToasterPerformance
    /// (https://github.com/ckhawks/ToasterPerformance, mod GUID
    /// pw.stellaric.toaster.performance).
    ///
    /// Toaster prefixes Server_GatherSynchronizedObjectData and
    /// Client_SynchronizeObjects with full-replacement prefixes that return
    /// false and inline the position encode / decode as (short)(pos.x * 655f)
    /// / d.X / 655f.  When chunks are active (arena tweaks on, positions far
    /// from rink centre), this inlining bypasses our EncodeSynchronizedObject
    /// / DecodeSynchronizedObjectData prefixes -- so positions are sent and
    /// received through the vanilla 655 precision only, capping range at
    /// +/-50 m.  Any object past that range desyncs visually.
    ///
    /// Fix: register our own prefixes on the same two outer methods with
    /// Priority.First so we run BEFORE Toaster.  When chunks are inactive
    /// (vanilla rink), our prefix returns true and lets Toaster (or vanilla)
    /// handle the call -- Toaster's perf wins stay live.  When chunks are
    /// active, our prefix replaces the body with a chunk-aware version
    /// (calling ChunkRegistry helpers for position encode / decode) and
    /// returns false; Toaster's prefix doesn't get to run because Harmony
    /// stops the prefix chain on the first false-return.
    ///
    /// Lifecycle: patches are installed by Enable() from
    /// NetworkBoundsPatch.EnableOpenWorldPrecision and removed by Disable().
    /// While installed the prefixes self-gate on ChunkRegistry.ChunksActive,
    /// so they are inert whenever chunking is off even if Enable ran.
    ///
    /// Maintenance: the replacement bodies mirror vanilla
    /// SynchronizedObjectManager logic.  If vanilla's implementation changes
    /// (new fields, restructured smoothing branch), these replacements need
    /// to follow.  If a private field name ever changes, ResolveAccessors
    /// fails closed: the prefixes return true and hand the call back to
    /// Toaster / vanilla (range caps at +/-50 m but stays functional).
    /// </summary>
    internal static class SyncObjectChunkPatches
    {
        private const string HarmonyId = "compadjust.chunksync.toastercompat";

        private static Harmony _harmony;
        private static bool _enabled;

        // ── Reflection accessors for private SynchronizedObjectManager state ──
        // Cached at first call.  FieldRefAccess is faster than FieldInfo
        // (compiled IL accessor delegate) -- used on the per-tick hot path.
        private static AccessTools.FieldRef<SynchronizedObjectManager, List<SynchronizedObject>> _fSynchronizedObjects;
        private static AccessTools.FieldRef<SynchronizedObjectManager, double> _fServerLastSentServerTime;
        private static AccessTools.FieldRef<SynchronizedObjectManager, bool>   _fClientHasReceivedFirstTick;
        private static AccessTools.FieldRef<SynchronizedObjectManager, SortedList<double, SynchronizedObjectsSnapshot>> _fSnapshots;
        private static AccessTools.FieldRef<SynchronizedObjectManager, double> _fClientLocalTimeline;
        private static AccessTools.FieldRef<SynchronizedObjectManager, double> _fClientLocalTimescale;
        private static AccessTools.FieldRef<SynchronizedObjectManager, ExponentialMovingAverage> _fDriftEma;
        private static AccessTools.FieldRef<SynchronizedObjectManager, ExponentialMovingAverage> _fDeliveryTimeEma;
        private static FieldInfo _fSnapshotInterpolationSettings; // value-type, may need GetValue/SetValue

        private static bool _accessorsResolved;
        // Set once we've TRIED resolution (success or failure) -- avoids a
        // per-tick reflection storm if a private field name ever changes
        // in a vanilla update.  On failure we log once, then fall back
        // forever (return true from prefix -> Toaster / vanilla handles it
        // without chunking, capping range at +/-50 m but staying functional).
        private static bool _resolveAttempted;

        // Reusable staging buffer -- server gather runs every tick.  Toaster
        // reuses its own buffer for the same reason.  Not thread-safe, but
        // Unity physics + RPC dispatch are single-threaded so we're fine.
        private static readonly List<SynchronizedObjectData> _gatherStaging = new List<SynchronizedObjectData>(64);

        // ──────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────

        public static void Enable()
        {
            if (_enabled) return;

            try
            {
                if (_harmony == null) _harmony = new Harmony(HarmonyId);

                var gather = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
                if (gather != null)
                    _harmony.Patch(gather, prefix: new HarmonyMethod(typeof(SyncObjectChunkPatches), nameof(ServerGatherPrefix)) { priority = Priority.First });
                else
                    Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches: Server_GatherSynchronizedObjectData not found -- ToasterPerformance server compat disabled.");

                var clientSync = AccessTools.Method(typeof(SynchronizedObjectManager), "Client_SynchronizeObjects");
                if (clientSync != null)
                    _harmony.Patch(clientSync, prefix: new HarmonyMethod(typeof(SyncObjectChunkPatches), nameof(ClientSyncPrefix)) { priority = Priority.First });
                else
                    Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches: Client_SynchronizeObjects not found -- ToasterPerformance client compat disabled.");

                _enabled = true;
                CompetitiveAdjustments.ConfigManager.Log("SyncObjectChunkPatches enabled (ToasterPerformance chunk-aware encode/decode compat, Priority.First).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches patch install failed: " + ex.Message);
            }
        }

        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }
        }

        private static bool ResolveAccessors()
        {
            if (_accessorsResolved) return true;
            if (_resolveAttempted) return false; // tried once, failed -- don't retry
            _resolveAttempted = true;
            try
            {
                var t = typeof(SynchronizedObjectManager);
                _fSynchronizedObjects           = AccessTools.FieldRefAccess<SynchronizedObjectManager, List<SynchronizedObject>>("synchronizedObjects");
                _fServerLastSentServerTime      = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("serverLastSentServerTime");
                _fClientHasReceivedFirstTick    = AccessTools.FieldRefAccess<SynchronizedObjectManager, bool>("clientHasReceivedFirstTick");
                _fSnapshots                     = AccessTools.FieldRefAccess<SynchronizedObjectManager, SortedList<double, SynchronizedObjectsSnapshot>>("snapshots");
                _fClientLocalTimeline           = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("clientLocalTimeline");
                _fClientLocalTimescale          = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("clientLocalTimescale");
                _fDriftEma                      = AccessTools.FieldRefAccess<SynchronizedObjectManager, ExponentialMovingAverage>("driftEma");
                _fDeliveryTimeEma               = AccessTools.FieldRefAccess<SynchronizedObjectManager, ExponentialMovingAverage>("deliveryTimeEma");
                _fSnapshotInterpolationSettings = AccessTools.Field(t, "snapshotInterpolationSettings");
                _accessorsResolved = _fSynchronizedObjects != null
                                  && _fServerLastSentServerTime != null
                                  && _fClientHasReceivedFirstTick != null
                                  && _fSnapshots != null
                                  && _fClientLocalTimeline != null
                                  && _fClientLocalTimescale != null
                                  && _fDriftEma != null
                                  && _fDeliveryTimeEma != null
                                  && _fSnapshotInterpolationSettings != null;
                if (!_accessorsResolved)
                    Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches: failed to resolve one or more private fields -- ToasterPerformance compat disabled.");
                return _accessorsResolved;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] SyncObjectChunkPatches accessor resolution failed: " + ex.Message);
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Server gather: chunked encode.
        // Priority.First so we run before ToasterPerformance's prefix.  We
        // only "claim" the call (return false, replacing body) when chunks
        // are active; otherwise return true and let Toaster / vanilla run.
        //
        // IMPORTANT: returning false skips ALL other prefixes on this method,
        // including ChunkSyncServer.GatherPrefix which would normally set
        // ChunkRegistry.CurrentEncodeTickId and run the hysteresis sweep.  We
        // MUST invoke it manually before doing the chunked encode, otherwise
        // the encode reads a stale tickId and objects don't get their chunk
        // handoffs.
        // ──────────────────────────────────────────────────────────────────
        public static bool ServerGatherPrefix(SynchronizedObjectManager __instance, bool forceAllObjects, ref SynchronizedObjectData[] __result)
        {
            if (!ChunkRegistry.ChunksActive) return true; // hand off to Toaster / vanilla
            if (!ResolveAccessors()) return true;

            // CRITICAL: invoke ChunkSyncServer.GatherPrefix manually -- its
            // tickId stash + hysteresis sweep would otherwise be skipped by
            // our return false below (Harmony stops the prefix chain on first
            // false-return).  Without this call, the chunked encode below
            // reads a stale CurrentEncodeTickId and objects never hand off to
            // a new chunk when they cross thresholds.
            try { ChunkSyncServer.GatherPrefix(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] manual ChunkSyncServer.GatherPrefix failed: " + ex.Message);
            }

            var list = _fSynchronizedObjects(__instance);
            int tickRate = __instance.TickRate;
            double serverTimeNow = NetworkManager.Singleton.ServerTime.Time;
            float serverDeltaTime = (float)(serverTimeNow - _fServerLastSentServerTime(__instance));

            var staging = _gatherStaging;
            staging.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var so = list[i];
                if (so == null) continue;
                if (!forceAllObjects && !so.ShouldSendPosition(tickRate) && !so.ShouldSendRotation(tickRate))
                    continue;

                var tuple = so.OnServerTick(serverDeltaTime);
                Vector3 pos = tuple.Item1;
                Quaternion rot = tuple.Item2;
                ulong networkObjectId = tuple.Item3;
                ushort id = (ushort)networkObjectId;

                // Defensive: if a SO is present in vanilla's
                // synchronizedObjects list but missing from our slot table
                // (one-frame race between the spawn event reaching
                // SynchronizedObjectManagerController vs. ChunkSyncServer, or a
                // SO that joined the list via a non-event path), the encode
                // below would fall back to offset=0 and overflow the short on
                // any world position past +/-50 m.  Initialize the slot here
                // so the very first encode uses a correct chunk offset.
                if (!ChunkRegistry.TryGet(id, out _))
                    ChunkSyncServer.InitializeSlotFor(so);

                // CHUNK-AWARE position encode -- applies the per-object chunk
                // offset before the * 655 multiply, expanding range to +/-4 km
                // at vanilla 1.5 mm precision.
                staging.Add(new SynchronizedObjectData
                {
                    NetworkObjectId = id,
                    X = ChunkRegistry.EncodeX(pos.x, id),
                    Y = ChunkRegistry.EncodeY(pos.y),
                    Z = ChunkRegistry.EncodeZ(pos.z, id),
                    Rx = (short)(rot.x * 32767f),
                    Ry = (short)(rot.y * 32767f),
                    Rz = (short)(rot.z * 32767f),
                    Rw = (short)(rot.w * 32767f),
                });
            }
            __result = staging.ToArray();
            return false; // skip Toaster's prefix + vanilla
        }

        // ──────────────────────────────────────────────────────────────────
        // Client sync: chunked decode.
        // ──────────────────────────────────────────────────────────────────
        public static bool ClientSyncPrefix(SynchronizedObjectManager __instance,
            SynchronizedObjectData[] synchronizedObjectsData,
            float serverDeltaTime,
            double serverTime)
        {
            if (!ChunkRegistry.ChunksActive) return true; // hand off to Toaster / vanilla
            if (synchronizedObjectsData == null) return false;
            if (!ResolveAccessors()) return true;

            var list = _fSynchronizedObjects(__instance);

            if (__instance.UseNetworkSmoothing && _fClientHasReceivedFirstTick(__instance))
            {
                var snapshotList = new List<SynchronizedObjectSnapshot>(synchronizedObjectsData.Length);
                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                {
                    ref var d = ref synchronizedObjectsData[i];
                    DecodeChunked(in d, out Vector3 pos, out Quaternion rot);
                    var so = FindByNetworkObjectId(list, d.NetworkObjectId);
                    if (so == null) continue;
                    snapshotList.Add(so.OnClientSmoothTick(pos, rot, so, serverDeltaTime));
                }
                var snapshot = new SynchronizedObjectsSnapshot(serverTime, NetworkManager.Singleton.LocalTime.Time, snapshotList);

                // Interpolation settings + snapshot-buffer adjust.
                // snapshotInterpolationSettings is a struct-or-class field;
                // access via FieldInfo (assignment goes through SetValue if
                // it's a struct, which is the worst case).
                var settings = (SnapshotInterpolationSettings)_fSnapshotInterpolationSettings.GetValue(__instance);
                if (settings.dynamicAdjustment)
                {
                    settings.bufferTimeMultiplier = SnapshotInterpolation.DynamicAdjustment(
                        __instance.TickInterval,
                        _fDeliveryTimeEma(__instance).StandardDeviation,
                        settings.dynamicAdjustmentTolerance) * __instance.NetworkSmoothingStrength;
                    _fSnapshotInterpolationSettings.SetValue(__instance, settings);
                }

                double bufferTime = __instance.TickInterval * settings.bufferTimeMultiplier;
                SnapshotInterpolation.InsertAndAdjust(
                    _fSnapshots(__instance),
                    settings.bufferLimit,
                    snapshot,
                    ref _fClientLocalTimeline(__instance),
                    ref _fClientLocalTimescale(__instance),
                    __instance.TickInterval,
                    bufferTime,
                    settings.catchupSpeed,
                    settings.slowdownSpeed,
                    ref _fDriftEma(__instance),
                    settings.catchupNegativeThreshold,
                    settings.catchupPositiveThreshold,
                    ref _fDeliveryTimeEma(__instance));
            }
            else
            {
                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                {
                    ref var d = ref synchronizedObjectsData[i];
                    DecodeChunked(in d, out Vector3 pos, out Quaternion rot);
                    var so = FindByNetworkObjectId(list, d.NetworkObjectId);
                    if (so == null) continue;
                    so.OnClientTick(pos, rot, serverDeltaTime);
                }
            }
            return false; // skip Toaster's prefix + vanilla
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static void DecodeChunked(in SynchronizedObjectData d, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(
                ChunkRegistry.DecodeX(d.X, d.NetworkObjectId),
                ChunkRegistry.DecodeY(d.Y),
                ChunkRegistry.DecodeZ(d.Z, d.NetworkObjectId));
            rot = new Quaternion(
                d.Rx / 32767f,
                d.Ry / 32767f,
                d.Rz / 32767f,
                d.Rw / 32767f);
        }

        // O(N) lookup matching vanilla's List.Find pattern.  Toaster maintains
        // an O(1) dict that we don't have access to here; that's fine because
        // this fallback only runs when chunks are active (arena tweaks on),
        // where Toaster's optimization is being bypassed anyway.  N is at most
        // ~total synchronized objects (~32 in a typical match) so the linear
        // scan is fast.
        private static SynchronizedObject FindByNetworkObjectId(List<SynchronizedObject> list, ushort id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var so = list[i];
                if (so != null && so.NetworkObjectId == id) return so;
            }
            return null;
        }
    }
}
