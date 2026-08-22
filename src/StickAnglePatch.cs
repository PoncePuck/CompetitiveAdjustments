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
    // The difference is what bounds the buffer. With the lock ON the mod substitutes the
    // player's own range for the game's fields, which FreeBlade has already widened to
    // +/-127. With the lock OFF the buffer WRAPS instead of clamping, which is the only
    // form of genuinely uncapped spin the game's own types allow: see BladeSpinWrap.
    //
    // Neither of those bounds the SPEED, which is what a free spinning mouse wheel
    // exploits, so the movement also goes through the server's spin limiter on its way
    // into the buffer: see src/StickSpinFatigue.cs. That is a no-op for a blade nobody
    // has spun a full revolution recently, which includes every blade turned with a
    // notched wheel.
    //
    // THE "BLADE LOCKS AT MAX" REPORT. The lock ships ON with bounds of -4/+4, which is
    // exactly vanilla's own minimumBladeAngle/maximumBladeAngle, so at stock settings the
    // client hands back precisely the limit FreeBlade had just removed and the blade climbs
    // to 4 and stops. That is the reported symptom, and it is the INTENDED DEFAULT: a stock
    // client plays like vanilla even against a server running FreeBlade, and free spin is
    // something the player turns on, either by switching the lock off or by widening the
    // range. What was wrong originally was not the behaviour but that nothing named it; the
    // toggle beside it now says so, as do the config comment and the README.
    //
    // It went the other way for one build. de073ff made the lock opt-in and added a
    // ClientConfigVersion 1 migration that switched it off for files sitting at the old
    // default; fafe474 reversed both, since a migration that disables the intended default
    // would fight it on every launch. There is no live migration now. Do not reintroduce one
    // here without reading MigrateClientConfig first.
    //
    // The bounds are ordered here as well, so no hand-edited file can collapse the range and
    // freeze the blade a different way.

    /// <summary>
    /// The rollover used by uncapped spin, in the same sbyte units BladeAngleInput carries.
    /// </summary>
    /// <remarks>
    /// There is no such thing as an unbounded blade angle on the wire. BladeAngleInput is a
    /// NetworkedInput&lt;sbyte&gt;, so every value the client can send fits in -128..127, and
    /// FreeBlade widening minimumBladeAngle/maximumBladeAngle to +/-127 is already the widest
    /// clamp the type allows. That is why "uncapped" still stopped: at Stick.bladeAngleStep of
    /// 12.5 degrees, 127 steps is 1587.5 degrees, four and a bit turns, and then the blade sits
    /// there.
    ///
    /// What rescues it is that the angle is only ever consumed modulo a turn. Stick.FixedUpdate
    /// does Quaternion.AngleAxis(value * bladeAngleStep, forward) and UIMinimap subtracts
    /// Stick.BladeAngle from a UI rotation; nothing integrates it or compares it against a
    /// threshold. So a value that rolls over a whole number of turns is not just close to the
    /// one before it, it is the same pose, and the spin can carry on forever inside an sbyte.
    ///
    /// The rollover has to land on a whole turn or the seam is visible. At 12.5 degrees a turn
    /// is 28.8 steps, so the first step count that closes the circle is 144 (1800 degrees, five
    /// turns), which still fits inside the 256 values an sbyte holds. Rolling over at +/-127
    /// instead would jump the blade by 52.5 degrees every time.
    /// </remarks>
    internal static class BladeSpinWrap
    {
        // 144 steps at the stock 12.5 degrees. Used only when the real step cannot be read.
        private const int FallbackPeriod = 144;

        // The widest rollover an sbyte holds, for a step that never closes the circle.
        private const int WidestPeriod = 255;

        private static int _period;

        /// <summary>
        /// Step count that adds up to a whole number of turns, so rolling the buffer over it
        /// changes nothing on screen. Resolved once from a live Stick, because bladeAngleStep
        /// is a serialized field and the prefab, not the source default, has the last word.
        /// </summary>
        internal static int Period(Stick stick)
        {
            if (_period != 0) return _period;

            float step = ReadStep(stick);
            _period = FindWholeTurnPeriod(step);
            if (_period == 0)
            {
                // No step count under 256 lands on a whole turn, so no seamless rollover
                // exists. Take the widest span the wire allows and let it jump.
                Debug.LogWarning($"[COMPADJUST] Blade angle step {step} closes no whole turn " +
                                 $"inside an sbyte; uncapped spin will jump at the rollover.");
                _period = WidestPeriod;
            }
            return _period;
        }

        /// <summary>Fold <paramref name="value"/> into [-period/2, +period/2).</summary>
        internal static float Fold(float value, int period)
        {
            float half = period * 0.5f;
            return Mathf.Repeat(value + half, period) - half;
        }

        // Shared with the spin limiter, which needs the same number for the opposite
        // reason: a whole turn of rollover here, 360/step units per revolution there.
        // Returns 0 when the field cannot be read, which FindWholeTurnPeriod answers with
        // the stock period.
        private static float ReadStep(Stick stick)
            => CompetitiveAdjustments.StickSpinFatigue.BladeStepDegrees(stick);

        /// <summary>
        /// Smallest n in 1..255 where n steps is a whole number of turns, or 0 if there is none.
        /// </summary>
        private static int FindWholeTurnPeriod(float step)
        {
            step = Mathf.Abs(step);
            if (step < 0.0001f) return FallbackPeriod;   // unreadable step, assume the stock one

            for (int n = 1; n <= WidestPeriod; n++)
            {
                float turns = Mathf.Round(n * step / 360f);
                if (turns >= 1f && Mathf.Abs(n * step - turns * 360f) < 0.01f) return n;
            }
            return 0;
        }
    }

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
            Stick stick = input.Player?.Stick;
            if (stick == null) return false;

            var clientCfg = DashFallMod.Client.DashFallConfigLoader.ClientConfig;
            if (clientCfg == null) return true;   // nothing to read a range out of

            float start = StickAngleRefs.bladeAngleBufferRef(input);
            float moved = delta;
            float correction = 0f;      // catch-up back into a range the buffer starts outside of
            // Which pair applies is decided by the role of the player this input belongs to, not
            // by a cached "am I a goalie" on the runner. Read straight off Player.Role so a
            // position change mid-shift takes effect on the next input rather than on the next
            // role poll, and so the two settings can never be applied to the wrong position.
            bool goalie = input.Player != null && input.Player.Role == PlayerRole.Goalie;
            clientCfg.GetFreeBlade(goalie, out bool locked, out float cfgMin, out float cfgMax);

            bool wraps = !locked;

            // Lock off means uncapped, and uncapped has to wrap. Falling through to vanilla
            // here was still a cap: vanilla clamps against the game's own
            // minimumBladeAngle/maximumBladeAngle, which FreeBlade had set to +/-127, so the
            // blade stopped dead after about four and a half turns. See BladeSpinWrap for why
            // rolling over is invisible. The fold happens at the end, on the position, once
            // the movement has been decided.
            if (!wraps)
            {
                // Ordered rather than trusted. Mathf.Clamp(v, min, max) with min > max does not
                // throw or return v: it pins to min when v < min and otherwise to max, so a
                // crossed range makes the blade sit on one bound or flip between the two, which
                // is the same "stuck" symptom from a different cause. The range slider in the
                // settings UI cannot produce a crossed pair, but a hand-edited file can.
                float lo = Mathf.Min(cfgMin, cfgMax);
                float hi = Mathf.Max(cfgMin, cfgMax);

                // Never ask for an angle the game itself would reject. FreeBlade widens these to
                // +/-127; intersecting keeps the client honest if that ever stops being true, and
                // guarantees the cast below stays inside sbyte.
                lo = Mathf.Max(lo, StickAngleRefs.minBladeRef(input));
                hi = Mathf.Min(hi, StickAngleRefs.maxBladeRef(input));
                if (lo > hi) return true;   // no usable range at all, let vanilla have it

                // As a movement, not a position, and before fatigue sees it: an angle the
                // range refuses must not be counted as spin, or a player parked against
                // either bound would be fatigued for a blade that is not turning.
                //
                // Two different things happen here and only one of them is spin. The buffer
                // can START outside [lo, hi]: switching this lock back ON after spinning with
                // it off leaves the buffer anywhere in the wrap period, and dragging the range
                // slider in under the current angle does the same. Clamping then produces a
                // "movement" of hundreds of degrees that nobody asked for, and booking that
                // against the limiter bought four stacks off a single scroll click and left
                // the blade crawling back into range for the better part of ten seconds.
                //
                // So the catch-up is split out and applied whole, the way it was before the
                // limiter existed, and only the part of the step that happens INSIDE the range
                // is offered to the limiter. A buffer already in range gives correction 0 and
                // this is exactly the old line; a player parked ON a bound still contributes
                // nothing, which was the original point.
                float from = Mathf.Clamp(start, lo, hi);
                correction = from - start;
                moved = Mathf.Clamp(start + delta, lo, hi) - from;
            }

            // Returns `moved` untouched unless this player has spun a revolution recently
            // enough to have earned a speed limit. See src/StickSpinFatigue.cs.
            moved = CompetitiveAdjustments.StickSpinFatigue.ThrottleLocalInput(input, moved, cfg);

            float buf = start + correction + moved;
            if (wraps) buf = BladeSpinWrap.Fold(buf, BladeSpinWrap.Period(stick));

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
