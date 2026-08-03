using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

// ═══════════════════════════════════════════════════════════════════════════
// Server-side patches — applied via HarmonyPatchHelper for "CompetitivePuckTweaks"
// ═══════════════════════════════════════════════════════════════════════════
namespace CompetitivePuckTweaks.src
{
    /// <summary>
    /// Shared accessor cache for private PlayerInput fields used by both
    /// FreeBlade and HighSticking features.
    /// </summary>
    internal static class StickAngleRefs
    {
        internal static readonly AccessTools.FieldRef<PlayerInput, int> minBladeRef =
            AccessTools.FieldRefAccess<PlayerInput, int>("minimumBladeAngle");

        internal static readonly AccessTools.FieldRef<PlayerInput, int> maxBladeRef =
            AccessTools.FieldRefAccess<PlayerInput, int>("maximumBladeAngle");

        internal static readonly AccessTools.FieldRef<PlayerInput, Vector2> minStickAngleRef =
            AccessTools.FieldRefAccess<PlayerInput, Vector2>("minimumStickRaycastOriginAngle");

        internal static readonly AccessTools.FieldRef<PlayerInput, Vector2> maxStickAngleRef =
            AccessTools.FieldRefAccess<PlayerInput, Vector2>("maximumStickRaycastOriginAngle");

        internal static readonly AccessTools.FieldRef<PlayerInput, float> bladeAngleBufferRef =
            AccessTools.FieldRefAccess<PlayerInput, float>("bladeAngleBuffer");

        // Routed through CompAdjustEffective so EnableCompAdjust=false silences
        // FreeBlade AND HighSticking at the same time, without each consumer
        // needing its own master check.
        internal static CompetitiveAdjustments.CompAdjustConfig Cfg =>
            CompetitiveAdjustments.ConfigManager.CompAdjustEffective;

        // Saved vanilla blade angle limits per player (captured before first modification)
        private static readonly Dictionary<int, (int min, int max)> _savedBladeAngles =
            new Dictionary<int, (int min, int max)>();

        /// <summary>
        /// Save the vanilla blade angle limits for a player before any modification.
        /// Called from spawn patches so we can restore on disable.
        /// </summary>
        internal static void SaveOriginals(Player player)
        {
            if (player?.PlayerInput == null) return;
            int id = player.GetInstanceID();
            if (_savedBladeAngles.ContainsKey(id)) return;
            _savedBladeAngles[id] = (
                minBladeRef(player.PlayerInput),
                maxBladeRef(player.PlayerInput)
            );
        }

        /// <summary>
        /// Re-apply or restore FreeBlade / HighSticking limits on all live players.
        /// Called from /reload and config sync receive so changes take effect at runtime.
        /// </summary>
        internal static void RefreshFreeBladeForAllPlayers()
        {
            var cfg = Cfg;
            try
            {
                foreach (var player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (player?.PlayerInput == null) continue;
                    int id = player.GetInstanceID();

                    // Save originals on first encounter (for players who spawned before mod loaded)
                    if (!_savedBladeAngles.ContainsKey(id))
                        _savedBladeAngles[id] = (minBladeRef(player.PlayerInput), maxBladeRef(player.PlayerInput));

                    if (cfg.FreeBladeEnabled)
                    {
                        minBladeRef(player.PlayerInput) = -127;
                        maxBladeRef(player.PlayerInput) = 127;
                    }
                    else if (_savedBladeAngles.TryGetValue(id, out var orig))
                    {
                        minBladeRef(player.PlayerInput) = orig.min;
                        maxBladeRef(player.PlayerInput) = orig.max;
                    }

                    if (cfg.HighStickingEnabled)
                    {
                        Vector2 min = minStickAngleRef(player.PlayerInput);
                        min.x = cfg.HighStickingMaxAngle;
                        minStickAngleRef(player.PlayerInput) = min;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] RefreshFreeBlade failed: {ex.Message}");
            }
        }
    }

    // ── 1. Apply blade / stick angle limits on every spawn ──────────────────

    [HarmonyPatch(typeof(Player), "Server_SpawnCharacter")]
    public static class BladeAngleSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance.PlayerInput == null) return;
            var cfg = StickAngleRefs.Cfg;

            // Save vanilla limits before any modification so /reload can restore them
            StickAngleRefs.SaveOriginals(__instance);

            if (cfg.FreeBladeEnabled)
            {
                StickAngleRefs.minBladeRef(__instance.PlayerInput) = -127;
                StickAngleRefs.maxBladeRef(__instance.PlayerInput) = 127;
            }

            if (cfg.HighStickingEnabled)
            {
                Vector2 min = StickAngleRefs.minStickAngleRef(__instance.PlayerInput);
                min.x = cfg.HighStickingMaxAngle;
                StickAngleRefs.minStickAngleRef(__instance.PlayerInput) = min;
            }
        }
    }

    // ── 2. Bypass server blade-angle clamp in the RPC handler ───────────────

    [HarmonyPatch(typeof(PlayerInput), "Server_BladeAngleInputRpc")]
    public static class BladeAngleRpcPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerInput __instance, sbyte value)
        {
            if (StickAngleRefs.Cfg.FreeBladeEnabled)
                __instance.BladeAngleInput.ServerValue = value;
        }
    }

    // ── 3. Dynamic high-sticking: only expand range at high angles ──────────

    [HarmonyPatch(typeof(StickPositioner), "FixedUpdate")]
    public static class HighStickingFixedUpdatePatch
    {
        private static readonly AccessTools.FieldRef<StickPositioner, GameObject> raycastOriginRef =
            AccessTools.FieldRefAccess<StickPositioner, GameObject>("raycastOrigin");

        [HarmonyPrefix]
        public static void Prefix(StickPositioner __instance)
        {
            var cfg = StickAngleRefs.Cfg;
            if (!cfg.HighStickingEnabled) return;
            if (__instance.Player == null || __instance.Player.PlayerInput == null) return;

            var input = __instance.Player.PlayerInput;

            // Read current vertical angle of the raycast origin
            GameObject origin = raycastOriginRef(__instance);
            if (origin == null) return;

            float currentAngle = origin.transform.localEulerAngles.x;
            // Normalize to signed range [-180, 180]
            if (currentAngle > 180f) currentAngle -= 360f;

            // Activation check: only expand range when angle reaches the threshold.
            // ActivateAngle and MaxAngle are negative (e.g. -20 and -80).
            // More negative = higher stick position.
            Vector2 min = StickAngleRefs.minStickAngleRef(input);
            if (currentAngle <= cfg.HighStickingActivateAngle)
                min.x = cfg.HighStickingMaxAngle;
            else
                min.x = -25f; // vanilla default
            StickAngleRefs.minStickAngleRef(input) = min;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Client-side patches — applied via HarmonyPatchHelper for "CompetitiveCompanion"
// ═══════════════════════════════════════════════════════════════════════════
namespace CompetitiveCompanion
{
    using CompetitivePuckTweaks.src;

    // ── 4. Apply blade-angle limits on client after spawn ───────────────────

    // Hooked on PlayerInput.OnNetworkSpawn rather than PlayerBody.OnNetworkPostSpawn.
    // In b1117 the body's Player reference is resolved via HandlePlayerReference from
    // the replicated PlayerReference, which is not guaranteed resolvable at
    // OnNetworkPostSpawn (it needs the Player NetworkObject to have already spawned on
    // this client). When it was null the old hook silently skipped and the blade stayed
    // clamped to the vanilla +/-4 limit (the reported "freeblade won't rotate"). On
    // PlayerInput, Player is set in Awake via GetComponent, so it is always available
    // here. The config-timing case is still covered by RefreshFreeBladeForAllPlayers on
    // config-sync receive.
    [HarmonyPatch(typeof(PlayerInput), "OnNetworkSpawn")]
    public static class ClientBladeAngleSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerInput __instance)
        {
            var player = __instance.Player;
            if (player == null) return;

            // Save vanilla limits before modification so config sync can restore them
            StickAngleRefs.SaveOriginals(player);

            var cfg = DashFallMod.ConfigManager.CompAdjustEffective;
            if (!cfg.FreeBladeEnabled) return;

            StickAngleRefs.minBladeRef(__instance) = -127;
            StickAngleRefs.maxBladeRef(__instance) = 127;
        }
    }

    // ── 5/6. Blade angle UP and DOWN under the client spin lock ─────────────
    //
    // These replace vanilla's OnBladeAngle{Up,Down}ActionPerformed, which are:
    //
    //     bladeAngleBuffer += context.ReadValue<float>();          // '-=' going down
    //     bladeAngleBuffer = Mathf.Clamp(bladeAngleBuffer, minimumBladeAngle, maximumBladeAngle);
    //     BladeAngleInput.ClientValue = (sbyte)bladeAngleBuffer;
    //
    // The only difference is which bounds the clamp uses: the mod substitutes the player's
    // own range for the game's fields, which FreeBlade has already widened to +/-127.
    // Nothing here wraps, despite what these section headers said for a long time.
    //
    // THE "BLADE LOCKS AT MAX" BUG. The lock's bounds defaulted to -4/+4, which is exactly
    // vanilla's own minimumBladeAngle/maximumBladeAngle, so with the lock on by default the
    // client handed back precisely the limit FreeBlade had just removed. The blade climbed
    // to 4 and stopped, and nothing in the UI suggested the client was the one stopping it.
    // The default is now the full range (see FreeBladeSpinRange), existing configs carrying
    // the old pair are widened on load, and the bounds are ordered here as well so no config
    // and no future editor can collapse the range and freeze the blade.

    internal static class BladeSpinLock
    {
        /// <summary>
        /// Shared body for both directions. <paramref name="delta"/> is the already-signed
        /// step. Returns true to fall through to vanilla.
        /// </summary>
        internal static bool Apply(PlayerInput input, float delta)
        {
            var cfg = DashFallMod.ConfigManager.CompAdjustEffective;
            if (!cfg.FreeBladeEnabled) return true;

            if (GlobalStateManager.UIState.IsMouseRequired) return false;
            if (input.Player?.Stick == null) return false;

            var clientCfg = DashFallMod.Client.DashFallConfigLoader.ClientConfig;
            if (clientCfg == null || !clientCfg.FreeBladeSpinLockEnabled) return true;

            // Ordered rather than trusted. Mathf.Clamp(v, min, max) with min > max does not
            // throw or return v: it pins to min when v < min and otherwise to max, so a
            // crossed range makes the blade sit on one bound or flip between the two, which
            // is the same "stuck" symptom from a different cause. The range slider in the
            // settings UI cannot produce a crossed pair, but a hand-edited file can.
            float lo = Mathf.Min(clientCfg.FreeBladeSpinMin, clientCfg.FreeBladeSpinMax);
            float hi = Mathf.Max(clientCfg.FreeBladeSpinMin, clientCfg.FreeBladeSpinMax);

            // Never ask for an angle the game itself would reject. FreeBlade widens these to
            // +/-127; intersecting keeps the client honest if that ever stops being true, and
            // guarantees the cast below stays inside sbyte.
            lo = Mathf.Max(lo, StickAngleRefs.minBladeRef(input));
            hi = Mathf.Min(hi, StickAngleRefs.maxBladeRef(input));
            if (lo > hi) return true;   // no usable range at all, let vanilla have it

            float buf = StickAngleRefs.bladeAngleBufferRef(input);
            buf = Mathf.Clamp(buf + delta, lo, hi);

            StickAngleRefs.bladeAngleBufferRef(input) = buf;
            input.BladeAngleInput.ClientValue = (sbyte)buf;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "OnBladeAngleUpActionPerformed")]
    public static class BladeAngleUpSpinPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerInput __instance, InputAction.CallbackContext context)
            => BladeSpinLock.Apply(__instance, context.ReadValue<float>());
    }

    [HarmonyPatch(typeof(PlayerInput), "OnBladeAngleDownActionPerformed")]
    public static class BladeAngleDownSpinPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerInput __instance, InputAction.CallbackContext context)
            => BladeSpinLock.Apply(__instance, -context.ReadValue<float>());
    }
}
