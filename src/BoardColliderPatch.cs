using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace CompetitivePuckTweaks.src
{
    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
    public class BoardColliderPatch
    {
        // Substring set kept lowercase to match against collider.name.ToLowerInvariant()
        // once per scan. Hot-path string allocation is avoided by precomputing this set
        // and caching results across player spawns until the scene drops them.
        private static readonly string[] BoardNameSubstrings =
            { "left", "right", "front", "back", "top", "barrier", "board", "wall" };

        private static readonly List<Collider> foundBoardColliders = new List<Collider>();
        private static bool _scanned;

        [HarmonyPostfix]
        public static void Postfix()
        {
            // Soft-board physics is server-authoritative; reading it from
            // PluginCore.config (CompTweaks) on a remote client picks up
            // local default values and would diverge from the host.
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            if (!PluginCore.config.EnableSoftBoards) return;

            // Drop any destroyed-but-still-referenced colliders before deciding
            // whether to re-scan. Without this, a scene reload would keep
            // foundBoardColliders.Count > 0 even though every entry is dead.
            for (int i = foundBoardColliders.Count - 1; i >= 0; i--)
                if (foundBoardColliders[i] == null) foundBoardColliders.RemoveAt(i);

            if (foundBoardColliders.Count == 0 || !_scanned)
            {
                FindBoardColliders();
                _scanned = true;
            }

            // Re-apply physics to found colliders (in case config changed).
            for (int i = 0; i < foundBoardColliders.Count; i++)
            {
                var collider = foundBoardColliders[i];
                if (collider != null)
                    ApplySoftBoardPhysics(collider);
            }
        }

        private static void FindBoardColliders()
        {
            Collider[] allColliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            foundBoardColliders.Clear();

            for (int i = 0; i < allColliders.Length; i++)
            {
                var collider = allColliders[i];
                string name = collider.name.ToLowerInvariant();
                if (name.IndexOf("collider") < 0) continue;

                bool matched = false;
                for (int j = 0; j < BoardNameSubstrings.Length; j++)
                {
                    if (name.IndexOf(BoardNameSubstrings[j]) >= 0) { matched = true; break; }
                }

                if (matched)
                {
                    foundBoardColliders.Add(collider);
                    PluginCore.Log($"Found board collider: {collider.name}");
                }
            }

            PluginCore.Log($"Found {foundBoardColliders.Count} board colliders to modify");
        }

        public static void ApplySoftBoardPhysics(Collider collider)
        {
            PhysicsMaterial mat = collider.material;
            if (mat == null)
            {
                mat = new PhysicsMaterial("SoftBoardMaterial");
                collider.material = mat;
            }

            mat.bounciness = PluginCore.config.BoardBounciness;
            mat.dynamicFriction = PluginCore.config.BoardFriction;
            mat.staticFriction = PluginCore.config.BoardFriction;
            mat.bounceCombine = PhysicsMaterialCombine.Average;
            mat.frictionCombine = PhysicsMaterialCombine.Average;

            // Lock the attached Rigidbody (if any) to kinematic so a soft
            // collision material doesn't let the board itself drift. Prior
            // code also wrote rb.mass and damping; both are inert under
            // kinematic, so they're not restored.
            Rigidbody rb = collider.attachedRigidbody;
            if (rb != null && !rb.isKinematic)
                rb.isKinematic = true;

            PluginCore.Dbg($"Applied soft board physics to {collider.name} (bounciness: {mat.bounciness}, friction: {mat.dynamicFriction})");
        }
    }
}
