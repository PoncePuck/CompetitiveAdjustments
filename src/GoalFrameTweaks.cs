// GoalFrameTweaks.cs - make the base-game goal frame follow the goal, no imported mesh.

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
