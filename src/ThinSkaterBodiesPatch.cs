using HarmonyLib;
using UnityEngine;

namespace DashFallMod {
    // Must run on EVERY role, not just the server: it edits the torso/groin
    // MeshCollider transforms, which are purely local per-peer prefab state and
    // are never networked. Living in CompetitivePuckTweaks.src meant this was
    // only ever installed where Tweaks.PluginCore.OnEnable succeeds (a dedicated
    // server or listen host), so a remote client never got the patch and never
    // saw a thinned body at all. Same fix as PlayerLegPadPatch.
    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
    public static class ThinSkaterBodiesPatch {
        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance, ref PlayerMesh ___playerMesh) {
            var cfg = CompetitiveAdjustments.ConfigManager.CompTweaksEffective;
            if (cfg == null || !cfg.ThinSkaterBodies || ___playerMesh == null || __instance.name.Contains("Goalie")) return;

            float factor = cfg.SkaterThinningFactor;
            if (factor == 1f)
                return;

            var groinMcs = ___playerMesh.PlayerGroin?.GetComponentsInChildren<MeshCollider>();
            foreach (MeshCollider groinMc in groinMcs) {
                if (groinMc == null)
                    continue;

                var s = groinMc.transform.localScale;
                groinMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);
            }

            var torsoMcs = ___playerMesh.PlayerTorso?.GetComponentsInChildren<MeshCollider>();
            foreach (MeshCollider torsoMc in torsoMcs) {
                if (torsoMc == null)
                    continue;

                CompetitiveAdjustments.ConfigManager.Log(
                    $"Torso collider: convex={torsoMc.convex} isTrigger={torsoMc.isTrigger} " +
                    $"attachedRigidbody={(torsoMc.attachedRigidbody != null ? torsoMc.attachedRigidbody.name : "none")} " +
                    $"rbKinematic={(torsoMc.attachedRigidbody != null ? torsoMc.attachedRigidbody.isKinematic.ToString() : "n/a")}");

                var s = torsoMc.transform.localScale;
                torsoMc.transform.localScale = new Vector3(s.x * factor, s.y, s.z * factor);
            }
        }
    }
}
