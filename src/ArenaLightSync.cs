// ArenaLightSync.cs - move the arena's lights with the resized rink.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DashFallMod
{
    public static partial class GoalNetTweaks
    {
        // Lights are not renderers, so the proxy pass does not reach them: it redraws
        // batched geometry through a matrix, which moves no transform at all. A light
        // parked in some other scene root therefore stays where it was baked while the
        // rink grows around it, which at ArenaScaleX 2 leaves the pools of light bunched
        // down the middle of a rink twice as wide.
        //
        // Same three-way split as the scenery, for the same reasons:
        //   under the level root  ->  the scaled parent already carries the whole resize,
        //                             all three axes and the offset. Nothing to do.
        //   outside it            ->  nothing applies, so the full delta goes on by hand
        //   directional           ->  no position to speak of, left alone (this is the sun)
        //
        // The first case used to be a height-only fix-up, from when ScaleLevelDefaultRoot
        // scaled the level root by (width, 1, length) and world Y really was the one axis
        // the parent did not supply. It now scales by (width, ArenaScaleZ, length), so that
        // fix-up became a second application of ArenaScaleZ on top of the parent's: a
        // fixture at 12 m under ArenaScaleZ 2 was placed at 48 m instead of 24 m, above the
        // ceiling it is meant to hang from. Invisible at the default ArenaScaleZ of 1,
        // which is why it survived testing. This is the same rule the non-batched renderers
        // and the group-2 scenery already follow, and for the same reason.
        //
        // Range grows with the largest scale factor so a light never covers less of the
        // rink than it used to. Intensity is deliberately NOT derived: a light lifted 3x
        // higher loses roughly 9x its illuminance at ice level by the inverse square law,
        // and guessing a compensation curve is how arenas end up either black or blown
        // out. ArenaLightIntensityScale is there to be turned by eye instead.

        private struct LightBaseline
        {
            public Vector3 Position;
            public float Range;
            public float Intensity;

            // Recorded at capture rather than re-tested on use, so the restore path makes
            // the same choice the sync path did. A light we never moved must not be
            // "restored" to a world position measured under a scale the parent has since
            // changed, which would displace the one fixture that was always correct.
            public bool UnderLevelRoot;
        }

        private static readonly Dictionary<int, LightBaseline> _lightBaselines =
            new Dictionary<int, LightBaseline>();
        private static readonly List<Light> _adjustedLights = new List<Light>();

        /// <summary>
        /// Client-local brightness multiplier for arena lights, applied after they are
        /// moved. Left at 1 the lights keep their authored intensity at their new
        /// positions, which reads darker at large ArenaScaleZ because everything is
        /// further from the ice.
        /// </summary>
        private static float ResolveArenaLightIntensityScale()
        {
            try
            {
                float value = DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ArenaLightIntensityScale ?? 1f;
                return Mathf.Clamp(value, 0.05f, 20f);
            }
            catch { }

            return 1f;
        }

        private static void SyncArenaLights(Matrix4x4 delta, Transform levelRoot)
        {
            if (levelRoot == null)
            {
                RestoreArenaLights();
                return;
            }

            Vector3 deltaScale = delta.lossyScale;
            float rangeScale = Mathf.Max(Mathf.Abs(deltaScale.x), Mathf.Max(Mathf.Abs(deltaScale.y), Mathf.Abs(deltaScale.z)));
            float intensityScale = ResolveArenaLightIntensityScale();

            int moved = 0, skipped = 0, baked = 0;

            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light == null) continue;
                if (light.type == LightType.Directional) { skipped++; continue; }
                if (!IsMovableLight(light, levelRoot)) { skipped++; continue; }

                // Baked lights contribute nothing at runtime, so moving one changes
                // nothing you can see. Counted so the log says whether this arena is lit
                // by fixtures we can actually drive or by a bake we cannot.
                if (light.bakingOutput.isBaked) baked++;

                Transform t = light.transform;
                int id = light.GetInstanceID();

                if (!_lightBaselines.TryGetValue(id, out LightBaseline baseline))
                {
                    baseline = new LightBaseline
                    {
                        Position = t.position,
                        Range = light.range,
                        Intensity = light.intensity,
                        UnderLevelRoot = t.IsChildOf(levelRoot),
                    };
                    _lightBaselines[id] = baseline;
                    _adjustedLights.Add(light);
                }

                // Under the scaled root there is nothing to write: the parent supplies the
                // full resize. Note this baseline was measured AFTER ScaleLevelDefaultRoot
                // ran, so it is an already-scaled world position and not a vanilla value
                // to build a target from. Only the lights outside the root need the delta,
                // and theirs is genuinely vanilla because nothing else touches them.
                if (!baseline.UnderLevelRoot)
                    t.position = delta.MultiplyPoint3x4(baseline.Position);

                light.range = baseline.Range * rangeScale;
                light.intensity = baseline.Intensity * intensityScale;
                moved++;
            }

            SyncRevivedBakedLights(intensityScale);
            ReportLightsOnce(moved, skipped, baked, rangeScale, intensityScale);
        }

        // ── Reviving baked fixtures ──────────────────────────────────────────────
        // The proxy draws geometry through Graphics.RenderMesh, and manually submitted
        // geometry cannot carry baked lightmaps. So every surface the proxy owns, which is
        // now the entire building, is lit by realtime lights and ambient alone. If this
        // arena was lit mostly by a bake, that is the whole lighting problem, and moving
        // the fixtures above does nothing because a baked light emits nothing at runtime.
        //
        // A baked light cannot be switched to realtime in place, but a fresh Light created
        // at runtime defaults to realtime, so the fixture is copied onto one we own.
        // Intensity is damped hard: a bake is an accumulated solution and replaying every
        // fixture at authored strength as direct light blows the rink out.
        //
        // ON by default: the proxy always destroys the bake, so shipping it without the
        // compensation ships a known-flat arena. Capped, because URP only lights an object
        // with a handful of additional lights anyway and reviving forty fixtures would buy
        // nothing past the first few while costing for all of them.

        // URP only lights any single object with a handful of additional lights, so a
        // higher cap costs light culling rather than shading work, and coverage matters
        // far more than count: 16 fixtures chosen purely by strength all land over the
        // rink, and the hangar shell stays lit by nothing but a per-object probe sample.
        // A probe gives ONE value for a whole mesh, so a wall that size renders flat no
        // matter how good the probes are. Realtime lights are what put normal-based
        // shading back on large surfaces, so they have to be spread, not stacked.
        private const int MaxRevivedLights = 32;

        /// <summary>
        /// Minimum spacing between revived fixtures before the cap starts dropping them,
        /// in metres of the ORIGINAL arena. Scaled up with the rink so the spread tracks
        /// the building rather than bunching as it grows.
        /// </summary>
        private const float RevivedLightSpacing = 10f;

        /// <summary>
        /// A revived fixture and the intensity it was derived from BEFORE
        /// ArenaLightIntensityScale. Keeping the pre-scale value is what lets the
        /// brightness knob be re-applied in place instead of compounding on itself.
        /// </summary>
        private struct RevivedLight
        {
            public GameObject Object;
            public Light Light;
            public float BaseIntensity;
        }

        private static readonly List<RevivedLight> _revivedLights = new List<RevivedLight>();
        private static Transform _revivedLightRoot;
        private static float _revivedIntensityScale = float.NaN;

        private static bool ResolveReviveBakedArenaLights()
        {
            try
            {
                return DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ReviveBakedArenaLights != false;
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Above this share of proxy draws keeping their lightmap, the bake is considered
        /// recovered and the revived fixtures are dropped.
        /// </summary>
        private const float LightmapCoverageThreshold = 0.5f;

        private static void SyncRevivedBakedLights(float intensityScale)
        {
            if (!ResolveReviveBakedArenaLights())
            {
                DestroyRevivedLights();
                return;
            }

            // A revived fixture only ever stood in for the lost bake. Now that the
            // lightmap is handed back to the proxy draws, the bake already contains every
            // one of these fixtures, and adding them again as realtime lights double-counts
            // them: the arena reads correctly lit but with a scatter of bright specular
            // blobs on the ice where each revived point light lands on top of the baked
            // pool it was copied from.
            if (ArenaProxyVisual.LightmapCoverage > LightmapCoverageThreshold)
            {
                if (_revivedLights.Count > 0)
                {
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"Baked lightmaps came back for {ArenaProxyVisual.LightmapCoverage:P0} of the proxy draws, " +
                        "so the revived fixtures are being dropped: the bake already contains them.");
                }

                DestroyRevivedLights();
                return;
            }

            if (_revivedLights.Count > 0)
            {
                // Already built. Brightness is the only thing that can change without a
                // rebuild, and re-applying it in place is what keeps the knob live:
                // without this the revived fixtures stayed pinned to whatever
                // ArenaLightIntensityScale was set to when they were created, so turning
                // it up brightened the real fixtures and left the substitutes behind.
                if (!Mathf.Approximately(_revivedIntensityScale, intensityScale))
                    RescaleRevivedLights(intensityScale);
                return;
            }

            if (_revivedLightRoot == null)
            {
                var root = new GameObject("CompAdjustRevivedArenaLights");
                UnityEngine.Object.DontDestroyOnLoad(root);
                _revivedLightRoot = root.transform;
            }

            // Strongest first, so the cap keeps the fixtures that actually matter rather
            // than whichever ones the scene happened to enumerate first.
            var candidates = new List<Light>();
            for (int i = 0; i < _adjustedLights.Count; i++)
            {
                Light source = _adjustedLights[i];
                if (source != null && source.bakingOutput.isBaked) candidates.Add(source);
            }

            candidates.Sort((a, b) => (b.intensity * b.range).CompareTo(a.intensity * a.range));

            List<Light> chosen = ChooseSpreadFixtures(candidates);
            int revived = 0;
            var covered = new Bounds();
            bool haveCoverage = false;

            for (int i = 0; i < chosen.Count; i++)
            {
                Light source = chosen[i];
                if (source == null) continue;

                if (!haveCoverage) { covered = new Bounds(source.transform.position, Vector3.zero); haveCoverage = true; }
                else covered.Encapsulate(source.transform.position);

                var go = new GameObject("RevivedFixture");
                go.transform.SetParent(_revivedLightRoot, false);
                go.transform.position = source.transform.position;
                go.transform.rotation = source.transform.rotation;

                Light copy = go.AddComponent<Light>();
                copy.type = source.type;
                copy.color = source.color;
                copy.range = source.range;
                copy.spotAngle = source.spotAngle;
                copy.innerSpotAngle = source.innerSpotAngle;
                copy.cullingMask = source.cullingMask;
                copy.bounceIntensity = 0f;

                // The AUTHORED intensity, off the baseline. Reading source.intensity here
                // was wrong: the sync loop above has already multiplied every adjusted
                // light by intensityScale, so scaling that again squared the client's
                // setting and ArenaLightIntensityScale 2 lit the revived fixtures 4x
                // (and 0.5 lit them at a quarter). The 0.45 damping factor was chosen
                // against the authored value, not against a pre-scaled one.
                float authored = _lightBaselines.TryGetValue(source.GetInstanceID(), out LightBaseline sourceBaseline)
                    ? sourceBaseline.Intensity
                    : source.intensity;

                // A fixture whose authored intensity is near zero was contributing only
                // through the bake, so it needs a value rather than a scale.
                float baseIntensity = authored < 0.05f ? 0.35f : authored * 0.45f;
                copy.intensity = baseIntensity * intensityScale;

                // Shadow maps for every revived fixture is a lot of cost for light that is
                // standing in for an ambient bake, and the sun already grounds everything.
                copy.shadows = LightShadows.None;
                copy.enabled = true;

                _revivedLights.Add(new RevivedLight { Object = go, Light = copy, BaseIntensity = baseIntensity });
                revived++;
            }

            _revivedIntensityScale = intensityScale;

            if (revived > 0)
            {
                // The spread is the number that matters, not the count: it says whether
                // the revived fixtures actually reach the hangar shell or only cover the
                // rink. Compare it against how far the boards now sit from centre ice.
                CompetitiveAdjustments.ConfigManager.Log(
                    $"Revived {revived} of {candidates.Count} baked arena fixture(s) as realtime lights to stand " +
                    $"in for the lost bake, spread over {covered.size.x:F0} x {covered.size.z:F0} m " +
                    $"and {covered.size.y:F0} m tall. Set ReviveBakedArenaLights = false to go back to ambient only.");
            }
            else if (candidates.Count == 0)
            {
                CompetitiveAdjustments.ConfigManager.Log(
                    "No baked arena fixtures to revive; this arena's lighting is realtime or ambient already.");
            }
        }

        /// <summary>
        /// Picks fixtures strongest-first but refuses to place two closer than
        /// <see cref="RevivedLightSpacing"/>, so the budget buys coverage of the whole
        /// building instead of a cluster over centre ice. If spacing leaves the budget
        /// unspent, the remainder is filled with the strongest of what was passed over.
        /// </summary>
        private static List<Light> ChooseSpreadFixtures(List<Light> candidates)
        {
            var chosen = new List<Light>(MaxRevivedLights);
            var positions = new List<Vector3>(MaxRevivedLights);

            float spacing = RevivedLightSpacing * Mathf.Max(1f, _proxyWorldScale.x);
            float spacingSqr = spacing * spacing;

            for (int pass = 0; pass < 2 && chosen.Count < MaxRevivedLights; pass++)
            {
                bool enforceSpacing = pass == 0;

                for (int i = 0; i < candidates.Count && chosen.Count < MaxRevivedLights; i++)
                {
                    Light candidate = candidates[i];
                    if (candidate == null || chosen.Contains(candidate)) continue;

                    Vector3 position = candidate.transform.position;
                    if (enforceSpacing && IsCrowded(position, positions, spacingSqr)) continue;

                    chosen.Add(candidate);
                    positions.Add(position);
                }
            }

            return chosen;
        }

        private static bool IsCrowded(Vector3 position, List<Vector3> taken, float spacingSqr)
        {
            for (int i = 0; i < taken.Count; i++)
                if ((taken[i] - position).sqrMagnitude < spacingSqr) return true;

            return false;
        }

        /// <summary>
        /// Re-applies ArenaLightIntensityScale to the revived fixtures without rebuilding
        /// them. Always from the stored pre-scale value, never from the light's current
        /// intensity, so repeated calls cannot compound.
        /// </summary>
        private static void RescaleRevivedLights(float intensityScale)
        {
            _revivedIntensityScale = intensityScale;

            for (int i = 0; i < _revivedLights.Count; i++)
            {
                Light light = _revivedLights[i].Light;
                if (light != null) light.intensity = _revivedLights[i].BaseIntensity * intensityScale;
            }
        }

        private static void DestroyRevivedLights()
        {
            for (int i = 0; i < _revivedLights.Count; i++)
                if (_revivedLights[i].Object != null) UnityEngine.Object.Destroy(_revivedLights[i].Object);

            _revivedLights.Clear();
            _revivedIntensityScale = float.NaN;

            if (_revivedLightRoot != null)
            {
                UnityEngine.Object.Destroy(_revivedLightRoot.gameObject);
                _revivedLightRoot = null;
            }
        }

        private static bool IsMovableLight(Light light, Transform levelRoot)
        {
            Transform t = light.transform;

            // Player headlamps and any other networked light must stay put. The walk stops
            // at the level root because `Level` is itself a NetworkBehaviour, so running
            // past it would reject every light in the building.
            for (Transform current = t; current != null && current != levelRoot; current = current.parent)
            {
                if (current.GetComponent<Unity.Netcode.NetworkObject>() != null) return false;
                if (current.name.StartsWith("CompAdjust", StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static void RestoreArenaLights()
        {
            for (int i = 0; i < _adjustedLights.Count; i++)
            {
                Light light = _adjustedLights[i];
                if (light == null) continue;
                if (!_lightBaselines.TryGetValue(light.GetInstanceID(), out LightBaseline baseline)) continue;

                // Only the lights we actually moved get a position back. An under-root
                // light was never written, and its stored position was measured under a
                // scale the parent may have changed since, so writing it here is the one
                // way to displace a fixture that was correct all along.
                if (!baseline.UnderLevelRoot) light.transform.position = baseline.Position;
                light.range = baseline.Range;
                light.intensity = baseline.Intensity;
            }

            _adjustedLights.Clear();
            _lightBaselines.Clear();
            _lastReportedLights = -1;
            DestroyRevivedLights();
        }

        private static int _lastReportedLights = -1;

        private static void ReportLightsOnce(int moved, int skipped, int baked, float rangeScale, float intensityScale)
        {
            int signature = moved * 397 ^ skipped * 31 ^ baked;
            if (signature == _lastReportedLights) return;
            _lastReportedLights = signature;

            CompetitiveAdjustments.ConfigManager.Log(
                $"Arena lights: {moved} moved with the rink (range x{rangeScale:F2}, intensity x{intensityScale:F2}), " +
                $"{skipped} left alone (directional or networked), {baked} of the moved ones are BAKED and " +
                "contribute nothing at runtime. Tune ArenaLightIntensityScale in the client config.");
        }
    }
}
