// AdminConfigEditor.cs
// Server-side admin authentication and the shared apply-and-broadcast pipeline
// for the in-game live config editor (SERVER tab).
//
// Two small statics live here:
//   AdminAuth          - identity (host / the game's own admin flag) plus the
//                        shared random editor password.  This is the single
//                        source of truth for "may this client edit the config?".
//   ConfigApplyService - the shared tail extracted from /reload: apply live,
//                        re-broadcast every sync channel, and push the full
//                        credential-free config to all clients.
//
// The network plumbing (named-message handlers, _authedClients, the chunked
// string transport) lives in PoncePuck.Keybinds.ServerBridge alongside the
// existing PPKB/* channels; these statics hold only the pure logic so both the
// chat-command path and the editor path share one implementation.

using System;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace CompetitiveAdjustments
{
    public static class AdminAuth
    {
        // ---- Identity -------------------------------------------------------

        // True for the listen-server host and for any player the game itself
        // marks as an admin (AdminLevel > 0), exactly how the other mods detect
        // admins, so there is no separate allowlist to maintain.  Shared by the
        // /reload chat gate (SmallPatches) and the editor auth.
        public static bool IsAdmin(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsHost && clientId == NetworkManager.ServerClientId)
                return true;

            var pm = PlayerManager.Instance;
            if (pm == null) return false;
            Player player = pm.GetPlayerByClientId(clientId);
            if (player == null) return false;

            return player.AdminLevel.Value > 0;
        }

        // Full grant check for an editor unlock request.  Order: trusted
        // identity, then the OpenConfigChanges "open server" escape hatch, then
        // the typed password.
        public static bool IsAllowed(ulong clientId, string password)
        {
            if (IsAdmin(clientId)) return true;

            var cfg = ConfigManager.Config;
            if (cfg != null && cfg.CompTweaks != null && cfg.CompTweaks.OpenConfigChanges)
                return true;

            return VerifyPassword(password);
        }

        // ---- Password ------------------------------------------------------

        // A random, human-shareable editor password.  No salt/hash: it is a
        // generated secret kept server-side only (the whole Admin block is
        // stripped from the wire), so the host can read it back and hand it out.
        // The alphabet omits ambiguous characters (0/O, 1/I/l).
        public static string GeneratePassword()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            byte[] bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        // Verifies a typed password against the stored editor password.  Returns
        // false when no password is set or the supplied password is empty, so an
        // empty PPKB/AdminAuth never grants by password.
        public static bool VerifyPassword(string password)
        {
            var admin = ConfigManager.Config?.Admin;
            if (admin == null) return false;
            if (string.IsNullOrEmpty(admin.EditorPassword)) return false;
            if (string.IsNullOrEmpty(password)) return false;
            return FixedTimeEquals(password, admin.EditorPassword);
        }

        // Length-independent, content-constant-time string compare so password
        // verification does not leak timing about how many leading characters
        // matched.
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ (i < b.Length ? b[i] : a[i]);
            return diff == 0;
        }

        // ---- Security self-check -------------------------------------------

        // Guards the wire path: a payload that ever contains the Admin block or
        // any credential key is a serious leak, so log loudly and let callers
        // refuse to send.  Returns true when the payload is safe to transmit.
        public static bool AssertNoCredentials(string wireJson)
        {
            if (string.IsNullOrEmpty(wireJson)) return true;
            if (wireJson.Contains("\"Admin\"")
                || wireJson.Contains("EditorPassword"))
            {
                ConfigManager.LogError("BLOCKED: config wire payload contains credential keys; refusing to transmit.");
                return false;
            }
            return true;
        }
    }

    public static class ConfigApplyService
    {
        // The exact apply-and-broadcast tail formerly inlined in
        // ReloadServerConfig.  Runs the live physics/feature apply, refreshes
        // goal/arena geometry, re-pushes every existing sync channel, and
        // finally broadcasts the full credential-free config so remote editors
        // see true live values.  Server-only.
        public static void ApplyAndBroadcast()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            // Runtime safety: these two feed Time.fixedDeltaTime and
            // Physics.defaultSolverIterations directly, so an extreme value
            // entered in the editor (or a hand-edited file) could brick the
            // session.  Clamp on the live config before applying or broadcasting.
            ClampRuntimeLimits();

            CompetitivePuckTweaks.src.PluginCore.ApplyLiveConfigFull();
            DashFallMod.GoalNetTweaks.RefreshAll();
            PoncePuck.Keybinds.ServerBridge.BroadcastFeaturesToAllClients();
            PoncePuck.Keybinds.ServerBridge.BroadcastGoalTweaksToAllClients();

            try
            {
                var players = PlayerManager.Instance?.GetPlayers();
                if (players != null)
                    foreach (Player player in players)
                        CompetitivePuckTweaks.src.PluginCore.ManualSync(player.OwnerClientId);
            }
            catch (Exception e)
            {
                ConfigManager.LogWarning("ApplyAndBroadcast per-player manual sync failed: " + e.Message);
            }

            PoncePuck.Keybinds.ServerBridge.BroadcastConfigFullToAllClients();
        }

        // Clamps the two physics knobs that can brick a session to safe ranges.
        private static void ClampRuntimeLimits()
        {
            var ct = ConfigManager.Config?.CompTweaks;
            if (ct == null) return;

            if (float.IsNaN(ct.FixedDeltaTime) || ct.FixedDeltaTime < 0.002f) ct.FixedDeltaTime = 0.002f;
            else if (ct.FixedDeltaTime > 0.05f) ct.FixedDeltaTime = 0.05f;

            if (ct.SolverIterations < 1) ct.SolverIterations = 1;
            else if (ct.SolverIterations > 64) ct.SolverIterations = 64;
        }

        // Shared in-process routine for an admin edit: overwrite the live config
        // from the pushed wire JSON (sections + enables only; Admin untouched),
        // persist, then apply and broadcast.  Both the PPKB/AdminConfigSet
        // handler and the host SAVE shortcut call this so there is one code path
        // for "accept an edited config".  Server-only.
        public static void ApplyServerConfigEdit(string wireJson)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            ConfigManager.LoadFromJson(wireJson); // never reads an Admin block
            ClampRuntimeLimits();                 // keep disk in step with the applied clamp
            ConfigManager.SaveConfig();           // persists the live Admin block intact
            ApplyAndBroadcast();
        }
    }
}
