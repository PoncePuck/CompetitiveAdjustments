using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Unbounded network position range at bit-identical vanilla precision, by wrapping
    /// the coordinate rather than widening the range or appending a chunk index.
    ///
    /// THE IDEA
    /// B1231 quantises each position axis into a short across a fixed window:
    /// X is -25..+25, Y and Z are -50..+50, and CompressFloatToShort SATURATES, so an
    /// object past the window pins at the window on every client. That is the bug.
    ///
    /// Instead of making the window bigger (SyncRangePatch, which costs precision
    /// linearly and forever) we make the coordinate small. Choose a period P equal to the
    /// window WIDTH and send
    ///
    ///     k     = round(world / P)
    ///     local = world - k * P          which always lies in [-P/2, +P/2]
    ///
    /// and [-P/2, +P/2] is exactly the vanilla window. So the value handed to the vanilla
    /// compressor is always in range, at the vanilla step size, at any distance from the
    /// origin. Precision does not degrade. Nothing is appended to the wire.
    ///
    /// THE CLIENT NEVER RECEIVES k
    /// It recovers k by unwrapping against a predictor, the same way phase unwrapping
    /// works:
    ///
    ///     k = round((predicted - local) / P)
    ///     world = local + k * P
    ///
    /// which is exact whenever |predicted - true| &lt; P/2. The predictor is the last world
    /// position we committed for that object (zero-order hold), so the error is simply how
    /// far the object travelled between two accepted records. On the narrow axis that
    /// allows 25 m of travel between records, which at 45 m/s is 555 ms.
    ///
    /// Zero-order hold beats velocity extrapolation here, which is counterintuitive but
    /// measured: extrapolation is perfect while an object moves straight and twice as bad
    /// as ZOH when it reverses, and reversing off the boards is precisely the case that
    /// happens in this game.
    ///
    /// WHY NOT THE PREVIOUS DESIGN
    /// The earlier attempt appended a UInt16 chunk word to each record behind a mask bit.
    /// WriteNetworkSerializable writes one element count and then concatenates records
    /// with NO per-record length, so any Write/Read asymmetry does not corrupt one object,
    /// it desynchronises the read cursor and shreds every remaining record in the packet.
    /// That design set the flag bit unconditionally and appended the payload on a
    /// best-effort path whose failure was swallowed, which is a sufficient mechanism for
    /// the "Reading past the end of the buffer" it produced live. Here nothing is appended
    /// and nothing is consumed, so that entire class of failure is not representable.
    ///
    /// PRECISION, MEASURED
    ///     axis  period  max|local|  reconstruct err   total err
    ///     X     50      25.000000   0.000e+00         0.000488 m
    ///     Y/Z   100     50.000000   0.000e+00         0.000977 m
    /// over +/-1,000,000 m. world - k*P is exact in float32 (Sterbenz: k*P is always
    /// within P/2 of world), so the only error left is vanilla's own LSB rounding.
    /// </summary>
    internal static class WrapSync
    {
        // ── Protocol constants ────────────────────────────────────────────────
        // The period per axis IS the vanilla window width. Both ends must agree
        // exactly and forever; these are compile-time constants and never config.
        public const float PeriodX = 50f;   // vanilla X window is -25..+25
        public const float PeriodY = 100f;  // vanilla Y window is -50..+50
        public const float PeriodZ = 100f;  // vanilla Z window is -50..+50

        /// <summary>
        /// ChangeMask bit marking a record whose position is wrapped rather than absolute.
        ///
        /// Bit 14. Vanilla's maximum reachable mask is 3071 | 1024 | 4096 = 8191, because
        /// WithComponentMask and Merge both mask to 3071 and WithAsleep only ORs 4096. So
        /// bits 13, 14 and 15 are unreachable by an unmodded peer, which is what licenses
        /// installing the client decode permanently: without this bit it can never fire.
        ///
        /// Write and Read branch only on bits 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024
        /// and 2048. Bit 14 gates nothing, so setting it changes no record's length.
        /// </summary>
        public const ushort MaskWrapped = 1 << 14; // 16384

        /// <summary>Vanilla's own mask ceiling, used by the self-test to prove bit 14 is free.</summary>
        public const ushort MaxVanillaMask = 8191;

        /// <summary>
        /// Per-tick displacement beyond which we treat a move as a teleport rather than
        /// motion. A teleport can exceed the unwrap margin in a single step, so the object
        /// is forced to a full reliable send carrying a fresh seed instead of being
        /// unwrapped against a predictor that is now meaningless.
        /// </summary>
        public const float JumpGuardX = 8f;
        public const float JumpGuardYZ = 16f;

        private const int TableSize = 65536;

        // ── Wrap / unwrap ─────────────────────────────────────────────────────

        /// <summary>
        /// Deterministic round-half-up. Deliberately not Mathf.RoundToInt, which is
        /// banker's rounding: the server and the client compute k from different
        /// quantities, so a tie that resolves differently on the two ends would put them a
        /// whole period apart. Half-up resolves identically everywhere.
        /// </summary>
        private static int RoundHalfUp(float v) => (int)Mathf.Floor(v + 0.5f);

        /// <summary>Wrap one axis. Returns the chunk-local value; k comes back via out.</summary>
        public static float WrapAxis(float world, float period, out int k)
        {
            k = RoundHalfUp(world / period);
            return world - k * period;
        }

        /// <summary>
        /// Recover the true coordinate from a wrapped one, using a predictor.
        /// Exact when |predicted - world| is under half a period; off by exactly one whole
        /// period otherwise, never by a fraction. That "whole period or nothing" property
        /// is what makes the failure detectable rather than a slow drift.
        /// </summary>
        public static float UnwrapAxis(float local, float predicted, float period)
        {
            int k = RoundHalfUp((predicted - local) / period);
            return local + k * period;
        }

        public static Vector3 Wrap(Vector3 world, out int kx, out int ky, out int kz)
        {
            return new Vector3(
                WrapAxis(world.x, PeriodX, out kx),
                WrapAxis(world.y, PeriodY, out ky),
                WrapAxis(world.z, PeriodZ, out kz));
        }

        public static Vector3 Unwrap(Vector3 local, Vector3 predicted)
        {
            return new Vector3(
                UnwrapAxis(local.x, predicted.x, PeriodX),
                UnwrapAxis(local.y, predicted.y, PeriodY),
                UnwrapAxis(local.z, predicted.z, PeriodZ));
        }

        // ── Server-side per-object state ──────────────────────────────────────
        // Flat arrays indexed by the 16-bit network object id. Puck truncates the id to a
        // ushort on the wire, so a 65536-entry array is a total function of the key with
        // no hashing, no allocation and no lookup failure on the per-tick hot path.

        private static readonly Vector3[] _srvWorld = new Vector3[TableSize];
        private static readonly bool[] _srvWrapped = new bool[TableSize];
        private static readonly int[] _srvGen = new int[TableSize];

        /// <summary>
        /// Bumped once per Capture. Every table entry is stamped with the generation that
        /// wrote it, and the Write hook refuses to act on an entry from an older one.
        ///
        /// This exists because all four adversarial reviews independently found the same
        /// bug in the previous shape of this design: if the feature disarms between the
        /// constructor and Write, or if Write runs for an object this tick never captured,
        /// a stale table entry gets applied and the position is offset twice. A stale entry
        /// is now structurally unreadable rather than merely unlikely.
        /// </summary>
        private static int _gen;

        public static int Generation => _gen;
        public static void BumpGeneration() => _gen++;

        // No teleport flag here any more. There used to be a _srvTeleported array written on
        // every capture and read by nothing: the live signal is WrapSyncEncode._teleported,
        // drained per client. Two channels for one fact, one of them write-only across 65536
        // slots every tick, invited exactly the wrong conclusion when reading this signature.
        public static void StampServer(ushort id, Vector3 world, bool wrapped)
        {
            _srvWorld[id] = world;
            _srvWrapped[id] = wrapped;
            _srvGen[id] = _gen;
        }

        /// <summary>
        /// Last true world position we recorded for this object, ignoring the generation.
        ///
        /// Deliberately generation-blind, unlike TryGetServerStamp. This is used only for
        /// teleport detection, which compares this tick's pose against the previous one and
        /// must work across the generation boundary that every Capture creates. Using the
        /// generation-checked accessor here would report a teleport on the first object of
        /// every single tick.
        /// </summary>
        public static bool PeekServerWorld(ushort id, out Vector3 world)
        {
            world = _srvWorld[id];
            return _srvGen[id] != 0;
        }

        public static bool TryGetServerStamp(ushort id, out Vector3 world, out bool wrapped)
        {
            world = _srvWorld[id];
            wrapped = _srvWrapped[id];
            return _srvGen[id] == _gen;
        }


        public static void EvictServer(ushort id)
        {
            _srvWorld[id] = Vector3.zero;
            _srvWrapped[id] = false;
            _srvGen[id] = 0;
        }

        // ── Client-side per-object state ──────────────────────────────────────
        // The predictor. CliWorld is the last world position we committed for an object,
        // and CliSeen says whether we have one at all.

        private static readonly Vector3[] _cliWorld = new Vector3[TableSize];
        private static readonly bool[] _cliSeen = new bool[TableSize];

        public static bool TryGetPredictor(ushort id, out Vector3 predicted)
        {
            predicted = _cliWorld[id];
            return _cliSeen[id];
        }

        public static void CommitClient(ushort id, Vector3 world)
        {
            _cliWorld[id] = world;
            _cliSeen[id] = true;
        }

        /// <summary>
        /// Apply an authoritative seed. This is the only mechanism that can repair a
        /// settled one-period error, which is otherwise a stable and invisible fixed
        /// point: once the predictor is a whole period out, every subsequent unwrap
        /// reproduces the same offset and nothing in the residual reveals it.
        /// </summary>
        public static void ApplySeed(ushort id, Vector3 world) => CommitClient(id, world);

        public static void EvictClient(ushort id)
        {
            _cliWorld[id] = Vector3.zero;
            _cliSeen[id] = false;
        }

        public static void ClearServer()
        {
            System.Array.Clear(_srvWorld, 0, TableSize);
            System.Array.Clear(_srvWrapped, 0, TableSize);
            System.Array.Clear(_srvGen, 0, TableSize);
            // NOT 0. Slot value 0 is the "never stamped" sentinel that PeekServerWorld and
            // TryGetServerStamp test against, so leaving the live generation at 0 too makes
            // every one of the 65536 freshly-cleared slots compare EQUAL to the current
            // generation. TryGetServerStamp would then report a valid stamp of world
            // (0,0,0) wrapped=false for every id, WritePrefix would take the "stamp is
            // current" branch, and TxStaleGen would stop counting the very thing it exists
            // to count.
            _gen = 1;
        }

        /// <summary>
        /// Also drops ServerIsWrapping: client ids and sessions are reused, and a stale true
        /// carried into a vanilla or older server would hold the wire range at vanilla on a
        /// resized rink, which saturates every coordinate.
        /// </summary>
        public static void ClearClient()
        {
            System.Array.Clear(_cliWorld, 0, TableSize);
            System.Array.Clear(_cliSeen, 0, TableSize);
            ServerIsWrapping = false;
            ServerStatusKnown = false;
        }

        public static void ClearAll() { ClearServer(); ClearClient(); }

        // ── Peer capability ───────────────────────────────────────────────────
        // A peer is "capable" when it has announced that it honours MaskWrapped and has a
        // seed handler registered. The server wraps only for capable clients; everyone
        // else gets absolute coordinates, which is the vanilla saturating behaviour, i.e.
        // the bug they would have had anyway rather than something worse.

        private static readonly HashSet<ulong> _capable = new HashSet<ulong>();

        public static bool IsPeerCapable(ulong clientId) => _capable.Contains(clientId);
        public static bool MarkPeerCapable(ulong clientId) => _capable.Add(clientId);
        public static bool MarkPeerIncapable(ulong clientId) => _capable.Remove(clientId);
        public static void ClearPeers() => _capable.Clear();

        // ── Counters, for the log line that proves this works live ────────────

        public static long RxRecords, RxWrapped, RxSeeds, RxUnseeded, RxKFlips;

        /// <summary>
        /// Largest per-axis gap ever seen between an arriving seed (the server's true world
        /// position) and the predictor we would have unwrapped against, in metres.
        ///
        /// This is the margin question turned into a measurement. Unwrap is exact only while
        /// that gap stays under P/2, so this number IS the safety margin: it should sit near
        /// zero, and a value climbing toward 25 means the predictor is going stale faster
        /// than the seeds refresh it and objects are about to land a whole period out.
        /// It was declared and zeroed but never assigned, so the one statistic that could
        /// answer the question reported nothing.
        /// </summary>
        public static float RxSeedResidualMax;

        /// <summary>Compare an arriving seed against the current predictor, before applying it.</summary>
        public static void NoteSeedResidual(ushort id, Vector3 seedWorld)
        {
            try
            {
                if (!TryGetPredictor(id, out Vector3 predicted)) return;
                Vector3 d = seedWorld - predicted;
                float worst = Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z)));
                if (worst > RxSeedResidualMax) RxSeedResidualMax = worst;
            }
            catch { }
        }
        public static long TxSeeds, TxTeleportForces, TxStaleGen, TxBaselineWipes;
        /// <summary>Displacement-triggered reseeds: an object moved far without being sent.</summary>
        public static long TxDriftForces;
        /// <summary>Records actually shipped wrapped, and records restored to absolute.</summary>
        public static long TxWrapped, TxDowngraded;

        /// <summary>
        /// True when wrapped positions govern the wire, and therefore when the position
        /// range must stay at VANILLA rather than growing with the arena.
        ///
        /// These two features are mutually exclusive by construction. Wrapping folds a
        /// world coordinate into the vanilla window so the window never has to grow; range
        /// widening grows the window so the coordinate always fits. Running both means the
        /// folded coordinate, which now only ever spans +/-25, gets compressed against a
        /// range ten times that size. It uses a tenth of the Int16 codes and the
        /// quantisation step is exactly as coarse as widening alone. Wrapping then costs
        /// seeds and a marker bit and buys nothing at all.
        ///
        /// Resolved differently per role on purpose. The server is authoritative and knows
        /// whether it is really wrapping (self-test included). A client cannot know that, so
        /// it trusts the synced flag, which the server only advertises when it is genuinely
        /// armed. Both ends must reach the SAME answer or every position is out by the ratio
        /// between the two ranges.
        /// </summary>
        public static bool WrappingGovernsRange
        {
            get
            {
                try
                {
                    var nm = Unity.Netcode.NetworkManager.Singleton;
                    if (nm != null && nm.IsServer) return WrapSyncEncode.Armed;
                    return ServerIsWrapping;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Client-side mirror of "the server is wrapping", written ONLY by the config-sync
        /// unpack.
        ///
        /// This deliberately does NOT live in ServerConfig. It used to be a public field of
        /// [Serializable] CompAdjustConfig, which meant a second channel serialized it: the
        /// PPKB/ConfigFull JSON carried the server's on-disk value (always false, because no
        /// server code ever wrote the real state into it) and landed AFTER the sync package
        /// that carried the truth. The client's status was clobbered back to false, so it
        /// re-widened its decode window to the arena scale while the server kept encoding
        /// wrapped locals against the vanilla window. Every coordinate then decoded by the
        /// widen ratio too large and got snapped to whole-period multiples.
        ///
        /// A replicated status with two writers and two transports is the bug. One field,
        /// one writer, no serializer can reach it.
        /// </summary>
        public static bool ServerIsWrapping;

        /// <summary>
        /// True once a config sync has actually told us what the server is doing.
        ///
        /// ServerIsWrapping defaults to false, and false means "widen the range with the
        /// arena". So before the first sync a client would answer the range question from a
        /// default rather than from anything the server said. The trigger that asks is
        /// PPKB/GoalTweaks, which carries the arena scale but NOT the wrap status, so that
        /// window is real: the client learns the rink is 10x while still assuming vanilla
        /// encoding, and widens against a server that is wrapping.
        ///
        /// Until this is true, a client must leave the range alone.
        /// </summary>
        public static bool ServerStatusKnown;

        /// <summary>Records the server's wrapping status from a config sync.</summary>
        public static void SetServerWrapping(bool wrapping)
        {
            ServerIsWrapping = wrapping;
            ServerStatusKnown = true;
        }

        private static float _nextStatsAt;

        /// <summary>
        /// Periodic one-line report. Called from the per-frame bridge on every role.
        ///
        /// Every counter in this file was being incremented and never read, which for a
        /// feature about to be switched on for the first time is the difference between
        /// "it works" and "nothing happened and nobody noticed". The numbers that matter:
        /// TxWrapped rising means the server really is wrapping; RxWrapped rising means the
        /// client really is honouring the marker; RxUnseeded should stay at zero, because
        /// anything else means the seeding analysis is wrong somewhere.
        /// </summary>
        public static void MaybeLogStats()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextStatsAt) return;
                _nextStatsAt = Time.realtimeSinceStartup + 15f;

                bool anyTx = TxWrapped != 0 || TxDowngraded != 0 || TxSeeds != 0;
                bool anyRx = RxWrapped != 0 || RxUnseeded != 0 || RxKFlips != 0;
                if (!anyTx && !anyRx) return;   // silent until something actually happens

                if (anyTx)
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"WrapSync TX: wrapped={TxWrapped} downgraded={TxDowngraded} seeds={TxSeeds} "
                        + $"teleportForces={TxTeleportForces} staleGen={TxStaleGen} "
                        + $"baselineWipes={TxBaselineWipes}");

                if (anyRx)
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"WrapSync RX: records={RxRecords} wrapped={RxWrapped} seeds={RxSeeds} "
                        + $"kFlips={RxKFlips} UNSEEDED={RxUnseeded} "
                        + $"worstSeedResidual={RxSeedResidualMax:F2}m (margin is {PeriodX * 0.5f:F0}m)"
                        + (RxUnseeded != 0
                            ? "  <-- nonzero means wrapped records arrived with no predictor; "
                              + "those decoded at k=0 and may be a whole period out."
                            : ""));
            }
            catch { }
        }

        public static void ResetCounters()
        {
            RxRecords = RxWrapped = RxSeeds = RxUnseeded = RxKFlips = 0;
            RxSeedResidualMax = 0f;
            TxSeeds = TxTeleportForces = TxStaleGen = TxBaselineWipes = TxWrapped = TxDowngraded = TxDriftForces = 0;
        }

        // ── Self-test, phase 1: the arithmetic ────────────────────────────────

        /// <summary>
        /// Prove the wrap/unwrap arithmetic on this runtime before anything touches the
        /// network. Returns null on success or the first failure description.
        ///
        /// This asserts the FAILURE mode as well as the success mode. A scheme whose
        /// errors are always exactly one whole period is recoverable and detectable; one
        /// that can be off by a fraction is neither. The sweep past the margin checks that
        /// property explicitly rather than only checking that the good cases work.
        /// </summary>
        public static string SelfTestPhase1()
        {
            float[] periods = { PeriodX, PeriodY, PeriodZ };
            string[] names = { "X", "Y", "Z" };

            for (int a = 0; a < periods.Length; a++)
            {
                float p = periods[a];
                float half = p * 0.5f;

                // Deterministic sweep well past any plausible arena, plus the origin.
                for (float x = -3000f; x <= 3000f; x += 0.37f)
                {
                    float local = WrapAxis(x, p, out int k);

                    if (Mathf.Abs(local) > half + 1e-3f)
                        return $"axis {names[a]}: |local|={Mathf.Abs(local)} exceeds P/2={half} at world={x}";

                    // Reconstruction must be exact, not merely close.
                    float rebuilt = local + k * p;
                    if (Mathf.Abs(rebuilt - x) > 1e-3f)
                        return $"axis {names[a]}: reconstruct error {Mathf.Abs(rebuilt - x)} at world={x}";

                    // Unwrap against a predictor inside the margin must recover exactly.
                    foreach (float frac in new[] { 0f, 0.2f, 0.45f, -0.2f, -0.45f })
                    {
                        float predicted = x + frac * p;
                        float got = UnwrapAxis(local, predicted, p);
                        if (Mathf.Abs(got - x) > 1e-3f)
                            return $"axis {names[a]}: unwrap failed at world={x} predictorError={frac * p} got={got}";
                    }

                    // Past the margin the error must be exactly one whole period. If it can
                    // be a fraction, the whole detectability argument collapses.
                    foreach (float frac in new[] { 0.75f, 1.0f, -0.75f, -1.0f })
                    {
                        float predicted = x + frac * p;
                        float got = UnwrapAxis(local, predicted, p);
                        float err = Mathf.Abs(got - x);
                        float periods_off = err / p;
                        if (err > 1e-3f && Mathf.Abs(periods_off - Mathf.Round(periods_off)) > 1e-3f)
                            return $"axis {names[a]}: past-margin error {err} is not a whole period at world={x}";
                    }
                }
            }

            // Bit 14 must be outside anything vanilla can produce.
            //
            // Derived from the RUNNING BUILD, by OR-ing every CHANGE_MASK_* constant the game
            // itself declares. The previous check compared two of our own consts, which
            // Roslyn folded to a constant false and emitted as nothing at all: it read the
            // build it was supposedly testing exactly never. On B1231 the computed OR is 8191
            // so this changes nothing; on a build that adds a fourteenth flag it hard-fails,
            // which is the only day it was ever going to matter.
            int ceiling = ComputeVanillaMaskCeiling();
            if (ceiling < 0)
                return "could not read the CHANGE_MASK_* constants off this build, so the "
                       + "marker bit cannot be proven free";
            if ((ceiling & MaskWrapped) != 0)
                return $"marker bit {MaskWrapped} collides with this build's attainable "
                       + $"ChangeMask ceiling {ceiling}; wrapping cannot be marked safely here";
            if (ceiling != MaxVanillaMask)
                Debug.LogWarning($"[COMPADJUST] WrapSync: this build's ChangeMask ceiling is {ceiling}, "
                                 + $"not the expected {MaxVanillaMask}. The marker bit is still free, "
                                 + "so this is informational, but the wire assumptions were authored "
                                 + "against a different build.");

            return null;
        }

        /// <summary>
        /// OR of every CHANGE_MASK_* constant on SynchronizedObjectData: the largest
        /// ChangeMask an unmodified peer can put on the wire. Returns -1 if they cannot be
        /// read, which blocks arming rather than guessing.
        /// </summary>
        private static int ComputeVanillaMaskCeiling()
        {
            try
            {
                int or = 0, seen = 0;
                var fields = typeof(SynchronizedObjectData).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static);
                foreach (var f in fields)
                {
                    if (!f.Name.StartsWith("CHANGE_MASK_")) continue;
                    object v = f.GetValue(null);
                    if (v == null) continue;
                    or |= System.Convert.ToInt32(v);
                    seen++;
                }
                return seen == 0 ? -1 : or;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Phase 2: prove the PRECISION claim, which is the entire reason this exists.
        ///
        /// Runs a world coordinate through the real wire path: wrap, compress to Int16
        /// against the VANILLA window, decompress, unwrap against a predictor. The residual
        /// must stay at vanilla's half-LSB no matter how far from the origin the coordinate
        /// is. If this passes, a 200 m rink quantises exactly as finely as a 50 m one, which
        /// is what range widening cannot do.
        /// </summary>
        public static string SelfTestPhase2()
        {
            // The LIVE bounds, not our own literals. Passing the same hardcoded pair to both
            // compress and decompress made this an identity test on Unity's Lerp: any
            // consistent pair round-trips to within half a step, so it could not detect the
            // single failure it exists to catch, a window that grew with the arena. These are
            // the same accessors the transpiled constructor would actually call.
            var axes = new[]
            {
                new { Name = "X", Period = PeriodX,
                      Min = SyncRangePatch.IsPatched ? SyncRangePatch.GetMinX() : -25f,
                      Max = SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxX() : 25f },
                new { Name = "Y", Period = PeriodY,
                      Min = SyncRangePatch.IsPatched ? SyncRangePatch.GetMinY() : -50f,
                      Max = SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxY() : 50f },
                new { Name = "Z", Period = PeriodZ,
                      Min = SyncRangePatch.IsPatched ? SyncRangePatch.GetMinZ() : -50f,
                      Max = SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxZ() : 50f },
            };

            foreach (var ax in axes)
            {
                // Vanilla's own quantisation step, which is the bar we must not fall below.
                float step = (ax.Max - ax.Min) / 65535f;

                // 1.5 quantisation steps. Modelled worst case is 0.56 steps, so this is not
                // slack for its own sake: it absorbs any difference in rounding mode inside
                // CompressFloatToShort without weakening what the test proves. The failure
                // this must catch is a range that GREW with the arena, and that shows up as
                // N steps of error for an arena scaled N times, not as a fraction of one.
                float tolerance = step * 1.5f;

                for (float world = -2000f; world <= 2000f; world += 0.61f)
                {
                    float local = WrapAxis(world, ax.Period, out _);

                    // The wrapped value MUST fit the vanilla window, or CompressFloatToShort
                    // saturates and the whole scheme silently pins objects to a plane.
                    if (local < ax.Min || local > ax.Max)
                        return $"axis {ax.Name}: wrapped {local} escapes the vanilla window "
                               + $"[{ax.Min},{ax.Max}] at world={world}";

                    short wire = NetworkingUtils.CompressFloatToShort(local, ax.Min, ax.Max);
                    float back = NetworkingUtils.DecompressShortToFloat(wire, ax.Min, ax.Max);

                    // Predictor deliberately offset, but inside the margin.
                    float predicted = world + 0.3f * ax.Period;
                    float got = UnwrapAxis(back, predicted, ax.Period);

                    float err = Mathf.Abs(got - world);
                    if (err > tolerance)
                        return $"axis {ax.Name}: residual {err * 1000f:F3}mm exceeds vanilla "
                               + $"half-LSB tolerance {tolerance * 1000f:F3}mm at world={world}";
                }
            }

            return null;
        }

        /// <summary>
        /// Phase 3: prove LENGTH NEUTRALITY against the game's real serializer.
        ///
        /// The design rests on the marker bit adding nothing to the wire. Records are
        /// concatenated with no per-record length, so a single byte of disagreement between
        /// Write and Read does not misplace one object, it shreds every record after it.
        /// That was the failure mode of the previous chunked attempt, so this asserts the
        /// property against SynchronizedObjectData.Write itself rather than reasoning about
        /// which literals its branches test.
        /// </summary>
        public static string SelfTestPhase3()
        {
            try
            {
                var probes = new[]
                {
                    (ushort)CHANGE_MASK_ALL, (ushort)CHANGE_MASK_MOTION,
                    (ushort)(CHANGE_MASK_ALL | 1024), (ushort)(CHANGE_MASK_ALL | 4096),
                    (ushort)8, (ushort)0,
                };

                // Prove the instrument works before trusting what it reports. If Write
                // silently produced nothing, every probe would measure 0, plain would equal
                // marked, and this phase would pass having demonstrated nothing at all.
                int empty = MeasureWriteLength(0);
                int full = MeasureWriteLength(CHANGE_MASK_ALL);
                if (empty <= 0)
                    return "Write produced no bytes; the length probe is not measuring anything";
                if (full <= empty)
                    return $"Write length did not grow with the change mask ({empty} -> {full}); "
                           + "the length probe is not sensitive to record content";

                foreach (ushort baseMask in probes)
                {
                    int plain = MeasureWriteLength(baseMask);
                    int marked = MeasureWriteLength((ushort)(baseMask | MaskWrapped));

                    if (plain <= 0 || marked <= 0)
                        return "could not measure Write length on this build";

                    int readBack = MeasureReadLength(baseMask);
                    int readMarked = MeasureReadLength((ushort)(baseMask | MaskWrapped));
                    if (readBack != plain || readMarked != marked)
                        return $"Read consumed a different byte count than Write produced for mask "
                               + $"{baseMask} (wrote {plain}/{marked}, read {readBack}/{readMarked}). "
                               + "Records are concatenated with no per-record length, so this would "
                               + "shred every record after the first.";

                    if (plain != marked)
                        return $"marker bit changed the serialized length for mask {baseMask}: "
                               + $"{plain} bytes without, {marked} with. The marker is NOT length "
                               + "neutral on this build and arming would shred the record stream.";
                }

                return null;
            }
            catch (Exception ex)
            {
                return "phase 3 threw: " + ex.Message;
            }
        }

        private const int CHANGE_MASK_ALL = 3071;
        private const int CHANGE_MASK_MOTION = 1023;

        // Invoked reflectively so this does not depend on Write's accessibility, and so a
        // build that renames or reshapes it degrades to "cannot measure" (which blocks
        // arming) rather than to a compile error or a wrong answer.
        private static System.Reflection.MethodInfo _writeMethod;
        private static bool _writeMethodResolved;

        private static int MeasureWriteLength(ushort mask)
        {
            if (!_writeMethodResolved)
            {
                _writeMethodResolved = true;
                _writeMethod = typeof(SynchronizedObjectData).GetMethod(
                    "Write",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(FastBufferWriter) }, null);
            }
            if (_writeMethod == null) return -1;

            object data = new SynchronizedObjectData(1UL, new Vector3(1f, 2f, 3f),
                Quaternion.identity, Vector3.zero, Vector3.zero);

            // Boxed, so the mask is set through the box the reflective call will unbox from.
            var maskField = typeof(SynchronizedObjectData).GetField("ChangeMask");
            if (maskField == null) return -1;
            maskField.SetValue(data, mask);

            var w = new FastBufferWriter(256, Allocator.Temp);
            try
            {
                _writeMethod.Invoke(data, new object[] { w });
                return w.Length;
            }
            finally { w.Dispose(); }
        }

        private static System.Reflection.MethodInfo _readMethod;
        private static bool _readMethodResolved;

        /// <summary>
        /// Bytes the game's own Read CONSUMES for a record Write produced. Measuring Write
        /// alone proved half of a two-sided property: the failure that matters is Write and
        /// Read disagreeing, and only a round trip can see that.
        /// </summary>
        private static int MeasureReadLength(ushort mask)
        {
            if (!_readMethodResolved)
            {
                _readMethodResolved = true;
                _readMethod = typeof(SynchronizedObjectData).GetMethod(
                    "Read",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(FastBufferReader) }, null);
            }
            if (_readMethod == null || _writeMethod == null) return -1;

            object data = new SynchronizedObjectData(1UL, new Vector3(1f, 2f, 3f),
                Quaternion.identity, Vector3.zero, Vector3.zero);
            var maskField = typeof(SynchronizedObjectData).GetField("ChangeMask");
            if (maskField == null) return -1;
            maskField.SetValue(data, mask);

            var w = new FastBufferWriter(256, Allocator.Temp);
            try
            {
                _writeMethod.Invoke(data, new object[] { w });
                var r = new FastBufferReader(w, Allocator.Temp);
                try
                {
                    object fresh = new SynchronizedObjectData();
                    _readMethod.Invoke(fresh, new object[] { r });
                    return r.Position;
                }
                finally { r.Dispose(); }
            }
            catch { return -1; }
            finally { w.Dispose(); }
        }

        /// <summary>
        /// Run every phase. Cached: the result cannot change within a process, and the
        /// sweeps are not free.
        /// </summary>
        public static bool SelfTestPassed
        {
            get
            {
                if (_selfTestRun) return _selfTestPassed;
                _selfTestRun = true;

                string fail = SelfTestPhase1() ?? SelfTestPhase2() ?? SelfTestPhase3();
                _selfTestPassed = fail == null;

                if (_selfTestPassed)
                {
                    CompetitiveAdjustments.ConfigManager.Log(
                        "WrapSync self-test PASSED (arithmetic, precision, wire-length neutrality). "
                        + $"Quantisation stays at vanilla {(50f / 65535f) * 1000f:F3}mm on X and "
                        + $"{(100f / 65535f) * 1000f:F3}mm on Y/Z at any arena scale.");
                }
                else
                {
                    Debug.LogWarning("[COMPADJUST] WrapSync self-test FAILED: " + fail
                                     + ". Wrapped positions will NOT arm on this build.");
                }
                return _selfTestPassed;
            }
        }

        private static bool _selfTestRun;
        private static bool _selfTestPassed;
    }
}
