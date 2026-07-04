using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CompetitiveAdjustments
{
    /// <summary>
    /// One-shot scene-hierarchy dump for planning the "resize the base-game arena + hangar"
    /// feature. The arena/hangar geometry is scene/prefab data (not in the decompiled code),
    /// so this reports the real runtime tree: top-level scene roots, the arena root subtree,
    /// and any hangar/stands objects, with transforms, renderers+textures, and colliders.
    /// Prefix [ARENADUMP]. Fires once per level spawn; safe to leave in until the feature lands.
    /// </summary>
    public static class ArenaHierarchyDiag
    {
        // Disabled by default now that the hierarchy + static-batching + marker parentage
        // are characterised. Flip to true to re-dump the full scene tree when investigating.
        public static bool Enabled = false;
        private static bool _dumped;

        private static readonly string[] InterestingNames =
        {
            "arena", "rink", "hangar", "hanger", "warehouse", "building", "stadium",
            "stands", "crowd", "spectator", "ice", "board", "barrier", "wall", "roof",
            "ceiling", "floor", "glass", "bench", "zamboni", "net", "goal", "bound"
        };

        private static void Log(string msg) => Debug.Log("[ARENADUMP] " + msg);

        public static void ResetOneShot() => _dumped = false;

        public static void DumpOnce()
        {
            if (!Enabled || _dumped) return;
            _dumped = true;
            try { Dump(); }
            catch (System.Exception e) { Debug.LogWarning("[ARENADUMP] failed: " + e); }
        }

        private static void Dump()
        {
            Log("================= SCENE HIERARCHY DUMP =================");

            // Top-level scene roots = transforms with no parent. Summarise each so the arena
            // and hangar roots are easy to spot.
            Log("--- SCENE ROOT OBJECTS ---");
            var seenRoots = new HashSet<int>();
            foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (tr == null) continue;
                var root = tr.root;
                if (root == null || !seenRoots.Add(root.GetInstanceID())) continue;
                int rends = root.GetComponentsInChildren<Renderer>(true).Length;
                int cols = root.GetComponentsInChildren<Collider>(true).Length;
                Log($"  ROOT '{root.name}' active={root.gameObject.activeInHierarchy} children={root.childCount} descRenderers={rends} descColliders={cols} localScale={V(root.localScale)} worldPos={V(root.position)}");
            }

            // Find the arena root the same way ArenaTweaks does, plus any hangar-ish root.
            var arenaRoot = FindByName(new[] { "arena", "rink" });
            var hangarRoot = FindByName(new[] { "hangar", "hanger", "warehouse", "building", "stadium" });

            if (arenaRoot != null)
            {
                Log($"=== ARENA ROOT: '{arenaRoot.name}' (path {PathOf(arenaRoot)}) ===");
                DumpSubtree(arenaRoot, 0, 6);
            }
            else Log("ARENA ROOT: <not found by name arena/rink>");

            if (hangarRoot != null && hangarRoot != arenaRoot)
            {
                Log($"=== HANGAR ROOT: '{hangarRoot.name}' (path {PathOf(hangarRoot)}) ===");
                DumpSubtree(hangarRoot, 0, 6);
            }
            else Log("HANGAR ROOT: <not found separately; may be under the arena root>");

            // Spawn markers + goals: log their scene-root parentage so we know whether scaling
            // 'Level Default' repositions them for free (marker.root == Level Default) or the mod
            // must scale spawn/puck positions itself.
            Log("=== SPAWN MARKERS & GOALS (root parentage) ===");
            DumpComponents<PlayerPosition>("PlayerPosition");
            DumpComponents<PuckPosition>("PuckPosition");
            DumpComponents<Goal>("Goal");
            DumpComponents<GoalTrigger>("GoalTrigger");

            Log("================= END DUMP =================");
        }

        // Walk the subtree, but only print nodes that are 'interesting' (named like arena parts)
        // or that carry a Renderer/Collider, so the output stays readable.
        private static void DumpSubtree(Transform t, int depth, int maxDepth)
        {
            if (t == null || depth > maxDepth) return;

            var r = t.GetComponent<Renderer>();
            var c = t.GetComponent<Collider>();
            var mf = t.GetComponent<MeshFilter>();
            bool interesting = IsInteresting(t.name) || r != null || c != null || mf != null;

            if (interesting)
            {
                var sb = new StringBuilder();
                sb.Append(new string(' ', depth * 2));
                sb.Append($"'{t.name}' active={t.gameObject.activeSelf} localScale={V(t.localScale)} localPos={V(t.localPosition)} lossyScale={V(t.lossyScale)}");
                if (mf != null && mf.sharedMesh != null)
                    sb.Append($" | mesh={mf.sharedMesh.name} readable={mf.sharedMesh.isReadable}");
                if (c != null)
                    sb.Append($" | COL {c.GetType().Name} trigger={c.isTrigger} enabled={c.enabled} layer={LayerMask.LayerToName(c.gameObject.layer)} tag={c.tag} boundsSize={V(c.bounds.size)}");
                if (r != null)
                {
                    // isPartOfStaticBatch is the decisive check: if true, the mesh verts are
                    // baked into a scene-root combined mesh and scaling this transform will NOT
                    // move the visual (only the separate colliders move). Non-readable meshes
                    // cannot be un-baked at runtime.
                    sb.Append($" | REND {r.GetType().Name} staticBatch={r.isPartOfStaticBatch} mats=[");
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var m = mats[i];
                        if (m == null) { sb.Append("<null>"); continue; }
                        sb.Append(m.name);
                        if (m.HasProperty("_BaseMap"))
                        {
                            var tex = m.GetTexture("_BaseMap");
                            if (tex != null) sb.Append($"(tex={tex.name} tiling={V2(m.GetTextureScale("_BaseMap"))})");
                        }
                    }
                    sb.Append("]");
                }
                Log(sb.ToString());
            }

            for (int i = 0; i < t.childCount; i++)
                DumpSubtree(t.GetChild(i), depth + 1, maxDepth);
        }

        private static void DumpComponents<T>(string label) where T : Component
        {
            var found = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            if (found == null || found.Length == 0) { Log($"  {label}: <none found>"); return; }
            foreach (var comp in found)
            {
                if (comp == null) continue;
                var t = comp.transform;
                Log($"  {label} '{t.name}' root='{t.root.name}' worldPos={V(t.position)} localPos={V(t.localPosition)} path={PathOf(t)}");
            }
        }

        private static bool IsInteresting(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var n in InterestingNames)
                if (name.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static Transform FindByName(string[] keys)
        {
            Transform best = null;
            int bestDepth = int.MaxValue;
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                bool match = false;
                foreach (var k in keys)
                    if (t.name.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                if (!match) continue;
                // Prefer the shallowest match (closest to a root) as the "root".
                int depth = 0; var p = t.parent; while (p != null) { depth++; p = p.parent; }
                if (depth < bestDepth) { bestDepth = depth; best = t; }
            }
            return best;
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }

        private static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
        private static string V2(Vector2 v) => $"({v.x:F2},{v.y:F2})";
    }
}
