using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// The predictor seed channel for wrapped positions.
    ///
    /// WHAT THIS CARRIES, AND WHY IT IS NOT A CHUNK INDEX
    /// It carries a float32 true world position per object. That is the same 14 bytes a
    /// (id, chunk index) pair would cost, but it is a fundamentally safer thing to send,
    /// because it is a PREDICTOR SEED rather than a DECODE KEY:
    ///
    ///   A decode key must be bound to a specific record. If it arrives late, early, or
    ///   for the wrong tick, the record decodes wrongly and there is no way to notice.
    ///   Anchor freshness, LOD divisor staleness and retransmit ordering all become
    ///   correctness cliffs.
    ///
    ///   A seed only has to be CLOSE. The unwrap tolerates half a period of predictor
    ///   error, so a seed that is a few ticks stale is still an excellent predictor.
    ///   Every ordering question degrades from "wrong position" to "slightly older
    ///   starting point", which the margin absorbs.
    ///
    /// It also does something no chunk index can: it repairs a settled error. Once a
    /// predictor is a whole period out, every later unwrap reproduces that same offset
    /// exactly, and nothing in the arriving data reveals it. Overwriting the predictor
    /// with an authoritative world position is the only thing that closes that hole, and
    /// it is why the residual-based detector that an earlier design used was dropped: it
    /// was measured to be not merely weak but actively harmful, rejecting correct data at
    /// large honest predictor error while missing a large fraction of real mis-unwraps.
    ///
    /// WHEN IT IS SENT
    /// A seed rides every reliable batch. Reliable records are exactly the periodic full
    /// sends, the sleep transitions, and the join seed, so every awake object is re-anchored
    /// at least once per second and a sleeping object gets one correct seed when it parks
    /// and needs no more. At 40 objects that is roughly 560 B/s per client.
    ///
    /// DELIVERY
    /// ReliableFragmentedSequenced. It is the only delivery that lifts the size ceiling
    /// from NonFragmentedMessageMaxSize to the fragmented limit, and it shares the pipeline
    /// with the reliable object RPC, which is what gives the seed and the batch it
    /// describes a defined order rather than a race.
    /// </summary>
    /// <summary>
    /// Returns false for an id whose true world position is not known, so that seed is
    /// omitted entirely rather than sent as the zeroed table slot. A wrong seed is worse
    /// than a missing one: it marks the object seeded and pins it near the origin.
    /// </summary>
    internal delegate bool TryWorld(ushort id, out Vector3 world);

    internal static class WrapSyncSeed
    {
        /// <summary>Seed payloads dropped because they did not come from the server.</summary>
        public static int SeedRejectedSender;
        /// <summary>Seeds omitted because the server had no true world position for the id.</summary>
        public static int SeedSkippedUnknown;

        private static readonly List<KeyValuePair<ushort, Vector3>> _resolved =
            new List<KeyValuePair<ushort, Vector3>>();

        public const string MessageName = "PPKB/PosSeed";

        /// <summary>
        /// Payload version. Bumped whenever the layout changes so a mismatched peer is
        /// rejected loudly instead of misparsing.
        /// </summary>
        private const byte PayloadVersion = 1;

        /// <summary>
        /// Identifies the exact protocol both ends must agree on. Derived from the wrap
        /// periods and the marker bit, so a build with different constants is refused
        /// rather than silently decoding every position a whole period out. Positions are
        /// the one thing where disagreeing quietly is worse than not working.
        /// </summary>
        public static uint Fingerprint()
        {
            unchecked
            {
                uint h = 2166136261u;
                void Mix(float f)
                {
                    uint bits = (uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
                    for (int i = 0; i < 4; i++) { h ^= (bits >> (i * 8)) & 0xFF; h *= 16777619u; }
                }
                Mix(WrapSync.PeriodX);
                Mix(WrapSync.PeriodY);
                Mix(WrapSync.PeriodZ);
                Mix(WrapSync.JumpGuardX);
                Mix(WrapSync.JumpGuardYZ);
                h ^= WrapSync.MaskWrapped; h *= 16777619u;
                h ^= PayloadVersion; h *= 16777619u;
                return h;
            }
        }

        private static uint _peerFingerprintMismatchLogged;

        // ── Send (server) ─────────────────────────────────────────────────────

        /// <summary>
        /// Ids queued to be seeded on the next reliable flush. A set rather than a list so
        /// an object that is queued twice in one tick costs one entry.
        /// </summary>
        private static readonly HashSet<ushort> _pending = new HashSet<ushort>();

        public static void QueueSeed(ushort id) => _pending.Add(id);
        public static int PendingCount => _pending.Count;
        public static void ClearPending() => _pending.Clear();

        /// <summary>
        /// Flush the queued seeds to one client. Called from the reliable RPC prefix so the
        /// seed is delivered ahead of the batch it describes, on the same pipeline.
        ///
        /// Never sends to the server's own client id. On a listen host the named-message
        /// send short-circuits straight into the receive handler synchronously, which would
        /// re-enter this subsystem from inside a send. The host is never a sync target in
        /// the first place, so there is nothing to seed.
        /// </summary>
        public static void Flush(ulong clientId, TryWorld worldOf)
        {
            if (_pending.Count == 0) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (clientId == NetworkManager.ServerClientId) return;

            var cmm = nm.CustomMessagingManager;
            if (cmm == null) return;

            // Resolve BEFORE sizing, because the count is written ahead of the entries and
            // an id skipped inside the loop would leave the reader expecting a record that
            // is not there. Every subsequent field would then be read from the wrong offset.
            _resolved.Clear();
            foreach (var id in _pending)
            {
                if (!worldOf(id, out Vector3 p)) { SeedSkippedUnknown++; continue; }
                _resolved.Add(new KeyValuePair<ushort, Vector3>(id, p));
            }
            if (_resolved.Count == 0) { _pending.Clear(); return; }

            int count = _resolved.Count;
            // 1 version + 4 fingerprint + 2 count + 14 per entry.
            int size = 1 + 4 + 2 + count * 14;

            FastBufferWriter w = default;
            try
            {
                w = new FastBufferWriter(size, Allocator.Temp);
                w.WriteValueSafe(PayloadVersion);
                w.WriteValueSafe(Fingerprint());
                w.WriteValueSafe((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    ushort id = _resolved[i].Key;
                    Vector3 p = _resolved[i].Value;
                    w.WriteValueSafe(id);
                    w.WriteValueSafe(p.x);
                    w.WriteValueSafe(p.y);
                    w.WriteValueSafe(p.z);
                }

                // SendPreSerializedMessage indexes the send queue by client id and throws
                // for a client that dropped mid-tick. A throw here would propagate into the
                // server tick, which OnAfterSimulate swallows, silently killing object sync
                // for everyone.
                cmm.SendNamedMessage(MessageName, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
                WrapSync.TxSeeds += count;
            }
            catch (Exception e)
            {
                CompetitiveAdjustments.ConfigManager.Dbg($"PosSeed flush to {clientId} failed: {e.Message}");
            }
            finally
            {
                if (w.IsInitialized) w.Dispose();
            }
        }

        // ── Receive (client) ──────────────────────────────────────────────────

        public static void OnSeedMsg(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                // FIRST statement, before a single byte is read. This handler is registered
                // on every role, so on a listen host a client can address it directly. It was
                // the only PPKB handler without a sender check, and a forged seed is not a
                // nuisance: the predictor is what chooses the unwrap multiple, so writing it
                // lets any peer displace any object by whole periods.
                if (senderClientId != NetworkManager.ServerClientId)
                {
                    SeedRejectedSender++;
                    return;
                }

                if (reader.Length - reader.Position < 7) return;

                reader.ReadValueSafe(out byte version);
                reader.ReadValueSafe(out uint fingerprint);
                reader.ReadValueSafe(out ushort count);

                if (version != PayloadVersion || fingerprint != Fingerprint())
                {
                    // Do not apply anything. A peer running different wrap constants would
                    // seed us with positions that mean something else.
                    if (_peerFingerprintMismatchLogged != fingerprint)
                    {
                        _peerFingerprintMismatchLogged = fingerprint;
                        Debug.LogWarning(
                            $"[COMPADJUST] PosSeed from {senderClientId} has version {version} fingerprint {fingerprint:X8}, "
                            + $"we are version {PayloadVersion} fingerprint {Fingerprint():X8}. Ignoring. "
                            + "The two builds disagree on the wrapped-position protocol and must match.");
                    }
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    // Bounds-check every entry. A truncated payload must stop the loop, not
                    // read past the end.
                    if (reader.Length - reader.Position < 14) return;
                    reader.ReadValueSafe(out ushort id);
                    reader.ReadValueSafe(out float x);
                    reader.ReadValueSafe(out float y);
                    reader.ReadValueSafe(out float z);
                    // BEFORE applying: the gap between the server's truth and what we would
                    // have unwrapped against is the live safety margin.
                    var seedWorld = new Vector3(x, y, z);
                    WrapSync.NoteSeedResidual(id, seedWorld);
                    WrapSync.ApplySeed(id, seedWorld);
                    WrapSync.RxSeeds++;
                }
            }
            catch (Exception e)
            {
                CompetitiveAdjustments.ConfigManager.Dbg("PosSeed parse failed: " + e.Message);
            }
        }
    }
}
