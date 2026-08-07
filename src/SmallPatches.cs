using System;
using AYellowpaper.SerializedCollections;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace CompetitiveAdjustments
{
    public static class SharedConstants
    {
        public const string MOD_NAME = "COMPADJUST";
        public const string COMPANION_VERSION = "0.4c";
        public const string TWEAKS_VERSION = "0.6a-b45";

        /// <summary>
        /// Monotonic build number, the one thing two builds can compare without agreeing on
        /// anything else. BUMP ON EVERY WORKSHOP RELEASE.
        ///
        /// Deliberately an int and not the version strings above. The Ruleset mod solves the
        /// same problem by carrying a hardcoded list of every version it has ever shipped and
        /// asking whether the peer's string is in it; that works, but it is a list somebody
        /// has to remember to append to, and a version that never made the list reads as
        /// current. A number only has to be bigger.
        /// </summary>
        public const int MOD_BUILD = 3;

        /// <summary>
        /// The oldest client build this server will not complain about.
        ///
        /// Raise this ONLY when a change actually breaks older clients, never merely because
        /// a new build shipped. If it tracked MOD_BUILD, every client would be scolded for
        /// the few hours between the server updating and the Workshop item reaching them,
        /// which is the fastest way to teach people to ignore the warning.
        ///
        /// 2 is the first build that sends PPKB/ClientVersion at all. Anything older cannot
        /// report, and is detected by staying silent instead. See ClientVersionCheck.
        ///
        /// Build 3 (stick spin fatigue) deliberately did NOT raise this. It carries its one
        /// new synced flag in a spare bit of the existing ConfigSyncPackage.BoolFlags, so the
        /// wire size is unchanged and a build 2 client reads a correct 0 for it rather than
        /// misparsing anything. Nothing about build 2 is broken against a build 3 server, so
        /// scolding it would be the exact false positive the paragraph above warns about.
        ///
        /// While this equals MOD_BUILD's predecessor rather than MOD_BUILD, the reported-but-
        /// too-old branch of ClientVersionCheck stays dormant: no shipped client can report a
        /// build below it. The popup that branch raises is previewable from the debug section
        /// of the SETTINGS tab so it does not go untested. See ForceShowServerRejectedForTest.
        /// </summary>
        public const int MIN_SUPPORTED_CLIENT_BUILD = 2;
    }
}

namespace DashFallMod
{
    public static class ConfigManager
    {
        // Default shortcut now returns the effective Dashfall config.  When
        // EnableDashfall is off this is the all-features-disabled sentinel,
        // so every consumer that does `cfg.SkaterDiveEnabled` etc. naturally
        // sees false without needing its own master check.  UI display code
        // that wants to show the user's saved intent should read
        // ConfigRaw below.
        public static CompetitiveAdjustments.DashfallConfig Config =>
            CompetitiveAdjustments.ConfigManager.DashfallEffective;

        public static CompetitiveAdjustments.DashfallConfig ConfigRaw =>
            CompetitiveAdjustments.ConfigManager.Config.Dashfall;

        public static CompetitiveAdjustments.CompAdjustConfig CompAdjust =>
            CompetitiveAdjustments.ConfigManager.Config.CompAdjust;

        // Use this anywhere a feature should be silenced when the top-level
        // EnableCompAdjust master flag is off.  UI display code that wants to
        // show the user's saved intent should keep reading CompAdjust above.
        public static CompetitiveAdjustments.CompAdjustConfig CompAdjustEffective =>
            CompetitiveAdjustments.ConfigManager.CompAdjustEffective;

        public static void EnsureConfig() =>
            CompetitiveAdjustments.ConfigManager.EnsureConfig();

        public static void ReloadConfig() =>
            CompetitiveAdjustments.ConfigManager.ReloadConfig();

        public static void Log(string msg) =>
            CompetitiveAdjustments.ConfigManager.Log(msg);

        public static void Dbg(string msg) =>
            CompetitiveAdjustments.ConfigManager.Dbg(msg);
    }

    /// <summary>
    /// Widens the goalie butterfly leg pad by ButterflyPadOffset.
    ///
    /// Deliberately lives in DashFallMod rather than CompetitiveCompanion.  The
    /// companion is only constructed when the process is not headless, so a patch
    /// registered there never applies on a dedicated server: every client would
    /// move the marker the pad collider follows while the server kept the vanilla
    /// one, and the server is the side that resolves puck/pad contacts.  Shots
    /// that visibly hit the pad would go in.  The DashFallMod namespace is patched
    /// in every role exactly once.
    /// </summary>
    [HarmonyPatch(typeof(PlayerLegPad), "Awake")]
    public class PlayerLegPadPatch
    {
        // Vanilla localPosition of every Butterfly marker we have touched, kept
        // alongside the marker so the offset always applies to an untouched
        // baseline.  The previous version did `localPosition += offset`, which
        // compounds the moment it runs twice on the same marker.
        private static readonly System.Collections.Generic.List<Entry> _entries =
            new System.Collections.Generic.List<Entry>();

        private struct Entry
        {
            public Transform Marker;
            public Vector3 BasePos;
        }

        private static bool _loggedButterflyNotFound;

        // One source for both roles.  On a server this is the operator's value;
        // on a client it is what the server synced, because ReceiveMessage mirrors
        // LegPadOffset into this same field.  Both sides therefore land on the
        // same number and the collider matches what the player sees.
        private static float Offset
        {
            get
            {
                var ct = CompetitiveAdjustments.ConfigManager.CompTweaksEffective;
                return ct != null ? ct.ButterflyPadOffset : 0f;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerLegPad __instance, ref SerializedDictionary<PlayerLegPadState, Transform> ___positions)
        {
            if (___positions == null || !___positions.ContainsKey(PlayerLegPadState.Butterfly))
            {
                if (!_loggedButterflyNotFound)
                {
                    CompetitiveAdjustments.ConfigManager.Log("Leg pad butterfly position NOT found.");
                    _loggedButterflyNotFound = true;
                }
                return;
            }

            Transform marker = ___positions[PlayerLegPadState.Butterfly];
            if (marker == null) return;

            Vector3 basePos = Track(marker);
            Apply(marker, basePos, Offset);
        }

        private static Vector3 Track(Transform marker)
        {
            // Prune as we scan.  The list otherwise only shrinks inside ReapplyAll, which
            // fires on a config sync and nothing else, while every replay spawns two fresh
            // pads per recorded goalie.  A match with many goal replays would accumulate
            // dead Transform wrappers and make every Track call walk all of them.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Marker == null)   // Unity null: the pad was destroyed
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                // Keep ReferenceEquals for the identity match.  A plain == would let two
                // destroyed wrappers compare equal and hand a fresh pad a stale baseline.
                if (ReferenceEquals(_entries[i].Marker, marker)) return _entries[i].BasePos;
            }

            var entry = new Entry { Marker = marker, BasePos = marker.localPosition };
            _entries.Add(entry);
            return entry.BasePos;
        }

        private static void Apply(Transform marker, Vector3 basePos, float offset)
        {
            // Sign by side so both pads widen outward instead of both sliding
            // the same way across the crease.
            float dir = basePos.x > 0f ? 1f : -1f;
            marker.localPosition = new Vector3(basePos.x + dir * offset, basePos.y, basePos.z);
        }

        /// <summary>
        /// Re-applies the current offset to every live marker.  A server knows its
        /// value at load, but a client only learns it when the config sync lands,
        /// which is normally after the pads have already run Awake.  Without this
        /// the client would sit at offset 0 against a server that is not, which is
        /// the same collider/visual divergence in the other direction.
        /// </summary>
        public static void ReapplyAll()
        {
            float offset = Offset;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Transform marker = _entries[i].Marker;
                if (marker == null)   // Unity null: the pad was destroyed
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                Apply(marker, _entries[i].BasePos, offset);
            }
        }
    }
}

namespace CompetitiveCompanion
{
    [HarmonyPatch(typeof(PuckManager), "AddPuck")]
    public class PuckPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PuckManager __instance, Puck puck)
        {
            puck.transform.localScale = CompetitivePuckTweaks.src.PuckPatch.GetSyncedPuckScaleVector();
            if (CompetitiveAdjustments.BallModeHelper.IsBallModeEnabled)
                CompetitiveAdjustments.BallModeHelper.TransformPuckToBall(puck);
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
    public class PuckPostSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Puck __instance)
        {
            if (__instance == null) return;

            __instance.transform.localScale = CompetitivePuckTweaks.src.PuckPatch.GetSyncedPuckScaleVector();

            if (CompetitiveAdjustments.BallModeHelper.IsBallModeEnabled)
                CompetitiveAdjustments.BallModeHelper.TransformPuckToBall(__instance);

            // Client timing guard: if spawn happened before receiving CPT_sync_config,
            // ask the server for a fresh sync and let ReceiveMessage re-apply to all pucks.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
                PluginCore.RequestConfigSyncFromServer("Companion.Puck.OnNetworkPostSpawn");
        }
    }

}

namespace CompetitiveCompanion.src
{
    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    public class StickPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance, ref GameObject ___shaftHandle)
        {
            if (__instance == null)
            {
                Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] Stick null on network post spawn");
                return;
            }

            StickMesh newStickMesh = __instance.gameObject.GetComponentInChildren<StickMesh>();
            if (newStickMesh == null)
            {
                Debug.LogError($"[{CompetitiveAdjustments.SharedConstants.MOD_NAME}] StickMesh is null!");
            }
        }
    }

}

namespace CompetitivePuckTweaks.src
{
    public class FloatComponent : MonoBehaviour
    {
        public float value { get; set; } = 0f;
    }

    // b1117 GoalController is a plain MonoBehaviour with no OnNetworkSpawn (b897
    // had one); patching the missing method threw at patch time and silently
    // dropped the goal-post bounciness tweak. Retarget to Awake, which sets the
    // private `goal` field on its first line, so ___goal is valid in this postfix.
    [HarmonyPatch(typeof(GoalController), "Awake")]
    public class GoalControllerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GoalController __instance, ref Goal ___goal)
        {
            if (___goal == null) return;

            Transform postCollider = null;
            for (int i = 0; i < ___goal.transform.childCount; i++)
            {
                Transform child = ___goal.transform.GetChild(i);
                if (child.name.Contains("Goal Post Collider"))
                {
                    postCollider = child;
                }
            }

            if (postCollider == null)
            {
                PluginCore.Log("Post collider not found.");
                return;
            }

            foreach (CapsuleCollider col in postCollider.GetComponents<CapsuleCollider>())
            {
                col.material.bounciness = PluginCore.config.postBounciness;
            }
        }
    }

    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.Server_SpawnPucksForPhase))]
    public class PuckManagerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PuckManager __instance, GamePhase phase) {
            try {
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                    return;

                // Re-assert tuned puck physics for every phase. Pucks recycled
                // across a phase transition (warmup -> play) otherwise keep vanilla
                // mass/drag and fall out of PuckIDs, which is why the feel is right
                // in a fresh warmup but "breaks" once a game is played.
                foreach (Puck puck in __instance.GetPucks())
                    PuckPatch.ApplyPuckPhysics(puck);

                // Scale warm-up puck spawns with the arena sizing, matching the player
                // spawn scaling (same GoalNetTweaks helper). Warm-up pucks are placed
                // at fixed scene markers (vanilla coordinates) spread across the rink,
                // so on a shrunk rink they'd otherwise sit outside the boards. Other
                // phases drop the puck at centre ice (world origin), where scaling is a
                // no-op, so we gate to Warm-up to leave face-off/replay pucks untouched.
                if (phase == GamePhase.Warmup) {
                    foreach (Puck puck in __instance.GetPucks()) {
                        if (puck == null) continue;
                        var raw = puck.transform.position;
                        var scaled = DashFallMod.GoalNetTweaks.ScaleSpawnPositionWithArena(raw);
                        if (scaled == raw) continue;
                        puck.transform.position = scaled;
                        if (puck.Rigidbody != null) {
                            puck.Rigidbody.position = scaled;
                            puck.Rigidbody.linearVelocity = Vector3.zero;
                            puck.Rigidbody.angularVelocity = Vector3.zero;
                        }
                    }
                }

                // Random drop only overrides the Play phase; keep other phases vanilla.
                if (PluginCore.config.RandomPuckDrop && phase == GamePhase.Play) {
                    foreach (Puck puck in __instance.GetPucks())
                        puck.Rigidbody.AddForce(Vector3.down * UnityEngine.Random.Range(5.5f, 9f), ForceMode.VelocityChange);
                }
            }
            catch (Exception ex) {
                Debug.LogError($"[PuckManagerPatch] Failed in Server_SpawnPucksForPhase Postfix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(SynchronizedObjectManager), "Awake")]
    public class SyncObjMngrPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SynchronizedObjectManager __instance, ref SnapshotInterpolationSettings ___snapshotInterpolationSettings, ref bool ___skipLateTicks)
        {
            ___skipLateTicks = false;
            ___snapshotInterpolationSettings.bufferLimit = 128;
            ___snapshotInterpolationSettings.bufferTimeMultiplier = 2.5f;
        }
    }

    [HarmonyPatch(typeof(ChatManager), "Client_SendChatMessageRpc")]
    public class ChatManagerCommandPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ChatManager __instance, string content, bool isQuickChat, bool isTeamChat, RpcParams rpcParams)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return true;
            }

            ulong clientId = rpcParams.Receive.SenderClientId;
            string command = content.Trim().ToLowerInvariant();

            if (command == "/v" || command == "/version")
            {
                SendSystemMessage(clientId, CompetitiveAdjustments.SharedConstants.TWEAKS_VERSION);
                return false;
            }

            if (!PluginCore.config.OpenConfigChanges && !IsAdmin(clientId))
            {
                if (command == "/reload" || command == "/resetserver" || command == "/forcesync" || command == "/fs" || command == "/killserver")
                {
                    SendSystemMessage(clientId, "<color=#ff9900>No permission for server config commands.</color>");
                    return false;
                }
                return true;
            }

            switch (command)
            {
                case "/resetserver":
                    if (GameManager.Instance == null)
                    {
                        SendSystemMessage(clientId, "<color=#ff9900>No active game to reset.</color>");
                        return false;
                    }
                    GameManager.Instance.Server_SetGameState(
                        phase: GamePhase.Warmup,
                        tick: 0,
                        period: 1,
                        blueScore: 0,
                        redScore: 0,
                        isOvertime: false);
                    SendSystemMessage(clientId, "Server reset.");
                    return false;

                case "/killserver":
                    Application.Quit();
                    return false;

                case "/forcesync":
                case "/fs":
                {
                    var players = PlayerManager.Instance?.GetPlayers();
                    if (players != null)
                    {
                        foreach (Player player in players)
                            PluginCore.ManualSync(player.OwnerClientId);
                    }

                    SendSystemMessage(clientId, "Config synced to all clients.");
                    return false;
                }

                case "/reload":
                    ReloadServerConfig(clientId);
                    return false;
            }

            return true;
        }

        // Shared with the admin config editor's auth path.
        private static bool IsAdmin(ulong clientId) =>
            CompetitiveAdjustments.AdminAuth.IsAdmin(clientId);

        private static void SendSystemMessage(ulong clientId, string message)
        {
            var chatMgr = NetworkBehaviourSingleton<ChatManager>.Instance;
            if (chatMgr != null)
            {
                chatMgr.Server_SendChatMessage(message, "#b8b8b8", new ulong[] { clientId });
            }
        }

        private static void ReloadServerConfig(ulong clientId)
        {
            try
            {
                CompetitiveAdjustments.ConfigManager.EnsureConfig();
                CompetitiveAdjustments.ConfigManager.ReloadConfig();

                // Shared tail: apply live, re-broadcast every sync channel, and
                // push the full credential-free config to all clients.
                CompetitiveAdjustments.ConfigApplyService.ApplyAndBroadcast();

                SendSystemMessage(clientId, "<color=#00ff00>Config reloaded successfully.</color>");
                CompetitiveAdjustments.ConfigManager.Log("Config reloaded via /reload command.");
            }
            catch (Exception ex)
            {
                SendSystemMessage(clientId, $"<color=#ff0000>Config reload failed: {ex.Message}</color>");
            }
        }
    }

    [HarmonyPatch(typeof(VelocityLean), "Awake")]
    public class VelocityLeanPatch
    {
        [HarmonyPostfix]
        public static void Postfix(VelocityLean __instance, ref float ___angularForceMultiplier)
        {
            ___angularForceMultiplier = PluginCore.config.AngularForceMultiplier;
        }
    }

}