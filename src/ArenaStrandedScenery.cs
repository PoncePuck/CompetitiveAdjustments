// ArenaStrandedScenery.cs - move the arena scenery the batched-geometry proxy cannot reach.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DashFallMod
{
    public static partial class GoalNetTweaks
    {
        // Resizing the arena splits the scenery into three groups, and only two of them
        // were handled before this file existed:
        //
        //   1. Statically batched geometry (boards, hangar shell, seats). Frozen at bake
        //      time, redrawn by ArenaProxyVisual under the resize matrix.
        //   2. Non-batched children of 'Level Default'. Already scaled, because
        //      ScaleLevelDefaultRoot scales the root they hang off.
        //   3. Non-batched objects parked in some OTHER scene root. Nothing touches these.
        //
        // The crowd looks like group 3 and is NOT. See CrowdSeating below: the game already
        // places every member correctly, and this file's job for the crowd is to leave it
        // alone and, at most, put a stale member back on its own seat.
        //
        // Two rules for the scenery this file DOES move, each learned from a specific way
        // it went wrong:
        //
        //   Never walk per renderer. That reaches individual BONES of players and crowd
        //   members, and writing a bone transform is wrong twice over: the animation
        //   overwrites it every frame, and the baseline gets captured from whatever pose
        //   was current. A log line reading "972 renderer(s) moved by hand" was this.
        //
        //   Position only, no scale. Scaling is what would spread scenery to fill a widened
        //   building, but it stretches each object by the same factors, and at a 3x vertical
        //   scale that is a room full of three-times-too-tall furniture.
        //
        // WHAT WAS WRONG WITH THE CROWD, decompiled from Puck.dll rather than assumed:
        //
        //   CrowdManager.RegisterCrowdPosition does
        //     Object.Instantiate(prefab, position.transform.position, position.transform.rotation, base.transform)
        //   so a member is born AT ITS SEAT MARKER'S CURRENT WORLD POSITION, parented to the
        //   manager. CrowdPosition.Start() is what registers the seat, and the markers live
        //   under 'Level Default'. So by the time a member exists, the marker has already
        //   been moved by the arena resize and the member is already in the right place.
        //   Mapping it through the delta AGAIN is what threw the crowd out over the ice as
        //   giant scattered shapes: the delta was applied twice.
        //
        //   There is also NO POOLING, which several comments here used to assert as the
        //   reason for the whole design. UnregisterCrowdPosition calls Object.Destroy and
        //   Register calls Object.Instantiate. Nothing is reused, so "a pooled object keeps
        //   its id and its baseline" was never true and the baseline-by-instance-id scheme
        //   was solving a problem that does not exist.



        /// <summary>
        /// Client-local. Hides the crowd outright instead of moving it. Worth reaching for
        /// at large arena scales, where the crowd is stretched by the same factors as the
        /// building and a 3x vertical scale turns spectators into distorted giants.
        /// </summary>
        private static bool ResolveHideArenaCrowd()
        {
            try
            {
                return DashFallMod.Client.DashFallConfigLoader.ClientConfig?.HideArenaCrowd == true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Keeps crowd members sitting on their own seats, and hides them when asked.
        ///
        /// This replaces mapping the crowd through the arena delta, which double-applied it
        /// (see the file header). The authority is the game's own
        /// CrowdManager.crowdPositionMemberMap: every entry pairs a CrowdPosition marker
        /// with the member instantiated for it, and the marker rides the scaled level root,
        /// so the marker's CURRENT world position is by definition the correct seat at the
        /// current arena scale.
        ///
        /// Reading that instead of applying a transform of our own makes the pass idempotent
        /// and impossible to double-apply: running it twice, or running it on a member the
        /// game already placed correctly, is a no-op. It also fixes the case the delta could
        /// never handle, a member created BEFORE a live arena rescale, which stayed at the
        /// old seat because its baseline was captured there.
        /// </summary>
        private static class CrowdSeating
        {
            private static FieldInfo _mapField;
            private static bool _probed;
            private static bool _warned;

            private static readonly HashSet<int> _hidden = new HashSet<int>();

            internal static Transform ManagerTransform =>
                CrowdManager.Instance != null ? CrowdManager.Instance.transform : null;

            private static FieldInfo ResolveMapField()
            {
                if (_probed) return _mapField;
                _probed = true;

                _mapField = typeof(CrowdManager).GetField(
                    "crowdPositionMemberMap", BindingFlags.Instance | BindingFlags.NonPublic);

                if (_mapField == null && !_warned)
                {
                    _warned = true;
                    CompetitiveAdjustments.ConfigManager.LogWarning(
                        "CrowdManager.crowdPositionMemberMap not found, so crowd members cannot be re-seated after a " +
                        "live arena rescale. Newly spawned members are still placed correctly by the game; only ones " +
                        "that already existed when the scale changed will sit on their old seats until they respawn.");
                }

                return _mapField;
            }

            internal static void Sync(bool hide)
            {
                var manager = CrowdManager.Instance;
                if (manager == null) return;

                var field = ResolveMapField();
                if (field == null) return;

                if (!(field.GetValue(manager) is IDictionary map)) return;

                foreach (DictionaryEntry entry in map)
                {
                    var seat = entry.Key as Component;
                    var member = entry.Value as Component;
                    if (seat == null || member == null) continue;

                    var memberTransform = member.transform;
                    int id = memberTransform.gameObject.GetInstanceID();

                    if (hide)
                    {
                        if (memberTransform.gameObject.activeSelf)
                        {
                            memberTransform.gameObject.SetActive(false);
                            _hidden.Add(id);
                        }
                        continue;
                    }

                    // Only what this code switched off, so members the game itself disabled
                    // stay disabled.
                    if (_hidden.Remove(id) && !memberTransform.gameObject.activeSelf)
                        memberTransform.gameObject.SetActive(true);

                    Vector3 target = seat.transform.position;
                    if ((memberTransform.position - target).sqrMagnitude > 0.000001f)
                        memberTransform.position = target;
                }
            }

            /// <summary>Undoes the hide. Seating needs no undo: the seats ARE the vanilla answer.</summary>
            internal static void Release()
            {
                if (_hidden.Count == 0) return;

                var manager = CrowdManager.Instance;
                var field = manager != null ? ResolveMapField() : null;
                if (field != null && field.GetValue(manager) is IDictionary map)
                {
                    foreach (DictionaryEntry entry in map)
                    {
                        if (!(entry.Value is Component member) || member == null) continue;
                        if (_hidden.Contains(member.gameObject.GetInstanceID()) && !member.gameObject.activeSelf)
                            member.gameObject.SetActive(true);
                    }
                }

                _hidden.Clear();
            }
        }

        /// <summary>The CrowdManager's transform, so the scenery scan can leave its members alone.</summary>
        private static Transform CrowdSeatingManagerTransform => CrowdSeating.ManagerTransform;

        private static void SyncStrandedScenery(Matrix4x4 delta, List<Transform> containers)
        {
            // The follower runs even with no containers, because the crowd pass is driven
            // from its LateUpdate and the crowd no longer produces containers at all.
            EnsureSceneryFollower();
            _sceneryFollower.Hide = ResolveHideArenaCrowd();

            if (containers == null || containers.Count == 0)
            {
                _sceneryFollower.ReleaseContainers();
                ReportSceneryOnce(0, _sceneryFollower.Hide ? 1 : 0);
                return;
            }

            // Hand the previous set back before adopting a new one. The follower's baselines
            // are keyed by child instance id, and a child that drops out of the set is
            // simply never written again, so it stays frozen wherever the last delta put it
            // with nothing left to restore it. Releasing first is what makes a change in
            // what counts as a container a clean swap instead of a leak.
            //
            // Gated on the set actually differing: the proxy rescans every two seconds and
            // an unconditional release would restore and re-apply the whole crowd on that
            // cadence, which is visible as jitter.
            var next = containers.ToArray();
            if (!SameContainers(_sceneryFollower.Containers, next)) _sceneryFollower.ReleaseContainers();

            _sceneryFollower.Containers = next;
            _sceneryFollower.Delta = delta;

            ReportSceneryOnce(containers.Count, _sceneryFollower.Hide ? 1 : 0);
        }

        /// <summary>
        /// Set comparison, not sequence comparison. The container list is built in whatever
        /// order FindObjectsByType(..., FindObjectsSortMode.None) happened to return the
        /// first renderer of each one, and that order is explicitly unspecified. Comparing
        /// positionally would report "changed" on a reshuffle and release and re-apply every
        /// container on the two second rescan, which is visible as jitter.
        /// </summary>
        private static bool SameContainers(Transform[] a, Transform[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var seen = new HashSet<int>();
            for (int i = 0; i < a.Length; i++) if (a[i] != null) seen.Add(a[i].GetInstanceID());
            for (int i = 0; i < b.Length; i++) if (b[i] == null || !seen.Contains(b[i].GetInstanceID())) return false;

            return true;
        }

        private static SceneryFollower _sceneryFollower;

        private static void EnsureSceneryFollower()
        {
            if (_sceneryFollower != null) return;

            var go = new GameObject("CompAdjustSceneryFollower");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _sceneryFollower = go.AddComponent<SceneryFollower>();
        }

        /// <summary>
        /// Keeps genuinely stranded scenery sitting where the resized arena puts it, and
        /// drives the crowd pass every frame.
        ///
        /// A one-shot pass at rebuild time cannot work: objects appear and disappear between
        /// rebuilds, so anything captured at rebuild time is stale within seconds and a new
        /// object sits at its vanilla position until the next pass. On a two second cadence
        /// that reads as scenery teleporting around. Following the CONTAINER instead means a
        /// new child is placed the frame it appears.
        ///
        /// The crowd is deliberately NOT handled here; see CrowdSeating for why applying the
        /// delta to it was wrong.
        /// </summary>
        private sealed class SceneryFollower : MonoBehaviour
        {
            internal Transform[] Containers = Array.Empty<Transform>();
            internal Matrix4x4 Delta = Matrix4x4.identity;
            internal bool Hide;

            private readonly Dictionary<int, Vector3> _baselines = new Dictionary<int, Vector3>();
            private float _nextPrune;

            /// <summary>Puts every tracked child back on its vanilla position and forgets it.</summary>
            internal void ReleaseContainers()
            {
                for (int c = 0; c < Containers.Length; c++)
                {
                    Transform container = Containers[c];
                    if (container == null) continue;

                    for (int i = 0; i < container.childCount; i++)
                    {
                        Transform child = container.GetChild(i);
                        if (_baselines.TryGetValue(child.GetInstanceID(), out Vector3 baseline))
                            child.position = baseline;
                    }
                }

                Containers = Array.Empty<Transform>();
                _baselines.Clear();
            }

            private void LateUpdate()
            {
                // Runs regardless of the container list, because the crowd produces no
                // containers now and this is what re-seats it. Hide is a live client toggle,
                // so both transitions are handled inside.
                CrowdSeating.Sync(Hide);

                if (Containers.Length == 0) return;

                for (int c = 0; c < Containers.Length; c++)
                {
                    Transform container = Containers[c];
                    if (container == null) continue;

                    for (int i = 0; i < container.childCount; i++)
                    {
                        Transform child = container.GetChild(i);
                        int id = child.GetInstanceID();

                        if (!_baselines.TryGetValue(id, out Vector3 baseline))
                        {
                            // First sight is the vanilla placement, before we touch it. Sound
                            // only because this no longer runs on the crowd: a crowd member is
                            // born at an ALREADY-SCALED seat, so capturing its first position
                            // as "vanilla" and applying the delta doubled the resize.
                            baseline = child.position;
                            _baselines[id] = baseline;
                        }

                        Vector3 target = Delta.MultiplyPoint3x4(baseline);
                        if ((child.position - target).sqrMagnitude > 0.000001f) child.position = target;
                    }
                }

                if (Time.unscaledTime >= _nextPrune)
                {
                    _nextPrune = Time.unscaledTime + 10f;
                    Prune();
                }
            }

            // Objects die off; without this the baseline map grows for the session.
            private void Prune()
            {
                if (_baselines.Count < 512) return;

                var live = new HashSet<int>();
                for (int c = 0; c < Containers.Length; c++)
                {
                    Transform container = Containers[c];
                    if (container == null) continue;
                    for (int i = 0; i < container.childCount; i++)
                        live.Add(container.GetChild(i).GetInstanceID());
                }

                var dead = new List<int>();
                foreach (var kv in _baselines)
                    if (!live.Contains(kv.Key)) dead.Add(kv.Key);

                for (int i = 0; i < dead.Count; i++) _baselines.Remove(dead[i]);
            }
        }

        private static void RestoreStrandedScenery()
        {
            // Two separate undos. The crowd only ever needs unhiding, because its seating
            // comes from the game's own markers and is already correct at any scale; the
            // generic containers need their children put back on the vanilla positions this
            // code moved them off. Neither re-enables anything it did not switch off, so
            // members the game itself disabled stay disabled.
            CrowdSeating.Release();
            _sceneryFollower?.ReleaseContainers();

            _lastReportedScenery = -1;
        }

        /// <summary>Undoes everything this file did. Called when arena tweaks go off.</summary>
        private static void RestoreAllStrandedScenery()
        {
            // The follower is a DontDestroyOnLoad MonoBehaviour that rewrites its
            // containers' children EVERY LateUpdate from a stored Delta, so it does not
            // stop just because the code that fed it stopped running. Leaving it here left
            // the crowd pinned to the last resize forever: turning arena tweaks off, or
            // simply putting the scales back to 1, restored the rink and left the
            // spectators floating over the ice with no way back short of a restart. Same
            // for HideArenaCrowd, which is a SetActive(false) the follower reapplies.
            RestoreStrandedScenery();

            RestoreArenaLights();

            // Only on the way out, never on the rebuild-per-config-change path: handing
            // the scenery back and taking it again would reparent a whole building twice
            // per keystroke in the arena settings.
            ReleaseSceneryScaler();
        }

        private static int _lastReportedScenery = -1;

        private static void ReportSceneryOnce(int containers, int hidden)
        {
            int signature = containers * 397 ^ hidden;
            if (signature == _lastReportedScenery) return;
            _lastReportedScenery = signature;

            CompetitiveAdjustments.ConfigManager.Log(
                $"Arena scenery outside the scaled root: following {containers} container(s) every frame" +
                (hidden != 0 ? " and hiding their contents" : string.Empty) +
                ". Set HideArenaCrowd = true in the client config to remove the crowd instead.");
        }
    }
}
