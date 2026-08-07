// StickSpinFatigue.cs
// A speed limit on the blade, earned by spinning it.
//
// A mouse wheel built on a bearing rather than a notched detent emits scroll events
// far faster than a hand can click a normal wheel, and with the client spin lock off
// the blade buffer wraps instead of stopping (BladeSpinWrap in StickAnglePatch.cs).
// The two together are a blade that can be flicked into a permanent full speed spin
// that nothing in the game damps. This is that damping: turn the blade a whole
// revolution in one direction inside the trigger window and its angular speed is
// capped for a second, deeper each time the player earns it again, clearing once
// they stop.
//
// The same state machine runs in two places, on separate state:
//
//   client  BladeSpinLock.Apply, on the owner's own input, so the slowdown is felt
//           as the wheel is spun rather than a round trip later. This is the half
//           that decides what everyone sees, because the blade pose comes from
//           BladeAngleInput.ServerValue, which is the owner's value relayed back.
//   server  the Client_BladeAngleInputRpc prefix below, on the value the owner
//           actually sent, so a client that has had the limiter removed gains
//           nothing by it. It runs a deliberately looser cap (ServerHeadroom): an
//           honest client has already throttled itself with the same numbers, so
//           the server should never be the one clipping, and the headroom is there
//           to absorb clock jitter between the two rather than to grant speed.
//
// A listen host runs BOTH halves for its own player, which is why the two keep
// separate state rather than sharing one entry per PlayerInput.
//
// Everything is measured in DEGREES OF BLADE ROTATION, never in wheel notches, so
// the binding's own scale factor is irrelevant: one unit of BladeAngleInput is
// Stick.bladeAngleStep degrees, 12.5 on the stock prefab, which puts a revolution
// at 28.8 units. A notched wheel cannot reach the default trigger (a revolution
// inside half a second is 57.6 units per second, sustained), so this is inert for
// anyone playing on one.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CompetitiveAdjustments
{
    public static class StickSpinFatigue
    {
        // Stock Stick.bladeAngleStep, used only when the live field cannot be read.
        private const float FallbackStepDegrees = 12.5f;

        // How much of the current cap may be banked while nothing is scrolling, as
        // seconds of it. Input arrives in bunches: the clock does not move between two
        // scroll events in one frame, and the server can take several of one client's
        // messages in a single network update. With no bucket at all the first event of
        // a bunch would be handed everything and the rest nothing, which reads as a
        // stutter rather than a slowdown. A small bucket smooths that out without
        // raising the sustained rate.
        private const float BurstSeconds = 0.15f;

        // The server's cap as a multiple of the client's.  See the file header.
        private const float ServerHeadroom = 1.5f;

        // How long the server's reference angle may go unrefreshed before it is treated as
        // meaningless rather than merely old.
        //
        // Deliberately much longer than the trigger window. The reference has to be dropped
        // for gaps where this half was not running at all (the enforcement toggle flipped, a
        // role change, a fresh player), but every drop is a message relayed without being
        // measured, and at one window's spacing that was a bypass: a client that sent one
        // blade value every 0.51 s had every one of them adopted whole, so the limiter never
        // engaged and the blade moved in half-turn steps rather than at the cap. Three
        // seconds bounds that to a step every three seconds, which is slower than the cap
        // allows anyway, while still being far shorter than any real gap in enforcement.
        //
        // A genuine pause in play does not need this: Throttle already abandons the
        // revolution in progress once the gap exceeds the window, and the token bucket only
        // banks BurstSeconds of movement however long the player waits.
        private const float ReferenceStaleSeconds = 3f;

        private sealed class SpinState
        {
            // Doubles, because these are absolute times and a dedicated server stays up
            // for days: a float clock past about a day quantises to more than the gaps
            // being measured here.
            public double LastInputAt;
            public double TurnOpenedAt;    // when the current revolution started accumulating
            public double SlowUntil;

            public int Direction;          // +1 / -1, the way the current revolution is going
            public float TurnDegrees;      // degrees turned that way since TurnOpenedAt
            public int Stacks;
            public float Budget;           // degrees banked at the current cap

            // Server only, and deliberately TWO numbers.
            //
            // LastRequestedValue is the last angle the client asked for, and per message
            // movement is measured against it. LastSentValue is the last angle actually
            // relayed, and the next one is built from it. They hold the same number until
            // the cap bites and different ones afterwards, which is why measuring against
            // the relayed value was wrong: the measured delta grew with the lag instead of
            // tracking the client, and the moment the lag passed half a wrap period the fold
            // read a forward spin as a large backward one. From there the limiter drove the
            // relayed angle on phantom reversals and it stopped meaning anything at all.
            //
            // BladeAngleInput.ServerValue cannot stand in for either, because the server's
            // own copy is written by a DeferLocal RPC a tick later.
            public float LastRequestedValue;
            public float LastSentValue;
            public bool HasLastSentValue;
        }

        private static readonly Dictionary<int, SpinState> _clientStates = new Dictionary<int, SpinState>();
        private static readonly Dictionary<int, SpinState> _serverStates = new Dictionary<int, SpinState>();

        private static SpinState StateFor(Dictionary<int, SpinState> states, int key)
        {
            SpinState state;
            if (!states.TryGetValue(key, out state))
            {
                state = new SpinState();
                states[key] = state;
            }
            return state;
        }

        /// <summary>Drops both halves of a player's state.  Called from the despawn patch below.</summary>
        public static void Forget(PlayerInput input)
        {
            if (input == null) return;
            int key = input.GetInstanceID();
            _clientStates.Remove(key);
            _serverStates.Remove(key);
        }

        // ── Blade step ──────────────────────────────────────────────────────────

        private static AccessTools.FieldRef<Stick, float> _stepRef;
        private static bool _stepRefResolved;

        /// <summary>
        /// Live Stick.bladeAngleStep in degrees per BladeAngleInput unit, or 0 when it
        /// cannot be read.  It is a serialized field, so the prefab and not the source
        /// default has the last word, and both this and BladeSpinWrap need the same
        /// number: a revolution is 360/step units for one of them and a whole turn of
        /// rollover for the other.
        /// </summary>
        internal static float BladeStepDegrees(Stick stick)
        {
            if (!_stepRefResolved)
            {
                _stepRefResolved = true;
                // Lazily, and swallowing the failure, because this runs off an input action:
                // a throw in a static initialiser here would take the blade keys with it, and
                // the fallback is a perfectly good answer for the stock prefab.
                try { _stepRef = AccessTools.FieldRefAccess<Stick, float>("bladeAngleStep"); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[COMPADJUST] Could not read Stick.bladeAngleStep ({ex.Message}); " +
                                     $"assuming the stock {FallbackStepDegrees} degrees per step.");
                }
            }
            if (_stepRef == null || stick == null) return 0f;
            try { return _stepRef(stick); }
            catch { return 0f; }
        }

        // ── Config, sanitised at the point of use ───────────────────────────────
        //
        // Not in ClampRuntimeLimits, which only guards the two knobs that can brick a
        // session, and not on load either: the SERVER tab writes these as free numeric
        // fields and a hand-edited file never passes through the editor at all.  The
        // revolution and the window are the two that matter, since the trigger loop
        // below subtracts one from an accumulator and divides time by the other.

        private static float TurnDegrees(CompAdjustConfig c) => Mathf.Max(1f, c.StickSpinFatigueRotationDegrees);
        private static float Window(CompAdjustConfig c) => Mathf.Max(0.01f, c.StickSpinFatigueWindowSeconds);
        private static float SlowSeconds(CompAdjustConfig c) => Mathf.Max(0.05f, c.StickSpinFatigueSlowSeconds);
        private static float Limit(CompAdjustConfig c) => Mathf.Max(1f, c.StickSpinFatigueLimitDegreesPerSecond);
        private static float StackFactor(CompAdjustConfig c) => Mathf.Clamp(c.StickSpinFatigueStackFactor, 0.05f, 1f);
        private static int MaxStacks(CompAdjustConfig c) => Mathf.Clamp(c.StickSpinFatigueMaxStacks, 1, 20);
        private static float Recovery(CompAdjustConfig c) => Mathf.Max(0f, c.StickSpinFatigueRecoverySeconds);

        // ── Entry points ────────────────────────────────────────────────────────

        /// <summary>
        /// Throttles one blade angle input step for the local player, in BladeAngleInput
        /// units, and returns how far the blade is allowed to move.  Pass the movement the
        /// blade would make AFTER the spin lock's own clamp, so an angle the range refuses
        /// never counts as spin.
        /// </summary>
        public static float ThrottleLocalInput(PlayerInput input, float deltaUnits, CompAdjustConfig cfg)
        {
            if (input == null || cfg == null || !cfg.StickSpinFatigueEnabled) return deltaUnits;

            Stick stick = input.Player != null ? input.Player.Stick : null;
            if (stick == null) return deltaUnits;

            return ThrottleUnits(StateFor(_clientStates, input.GetInstanceID()), stick, deltaUnits, cfg, 1f);
        }

        /// <summary>
        /// Throttles the blade angle a client asked the server to relay, and returns the
        /// value to relay instead.  Server side.
        /// </summary>
        public static sbyte ThrottleRelayedValue(PlayerInput input, sbyte requested, CompAdjustConfig cfg)
        {
            if (input == null || cfg == null || !cfg.StickSpinFatigueEnabled) return requested;

            Stick stick = input.Player != null ? input.Player.Stick : null;
            if (stick == null) return requested;

            SpinState state = StateFor(_serverStates, input.GetInstanceID());
            int period = CompetitiveCompanion.BladeSpinWrap.Period(stick);

            // Adopt the value outright when there is no usable reference: the first message
            // from this player, or a gap long enough that whatever we last saw cannot be
            // related to what just arrived.  That covers every stretch where this half was
            // not running (the enforcement toggle flipped, a role change, a fresh player).
            // Ageing the clock alongside it is load bearing: without that the next message
            // would find the same stale gap and adopt as well.
            //
            // See ReferenceStaleSeconds for why this threshold is not the trigger window.
            double now = Time.unscaledTimeAsDouble;
            if (!state.HasLastSentValue || now - state.LastInputAt > ReferenceStaleSeconds)
            {
                state.HasLastSentValue = true;
                state.LastRequestedValue = requested;
                state.LastSentValue = requested;
                state.LastInputAt = now;
                return requested;
            }

            // The client sends an absolute angle, so the movement is the difference from the
            // last one IT ASKED FOR, folded over the rollover BladeSpinWrap uses.  Without
            // the fold, a client spinning with its lock off hands the server a value that
            // steps from +71 to -72, and a limiter reading that literally sees 143 units of
            // sudden reverse spin.  Measured against the client's own previous value the
            // step is only ever a few units (blade input is relayed reliably, at most once
            // per tick per change), so folding to the short way round is never ambiguous.
            //
            // Measured against what we RELAYED it would not be bounded that way, because a
            // throttled relay falls behind by design and the fold has no way to tell a lag
            // of most of a turn from a spin the other way.
            float delta = CompetitiveCompanion.BladeSpinWrap.Fold(requested - state.LastRequestedValue, period);
            state.LastRequestedValue = requested;

            float allowed = ThrottleUnits(state, stick, delta, cfg, ServerHeadroom);

            // Nothing was taken away and nothing is being taken away, so stay bit exact with
            // the client rather than rebuilding its value out of a delta.  This is also the
            // one place any lag built up during a slowdown is given back, in a single step,
            // once the slowdown is over.
            //
            // Gated on the slowdown rather than on `allowed == delta` alone so that a
            // message the budget happens to cover mid slowdown does not snap the blade
            // forward and then let it fall behind again on the next one.
            bool slowed = state.Stacks > 0 && now < state.SlowUntil;
            if (allowed == delta && !slowed)
            {
                state.LastSentValue = requested;
                return requested;
            }

            // Folded, not clamped.  Clamping pinned the relayed angle at the end of the
            // sbyte range for as long as a player kept spinning into a cap, which froze the
            // blade for everyone instead of slowing it; folding lets it keep turning at the
            // capped rate for as long as the cap lasts.  The angle is only ever consumed
            // modulo a turn, so the phase this leaves behind is not observable.
            //
            // A hard reset is the cost of that: Player.ResetInputs drops the angle straight
            // to 0, and mid slowdown that jump is smeared over the slowdown rather than
            // relayed as the 0 it is.  It resolves on the first message after the slowdown
            // ends, by the branch above.
            float next = CompetitiveCompanion.BladeSpinWrap.Fold(state.LastSentValue + allowed, period);
            state.LastSentValue = next;

            // Fold's range is [-period/2, +period/2], which is inside an sbyte for every
            // period that closes a whole turn but rounds to 128 at the top of the 255 wide
            // fallback.  Clamp to what the game's own blade clamp accepts.
            return (sbyte)Mathf.Clamp(Mathf.RoundToInt(next), -127, 127);
        }

        /// <summary>
        /// Shared body.  Returns the movement allowed, in the units it was given.
        /// </summary>
        private static float ThrottleUnits(SpinState state, Stick stick, float deltaUnits,
                                           CompAdjustConfig cfg, float headroom)
        {
            if (deltaUnits == 0f || float.IsNaN(deltaUnits) || float.IsInfinity(deltaUnits)) return deltaUnits;

            float step = BladeStepDegrees(stick);
            if (step <= 0f) step = FallbackStepDegrees;

            float wanted = deltaUnits * step;
            float allowed = Throttle(state, wanted, cfg, Time.unscaledTimeAsDouble, headroom);

            // Hand the caller's own value straight back on the common path rather than one
            // that has been through a multiply and a divide, so a blade nobody is throttling
            // moves exactly as far as it did before this file existed.
            if (allowed == wanted) return deltaUnits;
            return deltaUnits * (allowed / wanted);
        }

        // ── The state machine ───────────────────────────────────────────────────

        /// <summary>
        /// Books <paramref name="degrees"/> of blade rotation against the player's spin
        /// history and returns how much of it may happen now.
        /// </summary>
        private static float Throttle(SpinState state, float degrees, CompAdjustConfig cfg, double now, float headroom)
        {
            float gap = (float)(now - state.LastInputAt);
            if (gap < 0f) gap = 0f;         // a reloaded scene can take the clock backwards
            state.LastInputAt = now;

            float window = Window(cfg);

            // Stacks clear in one go once the slowdown has been over for the recovery time,
            // rather than decaying: the point is a state the player can feel arrive and feel
            // leave, and half a stack is neither.  This is reached on the player's next input
            // rather than on a timer, which is the same thing from where they are sitting.
            if (state.Stacks > 0 && now >= state.SlowUntil + Recovery(cfg))
            {
                state.Stacks = 0;
                state.Budget = 0f;
            }

            // Rotation accounting.  Reversing abandons the revolution in progress, which is
            // what makes this a spin limiter rather than a blade speed limit: working the
            // blade back and forth is not what a bearing wheel does.  A gap longer than the
            // window abandons it too, because nothing before such a gap can be part of a
            // revolution that completes inside one.
            int direction = degrees > 0f ? 1 : -1;
            if (direction != state.Direction || gap > window)
            {
                state.Direction = direction;
                state.TurnDegrees = 0f;
                state.TurnOpenedAt = now;
            }
            state.TurnDegrees += Mathf.Abs(degrees);

            float turn = TurnDegrees(cfg);
            int maxStacks = MaxStacks(cfg);

            // Counted on what the player asked for, not on what the cap let through, so that
            // spinning into an active slowdown keeps earning stacks.  Measuring the throttled
            // movement instead would make the first stack the last one, since a capped blade
            // can no longer turn a revolution inside the window.
            //
            // The loop guard is for a single absurd step: a tampered client can ask for most
            // of five turns in one message.  Past the guard the accumulator is dropped rather
            // than spun through thousands of iterations.
            int guard = 0;
            while (state.TurnDegrees >= turn && guard++ <= maxStacks)
            {
                bool fast = (now - state.TurnOpenedAt) <= window;
                state.TurnDegrees -= turn;
                state.TurnOpenedAt = now;
                if (!fast) continue;

                state.Stacks = Mathf.Min(state.Stacks + 1, maxStacks);
                state.SlowUntil = now + SlowSeconds(cfg);
            }
            if (state.TurnDegrees >= turn) state.TurnDegrees = 0f;

            if (state.Stacks <= 0 || now >= state.SlowUntil) return degrees;

            float cap = Limit(cfg) * Mathf.Pow(StackFactor(cfg), state.Stacks - 1) * headroom;
            cap = Mathf.Max(cap, 1f);

            state.Budget = Mathf.Min(state.Budget + cap * gap, cap * BurstSeconds);
            float allowed = Mathf.Min(Mathf.Abs(degrees), state.Budget);
            state.Budget -= allowed;
            return direction * allowed;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Server-side patch, applied via HarmonyPatchHelper for "CompetitivePuckTweaks"
// ═══════════════════════════════════════════════════════════════════════════
namespace CompetitivePuckTweaks.src
{
    /// <summary>
    /// Reads Unity.Netcode's private __rpc_exec_stage.
    /// </summary>
    /// <remarks>
    /// An RPC method body is called twice on a host: once as the send, which serialises
    /// and posts the call, and once as the execution of the delivered message.  Only the
    /// second is the server acting on a client's input, so a prefix that cannot tell them
    /// apart charges a host's own player twice for every scroll event.  The enum is nested
    /// internal, so it cannot be named here and its value is resolved out of it once
    /// instead.
    /// </remarks>
    internal static class RpcExecStage
    {
        private static readonly FieldInfo _field =
            AccessTools.Field(typeof(Unity.Netcode.NetworkBehaviour), "__rpc_exec_stage");

        private static readonly int _executeValue = ResolveExecute();
        private static bool _warned;

        private static int ResolveExecute()
        {
            try
            {
                Type t = _field != null ? _field.FieldType : null;
                if (t != null && t.IsEnum)
                {
                    foreach (string name in Enum.GetNames(t))
                        if (name == "Execute") return Convert.ToInt32(Enum.Parse(t, name));
                }
            }
            catch { }
            return 1;   // Send = 0, Execute = 1 in every version this has shipped against
        }

        /// <summary>
        /// True when the body about to run is the delivered call rather than the send.
        /// False when the field cannot be read at all, which turns server enforcement off
        /// rather than risk double charging; the client side limiter is unaffected.
        /// </summary>
        internal static bool IsExecuting(Unity.Netcode.NetworkBehaviour behaviour)
        {
            if (_field == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[COMPADJUST] Could not find NetworkBehaviour.__rpc_exec_stage; " +
                                     "server-side stick spin fatigue is off this session. Clients still " +
                                     "limit themselves.");
                }
                return false;
            }

            try { return Convert.ToInt32(_field.GetValue(behaviour)) == _executeValue; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Server half of the spin limiter.  Trims the blade angle a client asked to have
    /// relayed, before the game relays it, which is the only point where the value can
    /// still be changed for everyone: Client_BladeAngleInputRpc hands it straight to
    /// Server_BladeAngleInputRpc with RpcTarget.Everyone, and by the time that body runs
    /// on the server the remote clients have already been sent their copy.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInput), "Client_BladeAngleInputRpc")]
    public static class BladeSpinFatigueRelayPatch
    {
        [HarmonyPrefix]
        public static void Prefix(PlayerInput __instance, ref sbyte value)
        {
            var cfg = StickAngleRefs.Cfg;
            if (!cfg.FreeBladeEnabled) return;              // vanilla's range cannot hold a revolution
            if (!cfg.StickSpinFatigueEnabled) return;
            if (!cfg.StickSpinFatigueServerEnforced) return;

            var nm = __instance.NetworkManager;
            if (nm == null || !nm.IsServer) return;
            if (!RpcExecStage.IsExecuting(__instance)) return;

            value = CompetitiveAdjustments.StickSpinFatigue.ThrottleRelayedValue(__instance, value, cfg);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Both roles, applied via HarmonyPatchHelper for "DashFallMod"
// ═══════════════════════════════════════════════════════════════════════════
namespace DashFallMod
{
    /// <summary>
    /// Drops a player's spin history when their input despawns.  In the DashFallMod
    /// namespace because it has to run on both roles: a client keeps the owner's half and
    /// a server keeps one half per connected player.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInput), "OnNetworkDespawn")]
    public static class StickSpinFatigueCleanupPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerInput __instance)
        {
            CompetitiveAdjustments.StickSpinFatigue.Forget(__instance);
        }
    }
}
