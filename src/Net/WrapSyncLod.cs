using System;
using System.Reflection;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Keep the network level-of-detail bands rink-relative instead of metre-absolute.
    ///
    /// THE PROBLEM
    /// SynchronizedObjectLodSelector picks a band by comparing camera-to-object distance
    /// against SynchronizedObjectManager.serverLodBands[i].MinDistance, and culls against
    /// serverCulling.MinDistance. Both are fixed world-space metres tuned for a vanilla
    /// 25x50 m rink. Resizing the arena multiplies every real distance by the arena scale
    /// without touching those thresholds, so on a rink scaled 10x a player standing at the
    /// far blue line is, in metres, further away than vanilla's most distant band ever
    /// expected. Essentially every object lands in the last band and inherits its
    /// TickRateDivisor, and anything past the culling distance takes the culled divisor.
    ///
    /// A divisor of D means vanilla plans that object at most once every D ticks
    /// (SynchronizedObjectSendPlanner.PlanSend). So the exact feature wrapping exists to
    /// enable, a large rink, is what starves every object of updates. Remote players and the
    /// puck go choppy, and the gap between applied records is also what the unwrap margin
    /// has to survive.
    ///
    /// THE FIX
    /// Scale the thresholds by the same factor the arena was scaled by, so band membership
    /// stays a function of where you are ON THE RINK rather than how many metres away you
    /// happen to be. At scale 1 this is a no-op.
    ///
    /// Server-side only: these are the server's own send-planning inputs. The baseline is
    /// captured once, before the first modification, and restored on teardown, so leaving a
    /// modded server never leaves a client's LOD tuning altered.
    /// </summary>
    internal static class WrapSyncLod
    {
        private static float[] _baseBandDistances;
        private static float _baseCullDistance;
        private static bool _captured;
        private static float _appliedScale = 1f;

        private static FieldInfo _bandsField;
        private static FieldInfo _cullField;
        private static PropertyInfo _instanceProp;
        private static FieldInfo _minDistField;
        private static bool _resolved;

        private static bool Resolve()
        {
            if (_resolved) return _bandsField != null && _cullField != null
                                   && _instanceProp != null && _minDistField != null;
            _resolved = true;
            try
            {
                var mgr = typeof(SynchronizedObjectManager);
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _bandsField = mgr.GetField("serverLodBands", inst);
                _cullField = mgr.GetField("serverCulling", inst);
                _minDistField = typeof(SynchronizedObjectBandSettings).GetField("MinDistance");

                // NetworkBehaviourSingleton<T>.Instance, declared on the base type.
                for (var t = mgr; t != null; t = t.BaseType)
                {
                    _instanceProp = t.GetProperty("Instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_instanceProp != null) break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] WrapSyncLod could not resolve LOD fields: " + ex.Message);
            }
            return _bandsField != null && _cullField != null && _instanceProp != null && _minDistField != null;
        }

        private static object Manager()
        {
            try { return _instanceProp?.GetValue(null); }
            catch { return null; }
        }

        /// <summary>
        /// Set the LOD thresholds for an arena scaled by <paramref name="scale"/>.
        /// Idempotent, and always derived from the captured vanilla baseline rather than
        /// from the current values, so repeated calls cannot compound.
        /// </summary>
        public static void Apply(float scale)
        {
            try
            {
                if (!Resolve()) return;
                var mgr = Manager();
                if (mgr == null) return;
                if (scale <= 0f || float.IsNaN(scale)) return;

                var bands = _bandsField.GetValue(mgr) as Array;
                object cull = _cullField.GetValue(mgr);
                if (bands == null || cull == null) return;

                if (!_captured)
                {
                    _baseBandDistances = new float[bands.Length];
                    for (int i = 0; i < bands.Length; i++)
                        _baseBandDistances[i] = (float)_minDistField.GetValue(bands.GetValue(i));
                    _baseCullDistance = (float)_minDistField.GetValue(cull);
                    _captured = true;
                }

                if (Mathf.Abs(scale - _appliedScale) < 0.001f) return;
                _appliedScale = scale;

                for (int i = 0; i < bands.Length && i < _baseBandDistances.Length; i++)
                {
                    // Boxed so the struct can be mutated, then written back into the slot.
                    object b = bands.GetValue(i);
                    _minDistField.SetValue(b, _baseBandDistances[i] * scale);
                    bands.SetValue(b, i);
                }

                _minDistField.SetValue(cull, _baseCullDistance * scale);
                _cullField.SetValue(mgr, cull);

                CompetitiveAdjustments.ConfigManager.Log(
                    $"Network LOD bands scaled x{scale:F2} with the arena so band membership stays "
                    + "rink-relative. Without this every object on a resized rink falls into the "
                    + "farthest band and is sent at a fraction of the tick rate.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] WrapSyncLod.Apply failed: " + ex.Message);
            }
        }

        /// <summary>Put the vanilla thresholds back. Safe to call when nothing was changed.</summary>
        public static void Restore()
        {
            try
            {
                if (!_captured || !Resolve()) return;
                var mgr = Manager();
                if (mgr == null) return;

                var bands = _bandsField.GetValue(mgr) as Array;
                object cull = _cullField.GetValue(mgr);
                if (bands == null || cull == null) return;

                for (int i = 0; i < bands.Length && i < _baseBandDistances.Length; i++)
                {
                    object b = bands.GetValue(i);
                    _minDistField.SetValue(b, _baseBandDistances[i]);
                    bands.SetValue(b, i);
                }
                _minDistField.SetValue(cull, _baseCullDistance);
                _cullField.SetValue(mgr, cull);

                _appliedScale = 1f;
            }
            catch { }
        }
    }
}
