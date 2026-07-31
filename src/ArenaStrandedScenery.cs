// ArenaStrandedScenery.cs - move the arena scenery the batched-geometry proxy cannot reach.

using System;
using System.Collections.Generic;
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
        // The crowd is group 3, which is why the seats around them moved and the people
        // did not: seats are batched, people are SkinnedMeshRenderers in their own root,
        // so they stayed at vanilla position and ended up hovering over a widened rink.
        //
        // These are ordinary objects with live transforms, so no proxy trickery is needed:
        // map the world position through the same delta the batched geometry gets and the
        // crowd sits in the stands again instead of floating over the ice.
        //
        // Three rules, each learned from a specific way this went wrong:
        //
        //   Never walk per renderer. That reaches individual BONES of players and crowd
        //   members, and writing a bone transform is wrong twice over: the animation
        //   overwrites it every frame, and the baseline gets captured from whatever pose
        //   was current. A log line reading "972 renderer(s) moved by hand" was this.
        //
        //   Follow the CONTAINER every frame, not a list of members at rebuild time. The
        //   crowd is pooled, so a captured list is stale within seconds and a member
        //   spawned after a rebuild sits at its vanilla position until the next one. On a
        //   two second cadence that is "the crowd is teleporting around", and members
        //   caught mid-cycle over the widened ice are the dark shapes on the rink.
        //
        //   Position only, no scale. Scaling is what would spread a crowd to fill widened
        //   stands, but it stretches each spectator by the same factors, and at
        //   ArenaScaleZ 3 that is a stand full of three-times-too-tall people.



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

        private static void SyncStrandedScenery(Matrix4x4 delta, List<Transform> containers)
        {
            if (containers == null || containers.Count == 0)
            {
                RestoreStrandedScenery();
                return;
            }

            EnsureSceneryFollower();
            _sceneryFollower.Containers = containers.ToArray();
            _sceneryFollower.Delta = delta;
            _sceneryFollower.Hide = ResolveHideArenaCrowd();

            ReportSceneryOnce(containers.Count, _sceneryFollower.Hide ? 1 : 0);
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
        /// Keeps pooled scenery sitting where the resized arena puts it, every frame.
        ///
        /// A one-shot pass at rebuild time cannot work here. The crowd is POOLED: members
        /// are created and destroyed continuously, so anything captured at rebuild time is
        /// stale within seconds, and a member spawned just after a rebuild sits at its
        /// vanilla position until the next one. On a two second rebuild cadence that is
        /// exactly what "the crowd is teleporting around" looks like, and members caught
        /// mid-cycle over the widened ice are the dark shapes on the rink.
        ///
        /// Following the container instead means a new member is placed the frame it
        /// appears. Baselines are keyed by instance id: a pooled object reactivated keeps
        /// its id and its baseline, while a genuinely new one is captured fresh at its
        /// vanilla spawn position, which is what we want in both cases.
        /// </summary>
        private sealed class SceneryFollower : MonoBehaviour
        {
            internal Transform[] Containers = Array.Empty<Transform>();
            internal Matrix4x4 Delta = Matrix4x4.identity;
            internal bool Hide;

            private readonly Dictionary<int, Vector3> _baselines = new Dictionary<int, Vector3>();

            // Exactly the children this component switched off, and nothing else. The
            // crowd is POOLED, so a container's children include members the pool is
            // deliberately keeping inactive; a blanket SetActive(true) on unhide would put
            // spectators on screen that the game had parked, which is a worse artefact
            // than the one it is undoing.
            private readonly HashSet<int> _hidden = new HashSet<int>();
            private float _nextPrune;

            internal void ReleaseAll()
            {
                UnhideAll();

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

            /// <summary>Re-enables only what this component hid.</summary>
            internal void UnhideAll()
            {
                if (_hidden.Count == 0) return;

                for (int c = 0; c < Containers.Length; c++)
                {
                    Transform container = Containers[c];
                    if (container == null) continue;

                    for (int i = 0; i < container.childCount; i++)
                    {
                        Transform child = container.GetChild(i);
                        if (_hidden.Contains(child.GetInstanceID()) && !child.gameObject.activeSelf)
                            child.gameObject.SetActive(true);
                    }
                }

                _hidden.Clear();
            }

            private void LateUpdate()
            {
                if (Containers.Length == 0) return;

                // HideArenaCrowd is a live client toggle, so the off transition has to be
                // handled here. Without it the hide was one-way: turning the setting back
                // off stopped hiding NEW members but left every already-hidden spectator
                // switched off until the arena resize was torn down or the game restarted.
                if (!Hide) UnhideAll();

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
                            // First sight is the vanilla placement, before we touch it.
                            baseline = child.position;
                            _baselines[id] = baseline;
                        }

                        if (Hide)
                        {
                            if (child.gameObject.activeSelf)
                            {
                                child.gameObject.SetActive(false);
                                _hidden.Add(id);
                            }
                            continue;
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

            // Pooled members die off; without this the baseline map grows for the session.
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

                // Same treatment for the hide set, or it keeps ids of pooled members that
                // have since been destroyed and grows for the session alongside the map it
                // was added to fix.
                _hidden.RemoveWhere(id => !live.Contains(id));
            }
        }

        private static void RestoreStrandedScenery()
        {
            // ReleaseAll unhides what the hide path switched off, then puts every tracked
            // child back on its vanilla position. It deliberately does NOT re-enable
            // everything it can see: the crowd is pooled, and members the pool had parked
            // must stay parked.
            _sceneryFollower?.ReleaseAll();

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
