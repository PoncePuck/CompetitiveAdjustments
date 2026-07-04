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
        private static bool    _loggedVanillaServerSkip;

        // Vanilla board PhysicsMaterial, captured lazily from the original arena
        // colliders so rebuilt custom boards can fall back to stock bounce when
        // soft boards are disabled. Resolved-flag distinguishes "not looked yet"
        // from "looked, none found".
        private static PhysicsMaterial _vanillaBoardPhysicMaterial;
        private static bool _vanillaBoardPhysicMaterialResolved;

        private static void SyncArenaVisuals(
            bool enabled,
            float scaleX,
            float scaleY,
            float scaleZ,
            float offsetX,
            float offsetY,
            float offsetZ,
            float rotX,
            float rotY,
            float rotZ,
            float barrierScaleX,
            float barrierScaleY,
            float barrierScaleZ)
        {
            const float barrierRotX = 0f, barrierRotY = 0f, barrierRotZ = 0f;
            RefreshFrameBundleIfChanged();
            TryLoadFrameBundle();

            if (enabled)
            {
                ApplyNetworkBoundsPatches();
                HandleAudioEnvironment();
            }

            // ── Determine which prefab to use ────────────────────────────────
            // Prefer the unified ArenaAndColliders prefab.  Fall back to the
            // legacy split arena/colliders pair when the unified one is absent.
            GameObject effectivePrefab = _arenaAndCollidersPrefab ?? _arenaPrefab;

            if (!enabled || effectivePrefab == null)
            {
                if (enabled && effectivePrefab == null && !_loggedArenaPrefabMissing)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Arena visual tweaks enabled but no arena prefab found in bundle.");
                    _loggedArenaPrefabMissing = true;
                }

                if (_arenaInstance != null)
                {
                    UnityEngine.Object.Destroy(_arenaInstance);
                    _arenaInstance = null;
                }

                if (_collidersInstance != null)
                {
                    UnityEngine.Object.Destroy(_collidersInstance);
                    _collidersInstance = null;
                    _colliderLayersSynced = false;
                }

                DestroyBarrierStandalone();
                RestoreOriginalArenaColliders();
                _usingArenaVisualColliderFallback = false;
                _arenaAppearanceSynced = false;
                RestoreOriginalArenaRenderers();
                RemoveNetworkBoundsPatches();
                RestoreAudioEnvironment();
                return;
            }

            var arenaRoot = FindArenaRoot();
            if (arenaRoot == null)
            {
                if (!_loggedArenaRootMissing)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Could not find arena root in scene; custom arena visual not spawned.");
                    _loggedArenaRootMissing = true;
                }
                return;
            }

            _loggedArenaRootMissing = false;

            int arenaRootId = arenaRoot.GetInstanceID();
            if (_arenaRootInstanceId != arenaRootId)
            {
                _arenaRootInstanceId = arenaRootId;
                _arenaAppearanceSynced = false;
                _loggedArenaRendererMatches = false;
                // A different arena has different board materials; drop the cache.
                _vanillaBoardPhysicMaterial = null;
                _vanillaBoardPhysicMaterialResolved = false;
            }

            // ── Unified prefab path (ArenaAndColliders) ──────────────────────
            if (_arenaAndCollidersPrefab != null)
            {
                SyncUnifiedInstance(arenaRoot, scaleX, scaleY, scaleZ, offsetX, offsetY, offsetZ, rotX, rotY, rotZ, barrierRotX, barrierRotY, barrierRotZ, barrierScaleX, barrierScaleY, barrierScaleZ);
            }
            // ── Legacy split path (separate arena + colliders prefabs) ───────
            else
            {
                SyncLegacyArenaInstance(arenaRoot, scaleX, scaleY, scaleZ, offsetX, offsetY, offsetZ, rotX, rotY, rotZ, barrierRotX, barrierRotY, barrierRotZ, barrierScaleX, barrierScaleY, barrierScaleZ);
            }

            if (!_arenaAppearanceSynced)
            {
                var visualRoot = _arenaInstance;
                if (visualRoot != null)
                {
                    SyncArenaVisualAppearance(arenaRoot, visualRoot.transform);
                    _arenaAppearanceSynced = true;
                }
            }
            else
            {
                // Every tick after initial setup: propagate texture/color changes from the
                // (hidden) source renderers to our custom clones.  Smoothness and metallic
                // are intentionally skipped so other mods can control those values freely.
                LiveSyncArenaSourceTextures();
            }

            if (_arenaInstance != null)
                HideOriginalArenaRenderers(arenaRoot, _arenaInstance.transform);

            //BoardColliderPatch.Postfix();
        }

        // ── Unified prefab: one instance has visuals + Colliders child ───────
        private static void SyncUnifiedInstance(
            Transform arenaRoot,
            float scaleX, float scaleY, float scaleZ,
            float offsetX, float offsetY, float offsetZ,
            float rotX, float rotY, float rotZ,
            float barrierRotX, float barrierRotY, float barrierRotZ,
            float barrierScaleX, float barrierScaleY, float barrierScaleZ)
        {
            bool needsNewInstance = _arenaInstance == null
                || !string.Equals(_arenaInstance.name, UnifiedInstanceName, StringComparison.Ordinal)
                || _arenaInstance.transform.parent != arenaRoot;

            if (needsNewInstance)
            {
                if (_arenaInstance != null)
                    UnityEngine.Object.Destroy(_arenaInstance);
                if (_collidersInstance != null)
                {
                    UnityEngine.Object.Destroy(_collidersInstance);
                    _collidersInstance = null;
                    _colliderLayersSynced = false;
                }

                _arenaInstance = UnityEngine.Object.Instantiate(_arenaAndCollidersPrefab);
                _arenaInstance.name = UnifiedInstanceName;
                _arenaInstance.transform.SetParent(arenaRoot, false);// Parent to arena root so it inherits arena transform changes (e.g. from other mods) automatically.
                _arenaAppearanceSynced = false;
                _colliderLayersSynced = false;
                _usingArenaVisualColliderFallback = false;

                // Find the Colliders child and bookmark it
                var collidersChild = _arenaInstance.transform.Find(CollidersChildName);
                if (collidersChild != null)
                {
                    _collidersInstance = collidersChild.gameObject;
                    int generated = EnsureCustomColliderComponents(collidersChild);
                    int barrierOverrides = SyncBarrierColliderOverridesFromOriginal(
                        arenaRoot,
                        collidersChild,
                        scaleX,
                        scaleY,
                        scaleZ,
                        rotX,
                        rotY,
                        rotZ,
                        barrierRotX,
                        barrierRotY,
                        barrierRotZ,
                        barrierScaleX,
                        barrierScaleY,
                        barrierScaleZ);
                    int colliderCount = collidersChild.GetComponentsInChildren<Collider>(true).Length;
                    if (colliderCount > 0)
                    {
                        DisableOriginalArenaColliders(arenaRoot);
                        Debug.Log($"[COMPADJUST] Unified prefab spawned: {colliderCount} colliders ({generated} auto-generated, {barrierOverrides} barrier overrides) + visuals.");
                    }
                    else
                    {
                        CompetitiveAdjustments.ConfigManager.LogWarning("Unified prefab 'Colliders' child has no Collider components.");
                    }
                }
                else
                {
                    Debug.LogWarning($"[COMPADJUST] Unified prefab has no '{CollidersChildName}' child; colliders will not be replaced.");
                }

                if (!_loggedArenaSpawned)
                {
                    Debug.Log($"[COMPADJUST] Spawned unified arena+colliders under '{arenaRoot.name}'.");
                    _loggedArenaSpawned = true;
                }
            }
            else
            {
                // Existing instance – just ensure colliders child ref is valid
                if (_collidersInstance == null)
                {
                    var collidersChild = _arenaInstance.transform.Find(CollidersChildName);
                    if (collidersChild != null)
                    {
                        _collidersInstance = collidersChild.gameObject;
                        EnsureCustomColliderComponents(collidersChild);
                        SyncBarrierColliderOverridesFromOriginal(
                            arenaRoot,
                            collidersChild,
                            scaleX,
                            scaleY,
                            scaleZ,
                            rotX,
                            rotY,
                            rotZ,
                            barrierRotX,
                            barrierRotY,
                            barrierRotZ,
                            barrierScaleX,
                            barrierScaleY,
                            barrierScaleZ);
                    }
                }
                else
                {
                    EnsureCustomColliderComponents(_collidersInstance.transform);
                    SyncBarrierColliderOverridesFromOriginal(
                        arenaRoot,
                        _collidersInstance.transform,
                        scaleX,
                        scaleY,
                        scaleZ,
                        rotX,
                        rotY,
                        rotZ,
                        barrierRotX,
                        barrierRotY,
                        barrierRotZ,
                        barrierScaleX,
                        barrierScaleY,
                        barrierScaleZ);
                }
            }

            _arenaInstance.transform.localPosition = new Vector3(offsetX, offsetY, offsetZ);
            _arenaInstance.transform.localRotation = Quaternion.Euler(rotX, rotY, rotZ);
            // b1117 base rink is ~1.5x the bundled prefab, so scale the whole instance
            // (visual + wall colliders + corner barrier override, all children) up to
            // base size at ArenaScale 1.0. Spawns/minimap use the vanilla rink as their
            // reference, so they line up once the arena matches base.
            // Correction on the two horizontal axes only; localScale.z (config ArenaScaleZ
            // -> world Y height) is EXCLUDED so the boards keep base height. This makes the
            // instance non-uniform, which would shear the rounded barrier -- so the barrier
            // is built as a standalone object under the (unscaled, unrotated) rink root
            // instead of as a child here (see SyncBarrierColliderOverridesFromOriginal).
            _arenaInstance.transform.localScale = new Vector3(scaleX * ArenaBaseScaleCorrection, scaleY * ArenaBaseScaleCorrection, scaleZ);

            // Sync layers and debug brushes on the Colliders sub-tree
            if (_collidersInstance != null)
            {
                SyncCustomColliderLayersAndStates(arenaRoot, _collidersInstance.transform);
                SyncArenaColliderDebugBrushes(_collidersInstance.transform);
            }
        }

        // ── Legacy split path (old arena.prefab + colliders.prefab) ──────────
        private static void SyncLegacyArenaInstance(
            Transform arenaRoot,
            float scaleX, float scaleY, float scaleZ,
            float offsetX, float offsetY, float offsetZ,
            float rotX, float rotY, float rotZ,
            float barrierRotX, float barrierRotY, float barrierRotZ,
            float barrierScaleX, float barrierScaleY, float barrierScaleZ)
        {
            if (_arenaInstance == null)
            {
                _arenaInstance = UnityEngine.Object.Instantiate(_arenaPrefab);
                _arenaInstance.name = "CustomArenaVisual";
                _arenaInstance.transform.SetParent(arenaRoot, false);
                _arenaAppearanceSynced = false;
                if (!_loggedArenaSpawned)
                {
                    Debug.Log($"[COMPADJUST] Spawned custom arena visual under '{arenaRoot.name}'.");
                    _loggedArenaSpawned = true;
                }
            }
            else if (_arenaInstance.transform.parent != arenaRoot)
            {
                UnityEngine.Object.Destroy(_arenaInstance);
                _arenaInstance = UnityEngine.Object.Instantiate(_arenaPrefab);
                _arenaInstance.name = "CustomArenaVisual";
                _arenaInstance.transform.SetParent(arenaRoot, false);
                _loggedArenaRendererMatches = false;
                _arenaAppearanceSynced = false;
            }

            _arenaInstance.transform.localPosition = new Vector3(offsetX, offsetY, offsetZ);
            _arenaInstance.transform.localRotation = Quaternion.Euler(rotX, rotY, rotZ);
            // b1117 base rink is ~1.5x the bundled prefab, so scale the whole instance
            // (visual + wall colliders + corner barrier override, all children) up to
            // base size at ArenaScale 1.0. Spawns/minimap use the vanilla rink as their
            // reference, so they line up once the arena matches base.
            // Correction on the two horizontal axes only; localScale.z (config ArenaScaleZ
            // -> world Y height) is EXCLUDED so the boards keep base height. This makes the
            // instance non-uniform, which would shear the rounded barrier -- so the barrier
            // is built as a standalone object under the (unscaled, unrotated) rink root
            // instead of as a child here (see SyncBarrierColliderOverridesFromOriginal).
            _arenaInstance.transform.localScale = new Vector3(scaleX * ArenaBaseScaleCorrection, scaleY * ArenaBaseScaleCorrection, scaleZ);

            // Colliders from separate prefab or scene clone
            if (_collidersPrefab != null)
            {
                _usingArenaVisualColliderFallback = false;
                SyncLegacyColliders(arenaRoot, scaleX, scaleY, scaleZ, offsetX, offsetY, offsetZ, rotX, rotY, rotZ, barrierRotX, barrierRotY, barrierRotZ, barrierScaleX, barrierScaleY, barrierScaleZ);
            }
            else
            {
                SyncArenaCollidersFromSceneClone(arenaRoot, _arenaInstance.transform, scaleX, scaleY, scaleZ, offsetX, offsetY, offsetZ, rotX, rotY, rotZ);
            }
        }

        private static void SyncLegacyColliders(
            Transform arenaRoot,
            float scaleX, float scaleY, float scaleZ,
            float offsetX, float offsetY, float offsetZ,
            float rotX, float rotY, float rotZ,
            float barrierRotX, float barrierRotY, float barrierRotZ,
            float barrierScaleX, float barrierScaleY, float barrierScaleZ)
        {
            if (_collidersPrefab == null) return;

            bool needsPrefabInstance = _collidersInstance == null
                || !string.Equals(_collidersInstance.name, "CustomArenaColliders", StringComparison.Ordinal)
                || _collidersInstance.transform.parent != arenaRoot;

            if (needsPrefabInstance)
            {
                if (_collidersInstance != null)
                    UnityEngine.Object.Destroy(_collidersInstance);

                _collidersInstance = UnityEngine.Object.Instantiate(_collidersPrefab);
                _collidersInstance.name = "CustomArenaColliders";
                _collidersInstance.transform.SetParent(arenaRoot, false);
                _colliderLayersSynced = false;

                int generated = EnsureCustomColliderComponents(_collidersInstance.transform);
                int barrierOverrides = SyncBarrierColliderOverridesFromOriginal(
                    arenaRoot,
                    _collidersInstance.transform,
                    scaleX,
                    scaleY,
                    scaleZ,
                    rotX,
                    rotY,
                    rotZ,
                    barrierRotX,
                    barrierRotY,
                    barrierRotZ,
                    barrierScaleX,
                    barrierScaleY,
                    barrierScaleZ);
                int customCount = _collidersInstance.GetComponentsInChildren<Collider>(true).Length;
                if (customCount > 0)
                {
                    DisableOriginalArenaColliders(arenaRoot);
                    Debug.Log($"[COMPADJUST] Spawned custom arena colliders ({customCount} total, {generated} auto-generated, {barrierOverrides} barrier overrides).");
                }
                else
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Custom colliders prefab has no Collider components.");
                }
            }
            else
            {
                EnsureCustomColliderComponents(_collidersInstance.transform);
                SyncBarrierColliderOverridesFromOriginal(
                    arenaRoot,
                    _collidersInstance.transform,
                    scaleX,
                    scaleY,
                    scaleZ,
                    rotX,
                    rotY,
                    rotZ,
                    barrierRotX,
                    barrierRotY,
                    barrierRotZ,
                    barrierScaleX,
                    barrierScaleY,
                    barrierScaleZ);
            }

            _collidersInstance.transform.localPosition = new Vector3(offsetX, offsetY, offsetZ);
            _collidersInstance.transform.localRotation = Quaternion.Euler(rotX, rotY, rotZ);
            _collidersInstance.transform.localScale = new Vector3(scaleX, scaleY, scaleZ);

            SyncCustomColliderLayersAndStates(arenaRoot, _collidersInstance.transform);
            SyncArenaColliderDebugBrushes(_collidersInstance.transform);
        }

        private static void SyncArenaCollidersFromSceneClone(
            Transform arenaRoot,
            Transform customArenaRoot,
            float scaleX, float scaleY, float scaleZ,
            float offsetX, float offsetY, float offsetZ,
            float rotX, float rotY, float rotZ)
        {
            if (arenaRoot == null) return;

            bool needsNewInstance = _collidersInstance == null
                || _collidersInstance.transform.parent != arenaRoot
                || !string.Equals(_collidersInstance.name, "CustomArenaCollidersFromScene", StringComparison.Ordinal);

            if (needsNewInstance)
            {
                if (_collidersInstance != null)
                    UnityEngine.Object.Destroy(_collidersInstance);

                _collidersInstance = new GameObject("CustomArenaCollidersFromScene");
                _collidersInstance.transform.SetParent(arenaRoot, false);
            }

            _collidersInstance.transform.localPosition = new Vector3(offsetX, offsetY, offsetZ);
            _collidersInstance.transform.localRotation = Quaternion.Euler(rotX, rotY, rotZ);
            _collidersInstance.transform.localScale = new Vector3(scaleX, scaleY, scaleZ);

            bool rebuild = needsNewInstance || _collidersInstance.GetComponentsInChildren<Collider>(true).Length == 0;
            if (rebuild)
            {
                for (int i = _collidersInstance.transform.childCount - 1; i >= 0; i--)
                {
                    var child = _collidersInstance.transform.GetChild(i);
                    if (child != null)
                        UnityEngine.Object.Destroy(child.gameObject);
                }

                int created = 0;
                foreach (var source in arenaRoot.GetComponentsInChildren<Collider>(true))
                {
                    if (source == null) continue;
                    if (!source.enabled) continue;
                    if (_collidersInstance != null && (source.transform == _collidersInstance.transform || source.transform.IsChildOf(_collidersInstance.transform)))
                        continue;
                    if (customArenaRoot != null && (source.transform == customArenaRoot || source.transform.IsChildOf(customArenaRoot)))
                        continue;
                    if (!ShouldHideOriginalArenaCollider(source, arenaRoot))
                        continue;

                    if (TryCloneCollider(source, arenaRoot, _collidersInstance.transform))
                        created++;
                }

                if (created > 0)
                {
                    _usingArenaVisualColliderFallback = true;
                    DisableOriginalArenaColliders(arenaRoot);
                    SyncCustomColliderLayersAndStates(arenaRoot, _collidersInstance.transform);
                    SyncArenaColliderDebugBrushes(_collidersInstance.transform);

                    Debug.Log($"[COMPADJUST] Using scene-collider clone fallback ({created} colliders) with arena visual transform.");
                    _loggedArenaColliderFallback = true;
                }
                else
                {
                    RestoreOriginalArenaColliders();
                    _usingArenaVisualColliderFallback = false;
                    if (!_loggedArenaColliderFallback)
                    {
                        CompetitiveAdjustments.ConfigManager.LogWarning("Scene-collider clone fallback found no source colliders to clone.");
                        _loggedArenaColliderFallback = true;
                    }
                }
            }
            else if (_usingArenaVisualColliderFallback)
            {
                SyncCustomColliderLayersAndStates(arenaRoot, _collidersInstance.transform);
                SyncArenaColliderDebugBrushes(_collidersInstance.transform);
            }
        }

        private static bool TryCloneCollider(Collider source, Transform arenaRoot, Transform cloneRoot)
        {
            if (source == null || arenaRoot == null || cloneRoot == null) return false;

            // Fix for any translation done in y to the colliders.
            source.transform.position = new Vector3(source.transform.position.x, 0, source.transform.position.z);

            var cloneTransform = GetOrCreateCloneTransform(source.transform, arenaRoot, cloneRoot);
            if (cloneTransform == null) return false;

            var cloneGo = cloneTransform.gameObject;
            cloneGo.layer = source.gameObject.layer;

            Collider clone = null;

            if (source is BoxCollider sourceBox)
            {
                var box = cloneGo.AddComponent<BoxCollider>();
                box.center = sourceBox.center;
                box.size = sourceBox.size;
                clone = box;
            }
            else if (source is SphereCollider sourceSphere)
            {
                var sphere = cloneGo.AddComponent<SphereCollider>();
                sphere.center = sourceSphere.center;
                sphere.radius = sourceSphere.radius;
                clone = sphere;
            }
            else if (source is CapsuleCollider sourceCapsule)
            {
                var capsule = cloneGo.AddComponent<CapsuleCollider>();
                capsule.center = sourceCapsule.center;
                capsule.radius = sourceCapsule.radius;
                capsule.height = sourceCapsule.height;
                capsule.direction = sourceCapsule.direction;
                clone = capsule;
            }
            else if (source is MeshCollider sourceMesh)
            {
                var mesh = cloneGo.AddComponent<MeshCollider>();
                mesh.sharedMesh = sourceMesh.sharedMesh;
                mesh.convex = sourceMesh.convex;
                mesh.cookingOptions = sourceMesh.cookingOptions;
                clone = mesh;
            }

            if (clone == null)
            {
                UnityEngine.Object.Destroy(cloneGo);
                return false;
            }

            CopyColliderCommonSettings(source, clone);

            return true;
        }

        private static int SyncBarrierColliderOverridesFromOriginal(
            Transform arenaRoot,
            Transform customCollidersRoot,
            float scaleX,
            float scaleY,
            float scaleZ,
            float arenaRotX,
            float arenaRotY,
            float arenaRotZ,
            float barrierRotX,
            float barrierRotY,
            float barrierRotZ,
            float barrierScaleX,
            float barrierScaleY,
            float barrierScaleZ)
        {
            if (arenaRoot == null || customCollidersRoot == null) return 0;

            const string barrierOverrideRootName = "__originalBarrierOverrides";
            // Parent the barrier override to the RINK ROOT, not the arena instance. The
            // instance is non-uniformly scaled (height excluded) and rotated; a rounded
            // perimeter mesh under it would shear. The rink root is unscaled and world
            // aligned, so here the barrier is cloned 1:1 from the (base-size) vanilla
            // barrier and then scaled cleanly by the raw config scale in world axes:
            // world X = ArenaScaleX (width), world Y = ArenaScaleZ (height), world Z =
            // ArenaScaleY (length). No ArenaBaseScaleCorrection here -- the vanilla barrier
            // is already base-size, so raw config makes it track the corrected arena.
            var overrideRoot = arenaRoot.Find(barrierOverrideRootName);
            if (overrideRoot == null)
            {
                var go = new GameObject(barrierOverrideRootName);
                go.transform.SetParent(arenaRoot, false);
                overrideRoot = go.transform;
            }
            _barrierStandalone = overrideRoot;

            overrideRoot.localPosition = Vector3.zero;
            overrideRoot.localRotation = Quaternion.Euler(barrierRotX, barrierRotY, barrierRotZ);
            overrideRoot.localScale = new Vector3(
                scaleX > 0f ? scaleX : 1f,
                scaleZ > 0f ? scaleZ : 1f,
                scaleY > 0f ? scaleY : 1f);

            if (overrideRoot.GetComponentsInChildren<Collider>(true).Length > 0)
                return 0;

            int cloned = 0;
            foreach (var source in arenaRoot.GetComponentsInChildren<Collider>(true))
            {
                if (source == null) continue;
                if (source.transform == overrideRoot || source.transform.IsChildOf(overrideRoot))
                    continue;
                if (_arenaInstance != null && (source.transform == _arenaInstance.transform || source.transform.IsChildOf(_arenaInstance.transform)))
                    continue;
                if (_collidersInstance != null && (source.transform == _collidersInstance.transform || source.transform.IsChildOf(_collidersInstance.transform)))
                    continue;

                string path = GetRelativeTransformPath(arenaRoot, source.transform);
                string part = DetermineColliderPartKey((source.name ?? string.Empty) + "/" + path);
                if (!string.Equals(part, "barrier", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryCloneCollider(source, arenaRoot, overrideRoot))
                    continue;

                cloned++;

                if (source.enabled)
                {
                    source.enabled = false;
                    if (!_disabledOriginalColliders.Contains(source))
                        _disabledOriginalColliders.Add(source);
                }
            }

            // The standalone barrier is not visited by SyncCustomColliderLayersAndStates
            // (that only walks the arena instance's Colliders child), so set its tag here.
            // Clones already inherit the source's Boards layer via TryCloneCollider.
            foreach (var bc in overrideRoot.GetComponentsInChildren<Collider>(true))
                if (bc != null) { try { bc.gameObject.tag = "Soft Collider"; } catch { } }

            if (cloned > 0)
                Debug.Log($"[COMPADJUST] Cloned {cloned} original barrier collider(s) into the standalone rink-root barrier for clean non-uniform scaling.");

            return cloned;
        }

        private static void CopyColliderCommonSettings(Collider source, Collider clone)
        {
            if (source == null || clone == null) return;

            clone.enabled = source.enabled;
            clone.isTrigger = source.isTrigger;
            clone.sharedMaterial = source.sharedMaterial;
            clone.contactOffset = source.contactOffset;

            TryCopyColliderProperty(source, clone, "includeLayers");
            TryCopyColliderProperty(source, clone, "excludeLayers");
            TryCopyColliderProperty(source, clone, "layerOverridePriority");
            TryCopyColliderProperty(source, clone, "providesContacts");
            TryCopyColliderProperty(source, clone, "hasModifiableContacts");
        }

        private static void TryCopyColliderProperty(Collider source, Collider clone, string propertyName)
        {
            try
            {
                var property = typeof(Collider).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanRead || !property.CanWrite)
                    return;

                var value = property.GetValue(source, null);
                property.SetValue(clone, value, null);
            }
            catch { }
        }

        private static Transform GetOrCreateCloneTransform(Transform source, Transform arenaRoot, Transform cloneRoot)
        {
            if (source == null || arenaRoot == null || cloneRoot == null) return null;

            var chain = new System.Collections.Generic.List<Transform>();
            var current = source;
            while (current != null && current != arenaRoot)
            {
                chain.Add(current);
                current = current.parent;
            }

            if (current != arenaRoot)
                return null;

            chain.Reverse();

            var parent = cloneRoot;
            for (int i = 0; i < chain.Count; i++)
            {
                var sourceTransform = chain[i];
                string cloneName = BuildCloneNodeName(sourceTransform);
                Transform child = parent.Find(cloneName);
                if (child == null)
                {
                    var cloneGo = new GameObject(cloneName);
                    child = cloneGo.transform;
                    child.SetParent(parent, false);
                }

                child.localPosition = sourceTransform.localPosition;
                child.localRotation = sourceTransform.localRotation;
                child.localScale = sourceTransform.localScale;
                child.gameObject.layer = sourceTransform.gameObject.layer;

                parent = child;
            }

            return parent;
        }

        private static string BuildCloneNodeName(Transform sourceTransform)
        {
            if (sourceTransform == null) return "CloneNode";
            return $"{sourceTransform.name}__sib{sourceTransform.GetSiblingIndex()}";
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

        // The game stores _Smoothness=0 in every shared material because gloss is baked
        // into lightmaps.  These are the values we substitute for dynamic (probe-lit) use.
        private static float GetDynamicSmoothnessByPart(string part)
        {
            switch (part)
            {
                case "ice":
                case "ice_bottom": return 0.88f;
                case "glass":      return 1.00f;
                case "barrier":    return 0.45f;
                default:           return 0.30f;
            }
        }

        private static void SyncArenaVisualAppearance(Transform arenaRoot, Transform customArenaRoot)
        {
            if (arenaRoot == null || customArenaRoot == null) return;

            // Clear stale pairs — we're about to repopulate for this arena instance.
            _arenaRendererPairs.Clear();

            int loggedCount = 0;

            foreach (var dst in customArenaRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (dst == null) continue;

                // Renderers inside the Colliders sub-tree are invisible collision geometry.
                // They must not cast or receive shadows — an unsuppressed top/ceiling
                // collider mesh will project a shadow over the entire arena floor.
                // Exception: __clipBrush debug visualizers are managed by SyncArenaColliderDebugBrushes.
                if (_collidersInstance != null && dst.transform.IsChildOf(_collidersInstance.transform))
                {
                    if (string.Equals(dst.gameObject.name, "__clipBrush", StringComparison.Ordinal))
                        continue; // leave debug brushes alone
                    dst.shadowCastingMode = ShadowCastingMode.Off;
                    dst.receiveShadows    = false;
                    dst.enabled           = false;
                    continue;
                }
                // Skip debug clip-brush renderers outside colliders sub-tree too
                if (string.Equals(dst.gameObject.name, "__clipBrush", StringComparison.Ordinal))
                    continue;

                string dstPath = GetRelativeTransformPath(customArenaRoot, dst.transform);
                string dstPart = DetermineArenaPartKey(dst.name + "/" + dstPath);

                // The custom bundle has both Ice Top and Ice Bottom as near-coplanar surfaces.
                // Keep only the textured base layer to avoid z-fighting artifacts.
                if (dstPart == "ice_top")
                {
                    dst.enabled = false;
                    continue;
                }

                var src = FindBestArenaSourceRenderer(arenaRoot, customArenaRoot, dst);

                if (src != null)
                {
                    var srcMats = CreateMirroredMaterials(src);
                    if (srcMats != null && srcMats.Length > 0)
                    {
                        // The game bakes ALL smoothness into lightmaps — every shared material
                        // has _Smoothness=0. Override with per-part values for dynamic rendering.
                        float overrideSmooth = GetDynamicSmoothnessByPart(dstPart);
                        foreach (var mat in srcMats)
                        {
                            if (mat == null) continue;
                            if (mat.HasProperty("_Smoothness"))  mat.SetFloat("_Smoothness",  overrideSmooth);
                            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", overrideSmooth);
                        }
                        dst.materials = srcMats;
                    }

                    // Do NOT copy the game's MaterialPropertyBlock. The base game arena is
                    // baked and its block may contain smoothness/gloss overrides that were
                    // tuned for static lightmaps — copying them suppresses gloss entirely
                    // on our dynamic custom renderers.

                    // Record the dst→src pair for live per-tick texture/color propagation.
                    _arenaRendererPairs.Add((dst, src));

                    // Copy rendering layer mask so the game's URP lights (directional,
                    // point, spot) actually illuminate our custom renderers.
                    dst.renderingLayerMask = src.renderingLayerMask;

                    if (!_loggedArenaRendererMatches && loggedCount < 6)
                    {
                        string srcPath = GetRelativeTransformPath(arenaRoot, src.transform);
                        var firstSrcMat = src.sharedMaterials != null && src.sharedMaterials.Length > 0 ? src.sharedMaterials[0] : null;
                        string srcMatName = firstSrcMat != null ? firstSrcMat.name : "<none>";
                        float smoothness = firstSrcMat != null && firstSrcMat.HasProperty("_Smoothness")  ? firstSrcMat.GetFloat("_Smoothness")  :
                                          (firstSrcMat != null && firstSrcMat.HasProperty("_Glossiness") ? firstSrcMat.GetFloat("_Glossiness") : -1f);
                        float metallic   = firstSrcMat != null && firstSrcMat.HasProperty("_Metallic")   ? firstSrcMat.GetFloat("_Metallic")   : -1f;
                        Debug.Log($"[COMPADJUST] Arena renderer match: '{dstPath}' <= '{srcPath}' mat='{srcMatName}' smoothness={smoothness:F3} metallic={metallic:F3}.");
                        loggedCount++;
                    }
                }
                else
                {
                    // No source match: clean the bundle material's shadow/reflection
                    // keywords AND apply the per-part dynamic smoothness so the
                    // surface isn't silently rendered flat/dark. Resized-rink glass
                    // and windows rely on this to pick up environment reflections
                    // when the vanilla arena has no equivalently-named renderer to
                    // mirror from (otherwise they keep the bundle's flat default).
                    float overrideSmooth = GetDynamicSmoothnessByPart(dstPart);
                    foreach (var mat in dst.materials)
                    {
                        if (mat == null) continue;
                        mat.DisableKeyword("_RECEIVE_SHADOWS_OFF");
                        mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                        mat.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                        mat.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
                        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", overrideSmooth);
                        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", overrideSmooth);
                    }
                }

                // Always force probe sampling and shadow participation on every custom arena
                // renderer, regardless of whether a source match was found. The base game's
                // baked arena uses lightProbeUsage/reflectionProbeUsage = Off (lightmaps).
                // Keeping those settings kills ambient light and reflections on our dynamic
                // meshes. BlendProbes ensures we sample the scene's runtime probes.
                dst.lightProbeUsage      = LightProbeUsage.BlendProbes;
                dst.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                dst.shadowCastingMode    = ShadowCastingMode.On;
                dst.receiveShadows       = true;
                dst.enabled              = true;
            }

            if (loggedCount > 0)
                _loggedArenaRendererMatches = true;
        }

        private static bool ShouldScaleArenaBoundaryCollider(Collider collider, Transform arenaRoot)
        {
            if (collider == null || arenaRoot == null) return false;

            string path = GetRelativeTransformPath(arenaRoot, collider.transform);
            string text = (collider.name ?? string.Empty) + "/" + path;

            bool hasCollider = text.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasLeft = text.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasRight = text.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFront = text.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBack = text.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBorder = text.IndexOf("border", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("boarder", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("boards", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBarrier = text.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasBorder)
                return true;

            if (hasCollider && hasBarrier)
                return true;

            if (!hasCollider) return false;
            return hasLeft || hasRight || hasFront || hasBack;
        }

        private static void SyncArenaBoundaryColliders(Transform arenaRoot, Transform customArenaRoot, float scaleX, float scaleY, float scaleZ)
        {
            if (arenaRoot == null) return;

            for (int i = _scaledArenaBoundaryColliders.Count - 1; i >= 0; i--)
            {
                if (_scaledArenaBoundaryColliders[i] == null)
                    _scaledArenaBoundaryColliders.RemoveAt(i);
            }

            foreach (var collider in arenaRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                if (customArenaRoot != null && (collider.transform == customArenaRoot || collider.transform.IsChildOf(customArenaRoot)))
                    continue;
                if (!ShouldScaleArenaBoundaryCollider(collider, arenaRoot))
                    continue;

                int id = collider.GetInstanceID();

                if (collider is BoxCollider box)
                {
                    if (!_arenaBoxColliderBaseSize.ContainsKey(id))
                    {
                        _arenaBoxColliderBaseSize[id] = box.size;
                        _arenaBoxColliderBaseCenter[id] = box.center;
                    }

                    var baseSize = _arenaBoxColliderBaseSize[id];
                    var baseCenter = _arenaBoxColliderBaseCenter[id];
                    box.size = new Vector3(baseSize.x * scaleX, baseSize.y * scaleY, baseSize.z * scaleZ);
                    box.center = new Vector3(baseCenter.x * scaleX, baseCenter.y * scaleY, baseCenter.z * scaleZ);
                }
                else if (collider is CapsuleCollider capsule)
                {
                    if (!_arenaCapsuleColliderBaseRadius.ContainsKey(id))
                    {
                        _arenaCapsuleColliderBaseRadius[id] = capsule.radius;
                        _arenaCapsuleColliderBaseHeight[id] = capsule.height;
                        _arenaCapsuleColliderBaseCenter[id] = capsule.center;
                    }

                    float horizontalScale = (Mathf.Abs(scaleX) + Mathf.Abs(scaleZ)) * 0.5f;
                    capsule.radius = _arenaCapsuleColliderBaseRadius[id] * horizontalScale;
                    capsule.height = _arenaCapsuleColliderBaseHeight[id] * scaleY;
                    var baseCenter = _arenaCapsuleColliderBaseCenter[id];
                    capsule.center = new Vector3(baseCenter.x * scaleX, baseCenter.y * scaleY, baseCenter.z * scaleZ);
                }
                else if (collider is SphereCollider sphere)
                {
                    if (!_arenaSphereColliderBaseRadius.ContainsKey(id))
                    {
                        _arenaSphereColliderBaseRadius[id] = sphere.radius;
                        _arenaSphereColliderBaseCenter[id] = sphere.center;
                    }

                    float horizontalScale = (Mathf.Abs(scaleX) + Mathf.Abs(scaleZ)) * 0.5f;
                    sphere.radius = _arenaSphereColliderBaseRadius[id] * horizontalScale;
                    var baseCenter = _arenaSphereColliderBaseCenter[id];
                    sphere.center = new Vector3(baseCenter.x * scaleX, baseCenter.y * scaleY, baseCenter.z * scaleZ);
                }
                else if (collider is MeshCollider)
                {
                    var tr = collider.transform;
                    if (!_arenaMeshColliderBaseScale.ContainsKey(id))
                    {
                        _arenaMeshColliderBaseScale[id] = tr.localScale;
                    }

                    var baseScale = _arenaMeshColliderBaseScale[id];
                    tr.localScale = new Vector3(baseScale.x * scaleX, baseScale.y * scaleY, baseScale.z * scaleZ);
                }

                if (!_scaledArenaBoundaryColliders.Contains(collider))
                    _scaledArenaBoundaryColliders.Add(collider);
            }
        }

        private static void RestoreArenaBoundaryColliders()
        {
            foreach (var collider in _scaledArenaBoundaryColliders)
            {
                if (collider == null) continue;

                int id = collider.GetInstanceID();
                if (collider is BoxCollider box)
                {
                    if (_arenaBoxColliderBaseSize.TryGetValue(id, out var size)) box.size = size;
                    if (_arenaBoxColliderBaseCenter.TryGetValue(id, out var center)) box.center = center;
                }
                else if (collider is CapsuleCollider capsule)
                {
                    if (_arenaCapsuleColliderBaseRadius.TryGetValue(id, out var radius)) capsule.radius = radius;
                    if (_arenaCapsuleColliderBaseHeight.TryGetValue(id, out var height)) capsule.height = height;
                    if (_arenaCapsuleColliderBaseCenter.TryGetValue(id, out var center)) capsule.center = center;
                }
                else if (collider is SphereCollider sphere)
                {
                    if (_arenaSphereColliderBaseRadius.TryGetValue(id, out var radius)) sphere.radius = radius;
                    if (_arenaSphereColliderBaseCenter.TryGetValue(id, out var center)) sphere.center = center;
                }
                else if (collider is MeshCollider)
                {
                    if (_arenaMeshColliderBaseScale.TryGetValue(id, out var scale))
                        collider.transform.localScale = scale;
                }
            }

            _scaledArenaBoundaryColliders.Clear();
        }

        private static int EnsureCustomColliderComponents(Transform customCollidersRoot)
        {
            if (customCollidersRoot == null) return 0;

            int created = 0;
            int meshCreated = 0;
            int boxCreated = 0;

            foreach (var meshFilter in customCollidersRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;

                var go = meshFilter.gameObject;
                if (meshFilter.transform == customCollidersRoot) continue;
                if (string.Equals(go.name, "__clipBrush", StringComparison.Ordinal)) continue;
                if (go.name.IndexOf("colliders", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                bool hasChildMeshFilter = false;
                for (int i = 0; i < meshFilter.transform.childCount; i++)
                {
                    var child = meshFilter.transform.GetChild(i);
                    if (child != null && child.GetComponent<MeshFilter>() != null)
                    {
                        hasChildMeshFilter = true;
                        break;
                    }
                }
                if (hasChildMeshFilter) continue;

                if (go.GetComponent<Collider>() != null) continue;

                string meshPath = GetRelativeTransformPath(customCollidersRoot, meshFilter.transform);
                string meshPart = DetermineColliderPartKey((go.name ?? string.Empty) + "/" + meshPath);

                if (meshFilter.sharedMesh.isReadable)
                {
                    var meshCollider = go.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = meshFilter.sharedMesh;
                    meshCollider.convex = false;
                    meshCollider.isTrigger = false;
                    created++;
                    meshCreated++;
                    continue;
                }

                // Non-readable barrier meshes have an AABB that spans the full
                // rink perimeter, which produces unusable fallback colliders.
                // Keep original arena barrier colliders enabled instead.
                if (string.Equals(meshPart, "barrier", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[COMPADJUST] Skipping non-readable barrier fallback collider at '{meshPath}'. Original barrier colliders remain active.");
                    continue;
                }

                var renderer = go.GetComponent<Renderer>();
                var boxCollider = go.AddComponent<BoxCollider>();
                var bounds = meshFilter.sharedMesh.bounds;
                boxCollider.center = bounds.center;
                boxCollider.size = bounds.size;

                boxCollider.isTrigger = false;
                created++;
                boxCreated++;
            }

            if (created > 0)
                Debug.Log($"[COMPADJUST] Auto-generated {created} collider components for custom arena colliders ({meshCreated} mesh, {boxCreated} box fallback).");

            return created;
        }

        private static void DisableOriginalArenaColliders(Transform arenaRoot)
        {
            if (arenaRoot == null) return;

            _disabledOriginalColliders.RemoveAll(col => col == null);

            foreach (var col in arenaRoot.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                if (_arenaInstance != null && (col.transform == _arenaInstance.transform || col.transform.IsChildOf(_arenaInstance.transform)))
                    continue;
                if (_collidersInstance != null && (col.transform == _collidersInstance.transform || col.transform.IsChildOf(_collidersInstance.transform)))
                    continue;
                if (_barrierStandalone != null && (col.transform == _barrierStandalone || col.transform.IsChildOf(_barrierStandalone)))
                    continue;
                if (!ShouldHideOriginalArenaCollider(col, arenaRoot))
                    continue;
                if (!col.enabled)
                    continue;
                if (_disabledOriginalColliders.Contains(col))
                    continue;

                col.enabled = false;
                _disabledOriginalColliders.Add(col);
            }

            if (_disabledOriginalColliders.Count > 0)
                Debug.Log($"[COMPADJUST] Disabled {_disabledOriginalColliders.Count} original arena colliders.");
            else
                CompetitiveAdjustments.ConfigManager.LogWarning("No original arena colliders matched disable filter.");
        }

        // Tear down the standalone rounded-barrier clone (under the rink root). The
        // original barrier colliders it disabled are re-enabled by RestoreOriginalArenaColliders.
        private static void DestroyBarrierStandalone()
        {
            if (_barrierStandalone != null)
            {
                UnityEngine.Object.Destroy(_barrierStandalone.gameObject);
                _barrierStandalone = null;
            }
        }

        private static void RestoreOriginalArenaColliders()
        {
            int restored = 0;
            foreach (var col in _disabledOriginalColliders)
            {
                if (col != null)
                {
                    col.enabled = true;
                    restored++;
                }
            }
            _disabledOriginalColliders.Clear();
            if (restored > 0)
                Debug.Log($"[COMPADJUST] Restored {restored} original arena colliders.");
        }

        private static bool ShouldHideOriginalArenaCollider(Collider collider, Transform arenaRoot)
        {
            if (collider == null || arenaRoot == null) return false;

            string path = GetRelativeTransformPath(arenaRoot, collider.transform);
            string text = (collider.name ?? string.Empty) + "/" + path;

            if (text.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("goal", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("net", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("puck", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            bool hasCollider = text.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasLeft = text.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasRight = text.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFront = text.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBack = text.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasTop = text.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBottom = text.IndexOf("bottom", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasBarrier = text.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasIce = text.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasIce) return true;

            if (!hasCollider && !hasBarrier)
                return false;

            // Keep original barrier/board/wall colliders enabled; custom barrier
            // mesh is non-readable and cannot provide a faithful runtime collider.
            return hasLeft || hasRight || hasFront || hasBack || hasTop || hasBottom;
        }

        private static string DetermineColliderPartKey(string colliderNameOrPath)
        {
            if (string.IsNullOrEmpty(colliderNameOrPath)) return string.Empty;

            string value = colliderNameOrPath;
            if (value.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            if (value.IndexOf("board", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            if (value.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0) return "barrier";
            if (value.IndexOf("ice", StringComparison.OrdinalIgnoreCase) >= 0) return "bottom";
            if (value.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0) return "bottom";
            if (value.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0) return "bottom";
            if (value.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0) return "left";
            if (value.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0) return "right";
            if (value.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0) return "front";
            if (value.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0) return "back";
            if (value.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0) return "top";
            if (value.IndexOf("ceiling", StringComparison.OrdinalIgnoreCase) >= 0) return "top";
            if (value.IndexOf("bottom", StringComparison.OrdinalIgnoreCase) >= 0) return "bottom";
            return string.Empty;
        }

        private static bool IsLikelyBottomCollider(Collider collider, float lowestBoundsMinY)
        {
            if (collider == null) return false;

            Bounds bounds = collider.bounds;
            float horizontalMin = Mathf.Min(bounds.size.x, bounds.size.z);
            bool isThinOnY = bounds.size.y <= Mathf.Max(0.05f, horizontalMin * 0.35f);
            bool nearFloor = bounds.min.y <= (lowestBoundsMinY + 0.35f);
            return isThinOnY && nearFloor;
        }

        // Resolve the puck-collision board layer by name. The shipped build names it
        // "Boards"; some b1117 decompiles show "Barrier". Returns -1 if neither exists.
        private static int ResolveBoardLayer()
        {
            int layer = LayerMask.NameToLayer("Boards");
            if (layer < 0) layer = LayerMask.NameToLayer("Barrier");
            return layer;
        }

        private static void SyncCustomColliderLayersAndStates(Transform arenaRoot, Transform customCollidersRoot)
        {
            if (arenaRoot == null || customCollidersRoot == null) return;

            int iceLayer = LayerMask.NameToLayer("Ice");
            // The puck-collision board layer is "Boards" in the shipped build; some
            // decompiled b1117 trees name it "Barrier". Resolve whichever actually
            // exists. Do NOT fall back to the arena-root layer here: that is "Default",
            // which the puck does not collide with, so custom boards would pass through.
            int boardsLayer = ResolveBoardLayer();
            int fallbackLayer = arenaRoot.gameObject.layer;

            if (iceLayer < 0)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("Unity layer 'Ice' not found, falling back to arena root layer.");
                iceLayer = fallbackLayer;
            }
            if (boardsLayer < 0)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("Neither 'Boards' nor 'Barrier' layer found; custom boards will not collide with the puck.");
                boardsLayer = fallbackLayer;
            }

            var customColliders = customCollidersRoot.GetComponentsInChildren<Collider>(true);

            float lowestBoundsMinY = float.PositiveInfinity;
            foreach (var collider in customColliders)
            {
                if (collider == null) continue;
                lowestBoundsMinY = Mathf.Min(lowestBoundsMinY, collider.bounds.min.y);
            }

            int customCount = 0;
            int mappedLayerCount = 0;
            int heuristicBottomCount = 0;

            foreach (var target in customColliders)
            {
                if (target == null) continue;
                customCount++;

                string targetPath = GetRelativeTransformPath(customCollidersRoot, target.transform);
                string targetPart = DetermineColliderPartKey((target.name ?? string.Empty) + "/" + targetPath);

                int assignedLayer;
                if (string.Equals(targetPart, "bottom", StringComparison.OrdinalIgnoreCase))
                {
                    // Ice surface — use Ice layer so the game triggers ice audio/physics.
                    assignedLayer = iceLayer;
                }
                else if (string.Equals(targetPart, "top", StringComparison.OrdinalIgnoreCase))
                {
                    // Ceiling/top collider — treat like boards so pucks and players collide.
                    assignedLayer = boardsLayer;
                }
                else if (!string.IsNullOrEmpty(targetPart))
                {
                    // left / right / front / back / barrier → Boards layer so the game
                    // triggers board-hit audio and physics correctly.
                    assignedLayer = boardsLayer;
                    // Tag required for StickPositioner.ApplySoftCollision to push the
                    // player's stick away on contact, matching default board behaviour.
                    try { target.gameObject.tag = "Soft Collider"; } catch { }
                }
                else
                {
                    bool looksLikeBottom = !float.IsInfinity(lowestBoundsMinY)
                        && IsLikelyBottomCollider(target, lowestBoundsMinY);
                    assignedLayer = looksLikeBottom ? iceLayer : boardsLayer;
                    if (looksLikeBottom)
                        heuristicBottomCount++;
                }

                target.gameObject.layer = assignedLayer;
                target.isTrigger = false;
                target.enabled = true;
                mappedLayerCount++;

                // Give the rebuilt board colliders a bouncy physic material. The
                // name-scan BoardColliderPatch never matches these custom collider
                // names, and the unified-prefab path creates them with no
                // PhysicsMaterial at all, so the resized rink's boards otherwise
                // deaden the puck instead of rebounding. When soft boards are on we
                // apply the tuned values (server-authoritative, same as the
                // name-scan patch); otherwise fall back to the vanilla board
                // material so the resized rink at least matches stock bounce. Ice
                // (bottom) colliders stay untouched.
                if (assignedLayer == boardsLayer)
                {
                    bool softBoardsServer = CompetitivePuckTweaks.src.PluginCore.config.EnableSoftBoards
                        && Unity.Netcode.NetworkManager.Singleton != null
                        && Unity.Netcode.NetworkManager.Singleton.IsServer;

                    if (softBoardsServer)
                    {
                        CompetitivePuckTweaks.src.BoardColliderPatch.ApplySoftBoardPhysics(target);
                    }
                    else if (target.sharedMaterial == null)
                    {
                        var vanillaBoardMat = GetVanillaBoardPhysicMaterial(arenaRoot);
                        if (vanillaBoardMat != null)
                            target.sharedMaterial = vanillaBoardMat;
                    }
                }

                // Propagate the layer to all parent GameObjects up to (but not
                // including) the colliders root so that any game code walking
                // the hierarchy sees a consistent layer.
                var parent = target.transform.parent;
                while (parent != null && parent != customCollidersRoot)
                {
                    parent.gameObject.layer = assignedLayer;
                    parent = parent.parent;
                }

                if (!_colliderLayersSynced)
                {
                    string layerName = LayerMask.LayerToName(assignedLayer);
                    Debug.Log($"[COMPADJUST] Collider '{target.name}' part='{targetPart}' → layer {assignedLayer} ({layerName})");
                }
            }

            if (!_colliderLayersSynced)
            {
                Debug.Log($"[COMPADJUST] Custom arena colliders ready: {customCount} colliders, {mappedLayerCount} layer-matched by part.");
                if (heuristicBottomCount > 0)
                    Debug.Log($"[COMPADJUST] Assigned {heuristicBottomCount} untagged collider(s) to Ice using geometry fallback.");
                if (IsArenaColliderDebugEnabled())
                    LogArenaColliderHeights(customCollidersRoot);
                _colliderLayersSynced = true;
            }
        }

        // Find a representative vanilla board PhysicsMaterial from the original
        // (now-disabled but still present) arena colliders. Cached after the first
        // resolve. Returns null if the stock boards carry no explicit material, in
        // which case bare custom colliders already match vanilla and no copy is
        // needed. Board-side colliders resolve to part "barrier" via
        // DetermineColliderPartKey (the board/barrier check runs before the side
        // checks), so "barrier" covers the common case.
        private static PhysicsMaterial GetVanillaBoardPhysicMaterial(Transform arenaRoot)
        {
            if (_vanillaBoardPhysicMaterialResolved) return _vanillaBoardPhysicMaterial;
            if (arenaRoot == null) return null;

            foreach (var col in arenaRoot.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.sharedMaterial == null) continue;
                if (_arenaInstance != null && (col.transform == _arenaInstance.transform || col.transform.IsChildOf(_arenaInstance.transform))) continue;
                if (_collidersInstance != null && (col.transform == _collidersInstance.transform || col.transform.IsChildOf(_collidersInstance.transform))) continue;

                string path = GetRelativeTransformPath(arenaRoot, col.transform);
                string part = DetermineColliderPartKey((col.name ?? string.Empty) + "/" + path);
                if (part == "left" || part == "right" || part == "front" || part == "back" || part == "top" || part == "barrier")
                {
                    _vanillaBoardPhysicMaterial = col.sharedMaterial;
                    break;
                }
            }

            _vanillaBoardPhysicMaterialResolved = true;
            return _vanillaBoardPhysicMaterial;
        }

        private static void SyncArenaColliderDebugBrushes(Transform collidersRoot)
        {
            if (collidersRoot == null) return;

            bool enabled = IsArenaColliderDebugEnabled();
            foreach (var collider in collidersRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                SyncArenaColliderDebugBrush(collider, enabled);
            }

            // The rounded barrier is a standalone object under the rink root, not under
            // collidersRoot, so brush it separately -- otherwise the clip-brush visual
            // covers the straight walls but misses the corners.
            if (_barrierStandalone != null && _barrierStandalone != collidersRoot)
            {
                foreach (var collider in _barrierStandalone.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null) continue;
                    SyncArenaColliderDebugBrush(collider, enabled);
                }
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
            if (_collidersInstance != null)
            {
                SyncArenaColliderDebugBrushes(_collidersInstance.transform);
                if (IsArenaColliderDebugEnabled())
                    LogArenaColliderHeights(_collidersInstance.transform);
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

                // Skip our own spawned custom arena instances
                if (string.Equals(name, UnifiedInstanceName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(name, "CustomArenaVisual", StringComparison.OrdinalIgnoreCase))
                    continue;

                float score = 0f;
                if (exactArena) score += 1200f;
                else if (exactRink) score += 1100f;
                else if (containsArena) score += 700f;
                else if (containsRink) score += 600f;

                if (t.parent == null) score += 200f;
                score += Mathf.Min(200f, t.GetComponentsInChildren<Renderer>(true).Length * 4f);

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

        private static void HideOriginalArenaRenderers(Transform arenaRoot, Transform customArenaRoot)
        {
            int arenaRootId = arenaRoot != null ? arenaRoot.GetInstanceID() : 0;

            _hiddenArenaRenderers.RemoveAll(renderer => renderer == null);

            if (_hiddenArenaRootId != 0 && _hiddenArenaRootId != arenaRootId)
            {
                RestoreOriginalArenaRenderers();
            }

            for (int i = _hiddenArenaRenderers.Count - 1; i >= 0; i--)
            {
                var hidden = _hiddenArenaRenderers[i];
                if (hidden == null)
                {
                    _hiddenArenaRenderers.RemoveAt(i);
                    continue;
                }

                if (!ShouldHideOriginalArenaRenderer(hidden, arenaRoot))
                {
                    hidden.enabled = true;
                    _hiddenArenaRenderers.RemoveAt(i);
                }
            }

            foreach (var renderer in arenaRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (customArenaRoot != null && (renderer.transform == customArenaRoot || renderer.transform.IsChildOf(customArenaRoot)))
                    continue;
                if (!ShouldHideOriginalArenaRenderer(renderer, arenaRoot))
                    continue;

                if (!_hiddenArenaRenderers.Contains(renderer))
                    _hiddenArenaRenderers.Add(renderer);

                if (renderer.enabled)
                    renderer.enabled = false;
            }

            _hiddenArenaRootId = arenaRootId;
        }

        private static void RestoreOriginalArenaRenderers()
        {
            foreach (var renderer in _hiddenArenaRenderers)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }

            _hiddenArenaRenderers.Clear();
            _hiddenArenaRootId = 0;
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
        // counts. World X scales with config ArenaScaleX; world Z (rink length) scales
        // with config ArenaScaleY, because the arena prefab carries a default 90deg X
        // rotation (ArenaRotX=90) that maps config Y onto world Z. Config ArenaScaleZ is
        // the vertical axis and is not chunked.
        private const float VanillaArenaHalfExtentX = 50f;
        private const float VanillaArenaHalfExtentZ = 25f;

        private static void LogRequiredChunks()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool useSynced = _hasSyncedTweaks && nm != null && !nm.IsServer;
            var cfg = CompetitiveAdjustments.ConfigManager.Config?.CompAdjust;

            float scaleX = useSynced ? _syncedArenaScaleX : (cfg?.ArenaScaleX ?? 1f);
            // World Z (rink length) is scaled by config ArenaScaleY due to the 90deg arena
            // rotation; ArenaScaleZ is the vertical axis and does not affect the chunk grid.
            float scaleZ = useSynced ? _syncedArenaScaleY : (cfg?.ArenaScaleY ?? 1f);
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

        private static void HandleAudioEnvironment()
        {
            const float MAX_DISTANCE = 500f;
            if (_cachedReverbZone == null)
                _cachedReverbZone = Resources.FindObjectsOfTypeAll<AudioReverbZone>()
                    .FirstOrDefault(o => o.gameObject.scene.IsValid());

            var reverbZone = _cachedReverbZone;
            if (reverbZone == null) return;

            if (_originalReverbMaxDistance < 0f)
                _originalReverbMaxDistance = reverbZone.maxDistance;

            if (!Mathf.Approximately(reverbZone.maxDistance, MAX_DISTANCE))
            {
                reverbZone.gameObject.SetActive(true);
                reverbZone.transform.position = Vector3.zero;
                reverbZone.maxDistance = MAX_DISTANCE;
                CompetitiveAdjustments.ConfigManager.Log($"AudioReverbZone adjusted for expanded arena (maxDistance={MAX_DISTANCE}).");
            }
        }

        private static void RestoreAudioEnvironment()
        {
            if (_cachedReverbZone != null && _originalReverbMaxDistance >= 0f)
            {
                _cachedReverbZone.maxDistance = _originalReverbMaxDistance;
                CompetitiveAdjustments.ConfigManager.Log($"AudioReverbZone restored to {_originalReverbMaxDistance:F0} m.");
            }
            _cachedReverbZone = null;
            _originalReverbMaxDistance = -1f;
        }
    }

}
