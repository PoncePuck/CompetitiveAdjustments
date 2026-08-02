using System;
using HarmonyLib;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Extends Puck's network position quantisation so the arena reaches
    /// arbitrary distances from the rink centre without losing precision.
    ///
    /// Vanilla Puck quantises positions through SynchronizedObjectManager:
    ///     short encoded = (short)(position * 655f);
    /// shorts are signed 16-bit so the maximum representable position is
    ///     32767 / 655 ~= 50 m
    /// We Harmony-prefix both <c>EncodeSynchronizedObject</c> and
    /// <c>DecodeSynchronizedObjectData</c>, write our own quantisation into
    /// <c>__result</c>, and return false to skip the original body entirely.
    /// Our prefix calls <see cref="ChunkRegistry"/>'s axis helpers, which
    /// subtract / add a per-object chunk offset before / after the 16-bit
    /// clamp.  Range becomes +/-4 km (at sbyte chunk-index resolution) while
    /// keeping vanilla 1.5 mm precision.
    /// </summary>
    public static class NetworkBoundsPatch
    {
        private const float VANILLA_PRECISION = 655f;

        public static bool ChunksEnabled => ChunkRegistry.ChunksActive;

        private static Harmony _harmony;
        private static bool _patched;

        /// <summary>
        /// Install the Harmony prefixes.  Safe to call repeatedly.  Should be
        /// called from both server and client because encode/decode share a
        /// single implementation -- if either side runs vanilla while the
        /// other runs patched, every networked position will appear scaled
        /// or offset wrong.
        /// </summary>
        /// <returns>True when the prefixes are installed and chunked sync may run.</returns>
        public static bool EnsurePatched()
        {
            if (_patched) return true;

            try
            {
                if (_harmony == null)
                    _harmony = new Harmony("compadjust.networkbounds");

                var encode = AccessTools.Method(typeof(SynchronizedObjectManager), "EncodeSynchronizedObject");
                var decode = AccessTools.Method(typeof(SynchronizedObjectManager), "DecodeSynchronizedObjectData");

                if (encode == null || decode == null)
                {
                    Debug.LogWarning("[COMPADJUST] Could not find SynchronizedObjectManager encode/decode methods -- chunked sync disabled.");
                    return false;
                }

                _harmony.Patch(encode,
                    prefix: new HarmonyMethod(typeof(NetworkBoundsPatch), nameof(EncodePrefix)));
                _harmony.Patch(decode,
                    prefix: new HarmonyMethod(typeof(NetworkBoundsPatch), nameof(DecodePrefix)));

                _patched = true;
                CompetitiveAdjustments.ConfigManager.Log("NetworkBoundsPatch: full-replacement prefixes installed on EncodeSynchronizedObject / DecodeSynchronizedObjectData.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] Failed to apply network bounds patches: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Activate chunked sync mode.  Precision stays at 655 (the chunked
        /// mode's whole purpose is to keep vanilla precision), per-object
        /// offsets become live, and the server-side hysteresis sweep starts
        /// announcing chunk changes.  Idempotent.
        /// </summary>
        public static void EnableOpenWorldPrecision()
        {
            // Never light up the chunked stack on top of a failed patch.  The
            // encode/decode prefixes are what make chunk offsets symmetric; with
            // them missing, turning ChunksActive on means this side applies chunk
            // offsets the other side knows nothing about.  It also used to latch
            // the feature on permanently, because Disable() bailed on !_patched.
            if (!EnsurePatched())
            {
                Debug.LogWarning("[COMPADJUST] Chunked network sync NOT activated: encode/decode prefixes are not installed. Positions stay vanilla.");
                return;
            }

            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;
            ChunkRegistry.ChunksActive = true;

            ChunkSyncServer.Enable();
            ChunkSyncClient.Enable();
            // ToasterPerformance compat: claims Server_GatherSynchronizedObjectData
            // / Client_SynchronizeObjects at Priority.First so chunked encode/decode
            // survives even when Toaster's full-replacement prefixes are installed.
            SyncObjectChunkPatches.Enable();

            CompetitiveAdjustments.ConfigManager.Log($"Chunked network sync ACTIVE -- precision {VANILLA_PRECISION} (1.5 mm grid), chunk size {ChunkRegistry.ChunkSizeMeters} m.");
        }

        /// <summary>Returns to vanilla precision with no chunking.</summary>
        public static void RestoreVanillaPrecision()
        {
            SyncObjectChunkPatches.Disable();
            ChunkSyncServer.Disable();
            ChunkSyncClient.Disable();
            ChunkRegistry.ChunksActive = false;
            ChunkRegistry.Clear();
            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;
            CompetitiveAdjustments.ConfigManager.Log($"Network precision restored to vanilla {VANILLA_PRECISION} (chunks disabled).");
        }

        /// <summary>Full teardown -- unpatches Harmony and clears all state.  Idempotent.</summary>
        public static void Disable()
        {
            // Deliberately NOT gated on _patched alone.  Sub-systems and registry
            // state can be live while _patched is false (a caller that enabled
            // them despite a patch failure), and bailing here is what made the
            // feature impossible to turn back off.  Everything below is idempotent.
            if (!_patched && _harmony == null && !ChunkRegistry.ChunksActive) return;

            SyncObjectChunkPatches.Disable();
            ChunkSyncServer.Disable();
            ChunkSyncClient.Disable();
            ChunkRegistry.ChunksActive = false;
            ChunkRegistry.Clear();
            ChunkRegistry.ActivePrecision = VANILLA_PRECISION;

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Debug.LogWarning("[COMPADJUST] UnpatchSelf failed: " + ex.Message); }
                _harmony = null;
            }

            _patched = false;
            CompetitiveAdjustments.ConfigManager.Log("NetworkBoundsPatch fully disabled.");
        }

        // ──────────────────────────────────────────────────────────────────
        // Harmony prefixes -- full-replacement encode / decode.
        // ──────────────────────────────────────────────────────────────────

        // b1117 changed EncodeSynchronizedObject's return type from the b897
        // ValueTuple<ushort, short[], short[]> to the SynchronizedObjectData
        // struct. __result must match the current return type or Harmony throws
        // at patch time (which was silently disabling the whole chunked feature).
        public static bool EncodePrefix(
            ulong networkObjectId,
            Vector3 position,
            Quaternion rotation,
            ref SynchronizedObjectData __result)
        {
            ushort id = (ushort)networkObjectId;

            __result = new SynchronizedObjectData
            {
                NetworkObjectId = id,
                X = ChunkRegistry.EncodeX(position.x, id),
                Y = ChunkRegistry.EncodeY(position.y),
                Z = ChunkRegistry.EncodeZ(position.z, id),
                Rx = (short)(rotation.x * 32767f),
                Ry = (short)(rotation.y * 32767f),
                Rz = (short)(rotation.z * 32767f),
                Rw = (short)(rotation.w * 32767f)
            };

            return false;
        }

        public static bool DecodePrefix(
            SynchronizedObjectData synchronizedObjectData,
            ref System.ValueTuple<ushort, Vector3, Quaternion> __result)
        {
            ushort id = synchronizedObjectData.NetworkObjectId;

            float rx = synchronizedObjectData.Rx / 32767f;
            float ry = synchronizedObjectData.Ry / 32767f;
            float rz = synchronizedObjectData.Rz / 32767f;
            float rw = synchronizedObjectData.Rw / 32767f;

            float px = ChunkRegistry.DecodeX(synchronizedObjectData.X, id);
            float py = ChunkRegistry.DecodeY(synchronizedObjectData.Y);
            float pz = ChunkRegistry.DecodeZ(synchronizedObjectData.Z, id);

            __result = new System.ValueTuple<ushort, Vector3, Quaternion>(
                id,
                new Vector3(px, py, pz),
                new Quaternion(rx, ry, rz, rw));

            return false;
        }
    }
}
