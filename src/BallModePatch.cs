using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

namespace CompetitiveAdjustments
{
    public static class BallModeHelper
    {
        private static readonly HashSet<int> _modifiedPucks = new HashSet<int>();

        public static bool IsBallModeEnabled =>
            ConfigManager.CompAdjustEffective?.BallMode == true;

        public static void TransformPuckToBall(Puck puck)
        {
            if (puck == null) return;
            int id = puck.GetInstanceID();
            if (_modifiedPucks.Contains(id)) return;

            // Find puck material (skip shadows, prefer textured)
            Material puckMaterial = null;
            var originalRenderers = puck.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in originalRenderers)
            {
                if (renderer?.sharedMaterial == null) continue;
                if (renderer.sharedMaterial.name.Contains("Shadow")) continue;
                if (renderer.sharedMaterial.mainTexture != null)
                {
                    puckMaterial = renderer.sharedMaterial;
                    break;
                }
            }
            if (puckMaterial == null)
            {
                foreach (var renderer in originalRenderers)
                {
                    if (renderer?.sharedMaterial != null)
                    {
                        puckMaterial = renderer.sharedMaterial;
                        break;
                    }
                }
            }

            // Create or reuse sphere child
            Transform existing = puck.transform.Find("BallMesh");
            GameObject sphere;
            if (existing != null)
            {
                sphere = existing.gameObject;
            }
            else
            {
                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "BallMesh";
                sphere.transform.SetParent(puck.transform, false);
                sphere.transform.localPosition = Vector3.zero;
                sphere.transform.localRotation = Quaternion.identity;
                // 0.5 multiplier: unit sphere diameter=1, puck visual ~0.5 local units
                sphere.transform.localScale = Vector3.one * 0.5f;

                // Remove the primitive's own collider
                var primitiveCol = sphere.GetComponent<Collider>();
                if (primitiveCol != null) UnityEngine.Object.Destroy(primitiveCol);

                // Apply puck material to sphere
                if (puckMaterial != null)
                {
                    var mr = sphere.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        mr.material = new Material(puckMaterial);
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    }
                }
            }

            // Hide original puck mesh renderers (not the ball)
            var sphereRenderer = sphere.GetComponent<MeshRenderer>();
            foreach (var renderer in originalRenderers)
            {
                if (renderer != sphereRenderer && renderer.gameObject != sphere)
                    renderer.enabled = false;
            }

            // Ensure sphere renderer is visible
            if (sphereRenderer != null)
                sphereRenderer.enabled = true;

            // Server: add a SphereCollider alongside existing colliders.
            // We never remove StickCollider/IceCollider — those are Puck internals.
            // The sphere prevents the disc from lying flat on ice and gives ball bounces.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                if (puck.GetComponent<SphereCollider>() == null)
                {
                    var sc = puck.gameObject.AddComponent<SphereCollider>();
                    sc.isTrigger = false;
                }
                // Size the collider for the puck's current (possibly per-axis) scale.
                UpdateBallColliderRadius(puck);
            }

            _modifiedPucks.Add(id);
        }

        // Base SphereCollider radius at uniform scale 1. With a uniform puck scale
        // the visible ball diameter (sphere child localScale 0.5) and the collider
        // diameter (2 * 0.25) both come out to 0.5 * scale, so they match.
        private const float kBaseBallColliderRadius = 0.25f;

        /// <summary>
        /// Resize the ball-mode SphereCollider to track a per-axis (ellipsoid)
        /// puck. Unity scales a SphereCollider by the LARGEST lossy-scale axis, so
        /// on a stretched ball a fixed radius would balloon to the long axis and
        /// the ball would float / bounce early on the short axes. We divide the
        /// base radius by (max/min) so the collider's world radius tracks the
        /// SHORTEST half-extent and stays inside the visible ellipsoid (it touches
        /// the ice/boards instead of hovering). At uniform scale min==max so this
        /// reduces to the original 0.25 and nothing changes. Server only — the
        /// collider only exists on the server.
        /// </summary>
        public static void UpdateBallColliderRadius(Puck puck)
        {
            if (puck == null) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            var sc = puck.GetComponent<SphereCollider>();
            if (sc == null) return;

            Vector3 s = puck.transform.localScale;
            float maxAxis = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            float minAxis = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));

            sc.radius = maxAxis > 1e-4f
                ? kBaseBallColliderRadius * (minAxis / maxAxis)
                : kBaseBallColliderRadius;
        }

        public static void RestorePuckFromBall(Puck puck)
        {
            if (puck == null) return;
            int id = puck.GetInstanceID();
            if (!_modifiedPucks.Contains(id)) return;

            // Remove ball mesh. Destroy (not DestroyImmediate) so we don't yank
            // a GameObject out from under any logic that might still hold a
            // reference this frame.
            var ballMesh = puck.transform.Find("BallMesh");
            if (ballMesh != null)
            {
                var br = ballMesh.GetComponent<MeshRenderer>();
                if (br != null)
                {
                    // Free the per-puck Material clone created by TransformPuckToBall.
                    if (br.material != null) UnityEngine.Object.Destroy(br.material);
                    br.enabled = false;
                }
                UnityEngine.Object.Destroy(ballMesh.gameObject);
            }

            // Restore original renderers
            foreach (var renderer in puck.GetComponentsInChildren<MeshRenderer>())
            {
                if (renderer != null)
                    renderer.enabled = true;
            }

            // Server: remove the sphere collider that was added for ball mode
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                var sc = puck.GetComponent<SphereCollider>();
                if (sc != null) UnityEngine.Object.Destroy(sc);
            }

            _modifiedPucks.Remove(id);
        }

        public static void RefreshAllPucks()
        {
            try
            {
                if (PuckManager.Instance == null) return;
                var pucks = PuckManager.Instance.GetPucks();
                if (pucks == null) return;

                // Re-assert puck transform scale on every refresh so a ball-mode
                // toggle reshapes existing pucks immediately. GetSyncedPuckScaleVector
                // returns a uniform scale while ball mode is on (so the sphere mesh
                // and SphereCollider stay round) and the per-axis disc scale once it
                // is off; without this an existing puck would keep its old (ellipsoid
                // or disc) transform until it respawned.
                UnityEngine.Vector3 scale = CompetitivePuckTweaks.src.PuckPatch.GetSyncedPuckScaleVector();

                if (IsBallModeEnabled)
                {
                    foreach (var puck in pucks)
                    {
                        if (puck == null) continue;
                        puck.transform.localScale = scale;
                        TransformPuckToBall(puck);
                        // TransformPuckToBall early-returns for an already-ball puck,
                        // so resize the collider here too whenever the scale changes.
                        UpdateBallColliderRadius(puck);
                    }
                }
                else
                {
                    foreach (var puck in pucks)
                    {
                        if (puck == null) continue;
                        RestorePuckFromBall(puck);
                        puck.transform.localScale = scale;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[COMPADJUST] BallMode refresh failed: {ex.Message}");
            }
        }

        public static void OnPuckDespawned(Puck puck)
        {
            if (puck != null)
                _modifiedPucks.Remove(puck.GetInstanceID());
        }
    }
}
