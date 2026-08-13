using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DashFallMod.Net
{
    /// <summary>
    /// Server half of wrapped positions: wrap the coordinate before vanilla quantises it,
    /// mark the record, and keep every capable client's predictor seeded.
    ///
    /// STEP 4 STATE: Armed is a compile-time false. Every hook installs and every table
    /// populates, but nothing is wrapped and the marker bit is never set. The point of
    /// shipping it in this state is to prove the hooks are harmless before they do
    /// anything, because the expensive failures in this subsystem have all been "the hook
    /// itself broke something", not "the maths was wrong".
    ///
    /// THREE PROPERTIES THAT ARE NOT NEGOTIABLE, each because a review found the bug:
    ///
    /// 1. EVERY hook is wrapped in try/catch with a one-shot disarm. SynchronizedObject's
    ///    OnAfterSimulate wraps the whole server tick in a catch, so an unguarded throw in
    ///    any hook does not crash, it silently stops object sync for every player on the
    ///    server. Failing loudly and disarming is strictly better than failing invisibly.
    ///
    /// 2. The constructor hook stamps its table entry UNCONDITIONALLY, above every arming
    ///    test, and Write acts only when the stamp matches the current generation. Without
    ///    that, disarming between construction and write leaves a stale entry that gets
    ///    applied a second time and puts the object a whole period out.
    ///
    /// 3. The downgrade for a client that cannot decode re-encodes from the stored TRUE
    ///    world position, never by decompress-add-recompress from the wrapped short. That
    ///    reproduces exactly what an unmodded server would have sent, saturation included,
    ///    and cannot inherit a stale offset.
    ///
    /// Default on unknown state is DO NOTHING: no published target, or a generation
    /// mismatch, means the record is left exactly as vanilla built it.
    /// </summary>
    internal static class WrapSyncEncode
    {
        /// <summary>
        /// Step 4 ships with this hard false. Step 5 replaces it with a synced config flag
        /// gated on the runtime self-test passing.
        /// </summary>
        private const bool ArmedConst = false;

        public static bool Armed => ArmedConst && !_disarmed && _installed;

        private const string HarmonyId = "compadjust.wrapsync.encode";
        private static Harmony _harmony;
        private static bool _installed;
        private static bool _disarmed;

        // Which client the record currently being serialized is destined for.
        // Published by the Server_SynchronizePlayer prefix and cleared by its finalizer,
        // because Write has no idea who it is writing for.
        private static ulong _targetClientId;
        private static bool _hasTarget;

        // Ids whose pose jumped far enough this tick that the client's predictor cannot be
        // trusted. Drained in the Server_SynchronizePlayer prefix.
        private static readonly List<ushort> _teleported = new List<ushort>();

        public static bool Install()
        {
            if (_installed) return true;
            try
            {
                var capture = AccessTools.Method(typeof(SynchronizedObjectSnapshot), "Capture");
                var ctor = AccessTools.Constructor(typeof(SynchronizedObjectData), new[]
                {
                    typeof(ulong), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector3)
                });
                var syncPlayer = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizePlayer");
                var unreliableRpc = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
                var reliableRpc = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ReliableSynchronizeObjectsRpc");
                var write = AccessTools.Method(typeof(SynchronizedObjectData), "Write");
                var removeObject = AccessTools.Method(typeof(SynchronizedObjectManager), "RemoveSynchronizedObject");
                var registryClear = AccessTools.Method(typeof(SynchronizedObjectRegistry), "Clear");
                var forceSync = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ForceSynchronizeClientId");

                var all = new Dictionary<string, System.Reflection.MethodBase>
                {
                    { "Capture", capture }, { "ctor", ctor }, { "Server_SynchronizePlayer", syncPlayer },
                    { "Server_SynchronizeObjectsRpc", unreliableRpc },
                    { "Server_ReliableSynchronizeObjectsRpc", reliableRpc },
                    { "Write", write },
                    { "RemoveSynchronizedObject", removeObject }, { "Registry.Clear", registryClear },
                    { "Server_ForceSynchronizeClientId", forceSync },
                };
                foreach (var kv in all)
                {
                    if (kv.Value == null)
                    {
                        Debug.LogWarning($"[COMPADJUST] WrapSyncEncode: target '{kv.Key}' did not resolve on this build. "
                                         + "Not installing; wrapped positions stay off.");
                        return false;
                    }
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(capture, prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(CapturePrefix)));
                _harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(CtorPrefix)));
                _harmony.Patch(syncPlayer,
                    prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(SyncPlayerPrefix)),
                    finalizer: new HarmonyMethod(typeof(WrapSyncEncode), nameof(SyncPlayerFinalizer)));
                _harmony.Patch(forceSync,
                    prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(ForceSyncPrefix)),
                    finalizer: new HarmonyMethod(typeof(WrapSyncEncode), nameof(SyncPlayerFinalizer)));
                _harmony.Patch(unreliableRpc, prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(UnreliableRpcPrefix)));
                _harmony.Patch(reliableRpc, prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(ReliableRpcPrefix)));
                _harmony.Patch(write, prefix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(WritePrefix)));
                _harmony.Patch(removeObject, postfix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(RemoveObjectPostfix)));
                _harmony.Patch(registryClear, postfix: new HarmonyMethod(typeof(WrapSyncEncode), nameof(RegistryClearPostfix)));

                _installed = true;
                CompetitiveAdjustments.ConfigManager.Log(
                    $"WrapSync encode installed, ARMED={Armed}. "
                    + $"Periods X={WrapSync.PeriodX} Y={WrapSync.PeriodY} Z={WrapSync.PeriodZ}, "
                    + $"fingerprint {WrapSyncSeed.Fingerprint():X8}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[COMPADJUST] WrapSyncEncode failed to install: " + ex);
                try { _harmony?.UnpatchSelf(); } catch { }
                _harmony = null;
                _installed = false;
                return false;
            }
        }

        /// <summary>
        /// One-shot kill. Called from any hook that throws. Leaves the patches resident but
        /// makes every one of them a no-op, which is safer than unpatching from inside a
        /// patch while the server tick is on the stack.
        /// </summary>
        private static void Disarm(string where, Exception ex)
        {
            if (_disarmed) return;
            _disarmed = true;
            Debug.LogError($"[COMPADJUST] WrapSync encode DISARMED after a throw in {where}: {ex}. "
                           + "Positions revert to vanilla absolute encoding for the rest of this session.");
        }

        // ── E1: generation ────────────────────────────────────────────────────

        private static void CapturePrefix()
        {
            try
            {
                WrapSync.BumpGeneration();

                // Cleared HERE, at the start of the tick, not in the per-client finalizer.
                // Server_Tick calls Server_SynchronizePlayer once per client and each of
                // them must see the same teleport list; clearing it after the first client
                // meant everyone else silently missed the correction.
                _teleported.Clear();
            }
            catch (Exception ex) { Disarm("Capture", ex); }
        }

        // ── E2: the only quantisation entry point ─────────────────────────────

        private static void CtorPrefix(ulong networkObjectId, ref Vector3 position)
        {
            try
            {
                if (networkObjectId > ushort.MaxValue) return;
                ushort id = (ushort)networkObjectId;

                Vector3 trueWorld = position;
                bool wrap = Armed;

                // Teleport detection is INSIDE the arming test. It used to sit above it,
                // on the reasoning that observing costs nothing. That was wrong: the ids it
                // collects are fed to SynchronizedPlayerState.Forget below, which drops the
                // object's send state and changes vanilla's send scheduling. A disarmed
                // build that still reschedules sends is not inert, and "prove the hooks are
                // harmless before they do anything" is exactly the claim it invalidated.
                bool teleported = false;
                if (wrap && WrapSync.PeekServerWorld(id, out Vector3 prev))
                {
                    Vector3 d = trueWorld - prev;
                    teleported = Mathf.Abs(d.x) > WrapSync.JumpGuardX
                              || Mathf.Abs(d.y) > WrapSync.JumpGuardYZ
                              || Mathf.Abs(d.z) > WrapSync.JumpGuardYZ;
                    if (teleported) _teleported.Add(id);
                }

                if (wrap)
                {
                    position = WrapSync.Wrap(trueWorld, out _, out _, out _);
                }

                // UNCONDITIONAL, and above every arming test on purpose. A record that was
                // built while disarmed must still leave a stamp saying so, or Write cannot
                // tell "not wrapped" from "stale entry from when we were armed".
                WrapSync.StampServer(id, trueWorld, wrap, teleported);
            }
            catch (Exception ex) { Disarm("SynchronizedObjectData..ctor", ex); }
        }

        // ── E3 / E4: publish the target, drain teleports, flush seeds ─────────

        private static void SyncPlayerPrefix(SynchronizedPlayerState synchronizedPlayerState)
        {
            try
            {
                _hasTarget = false;
                var player = synchronizedPlayerState?.Player;
                if (player == null) return;

                _targetClientId = player.OwnerClientId;
                _hasTarget = true;

                // A teleported object's predictor is meaningless, so drop this client's
                // send state for it. Forget makes HasLastSentData false, which makes
                // IsFullSendDue true, which routes the object to a reliable full send
                // carrying a fresh seed. Safe here because PlanObject rewrites the LOD
                // selection before PlanSend reads the divisor.
                if (!Armed) return;   // never touch vanilla send state while disarmed

                DrainPendingWipe(synchronizedPlayerState);
                for (int i = 0; i < _teleported.Count; i++)
                {
                    synchronizedPlayerState.Forget(_teleported[i]);
                    WrapSync.TxTeleportForces++;
                }
            }
            catch (Exception ex) { Disarm("Server_SynchronizePlayer prefix", ex); }
        }

        /// <summary>
        /// The join path. Server_ForceSynchronizeClientId captures its own snapshot and calls
        /// Server_ReliableSynchronizeObjectsRpc directly, so it never passes through
        /// Server_SynchronizePlayer and the target published there. Without this hook the
        /// whole join batch was written with no known recipient, which after the WritePrefix
        /// inversion means it would be correctly downgraded rather than corrupted, but a
        /// joining client would then be the one peer that never receives a wrapped record
        /// and never gets seeded from one.
        ///
        /// Shares SyncPlayerFinalizer, which clears the target unconditionally: two publish
        /// sites, one retraction, and no way for a target to outlive its scope.
        /// </summary>
        private static void ForceSyncPrefix(ulong clientId)
        {
            try
            {
                _targetClientId = clientId;
                _hasTarget = true;
            }
            catch (Exception ex) { Disarm("ForceSyncPrefix threw", ex); }
        }

        private static void SyncPlayerFinalizer()
        {
            try
            {
                // Mop up anything queued but not flushed by the reliable path, then clear
                // the target so a stray Write outside this scope cannot act.
                if (_hasTarget && WrapSyncSeed.PendingCount > 0)
                    WrapSyncSeed.Flush(_targetClientId, TryWorldOf);

                WrapSyncSeed.ClearPending();
                _hasTarget = false;
                // _teleported is deliberately NOT cleared here; see CapturePrefix.
            }
            catch (Exception ex) { Disarm("Server_SynchronizePlayer finalizer", ex); }
        }

        // The downgrade re-encodes by hand, so it must compress against the SAME bounds the
        // constructor used. SyncRangePatch transpiles those very literals in that very
        // constructor and ArenaTweaks turns it on for any arena resize, so hardcoding
        // -25/25 here silently produced a differently-scaled coordinate on exactly the
        // servers most likely to need the downgrade. Reading the live accessors makes the
        // two features compose instead of requiring them to be kept apart by convention.
        private static float MinX => SyncRangePatch.IsPatched ? SyncRangePatch.GetMinX() : -25f;
        private static float MaxX => SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxX() : 25f;
        private static float MinY => SyncRangePatch.IsPatched ? SyncRangePatch.GetMinY() : -50f;
        private static float MaxY => SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxY() : 50f;
        private static float MinZ => SyncRangePatch.IsPatched ? SyncRangePatch.GetMinZ() : -50f;
        private static float MaxZ => SyncRangePatch.IsPatched ? SyncRangePatch.GetMaxZ() : 50f;

        private static bool TryWorldOf(ushort id, out Vector3 world)
        {
            // Propagates failure instead of returning the zeroed table slot. Seeding an
            // unknown id with (0,0,0) is strictly worse than not seeding it: ApplySeed sets
            // the seen flag, so the client stops counting itself unseeded and thereafter
            // unwraps that object against the world origin forever.
            return WrapSync.PeekServerWorld(id, out world);
        }

        // ── E5 / E6: seed queueing and flush ──────────────────────────────────

        private static void UnreliableRpcPrefix(SynchronizedObjectData[] synchronizedObjectsData)
        {
            try
            {
                if (!Armed || synchronizedObjectsData == null) return;
                // A delta that happens to carry every component is treated by the client as
                // a first sight, so it needs a seed even though it is on the unreliable
                // path. Almost always zero of these.
                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                    if (synchronizedObjectsData[i].HasAllComponents)
                        WrapSyncSeed.QueueSeed(synchronizedObjectsData[i].NetworkObjectId);
            }
            catch (Exception ex) { Disarm("Server_SynchronizeObjectsRpc prefix", ex); }
        }

        private static void ReliableRpcPrefix(SynchronizedObjectData[] synchronizedObjectsData)
        {
            try
            {
                if (!Armed || synchronizedObjectsData == null || !_hasTarget) return;

                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                    WrapSyncSeed.QueueSeed(synchronizedObjectsData[i].NetworkObjectId);

                // Flushed here, immediately before the reliable RPC goes out and on the
                // same reliable-sequenced pipeline, so the seed is applied ahead of the
                // batch it describes rather than racing it.
                WrapSyncSeed.Flush(_targetClientId, TryWorldOf);
                WrapSyncSeed.ClearPending();
            }
            catch (Exception ex) { Disarm("Server_ReliableSynchronizeObjectsRpc prefix", ex); }
        }

        // ── E7: mark, or downgrade. Never writes a byte. ──────────────────────

        private static void WritePrefix(ref SynchronizedObjectData __instance)
        {
            try
            {
                ushort id = __instance.NetworkObjectId;

                // Read the stamp FIRST, because it is the only thing that knows whether the
                // constructor already wrapped this record.
                if (!WrapSync.TryGetServerStamp(id, out Vector3 trueWorld, out bool wrapped))
                {
                    // No stamp for this generation. The ctor did not build this record this
                    // tick, so it was never wrapped and there is nothing to undo.
                    WrapSync.TxStaleGen++;
                    return;
                }
                if (!wrapped) return;   // built absolute; leave it exactly as vanilla made it

                // From here the record IS wrapped, so every remaining path must either mark
                // it or restore it. Returning early here was the design's worst bug: it
                // shipped a chunk-local coordinate labelled as an absolute one, which is a
                // silent 50 m displacement rather than a visible failure. "Do nothing" is
                // only a safe default when doing nothing leaves the record untouched, and
                // here it does not.
                if (!_disarmed && _hasTarget && WrapSync.IsPeerCapable(_targetClientId))
                {
                    // Mark it. Bit 14 gates no serialization branch in Write or Read
                    // (verified in IL: neither has any mask literal above 2048), so this
                    // cannot change the record's length. That length-neutrality is the
                    // whole reason this design cannot desynchronise the reader.
                    __instance.ChangeMask |= WrapSync.MaskWrapped;
                    return;
                }

                // Everything else, and this is now the DEFAULT rather than the exception:
                // an incapable peer, no published target, or a mid-tick disarm. Re-encode
                // from the stored TRUE world position. Not by
                // decompressing the wrapped short and adding an offset, which would carry
                // any stale state straight into the output. This is bit-for-bit what an
                // unmodded server would have sent for this pose, saturation included.
                __instance.X = NetworkingUtils.CompressFloatToShort(trueWorld.x, MinX, MaxX);
                __instance.Y = NetworkingUtils.CompressFloatToShort(trueWorld.y, MinY, MaxY);
                __instance.Z = NetworkingUtils.CompressFloatToShort(trueWorld.z, MinZ, MaxZ);
            }
            catch (Exception ex) { Disarm("SynchronizedObjectData.Write prefix", ex); }
        }

        // ── E8 / E9 / X1: eviction ────────────────────────────────────────────

        /// <summary>
        /// Called on genuine disconnect, from ClientVersionCheck.ForgetClient. Capability
        /// used to be dropped in a Server_RemoveSynchronizedPlayer postfix instead, which is
        /// despawn rather than disconnect: it fires on team change and respawn, and because
        /// the capability announce is sent once per session it could never be regained. A
        /// client that changed teams silently downgraded itself for the rest of the match.
        /// </summary>
        public static void ForgetClient(ulong clientId)
        {
            try
            {
                _pendingWipe.Remove(clientId);
                if (_hasTarget && _targetClientId == clientId) _hasTarget = false;
            }
            catch { }
        }

        private static void RemoveObjectPostfix(SynchronizedObject synchronizedObject)
        {
            try
            {
                if (synchronizedObject == null) return;
                ulong wide = synchronizedObject.NetworkObjectId;
                if (wide > ushort.MaxValue) return;
                WrapSync.EvictServer((ushort)wide);
            }
            catch (Exception ex) { Disarm("RemoveSynchronizedObject", ex); }
        }

        /// <summary>
        /// Session end, and it covers both roles: Server_Dispose calls registry.Clear(),
        /// and Event_Server_OnServerStopped calls Server_Dispose. A dedicated server never
        /// runs the client-side teardown paths, so hanging session cleanup off the registry
        /// is what makes it fire at all.
        /// </summary>
        private static void RegistryClearPostfix()
        {
            try
            {
                WrapSync.ClearServer();
                WrapSync.ClearPeers();
                WrapSyncSeed.ClearPending();
                _hasTarget = false;
                _teleported.Clear();
            }
            catch (Exception ex) { Disarm("SynchronizedObjectRegistry.Clear", ex); }
        }

        // ── Late join ─────────────────────────────────────────────────────────

        /// <summary>
        /// Drop a client's send baseline so every object, including sleeping ones, is
        /// resent in full.
        ///
        /// This is the entire late-join mechanism. A client announces capability after the
        /// join seed has gone out, so anything stationary during the join window was sent
        /// unwrapped and then went to sleep, and a sleeping object is never resent. Wiping
        /// the baseline makes IsFullSendDue true for everything, because it tests
        /// HasLastSentData before IsAwake.
        /// </summary>
        /// <summary>
        /// Drop every cached send state for one client, so the next tick re-sends every
        /// object in full instead of delta-ing against records that client decoded under the
        /// other set of rules.
        ///
        /// This was a no-op stub while ClientVersionCheck already logged "send baseline
        /// wiped to force a complete resend" on the line after calling it. That log was
        /// false, and the moment the capability bit can flip mid-session it also becomes a
        /// correctness hole: vanilla only re-sends a component when it changed by more than
        /// POSITION_CHANGE_THRESHOLD, so a stationary object that was last sent wrapped
        /// would never be re-sent absolute, and would sit a whole period out indefinitely.
        ///
        /// Deferred rather than done inline because the wipe has to happen between ticks,
        /// not during one: this is called from a named-message handler, which can land in
        /// the middle of a serialization pass.
        /// </summary>
        /// <returns>True if a wipe was queued, so the caller can log what actually happened.</returns>
        public static bool WipeSendBaseline(ulong clientId)
        {
            try
            {
                // Gated at the queue, not at the drain, so the disarmed build queues nothing
                // and the caller's log tells the truth. While disarmed nothing was ever
                // wrapped, so no cached record needs invalidating and the wipe would be a
                // pure unprompted resend of every object -- vanilla send scheduling changed
                // by a mod that is supposed to be doing nothing at all.
                //
                // Arming must therefore wipe every connected client's baseline itself,
                // rather than relying on the announce that already happened. That belongs
                // with the arming switch in Step 5.
                if (!Armed) return false;
                _pendingWipe.Add(clientId);
                return true;
            }
            catch (Exception ex) { Disarm("WipeSendBaseline", ex); return false; }
        }

        private static readonly HashSet<ulong> _pendingWipe = new HashSet<ulong>();
        private static System.Reflection.FieldInfo _sendStatesField;
        private static bool _sendStatesFieldResolved;

        /// <summary>
        /// Runs at the top of a player's serialization pass, which is the one point where
        /// the send-state dictionary is provably not being iterated. Returns true if it
        /// wiped, so the caller can log it honestly.
        /// </summary>
        private static bool DrainPendingWipe(SynchronizedPlayerState state)
        {
            if (_pendingWipe.Count == 0) return false;
            var player = state?.Player;
            if (player == null) return false;
            ulong owner = player.OwnerClientId;
            if (!_pendingWipe.Remove(owner)) return false;

            if (!_sendStatesFieldResolved)
            {
                _sendStatesFieldResolved = true;
                _sendStatesField = AccessTools.Field(typeof(SynchronizedPlayerState), "sendStates");
            }
            if (_sendStatesField == null) return false;

            // Clear in place rather than assigning a fresh dictionary: the field is read
            // directly by GetSendState/SetSendState/Forget, and replacing the instance would
            // strand any reference vanilla is already holding for this pass.
            if (_sendStatesField.GetValue(state) is System.Collections.IDictionary dict)
            {
                int n = dict.Count;
                dict.Clear();
                WrapSync.TxBaselineWipes++;
                CompetitiveAdjustments.ConfigManager.Log(
                    $"WrapSync: dropped {n} cached send states for client {owner}; "
                    + "every object will be re-sent in full on this tick.");
                return true;
            }
            return false;
        }
    }
}
