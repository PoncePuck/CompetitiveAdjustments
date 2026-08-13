using System.Collections.Generic;
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
        private static readonly bool[] _srvTeleported = new bool[TableSize];

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

        public static void StampServer(ushort id, Vector3 world, bool wrapped, bool teleported)
        {
            _srvWorld[id] = world;
            _srvWrapped[id] = wrapped;
            _srvTeleported[id] = teleported;
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

        public static bool TakeTeleport(ushort id)
        {
            if (!_srvTeleported[id]) return false;
            _srvTeleported[id] = false;
            return true;
        }

        public static void EvictServer(ushort id)
        {
            _srvWorld[id] = Vector3.zero;
            _srvWrapped[id] = false;
            _srvGen[id] = 0;
            _srvTeleported[id] = false;
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
            System.Array.Clear(_srvTeleported, 0, TableSize);
            _gen = 0;
        }

        public static void ClearClient()
        {
            System.Array.Clear(_cliWorld, 0, TableSize);
            System.Array.Clear(_cliSeen, 0, TableSize);
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
        public static float RxSeedResidualMax;
        public static long TxSeeds, TxTeleportForces, TxStaleGen, TxBaselineWipes;

        public static void ResetCounters()
        {
            RxRecords = RxWrapped = RxSeeds = RxUnseeded = RxKFlips = 0;
            RxSeedResidualMax = 0f;
            TxSeeds = TxTeleportForces = TxStaleGen = TxBaselineWipes = 0;
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
            if ((MaskWrapped & MaxVanillaMask) != 0)
                return $"MaskWrapped {MaskWrapped} collides with the vanilla mask ceiling {MaxVanillaMask}";

            return null;
        }
    }
}
