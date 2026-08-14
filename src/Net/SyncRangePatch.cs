using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Widens Puck's network position range so a resized arena does not clip on the wire.
    ///
    /// B1231 quantises positions inside SynchronizedObjectData: the constructor calls
    /// NetworkingUtils.CompressFloatToShort(value, min, max) per axis, and GetPosition()
    /// calls DecompressShortToFloat with the same bounds. The bounds are compile-time
    /// constants inlined as IL literals:
    ///
    ///     POSITION_MIN_X = -25   POSITION_MAX_X = 25
    ///     POSITION_MIN_Y = -50   POSITION_MAX_Y = 50
    ///     POSITION_MIN_Z = -50   POSITION_MAX_Z = 50
    ///
    /// CompressFloatToShort saturates (Mathf.InverseLerp, then Mathf.Lerp, then
    /// Mathf.Clamp), so a position past a bound is not wrapped, it is pinned exactly at
    /// the bound and stays there. On a rink scaled past those bounds that reads in game
    /// as an invisible wall: the server simulates correctly and the host sees nothing
    /// wrong, but every client receives pinned coordinates, so remote objects stop dead
    /// on a plane in world space.
    ///
    /// This replaces the old chunked-offset scheme, which is not portable to B1231:
    /// the manager-level Encode/DecodeSynchronizedObject choke point it patched no
    /// longer exists. Widening the range needs no per-object state, no chunk handoff
    /// protocol and no baseline invalidation, because GetChangeMask compares
    /// GetPosition() results in world-space floats against a 0.002 m threshold rather
    /// than comparing quantised shorts. It therefore reads the widened range for free
    /// and stays self-consistent.
    ///
    /// The cost is precision, traded uniformly rather than per object. A short carries
    /// 65535 steps across the span:
    ///
    ///     X at +/-25 (vanilla)  0.76 mm      X at +/-50   1.53 mm
    ///     Z at +/-50 (vanilla)  1.53 mm      Z at +/-100  3.05 mm
    ///
    /// For reference B1153 quantised at 655 units per metre, which is 1.53 mm, so a
    /// doubled range lands on exactly the precision this mod shipped with for years.
    ///
    /// SAFETY: server and client must agree on the range exactly. If they disagree,
    /// every position silently scales wrong instead of failing loudly. The range is
    /// therefore derived from the arena scale, which both ends already agree on through
    /// the existing PPKB/GoalTweaks sync, and it stays at the vanilla values unless
    /// GoalNetTweaks.TryGetEffectiveArenaScale reports a synced scale. That accessor
    /// returns false for an unsynced non-host, so a client on a vanilla server keeps
    /// vanilla numbers and decodes bit-identically to an unmodded client.
    /// </summary>
    public static class SyncRangePatch
    {
        // Vanilla B1231 bounds. These are the defaults and the teardown values.
        private const float VanillaMinX = -25f, VanillaMaxX = 25f;
        private const float VanillaMinY = -50f, VanillaMaxY = 50f;
        private const float VanillaMinZ = -50f, VanillaMaxZ = 50f;

        // Live bounds. Read from patched IL on both the encode and decode paths, so
        // these must stay in lockstep with the peer for the whole session.
        private static float _minX = VanillaMinX, _maxX = VanillaMaxX;
        private static float _minY = VanillaMinY, _maxY = VanillaMaxY;
        private static float _minZ = VanillaMinZ, _maxZ = VanillaMaxZ;

        // Called from transpiled IL. Must stay public static and signature-stable.
        public static float GetMinX() => _minX;
        public static float GetMaxX() => _maxX;
        public static float GetMinY() => _minY;
        public static float GetMaxY() => _maxY;
        public static float GetMinZ() => _minZ;
        public static float GetMaxZ() => _maxZ;

        /// <summary>
        /// True when the range transpilers are actually installed. This is what decides
        /// which bounds SynchronizedObjectData's constructor compresses against, so anything
        /// re-encoding a record by hand must read it rather than assume the vanilla literals.
        /// </summary>
        public static bool IsPatched => _patched;

        /// <summary>True when the live range is anything other than vanilla.</summary>
        public static bool IsWidened =>
            _maxX != VanillaMaxX || _maxY != VanillaMaxY || _maxZ != VanillaMaxZ;

        public static string DescribeRange() =>
            $"X +/-{_maxX:F1}m Y +/-{_maxY:F1}m Z +/-{_maxZ:F1}m";

        private const string HarmonyId = "compadjust.syncrange";
        private static Harmony _harmony;
        private static bool _patched;

        /// <summary>
        /// Scale a vanilla bound by the arena scale, keeping the same relative headroom
        /// the base game gave itself. Deriving from the vanilla bound rather than from a
        /// measured rink extent is deliberate: the vanilla bound already fits the vanilla
        /// rink with whatever margin the developers chose, so scaling it by the same
        /// factor as the rink preserves that margin without needing to know the rink size.
        ///
        /// Snapped up to a 0.5 m step so that a float difference between the server's
        /// own config value and the client's synced copy cannot put the two ends on
        /// different ranges. Never returns less than the vanilla bound, so this can only
        /// add room, never take it away.
        /// </summary>
        private static float ScaleBound(float vanillaBound, float scale)
        {
            if (!(scale > 1f) || float.IsNaN(scale) || float.IsInfinity(scale)) return vanillaBound;
            return Mathf.Max(vanillaBound, Mathf.Ceil(vanillaBound * scale * 2f) / 2f);
        }

        /// <summary>
        /// Install the transpilers and set the range from the current arena scale.
        /// Idempotent. Safe to call when the scale is 1: the accessors then return the
        /// vanilla numbers and the patched methods behave exactly like the originals.
        /// </summary>
        public static bool Enable()
        {
            // Decide the range FIRST, then decide whether patching is warranted at all.
            //
            // The old order patched unconditionally and then computed the range, so a rink
            // at scale 1.0 -- the overwhelmingly common case, including every default
            // server -- had two transpilers permanently resident on the SynchronizedObjectData
            // constructor and GetPosition for the sole purpose of substituting -25/25 and
            // -50/50 with accessors that return -25/25 and -50/50.
            //
            // That is all cost and no effect: those two methods are the entire serialization
            // path for every networked object every tick, they are a struct constructor and a
            // struct method, and they are the exact methods other patches also want. Not
            // patching when the numbers are unchanged removes the mod from that path
            // completely whenever the range is vanilla.
            RefreshRange();

            if (!IsWidened)
            {
                EnsureUnpatched("range is vanilla, so the transpilers would substitute identical constants");
                return true;
            }

            return EnsurePatched();
        }

        /// <summary>
        /// Recompute the live range from the current effective arena scale. Call this
        /// whenever the synced arena scale changes, not only on connect, or the two ends
        /// drift apart after a live config edit.
        /// </summary>
        public static void RefreshRange()
        {
            // Wrapping wins. It exists to make widening unnecessary, so while it is live the
            // window stays vanilla and every axis keeps full Int16 resolution no matter how
            // large the rink. Widening as well would fold the coordinate into +/-25 and then
            // spend a tenth of the codes representing it.
            if (WrapSync.WrappingGovernsRange)
            {
                if (IsWidened)
                    CompetitiveAdjustments.ConfigManager.Log(
                        "Wrapped positions are live, so the wire range returns to vanilla "
                        + "instead of growing with the arena. Quantisation goes back to "
                        + $"{(50f / 65535f) * 1000f:F2}mm on X and {(100f / 65535f) * 1000f:F2}mm "
                        + "on Y/Z, at any arena scale.");
                ResetRange();
                return;
            }

            if (!GoalNetTweaks.TryGetEffectiveArenaScale(out float scaleX, out float scaleZ))
            {
                // Not synced, or arena tweaks are off. Vanilla numbers.
                ResetRange();
                return;
            }

            float newMaxX = ScaleBound(VanillaMaxX, scaleX);
            float newMaxZ = ScaleBound(VanillaMaxZ, scaleZ);

            // The vertical axis is deliberately left at vanilla. TryGetEffectiveArenaScale
            // does not carry it, and +/-50 m of headroom is far more than a rink ceiling
            // needs even when ArenaScaleY is raised hard.
            if (newMaxX == _maxX && newMaxZ == _maxZ) return;

            _minX = -newMaxX; _maxX = newMaxX;
            _minZ = -newMaxZ; _maxZ = newMaxZ;

            CompetitiveAdjustments.ConfigManager.Log(
                $"Network position range set to {DescribeRange()} (arena scale X={scaleX:F2} Z={scaleZ:F2}); "
                + $"quantisation step X={(2f * _maxX) / 65535f * 1000f:F2}mm Z={(2f * _maxZ) / 65535f * 1000f:F2}mm. "
                + "Every peer must run this mod with the same arena scale.");
        }

        /// <summary>Back to the vanilla bounds. Leaves the patches installed.</summary>
        public static void ResetRange()
        {
            if (!IsWidened) return;
            _minX = VanillaMinX; _maxX = VanillaMaxX;
            _minY = VanillaMinY; _maxY = VanillaMaxY;
            _minZ = VanillaMinZ; _maxZ = VanillaMaxZ;
            CompetitiveAdjustments.ConfigManager.Log("Network position range restored to vanilla " + DescribeRange() + ".");
        }

        /// <summary>Reset the range and remove the transpilers. Idempotent.</summary>
        public static void Disable()
        {
            ResetRange();
            EnsureUnpatched("disabled");
        }

        /// <summary>
        /// Remove the transpilers if they are installed. Idempotent, and silent when there
        /// was nothing to remove, so the common vanilla-range path logs nothing.
        /// </summary>
        private static void EnsureUnpatched(string why)
        {
            if (_harmony == null && !_patched) return;
            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[COMPADJUST] SyncRangePatch UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }
            _patched = false;
            CompetitiveAdjustments.ConfigManager.Log(
                "SyncRangePatch: position range transpilers removed (" + why + "). "
                + "Wire range is vanilla " + DescribeRange() + ".");
        }

        private static bool EnsurePatched()
        {
            if (_patched) return true;
            try
            {
                var ctor = AccessTools.Constructor(typeof(SynchronizedObjectData), new[]
                {
                    typeof(ulong), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector3)
                });
                // NOT SynchronizedObjectData.GetPosition. See the note on CallSiteTranspiler.
                var bufferObjects = AccessTools.Method(typeof(SynchronizedObjectClientReceiver), "BufferObjects");
                var receiveReliable = AccessTools.Method(typeof(SynchronizedObjectClientReceiver), "ReceiveReliable");

                if (ctor == null || bufferObjects == null || receiveReliable == null)
                {
                    Debug.LogWarning("[COMPADJUST] SyncRangePatch: a patch target did not resolve; network position "
                                     + $"range stays vanilla. ctor={ctor != null} BufferObjects={bufferObjects != null} "
                                     + $"ReceiveReliable={receiveReliable != null}");
                    return false;
                }

                if (_harmony == null) _harmony = new Harmony(HarmonyId);
                _harmony.Patch(ctor, transpiler: new HarmonyMethod(typeof(SyncRangePatch), nameof(EncodeTranspiler)));
                _harmony.Patch(bufferObjects, transpiler: new HarmonyMethod(typeof(SyncRangePatch), nameof(CallSiteTranspiler)));
                _harmony.Patch(receiveReliable, transpiler: new HarmonyMethod(typeof(SyncRangePatch), nameof(CallSiteTranspiler)));

                _patched = true;
                CompetitiveAdjustments.ConfigManager.Log("SyncRangePatch: position range transpilers installed on "
                                                        + "SynchronizedObjectData ctor (encode) and on the "
                                                        + "BufferObjects/ReceiveReliable call sites (decode).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] SyncRangePatch failed to install: " + ex.Message);
                _harmony = null;
                _patched = false;
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Transpilers
        //
        // Both bodies are flat and regular. Encode, once per position axis:
        //     ldarg.2 ; ldfld Vector3::x ; ldc.r4 min ; ldc.r4 max
        //     call CompressFloatToShort ; stfld SynchronizedObjectData::X
        // Decode, once per position axis:
        //     ldarg.0 ; ldfld SynchronizedObjectData::X ; ldc.r4 min ; ldc.r4 max
        //     call DecompressShortToFloat
        //
        // We key off the position field (X, Y, Z) rather than off the literal values,
        // so the rotation bounds (+/-1) and the velocity bounds (+/-100) are untouched
        // even though they flow through the same two helpers. Matching on field identity
        // also means a build that changes a bound's value still transpiles correctly.
        // ──────────────────────────────────────────────────────────────────

        private static MethodInfo AccessorFor(string axis, bool min)
        {
            switch (axis)
            {
                case "X": return AccessTools.Method(typeof(SyncRangePatch), min ? nameof(GetMinX) : nameof(GetMaxX));
                case "Y": return AccessTools.Method(typeof(SyncRangePatch), min ? nameof(GetMinY) : nameof(GetMaxY));
                case "Z": return AccessTools.Method(typeof(SyncRangePatch), min ? nameof(GetMinZ) : nameof(GetMaxZ));
                default: return null;
            }
        }

        private static bool IsPositionField(object operand, out string axis)
        {
            axis = null;
            var field = operand as FieldInfo;
            if (field == null || field.DeclaringType != typeof(SynchronizedObjectData)) return false;
            if (field.Name != "X" && field.Name != "Y" && field.Name != "Z") return false;
            axis = field.Name;
            return true;
        }

        /// <summary>
        /// Rewrite the two bound literals feeding each position CompressFloatToShort call.
        /// The call is identified by the stfld that immediately follows it.
        /// </summary>
        public static IEnumerable<CodeInstruction> EncodeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var compress = AccessTools.Method(typeof(NetworkingUtils), "CompressFloatToShort");
            int rewritten = 0;

            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (compress == null) break;
                if (!(codes[i].opcode == System.Reflection.Emit.OpCodes.Call && Equals(codes[i].operand, compress))) continue;
                if (codes[i + 1].opcode != System.Reflection.Emit.OpCodes.Stfld) continue;
                if (!IsPositionField(codes[i + 1].operand, out string axis)) continue;
                if (i < 2) continue;
                if (codes[i - 2].opcode != System.Reflection.Emit.OpCodes.Ldc_R4) continue;
                if (codes[i - 1].opcode != System.Reflection.Emit.OpCodes.Ldc_R4) continue;

                codes[i - 2] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessorFor(axis, true))
                    .MoveLabelsFrom(codes[i - 2]).MoveBlocksFrom(codes[i - 2]);
                codes[i - 1] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessorFor(axis, false));
                rewritten++;
            }

            if (rewritten != 3)
                Debug.LogWarning($"[COMPADJUST] SyncRangePatch encode transpiler rewrote {rewritten} of 3 position axes. "
                                 + "Range widening is unreliable on this build; expect clipping or desync.");
            return codes;
        }

        /// <summary>
        /// The range-aware replacement for SynchronizedObjectData.GetPosition().
        ///
        /// Takes the record by ref so the call site's existing managed pointer is consumed
        /// directly and the substitution is stack-neutral.
        /// </summary>
        public static Vector3 ScaledPosition(ref SynchronizedObjectData d)
        {
            return new Vector3(
                NetworkingUtils.DecompressShortToFloat(d.X, _minX, _maxX),
                NetworkingUtils.DecompressShortToFloat(d.Y, _minY, _maxY),
                NetworkingUtils.DecompressShortToFloat(d.Z, _minZ, _maxZ));
        }

        /// <summary>
        /// Redirect `call instance Vector3 SynchronizedObjectData::GetPosition()` to the
        /// static ScaledPosition above, at the call site, in the CALLER.
        ///
        /// WHY NOT JUST PATCH GetPosition, WHICH IS OBVIOUSLY SIMPLER
        ///
        /// Because that combination is the one shape Harmony cannot detour correctly here:
        /// an instance method whose declaring type is a VALUE TYPE and whose return type is
        /// a struct larger than a machine word. Vector3 is 12 bytes, so it comes back
        /// through a hidden return-buffer pointer, and that pointer and `this` end up
        /// transposed. The decoded Vector3 is then written over the RECORD instead of into
        /// the return slot.
        ///
        /// SynchronizedObjectData is SequentialLayout, so its first twelve bytes are
        /// NetworkObjectId(0) ChangeMask(2) TickRateDivisor(4) X(6) Y(8) Z(10) -- precisely
        /// a Vector3-sized hole. The overwrite lands float bit patterns in ChangeMask, which
        /// clears bit 1024 (CHANGE_MASK_HIGH_PRECISION_ROTATION). get_Rotation then takes
        /// the packed branch and returns DecompressQuaternion(CompressedRotation) with a
        /// CompressedRotation that is legitimately 0 (the server only writes Rx/Ry/Rz/Rw in
        /// high-precision mode). DecompressQuaternion(0) is (x=1,y=0,z=0,w=0): exactly 180
        /// degrees about X. Meanwhile the real return slot is never written, so the position
        /// reads as exactly (0,0,0).
        ///
        /// That is the upside-down-at-the-origin bug, in full, and it is why it appeared
        /// only once an arena resize made this patch install.
        ///
        /// The two call sites patched here are instance methods on a CLASS returning void,
        /// which detours correctly. The constructor is likewise a safe shape (void return).
        /// The remaining GetPosition call sites live in change detection
        /// (SynchronizedObjectData.GetChangeMask twice, SynchronizedObjectSendPlanner.PlanObject)
        /// and are deliberately left alone: each compares two RECORDS decoded through the
        /// same function, so a uniform scale factor moves threshold sensitivity slightly and
        /// nothing else. Patching them would mean detouring a struct instance method to buy
        /// nothing.
        /// </summary>
        public static IEnumerable<CodeInstruction> CallSiteTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getPosition = AccessTools.Method(typeof(SynchronizedObjectData), "GetPosition");
            var replacement = AccessTools.Method(typeof(SyncRangePatch), nameof(ScaledPosition));
            int rewritten = 0;

            if (getPosition != null && replacement != null)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != System.Reflection.Emit.OpCodes.Call
                        && codes[i].opcode != System.Reflection.Emit.OpCodes.Callvirt) continue;
                    if (!Equals(codes[i].operand, getPosition)) continue;

                    codes[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, replacement)
                        .MoveLabelsFrom(codes[i]).MoveBlocksFrom(codes[i]);
                    rewritten++;
                }
            }

            if (rewritten != 1)
                Debug.LogWarning($"[COMPADJUST] SyncRangePatch call-site transpiler rewrote {rewritten} GetPosition "
                                 + "call(s), expected 1. Decoded positions may not match the encoded range.");
            return codes;
        }
    }
}
