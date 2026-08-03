using CompetitivePuckTweaks.src;
using DashFallMod.Net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DashFallMod
{
    public static partial class GoalNetTweaks
    {
        // ── Arena visuals + colliders (unified prefab) ────────────────────────
        // The bundle now contains a single "ArenaAndColliders" prefab whose
        // hierarchy is:
        //   ArenaAndColliders          ← visual root (Barrier, Glass, Ice, …)
        //     └─ Colliders             ← child that holds Back/Front/Left/Right/Top/Bottom/Barrier Colliders
        //
        // We instantiate one copy, steal materials from the original arena for
        // the visual children, assign Ice / Boards layers to the Colliders
        // children, and disable the originals.
        // Legacy split arena.prefab + Colliders.prefab is still supported as a fallback.

        private const string UnifiedInstanceName = "CustomArenaAndColliders";
        private const string CollidersChildName  = "Colliders";

        // ── Arena network bounds + audio environment state ────────────────────
        private static AudioReverbZone _cachedReverbZone;
        private static float   _originalReverbMaxDistance = -1f;
        private static float   _originalReverbMinDistance = -1f;
        private static Vector3 _originalReverbPosition;
        private static bool    _originalReverbActive;
        private static int     _reverbBaselineZoneId;
        private static bool    _loggedVanillaServerSkip;

        // Vanilla board PhysicsMaterial, captured lazily from the original arena
        // colliders so rebuilt custom boards can fall back to stock bounce when
        // soft boards are disabled. Resolved-flag distinguishes "not looked yet"
        // from "looked, none found".

        // ── Hybrid arena resize ───────────────────────────────────────────────
        // The base-game arena/hangar visual surfaces are STATICALLY BATCHED (baked
        // into a scene-root "Combined Mesh", non-readable), so a mod cannot move or
        // resize them at runtime. Their COLLIDERS, the goals, the faceoff/puck spawn
        // markers and the bounds source are NOT batched and all live under the
        // 'Level Default' scene root, which is at world origin and identity rotation.
        // So scaling that single transform resizes the entire GAMEPLAY layer for free,
        // shear-free. We therefore:
        //   (1) scale 'Level Default' by the config -> real collision + goals + spawns, and
        //   (2) keep the bundled arena prefab purely as the scalable VISUAL (the only
        //       resizable rink surface there is), driven by the SAME config so it stays
        //       locked to the resized base, and hide the frozen base rink visuals.
        // FindArenaRoot() returns the 'Rink' node; its .root is 'Level Default'.

        // Level Default resize state (baseline captured before the first scale).
        private static Transform _scaledLevelRoot;
        private static int       _scaledLevelRootId;
        private static Vector3   _levelRootBaseScale = Vector3.one;
        private static Vector3   _levelRootBasePos   = Vector3.zero;

        private static void SyncArenaVisuals(
            bool enabled,
            float width,    // world X
            float height,   // world Y
            float length,   // world Z
            float offsetX,
            float offsetY,
            float offsetZ)
        {
            // Only EnableArenaTweaks tears the resize down. The visual mode must never do
            // it: a dedicated server resolves to Off because it renders nothing, and
            // treating that as "disabled" left the server on vanilla-sized collision while
            // every client played a resized rink.
            //
            // Checked BEFORE FindArenaRoot, which is the expensive part of this method: a
            // full-scene FindObjectsByType<Transform> plus a .name read per hit, and
            // Object.name allocates a fresh managed string every time. This runs off a
            // PlayerBodyV2.OnNetworkPostSpawn postfix, so a faceoff that respawns twelve
            // bodies used to pay twelve whole-scene scans in a single frame, even for
            // users with arena tweaks turned off. The teardown below works from the
            // cached _scaledLevelRoot and never needed the scan.
            if (!enabled)
            {
                _proxyWanted = false;
                ArenaProxyVisual.Clear();
                RestoreAllStrandedScenery();
                RestoreLevelDefaultScale();
                RemoveNetworkBoundsPatches();
                RestoreAudioEnvironment();
                return;
            }

            var arenaRoot = FindArenaRoot();                                    // 'Rink'
            Transform levelRoot = arenaRoot != null ? arenaRoot.root : null;    // 'Level Default'

            if (arenaRoot == null || levelRoot == null)
            {
                if (!_loggedArenaRootMissing)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Could not find arena/Level Default root in scene; arena resize not applied.");
                    _loggedArenaRootMissing = true;
                }
                return;
            }
            _loggedArenaRootMissing = false;

            ApplyNetworkBoundsPatches();
            HandleAudioEnvironment(width, height, length, offsetX, offsetY, offsetZ);

            // (1) Resize the REAL base game (colliders + goals + spawn markers). Not a
            // rendering step, so it runs on a dedicated server too. Skipping it there put
            // the goals at vanilla positions on the server while their nets drew scaled on
            // every client, which is the goal collider mismatch.
            ScaleLevelDefaultRoot(levelRoot, width, height, length, offsetX, offsetY, offsetZ);

            // (2) Everything the resize implies. Transform work inside runs everywhere;
            // the render-only parts gate themselves on the visual mode.
            SyncProxyVisual(ResolveArenaVisualMode(), arenaRoot, width, height, length, offsetX, offsetY, offsetZ);
        }


        /// <summary>
        /// Tears the whole visual layer back down to vanilla so the next pass can build it
        /// from scratch.
        ///
        /// Every piece of this feature keeps state that survives a config change: which
        /// renderers a proxy group owns and has disabled, which the prefab path hid, the
        /// captured baselines for hand-moved scenery and lights, the revived fixtures.
        /// Diffing new config into that state is how a stale group ends up drawing the same
        /// geometry alongside a fresh one, which reads in-game as a doubled goal frame that
        /// only a restart clears. Rebuilding from vanilla makes a config save behave
        /// exactly like a fresh session, at the cost of one frame of churn.
        /// </summary>
        private static void InvalidateVisualState()
        {
            ArenaProxyVisual.Clear();
            _lastReportedBatched = -1;
        }

        // Client-local choice of where the rink visual comes from. Off means "use the
        // bundled prefab". A dedicated server renders nothing, so it always takes the
        // untouched legacy path.
        private static ArenaProxyVisual.Mode ResolveArenaVisualMode()
        {
            if (ArenaProxyVisual.IsHeadless()) return ArenaProxyVisual.Mode.Off;

            try
            {
                return ArenaProxyVisual.ParseMode(
                    DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ArenaVisualMode);
            }
            catch { }

            return ArenaProxyVisual.Mode.DrawMesh;
        }

        // (2a) No-asset visual: rescale the base game's own geometry so it matches the
        // resized collision. See src/ArenaProxyVisual.cs for why statically batched
        // geometry needs a trick at all.
        //
        // The scale is EXACTLY the one fed to 'Level Default' on all three axes, so the
        // visual cannot drift from collision, and no ArenaBaseScaleCorrection is involved:
        // this IS the base geometry, so 1.0 means base size.
        //
        // Returns false when nothing could be proxied, leaving the prefab path to run.
        private static bool SyncProxyVisual(
            ArenaProxyVisual.Mode mode,
            Transform arenaRoot,
            float width, float height, float length,
            float offsetX, float offsetY, float offsetZ)
        {
            _proxyWanted = false;

            var worldScale  = new Vector3(width, height, length);
            var worldOffset = new Vector3(offsetX, offsetY, offsetZ);

            // At vanilla size there is nothing to move, and proxying anyway would trade
            // the rink's baked lightmaps for a pixel-identical shape. The tolerance
            // absorbs the ~1 cm ArenaOffsetY default, which is not worth the bake.
            if (IsVisuallyUnresized(worldScale, worldOffset))
            {
                RestoreAllStrandedScenery();
                ArenaProxyVisual.ClearGroup(ArenaProxyVisual.ArenaGroupKey);
                return true;
            }

            _proxyWanted = true;
            _proxyMode = mode;
            _proxyArenaRoot = arenaRoot;
            _proxyWorldScale = worldScale;
            _proxyWorldOffset = worldOffset;

            return TryApplyArenaProxy();
        }

        // Proxy state kept so the 1 Hz runner can retry and re-scan without a full
        // RefreshAll. Without the retry, one early attempt that finds no batched renderers
        // latches the bundled prefab in for the rest of the session, because RefreshAll
        // only does real work when the config hash changes.
        private static bool _proxyWanted;
        private static ArenaProxyVisual.Mode _proxyMode;
        private static Transform _proxyArenaRoot;
        private static Vector3 _proxyWorldScale = Vector3.one;
        private static Vector3 _proxyWorldOffset;
        private static float _nextProxyRescan;

        /// <summary>
        /// Re-scan for batched geometry every couple of seconds while the proxy is wanted.
        /// Two jobs: retry after an attempt that ran before the scene was ready, and pick
        /// up geometry that only appears later, such as a crowd enabled at warmup. The
        /// scan is the only cost; an unchanged result short-circuits on the group
        /// signature without touching a single renderer.
        /// </summary>
        internal static void TickArenaProxyRescan()
        {
            if (!_proxyWanted || _proxyArenaRoot == null) return;

            // Ahead of the rescan gate and on its own faster cadence, because a scenery swap
            // destroys the loaded building and instantiates a new one. Waiting for the next
            // rescan would leave that new building at vanilla size around a resized rink for
            // up to two seconds, and the cost of checking is one static field read.
            SyncSceneryLoaderArena(_proxyWorldScale, _proxyWorldOffset);

            if (Time.unscaledTime < _nextProxyRescan) return;
            _nextProxyRescan = Time.unscaledTime + 2f;

            TryApplyArenaProxy();
        }

        private static bool TryApplyArenaProxy()
        {
            if (!_proxyWanted || _proxyArenaRoot == null) return false;

            var delta = Matrix4x4.TRS(_proxyWorldOffset, Quaternion.identity, _proxyWorldScale);

            // Before the scan, not after. This parents loaded scenery under a scaler, and
            // the scan skips everything under that scaler. Run it second and a scenery
            // prefab that carries baked static batching spends the gap between two rescans
            // scaled twice, once by the scaler and once by the proxy.
            SyncSceneryLoaderArena(_proxyWorldScale, _proxyWorldOffset);

            var targets = CollectArenaProxyTargets(delta, out var stranded);

            // ── Rendering: clients only ──────────────────────────────────────────
            // A dedicated server draws nothing, so the proxy draws, the crowd and the
            // lights have no work to do there.
            if (_proxyMode == ArenaProxyVisual.Mode.Off) return true;

            // Built before the scenery and light passes so they can read this build's
            // lightmap coverage. The light pass in particular decides whether to stand in
            // for a lost bake, and answering that from the PREVIOUS build's numbers means
            // it is always one config change behind.
            bool proxied = ArenaProxyVisual.SyncGroup(
                ArenaProxyVisual.ArenaGroupKey, _proxyMode, targets, "arena");

            // Scenery the proxy cannot reach because it is not batched and not parented to
            // the scaled level root. A crowd of SkinnedMeshRenderers is the case in point:
            // the seats around them are batched and move, the people are not and do not,
            // so they end up sitting in mid-air over the ice.
            SyncStrandedScenery(delta, stranded);

            // Lights are not renderers, so nothing above reaches them: the fixtures move
            // with the ceiling and the light stays where it was baked.
            SyncArenaLights(delta, _proxyArenaRoot.root);

            return proxied;
        }

        // EVERYTHING static batching has frozen, not just the rink surfaces. Scaling the
        // ice while the hangar shell, stands, crowd and ceiling stay at vanilla size
        // leaves the boards punching through the building, so the whole baked world moves
        // together. That is also why this does not reuse ShouldHideOriginalArenaRenderer:
        // that predicate exists to pick what the bundled prefab REPLACES, and it
        // deliberately excludes the surroundings the prefab has no geometry for.
        //
        // Two exclusions. Goals get their own proxy group per goal, because their delta
        // also has to carry GoalSizeScale. Non-batched renderers are ordinary children of
        // the scaled 'Level Default' root and have already resized themselves, so touching
        // them would apply the scale twice.
        //
        // Lights are not renderers and do not move, so at large scales the baked pools of
        // light will no longer line up with the fixtures above them.
        private static List<ArenaProxyVisual.Target> CollectArenaProxyTargets(
            Matrix4x4 delta, out List<Transform> stranded)
        {
            var targets = new List<ArenaProxyVisual.Target>(256);
            stranded = new List<Transform>();
            var strandedUnitIds = new HashSet<int>();

            // Scans Renderer, not MeshRenderer: a crowd built from SkinnedMeshRenderers
            // can never be statically batched, and looking only at MeshRenderer would omit
            // it from the diagnostics as well as the work, which reads as "the scan found
            // nothing wrong" when it simply never looked.
            var all = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            Transform levelRoot = _proxyArenaRoot != null ? _proxyArenaRoot.root : null;
            string strandedSample = null;
            string orphanSample = null;
            int orphans = 0;
            int underRoot = 0;

            // Built from the GOAL side, by identity, using the exact same traversal
            // SyncBaseGoalFrame uses to find its targets. Walking up from a renderer
            // looking for a Goal component is the same idea only when the hierarchy is
            // what you assume it is, and getting that wrong here means the arena group
            // steals the frame and draws it at the building's scale while the goal group
            // draws it at the goal's: two frames, same width, different heights.
            var goalRendererIds = CollectGoalRendererIds();

            for (int i = 0; i < all.Length; i++)
            {
                var mr = all[i];
                if (mr == null) continue;

                // A renderer the proxy already owns is disabled BY the proxy, so it must
                // stay in the list. Dropping it would empty the target list on every
                // rebuild and hand the visual straight back to the bundled prefab.
                if (!mr.enabled && !ArenaProxyVisual.IsOwnedBy(mr, ArenaProxyVisual.ArenaGroupKey)) continue;
                if (goalRendererIds.Contains(mr.GetInstanceID())) continue;
                if (IsUnderGoal(mr.transform)) continue;

                // Scenery-loader content already rides a scaler transform carrying this
                // exact delta. It has to be skipped on BOTH branches below: batched, or the
                // proxy draws the building a second time at the same size; unbatched, or the
                // stranded pass moves whatever inside it matches a crowd container name.
                if (IsSceneryLoaderOwned(mr.transform)) continue;

                var meshRenderer = mr as MeshRenderer;
                if (meshRenderer == null || !meshRenderer.isPartOfStaticBatch)
                {
                    // Not batched, so it follows its own transform. That is only a resize
                    // if it hangs off the scaled 'Level Default' root; anything parked in
                    // another scene root stays at vanilla size and position, which is what
                    // a crowd left sitting inside a widened rink looks like.
                    if (levelRoot == null) continue;

                    if (!mr.transform.IsChildOf(levelRoot))
                    {
                        // The crowd is owned by CrowdSeating, which seats each member on its
                        // own CrowdPosition marker. It must never reach the delta-based
                        // follower: a member is instantiated AT its already-scaled marker, so
                        // the delta would be applied a second time and throw the crowd out
                        // over the ice. Excluded by parentage rather than by name, because
                        // the name hints are what put the crowd on this path to begin with.
                        if (IsCrowdOwned(mr.transform)) continue;

                        if (TryResolveSceneryContainer(mr.transform, out Transform container))
                        {
                            if (strandedUnitIds.Add(container.GetInstanceID()))
                            {
                                stranded.Add(container);
                                if (strandedSample == null) strandedSample = DescribeTransformPath(container);
                            }
                            continue;
                        }

                        // Outside the scaled root and not scenery we know how to follow, so
                        // NOTHING moves it. Counted rather than skipped in silence: on a
                        // level whose arena is instantiated at runtime almost none of it is
                        // batched, and a scan that only reports what it took over reads as
                        // healthy while most of the rink stays at vanilla size.
                        //
                        // Networked objects are excluded from the count. Pucks and player
                        // bodies are outside the level root and are SUPPOSED to be left
                        // alone, so counting them buries the arena geometry that is not.
                        if (mr.transform.GetComponentInParent<Unity.Netcode.NetworkObject>(true) != null) continue;

                        orphans++;
                        if (orphanSample == null)
                            orphanSample = mr.GetType().Name + " " + DescribeTransformPath(mr.transform);
                        continue;
                    }

                    // Under the scaled root, so the parent transform already carries the
                    // full resize on all three axes. Nothing to do.
                    if (IsMovableUnderLevelRoot(mr, levelRoot)) underRoot++;
                    continue;
                }

                targets.Add(new ArenaProxyVisual.Target { Renderer = meshRenderer, WorldDelta = delta });
            }

            ReportStrandedRenderersOnce(all.Length, targets.Count, underRoot,
                stranded.Count, strandedSample, orphans, orphanSample);
            return targets;
        }

        /// <summary>
        /// The same rule as <see cref="IsMovableScenery"/>, but the walk stops AT the level
        /// root instead of running to the scene root.
        ///
        /// This distinction is the whole ball game: `Level` is a NetworkBehaviour, so the
        /// level root itself carries a NetworkObject. A search that runs past it therefore
        /// reports "networked" for every single renderer in the arena, which silently
        /// emptied the height fix-up list and left the barrier glass squat. Networked
        /// children that matter, goals above all, still sit strictly below the level root
        /// and are still excluded.
        /// </summary>
        private static bool IsMovableUnderLevelRoot(Renderer renderer, Transform levelRoot)
        {
            if (renderer == null || levelRoot == null) return false;

            for (Transform current = renderer.transform; current != null && current != levelRoot; current = current.parent)
            {
                if (current.GetComponent<Unity.Netcode.NetworkObject>() != null) return false;
                if (current.name.StartsWith("CompAdjust", StringComparison.Ordinal)) return false;
                if (string.Equals(current.name, UnifiedInstanceName, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        /// <summary>
        /// Containers whose contents count as movable scenery. The crowd lives under
        /// "Spectator Manager" in this game.
        /// </summary>
        private static readonly string[] SceneryContainerHints =
        {
            "spectator", "crowd", "audience", "bleacher",
        };

        /// <summary>
        /// Finds the scenery container a renderer belongs to, if any.
        ///
        /// This is opt-IN on purpose. The rule used to be "anything outside the level root
        /// that is not networked", which swept up 972 renderers including individual BONES
        /// of players and crowd members. Writing a transform per bone is wrong twice over:
        /// the animation overwrites it every frame, and the baseline gets captured from
        /// whatever pose happened to be current.
        ///
        /// The CONTAINER is what gets returned rather than the object itself, because the
        /// crowd is pooled: members are created and destroyed continuously, so a list of
        /// members captured at rebuild time is stale within seconds. Following the
        /// container means new members are picked up the frame they appear.
        ///
        /// The OUTERMOST match wins, not the first one found walking up, and that is not a
        /// detail. A crowd member is called "Crowd Member(Clone)", which contains the hint
        /// "crowd", so stopping at the first match returned the member itself as the
        /// container: 145 containers instead of one "Spectator Manager". SceneryFollower
        /// then moved each container's CHILDREN, which for a member are its body parts, so
        /// every spectator was taken apart and its pieces scattered by the arena delta while
        /// the member's own transform never moved. That is the giant, spread-out crowd
        /// hanging over the ice. Continuing the walk puts the container back at the manager,
        /// where the children are whole members and each one moves as a unit.
        /// </summary>
        private static bool TryResolveSceneryContainer(Transform t, out Transform container)
        {
            container = null;
            if (t == null) return false;

            // Never move anything the server owns the position of. Checked once, up front,
            // against the renderer itself: the walk below no longer stops at its first match,
            // so doing it per match would test a different subtree each time.
            if (t.GetComponentInParent<Unity.Netcode.NetworkObject>(true) != null) return false;

            for (Transform current = t; current != null; current = current.parent)
            {
                if (current.name.StartsWith("CompAdjust", StringComparison.Ordinal)) return false;
                if (string.Equals(current.name, UnifiedInstanceName, StringComparison.Ordinal)) return false;

                // Remembered rather than returned, so an enclosing manager outranks an
                // individual unit inside it.
                if (current != t && MatchesSceneryContainer(current.name)) container = current;
            }

            return container != null;
        }

        /// <summary>
        /// True for anything under the CrowdManager, whose members CrowdSeating owns.
        /// Falls back to false when the manager does not exist, which leaves the old
        /// name-based path in charge rather than silently dropping scenery.
        /// </summary>
        private static bool IsCrowdOwned(Transform t)
        {
            Transform manager = CrowdSeatingManagerTransform;
            if (manager == null || t == null) return false;
            return t == manager || t.IsChildOf(manager);
        }

        private static bool MatchesSceneryContainer(string name)
        {
            for (int i = 0; i < SceneryContainerHints.Length; i++)
                if (name.IndexOf(SceneryContainerHints[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static int _lastReportedBatched = -1;

        // Re-reported whenever the counts move, not just once: the first scan can easily
        // run before a crowd or any other late geometry exists, and a one-shot line would
        // then under-report the scene forever.
        private static void ReportStrandedRenderersOnce(
            int scanned, int batched, int underRoot, int stranded, string sample, int orphans, string orphanSample)
        {
            int signature = batched * 397 ^ underRoot * 31 ^ stranded * 7 ^ orphans;
            if (signature == _lastReportedBatched) return;
            _lastReportedBatched = signature;

            string message = $"Arena proxy scan: {scanned} renderer(s) -> {batched} batched and proxy-drawn, " +
                             $"{underRoot} unbatched under the scaled root, {stranded} scenery container(s) followed";
            if (orphans > 0)
                message += $", and {orphans} NOT batched and outside the scaled root, which NOTHING moves (e.g. '{orphanSample}')";
            else if (stranded > 0)
                message += $" (e.g. '{sample}')";

            CompetitiveAdjustments.ConfigManager.Log(message + ".");
        }

        private static string DescribeTransformPath(Transform t)
        {
            var parts = new List<string>();
            for (Transform current = t; current != null && parts.Count < 6; current = current.parent)
                parts.Add(current.name);

            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// Every renderer any Goal owns, by instance id. Goals are proxied per goal with a
        /// delta that also carries GoalSizeScale, so the arena group must never touch one.
        /// </summary>
        private static HashSet<int> CollectGoalRendererIds()
        {
            var ids = new HashSet<int>();

            foreach (var goal in UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None))
            {
                if (goal == null) continue;

                foreach (var r in goal.transform.GetComponentsInChildren<Renderer>(true))
                    if (r != null) ids.Add(r.GetInstanceID());
            }

            return ids;
        }

        private static bool IsUnderGoal(Transform t)
        {
            for (Transform current = t; current != null; current = current.parent)
                if (current.GetComponent<Goal>() != null) return true;

            return false;
        }

        private static bool IsVisuallyUnresized(Vector3 worldScale, Vector3 worldOffset)
        {
            const float scaleTolerance  = 0.002f;
            const float offsetTolerance = 0.05f;

            return Mathf.Abs(worldScale.x - 1f) < scaleTolerance
                && Mathf.Abs(worldScale.y - 1f) < scaleTolerance
                && Mathf.Abs(worldScale.z - 1f) < scaleTolerance
                && Mathf.Abs(worldOffset.x) < offsetTolerance
                && Mathf.Abs(worldOffset.y) < offsetTolerance
                && Mathf.Abs(worldOffset.z) < offsetTolerance;
        }

        // (1) Resize the real base-game arena via the 'Level Default' scene root.
        // Width = Unity X = ArenaScaleX, length = Unity Z = ArenaScaleY. Height (Unity Y)
        // rides ArenaScaleZ, so the boards, glass and ceiling get taller for REAL: their
        // colliders grow with them and a puck that clears the vanilla barrier no longer
        // leaves a rink whose walls are drawn three times that high. Goal net cloth is
        // disabled around the write so the transform delta is not read as an impulse that
        // explodes the net.
        //
        // Everything under the root scales with it, which is the point but is worth
        // knowing: the goals stretch vertically too, so a tall ArenaScaleZ wants
        // GoalSizeScaleY turned down to keep the net a net.
        private static void ScaleLevelDefaultRoot(Transform levelRoot, float width, float height, float length, float offsetX, float offsetY, float offsetZ)
        {
            if (levelRoot == null) return;

            if (_scaledLevelRootId != levelRoot.GetInstanceID())
            {
                // Hand the previous root back before adopting a new one, so a root that
                // FindArenaRoot stops choosing is not left carrying our scale.
                RestoreLevelDefaultScale();
                _scaledLevelRootId = levelRoot.GetInstanceID();
            }
            _scaledLevelRoot = levelRoot;

            // The baseline comes off the OBJECT, never off a live measurement here. See
            // ArenaBaselineMarker: the incoming level root can be a clone of the outgoing
            // one taken while our scale was still applied, and it is indistinguishable
            // from vanilla by inspection.
            var marker = ArenaBaselineMarker.Resolve(levelRoot, out bool captured);
            if (marker == null) return;

            if (captured)
            {
                // A first sight that is ALREADY carrying exactly the scale this config
                // produces is not a vanilla rink. A second scene load ('activeSceneChanged
                // -> level_default') hands us a brand new level root, with no marker to
                // inherit, already resized. Measured at face value it becomes the new
                // vanilla and the config multiplies on top: 1.25 x 1.25 = 1.5625.
                //
                // Vanilla is unit scale in this game, verified as the first capture on both
                // server and client. The correction is deliberately narrow: it only fires
                // when the scale matches OUR OWN output, so a level genuinely authored at
                // some other scale is still measured as it is.
                var ourOutput = new Vector3(width, height, length);
                if (!ApproxEqual(marker.BaseScale, Vector3.one) && ApproxEqual(marker.BaseScale, ourOutput))
                {
                    Debug.LogWarning($"[COMPADJUST] '{DescribeTransformPath(levelRoot)}' (id={_scaledLevelRootId}) " +
                                     $"was already at {marker.BaseScale}, which is exactly this config's output, " +
                                     "so it is a pre-resized root rather than a vanilla one. Treating its baseline " +
                                     "as unit scale instead of compounding the resize.");
                    // Correct BOTH halves of the baseline, not just the scale. A root
                    // carrying our output scale is carrying our output position too,
                    // because the same pass wrote both (targetPos = basePos + offset
                    // below). Passing BasePosition through unchanged stopped the scale
                    // compounding but started the OFFSET compounding: the root landed
                    // at vanillaPos + 2*offset, colliders and spawns drifted one whole
                    // offset away from the proxy-drawn visuals, and because the wrong
                    // value was then baked into the marker it was permanent for that
                    // root, including through RestoreLevelDefaultScale.
                    //
                    // Residual gap, unchanged by this fix: the detection is scale-based,
                    // so a config with unit scale but a non-zero offset produces a
                    // pre-resized root that is indistinguishable from a vanilla one.
                    // Nothing in the transform can tell those apart; only the marker
                    // can, and by definition this branch is the case with no marker.
                    marker.OverrideBaseline(Vector3.one, marker.BasePosition - new Vector3(offsetX, offsetY, offsetZ));
                    DumpLevelRootCandidates(levelRoot);
                }
                else
                {
                    Debug.Log($"[COMPADJUST] Captured '{DescribeTransformPath(levelRoot)}' (id={_scaledLevelRootId}) " +
                              $"vanilla baseline: scale {marker.BaseScale}, pos {marker.BasePosition}.");
                }
            }

            _levelRootBaseScale = marker.BaseScale;
            _levelRootBasePos   = marker.BasePosition;

            var targetScale = Vector3.Scale(_levelRootBaseScale, new Vector3(width, height, length));
            var targetPos   = _levelRootBasePos + new Vector3(offsetX, offsetY, offsetZ);

            if (ApproxEqual(levelRoot.localScale, targetScale) && ApproxEqual(levelRoot.localPosition, targetPos))
                return; // already applied; don't disturb the cloth sim

            var reenable = DisableAllGoalNetCloth();
            levelRoot.localScale    = targetScale;
            levelRoot.localPosition = targetPos;
            ReenableGoalNetCloth(reenable);

            // Logged unconditionally. It used to be gated on finding 'Rink/Barrier
            // Collider' as a re-cook probe, which silently produced NO log at all on a
            // scene that does not have that node, exactly when knowing whether the resize
            // ran would have been most useful. The baseline is in here too, because a
            // baseline captured from an already-scaled root is how a rejoin ends up
            // misaligned.
            Debug.Log($"[COMPADJUST] Resized '{DescribeTransformPath(levelRoot)}' (id={levelRoot.GetInstanceID()}) " +
                      $"from base scale {_levelRootBaseScale} to {targetScale}, base pos {_levelRootBasePos} -> {targetPos}. " +
                      DescribeBarrierColliderProbe(levelRoot));
        }

        /// <summary>
        /// State of the game's real 'Barrier Collider', which the Ruleset mod moves via
        /// GameObject.Find to open the rink for delay-of-game.
        ///
        /// Deliberately a NAME search rather than the old hardcoded 'Rink/Barrier Collider'
        /// path. That path does not hold on scenery-mod scenes, so the probe printed "not in
        /// this scene" for an object BoardColliderPatch was finding by name in the very same
        /// session. A false negative is worse than no probe at all here, because this is the
        /// line you read when the Ruleset's barrier lowering appears to do nothing.
        ///
        /// Reports what actually decides that question: how many candidates exist (their
        /// GameObject.Find picks an arbitrary one), whether the object is ACTIVE (their
        /// GameObject.Find cannot see inactive objects at all), and whether it hangs off the
        /// root we scale (which is what gives it the right width and length for free).
        ///
        /// Near-misses are counted separately and named. Their lookup is an EXACT-name match,
        /// so an object Unity has renamed 'Barrier Collider (Clone)' by instantiating it is
        /// invisible to them while being obviously present to anyone reading the hierarchy.
        /// Reporting "none found" without mentioning the clone sitting right there is how
        /// that costs an afternoon.
        /// </summary>
        private static string DescribeBarrierColliderProbe(Transform levelRoot)
        {
            const string barrierName = "Barrier Collider";

            var all = UnityEngine.Object.FindObjectsByType<Collider>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Collider chosen = null;
            int matches = 0, active = 0, underRoot = 0;
            var nearMisses = new List<string>();

            for (int i = 0; i < all.Length; i++)
            {
                var col = all[i];
                if (col == null) continue;

                if (col.name != barrierName)
                {
                    // Same object, renamed. Worth naming exactly, because the difference
                    // between this and an exact match IS the bug.
                    if (col.name.StartsWith(barrierName, StringComparison.Ordinal)
                        && nearMisses.Count < 4)
                        nearMisses.Add($"'{col.name}' at '{DescribeTransformPath(col.transform)}'");
                    continue;
                }

                matches++;
                bool isActive = col.gameObject.activeInHierarchy;
                if (isActive) active++;
                if (levelRoot != null && col.transform.IsChildOf(levelRoot)) underRoot++;

                // Prefer the one their Find could actually return.
                if (chosen == null || (isActive && !chosen.gameObject.activeInHierarchy)) chosen = col;
            }

            string near = nearMisses.Count > 0
                ? $" Also present under a DIFFERENT name, which an exact-name GameObject.Find " +
                  $"cannot return: {string.Join(", ", nearMisses)}."
                : string.Empty;

            if (matches == 0)
                return $"No collider named exactly '{barrierName}' anywhere in this scene, so the " +
                       "Ruleset's GameObject.Find for it returns null and its barrier lowering " +
                       "cannot run." + near;

            return $"'{barrierName}': {matches} in scene ({active} active, {underRoot} under the scaled root), " +
                   $"first at '{DescribeTransformPath(chosen.transform)}', world bounds now {chosen.bounds.size}." +
                   (active == 0
                       ? " NONE are active, so the Ruleset's GameObject.Find will return null."
                       : matches > 1
                           ? " More than one match, so the Ruleset's GameObject.Find picks an arbitrary one."
                           : string.Empty) + near;
        }

        /// <summary>
        /// Lists every scene root that could pass for a level root, with the state that
        /// decides whether its baseline is trustworthy. Printed when a pre-resized root is
        /// detected, to identify who handed it over already scaled.
        /// </summary>
        private static void DumpLevelRootCandidates(Transform chosen)
        {
            try
            {
                var sb = new System.Text.StringBuilder("[COMPADJUST] Level root candidates: ");
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                {
                    if (t == null || t.parent != null) continue;   // scene roots only

                    string name = t.name ?? string.Empty;
                    if (name.IndexOf("level", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("rink", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("arena", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    sb.Append($"['{name}' id={t.GetInstanceID()} scale={t.localScale} " +
                              $"marked={(t.GetComponent<ArenaBaselineMarker>() != null)} " +
                              $"scene={t.gameObject.scene.name} children={t.childCount}" +
                              $"{(t == chosen ? " <-- CHOSEN" : string.Empty)}] ");
                }

                Debug.Log(sb.ToString());
            }
            catch { }
        }

        /// <summary>
        /// Forgets the level root and its captured baseline WITHOUT writing to the old
        /// transform, for use when the scene it belonged to has gone away.
        /// </summary>
        private static void ResetLevelRootBaseline()
        {
            _scaledLevelRoot = null;
            _scaledLevelRootId = 0;
            _levelRootBaseScale = Vector3.one;
            _levelRootBasePos = Vector3.zero;
            _proxyWanted = false;
            _proxyArenaRoot = null;
            _loggedArenaRootMissing = false;
        }

        private static void RestoreLevelDefaultScale()
        {
            var levelRoot = _scaledLevelRoot;
            _scaledLevelRoot = null;
            if (levelRoot == null) return;

            // Straight off the object, so this is correct even when our statics have been
            // dropped or were never populated for this particular root.
            var marker = levelRoot.GetComponent<ArenaBaselineMarker>();
            if (marker == null) return;

            if (!ApproxEqual(levelRoot.localScale, marker.BaseScale) || !ApproxEqual(levelRoot.localPosition, marker.BasePosition))
            {
                var reenable = DisableAllGoalNetCloth();
                levelRoot.localScale    = marker.BaseScale;
                levelRoot.localPosition = marker.BasePosition;
                ReenableGoalNetCloth(reenable);
            }
        }

        private static List<Cloth> DisableAllGoalNetCloth()
        {
            var disabled = new List<Cloth>();
            foreach (var goal in UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None))
            {
                var cloth = goal != null ? goal.NetCloth : null;
                if (cloth != null && cloth.enabled) { cloth.enabled = false; disabled.Add(cloth); }
            }
            return disabled;
        }

        private static void ReenableGoalNetCloth(List<Cloth> cloths)
        {
            if (cloths == null) return;
            for (int i = 0; i < cloths.Count; i++)
                if (cloths[i] != null) cloths[i].enabled = true;
        }

        // Every tick: copy texture and color properties from the hidden source renderers to
        // our custom clones so live changes by other mods propagate automatically.
        // We intentionally skip _Smoothness and _Metallic — other mods own those.
        private static void LiveSyncArenaSourceTextures()
        {
            for (int i = _arenaRendererPairs.Count - 1; i >= 0; i--)
            {
                var (dst, src) = _arenaRendererPairs[i];
                if (dst == null || src == null) { _arenaRendererPairs.RemoveAt(i); continue; }

                // Use src.materials (per-instance) so we pick up any live modifications
                // another mod has applied to the source renderer's material instance.
                var srcMats = src.materials;
                var dstMats = dst.sharedMaterials;
                int count = Mathf.Min(srcMats != null ? srcMats.Length : 0,
                                      dstMats != null ? dstMats.Length : 0);
                for (int j = 0; j < count; j++)
                {
                    var s = srcMats[j];
                    var d = dstMats[j];
                    if (s == null || d == null) continue;
                    CopyTexturePropertyIfPresent(s, d, "_BaseMap");
                    CopyTexturePropertyIfPresent(s, d, "_MainTex");
                    CopyTexturePropertyIfPresent(s, d, "_BumpMap");
                    CopyTexturePropertyIfPresent(s, d, "_NormalMap");
                    CopyTexturePropertyIfPresent(s, d, "_MaskMap");
                    CopyTexturePropertyIfPresent(s, d, "_MetallicGlossMap");
                    CopyTexturePropertyIfPresent(s, d, "_OcclusionMap");
                    CopyTexturePropertyIfPresent(s, d, "_EmissionMap");
                    CopyColorPropertyIfPresent(s, d, "_BaseColor");
                    CopyColorPropertyIfPresent(s, d, "_Color");
                    CopyColorPropertyIfPresent(s, d, "_EmissionColor");
                    CopyColorPropertyIfPresent(s, d, "_TeamColor");
                }
            }
        }

        private static void SyncArenaColliderDebugBrushes(Transform collidersRoot)
        {
            if (collidersRoot == null) return;

            bool enabled = IsArenaColliderDebugEnabled();
            // Every collider under the level root, which is what collidersRoot now is.
            // There is no separate barrier pass any more: the standalone barrier clone
            // this used to need one for was removed along with the bundled collider
            // prefab, and the real 'Barrier Collider' is an ordinary child of the rink.
            foreach (var collider in collidersRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                SyncArenaColliderDebugBrush(collider, enabled);
            }
        }

        private static void SyncArenaColliderDebugBrush(Collider collider, bool enabled)
        {
            if (collider == null) return;

            const string debugBrushName = "__clipBrush";
            var debugBrush = collider.transform.Find(debugBrushName);

            if (!enabled)
            {
                if (debugBrush != null)
                    debugBrush.gameObject.SetActive(false);
                return;
            }

            if (debugBrush == null)
            {
                var brushGo = new GameObject(debugBrushName);
                debugBrush = brushGo.transform;
                debugBrush.SetParent(collider.transform, false);
                debugBrush.gameObject.layer = collider.gameObject.layer;
                debugBrush.gameObject.AddComponent<MeshFilter>();
                var brushRenderer = debugBrush.gameObject.AddComponent<MeshRenderer>();
                brushRenderer.shadowCastingMode = ShadowCastingMode.Off;
                brushRenderer.receiveShadows = false;
                brushRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                brushRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                brushRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            debugBrush.gameObject.SetActive(true);

            var meshFilter = debugBrush.GetComponent<MeshFilter>();
            var meshRenderer = debugBrush.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshRenderer == null) return;

            meshRenderer.enabled = true;
            meshRenderer.sharedMaterial = GetArenaColliderDebugMaterial();

            if (collider is BoxCollider box)
            {
                meshFilter.sharedMesh = GetPrimitiveDebugMesh(PrimitiveType.Cube);
                debugBrush.localPosition = box.center;
                debugBrush.localRotation = Quaternion.identity;
                debugBrush.localScale = box.size;
            }
            else if (collider is SphereCollider sphere)
            {
                meshFilter.sharedMesh = GetPrimitiveDebugMesh(PrimitiveType.Sphere);
                debugBrush.localPosition = sphere.center;
                debugBrush.localRotation = Quaternion.identity;
                float diameter = sphere.radius * 2f;
                debugBrush.localScale = new Vector3(diameter, diameter, diameter);
            }
            else if (collider is CapsuleCollider capsule)
            {
                meshFilter.sharedMesh = GetPrimitiveDebugMesh(PrimitiveType.Capsule);
                debugBrush.localPosition = capsule.center;
                debugBrush.localRotation = capsule.direction == 0
                    ? Quaternion.Euler(0f, 0f, 90f)
                    : (capsule.direction == 2 ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity);

                float diameter = capsule.radius * 2f;
                float length = Mathf.Max(capsule.height, diameter);
                debugBrush.localScale = capsule.direction == 0
                    ? new Vector3(length, diameter, diameter)
                    : (capsule.direction == 2
                        ? new Vector3(diameter, diameter, length)
                        : new Vector3(diameter, length, diameter));
            }
            else if (collider is MeshCollider meshCollider)
            {
                var debugMesh = meshCollider.sharedMesh;
                if (debugMesh == null)
                {
                    var sourceMeshFilter = collider.GetComponent<MeshFilter>();
                    if (sourceMeshFilter != null)
                        debugMesh = sourceMeshFilter.sharedMesh;
                }

                if (debugMesh != null)
                {
                    meshFilter.sharedMesh = debugMesh;
                    debugBrush.localPosition = Vector3.zero;
                    debugBrush.localRotation = Quaternion.identity;
                    debugBrush.localScale = Vector3.one;
                }
                else
                {
                    // Last-resort fallback so we can still see a brush for non-readable mesh colliders.
                    meshFilter.sharedMesh = GetPrimitiveDebugMesh(PrimitiveType.Cube);
                    var bounds = collider.bounds;
                    var centerLocal = collider.transform.InverseTransformPoint(bounds.center);
                    var size = bounds.size;
                    debugBrush.localPosition = centerLocal;
                    debugBrush.localRotation = Quaternion.identity;
                    debugBrush.localScale = new Vector3(
                        Mathf.Max(0.001f, size.x),
                        Mathf.Max(0.001f, size.y),
                        Mathf.Max(0.001f, size.z));
                }
            }
            else
            {
                debugBrush.gameObject.SetActive(false);
            }
        }

        private static bool IsArenaColliderDebugEnabled()
        {
            try
            {
                return DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowArenaClipBrushes == true;
            }
            catch { }

            return false;
        }

        /// <summary>Called from the UI toggle to re-sync arena collider debug brushes immediately.</summary>
        public static void RefreshArenaColliderBrushes()
        {
            // Points at the real base-game colliders under 'Level Default'. It used to
            // visualise the bundled collider prefab, which no longer exists.
            var levelRoot = FindArenaRoot()?.root;
            if (levelRoot != null)
            {
                SyncArenaColliderDebugBrushes(levelRoot);
                if (IsArenaColliderDebugEnabled())
                    LogArenaColliderHeights(levelRoot);
            }
        }

        // Dev diagnostic: dump every arena collider's world-space Y extents so the
        // real (post scale + offset) board/barrier heights can be read directly
        // instead of guessing from vanilla rink numbers. Barriers are the clones
        // under __originalBarrierOverrides; everything else is a board/ice piece
        // (the custom SubMesh_N mesh colliders from Colliders.fbx). Gated behind the
        // ShowArenaClipBrushes debug toggle by its callers, so it is silent in
        // normal play and re-emits when the toggle is flipped on or the arena rebuilds.
        internal static void LogArenaColliderHeights(Transform collidersRoot)
        {
            if (collidersRoot == null) return;

            Transform barrierOverrides = collidersRoot.Find("__originalBarrierOverrides");
            var cols = collidersRoot.GetComponentsInChildren<Collider>(true);
            Debug.Log($"[COMPADJUST] Arena collider height dump: {cols.Length} colliders under '{collidersRoot.name}' (world-space Y).");
            foreach (var col in cols)
            {
                if (col == null) continue;
                if (string.Equals(col.gameObject.name, "__clipBrush", StringComparison.Ordinal)) continue;

                bool isBarrier = barrierOverrides != null
                    && (col.transform == barrierOverrides || col.transform.IsChildOf(barrierOverrides));
                Bounds b = col.bounds;
                string layer = LayerMask.LayerToName(col.gameObject.layer);
                Debug.Log($"[COMPADJUST]   {(isBarrier ? "BARRIER" : "board  ")} '{col.name}' layer={layer} " +
                          $"y[min={b.min.y:F3} max={b.max.y:F3} center={b.center.y:F3} height={b.size.y:F3}] " +
                          $"x[{b.min.x:F2}..{b.max.x:F2}] z[{b.min.z:F2}..{b.max.z:F2}]");
            }
        }

        private static Material GetArenaColliderDebugMaterial()
        {
            if (_arenaColliderDebugMaterial != null)
                return _arenaColliderDebugMaterial;

            var shader = Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");
            _arenaColliderDebugMaterial = new Material(shader);
            _arenaColliderDebugMaterial.color = new Color(0.15f, 0.95f, 0.85f, 0.24f);
            return _arenaColliderDebugMaterial;
        }

        private static Mesh GetPrimitiveDebugMesh(PrimitiveType primitiveType)
        {
            switch (primitiveType)
            {
                case PrimitiveType.Cube:
                    if (_debugCubeMesh == null) _debugCubeMesh = CreatePrimitiveDebugMesh(PrimitiveType.Cube);
                    return _debugCubeMesh;
                case PrimitiveType.Sphere:
                    if (_debugSphereMesh == null) _debugSphereMesh = CreatePrimitiveDebugMesh(PrimitiveType.Sphere);
                    return _debugSphereMesh;
                case PrimitiveType.Capsule:
                    if (_debugCapsuleMesh == null) _debugCapsuleMesh = CreatePrimitiveDebugMesh(PrimitiveType.Capsule);
                    return _debugCapsuleMesh;
                default:
                    return null;
            }
        }

        private static Mesh CreatePrimitiveDebugMesh(PrimitiveType primitiveType)
        {
            var temp = GameObject.CreatePrimitive(primitiveType);
            try
            {
                var meshFilter = temp.GetComponent<MeshFilter>();
                return meshFilter != null ? meshFilter.sharedMesh : null;
            }
            finally
            {
                UnityEngine.Object.Destroy(temp);
            }
        }

        private static Transform FindArenaRoot()
        {
            Transform best = null;
            float bestScore = float.MinValue;

            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t == null) continue;

                string name = t.name ?? string.Empty;
                if (name.Length == 0) continue;

                bool exactArena = string.Equals(name, "arena", StringComparison.OrdinalIgnoreCase);
                bool exactRink = string.Equals(name, "rink", StringComparison.OrdinalIgnoreCase);
                bool containsArena = name.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0;
                bool containsRink = name.IndexOf("rink", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!exactArena && !exactRink && !containsArena && !containsRink)
                    continue;

                // Never let loaded scenery win the vote. 'PonceArena(Clone)' scores on the
                // name and carries plenty of rink-shaped renderers, so once a scenery mod
                // has swapped the building in, the answer can flip from the game's own
                // 'Rink' to the loaded prefab and take the level root with it.
                if (IsSceneryLoaderOwned(t))
                    continue;

                float score = 0f;
                if (exactArena) score += 1200f;
                else if (exactRink) score += 1100f;
                else if (containsArena) score += 700f;
                else if (containsRink) score += 600f;

                if (t.parent == null) score += 200f;

                // Count descendants that are actual rink surfaces we would hide
                // (ice / boards / glass / barrier ...). A candidate that parents NONE of
                // them is not a real arena root -- e.g. the bare mesh named exactly
                // "Arena" in the Ponce custom-scenery scene, which otherwise hijacks the
                // exact-name score (1200) away from the real ice parent, leaving the base
                // rink visible and anchoring the visual clone to the wrong node. Skip it.
                var descendantRenderers = t.GetComponentsInChildren<Renderer>(true);
                int hideableRinkSurfaces = 0;
                foreach (var candidateRenderer in descendantRenderers)
                    if (ShouldHideOriginalArenaRenderer(candidateRenderer, t)) hideableRinkSurfaces++;
                if (hideableRinkSurfaces == 0)
                    continue;

                score += Mathf.Min(200f, descendantRenderers.Length * 4f);
                // Bias strongly toward the node that actually contains the rink surfaces,
                // so a richer container (ice + boards + glass) wins over a name-only match
                // and hiding covers the whole rink rather than just the ice.
                score += Mathf.Min(600f, hideableRinkSurfaces * 50f);

                int depth = 0;
                var p = t.parent;
                while (p != null)
                {
                    depth++;
                    p = p.parent;
                }

                score -= depth * 5f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = t;
                }
                else if (Mathf.Approximately(score, bestScore) && best != null)
                {
                    if (string.CompareOrdinal(name, best.name) < 0)
                        best = t;
                }
            }

            return best;
        }

        // ── Arena network bounds patches ──────────────────────────────────────
        // Applied when EnableArenaTweaks is true; unapplied when it is turned off.
        // Replaces vanilla 16-bit position quantisation with the chunked-sync
        // system in src/Net/, keeping vanilla 1.5 mm precision out to +/-4 km.

        private static void ApplyNetworkBoundsPatches()
        {
            if (NetworkBoundsPatch.ChunksEnabled) return;

            // Guard 1: no active network session — the DontDestroyOnLoad Runner can fire
            // RefreshAll() after disconnect or after mod disable; bail out in that case.
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null) return;

            // Guard 2: if we're a client, only apply when we have actually received a
            // PPKB/GoalTweaks sync from the current server.  That message is sent only
            // by servers running this mod (see DashFall.ServerBridge), so its absence
            // means we're on a vanilla server and the chunked sync must stay inert --
            // installing the encode/decode prefix here would desync us from the server.
            if (!nm.IsServer && !_hasSyncedTweaks)
            {
                if (!_loggedVanillaServerSkip)
                {
                    _loggedVanillaServerSkip = true;
                    CompetitiveAdjustments.ConfigManager.Log("Chunked network sync NOT enabled: no PPKB/GoalTweaks received -- server is vanilla or has this mod disabled.");
                }
                return;
            }

            _loggedVanillaServerSkip = false;
            NetworkBoundsPatch.EnableOpenWorldPrecision();
            LogRequiredChunks();
        }

        internal static void RemoveNetworkBoundsPatches()
        {
            _loggedVanillaServerSkip = false;
            NetworkBoundsPatch.Disable();
        }

        // Vanilla rink half-extent along world X / Z, used to derive required chunk
        // counts. World X scales with ArenaScaleX and world Z (rink length) with
        // ArenaScaleZ. ArenaScaleY is the vertical axis and is not chunked.
        private const float VanillaArenaHalfExtentX = 50f;
        private const float VanillaArenaHalfExtentZ = 25f;

        private static void LogRequiredChunks()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool useSynced = _hasSyncedTweaks && nm != null && !nm.IsServer;
            var cfg = CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;

            float scaleX = useSynced ? _syncedArenaScaleX : (cfg?.ArenaScaleX ?? 1f);
            // ArenaScaleY is the vertical axis and does not affect the chunk grid.
            float scaleZ = useSynced ? _syncedArenaScaleZ : (cfg?.ArenaScaleZ ?? 1f);
            if (scaleX <= 0f) scaleX = 1f;
            if (scaleZ <= 0f) scaleZ = 1f;

            float halfX = VanillaArenaHalfExtentX * scaleX;
            float halfZ = VanillaArenaHalfExtentZ * scaleZ;
            int requiredChunksX = Mathf.CeilToInt(halfX / ChunkRegistry.ChunkSizeMeters);
            int requiredChunksZ = Mathf.CeilToInt(halfZ / ChunkRegistry.ChunkSizeMeters);
            int maxChunkIndex = Mathf.Max(requiredChunksX, requiredChunksZ);

            CompetitiveAdjustments.ConfigManager.Log(
                $"Arena half-extent X={halfX:F1}m Z={halfZ:F1}m (scale X={scaleX:F2} Z={scaleZ:F2}); " +
                $"required chunks per axis: X={requiredChunksX} Z={requiredChunksZ}; chunk-index limit +/-{sbyte.MaxValue} (~{sbyte.MaxValue * ChunkRegistry.ChunkSizeMeters:F0}m).");

            if (maxChunkIndex > sbyte.MaxValue)
                Debug.LogWarning($"[COMPADJUST] Required chunk index {maxChunkIndex} exceeds sbyte range; positions beyond +/-{sbyte.MaxValue * ChunkRegistry.ChunkSizeMeters:F0}m will clamp.");
        }

        // ── Audio environment adjustment ──────────────────────────────────────
        // Expands the AudioReverbZone to cover the enlarged arena so audio
        // dampening still applies correctly with custom arena scale.

        private static void HandleAudioEnvironment(
            float width, float height, float length, float offsetX, float offsetY, float offsetZ)
        {
            if (_cachedReverbZone == null)
                _cachedReverbZone = Resources.FindObjectsOfTypeAll<AudioReverbZone>()
                    .FirstOrDefault(o => o.gameObject.scene.IsValid());

            var reverbZone = _cachedReverbZone;
            if (reverbZone == null) return;

            // Keyed to the ZONE, not to a bare "have we captured yet" flag. A scene reload
            // destroys the zone and the line above resolves a new one, but the old sentinel
            // said "already captured", so the new zone kept the previous zone's numbers as
            // its vanilla baseline. Re-capturing per instance is what makes a reload safe.
            int zoneId = reverbZone.GetInstanceID();
            if (_reverbBaselineZoneId != zoneId)
            {
                _reverbBaselineZoneId = zoneId;
                _originalReverbMaxDistance = reverbZone.maxDistance;
                _originalReverbMinDistance = reverbZone.minDistance;
                _originalReverbPosition = reverbZone.transform.position;
                _originalReverbActive = reverbZone.gameObject.activeSelf;
            }

            // Both radii scale, by the LARGEST horizontal factor. An AudioReverbZone is a
            // sphere, so a rink stretched on one axis has to be covered by the long one or
            // the far corners fall outside the zone and go dry.
            //
            // Height is left out deliberately: it scales the roof, not the distance from
            // centre ice to the boards, and folding it in made a tall arena bleed reverb far
            // past the building.
            //
            // This used to be a flat 500 m on both a grown and a shrunk rink. That is the
            // reported bug: the range did not track the arena at all. On a shrunk rink it
            // covered several buildings' worth of space, so the dampening that should come
            // in past the boards never arrived, and the vanilla ratio between the two radii
            // was destroyed because only maxDistance was written.
            float factor = Mathf.Max(Mathf.Abs(width), Mathf.Abs(length));

            float targetMax = _originalReverbMaxDistance * factor;
            float targetMin = _originalReverbMinDistance * factor;

            // The zone rides the rink, so it has to follow ArenaOffset like everything else.
            // Forcing it to the origin left the reverb centred on a rink that had moved.
            Vector3 targetPosition = _originalReverbPosition + new Vector3(offsetX, offsetY, offsetZ);

            // The setters are extern InternalCall, so whether the native side clamps min
            // against max is not knowable from the managed assembly. Write the widening one
            // first in either direction and the question stops mattering: no intermediate
            // state ever has min above max, so nothing can be clamped away.
            bool growing = targetMax >= reverbZone.maxDistance;

            if (Mathf.Approximately(reverbZone.maxDistance, targetMax)
                && Mathf.Approximately(reverbZone.minDistance, targetMin)
                && reverbZone.transform.position == targetPosition
                && reverbZone.gameObject.activeSelf)
                return;

            reverbZone.gameObject.SetActive(true);
            reverbZone.transform.position = targetPosition;

            if (growing) { reverbZone.maxDistance = targetMax; reverbZone.minDistance = targetMin; }
            else         { reverbZone.minDistance = targetMin; reverbZone.maxDistance = targetMax; }

            CompetitiveAdjustments.ConfigManager.Log(
                $"AudioReverbZone scaled to the arena (x{factor:F2}): " +
                $"min {_originalReverbMinDistance:F0} -> {reverbZone.minDistance:F0} m, " +
                $"max {_originalReverbMaxDistance:F0} -> {reverbZone.maxDistance:F0} m, " +
                $"centre {targetPosition}.");
        }

        private static void RestoreAudioEnvironment()
        {
            if (_cachedReverbZone != null && _originalReverbMaxDistance >= 0f)
            {
                // Same widen-first rule as the apply path, and it has to be decided the same
                // way. Hardcoding max-then-min is only correct when restoring from a SHRUNK
                // arena; coming back from a grown one that order narrows first and can be
                // clamped away.
                if (_originalReverbMaxDistance >= _cachedReverbZone.maxDistance)
                {
                    _cachedReverbZone.maxDistance = _originalReverbMaxDistance;
                    _cachedReverbZone.minDistance = _originalReverbMinDistance;
                }
                else
                {
                    _cachedReverbZone.minDistance = _originalReverbMinDistance;
                    _cachedReverbZone.maxDistance = _originalReverbMaxDistance;
                }

                // The apply path moves and force-enables the zone, so teardown has to undo
                // both or the reverb stays centred on a rink that is no longer resized.
                _cachedReverbZone.transform.position = _originalReverbPosition;
                if (_cachedReverbZone.gameObject.activeSelf != _originalReverbActive)
                    _cachedReverbZone.gameObject.SetActive(_originalReverbActive);

                CompetitiveAdjustments.ConfigManager.Log(
                    $"AudioReverbZone restored to min {_originalReverbMinDistance:F0} m, " +
                    $"max {_originalReverbMaxDistance:F0} m, centre {_originalReverbPosition}.");
            }
            _cachedReverbZone = null;
            _originalReverbMaxDistance = -1f;
            _originalReverbMinDistance = -1f;
            _reverbBaselineZoneId = 0;
        }
    }

}
