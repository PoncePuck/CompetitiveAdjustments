// ArenaSceneryLoaderSync.cs - stretch Dem's Scenery Loader content to fit a resized arena.

using System;
using System.Reflection;
using UnityEngine;

namespace DashFallMod
{
    public static partial class GoalNetTweaks
    {
        // Dem's Scenery Loader (SceneryChanger.dll, Steam workshop 3566470321) swaps the
        // arena SURROUNDINGS for a prefab out of an AssetBundle: it keeps the game's own
        // 'Rink' and hides the hangar, glass, scoreboard and crowd standing behind it.
        //
        // That prefab is authored against the vanilla rink, so on a server running this mod
        // with an arena resize the loaded building stays vanilla size while the rink inside
        // it grows, and the widened boards punch straight out through the walls. It is the
        // same failure the base hangar had before ArenaProxyVisual existed, and the fix is
        // the same idea: give the scenery the SAME world delta the batched base geometry
        // gets, so the building and the rink track each other.
        //
        // It is far simpler here than it is for the base game, because the scenery is an
        // ordinary runtime-instantiated prefab with a live transform rather than statically
        // batched scene geometry that cannot be moved at all.
        //
        // Reflection, because a workshop DLL is not a build-time reference and most players
        // do not have it installed. Every entry point below degrades to a no-op when the
        // type is missing.
        //
        // What we read (public static, SceneryChanger.Services.RinkSceneState):
        //
        //   GameObject Container    "SceneryLoaderContent", a scene root at identity
        //   GameObject ActiveRoot   the instantiated scenery prefab, parented to Container
        //
        // Only ActiveRoot is needed: Container is reached through it as the parent, which
        // also keeps us correct if the loader ever changes where it parks its content.

        private const string SceneryLoaderStateType = "SceneryChanger.Services.RinkSceneState";

        // The "CompAdjust" prefix is not decoration. IsMovableUnderLevelRoot and
        // TryResolveSceneryContainer both skip anything under a node named that way, so the
        // scaler is excluded from the rest of the arena passes for free.
        private const string SceneryScalerName = "CompAdjustSceneryScaler";

        private static FieldInfo _sceneryActiveRootField;
        private static float _nextSceneryProbe;
        private static bool _loggedSceneryLoaderFound;

        // The scale goes on a wrapper transform inserted between the loader's container and
        // its content, rather than on the content root itself. A prefab root that carries a
        // rotation cannot express a world-axis non-uniform scale in its own localScale: the
        // stretch would rotate with the mesh and a 3x-wide rink would come out 3x LONG. A
        // parent can, because Unity composes parent x child straight into the renderer
        // matrix and is perfectly happy for the result to carry shear.
        private static Transform _sceneryScaler;
        private static Transform _sceneryScaledRoot;

        // Where the content sat before we took it, so releasing puts it back exactly.
        private static Transform _sceneryBaseParent;
        private static int _sceneryBaseSiblingIndex = -1;
        private static Vector3 _sceneryBasePosition;
        private static Quaternion _sceneryBaseRotation = Quaternion.identity;
        private static Vector3 _sceneryBaseScale = Vector3.one;

        /// <summary>
        /// Whether a renderer belongs to scenery the scaler already owns.
        ///
        /// Used to keep the arena proxy off it. A scenery prefab built with static batching
        /// baked into the bundle would otherwise be picked up by the proxy scan as well,
        /// and then the building gets the resize twice: once from the scaler transform and
        /// once from the proxy's draw matrix.
        /// </summary>
        internal static bool IsSceneryLoaderOwned(Transform t)
        {
            return _sceneryScaler != null && t != null && t.IsChildOf(_sceneryScaler);
        }

        /// <summary>
        /// Point the loaded scenery at the resized rink, or hand it back if there is
        /// nothing to do. Idempotent, and cheap enough to call every tick: the steady state
        /// is one static field read and three transform writes.
        /// </summary>
        private static void SyncSceneryLoaderArena(Vector3 worldScale, Vector3 worldOffset)
        {
            // NOT gated on the visual mode, and specifically not skipped when headless.
            // This writes a TRANSFORM, and a scenery prefab is a building: its walls and
            // its glass carry colliders. DSL hides the vanilla hangar and glass behind its
            // own, so those surfaces ARE the loaded prefab. Scaling it on clients only
            // leaves a dedicated server colliding with a vanilla-sized building while
            // every client sees a stretched one, which reads in-game as the glass not
            // matching the boards.
            //
            // Same rule as the rest of the resize: transform work runs everywhere, only
            // rendering is client-side.
            if (!ResolveScaleSceneryLoaderArena()) { ReleaseSceneryScaler(); return; }

            Transform root = FindSceneryLoaderRoot();
            if (root == null) { ReleaseSceneryScaler(); return; }

            // Re-attach covers all three ways the link breaks: first sight, a scenery swap
            // that destroys the old root and instantiates a new one, and the loader
            // reparenting its content back onto the container underneath us.
            if (_sceneryScaler == null || root != _sceneryScaledRoot || root.parent != _sceneryScaler)
                AttachSceneryScaler(root);

            if (_sceneryScaler == null) return;

            // Exactly the delta the batched base geometry is drawn with, so the building
            // cannot drift from the rink standing in it: scale about the rink centre (world
            // origin), then translate by the arena offset.
            _sceneryScaler.localPosition = worldOffset;
            _sceneryScaler.localRotation = Quaternion.identity;
            _sceneryScaler.localScale = worldScale;
        }

        private static void AttachSceneryScaler(Transform root)
        {
            ReleaseSceneryScaler();

            Transform parent = root.parent != null ? root.parent : null;

            _sceneryBaseParent = parent;
            _sceneryBaseSiblingIndex = root.GetSiblingIndex();
            _sceneryBasePosition = root.localPosition;
            _sceneryBaseRotation = root.localRotation;
            _sceneryBaseScale = root.localScale;

            var scaler = new GameObject(SceneryScalerName).transform;

            // worldPositionStays false on both moves, and it matters twice. On the scaler it
            // keeps the fresh identity transform identity, which is what makes its
            // localScale read as a world-axis scale about the rink centre. On the content it
            // keeps the local transform the prefab was instantiated with, where the true
            // flag would have Unity divide out the scale we are about to apply and leave the
            // building at vanilla size with nothing visibly wrong.
            scaler.SetParent(parent, false);
            root.SetParent(scaler, false);

            root.localPosition = _sceneryBasePosition;
            root.localRotation = _sceneryBaseRotation;
            root.localScale = _sceneryBaseScale;

            _sceneryScaler = scaler;
            _sceneryScaledRoot = root;

            // The proxy scan skips what the scaler owns, but it only re-runs every two
            // seconds. Scenery that arrives between two scans and carries baked static
            // batching would spend that gap scaled by the scaler AND drawn by the proxy, so
            // pull the next scan forward to this frame and close the window.
            _nextProxyRescan = 0f;

            ReportSceneryLoaderOnce(root);
        }

        /// <summary>Puts the scenery back exactly where the loader left it.</summary>
        private static void ReleaseSceneryScaler()
        {
            Transform scaler = _sceneryScaler;
            Transform root = _sceneryScaledRoot;

            _sceneryScaler = null;
            _sceneryScaledRoot = null;

            if (scaler == null) return;

            // Reparent BEFORE the destroy. The scenery is a CHILD of the scaler, so
            // destroying the scaler first takes the whole building with it and the player is
            // left standing on a rink in an empty void.
            if (root != null && root.parent == scaler)
            {
                root.SetParent(_sceneryBaseParent != null ? _sceneryBaseParent : null, false);
                root.localPosition = _sceneryBasePosition;
                root.localRotation = _sceneryBaseRotation;
                root.localScale = _sceneryBaseScale;
                if (_sceneryBaseSiblingIndex >= 0) root.SetSiblingIndex(_sceneryBaseSiblingIndex);
            }

            UnityEngine.Object.Destroy(scaler.gameObject);

            _sceneryBaseParent = null;
            _sceneryBaseSiblingIndex = -1;
            _lastReportedSceneryLoader = 0;
        }

        private static Transform FindSceneryLoaderRoot()
        {
            FieldInfo field = ResolveSceneryLoaderField();
            if (field == null) return null;

            try
            {
                var go = field.GetValue(null) as GameObject;
                return go != null ? go.transform : null;
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.Dbg("Scenery Loader root read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves RinkSceneState.ActiveRoot, retrying on a timer while it is missing.
        ///
        /// A negative answer is deliberately NOT latched. The workshop DLL is loaded by the
        /// game's own mod loader, and caching "not installed" from the first probe would
        /// survive a scenery mod that finished loading a second later, leaving the feature
        /// dead for the session on exactly the machines it is for.
        /// </summary>
        private static FieldInfo ResolveSceneryLoaderField()
        {
            if (_sceneryActiveRootField != null) return _sceneryActiveRootField;
            if (Time.unscaledTime < _nextSceneryProbe) return null;
            _nextSceneryProbe = Time.unscaledTime + 5f;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(SceneryLoaderStateType, false); }
                catch { continue; }
                if (type == null) continue;

                _sceneryActiveRootField = type.GetField("ActiveRoot", BindingFlags.Public | BindingFlags.Static);
                if (_sceneryActiveRootField == null) continue;

                if (!_loggedSceneryLoaderFound)
                {
                    _loggedSceneryLoaderFound = true;
                    CompetitiveAdjustments.ConfigManager.Log(
                        $"Scenery Loader detected ({assembly.GetName().Name}); its arena will be stretched to fit the resized rink.");
                }

                return _sceneryActiveRootField;
            }

            return null;
        }

        private static bool ResolveScaleSceneryLoaderArena()
        {
            try
            {
                return DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ScaleSceneryLoaderArena != false;
            }
            catch { }

            return true;
        }

        private static int _lastReportedSceneryLoader;

        private static void ReportSceneryLoaderOnce(Transform root)
        {
            int id = root.GetInstanceID();
            if (id == _lastReportedSceneryLoader) return;
            _lastReportedSceneryLoader = id;

            int renderers = root.GetComponentsInChildren<Renderer>(true).Length;
            CompetitiveAdjustments.ConfigManager.Log(
                $"Scenery Loader content '{root.name}' ({renderers} renderer(s)) parented under '{SceneryScalerName}' " +
                "and stretched to the resized rink. Set ScaleSceneryLoaderArena = false in the client config to " +
                "leave it at vanilla size.");
        }
    }
}
