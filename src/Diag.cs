using System.Text;
using UnityEngine;

namespace CompetitiveAdjustments
{
    /// <summary>
    /// One-shot runtime diagnostics for the b1117 migration. The arena-collider
    /// and goal-frame systems key off runtime GameObject names/hierarchy that are
    /// not visible in the decompiled game source, so these dumps report the real
    /// structure to the log. Prefix [COMPDIAG]. Set Enabled=false to silence.
    /// </summary>
    public static class Diag
    {
        public static bool Enabled = true;

        // One-shot guard so the arena-collider sync path (which can run repeatedly)
        // dumps only once per session. Fires at the first sync after load, so loading
        // into a game with the resized arena captures it.
        public static bool ArenaDumped;

        public static void Log(string msg)
        {
            if (!Enabled) return;
            Debug.Log("[COMPDIAG] " + msg);
        }

        public static string Path(Transform root, Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null && p != root)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }

        /// <summary>Dump every collider under a root: path, layer, enabled/trigger/tag, world-bounds size.</summary>
        public static void DumpColliders(Transform root, string label)
        {
            if (!Enabled || root == null) return;
            int barrier = LayerMask.NameToLayer("Barrier");
            int boards = LayerMask.NameToLayer("Boards");
            int ice = LayerMask.NameToLayer("Ice");
            int puck = LayerMask.NameToLayer("Puck");
            int boardLayer = boards >= 0 ? boards : barrier;
            bool puckHitsBoard = puck >= 0 && boardLayer >= 0 && !Physics.GetIgnoreLayerCollision(puck, boardLayer);
            bool puckHitsDefault = puck >= 0 && !Physics.GetIgnoreLayerCollision(puck, 0);
            Log($"=== COLLIDERS {label} root='{root.name}' worldScale={root.lossyScale} ===");
            Log($"layers: Barrier={barrier} Boards={boards} Ice={ice} Puck={puck} boardLayer={boardLayer} | Puck<->board collide={puckHitsBoard} | Puck<->Default collide={puckHitsDefault}");
            var cols = root.GetComponentsInChildren<Collider>(true);
            Log($"collider count={cols.Length}");
            foreach (var c in cols)
            {
                if (c == null) continue;
                Vector3 s = c.bounds.size;
                Log($"  {Path(root, c.transform)} | {c.GetType().Name} | layer={LayerMask.LayerToName(c.gameObject.layer)}({c.gameObject.layer}) | enabled={c.enabled} activeSelf={c.gameObject.activeSelf} | trigger={c.isTrigger} | tag={c.tag} | size=({s.x:F2},{s.y:F2},{s.z:F2})");
            }
        }

        /// <summary>Dump a goal's transform and its child renderers so frame orientation can be computed.</summary>
        public static void DumpGoal(Transform goalRoot, string teamLabel, Transform originalFrame, Transform customFrame)
        {
            if (!Enabled || goalRoot == null) return;
            Log($"=== GOAL team={teamLabel} '{goalRoot.name}' ===");
            Log($"  goal worldPos={goalRoot.position} worldEuler={goalRoot.eulerAngles} localEuler={goalRoot.localEulerAngles} lossyScale={goalRoot.lossyScale} forward={goalRoot.forward}");
            if (originalFrame != null)
                Log($"  ORIGINAL frame '{originalFrame.name}' worldPos={originalFrame.position} worldEuler={originalFrame.eulerAngles} localEuler={originalFrame.localEulerAngles}");
            else
                Log("  ORIGINAL frame: <not found>");
            if (customFrame != null)
                Log($"  CUSTOM frame worldPos={customFrame.position} worldEuler={customFrame.eulerAngles} localEuler={customFrame.localEulerAngles} localScale={customFrame.localScale}");
            foreach (var r in goalRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                Log($"    renderer '{Path(goalRoot, r.transform)}' worldEuler={r.transform.eulerAngles} localEuler={r.transform.localEulerAngles} bounds={r.bounds.size}");
            }
        }
    }
}
