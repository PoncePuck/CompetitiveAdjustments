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
}

namespace CompetitiveCompanion
{
    [HarmonyPatch(typeof(PuckManager), "AddPuck")]
    public class PuckPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PuckManager __instance, Puck puck)
        {
            puck.transform.localScale = Vector3.one * PluginCore.config.PuckScale;
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

            float scale = 1f;
            if (PluginCore.config != null)
                scale = PluginCore.config.PuckScale;

            __instance.transform.localScale = Vector3.one * scale;

            if (CompetitiveAdjustments.BallModeHelper.IsBallModeEnabled)
                CompetitiveAdjustments.BallModeHelper.TransformPuckToBall(__instance);

            // Client timing guard: if spawn happened before receiving CPT_sync_config,
            // ask the server for a fresh sync and let ReceiveMessage re-apply to all pucks.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
                PluginCore.RequestConfigSyncFromServer("Companion.Puck.OnNetworkPostSpawn");
        }
    }

    [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]
    public class PlayerBodyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBodyV2 __instance, ref float ___slideTurnMultiplier,
            ref float ___stopDrag, ref float ___balanceRecoveryTime, ref PlayerMesh ___playerMesh)
        {
            if (PluginCore.torsoMesh == null) return;
            if (__instance.name.Contains("Goalie")) return;

            // ── VISUAL ONLY — never touch MeshCollider or mr.enabled ──────────────────
            // MeshCollider is owned by CompetitivePuckTweaks (server config).
            // MeshRendererHider handles local player body visibility via camera events.
            var mf = ___playerMesh.PlayerTorso.GetComponentInChildren<MeshFilter>();
            if (mf == null) return;

            var df = CompetitiveAdjustments.ConfigManager.CompAdjustEffective;
            bool showCustomVisual = PluginCore.torsoMesh != null
                && !(df?.DisableCustomTorsoVisual == true)
                && (DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowCustomTorsoMesh ?? true);

            // Always save the true original before we potentially overwrite it, so
            // RefreshPlayerTorsoStates can restore it even if this patch runs after Tweaks'.
            int mfId = mf.GetInstanceID();
            if (!CompetitivePuckTweaks.src.PluginCore.OriginalTorsoMeshes.ContainsKey(mfId))
                CompetitivePuckTweaks.src.PluginCore.OriginalTorsoMeshes[mfId] = mf.sharedMesh;

            if (showCustomVisual)
            {
                mf.sharedMesh = PluginCore.torsoMesh;
                mf.transform.localScale = new Vector3(
                    PluginCore.torsoMeshScale * (df?.CustomTorsoScaleX ?? 1f),
                    PluginCore.torsoMeshScale * (df?.CustomTorsoScaleY ?? 1f),
                    PluginCore.torsoMeshScale * (df?.CustomTorsoScaleZ ?? 1f));
                mf.transform.localPosition = Vector3.zero;
                mf.transform.localRotation = Quaternion.Euler(0, 180, 0);
            }
            // If !showCustomVisual: original mesh from game spawn is already in place — leave it.
        }
    }

    [HarmonyPatch(typeof(MeshRendererTexturer), "SetTexture")]
    public class MeshRendererTexturerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MeshRendererTexturer __instance, ref Material ___material)
        {
            if (___material == null) return;
            if (!__instance.gameObject.name.Contains("Torso") &&
                !__instance.gameObject.name.Contains("Groin")) return;

            // Skip goalies — their torso/groin materials must not be modified.
            var body = __instance.GetComponentInParent<PlayerBodyV2>();
            if (body == null || body.name.Contains("Goalie")) return;

            var dfCfg = CompetitiveAdjustments.ConfigManager.CompAdjustEffective;
            bool customActive = PluginCore.torsoMesh != null
                                && !(dfCfg?.DisableCustomTorsoVisual == true)
                                && (DashFallMod.Client.DashFallConfigLoader.ClientConfig?.ShowCustomTorsoMesh ?? true);

            if (customActive)
            {
                // Custom torso is active — keep torso and groin fully opaque so they render correctly.
                ___material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                ___material.SetOverrideTag("RenderType", "Opaque");
                Color c = ___material.color;
                c.a = 1.0f;
                ___material.color = c;
            }
            else
            {
                ___material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                ___material.SetOverrideTag("RenderType", "Transparent");
                Color color = ___material.color;
                color.a = 0.1f;
                ___material.color = color;
            }
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

    [HarmonyPatch(typeof(PlayerLegPad), "Awake")]
    public class PlayerLegPadPatch
    {
        private static bool _loggedButterflyFound;
        private static bool _loggedButterflyNotFound;

        [HarmonyPostfix]
        public static void Postfix(PlayerLegPad __instance, ref SerializedDictionary<PlayerLegPadState, Transform> ___positions)
        {
            if (___positions.ContainsKey(PlayerLegPadState.Butterfly))
            {
                if (!_loggedButterflyFound)
                {
                    PluginCore.Log("Leg pad butterfly position found");
                    _loggedButterflyFound = true;
                }
                Transform legPadPosition = ___positions[PlayerLegPadState.Butterfly];
                if (legPadPosition.localPosition.x > 0)
                {
                    legPadPosition.localPosition += new Vector3(PluginCore.config.ButterflyPadOffset, 0, 0);
                }
                else
                {
                    legPadPosition.localPosition -= new Vector3(PluginCore.config.ButterflyPadOffset, 0, 0);
                }

                ___positions[PlayerLegPadState.Butterfly] = legPadPosition;
            }
            else
            {
                if (!_loggedButterflyNotFound)
                {
                    PluginCore.Log("Leg pad butterfly position NOT found");
                    _loggedButterflyNotFound = true;
                }
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

    [HarmonyPatch(typeof(GoalController), "OnNetworkSpawn")]
    public class GoalControllerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GoalController __instance, ref Goal ___goal)
        {
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

        private static bool IsAdmin(ulong clientId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost && clientId == NetworkManager.ServerClientId)
            {
                return true;
            }

            var pm = PlayerManager.Instance;
            if (pm == null) return false;
            Player player = pm.GetPlayerByClientId(clientId);
            if (player == null) return false;

            var adminMgr = ServerManager.Instance?.AdminManager;
            return adminMgr != null && adminMgr.IsSteamIdAdmin(player.SteamId.Value.ToString());
        }

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

                PluginCore.ApplyLiveConfigFull();
                DashFallMod.GoalNetTweaks.RefreshAll();
                PoncePuck.Keybinds.ServerBridge.BroadcastFeaturesToAllClients();
                PoncePuck.Keybinds.ServerBridge.BroadcastGoalTweaksToAllClients();

                try
                {
                    var players = PlayerManager.Instance?.GetPlayers();
                    if (players != null)
                    {
                        foreach (Player player in players)
                            PluginCore.ManualSync(player.OwnerClientId);
                    }
                }
                catch (Exception e)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("ReloadServerConfig per-player manual sync failed: " + e.Message);
                }

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