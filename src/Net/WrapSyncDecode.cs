using System;
using HarmonyLib;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Client half of wrapped positions: unwrap an incoming wrapped coordinate against the
    /// predictor and commit the result.
    ///
    /// INSTALLED ONCE AT PLUGIN LOAD AND NEVER REMOVED. That is safe because these hooks
    /// are provably inert without WrapSync.MaskWrapped, and bit 14 is unreachable by an
    /// unmodded peer: verified in IL, vanilla's maximum attainable ChangeMask is
    /// 3071 | 1024 | 4096 = 8191.
    ///
    /// Permanence is a correctness property, not laziness. If decode could disarm, an
    /// armed server would keep sending wrapped records to a client that had just unpatched
    /// itself, and every position would land a whole period out with nothing to detect it.
    /// With decode always resident the server is the only thing that arms, so there is one
    /// switch instead of two that must agree.
    ///
    /// WHY THE FLAG IS LATCHED FROM THE RAW RECORD
    /// Merge and WithComponentMask both mask to 3071 (verified in IL), so the merged record
    /// the game actually applies has bit 14 stripped. The only place the marker is visible
    /// is the raw record handed to TryMergeReceivedData, so D1 latches it there and D2/D3
    /// consume the latch by object id. Both BufferObjects and ReceiveReliable call
    /// TryMergeReceivedData before the apply methods, so the ordering holds on both paths.
    /// </summary>
    internal static class WrapSyncDecode
    {
        private const string HarmonyId = "compadjust.wrapsync.decode";
        private static Harmony _harmony;
        private static bool _installed;

        /// <summary>
        /// True once the decode hooks are resident. This is exactly what the capability
        /// announce reports, and it must mean "the handler is installed and I honour bit
        /// 14", nothing weaker. The previous implementation announced capability off a flag
        /// that was true whenever the SAFE patches installed, including when the wire
        /// patches had been skipped, which told a server to send data this client had
        /// nothing installed to consume.
        /// </summary>
        public static bool Installed => _installed;

        // Per-object latch: did the raw record for this id carry the wrapped marker?
        private const int TableSize = 65536;
        private static readonly bool[] _wrappedLatch = new bool[TableSize];

        public static bool Install()
        {
            if (_installed) return true;
            try
            {
                var tryMerge = AccessTools.Method(typeof(SynchronizedObjectClientReceiver), "TryMergeReceivedData");
                var buffer = AccessTools.Method(typeof(SynchronizedObject), "Client_BufferSynchronizedObjectData");
                var applyRel = AccessTools.Method(typeof(SynchronizedObject), "Client_ApplyReliableSynchronizedObjectData");
                var forget = AccessTools.Method(typeof(SynchronizedObjectClientReceiver), "Forget");
                var dispose = AccessTools.Method(typeof(SynchronizedObjectClientReceiver), "Dispose");

                if (tryMerge == null || buffer == null || applyRel == null || forget == null || dispose == null)
                {
                    Debug.LogWarning("[COMPADJUST] WrapSyncDecode: a target did not resolve on this build; "
                                     + "wrapped positions will not be honoured. "
                                     + $"tryMerge={tryMerge != null} buffer={buffer != null} "
                                     + $"applyRel={applyRel != null} forget={forget != null} dispose={dispose != null}");
                    return false;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(tryMerge, postfix: new HarmonyMethod(typeof(WrapSyncDecode), nameof(TryMergePostfix)));
                _harmony.Patch(buffer, prefix: new HarmonyMethod(typeof(WrapSyncDecode), nameof(BufferPrefix)));
                _harmony.Patch(applyRel, prefix: new HarmonyMethod(typeof(WrapSyncDecode), nameof(ApplyReliablePrefix)));
                _harmony.Patch(forget, postfix: new HarmonyMethod(typeof(WrapSyncDecode), nameof(ForgetPostfix)));
                _harmony.Patch(dispose, postfix: new HarmonyMethod(typeof(WrapSyncDecode), nameof(DisposePostfix)));

                _installed = true;
                CompetitiveAdjustments.ConfigManager.Log(
                    "WrapSync decode installed (permanent, inert without wire bit "
                    + $"{WrapSync.MaskWrapped}). Periods X={WrapSync.PeriodX} Y={WrapSync.PeriodY} Z={WrapSync.PeriodZ}, "
                    + $"protocol fingerprint {WrapSyncSeed.Fingerprint():X8}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] WrapSyncDecode failed to install: " + ex);
                try { _harmony?.UnpatchSelf(); } catch { }
                _harmony = null;
                _installed = false;
                return false;
            }
        }

        // ── D1: latch the marker from the raw record ──────────────────────────

        // ── upside-down-at-origin probe (DETECTOR ONLY, changes nothing) ─────
        //
        // The cause of this symptom is NOT here. It was Harmony detouring
        // SynchronizedObjectData.GetPosition -- a struct instance method returning a
        // 12-byte Vector3 -- which transposes `this` with the hidden return-buffer pointer
        // and writes the decoded Vector3 over the first twelve bytes of the record. That
        // clears CHANGE_MASK_HIGH_PRECISION_ROTATION in ChangeMask, so get_Rotation takes
        // the packed branch over a CompressedRotation that is legitimately 0, and
        // DecompressQuaternion(0) is exactly 180 degrees about X. Fixed in
        // SyncRangePatch.CallSiteTranspiler.
        //
        // An earlier version of this file SUPPRESSED the sentinel here, on the theory that
        // Merge could leave a first record with no rotation. It cannot: TryMergeReceivedData
        // rejects a first record outright unless HasAllComponents, and HasAllComponents
        // includes CHANGE_MASK_ROTATION. Vanilla Merge is sound, so that mitigation was
        // treating a symptom whose stated cause did not exist, and it would have masked the
        // rotation half of a real defect while leaving the body at the origin.
        //
        // What remains is a detector. It never modifies a pose. If this ever fires again,
        // something is still corrupting ChangeMask and that is worth knowing loudly rather
        // than papering over.
        private static readonly bool[] _rotEverSeen = new bool[TableSize];
        private static int _rotSentinelLogged;
        private static long _rotSentinelSeen;
        private const int RotSentinelLogLimit = 10;

        /// <summary>Rotation sentinels observed. Nonzero means the ChangeMask corruption is back.</summary>
        public static long RotationSentinelsSeen => _rotSentinelSeen;

        private static void TryMergePostfix(SynchronizedObjectData synchronizedObjectData)
        {
            try
            {
                ulong wide = synchronizedObjectData.NetworkObjectId;
                if (wide > ushort.MaxValue) return;
                ushort id = (ushort)wide;

                _wrappedLatch[id] = (synchronizedObjectData.ChangeMask & WrapSync.MaskWrapped) != 0;

                if ((synchronizedObjectData.ChangeMask & ChangeMaskRotation) != 0)
                    _rotEverSeen[id] = true;
            }
            catch { /* never let a decode hook throw into the receive path */ }
        }

        private const ushort ChangeMaskRotation = 8;

        /// <summary>
        /// True when this quaternion is bit-for-bit the value DecompressQuaternion(0) yields.
        /// Compared tightly rather than with Quaternion.Angle: the point is to catch the
        /// exact sentinel, not anything merely near it, so a body genuinely lying on its back
        /// is left alone.
        /// </summary>
        private static bool IsZeroRotationSentinel(Quaternion q)
        {
            const float eps = 1e-6f;
            return Mathf.Abs(q.x - 1f) < eps && Mathf.Abs(q.y) < eps
                   && Mathf.Abs(q.z) < eps && Mathf.Abs(q.w) < eps;
        }

        /// <summary>Observe only. Deliberately does not touch the rotation.</summary>
        private static void ProbeRotation(ushort id, Quaternion rotation, Vector3 position)
        {
            if (!IsZeroRotationSentinel(rotation)) return;

            _rotSentinelSeen++;
            if (_rotSentinelLogged >= RotSentinelLogLimit) return;
            _rotSentinelLogged++;
            Debug.LogWarning(
                $"[COMPADJUST] ROTATION SENTINEL applied to id={id}: rotation is exactly "
                + "DecompressQuaternion(0) = 180 degrees about X, meaning ChangeMask lost "
                + $"CHANGE_MASK_HIGH_PRECISION_ROTATION. position={position} "
                + $"rotationEverSeenForThisId={_rotEverSeen[id]} [occurrence {_rotSentinelSeen}]"
                + (_rotSentinelLogged == RotSentinelLogLimit ? " -- further occurrences counted, not logged." : ""));
        }

        // ── D2 / D3: unwrap and commit ────────────────────────────────────────

        private static void BufferPrefix(SynchronizedObject __instance, ref Vector3 position, ref Quaternion rotation)
        {
            Decode(__instance, ref position, ref rotation);
        }

        private static void ApplyReliablePrefix(SynchronizedObject __instance, ref Vector3 position, ref Quaternion rotation)
        {
            Decode(__instance, ref position, ref rotation);
        }

        /// <summary>
        /// Both hooks are prefixes that return void, so they can never skip the original.
        /// That is deliberate: skipping would bypass MarkReceived / Append / MarkNewSample
        /// and stall the interpolator, and because the local player's own body is kinematic
        /// and externally driven on a client, a stall there freezes the player in place.
        /// This only ever rewrites the position argument.
        /// </summary>
        private static void Decode(SynchronizedObject obj, ref Vector3 position, ref Quaternion rotation)
        {
            try
            {
                if (obj == null) return;
                ulong wide = obj.NetworkObjectId;
                if (wide > ushort.MaxValue) return;
                ushort id = (ushort)wide;

                WrapSync.RxRecords++;

                ProbeRotation(id, rotation, position);

                if (!_wrappedLatch[id])
                {
                    // Absolute record. Still commit it, because it is the best predictor we
                    // have for whatever comes next, wrapped or not.
                    WrapSync.CommitClient(id, position);
                    return;
                }

                WrapSync.RxWrapped++;

                if (!WrapSync.TryGetPredictor(id, out Vector3 predicted))
                {
                    // First sight of a wrapped object with no seed yet. Decode at k = 0,
                    // which is correct for anything inside the first period and wrong by
                    // whole periods otherwise, and let the next reliable batch's seed
                    // correct it. Counted, because a nonzero count here means the
                    // seeding analysis is wrong somewhere.
                    WrapSync.RxUnseeded++;
                    WrapSync.CommitClient(id, position);
                    return;
                }

                Vector3 before = position;
                Vector3 world = WrapSync.Unwrap(position, predicted);

                // A chunk flip is normal and expected; it is only interesting next to the
                // residual, which is what distinguishes healthy wrapping from disagreement.
                if ((world - before).sqrMagnitude > 1f) WrapSync.RxKFlips++;

                position = world;
                WrapSync.CommitClient(id, world);
            }
            catch { /* never throw into the receive path */ }
        }

        // ── D4: eviction ──────────────────────────────────────────────────────

        private static void ForgetPostfix(ulong networkObjectId)
        {
            try
            {
                if (networkObjectId > ushort.MaxValue) return;
                ushort id = (ushort)networkObjectId;
                _wrappedLatch[id] = false;
                _rotEverSeen[id] = false;
                WrapSync.EvictClient(id);
            }
            catch { }
        }

        private static void DisposePostfix()
        {
            try
            {
                Array.Clear(_wrappedLatch, 0, TableSize);
                Array.Clear(_rotEverSeen, 0, TableSize);
                WrapSync.ClearClient();
            }
            catch { }
        }
    }
}
