// ArenaProxyVisual.cs - redraw statically batched scene geometry under our own matrix.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DashFallMod
{
    /// <summary>
    /// Moves and resizes base-game geometry that static batching has frozen: the rink
    /// surfaces, and the goal frames.
    ///
    /// Why it needs a trick at all: a statically batched renderer's mesh is baked into a
    /// scene-wide combined mesh whose vertices are already in WORLD space, and the
    /// renderer's own transform is ignored at draw time. That is why scaling
    /// 'Level Default' resizes the colliders, goals and spawn markers while the rink
    /// visual stays vanilla, and why scaling a goal moves its cloth net but leaves the
    /// posts behind. The combined mesh is also non readable in player builds, so CPU
    /// vertex surgery returns empty arrays and silently produces garbage.
    ///
    /// Two ways around it, both implemented here:
    ///
    /// <b>DrawMesh</b> (default, proven). <see cref="MeshRenderer.subMeshStartIndex"/>
    /// says where the renderer's slice of the combined mesh begins, and the next batched
    /// renderer's start index says where it ends. Disable the renderer and resubmit that
    /// slice through <see cref="Graphics.RenderMesh"/> under our own matrix. The baked
    /// vertices being world space is exactly what makes this work: the draw matrix IS the
    /// world transform we want, with no parent chain involved. Costs one draw call per
    /// renderer per material, and Unity cannot light manually submitted geometry with
    /// baked lightmaps, so it falls back to realtime lights plus ambient.
    ///
    /// <b>BatchRoot</b> (opt in, unverified on this game build).
    /// <see cref="Renderer.staticBatchRootTransform"/> is the transform Unity multiplies
    /// the baked vertices by; vanilla leaves it null, which is what "world space" means in
    /// practice. Point the renderers at a transform we own and scale that instead. The
    /// geometry then moves with lightmaps, shadows and probes intact at zero extra draw
    /// calls, which is strictly better if this build honours it. Only usable when every
    /// renderer in a group shares one matrix, so goal frames always take the DrawMesh
    /// path.
    ///
    /// Work is tracked in independent groups (the arena is one, each goal is another) so
    /// they can be rebuilt and torn down separately.
    /// </summary>
    internal static class ArenaProxyVisual
    {
        internal enum Mode
        {
            Off = 0,
            DrawMesh = 1,
            BatchRoot = 2,
        }

        /// <summary>
        /// A renderer to take over, and the world-space transform its baked geometry
        /// should be moved by. Identity means "leave it exactly where vanilla drew it".
        /// </summary>
        internal struct Target
        {
            public MeshRenderer Renderer;
            public Matrix4x4 WorldDelta;
        }

        /// <summary>Group key for the rink surfaces. Goal groups use their goal root's instance id.</summary>
        internal const int ArenaGroupKey = 0;

        private struct Draw
        {
            public Mesh Mesh;
            public int SubMesh;
            public Matrix4x4 Matrix;
            public RenderParams Params;
        }

        private struct Rerooted
        {
            public Renderer Renderer;
            public Transform OriginalRoot;
        }

        private sealed class Group
        {
            public int Key;
            public Mode Mode;
            public int Signature;
            public int RendererCount;
            public readonly List<Renderer> Hidden = new List<Renderer>();
            public readonly List<Rerooted> Rerooted = new List<Rerooted>();
            public Draw[] Draws = Array.Empty<Draw>();
            public int LightmapDraws;
            public GameObject BatchRootObject;

            public bool HasDeadRenderers()
            {
                for (int i = 0; i < Hidden.Count; i++)
                    if (Hidden[i] == null) return true;
                for (int i = 0; i < Rerooted.Count; i++)
                    if (Rerooted[i].Renderer == null) return true;
                return false;
            }
        }

        private static readonly Dictionary<int, Group> _groups = new Dictionary<int, Group>();

        // Which group owns each renderer. A renderer taken over twice would be drawn
        // twice, once per group matrix, which reads in-game as a ghost copy of the
        // geometry at the wrong size.
        private static readonly Dictionary<int, int> _ownerByRenderer = new Dictionary<int, int>();

        private static Drawer _drawer;
        private static bool _loggedBadMode;
        private static bool _verifiedWorldSpace;

        internal static bool Active => _groups.Count > 0;

        /// <summary>
        /// Fraction of proxy draws that got their baked lightmap back, 0 to 1. Anything
        /// standing in for a lost bake has to check this first: once the bake is genuinely
        /// restored, a substitute stops being a substitute and starts double-counting.
        /// </summary>
        internal static float LightmapCoverage
        {
            get
            {
                int lit = 0, total = 0;
                foreach (var kv in _groups)
                {
                    lit += kv.Value.LightmapDraws;
                    total += kv.Value.Draws.Length;
                }

                return total > 0 ? (float)lit / total : 0f;
            }
        }

        /// <summary>Renderers currently driven by the proxy, across every group.</summary>
        internal static int ProxiedRenderers
        {
            get
            {
                int total = 0;
                foreach (var kv in _groups) total += kv.Value.RendererCount;
                return total;
            }
        }

        /// <summary>
        /// Off means "use the bundled prefab instead". An unset or unrecognised value
        /// lands on the default DrawMesh path rather than silently reverting to the
        /// prefab, so a typo in the client config shows up as a warning.
        /// </summary>
        internal static Mode ParseMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Mode.DrawMesh;

            string trimmed = value.Trim();
            if (trimmed.Equals("drawmesh", StringComparison.OrdinalIgnoreCase)) return Mode.DrawMesh;
            if (trimmed.Equals("batchroot", StringComparison.OrdinalIgnoreCase)) return Mode.BatchRoot;
            if (trimmed.Equals("prefab", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return Mode.Off;

            if (!_loggedBadMode)
            {
                _loggedBadMode = true;
                Debug.LogWarning($"[COMPADJUST] Unknown ArenaVisualMode '{trimmed}'; using 'drawmesh'. " +
                                 "Valid values: drawmesh, batchroot, prefab.");
            }
            return Mode.DrawMesh;
        }

        /// <summary>A dedicated server draws nothing; never touch renderers there.</summary>
        internal static bool IsHeadless()
        {
            try
            {
                return Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Take over <paramref name="targets"/> under <paramref name="key"/>, replacing
        /// whatever that group held before. Returns false when nothing could be proxied,
        /// which is the caller's cue to fall back to a bundled prefab.
        /// </summary>
        internal static bool SyncGroup(int key, Mode mode, List<Target> targets, string label)
        {
            if (mode == Mode.Off || targets == null || targets.Count == 0 || IsHeadless())
            {
                ClearGroup(key);
                return false;
            }

            int signature = ComputeSignature(mode, targets);
            if (_groups.TryGetValue(key, out Group existing)
                && existing.Signature == signature
                && existing.RendererCount > 0
                && !existing.HasDeadRenderers())
            {
                return true;
            }

            ClearGroup(key);

            // BatchRoot drives a whole group from one transform, so it can only express a
            // single shared matrix. Anything per-renderer (goal post thickness) has to go
            // through DrawMesh.
            Mode effective = mode;
            if (effective == Mode.BatchRoot && !AllTargetsShareMatrix(targets))
                effective = Mode.DrawMesh;

            var group = new Group { Key = key, Mode = effective, Signature = signature };
            bool ok = effective == Mode.BatchRoot
                ? BuildBatchRoot(group, targets)
                : BuildDrawMesh(group, targets);

            if (!ok)
            {
                DisposeGroup(group);
                return false;
            }

            _groups[key] = group;
            Debug.Log($"[COMPADJUST] Proxy visual active for {label} ({effective}): " +
                      $"{group.RendererCount} renderer(s), {group.Draws.Length} draw(s).");
            return true;
        }

        internal static void ClearGroup(int key)
        {
            if (!_groups.TryGetValue(key, out Group group)) return;
            _groups.Remove(key);
            DisposeGroup(group);
        }

        internal static void Clear()
        {
            var keys = new List<int>(_groups.Keys);
            for (int i = 0; i < keys.Count; i++) ClearGroup(keys[i]);
            _groups.Clear();
            DestroyLightmappedMaterials();
        }

        private static void DisposeGroup(Group group)
        {
            if (group == null) return;

            for (int i = 0; i < group.Hidden.Count; i++)
            {
                var r = group.Hidden[i];
                if (r == null) continue;
                r.enabled = true;
                ReleaseOwnership(r, group.Key);
            }
            group.Hidden.Clear();

            for (int i = 0; i < group.Rerooted.Count; i++)
            {
                var entry = group.Rerooted[i];
                if (entry.Renderer == null) continue;
                TrySetBatchRoot(entry.Renderer, entry.OriginalRoot);
                ReleaseOwnership(entry.Renderer, group.Key);
            }
            group.Rerooted.Clear();

            group.Draws = Array.Empty<Draw>();
            group.RendererCount = 0;

            if (group.BatchRootObject != null)
            {
                UnityEngine.Object.Destroy(group.BatchRootObject);
                group.BatchRootObject = null;
            }
        }

        // ── DrawMesh mode ────────────────────────────────────────────────────────
        private static bool BuildDrawMesh(Group group, List<Target> targets)
        {
            var draws = new List<Draw>(targets.Count * 2);
            int renderers = 0;
            int skippedRange = 0;
            int identityST = 0;
            int batchedST = 0;
            string sampleST = null;

            for (int i = 0; i < targets.Count; i++)
            {
                MeshRenderer mr = targets[i].Renderer;
                if (!IsDrawable(mr)) continue;
                if (IsOwnedByAnother(mr, group.Key)) continue;

                var filter = mr.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;

                var mats = mr.sharedMaterials;
                int first = mr.subMeshStartIndex;
                if (first < 0 || first >= mesh.subMeshCount)
                {
                    // Not the slice layout we expect; drawing it would splatter unrelated
                    // scene geometry around, so leave this renderer alone.
                    skippedRange++;
                    continue;
                }

                int count = ResolveSliceCount(mesh, first, mats != null ? mats.Length : 1);

                if (!_verifiedWorldSpace)
                {
                    _verifiedWorldSpace = true;
                    VerifyWorldSpaceAssumption(mr, mesh, first);
                }

                Matrix4x4 matrix = ResolveDrawMatrix(mr, targets[i].WorldDelta);
                Bounds worldBounds = ResolveWorldBounds(mr, matrix);

                int added = 0;
                for (int k = 0; k < count; k++)
                {
                    var material = mats != null && k < mats.Length ? mats[k] : mr.sharedMaterial;
                    if (material == null) continue;   // collider-visualisation meshes have none

                    // Hand the renderer's own lightmap back if it had one. This is the
                    // whole ball game for shading: everything else here is a substitute
                    // for the bake, and substitutes cannot reproduce per-texel indirect on
                    // a hangar-sized surface. Probes are the fallback when there is no
                    // lightmap to restore.
                    bool lightmapped = TryResolveLightmap(mr, material, out Material drawMaterial, out MaterialPropertyBlock block);

                    // unity_LightmapST is the renderer's window into the atlas. If static
                    // batching already folded it into the combined mesh's UV2 then applying
                    // it again samples the wrong region entirely, and unrelated patches of
                    // the atlas bleed onto surfaces as blobs. An all-identity report means
                    // the ST is a no-op and the artefact is elsewhere.
                    if (lightmapped)
                    {
                        Vector4 st = mr.lightmapScaleOffset;
                        if (Mathf.Approximately(st.x, 1f) && Mathf.Approximately(st.y, 1f)
                            && Mathf.Approximately(st.z, 0f) && Mathf.Approximately(st.w, 0f)) identityST++;
                        else if (sampleST == null) sampleST = $"'{mr.name}' idx={mr.lightmapIndex} ST={st}";

                        // How many of those STs we are deliberately ignoring because the
                        // batcher already applied them. A high count here with the black
                        // patches gone is the confirmation that this was the cause; a count
                        // of zero means the patches came from something else.
                        if (mr.isPartOfStaticBatch) batchedST++;
                    }

                    draws.Add(new Draw
                    {
                        Mesh = mesh,
                        SubMesh = first + k,
                        Matrix = matrix,
                        Params = new RenderParams(drawMaterial)
                        {
                            layer = mr.gameObject.layer,
                            renderingLayerMask = mr.renderingLayerMask,
                            shadowCastingMode = mr.shadowCastingMode,
                            receiveShadows = mr.receiveShadows,
                            matProps = block,
                            // Lightmapped geometry normally has probe sampling switched
                            // off, because the lightmap already carries its indirect. Copy
                            // that only when the lightmap actually came back; otherwise
                            // probes are the only indirect term left, and with them off as
                            // well every surface renders as flat ambient.
                            lightProbeUsage = lightmapped ? LightProbeUsage.Off : LightProbeUsage.BlendProbes,
                            reflectionProbeUsage = mr.reflectionProbeUsage,
                            worldBounds = worldBounds,
                        },
                    });
                    added++;
                }

                if (added == 0) continue;

                mr.enabled = false;
                group.Hidden.Add(mr);
                TakeOwnership(mr, group.Key);
                renderers++;
            }

            if (skippedRange > 0)
                Debug.LogWarning($"[COMPADJUST] Proxy visual skipped {skippedRange} renderer(s) whose " +
                                 "sub-mesh slice fell outside the combined mesh.");

            if (renderers == 0 || draws.Count == 0) return false;

            int keptLightmap = 0;
            for (int i = 0; i < draws.Count; i++)
                if (draws[i].Params.matProps != null) keptLightmap++;

            Debug.Log($"[COMPADJUST] Proxy lighting: {keptLightmap} of {draws.Count} draw(s) kept their baked " +
                      $"lightmap; the rest fall back to light probes. Atlas mapping: {identityST} identity" +
                      (sampleST != null ? $", first non-identity {sampleST}" : ", all identity") +
                      $"; {batchedST} batched renderer(s) had their ST ignored (UV2 already in atlas space).");

            EnsureDrawer();
            group.LightmapDraws = keptLightmap;
            group.Draws = draws.ToArray();
            group.RendererCount = renderers;
            return true;
        }

        /// <summary>
        /// Batched vertices live in the space of the renderer's static batch root, which
        /// vanilla leaves null (= world space), so the draw matrix is just our world
        /// delta. Fold a real root in if some build ever sets one.
        /// </summary>
        private static Matrix4x4 ResolveDrawMatrix(MeshRenderer mr, Matrix4x4 delta)
        {
            Transform batchRoot = GetBatchRoot(mr);
            return batchRoot == null ? delta : delta * batchRoot.localToWorldMatrix;
        }

        // ── Lightmaps ────────────────────────────────────────────────────────────
        // Unity binds a lightmap per RENDERER, from Renderer.lightmapIndex, and manually
        // submitted geometry has no renderer to read it from. That is the real reason
        // proxied surfaces lose their shading, and no amount of realtime fill can stand in
        // for per-texel indirect on a surface the size of a hangar wall.
        //
        // It can be handed back by hand. The lightmap is sampled by UV, not by position,
        // so a resized mesh keeps sampling the same texels: the bake stretches with the
        // geometry, which is exactly what is wanted. Three pieces are needed:
        //
        //   LIGHTMAP_ON            shader keyword, per material, so the lightmap branch
        //                          compiles in. Set on a COPY, because forcing it on the
        //                          shared material would break any non-lightmapped
        //                          renderer that happens to use the same one.
        //   unity_Lightmap[Ind]    the textures themselves, from LightmapSettings
        //   unity_LightmapST       the renderer's own scale/offset into the atlas
        //
        // The copy is the one real cost: another mod editing the shared material at
        // runtime no longer propagates to our draw. Copies are rebuilt whenever groups are,
        // so an edit lands on the next config change rather than instantly.

        private static readonly Dictionary<long, Material> _lightmappedMaterials = new Dictionary<long, Material>();

        /// <summary>
        /// Client-local escape hatch. Turn it off to fall back to light probes and settle
        /// in one restart whether an artefact comes from the restored lightmap or from
        /// something else entirely (shadows, reflection probes, moved geometry).
        /// </summary>
        private static bool ResolveRestoreLightmaps()
        {
            try
            {
                return DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ProxyRestoreLightmaps != false;
            }
            catch { }

            return true;
        }

        private static bool TryResolveLightmap(
            MeshRenderer mr, Material source, out Material material, out MaterialPropertyBlock block)
        {
            material = source;
            block = null;

            if (!ResolveRestoreLightmaps()) return false;

            int index = mr.lightmapIndex;
            // 65534 is Unity's "lit by a lightmap that is not loaded", 65535 is "none".
            if (index < 0 || index >= 65534) return false;

            LightmapData[] maps = LightmapSettings.lightmaps;
            if (maps == null || index >= maps.Length) return false;

            LightmapData data = maps[index];
            Texture2D colour = data != null ? data.lightmapColor : null;
            if (colour == null) return false;

            long key = (long)source.GetInstanceID() * 100003L + index;
            if (!_lightmappedMaterials.TryGetValue(key, out material) || material == null)
            {
                material = new Material(source);
                material.EnableKeyword("LIGHTMAP_ON");
                // DIRLIGHTMAP_COMBINED is required, not optional. Dropping it was tried
                // and it took the hangar's shading with it: the colour lightmap alone is
                // flat irradiance, and the direction texture is what carries the
                // normal-dependent variation that makes a big surface read as shaded.
                if (data.lightmapDir != null) material.EnableKeyword("DIRLIGHTMAP_COMBINED");
                _lightmappedMaterials[key] = material;
            }

            block = new MaterialPropertyBlock();
            block.SetTexture("unity_Lightmap", colour);
            if (data.lightmapDir != null) block.SetTexture("unity_LightmapInd", data.lightmapDir);

            // Statically batched geometry carries lightmap UVs that are ALREADY in atlas
            // space: the batcher folds each source renderer's scale/offset into the
            // combined mesh's UV2 when it builds it. Applying the renderer's ST on top of
            // that transforms twice, and the second transform lands the sample somewhere
            // else in the atlas, usually in the unused padding between charts, which is
            // black. That is the black patch on the boards.
            //
            // It only ever showed on a stretched rink because that is the only time the
            // proxy runs at all: at an identity delta the game keeps drawing its own
            // renderers and none of this code is involved. The stretch amount is
            // irrelevant, the bug is in the restore path.
            //
            // Pass the UVs through unchanged for batched renderers, and keep the real ST
            // for any unbatched one, whose UV2 is still in 0..1 mesh space.
            Vector4 st = mr.isPartOfStaticBatch ? new Vector4(1f, 1f, 0f, 0f) : mr.lightmapScaleOffset;
            block.SetVector("unity_LightmapST", st);
            return true;
        }

        private static void DestroyLightmappedMaterials()
        {
            foreach (var kv in _lightmappedMaterials)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);

            _lightmappedMaterials.Clear();
        }

        /// <summary>
        /// Culling bounds for a proxied slice. This is why the draws go through
        /// Graphics.RenderMesh rather than Graphics.DrawMesh: DrawMesh culls against the
        /// MESH bounds, and a scene-wide combined mesh is never off screen, so every draw
        /// would survive culling. A batched renderer's own world bounds are already the
        /// slice's bounds (that is how Unity culls it individually), so moving them by the
        /// same matrix gives exactly the right box.
        /// </summary>
        private static Bounds ResolveWorldBounds(MeshRenderer mr, Matrix4x4 matrix)
        {
            Bounds source = mr.bounds;

            // Degenerate bounds would cull the geometry away entirely, which is a far
            // worse failure than not culling. Fall back to "always visible".
            if (source.size.sqrMagnitude < 1e-6f)
                return new Bounds(Vector3.zero, Vector3.one * 100000f);

            Vector3 centre = matrix.MultiplyPoint3x4(source.center);
            Vector3 e = source.extents;
            var extents = new Vector3(
                Mathf.Abs(matrix.m00) * e.x + Mathf.Abs(matrix.m01) * e.y + Mathf.Abs(matrix.m02) * e.z,
                Mathf.Abs(matrix.m10) * e.x + Mathf.Abs(matrix.m11) * e.y + Mathf.Abs(matrix.m12) * e.z,
                Mathf.Abs(matrix.m20) * e.x + Mathf.Abs(matrix.m21) * e.y + Mathf.Abs(matrix.m22) * e.z);

            return new Bounds(centre, extents * 2f);
        }

        /// <summary>
        /// One-shot sanity check on the assumption the whole DrawMesh path rests on: that
        /// the batched vertices are in world space. SubMeshDescriptor.bounds is stored
        /// metadata, so it reads correctly even though the combined mesh is non-readable;
        /// if it lines up with the renderer's world bounds the assumption holds.
        /// </summary>
        private static void VerifyWorldSpaceAssumption(MeshRenderer mr, Mesh mesh, int firstSubMesh)
        {
            try
            {
                Vector3 sliceCentre = mesh.GetSubMesh(firstSubMesh).bounds.center;
                Vector3 worldCentre = mr.bounds.center;
                float drift = Vector3.Distance(sliceCentre, worldCentre);
                if (drift <= 0.5f) return;

                Debug.LogWarning($"[COMPADJUST] Proxy visual: '{mr.name}' sub-mesh {firstSubMesh} centre " +
                                 $"{sliceCentre} sits {drift:F2}m from its world bounds centre {worldCentre}. " +
                                 "The batched vertices may not be in world space; if geometry draws in the " +
                                 "wrong place, set ArenaVisualMode to \"prefab\" in the client config.");
            }
            catch { }
        }

        // ── BatchRoot mode ───────────────────────────────────────────────────────
        private static bool AllTargetsShareMatrix(List<Target> targets)
        {
            for (int i = 1; i < targets.Count; i++)
                if (targets[i].WorldDelta != targets[0].WorldDelta) return false;
            return true;
        }

        private static bool BuildBatchRoot(Group group, List<Target> targets)
        {
            if (BatchRootProperty() == null)
            {
                Debug.LogWarning("[COMPADJUST] ArenaVisualMode 'batchroot' is not available on this Unity build " +
                                 "(Renderer.staticBatchRootTransform is missing); use 'drawmesh'.");
                return false;
            }

            group.BatchRootObject = new GameObject("CompAdjustProxyBatchRoot");
            UnityEngine.Object.DontDestroyOnLoad(group.BatchRootObject);

            // Identity here must mean "exactly where vanilla drew it", so this transform
            // carries the whole delta and nothing else.
            Transform t = group.BatchRootObject.transform;
            t.SetParent(null, false);
            Matrix4x4 delta = targets[0].WorldDelta;
            t.position = delta.GetColumn(3);
            t.rotation = delta.rotation;
            t.localScale = delta.lossyScale;

            int rerooted = 0;
            int skippedExistingRoot = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                MeshRenderer mr = targets[i].Renderer;
                if (!IsDrawable(mr)) continue;
                if (IsOwnedByAnother(mr, group.Key)) continue;

                // A renderer that already has a root has its vertices baked in that root's
                // space, not world space; repointing it would teleport it.
                if (GetBatchRoot(mr) != null) { skippedExistingRoot++; continue; }
                if (!TrySetBatchRoot(mr, t)) continue;

                group.Rerooted.Add(new Rerooted { Renderer = mr, OriginalRoot = null });
                TakeOwnership(mr, group.Key);
                rerooted++;
            }

            if (skippedExistingRoot > 0)
                Debug.LogWarning($"[COMPADJUST] Proxy visual left {skippedExistingRoot} renderer(s) alone: " +
                                 "they already carry a static batch root.");

            if (rerooted == 0) return false;

            group.RendererCount = rerooted;
            return true;
        }

        // ── Renderer.staticBatchRootTransform ────────────────────────────────────
        // The transform Unity multiplies a batched renderer's baked vertices by. Null
        // means they are already in world space, which is the vanilla case here. Both
        // accessors are internal in this Unity build, hence the reflection.
        private static PropertyInfo _batchRootProperty;
        private static bool _batchRootProbed;

        private static PropertyInfo BatchRootProperty()
        {
            if (_batchRootProbed) return _batchRootProperty;
            _batchRootProbed = true;

            try
            {
                _batchRootProperty = typeof(Renderer).GetProperty(
                    "staticBatchRootTransform",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch { _batchRootProperty = null; }

            return _batchRootProperty;
        }

        /// <summary>
        /// True when a renderer's batched vertices are baked in some root's space rather
        /// than world space, which makes Renderer.bounds follow that root instead of
        /// standing still. Anything reading batched bounds as a fixed world quantity has to
        /// check this first; 'batchroot' mode sets exactly this property.
        /// </summary>
        internal static bool HasStaticBatchRoot(Renderer renderer) => GetBatchRoot(renderer) != null;

        private static Transform GetBatchRoot(Renderer renderer)
        {
            var prop = BatchRootProperty();
            if (prop == null || !prop.CanRead || renderer == null) return null;

            try { return prop.GetValue(renderer) as Transform; }
            catch { return null; }
        }

        private static bool TrySetBatchRoot(Renderer renderer, Transform value)
        {
            var prop = BatchRootProperty();
            if (prop == null || !prop.CanWrite || renderer == null) return false;

            try { prop.SetValue(renderer, value); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Only take over renderers the game is actually drawing. Graphics.DrawMesh does
        /// not care whether a GameObject is active, so proxying a hidden renderer would
        /// put geometry on screen that vanilla keeps switched off.
        /// </summary>
        private static bool IsDrawable(MeshRenderer mr)
        {
            return mr != null
                && mr.enabled
                && mr.gameObject.activeInHierarchy
                && mr.isPartOfStaticBatch;
        }

        // ── ownership ────────────────────────────────────────────────────────────
        /// <summary>
        /// True when this group already drives the renderer. Callers building a target
        /// list MUST allow these through even though they are disabled: the proxy is what
        /// disabled them, so filtering on Renderer.enabled would drop the entire existing
        /// group on every rebuild and make the whole thing look like it found nothing.
        /// </summary>
        internal static bool IsOwnedBy(Renderer renderer, int key)
        {
            return renderer != null
                && _ownerByRenderer.TryGetValue(renderer.GetInstanceID(), out int owner)
                && owner == key;
        }

        /// <summary>The group currently driving this renderer, or null if none.</summary>
        internal static string DescribeOwner(Renderer renderer)
        {
            if (renderer == null) return "none";
            if (!_ownerByRenderer.TryGetValue(renderer.GetInstanceID(), out int owner)) return "none";
            return owner == ArenaGroupKey ? "arena" : "goal:" + owner;
        }

        private static bool IsOwnedByAnother(Renderer renderer, int key)
        {
            return _ownerByRenderer.TryGetValue(renderer.GetInstanceID(), out int owner) && owner != key;
        }

        private static void TakeOwnership(Renderer renderer, int key)
        {
            _ownerByRenderer[renderer.GetInstanceID()] = key;
        }

        private static void ReleaseOwnership(Renderer renderer, int key)
        {
            int id = renderer.GetInstanceID();
            if (_ownerByRenderer.TryGetValue(id, out int owner) && owner == key)
                _ownerByRenderer.Remove(id);
        }

        // ── slice bounds ─────────────────────────────────────────────────────────
        // sharedMaterials.Length is only an ESTIMATE of how many sub-meshes a batched
        // renderer owns. Unity lets a renderer carry more materials than it has
        // sub-meshes, and when it does, the extra ones would walk straight into the NEXT
        // renderer's slice of the combined mesh: that is how a goal frame ends up drawn
        // a second time under the rink's matrix. Clamp every slice to where the next
        // batched renderer on the same mesh begins.
        private static readonly Dictionary<int, int[]> _batchStarts = new Dictionary<int, int[]>();
        private static int _batchStartsFrame = -1;

        private static int ResolveSliceCount(Mesh mesh, int first, int materialCount)
        {
            int limit = mesh.subMeshCount;

            int[] starts = GetBatchStarts(mesh);
            for (int i = 0; i < starts.Length; i++)
            {
                if (starts[i] > first) { limit = Mathf.Min(limit, starts[i]); break; }
            }

            return Mathf.Clamp(materialCount, 1, Mathf.Max(1, limit - first));
        }

        private static int[] GetBatchStarts(Mesh mesh)
        {
            if (_batchStartsFrame != Time.frameCount)
            {
                _batchStartsFrame = Time.frameCount;
                _batchStarts.Clear();
            }

            int meshId = mesh.GetInstanceID();
            if (_batchStarts.TryGetValue(meshId, out int[] cached)) return cached;

            var starts = new List<int>();
            var all = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                MeshRenderer mr = all[i];
                if (mr == null || !mr.isPartOfStaticBatch) continue;

                var filter = mr.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                if (filter.sharedMesh.GetInstanceID() != meshId) continue;

                int start = mr.subMeshStartIndex;
                if (!starts.Contains(start)) starts.Add(start);
            }

            starts.Sort();
            int[] result = starts.ToArray();
            _batchStarts[meshId] = result;
            return result;
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static int ComputeSignature(Mode mode, List<Target> targets)
        {
            unchecked
            {
                int hash = (int)mode * 397 ^ targets.Count;
                for (int i = 0; i < targets.Count; i++)
                {
                    MeshRenderer mr = targets[i].Renderer;
                    hash = hash * 397 ^ (mr != null ? mr.GetInstanceID() : 0);
                    hash = hash * 397 ^ targets[i].WorldDelta.GetHashCode();
                }
                return hash;
            }
        }

        private static void EnsureDrawer()
        {
            if (_drawer != null) return;
            var go = new GameObject("CompAdjustProxyDrawer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _drawer = go.AddComponent<Drawer>();
        }

        /// <summary>
        /// Resubmits every group's batched slices each frame. RenderParams.camera is left
        /// null so the draws reach every camera, including the minimap, exactly as the
        /// real renderers would have.
        /// </summary>
        private sealed class Drawer : MonoBehaviour
        {
            private void LateUpdate()
            {
                foreach (var kv in _groups)
                {
                    Draw[] draws = kv.Value.Draws;
                    for (int i = 0; i < draws.Length; i++)
                    {
                        if (draws[i].Mesh == null || draws[i].Params.material == null) continue;
                        Graphics.RenderMesh(in draws[i].Params, draws[i].Mesh, draws[i].SubMesh, draws[i].Matrix);
                    }
                }
            }
        }
    }
}
