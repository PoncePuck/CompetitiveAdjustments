// ServerConfig.cs
// Unified server-side configuration for CompetitiveAdjustments.
// Nested JSON: { "ConfigVersion": 12, "Dashfall": { ... }, "CompAdjust": { ... }, "CompTweaks": { ... } }
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
        public bool EnableGoalNetTweaks = false;
        public float GoalThicknessScale = 1f;
        public float GoalSizeScaleX = 1f;
        public float GoalSizeScaleY = 1f;
        public float GoalSizeScaleZ = 1f;
        public float GoalBackOffset = 0f;
        // Custom goal-frame alignment. The bundled frame prefab's mesh orientation
        // does not match the b1117 goal, so expose live-tunable local rotation (Euler
        // degrees), position offset, and uniform scale to line the frame up with the
        // net. Applied on every goal refresh (host path), so /reload updates it live.
        // RotY defaults to 180: the b1117 goal frame faces opposite the prefab mesh
        // (confirmed in-game). Scale defaults to the historical 100x Blender-unit
        // correction; the b1117 goal is slightly larger, so bump it if the frame reads
        // small against the net.
        public float GoalFrameRotX = 0f;
        public float GoalFrameRotY = 180f;
        public float GoalFrameRotZ = 0f;
        public float GoalFrameOffsetX = 0f;
        public float GoalFrameOffsetY = 0f;
        public float GoalFrameOffsetZ = 0f;
        public float GoalFrameScale = 100f;
        // Inset applied to faceoff/spawn positions when the arena is scaled. 1.0 keeps
        // spawns at the exact proportional spot (correct now that ArenaScale 1.0 == the
        // base rink); <1 pulls them toward centre ice.
        public float SpawnFitInset = 1.0f;
        public bool EnableArenaTweaks = false;
        // Arena scale axes are WORLD axes, and have been since ConfigVersion 16.
        // Before that Y and Z were swapped: ArenaScaleY drove world Z and ArenaScaleZ
        // drove world Y, inherited from a bundled arena prefab that was rotated 90
        // degrees so its local Z pointed up. That prefab is gone, the swap outlived its
        // reason, and it confused every consumer that met it. Existing configs are
        // migrated by swapping the two values on load, so a rink keeps its shape.
        public float ArenaScaleX = 1f;   // world X, rink width
        // World Y, rink height. Scales for REAL, not just visually: the boards, glass and
        // ceiling colliders grow with it, and so does everything else under the level
        // root, the goals included. Turn GoalSizeScaleY down to compensate if a tall
        // arena leaves the nets stretched.
        public float ArenaScaleY = 1f;
        public float ArenaScaleZ = 1f;   // world Z, rink length
        public float ArenaOffsetX = 0f;
        public float ArenaOffsetY = 0.0108f;
        public float ArenaOffsetZ = 0f;

        // Chunked network positions. Puck quantises positions into a fixed box, world X
        // to +/-25 m and Y and Z to +/-50 m, and the compression SATURATES rather than
        // wrapping, so an object past the edge is pinned exactly at it. On a rink scaled
        // past that box, clients see remote objects stop dead on an invisible plane while
        // the server simulates correctly. Chunking gives each object a per-axis offset so
        // the wire only ever carries a chunk-local coordinate, which keeps positions
        // correct at any arena scale with bit-identical vanilla precision.
        //
        // Off by default. It changes what goes on the wire (one extra UInt16 on records
        // that need it), so every peer must run this mod. A client that has not announced
        // chunk capability is served vanilla-shaped records and simply sees the old
        // clipping, rather than a corrupted stream.
        public bool EnableChunkedPositions = false;

        // --- Free Blade ---
        public bool FreeBladeEnabled = false;

        // --- Stick Spin Fatigue ---
        // A speed limit on the blade that a player earns by spinning it, aimed at the
        // free spinning (bearing) mouse wheels that can hold the blade at full speed
        // indefinitely.  See src/StickSpinFatigue.cs.
        //
        // On by default, and inert on its own: it does nothing unless FreeBlade is on,
        // and the default trigger asks for 57.6 scroll steps per second sustained for
        // half a second, which a notched wheel cannot produce.  Set Enabled to false to
        // take it out entirely.
        public bool StickSpinFatigueEnabled = true;
        // How far the blade must turn one way to count as a revolution, in degrees, and
        // how quickly it has to get there to count as a fast one.  One unit of blade
        // angle is 12.5 degrees on the stock stick, so 360 degrees is 28.8 scroll steps.
        public float StickSpinFatigueRotationDegrees = 360f;
        public float StickSpinFatigueWindowSeconds = 0.5f;
        // How long the slowdown lasts, measured from the last fast revolution.
        public float StickSpinFatigueSlowSeconds = 1f;
        // Top blade speed while slowed, in degrees per second, at one stack.  Each
        // further stack multiplies it by StackFactor, so the defaults give 360, 216,
        // 130 and 78 degrees per second.  A revolution per second at one stack is still
        // brisk; it is half of what the default trigger demands.
        public float StickSpinFatigueLimitDegreesPerSecond = 360f;
        public float StickSpinFatigueStackFactor = 0.6f;
        public int StickSpinFatigueMaxStacks = 4;
        // Quiet time after the slowdown ends before the stacks clear.
        public float StickSpinFatigueRecoverySeconds = 1.5f;
        // Re-apply the limit on the server, to the value each client asks it to relay,
        // so a client running without the limiter gains nothing by it.  The server runs
        // a looser cap than the client does, and an honest client has already throttled
        // itself, so this should never be the half that bites.  Turn it off to leave the
        // rule entirely to the clients.
        public bool StickSpinFatigueServerEnforced = true;

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
        // Per-axis multipliers applied on top of the uniform PuckScale. Final
        // localScale = PuckScale * (PuckScaleX, PuckScaleY, PuckScaleZ). The puck
        // is a flat disc: X/Z are the diameter, Y is the thickness. All default
        // to 1 so existing configs reproduce the old uniform behaviour.
        public float PuckScaleX = 1f;
        public float PuckScaleY = 1f;
        public float PuckScaleZ = 1f;
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

    // Credentials for the in-game admin config editor.  Stored inside
    // CompetitiveAdjustments.json but DELIBERATELY excluded from every
    // client-facing serialization (see ServerConfig.SerializeForWire and
    // ConfigManager.LoadFromJson).  Broadcasting these would hand every
    // connected client the admin password material, so the wire path must
    // never carry this block.
    [Serializable]
    public class AdminAuthConfig
    {
        // A random password generated once when the config file is first written
        // (see ConfigManager.WriteConfig).  Server-side only: the whole Admin
        // block is stripped from every wire payload, so this never reaches a
        // client.  The host reads it from CompetitiveAdjustments.json (or the
        // SERVER tab) and shares it to grant a non-admin editor access.  Delete
        // the file, or blank this field, to regenerate it on the next write.
        //
        // Admin identity itself (host + the game's own admin list) is resolved
        // live in AdminAuth.IsAdmin, so there is no Steam-ID allowlist here.
        public string EditorPassword = "";
    }

    [Serializable]
    public class ServerConfig
    {
        // Single source of truth for the config schema version.  Bump this
        // whenever fields are added or removed so existing files are migrated
        // (merged onto the current defaults) on the next load.
        public const int CURRENT_VERSION = 17;
        public int ConfigVersion = CURRENT_VERSION;
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
        // Admin editor credentials.  NEVER serialized onto the wire.
        public AdminAuthConfig Admin = new AdminAuthConfig();

        // Produces the compact (non-indented) JSON sent to clients and accepted
        // back from the admin editor.  This is the SAME content WriteConfig
        // stitches MINUS the Admin block, so credentials never leave the
        // server.  Because WriteConfig already stitches blocks by hand we get a
        // credential-free serialization for free by simply omitting Admin here.
        public string SerializeForWire()
        {
            string dashfallJson   = JsonUtility.ToJson(Dashfall   ?? new DashfallConfig(),   false);
            string compAdjustJson = JsonUtility.ToJson(CompAdjust ?? new CompAdjustConfig(), false);
            string compTweaksJson = JsonUtility.ToJson(CompTweaks ?? new CompTweaksConfig(), false);

            return "{"
                + "\"ConfigVersion\":"    + ConfigVersion + ","
                + "\"EnableDashfall\":"   + (EnableDashfall   ? "true" : "false") + ","
                + "\"EnableCompAdjust\":" + (EnableCompAdjust ? "true" : "false") + ","
                + "\"EnableCompTweaks\":" + (EnableCompTweaks ? "true" : "false") + ","
                + "\"Dashfall\":"   + dashfallJson   + ","
                + "\"CompAdjust\":" + compAdjustJson + ","
                + "\"CompTweaks\":" + compTweaksJson
                + "}";
        }
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
            EnableGoalNetTweaks           = false,
            EnableArenaTweaks             = false,
            FreeBladeEnabled              = false,
            StickSpinFatigueEnabled       = false,
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

        // Guarantees a valid, current-schema config file exists on disk.  This
        // never touches the in-memory Config or fires reconcile hooks -- that
        // is ReloadConfig's job, and callers invoke ReloadConfig right after.
        public static void EnsureConfig()
        {
            // Every step here is best-effort, and that is the point.
            //
            // A config we cannot create means a server running DEFAULT settings, which
            // ReloadConfig already handles and now says out loud. It must never mean a
            // server running NO MOD. Both statements below used to sit above the try: the
            // ConfigDir getter creates the directory itself, and the first-run WriteConfig
            // is a bare File.WriteAllText. On a read-only install directory, or one owned
            // by a different account than the service (the same ownership situation that
            // produced the repeated-backup bug this file's upgrade path was rewritten for),
            // either throws UnauthorizedAccessException. Nothing caught it here, so it
            // unwound into CompetitiveAdjustmentsGameMod.OnEnable's catch, which returns
            // false and leaves every sub-mod and every Harmony patch unloaded. The operator
            // sees a mod that did not load at all, for a file that is merely unwritable.
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
            }
            catch (Exception ex)
            {
                LogWarning($"Could not create the config directory: {ex.Message}. The server runs with DEFAULT " +
                           "settings this session and nothing is saved. The mod itself is still loaded.");
                return;
            }

            try
            {
                if (!File.Exists(ConfigFile))
                {
                    WriteConfig(new ServerConfig());
                    return;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Could not create '{ConfigFile}': {ex.Message}. The server runs with DEFAULT settings " +
                           "this session and nothing is saved. The mod itself is still loaded. Check that the " +
                           "config directory is writable by the account the server runs as.");
                return;
            }

            try
            {
                string raw = File.ReadAllText(ConfigFile);

                // The old flat format (no section objects) cannot be migrated
                // field-by-field, so preserve the user's file as a timestamped
                // backup and write fresh defaults.
                bool isNested = raw.Contains("\"Dashfall\"") || raw.Contains("\"CompTweaks\"");
                if (!isNested)
                {
                    string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(ConfigFile, Path.Combine(ConfigDir, $"CompetitiveAdjustments_{ts}_old.json"));
                    WriteConfig(new ServerConfig());
                    return;
                }

                // Nested but stale: merge the existing values onto the current
                // defaults and rewrite, so newly-added fields appear with their
                // defaults and the version is bumped.  Existing values survive.
                string clean = StripJson(raw);
                if (NeedsUpgrade(clean)) UpgradeConfigFile(clean, ParseConfig(clean));
            }
            catch (Exception ex)
            {
                CompetitiveAdjustments.ConfigManager.LogWarning("Error upgrading config: " + ex.Message);
            }
        }

        // Exactly the bytes WriteConfig would put on disk for cfg. Split out so an
        // upgrade can compare its result against the current file before touching
        // anything; see UpgradeConfigFile.
        private static string RenderConfig(ServerConfig cfg)
        {
            cfg.Admin = cfg.Admin ?? new AdminAuthConfig();
            // Generate the random editor password the first time the file is
            // written (and after an upgrade that dropped the old credentials).
            if (string.IsNullOrEmpty(cfg.Admin.EditorPassword))
                cfg.Admin.EditorPassword = AdminAuth.GeneratePassword();
            return BuildConfigContent(cfg, redactAdmin: false);
        }

        // Write each sub-section via JsonUtility (flat serialization per section)
        // then stitch into a nested JSON file.
        private static void WriteConfig(ServerConfig cfg)
            => File.WriteAllText(ConfigFile, RenderConfig(cfg));

        /// <summary>How many timestamped upgrade backups to keep before deleting the oldest.</summary>
        private const int MaxUpgradeBackups = 5;

        private static bool _warnedUpgradeUnwritable;
        private static bool _warnedUpgradeNoOp;

        /// <summary>
        /// Brings the on-disk file up to the current schema, backing it up first.
        ///
        /// Written this way because of a server that accumulated twenty-odd identical
        /// backups. Its config was owned by root while the game ran as another user, so
        /// every launch backed the file up (a NEW file, in a directory the service could
        /// write), then failed to write the config itself, left it stale, and did the whole
        /// thing again next launch. The giveaway was that every backup carried the SAME
        /// modified time: File.Copy preserves the source's timestamp, so they were all
        /// copies of a file that never changed.
        ///
        /// Two guards. The backup is deleted again if the write it was taken for did not
        /// happen, so a config that cannot be written stops littering. And an upgrade that
        /// would produce byte-identical output does nothing at all, which covers the case
        /// where NeedsUpgrade is satisfied by something RenderConfig does not actually
        /// change.
        /// </summary>
        private static void UpgradeConfigFile(string clean, ServerConfig parsed)
        {
            // RenderConfig MUTATES what it renders: it mints an editor password when the
            // config carries none, so that a freshly written file has one. That is right for
            // a write that lands and wrong for one that does not. ReloadConfig publishes this
            // very object as the live Config, so a failed write left the server running a
            // password that exists nowhere on disk, re-minted on every launch and every
            // reload. Nobody could ever produce it: the file does not have it, ExportConfigJson
            // redacts it, and the reveal row needs a client UI a headless server does not have.
            // Meanwhile OnAdminAuthMsg saw a non-empty password and told non-admins to "enter
            // the admin password to unlock". Identity-based admins were never affected, so this
            // is the share-a-password path being advertised while it cannot work.
            //
            // Captured here and put back on every path that does not write, so the live config
            // and the file always agree about whether a password exists.
            string priorPassword = parsed != null && parsed.Admin != null ? parsed.Admin.EditorPassword : null;

            string updated;
            try { updated = RenderConfig(parsed); }
            catch (Exception ex)
            {
                RestoreEditorPassword(parsed, priorPassword);
                LogWarning("Could not render the upgraded config: " + ex.Message);
                return;
            }

            string current = null;
            try { current = File.ReadAllText(ConfigFile); } catch { }

            if (current != null && string.Equals(current, updated, StringComparison.Ordinal))
            {
                if (!_warnedUpgradeNoOp)
                {
                    _warnedUpgradeNoOp = true;
                    LogWarning("Config upgrade wanted but the result is byte-identical to the file on disk, " +
                               "so nothing was written. NeedsUpgrade is matching on something the writer does " +
                               "not emit; the config is fine and this will not repeat-log.");
                }
                return;
            }

            string backup = null;
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backup = Path.Combine(ConfigDir, $"CompetitiveAdjustments_{ts}_upgrade.json");
                    File.Copy(ConfigFile, backup, true);
                }
            }
            catch (Exception ex)
            {
                LogWarning("Config backup failed: " + ex.Message);
                backup = null;
            }

            try
            {
                File.WriteAllText(ConfigFile, updated);
                Log($"Config upgraded to version {ServerConfig.CURRENT_VERSION}" +
                    (backup != null ? $"; previous file kept as {Path.GetFileName(backup)}." : "."));
                PruneUpgradeBackups();
            }
            catch (Exception ex)
            {
                // The backup exists only to protect a rewrite that just failed, so it is
                // pure litter. Removing it is what stops one file per launch forever.
                if (backup != null)
                {
                    try { File.Delete(backup); } catch { }
                }

                // Nothing reached disk, so the password RenderConfig may have minted must not
                // stay in the object ReloadConfig is about to publish as the live Config.
                RestoreEditorPassword(parsed, priorPassword);

                if (!_warnedUpgradeUnwritable)
                {
                    _warnedUpgradeUnwritable = true;
                    LogWarning($"Could not write '{ConfigFile}': {ex.Message}. The server is running the config it " +
                               "read, but the upgrade cannot be saved and will be attempted again on every launch. " +
                               "Check the file's ownership and permissions: this happens when the config is owned by " +
                               "a different user (root) than the one the server runs as.");
                }
            }
        }

        /// <summary>
        /// Puts back the editor password RenderConfig may have minted, for a write that did
        /// not happen.
        ///
        /// A null prior means the config arrived with no Admin block at all. RenderConfig
        /// created one, which is harmless and is kept, but its password goes back to empty
        /// so the live config agrees with the file: no password set. An empty password is
        /// the state AdminAuth already understands, and it is what stops the auth path
        /// advertising a credential nobody can produce.
        /// </summary>
        private static void RestoreEditorPassword(ServerConfig cfg, string priorPassword)
        {
            if (cfg == null || cfg.Admin == null) return;
            cfg.Admin.EditorPassword = priorPassword ?? "";
        }

        /// <summary>Keeps the newest few upgrade backups and deletes the rest.</summary>
        private static void PruneUpgradeBackups()
        {
            try
            {
                var files = Directory.GetFiles(ConfigDir, "CompetitiveAdjustments_*_upgrade.json");
                if (files.Length <= MaxUpgradeBackups) return;

                Array.Sort(files, (a, b) => string.CompareOrdinal(b, a));   // newest name first (timestamped)
                for (int i = MaxUpgradeBackups; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
                Log($"Pruned {files.Length - MaxUpgradeBackups} old config backup(s), keeping the newest {MaxUpgradeBackups}.");
            }
            catch (Exception ex) { LogWarning("Could not prune old config backups: " + ex.Message); }
        }

        // Builds the nested, indented on-disk JSON for a config.  When
        // redactAdmin is true the editor password is blanked (for a shareable
        // export); the live config is never mutated by the redaction, a throwaway
        // Admin copy is serialized instead.
        private static string BuildConfigContent(ServerConfig cfg, bool redactAdmin)
        {
            cfg.ConfigVersion = ServerConfig.CURRENT_VERSION;
            string dashfallJson = JsonUtility.ToJson(cfg.Dashfall, true);
            string compAdjustJson = JsonUtility.ToJson(cfg.CompAdjust, true);
            string compTweaksJson = JsonUtility.ToJson(cfg.CompTweaks, true);
            var admin = redactAdmin ? new AdminAuthConfig { EditorPassword = "" }
                                    : (cfg.Admin ?? new AdminAuthConfig());
            string adminJson = JsonUtility.ToJson(admin, true);

            // Indent sub-section lines by 2 spaces
            string IndentBlock(string json)
            {
                var lines = json.Split('\n');
                for (int i = 1; i < lines.Length; i++)
                    lines[i] = "  " + lines[i];
                return string.Join("\n", lines);
            }

            return
                $"{{\n" +
                $"  \"ConfigVersion\": {cfg.ConfigVersion},\n" +
                $"  \"EnableDashfall\":  {(cfg.EnableDashfall  ? "true" : "false")},\n" +
                $"  \"EnableCompAdjust\": {(cfg.EnableCompAdjust ? "true" : "false")},\n" +
                $"  \"EnableCompTweaks\": {(cfg.EnableCompTweaks ? "true" : "false")},\n" +
                $"  \"Dashfall\": {IndentBlock(dashfallJson)},\n" +
                $"  \"CompAdjust\": {IndentBlock(compAdjustJson)},\n" +
                $"  \"CompTweaks\": {IndentBlock(compTweaksJson)},\n" +
                $"  \"Admin\": {IndentBlock(adminJson)}\n" +
                $"}}";
        }

        // The full config in the readable on-disk format with the editor password
        // blanked, safe to share or back up.  Pass the editor's working copy to
        // export exactly what is on screen, or null to export the live config.
        public static string ExportConfigJson(ServerConfig cfg = null)
        {
            return BuildConfigContent(cfg ?? Config ?? new ServerConfig(), redactAdmin: true);
        }

        // Writes the redacted export next to the live config and returns the full
        // path (or null on failure).
        public static string ExportConfigToFile(ServerConfig cfg = null)
        {
            try
            {
                string path = Path.Combine(ConfigDir, "CompetitiveAdjustments_export.json");
                File.WriteAllText(path, ExportConfigJson(cfg));
                return path;
            }
            catch (Exception ex)
            {
                LogWarning("ExportConfigToFile failed: " + ex.Message);
                return null;
            }
        }

        public static void SaveConfig()
        {
            WriteConfig(Config ?? new ServerConfig());
        }

        // BackupConfigFile lived here. Its job is now inside UpgradeConfigFile, because
        // taking the backup and doing the write have to be one decision: a backup kept for
        // a write that then failed is exactly the litter this was producing.

        public static void ReloadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFile))
                {
                    Config = new ServerConfig();
                    SyncFeatureStates(Config);
                    NotifySubModReconcile();
                    return;
                }

                string clean = StripJson(File.ReadAllText(ConfigFile));
                var cfg = ParseConfig(clean);

                // Self-heal the file if it is missing fields or on an older
                // version, so a direct ReloadConfig (no prior EnsureConfig)
                // still brings the on-disk file up to the current schema.
                if (NeedsUpgrade(clean)) UpgradeConfigFile(clean, cfg);

                Config = cfg;
                SyncFeatureStates(cfg);
                NotifySubModReconcile();
            }
            catch (Exception ex)
            {
                // Say something. This used to be a bare `catch` that silently replaced the
                // operator's config with defaults, so a server whose file could not be read
                // ran vanilla settings and looked like the mod was simply not working. The
                // fallback is still correct, it just has to be visible.
                LogWarning($"Could not load '{ConfigFile}': {ex.Message}. Falling back to DEFAULT settings for this " +
                           "session; nothing from the file is applied until it loads cleanly.");
                Config = new ServerConfig();
                SyncFeatureStates(Config);
                NotifySubModReconcile();
            }
        }

        // Strip // and /* */ comments and trailing commas so JsonUtility can
        // parse a hand-edited config file.
        private static string StripJson(string raw)
        {
            return Regex.Replace(
                Regex.Replace(
                    Regex.Replace(raw, @"//.*?$", "", RegexOptions.Multiline),
                    @"/\*.*?\*/", "", RegexOptions.Singleline),
                @",\s*(\}|\])", "$1");
        }

        // Parse a cleaned config string into a ServerConfig.  Anything absent
        // from the file keeps its type default, so this is a merge of the
        // file's values onto the current schema.  Pure: no I/O, no mutation of
        // the live Config, no reconcile hooks.
        private static ServerConfig ParseConfig(string clean)
        {
            var cfg = new ServerConfig();

            // Old configs without these top-level flags keep the default-true
            // behaviour.
            cfg.EnableDashfall   = ExtractTopLevelBool(clean, "EnableDashfall",   true);
            cfg.EnableCompAdjust = ExtractTopLevelBool(clean, "EnableCompAdjust", true);
            cfg.EnableCompTweaks = ExtractTopLevelBool(clean, "EnableCompTweaks", true);

            // Extract each section and deserialize flat into its class.
            string dashfallJson   = ExtractSection(clean, "Dashfall");
            string compAdjustJson = ExtractSection(clean, "CompAdjust");
            string compTweaksJson = ExtractSection(clean, "CompTweaks");

            if (!string.IsNullOrEmpty(dashfallJson))
                JsonUtility.FromJsonOverwrite(dashfallJson, cfg.Dashfall);
            if (!string.IsNullOrEmpty(compAdjustJson))
                JsonUtility.FromJsonOverwrite(compAdjustJson, cfg.CompAdjust);
            else if (!string.IsNullOrEmpty(dashfallJson))
                // Migrate: CompAdjust fields were previously stored in Dashfall.
                JsonUtility.FromJsonOverwrite(dashfallJson, cfg.CompAdjust);
            if (!string.IsNullOrEmpty(compTweaksJson))
                JsonUtility.FromJsonOverwrite(compTweaksJson, cfg.CompTweaks);

            MigrateArenaScaleAxes(cfg, ParseConfigVersion(clean));

            // Admin credentials live in the on-disk file only.  ParseConfig is
            // the disk path, so it is the one place we DO read the Admin block.
            // The wire path (LoadFromJson) deliberately never touches it.
            string adminJson = ExtractSection(clean, "Admin");
            if (!string.IsNullOrEmpty(adminJson))
                JsonUtility.FromJsonOverwrite(adminJson, cfg.Admin);

            return cfg;
        }

        /// <summary>
        /// ConfigVersion 16 made the arena scale axes world axes. Before it,
        /// ArenaScaleY drove world Z (length) and ArenaScaleZ drove world Y (height);
        /// now each drives the axis it is named after. Swapping the two stored values on
        /// the way in keeps an existing rink exactly the shape its owner set up, which
        /// matters because the alternative is silently turning a tall rink into a long
        /// one on the next server restart.
        /// </summary>
        private static void MigrateArenaScaleAxes(ServerConfig cfg, int storedVersion)
        {
            if (cfg?.CompAdjust == null) return;
            if (storedVersion < 0 || storedVersion >= 16) return;   // absent (-1) means pre-versioning; leave it

            float oldLength = cfg.CompAdjust.ArenaScaleY;   // world Z under the old naming
            float oldHeight = cfg.CompAdjust.ArenaScaleZ;   // world Y under the old naming
            if (Mathf.Approximately(oldLength, oldHeight)) return;

            cfg.CompAdjust.ArenaScaleY = oldHeight;
            cfg.CompAdjust.ArenaScaleZ = oldLength;

            Log($"Config v{storedVersion} -> v{ServerConfig.CURRENT_VERSION}: arena scale axes are now world axes, " +
                $"so ArenaScaleY/ArenaScaleZ were swapped ({oldLength}/{oldHeight} -> {oldHeight}/{oldLength}) " +
                "to keep the rink the same shape.");
        }

        // Overwrite ONLY the three sections and the three section enables from a
        // wire JSON payload (the SerializeForWire shape).  Used by both the
        // client full-config mirror and the server's inbound admin-edit handler.
        //
        // Two critical guarantees:
        //  1. It NEVER reads an "Admin" block, even if a malicious client stuffs
        //     one into the payload.  The live Config.Admin is left untouched, so
        //     credentials survive an inbound edit on the server and are never
        //     populated from the wire on a client.
        //  2. On a client it does NOT run the server-authoritative apply hooks
        //     (SyncFeatureStates / NotifySubModReconcile).  Those set the static
        //     client feature gates from the config; on a client those gates are
        //     driven by the PPKB/Features sync, not by this display mirror, so
        //     running them here would let the synced config silently repurpose
        //     the client's feature gating.  They run only when this process is
        //     the server (host editing in-process, or the inbound edit handler).
        public static void LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                string clean = StripJson(json);
                var cfg = Config ?? new ServerConfig();

                cfg.EnableDashfall   = ExtractTopLevelBool(clean, "EnableDashfall",   cfg.EnableDashfall);
                cfg.EnableCompAdjust = ExtractTopLevelBool(clean, "EnableCompAdjust", cfg.EnableCompAdjust);
                cfg.EnableCompTweaks = ExtractTopLevelBool(clean, "EnableCompTweaks", cfg.EnableCompTweaks);

                string dashfallJson   = ExtractSection(clean, "Dashfall");
                string compAdjustJson = ExtractSection(clean, "CompAdjust");
                string compTweaksJson = ExtractSection(clean, "CompTweaks");

                if (!string.IsNullOrEmpty(dashfallJson))   JsonUtility.FromJsonOverwrite(dashfallJson,   cfg.Dashfall);
                if (!string.IsNullOrEmpty(compAdjustJson)) JsonUtility.FromJsonOverwrite(compAdjustJson, cfg.CompAdjust);
                if (!string.IsNullOrEmpty(compTweaksJson)) JsonUtility.FromJsonOverwrite(compTweaksJson, cfg.CompTweaks);
                // NOTE: no ExtractSection(clean, "Admin") here, by design.

                Config = cfg;

                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    SyncFeatureStates(cfg);
                    NotifySubModReconcile();
                }
            }
            catch (Exception ex)
            {
                LogWarning("LoadFromJson failed: " + ex.Message);
            }
        }

        // True when the on-disk file should be rewritten to reach the current
        // schema: either the version differs, or a known field/section is
        // absent (a safety net for a field added without a version bump).
        private static bool NeedsUpgrade(string clean)
        {
            if (ParseConfigVersion(clean) != ServerConfig.CURRENT_VERSION)
                return true;

            return !clean.Contains("\"GoalieSlidingReachReduction\"")
                || !clean.Contains("\"GoalieSlidingReachScale\"")
                || !clean.Contains("\"CompAdjust\"")
                || !clean.Contains("\"EnableDashfall\"")
                || !clean.Contains("\"EnableCompAdjust\"")
                || !clean.Contains("\"EnableCompTweaks\"")
                || !clean.Contains("\"Admin\"")
                || !clean.Contains("\"EditorPassword\"");
        }

        // Read the integer ConfigVersion, or -1 if absent/unparseable.  Parsing
        // the number (rather than substring-matching the literal) avoids "12"
        // spuriously matching "120", and correctly flags a newer file (e.g. 13)
        // as a version mismatch.
        private static int ParseConfigVersion(string json)
        {
            var m = Regex.Match(json, "\"ConfigVersion\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
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
