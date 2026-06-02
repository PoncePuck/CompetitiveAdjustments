// ServerConfig.cs
// Unified server-side configuration for CompetitiveAdjustments.
// Nested JSON: { "ConfigVersion": 10, "Dashfall": { ... }, "CompAdjust": { ... }, "CompTweaks": { ... } }
// Uses JsonUtility with comment-stripping (DashFall pattern).

using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CompetitiveAdjustments
{
    [Serializable]
    public class DashfallConfig
    {
        public bool EnableDebugLogs = false;

        // --- Dash Settings ---
        public bool EnableGoalieScaling = true;
        public float GoalieDashForceScale = 0.7f;
        public float GoalieDashStaminaScale = 0.6f;
        public float GoalieBaseStandingDashCooldown = 0.2f;

        // --- Dive Settings ---
        public float DiveVelocity = 1.5f;
        public float DiveTorque = 12.1f;
        public float MinStaminaForDive_Skater = 0.7f;
        public float DiveAutoClearIfNoFallSeconds = 1.0f;
        public float MinAxis = 0.350f;
        public bool EnableDiveFallenDrag = true;
        public float DiveFallenDragAmount = 0.5f;

        // --- Slide / Twist Settings ---
        public float SlideInfluenceForce = 100.0f;
        public float SlideInfluenceMaxSpeed = 4.47f;
        public float SlideInfluenceStaminaCostPerSecond = 0f;
        public float SlideInfluenceMinStamina = 0.0f;
        public float SlideTwistForceScale = 1.0f;

        // --- Feature Flags ---
        public bool SkaterDashEnabled = false;
        public bool SkaterDiveEnabled = true;
        public bool EnableTwistWhileSliding = true;
        public bool EnableSlideInfluence = true;
        public bool GoalieDiveEnabled = true;
        public bool GoalieTwistWhileSlidingEnabled = false;
        public bool GoalieSlideInfluenceEnabled = false;
        public bool GoalieStandingDashEnabled = true;
        public bool GoalieDashExtendEnabled = true;
        public float GoalieDashExtendMinSpeedForExtend = 1f;
        public float GoalieDashExtendMaxSpeedForExtend = 2f;
        public float GoalieDashExtendCurvePower = 1.5f;
        public bool GoalieStancesEnabled = true;
        public bool GoalieSlidingReachReduction = false;
        public float GoalieSlidingReachScale = 0.75f; // default reach scale when sliding

    }

    [Serializable]
    public class CompAdjustConfig
    {
        public bool SprintShoulderTrailEnabled = true;
        public bool EnableCustomSkaterTorsoModel = false;
        public float CustomTorsoScaleX = 1f;
        public float CustomTorsoScaleY = 1f;
        public float CustomTorsoScaleZ = 1f;
        public bool DisableCustomTorsoVisual = false;
        public bool EnableGoalNetTweaks = false;
        public float GoalThicknessScale = 1f;
        public float GoalSizeScaleX = 1f;
        public float GoalSizeScaleY = 1f;
        public float GoalSizeScaleZ = 1f;
        public float GoalBackOffset = 0f;
        public bool EnableArenaTweaks = false;
        public float ArenaScaleX = 1f;
        public float ArenaScaleY = 1f;
        public float ArenaScaleZ = 1f;
        public float ArenaOffsetX = 0f;
        public float ArenaOffsetY = 0.0108f;
        public float ArenaOffsetZ = 0f;
        public float ArenaRotX = 90f;
        public float ArenaRotY = 180f;
        public float ArenaRotZ = 0f;

        // --- Free Blade ---
        public bool FreeBladeEnabled = false;

        // --- High Sticking ---
        public bool HighStickingEnabled = false;
        public float HighStickingActivateAngle = -20f;
        public float HighStickingMaxAngle = -80f;

        // --- Stick Body Collision ---
        public bool StickBodyCollision = false;

        // --- Ball Mode ---
        public bool BallMode = false;
    }

    [Serializable]
    public class CompTweaksConfig
    {
        // --- Movement ---
        public float TurnAccelerationBase = 1.5f;
        public float TurnBrakeAccelerationBase = 4.5f;
        public float TurnMaxSpeedBase = 1.3125f;
        public float TurnAccelerationScaling = 0f;
        public float TurnBrakeAccelerationScaling = 0f;
        public float TurnMaxSpeedScaling = 0f;
        public float TurnDrag = 3f;

        public float GoalieTurnAcceleration = 1.5f;
        public float GoalieTurnBrakeAcceleration = 4.5f;
        public float GoalieTurnMaxSpeed = 1.3125f;
        public float GoalieTurnDrag = 3f;

        public float MaxBackwardsSpeed = 7.5f;
        public float MaxBackwardsSprintSpeed = 8.75f;

        public float GoalieMaxForwardsSpeed = 5f;
        public float GoalieMaxForwardsSprintSpeed = 6.25f;
        public float GoalieMaxBackwardsSpeed = 5f;
        public float GoalieMaxBackwardsSprintSpeed = 6f;

        public float AngularForceMultiplier = 6f;

        public float ForwardsAccelerationBase = 2f;
        public float ForwardsSprintAccelerationBase = 4.75f;
        public float ForwardsAccelerationMin = 2f;
        public float ForwardsSprintAccelerationMin = 4.75f;

        public float BackwardsAccelerationBase = 1.8f;
        public float BackwardsAccelerationMin = 1.8f;
        public float BackwardsSprintAccelerationBase = 2f;
        public float BackwardsSprintAccelerationMin = 2f;

        public float ForwardsAccelerationScaling = 0;
        public float ForwardsSprintAccelerationScaling = 0;

        public float BackwardsAccelerationScaling = 0;
        public float BackwardsSprintAccelerationScaling = 0;

        public float MaxForwardsSpeed = 7.5f;
        public float MaxForwardsSprintSpeed = 8.75f;

        public float PostSlideTurnTime = 0f;
        public float PostSlideTurnMax = 1.3125f;
        public float PostSlideTurnAcceleration = 1.5f;
        public float PostSlideBrakeAcceleration = 4.5f;

        // --- Stamina ---
        public float StaminaRegenerationRate = 10f;
        public float SprintStaminaDrainRate = 1.4f;
        public float SprintStaminaDrainRateOffset = 0;
        public float JumpStaminaDrain = 0.125f;
        public float TwistStaminaDrain = 0.125f;
        public float DashStaminaDrain = 0.125f;

        public float GoalieStaminaRegenerationRate = 10f;
        public float GoalieSprintStaminaDrainRate = 1.4f;
        public float GoalieSprintStaminaDrainRateOffset = 0;
        public float GoalieJumpStaminaDrain = 0.125f;
        public float GoalieTwistStaminaDrain = 0.125f;
        public float GoalieDashStaminaDrain = 0.25f;

        // --- Player Body ---
        public float SlideTurnMultiplier = 2f;
        public float StopDrag = 2.5f;
        public float BalanceRecoveryTime = 5f;
        public float GoalieDashSpeedLimit = 100000f;
        public bool EnableGoalieMicrodash = false;
        public float MicrodashStamCostFraction = 0.75f;
        public bool EnableSmallerModels = false;
        public float TorsoColliderRadiusFactor = 1f;
        public float HeadColliderRadiusFactor = 1f;
        public float PlayerColliderBounciness = 0.2f;
        public float SlideDrag = 0.1f;
        public float CenterSpawnOffset = 0f;
        public float TackleSpeedThreshold = 7.6f;
        public float TackleForceThreshold = 7f;
        public float TackleForceMultiplier = 0.3f;
        public bool ThinSkaterBodies = false;
        public float SkaterThinningFactor = 1f;
        public float ButterflyPadOffset = 0f;

        // --- Puck ---
        public float PuckMaxSpeed = 30f;
        public float PuckStickTensorX = 0.006f;
        public float PuckStickTensorY = 0.002f;
        public float PuckStickTensorZ = 0.006f;
        public float PuckScale = 1f;
        public float PuckDrag = 0.3f;
        public float PuckMass = 0.375f;
        public bool RandomPuckDrop = false;
        public bool EnablePuckThroughBodies = false;
        public bool EnablePuckThroughGroin = false;
        public bool PuckDragSpeedDependence = false;
        public float PuckNominalSpeed = 20f;
        public float PuckDragFactor = 0.0014f;
        public bool PuckHeightDependentDrag = false;
        public float PuckHeightLimit = 2f;
        public float PuckHeightDragFactor = 0f;

        // --- Stick ---
        public bool DisableStickCollision = false;
        public bool DisableShaftCollision = false;
        public bool EnableMidStickCollider = false;
        public float StickMass = 1f;
        public bool AlterStickPositionerOutput = true;
        public float ShaftHandleProportionalGain = 500f;
        public float StickPositionerOutputMax = 750f;
        public float GoaliePositionerOutputMax = 750f;
        public float StickOnPuckInverseMass = 1f;
        public bool EnableStickSpeedDecay = false;
        public int StickSpeedDecaySpan = 75;
        public float StickSpeedDecayLimit = 22f;
        public float StickSpeedDecayRate = 6f;
        public float StickSpeedDecayMin = 500;
        public float SoftCollisionForce = 20f;
        public float BladeTargetFocusPointOffsetY = 0f;

        // --- Arena ---
        public float postBounciness = 0f;
        public bool EnableSoftBoards = false;
        public float BoardBounciness = 0.19f;
        public float BoardFriction = 0.1f;
        public float BoardSoftness = 0.5f;

        // --- Physics ---
        public float FixedDeltaTime = 0.01f;
        public int SolverIterations = 6;
        public bool UsePhysicsModificationEvents = false;
        public bool EnableJohnBoardBounceTweak = false;
        public float JohnBoardBounceLinearReduction = 0.15f;
        public float JohnBoardBounceDefaultForce = 1f;

        // --- Misc ---
        public bool OpenConfigChanges = false;
        public bool BananaMode = false;
    }

    [Serializable]
    public class ServerConfig
    {
        public int ConfigVersion = 11;
        // Top-level section enables.  Each gates a whole feature category so
        // a user who only wants one category can disable the others without
        // touching every individual flag.  All default to true to preserve
        // historical behaviour for existing configs.
        public bool EnableDashfall  = true;
        public bool EnableCompAdjust = true;
        public bool EnableCompTweaks = true;
        public DashfallConfig Dashfall = new DashfallConfig();
        public CompAdjustConfig CompAdjust = new CompAdjustConfig();
        public CompTweaksConfig CompTweaks = new CompTweaksConfig();
    }

    public static class ConfigManager
    {
        public static ServerConfig Config { get; private set; } = new ServerConfig();

        private static string ConfigDir
        {
            get
            {
                string gameRoot = Application.dataPath;
                if (gameRoot.EndsWith("Puck_Data"))
                    gameRoot = Directory.GetParent(gameRoot).FullName;

                string folder = Path.Combine(gameRoot, "config");
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                return folder;
            }
        }

        private static string ConfigFile => Path.Combine(ConfigDir, "CompetitiveAdjustments.json");
        private const int CONFIG_VERSION = 11;

        // "All disabled" sentinel configs returned by the Effective accessors
        // when their master section flag is off.  Feature consumers read these
        // (via the Effective accessors and the shortcut properties in
        // DashFallMod.ConfigManager / Tweaks.PluginCore) so silencing a
        // section is a single boolean flip rather than rewriting every flag
        // check.  Numeric fields stay at the CompAdjustConfig /
        // CompTweaksConfig / DashfallConfig type defaults; the CompTweaks
        // defaults match the vanilla Puck values so an off CompTweaks gives
        // vanilla physics.
        private static readonly CompAdjustConfig _disabledCompAdjust = new CompAdjustConfig
        {
            SprintShoulderTrailEnabled    = false,
            EnableCustomSkaterTorsoModel  = false,
            DisableCustomTorsoVisual      = false,
            EnableGoalNetTweaks           = false,
            EnableArenaTweaks             = false,
            FreeBladeEnabled              = false,
            HighStickingEnabled           = false,
            StickBodyCollision            = false,
            BallMode                      = false,
        };

        private static readonly DashfallConfig _disabledDashfall = new DashfallConfig
        {
            // Force every feature toggle to false so master-off silences
            // dives, dashes, twists, slide influence, goalie features etc.
            SkaterDashEnabled                  = false,
            SkaterDiveEnabled                  = false,
            EnableTwistWhileSliding            = false,
            EnableSlideInfluence               = false,
            GoalieDiveEnabled                  = false,
            GoalieTwistWhileSlidingEnabled     = false,
            GoalieSlideInfluenceEnabled        = false,
            GoalieStandingDashEnabled          = false,
            GoalieDashExtendEnabled            = false,
            GoalieStancesEnabled               = false,
            GoalieSlidingReachReduction        = false,
            EnableGoalieScaling                = false,
            EnableDiveFallenDrag               = false,
            EnableDebugLogs                    = false,
        };

        // CompTweaks vanilla defaults already live on the type (per the
        // VanillaCPTValues.json reset), so a fresh CompTweaksConfig is the
        // "off" state.
        private static readonly CompTweaksConfig _disabledCompTweaks = new CompTweaksConfig();

        /// <summary>
        /// Returns the live CompAdjust config when the master flag is on AND
        /// the local process is authoritative on it (the server itself, or a
        /// client that has received the PPKB/GoalTweaks sync from the server).
        /// Otherwise returns a stripped "all features off" copy.  This catches
        /// the case where a client disconnects from a modded server (leaving
        /// stale synced values in local Config.CompAdjust) and connects to a
        /// vanilla server: the synced flag is cleared so the local stale
        /// state is no longer applied.
        /// UI display code that wants to show the user's intent should keep
        /// reading <c>Config.CompAdjust</c> directly.
        /// </summary>
        public static CompAdjustConfig CompAdjustEffective
        {
            get
            {
                if (Config == null || !Config.EnableCompAdjust) return _disabledCompAdjust;
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && !nm.IsServer && !DashFallMod.GoalNetTweaks.HasSyncedTweaks)
                    return _disabledCompAdjust;
                return Config.CompAdjust;
            }
        }

        /// <summary>
        /// Returns the live Dashfall config when the master flag is on, or a
        /// stripped "all features off" copy when it is off.  Read this
        /// instead of <c>Config.Dashfall</c> when deciding whether to enable
        /// a Dashfall feature.  Direct numeric-only reads (e.g. dive force
        /// tuning) can use either path since the disabled-Dashfall instance
        /// keeps the same tuning defaults.
        /// </summary>
        public static DashfallConfig DashfallEffective =>
            Config != null && Config.EnableDashfall ? Config.Dashfall : _disabledDashfall;

        /// <summary>
        /// Returns the live CompTweaks config when the master flag is on, or
        /// a fresh CompTweaksConfig (vanilla defaults) when it is off.  Tweaks
        /// patches read this via <c>PluginCore.config</c>, so silencing the
        /// section sets every Unity physics value back to vanilla on the next
        /// ApplyLiveConfig.
        /// </summary>
        public static CompTweaksConfig CompTweaksEffective =>
            Config != null && Config.EnableCompTweaks ? Config.CompTweaks : _disabledCompTweaks;

        public static void EnsureConfig()
        {
            if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);

            if (File.Exists(ConfigFile))
            {
                try
                {
                    string existing = File.ReadAllText(ConfigFile);
                    // Detect old flat format (doesn't have nested section headers)
                    bool isNested = existing.Contains("\"Dashfall\"") || existing.Contains("\"CompTweaks\"");
                    if (!isNested)
                    {
                        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Copy(ConfigFile, Path.Combine(ConfigDir, $"CompetitiveAdjustments_{ts}_old.json"));
                        WriteConfig(new ServerConfig());
                    }
                    else
                    {
                        bool missingGoalieReachFields =
                            !existing.Contains("\"GoalieSlidingReachReduction\"") ||
                            !existing.Contains("\"GoalieSlidingReachScale\"");
                        bool missingTorsoXYZFields =
                            !existing.Contains("\"CustomTorsoScaleX\"") ||
                            !existing.Contains("\"DisableCustomTorsoVisual\"");
                        bool missingCompAdjustSection = !existing.Contains("\"CompAdjust\"");
                        bool missingSectionEnables =
                            !existing.Contains("\"EnableDashfall\"") ||
                            !existing.Contains("\"EnableCompAdjust\"") ||
                            !existing.Contains("\"EnableCompTweaks\"");
                        bool outdatedVersion = !Regex.IsMatch(existing, $"\"ConfigVersion\"\\s*:\\s*{CONFIG_VERSION}");

                        if (missingGoalieReachFields || missingTorsoXYZFields || missingCompAdjustSection || missingSectionEnables || outdatedVersion)
                        {
                            ReloadConfig();
                            WriteConfig(Config ?? new ServerConfig());
                        }
                    }
                }
                catch (Exception ex)
                {
                    CompetitiveAdjustments.ConfigManager.LogWarning("Error upgrading config: " + ex.Message);
                }
            }
            else
            {
                WriteConfig(new ServerConfig());
            }
        }

        // Write each sub-section via JsonUtility (flat serialization per section)
        // then stitch into a nested JSON file.
        private static void WriteConfig(ServerConfig cfg)
        {
            cfg.ConfigVersion = CONFIG_VERSION;
            string dashfallJson = JsonUtility.ToJson(cfg.Dashfall, true);
            string compAdjustJson = JsonUtility.ToJson(cfg.CompAdjust, true);
            string compTweaksJson = JsonUtility.ToJson(cfg.CompTweaks, true);

            // Indent sub-section lines by 2 spaces
            string IndentBlock(string json)
            {
                var lines = json.Split('\n');
                for (int i = 1; i < lines.Length; i++)
                    lines[i] = "  " + lines[i];
                return string.Join("\n", lines);
            }

            string content =
                $"{{\n" +
                $"  \"ConfigVersion\": {cfg.ConfigVersion},\n" +
                $"  \"EnableDashfall\":  {(cfg.EnableDashfall  ? "true" : "false")},\n" +
                $"  \"EnableCompAdjust\": {(cfg.EnableCompAdjust ? "true" : "false")},\n" +
                $"  \"EnableCompTweaks\": {(cfg.EnableCompTweaks ? "true" : "false")},\n" +
                $"  \"Dashfall\": {IndentBlock(dashfallJson)},\n" +
                $"  \"CompAdjust\": {IndentBlock(compAdjustJson)},\n" +
                $"  \"CompTweaks\": {IndentBlock(compTweaksJson)}\n" +
                $"}}";
            File.WriteAllText(ConfigFile, content);
        }

        public static void SaveConfig()
        {
            WriteConfig(Config ?? new ServerConfig());
        }

        public static void ReloadConfig()
        {
            var cfg = new ServerConfig();
            try
            {
                if (!File.Exists(ConfigFile))
                {
                    Config = cfg;
                    SyncFeatureStates(cfg);
                    return;
                }

                string raw = File.ReadAllText(ConfigFile);
                string clean = Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(raw, @"//.*?$", "", RegexOptions.Multiline),
                        @"/\*.*?\*/", "", RegexOptions.Singleline),
                    @",\s*(\}|\])", "$1");

                // Top-level section enables.  Old configs without these keep
                // the default-true behaviour.
                cfg.EnableDashfall   = ExtractTopLevelBool(clean, "EnableDashfall",   true);
                cfg.EnableCompAdjust = ExtractTopLevelBool(clean, "EnableCompAdjust", true);
                cfg.EnableCompTweaks = ExtractTopLevelBool(clean, "EnableCompTweaks", true);

                // Extract each section and deserialize flat into its class
                string dashfallJson = ExtractSection(clean, "Dashfall");
                string compAdjustJson = ExtractSection(clean, "CompAdjust");
                string compTweaksJson = ExtractSection(clean, "CompTweaks");

                if (!string.IsNullOrEmpty(dashfallJson))
                    JsonUtility.FromJsonOverwrite(dashfallJson, cfg.Dashfall);
                if (!string.IsNullOrEmpty(compAdjustJson))
                    JsonUtility.FromJsonOverwrite(compAdjustJson, cfg.CompAdjust);
                else if (!string.IsNullOrEmpty(dashfallJson))
                    // Migrate: CompAdjust fields were previously stored in Dashfall section
                    JsonUtility.FromJsonOverwrite(dashfallJson, cfg.CompAdjust);
                if (!string.IsNullOrEmpty(compTweaksJson))
                    JsonUtility.FromJsonOverwrite(compTweaksJson, cfg.CompTweaks);

                bool missingGoalieReachFields =
                    !clean.Contains("\"GoalieSlidingReachReduction\"") ||
                    !clean.Contains("\"GoalieSlidingReachScale\"");
                bool missingTorsoXYZFields =
                    !clean.Contains("\"CustomTorsoScaleX\"") ||
                    !clean.Contains("\"DisableCustomTorsoVisual\"");
                bool missingCompAdjustSection = !clean.Contains("\"CompAdjust\"");
                bool missingSectionEnables =
                    !clean.Contains("\"EnableDashfall\"") ||
                    !clean.Contains("\"EnableCompAdjust\"") ||
                    !clean.Contains("\"EnableCompTweaks\"");
                bool outdatedVersion = !Regex.IsMatch(clean, $"\"ConfigVersion\"\\s*:\\s*{CONFIG_VERSION}");

                if (missingGoalieReachFields || missingTorsoXYZFields || missingCompAdjustSection || missingSectionEnables || outdatedVersion)
                {
                    WriteConfig(cfg);
                }

                Config = cfg;
                SyncFeatureStates(cfg);
                NotifySubModReconcile();
            }
            catch
            {
                Config = new ServerConfig();
                SyncFeatureStates(Config);
                NotifySubModReconcile();
            }
        }

        // Soft hand-off to CompetitiveAdjustmentsGameMod.ApplySubModEnables.
        // Lives behind a try so config code stays independent of the entry
        // point's lifecycle in case the type is not loaded yet.
        private static void NotifySubModReconcile()
        {
            try { CompetitiveAdjustmentsGameMod.NotifyConfigReloaded(); }
            catch { }
        }

        // Top-level boolean extraction.  Matches `"Name": true|false` at any
        // depth but the brace-aware parser would be overkill for three flags
        // that always live at the outermost level; this regex is sufficient.
        private static bool ExtractTopLevelBool(string json, string fieldName, bool defaultVal)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(true|false)");
            return m.Success ? bool.Parse(m.Groups[1].Value) : defaultVal;
        }

        // Brace-counting extraction of a named JSON object section.
        private static string ExtractSection(string json, string sectionName)
        {
            string key = $"\"{sectionName}\"";
            int keyIdx = json.IndexOf(key);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length);
            if (colonIdx < 0) return null;

            int start = json.IndexOf('{', colonIdx + 1);
            if (start < 0) return null;

            int depth = 1;
            int i = start + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                i++;
            }
            return depth == 0 ? json.Substring(start, i - start) : null;
        }

        private static void SyncFeatureStates(ServerConfig cfg)
        {
            try
            {
                // Read through DashfallEffective so EnableDashfall=false also
                // drops the static feature flags to false.  Without this the
                // GoalieDashExtend / Stances Harmony patches would keep
                // running based on cfg.Dashfall even when the master flag
                // says the section is off.
                var df = DashfallEffective;
                DashFallMod.GoalieDashExtend.Enabled = df.GoalieDashExtendEnabled;
                DashFallMod.Stances.Enabled = df.GoalieStancesEnabled;
            }
            catch { }
        }

        public static void Log(string message)
        {
            Debug.Log("[COMPADJUST] " + message);
        }

        public static void LogWarning(string message) {
            Debug.LogWarning("[COMPADJUST] " + message);
        }

        public static void LogError(string message) {
            Debug.LogError("[COMPADJUST] " + message);
        }

        public static void Dbg(string message)
        {
            if (Config.Dashfall.EnableDebugLogs) Log(message);
        }
    }
}
