// GoalFrameTweaks.cs - make the base-game goal frame follow the goal, no imported mesh.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DashFallMod
{
    public static partial class GoalNetTweaks
    {
        // The goal frame is statically batched, same as the rink surfaces (confirmed
        // in-game: scaling a goal stretches its Cloth net, which cannot be batched, and
        // leaves the posts sitting at vanilla size). So writing goal.transform.localScale
        // moves the colliders, the net and the trigger while the frame stays put, and the
        // bundled frame.prefab existed to paper over exactly that.
        //
        // It does not have to. The same treatment the rink gets works here: hand the
        // frame's batched renderers to ArenaProxyVisual with the world-space delta between
        // where the goal was baked and where it sits now. That delta picks up BOTH the
        // goal's own size scaling and the arena resize (the goals ride 'Level Default'),
        // because it is read straight off localToWorldMatrix.
        //
        // GoalThicknessScale rides along. It used to be physics only, scaling the capsule
        // radii on 'Goal Post Collider' while the posts looked unchanged. Each post is a
        // rod: one long local axis and two short ones. Scaling only the short pair in the
        // rod's own frame fattens it in place, which composes into the same draw matrix.
        // A welded single-mesh frame is not rod shaped, the aspect gate rejects it, and
        // thickness stays physics only, because there is nothing in one mesh to scale
        // independently.

        /// <summary>
        /// Long-axis to short-axis ratio a renderer must clear to be treated as a post.
        /// A goal post is around 24:1 and the crossbar around 36:1; a welded whole-frame
        /// mesh is nearer 2:1, so anything past 4 separates the two cases with room to
        /// spare.
        /// </summary>
        private const float MinRodAspect = 4f;

        private static readonly Dictionary<int, Vector3> _framePartBaseScale = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> _framePartBasePosition = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, int> _loggedFrameComposition = new Dictionary<int, int>();

        // ── Pivot-to-front measurement ───────────────────────────────────────────
        // The goal's PIVOT rides the resized level root, but SyncGoals holds the goal at
        // vanilla world SIZE, so the pivot-to-front vector does not scale while the painted
        // goal line does. Correcting that needs the vanilla offset from the pivot to the
        // plane of the posts, and it has to be a per-goal measurement rather than a
        // constant so a custom-scenery goal with a different frame depth still lands right.
        //
        // A regulation net is about 1.83 m wide and 1.12 m deep, pivot to post plane about
        // 1.15 m. The scene combined mesh that a batched MeshFilter points at is roughly
        // 67 x 13 x 105, so these windows separate "a goal" from "the whole rink" with an
        // order of magnitude to spare, and a measurement that lands outside them is thrown
        // away rather than trusted.
        private const float MinGoalFrontOffset      = 0.05f;
        private const float MaxGoalFrontOffset      = 3f;
        private const float MaxGoalFrameSpanX       = 8f;
        private const float MaxGoalFrameSpanZ       = 5f;
        private const float MaxGoalFrameCentreDrift = 3f;

        /// <summary>
        /// Half-width of the window the batched world-bounds cross-check allows, matching
        /// the 0.5 m ArenaProxyVisual.VerifyWorldSpaceAssumption uses on these same
        /// renderers. Applied as containment rather than centre distance, so a
        /// multi-material renderer whose first slice legitimately sits off the full
        /// renderer's centre is not rejected for it.
        /// </summary>
        private const float BatchedBoundsMargin = 0.5f;

        private const int GoalFrameLayerUnresolved = -2;
        private static int _goalFrameLayer = GoalFrameLayerUnresolved;
        private static readonly HashSet<int> _loggedFrontOffsetFailure = new HashSet<int>();

        /// <summary>
        /// Signed world-Z offset from a goal's VANILLA WORLD pivot to the front-most plane
        /// of its frame, measured once from bake-time geometry and cached on the goal's own
        /// ArenaBaselineMarker.
        ///
        /// Returns false when it cannot be measured, and the caller must then leave the goal
        /// exactly where today's code puts it: a guessed offset moves the net to a wrong
        /// place, which is worse than the misalignment it was meant to fix. A failure is
        /// deliberately NOT cached, so the next pass retries for free once the scene is
        /// fully built.
        /// </summary>
        internal static bool TryGetGoalFrontOffsetZ(Goal goal, ArenaBaselineMarker marker, out float frontOffsetZ)
        {
            frontOffsetZ = 0f;
            if (goal == null || marker == null) return false;
            if (marker.TryGetGoalFrontOffsetZ(out frontOffsetZ)) return true;

            if (!TryMeasureGoalFrontOffsetZ(goal, marker, out frontOffsetZ))
            {
                frontOffsetZ = 0f;
                if (_loggedFrontOffsetFailure.Add(goal.transform.GetInstanceID()))
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning(
                        $"Could not measure the front plane of '{goal.name}'s frame, so the goal keeps its " +
                        "vanilla pivot placement. On a resized rink the mouth of the net may sit off the " +
                        "painted goal line.");
                }
                return false;
            }

            marker.SetGoalFrontOffsetZ(frontOffsetZ);
            CompetitiveAdjustments.ConfigManager.Log(
                $"Goal '{goal.name}' pivot-to-front offset measured at {frontOffsetZ:F4} m on world Z.");
            return true;
        }

        /// <summary>
        /// The goal's world matrix as its frame was BAKED, rebuilt from vanilla baselines
        /// rather than read live.
        ///
        /// It has to be rebuilt because SyncGoals runs after ScaleLevelDefaultRoot, so by
        /// the time anything here is measured the live chain already carries the arena
        /// resize and, on a later pass, our own pin. This mod writes exactly two kinds of
        /// transform above a frame renderer, the level root and the goal roots, and both
        /// carry an ArenaBaselineMarker. Substituting the marker wherever there is one and
        /// reading the live local TRS everywhere else therefore reproduces the untouched
        /// scene exactly, at any arena scale and after any number of pins.
        ///
        /// Doing it this way is also what lets the offset be a true WORLD quantity without
        /// assuming the level root sits at the origin with unit scale. It does here (logged:
        /// baseline scale (1,1,1), pos (0,0,0)), but comparing a world-space bounds against
        /// a parent-LOCAL pivot would silently return root-local units the moment that
        /// stopped being true, and every sanity window below would still pass.
        ///
        /// The goal's own rotation is read live because we never write it.
        /// </summary>
        private static Matrix4x4 BakeTimeWorldMatrix(Transform goalRoot, ArenaBaselineMarker goalMarker)
        {
            Matrix4x4 m = Matrix4x4.TRS(goalMarker.BasePosition, goalRoot.localRotation, goalMarker.BaseScale);

            for (Transform ancestor = goalRoot.parent; ancestor != null; ancestor = ancestor.parent)
            {
                var marker = ancestor.GetComponent<ArenaBaselineMarker>();
                Vector3 position = marker != null ? marker.BasePosition : ancestor.localPosition;
                Vector3 scale    = marker != null ? marker.BaseScale    : ancestor.localScale;
                m = Matrix4x4.TRS(position, ancestor.localRotation, scale) * m;
            }

            return m;
        }

        private static bool TryMeasureGoalFrontOffsetZ(Goal goal, ArenaBaselineMarker marker, out float frontOffsetZ)
        {
            frontOffsetZ = 0f;

            Transform goalRoot = goal.transform;
            Matrix4x4 bakedWorld = BakeTimeWorldMatrix(goalRoot, marker);
            Vector3 pivotWorld = bakedWorld.GetColumn(3);

            // A goal sitting on the centre line has no determinable mouth direction. Reject
            // rather than guess it from the rotation: a wrong sign here moves the net the
            // wrong way by twice the error.
            if (Mathf.Abs(pivotWorld.z) < 0.001f) return false;

            if (_goalFrameLayer == GoalFrameLayerUnresolved)
            {
                // Resolved by name and logged, so the layer index cannot rot into a comment
                // that lies. b1117 has it at 18.
                _goalFrameLayer = LayerMask.NameToLayer("Goal Frame");
                CompetitiveAdjustments.ConfigManager.Log($"'Goal Frame' physics layer resolved to {_goalFrameLayer}.");
            }

            Transform netRoot = goal.NetCloth != null ? goal.NetCloth.transform : null;

            bool have = false;
            Bounds frame = default;

            // Pass 0 takes the dedicated 'Goal Frame' layer. Pass 1 is the fallback for a
            // scene that does not use it, and it matches on the object name rather than
            // unioning every renderer under the goal: a union of unknowns can stretch past
            // the post plane while still clearing every window below. The b1117 batched
            // renderer is literally named 'Goal Frame' (see the composition log), so the two
            // passes target the same object by two independent signals.
            for (int pass = 0; pass < 2 && !have; pass++)
            {
                if (pass == 0 && _goalFrameLayer < 0) continue;

                foreach (var renderer in goalRoot.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer == null) continue;

                    Transform t = renderer.transform;
                    if (t == goalRoot) continue;
                    if (netRoot != null && (t == netRoot || t.IsChildOf(netRoot))) continue;

                    // Our own collider debug brushes, which SyncArenaColliderDebugBrush
                    // parents under EVERY collider in the level root and gives the
                    // collider's own layer. They are stand-in cubes sized to a collider, not
                    // frame geometry, and a goal collider on the frame layer would hand pass
                    // 0 a box that inflates the union. Worse than the usual false positive,
                    // because the resulting offset is then cached on the marker for the rest
                    // of the session. LogArenaColliderHeights skips them by the same name.
                    if (string.Equals(renderer.gameObject.name, "__clipBrush", StringComparison.Ordinal)) continue;

                    bool accept = pass == 0
                        ? renderer.gameObject.layer == _goalFrameLayer
                        : renderer.name != null && renderer.name.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!accept) continue;

                    if (!TryGetBakeTimeWorldBounds(renderer, goalRoot, bakedWorld, out Bounds bounds)) continue;

                    if (!have) { frame = bounds; have = true; }
                    else frame.Encapsulate(bounds);
                }
            }

            if (!have) return false;

            Vector3 size = frame.size;
            if (size.x > MaxGoalFrameSpanX || size.z > MaxGoalFrameSpanZ) return false;
            if (Mathf.Abs(frame.center.z - pivotWorld.z) > MaxGoalFrameCentreDrift) return false;

            // Both goals face centre ice, so the front is whichever extreme is nearer z = 0.
            float offset = (pivotWorld.z > 0f ? frame.min.z : frame.max.z) - pivotWorld.z;

            if (Mathf.Sign(offset) == Mathf.Sign(pivotWorld.z)) return false;   // mouth must face centre ice

            float magnitude = Mathf.Abs(offset);
            if (magnitude < MinGoalFrontOffset || magnitude > MaxGoalFrontOffset) return false;

            frontOffsetZ = offset;
            return true;
        }

        /// <summary>
        /// One frame renderer's AABB in BAKE-TIME WORLD space.
        ///
        /// Batched: the vertices live in the scene combined mesh already in world space and
        /// the transform is ignored at draw time, which is the whole reason this file exists.
        /// So Renderer.bounds IS the bake-time world AABB, and nothing we write to any
        /// transform can move it. ArenaProxyVisual.ResolveWorldBounds treats it as exactly
        /// that on these same renderers. The immutable slice metadata is used as the
        /// world-space assertion, not as the value.
        ///
        /// MeshFilter.sharedMesh.bounds must NOT be used on a batched renderer: batching
        /// repoints the filter at the whole combined mesh. That is the same trap that keeps
        /// the rod-aspect test above permanently dead, which is why the composition log
        /// reports "0 rod-shaped" for a frame that visibly has posts.
        ///
        /// Unbatched: mesh-local bounds pushed through the bake-time world matrix and the
        /// chain up to the goal root, with the thickness pass's captured baselines
        /// substituted for any node it has written.
        /// </summary>
        private static bool TryGetBakeTimeWorldBounds(
            MeshRenderer renderer, Transform goalRoot, Matrix4x4 bakedWorld, out Bounds bounds)
        {
            bounds = default;

            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return false;

            if (renderer.isPartOfStaticBatch)
            {
                // "Bounds are the bake-time world AABB" holds only while the vertices are
                // baked in WORLD space. A renderer carrying a static batch root has them
                // baked in that root's space instead, so its bounds follow the root, and
                // ArenaVisualMode 'batchroot' points exactly these renderers at a root that
                // carries the whole arena delta. Refusing to measure costs the pin on that
                // one client-local mode; measuring anyway would cache an offset polluted by
                // the arena scale we are trying to correct for.
                if (ArenaProxyVisual.HasStaticBatchRoot(renderer)) return false;

                bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude <= 1e-8f) return false;

                int first = renderer.subMeshStartIndex;
                if (first < 0 || first >= mesh.subMeshCount) return false;

                Vector3 sliceCentre;
                try { sliceCentre = mesh.GetSubMesh(first).bounds.center; }
                catch { return false; }

                Bounds check = bounds;
                check.Expand(2f * BatchedBoundsMargin);   // Expand() takes total size, so this is 0.5 m per side
                return check.Contains(sliceCentre);
            }

            Matrix4x4 relative = Matrix4x4.identity;
            for (Transform t = renderer.transform; t != null && t != goalRoot; t = t.parent)
                relative = BaselineLocalTRS(t) * relative;

            bounds = TransformBounds(bakedWorld * relative, mesh.bounds);
            return bounds.size.sqrMagnitude > 1e-8f;
        }

        /// <summary>
        /// A frame part's local TRS as it was before the thickness pass touched it. Only rod
        /// parts are ever written, and only their scale and position, so anything absent
        /// from those dictionaries is still carrying its authored value.
        /// </summary>
        private static Matrix4x4 BaselineLocalTRS(Transform t)
        {
            int id = t.GetInstanceID();
            Vector3 position = _framePartBasePosition.TryGetValue(id, out Vector3 p) ? p : t.localPosition;
            Vector3 scale    = _framePartBaseScale.TryGetValue(id, out Vector3 s)    ? s : t.localScale;
            return Matrix4x4.TRS(position, t.localRotation, scale);
        }

        /// <summary>Local-space bounds through an arbitrary matrix, as an axis-aligned box.</summary>
        private static Bounds TransformBounds(Matrix4x4 m, Bounds local)
        {
            Vector3 centre = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            var extents = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);

            return new Bounds(centre, extents * 2f);
        }

        /// <summary>
        /// Drives the base game's own goal frame to match the goal's current transform,
        /// optionally thickening its posts. Returns true when the frame will look right
        /// without the bundled prefab: either it is not batched and follows on its own, or
        /// the proxy took it over.
        /// </summary>
        private static bool SyncBaseGoalFrame(
            Goal goal,
            ArenaProxyVisual.Mode mode,
            bool thicknessEnabled,
            float thicknessScale)
        {
            if (goal == null) return false;

            Transform goalRoot = goal.transform;
            int rootId = goalRoot.GetInstanceID();

            if (!_goalBaseScale.TryGetValue(rootId, out Vector3 baseScale)
                || !_goalBasePosition.TryGetValue(rootId, out Vector3 basePosition))
            {
                // Baselines are captured earlier in the same refresh; if they are missing
                // the goal has not been touched yet and nothing needs correcting.
                ArenaProxyVisual.ClearGroup(rootId);
                return true;
            }

            // Where this goal's geometry was baked. 'Level Default' is at world origin with
            // identity rotation, so the baked world matrix is just the goal's own captured
            // local TRS, and everything the arena resize adds shows up in localToWorldMatrix
            // and therefore in the delta. We never write the goal's rotation, so reading it
            // live is safe.
            Matrix4x4 baked = Matrix4x4.TRS(basePosition, goalRoot.localRotation, baseScale);
            Matrix4x4 bakedInverse = baked.inverse;
            Matrix4x4 goalNow = goalRoot.localToWorldMatrix;
            Matrix4x4 delta = goalNow * bakedInverse;

            Transform netRoot = goal.NetCloth != null ? goal.NetCloth.transform : null;
            bool wantThickness = thicknessEnabled && !Mathf.Approximately(thicknessScale, 1f);

            // Nothing moved and nothing to fatten: leave the batched frame exactly as the
            // game draws it, baked lighting and all. Proxying an identity delta would cost
            // the bake for a pixel-identical result.
            if (!wantThickness && IsNegligibleDelta(delta))
            {
                ArenaProxyVisual.ClearGroup(rootId);
                return true;
            }

            var targets = new List<ArenaProxyVisual.Target>(8);
            int total = 0, batched = 0, rods = 0;
            string firstBatched = null;

            foreach (var renderer in goalRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                Transform t = renderer.transform;
                if (t == goalRoot) continue;
                if (netRoot != null && (t == netRoot || t.IsChildOf(netRoot))) continue;

                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;   // cloth net and any skinned piece

                total++;

                // Mesh.bounds is stored metadata, so this reads correctly even on the
                // non-readable meshes the game ships.
                Bounds localBounds = mesh.bounds;
                bool isRod = TryGetRodLongAxis(localBounds.size, out int longAxis);
                if (isRod) rods++;

                var mr = renderer as MeshRenderer;
                if (mr != null && mr.isPartOfStaticBatch)
                {
                    batched++;
                    if (firstBatched == null)
                        firstBatched = $"'{mr.name}' owner={ArenaProxyVisual.DescribeOwner(mr)}";

                    Matrix4x4 matrix = delta;
                    if (isRod && wantThickness)
                    {
                        // Thickness has to happen in the rod's OWN frame, so it is bracketed
                        // by the rod's transform relative to the goal. That relative
                        // transform is invariant (we only ever write the goal root), so it
                        // doubles as the baked one and the whole thing collapses back to
                        // `delta` when the thickness matrix is identity.
                        Matrix4x4 relative = goalRoot.worldToLocalMatrix * t.localToWorldMatrix;
                        matrix = goalNow
                            * relative
                            * BuildThicknessMatrix(localBounds.center, longAxis, thicknessScale)
                            * relative.inverse
                            * bakedInverse;
                    }

                    targets.Add(new ArenaProxyVisual.Target { Renderer = mr, WorldDelta = matrix });
                    continue;
                }

                // Not batched: this renderer already follows the goal transform, so only
                // the thickness needs writing.
                ApplyRodThicknessToTransform(t, localBounds, isRod, longAxis, wantThickness, thicknessScale);
            }

            LogFrameCompositionOnce(rootId, total, batched, rods, firstBatched);

            if (batched == 0)
            {
                // Nothing frozen, so nothing to take over.
                ArenaProxyVisual.ClearGroup(rootId);
                return true;
            }

            return ArenaProxyVisual.SyncGroup(rootId, mode, targets, "goal frame");
        }

        /// <summary>
        /// Thickness for a rod that is NOT batched, applied straight to its transform. The
        /// local position is compensated by the amount the mesh bounds centre would have
        /// moved, so a pivot sitting off the rod's own axis cannot slide the post sideways
        /// instead of fattening it.
        /// </summary>
        private static void ApplyRodThicknessToTransform(
            Transform t, Bounds localBounds, bool isRod, int longAxis, bool wantThickness, float thicknessScale)
        {
            if (!isRod) return;

            int id = t.GetInstanceID();
            if (!_framePartBaseScale.ContainsKey(id))
            {
                _framePartBaseScale[id] = t.localScale;
                _framePartBasePosition[id] = t.localPosition;
            }

            Vector3 baseScale = _framePartBaseScale[id];
            Vector3 basePosition = _framePartBasePosition[id];

            Vector3 targetScale = baseScale;
            if (wantThickness)
            {
                for (int axis = 0; axis < 3; axis++)
                    if (axis != longAxis) targetScale[axis] = baseScale[axis] * thicknessScale;
            }

            Vector3 centreShift = Vector3.Scale(baseScale - targetScale, localBounds.center);
            Vector3 targetPosition = basePosition + t.localRotation * centreShift;

            if (!ApproxEqual(t.localScale, targetScale)) t.localScale = targetScale;
            if (!ApproxEqual(t.localPosition, targetPosition)) t.localPosition = targetPosition;
        }

        /// <summary>
        /// True when the goal has not meaningfully moved from where its frame was baked.
        /// A pure arena scale shows up in lossyScale rather than the translation column,
        /// because it scales about world origin, so both are checked. The shift tolerance
        /// is tight: a couple of centimetres between frame and posts reads worse on a goal
        /// than on the rink, but it still absorbs the ~1 cm ArenaOffsetY default.
        /// </summary>
        private static bool IsNegligibleDelta(Matrix4x4 delta)
        {
            const float scaleTolerance = 0.002f;
            const float shiftTolerance = 0.02f;

            Vector3 scale = delta.lossyScale;
            Vector3 shift = delta.GetColumn(3);

            return Mathf.Abs(scale.x - 1f) < scaleTolerance
                && Mathf.Abs(scale.y - 1f) < scaleTolerance
                && Mathf.Abs(scale.z - 1f) < scaleTolerance
                && Mathf.Abs(shift.x) < shiftTolerance
                && Mathf.Abs(shift.y) < shiftTolerance
                && Mathf.Abs(shift.z) < shiftTolerance
                && Quaternion.Angle(delta.rotation, Quaternion.identity) < 0.1f;
        }

        /// <summary>Scales a rod's two short axes about its own bounds centre.</summary>
        private static Matrix4x4 BuildThicknessMatrix(Vector3 centre, int longAxis, float thicknessScale)
        {
            var scale = Vector3.one;
            for (int axis = 0; axis < 3; axis++)
                if (axis != longAxis) scale[axis] = thicknessScale;

            return Matrix4x4.Translate(centre) * Matrix4x4.Scale(scale) * Matrix4x4.Translate(-centre);
        }

        /// <summary>
        /// True when the bounds describe a rod: one clearly dominant axis. Reports which
        /// axis is the length, so the caller knows which two to fatten.
        /// </summary>
        private static bool TryGetRodLongAxis(Vector3 size, out int longAxis)
        {
            longAxis = 0;
            if (size.y > size[longAxis]) longAxis = 1;
            if (size.z > size[longAxis]) longAxis = 2;

            float longest = size[longAxis];
            float widest = 0f;
            for (int axis = 0; axis < 3; axis++)
                if (axis != longAxis) widest = Mathf.Max(widest, size[axis]);

            if (longest <= 0.0001f || widest <= 0.0001f) return false;
            return longest / widest >= MinRodAspect;
        }

        // Re-reported whenever the shape of the scan changes, so a run that happens before
        // the goals are fully built does not become the permanent record.
        private static void LogFrameCompositionOnce(
            int goalRootId, int total, int batched, int rods, string firstBatched)
        {
            int signature = (total * 73856093) ^ (batched * 19349663) ^ (rods * 83492791);
            if (_loggedFrameComposition.TryGetValue(goalRootId, out int previous) && previous == signature) return;
            _loggedFrameComposition[goalRootId] = signature;

            CompetitiveAdjustments.ConfigManager.Log(
                $"Base goal frame: {total} mesh renderer(s), {batched} statically batched, {rods} rod-shaped" +
                (firstBatched != null ? $", first {firstBatched}" : string.Empty) + ". " +
                (batched > 0
                    ? "Batched parts are proxy-drawn so the frame follows the goal."
                    : "Nothing batched, so the frame follows its transform on its own.") +
                (rods > 0
                    ? " GoalThicknessScale drives post thickness visually."
                    : " GoalThicknessScale stays physics-only (no rod-shaped posts found)."));
        }
    }
}
