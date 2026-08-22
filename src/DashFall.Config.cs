// DashFall.Config.cs - DashFall's own keybind configuration

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DashFallMod.Client
{
    /// <summary>
    /// Bounds for the client-side blade spin lock, in the same sbyte units the game's
    /// BladeAngleInput uses.
    /// </summary>
    public static class FreeBladeSpinRange
    {
        // How far the slider can be dragged. -128 is a valid sbyte but not a valid bound
        // here: the handler casts the clamped buffer with (sbyte), and a symmetric span
        // means the blade spins the same distance either way.
        //
        // These bound the LOCK only. They are not the ceiling on free spin: with the lock
        // off the buffer wraps rather than clamping, so the blade turns without end. See
        // BladeSpinWrap in src/StickAnglePatch.cs.
        public const float LimitMin = -127f;
        public const float LimitMax = 127f;

        // Vanilla's own minimumBladeAngle/maximumBladeAngle, and the default range for the
        // lock. The lock ships ON at this pair, which is deliberate: it is exactly the limit
        // FreeBlade removes, so a stock client stops the blade at 4 and plays like vanilla
        // even on a server running FreeBlade. Free spin is opt-in.
        //
        // So these two are the knob to widen if free spin is ever wanted out of the box.
        // Do not flip FreeBladeSpinLockEnabled instead: that makes the lock's name stop
        // describing what it does. See the field's own comment further down this file.
        public const float DefaultMin = -4f;
        public const float DefaultMax = 4f;
    }

    [Serializable]
    public class DashFallClientConfig
    {
        /// <summary>
        /// Schema version, used only to run one-time repairs on files written by older
        /// builds. It deliberately defaults to 0 rather than the current version: a file
        /// written before this field existed has no such key, and JsonUtility leaves a
        /// missing field at its initializer, so anything but 0 would make every old file
        /// claim to be current and skip the repair it needs.
        ///
        /// A migration must never be inferred from the values alone. Turning the blade lock
        /// off because it sits at the vanilla range would fire again every launch and
        /// silently undo a player who genuinely wants that setting; the stamp is what makes
        /// it one time.
        /// </summary>
        public int ClientConfigVersion = 0;

        public bool EnableClientDebug = false;
        public bool ShowArenaClipBrushes = false; // debug: visualise arena collider meshes
        public bool ShowPlayerClipBrushes = false; // debug: visualise player collider meshes
        // EnableMinimapTweaks is gone. The minimap is normalised by UIMinimap.Bounds, which
        // tracks the arena scale, so on a resized rink the vanilla minimap is simply wrong:
        // the dots sit at the wrong place on the map. There was never a reason to prefer
        // that, and the toggle only ever produced a broken minimap on a modded server. The
        // rescale is a no-op on a vanilla rink, so it is now unconditional.
        // How the resized rink's geometry is redrawn. "drawmesh" rescales the base
        // game's own statically batched surfaces; "batchroot" is the experimental
        // zero-draw-call variant; "off" leaves the visuals frozen at vanilla size while
        // collision still resizes, which is only useful for isolating a rendering
        // problem. See src/ArenaProxyVisual.cs.
        public string ArenaVisualMode = "drawmesh";
        // Hide the arena crowd instead of moving it with the resized rink. Worth turning
        // on at large arena scales, where the crowd is stretched by the same factors as
        // the building. See src/ArenaStrandedScenery.cs.
        public bool HideArenaCrowd = false;
        // Stretch a custom arena loaded by Dem's Scenery Loader so the resized rink fits
        // inside it. On by default, because the alternative is a vanilla-sized building
        // with the boards sticking out through its walls.
        // LEAVE THIS ON unless you are debugging. The scenery carries colliders, and the
        // server scales it too, so turning it off locally puts your collision out of step
        // with everyone else's. See src/ArenaSceneryLoaderSync.cs.
        public bool ScaleSceneryLoaderArena = true;
        // Brightness multiplier for the arena lights after they are moved with a resized
        // rink. Raise it when a tall ArenaScaleZ leaves the ice reading dark, since the
        // fixtures end up much further from it. See src/ArenaLightSync.cs.
        public float ArenaLightIntensityScale = 1f;
        // Copy the arena's BAKED light fixtures onto realtime lights. On by default,
        // because proxy-drawn geometry cannot carry baked lightmaps, so without this a
        // bake-lit arena renders as flat ambient with no shading at all. Turn it off to
        // go back to ambient only. See src/ArenaLightSync.cs.
        public bool ReviveBakedArenaLights = true;
        // Hand the base game's baked lightmaps back to the proxy-drawn geometry. On by
        // default; turn it off to fall back to light probes and find out in one restart
        // whether a shading artefact comes from the lightmap or from something else.
        // See src/ArenaProxyVisual.cs.
        public bool ProxyRestoreLightmaps = true;
        // Puck size is SERVER state. These are the runtime slots the sync receive path
        // writes (Companion.PluginCore.ReceiveMessage) and PuckPatch.GetSyncedPuckScaleVector
        // reads back; they are not client preferences and have no settings row, because the
        // server overwrites them on every sync.
        // Final localScale = PuckScale * (PuckScaleX, PuckScaleY, PuckScaleZ).
        //
        // [NonSerialized] is load-bearing, not tidiness. ReceiveMessage calls
        // SaveClientConfig, so while these persisted, the last server's puck shape was
        // written to disk and read back at the next launch BEFORE any sync arrived, and
        // applied again when hosting. With the settings rows gone there is no longer any way
        // to undo that from the UI. Keeping them session-only means an unsynced client is
        // always a 1.0 puck. ResetPuckScale covers the same ground within a session.
        [NonSerialized] public float PuckScale = 1f;
        [NonSerialized] public float PuckScaleX = 1f;
        [NonSerialized] public float PuckScaleY = 1f;
        [NonSerialized] public float PuckScaleZ = 1f;
        // ButterflyPadOffset was here and was write-only: the sync path set it and nothing
        // ever read it back. The leg pad reads ConfigManager.CompTweaksEffective, which the
        // same receive path mirrors two lines later, and that is the value the server and
        // the client agree on.
        // ON by default, and the range below is -4/+4, which is EXACTLY vanilla's
        // minimumBladeAngle/maximumBladeAngle. So a stock client behaves like vanilla even
        // on a server running FreeBlade, and free spin is something the player opts into by
        // turning this off. That is the intended default, confirmed deliberately.
        //
        // This interaction is NOT an oversight, and the comment is here because it looks
        // like one. At stock settings the lock hands back the precise limit FreeBlade
        // removes and the blade stops dead at 4, which is what the "free blade locks at max
        // twist" report described. The behaviour is the same; what changed is that it is now
        // a stated choice with a visible switch rather than a surprise.
        //
        // If free spin is ever wanted out of the box, widen FreeBladeSpinRange.Default*
        // rather than flipping this flag, so the lock keeps meaning what its name says.
        //
        // OFF is endless, not merely wide. The handler wraps the buffer instead of clamping
        // it, so there is no bound to reach in either direction. Widening the range above
        // instead tops out at +/-127, which is the widest clamp an sbyte on the wire allows.
        // Per role since ConfigVersion 2. The blade wants different treatment in the two
        // positions: a goalie holding an angle across the crease and a skater carrying the puck
        // are not asking the same thing of it, and a single shared range forced one setting to
        // serve both. Both roles ship with the lock ON at vanilla's own +/-4.
        //
        // The three unsuffixed fields below are the pre-split values, kept ONLY so an existing
        // config can be migrated once. Nothing reads them after MigrateFreeBladePerRole has run.
        // Do not add new readers; use GetFreeBlade(role).
        public bool FreeBladeSpinLockEnabledSkater = true;
        public float FreeBladeSpinMinSkater = FreeBladeSpinRange.DefaultMin;
        public float FreeBladeSpinMaxSkater = FreeBladeSpinRange.DefaultMax;

        public bool FreeBladeSpinLockEnabledGoalie = true;
        public float FreeBladeSpinMinGoalie = FreeBladeSpinRange.DefaultMin;
        public float FreeBladeSpinMaxGoalie = FreeBladeSpinRange.DefaultMax;

        [Obsolete("Pre-split value, migration source only. Read GetFreeBlade(bool) instead.")]
        public bool FreeBladeSpinLockEnabled = true;
        [Obsolete("Pre-split value, migration source only. Read GetFreeBlade(bool) instead.")]
        public float FreeBladeSpinMin = FreeBladeSpinRange.DefaultMin;
        [Obsolete("Pre-split value, migration source only. Read GetFreeBlade(bool) instead.")]
        public float FreeBladeSpinMax = FreeBladeSpinRange.DefaultMax;

        /// <summary>
        /// The lock state and bounds for one role. One accessor so every consumer agrees about
        /// which pair of fields belongs to which position.
        /// </summary>
        public void GetFreeBlade(bool goalie, out bool locked, out float min, out float max)
        {
            locked = goalie ? FreeBladeSpinLockEnabledGoalie : FreeBladeSpinLockEnabledSkater;
            min    = goalie ? FreeBladeSpinMinGoalie         : FreeBladeSpinMinSkater;
            max    = goalie ? FreeBladeSpinMaxGoalie         : FreeBladeSpinMaxSkater;
        }
        public bool EnableSprintShoulderTrail = true;
        public float SprintShoulderTrailTime = 0.45f;
        public float SprintShoulderTrailWidth = 0.08f;
        public string SprintShoulderTrailStartColorHex = "#FFFFFF";
        public string SprintShoulderTrailEndColorHex = "#FFFFFF";
        public float SprintShoulderTrailStartAlpha = 0.95f;
        public float SprintShoulderTrailEndAlpha = 0f;
    }

    [Serializable]
    public class SkaterKeybindConfig
    {
        public List<string> divekey = new List<string>();
        public List<string> dashleftkey = new List<string>();
        public List<string> dashrightkey = new List<string>();
        public List<string> powercarvekey = new List<string>();
        public List<string> twistleftkey = new List<string>();
        public List<string> twistrightkey = new List<string>();
        public List<string> slideinfluenceleftkey = new List<string>();
        public List<string> slideinfluencerightkey = new List<string>();
        public List<string> slideinfluenceforwardkey = new List<string>();
        public List<string> slideinfluencebackwardkey = new List<string>();
        
        // Action types: "PRESS", "RELEASE", "DOUBLE PRESS", "HOLD" for pressable actions
        public string divekeytype = "PRESS";
        public string dashleftkeytype = "PRESS";
        public string dashrightkeytype = "PRESS";
        public string powercarvekeytype = "HOLD";
        public string twistleftkeytype = "DOUBLE PRESS";
        public string twistrightkeytype = "DOUBLE PRESS";
        
        // Action types: "CONTINUOUS", "TOGGLE" for holdable/movement actions
        public string slideinfluenceleftkeytype = "CONTINUOUS";
        public string slideinfluencerightkeytype = "CONTINUOUS";
        public string slideinfluenceforwardkeytype = "CONTINUOUS";
        public string slideinfluencebackwardkeytype = "CONTINUOUS";
    }

    [Serializable]
    public class GoalieKeybindConfig
    {
        public List<string> divekey = new List<string>();
        public List<string> standingdashleftkey = new List<string>();
        public List<string> standingdashrightkey = new List<string>();
        public List<string> twistleftkey = new List<string>();
        public List<string> twistrightkey = new List<string>();
        public List<string> slideinfluenceleftkey = new List<string>();
        public List<string> slideinfluencerightkey = new List<string>();
        public List<string> slideinfluenceforwardkey = new List<string>();
        public List<string> slideinfluencebackwardkey = new List<string>();
        
        // Action types: "PRESS", "RELEASE", "DOUBLE PRESS", "HOLD" for pressable actions
        public string divekeytype = "PRESS";
        public string standingdashleftkeytype = "PRESS";
        public string standingdashrightkeytype = "PRESS";
        public string twistleftkeytype = "DOUBLE PRESS";
        public string twistrightkeytype = "DOUBLE PRESS";
        
        // Action types: "CONTINUOUS", "TOGGLE" for holdable/movement actions
        public string slideinfluenceleftkeytype = "CONTINUOUS";
        public string slideinfluencerightkeytype = "CONTINUOUS";
        public string slideinfluenceforwardkeytype = "CONTINUOUS";
        public string slideinfluencebackwardkeytype = "CONTINUOUS";
    }

    public static class DashFallConfigLoader
    {
        private static string GameDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string ConfigDir => Path.Combine(GameDir, "config");
        private static string ModHubDir => Path.Combine(ConfigDir, "ModHub");
        private static string DashFallDir => Path.Combine(ModHubDir, "DashFall");
        
        // Legacy path for migration fallback
        private static string LegacyDir => Path.Combine(ConfigDir, "playerinput");
        
        public static string SkaterPath => Path.Combine(DashFallDir, "PonceKeybinds.Skater.json");
        public static string GoaliePath => Path.Combine(DashFallDir, "PonceKeybinds.Goalie.json");
        public static string ClientConfigPath => Path.Combine(DashFallDir, "DashFall.Client.json");
        
        // Legacy paths for reading old configs
        private static string LegacySkaterPath => Path.Combine(LegacyDir, "PonceKeybinds.Skater.json");
        private static string LegacyGoaliePath => Path.Combine(LegacyDir, "PonceKeybinds.Goalie.json");
        private static string LegacyClientConfigPath => Path.Combine(LegacyDir, "DashFall.Client.json");
        
        /// <summary>
        /// Check if running on a dedicated server - should not create ModHub folders on server
        /// </summary>
        private static bool IsDedicatedServer()
        {
            return Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }
        
        // Cached client config
        private static DashFallClientConfig _clientConfig;
        public static DashFallClientConfig ClientConfig
        {
            get
            {
                if (_clientConfig == null) _clientConfig = LoadClientConfig();
                return _clientConfig;
            }
        }

        private static void EnsureConfigDir()
        {
            // Never create ModHub folders on dedicated servers
            if (IsDedicatedServer()) return;
            
            if (!Directory.Exists(ModHubDir))
            {
                Directory.CreateDirectory(ModHubDir);
            }
            if (!Directory.Exists(DashFallDir))
            {
                Directory.CreateDirectory(DashFallDir);
            }
        }
        
        /// <summary>
        /// Migrate a config file from legacy location to new ModHub/DashFall location
        /// </summary>
        private static bool TryMigrateLegacyConfig(string legacyPath, string newPath)
        {
            if (File.Exists(legacyPath) && !File.Exists(newPath))
            {
                try
                {
                    EnsureConfigDir();
                    File.Copy(legacyPath, newPath);
                    Debug.Log($"[COMPADJUST] Migrated config from {legacyPath} to {newPath}");
                    
                    // Mark the old file as migrated so we don't try again and other mods know it's done
                    try
                    {
                        string migratedPath = legacyPath + ".migrated";
                        if (File.Exists(migratedPath))
                            File.Delete(migratedPath);
                        File.Move(legacyPath, migratedPath);
                        Debug.Log($"[COMPADJUST] Renamed old file to: {Path.GetFileName(migratedPath)}");
                    }
                    catch (Exception renameEx)
                    {
                        Debug.LogWarning($"[COMPADJUST] Could not rename old file: {renameEx.Message}");
                    }
                    
                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[COMPADJUST] Failed to migrate config: {e.Message}");
                }
            }
            return false;
        }

        public static DashFallClientConfig LoadClientConfig()
        {
            try
            {
                EnsureConfigDir();
                
                // Try to migrate legacy config if needed
                TryMigrateLegacyConfig(LegacyClientConfigPath, ClientConfigPath);
                
                if (File.Exists(ClientConfigPath))
                {
                    var json = File.ReadAllText(ClientConfigPath);
                    var cfg = JsonUtility.FromJson<DashFallClientConfig>(json);
                    if (cfg == null) return NewClientConfig();
                    MigrateClientConfig(cfg);
                    return cfg;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load client config: {e.Message}");
            }
            return NewClientConfig();
        }

        /// <summary>
        /// A fresh config, stamped as current. Without the stamp a brand new install would
        /// be born at version 0 and run every back-compat migration against defaults that
        /// never needed them.
        /// </summary>
        private static DashFallClientConfig NewClientConfig()
            => new DashFallClientConfig { ClientConfigVersion = CurrentClientConfigVersion };

        /// <summary>Current client config schema version. Bump when adding a migration below.</summary>
        private const int CurrentClientConfigVersion = 2;

        /// <summary>
        /// One-time repairs for configs written by older builds, plus the ordering guard
        /// that runs every load.
        /// </summary>
        private static void MigrateClientConfig(DashFallClientConfig cfg)
        {
            // Read BEFORE the stamp below overwrites it. Every version-gated migration in this
            // method has to test against the version the file arrived with, not the one we are
            // about to write.
            int hadVersion = cfg != null ? cfg.ClientConfigVersion : 0;

            // v1 briefly turned the blade spin lock OFF for any config sitting at the old
            // on-at-(-4/+4) default, on the grounds that the pair cancels FreeBlade. That
            // migration is gone: the lock is deliberately ON by default again, so switching
            // it off on load would fight the intended default on every launch.
            //
            // The version field stays and keeps being stamped. It costs nothing, it is the
            // only way a future one-time repair can tell an old file from a current one, and
            // an already-stamped config must not be re-migrated. Anyone whose lock got
            // switched off by the short-lived v1 keeps it off until they toggle it back;
            // that is one click, and silently re-enabling a setting the player may since
            // have chosen deliberately would be worse than leaving it.
            cfg.ClientConfigVersion = CurrentClientConfigVersion;

            // v2 split the single free blade setting into a skater pair and a goalie pair.
            MigrateFreeBladePerRole(cfg, hadVersion);

            // Not versioned: a crossed or out-of-range pair can only come from a hand-edited
            // file, and Mathf.Clamp with min above max does not fail, it collapses the range
            // and freezes the blade. Cheap enough to assert on every load, per role.
            NormalizeFreeBladeRange("skater", ref cfg.FreeBladeSpinMinSkater, ref cfg.FreeBladeSpinMaxSkater);
            NormalizeFreeBladeRange("goalie", ref cfg.FreeBladeSpinMinGoalie, ref cfg.FreeBladeSpinMaxGoalie);
        }


        /// <summary>
        /// Copies a pre-split free blade setting onto both roles, once.
        ///
        /// Gated on the version the file arrived with, not on the values themselves. A player who
        /// had deliberately turned the lock off, or narrowed the range, keeps that on both roles
        /// rather than being silently reset to the new per-role defaults. A file already at
        /// version 2 or later is left alone, because by then the unsuffixed fields are stale.
        /// </summary>
        private static void MigrateFreeBladePerRole(DashFallClientConfig cfg, int hadVersion)
        {
            if (cfg == null || hadVersion >= 2) return;

#pragma warning disable CS0618 // the whole point of this method is to read the obsolete fields
            cfg.FreeBladeSpinLockEnabledSkater = cfg.FreeBladeSpinLockEnabled;
            cfg.FreeBladeSpinLockEnabledGoalie = cfg.FreeBladeSpinLockEnabled;
            cfg.FreeBladeSpinMinSkater = cfg.FreeBladeSpinMin;
            cfg.FreeBladeSpinMinGoalie = cfg.FreeBladeSpinMin;
            cfg.FreeBladeSpinMaxSkater = cfg.FreeBladeSpinMax;
            cfg.FreeBladeSpinMaxGoalie = cfg.FreeBladeSpinMax;

            Debug.Log($"[COMPADJUST] Migrated free blade settings to per-role: lock={cfg.FreeBladeSpinLockEnabled} " +
                      $"range={cfg.FreeBladeSpinMin}..{cfg.FreeBladeSpinMax} copied to both skater and goalie.");
#pragma warning restore CS0618
        }

        /// <summary>
        /// Orders and clamps one role's pair. Not versioned: a crossed or out-of-range pair can
        /// only come from a hand-edited file, and Mathf.Clamp with min above max does not fail, it
        /// collapses the range and freezes the blade. Cheap enough to assert on every load.
        /// </summary>
        private static void NormalizeFreeBladeRange(string role, ref float min, ref float max)
        {
            float lo = Mathf.Clamp(Mathf.Min(min, max), FreeBladeSpinRange.LimitMin, FreeBladeSpinRange.LimitMax);
            float hi = Mathf.Clamp(Mathf.Max(min, max), FreeBladeSpinRange.LimitMin, FreeBladeSpinRange.LimitMax);

            if (!Mathf.Approximately(lo, min) || !Mathf.Approximately(hi, max))
            {
                Debug.LogWarning($"[COMPADJUST] Free blade spin range for {role} {min}..{max} " +
                                 $"is crossed or out of range; using {lo}..{hi}.");
                min = lo;
                max = hi;
            }
        }

        public static void SaveClientConfig(DashFallClientConfig config)
        {
            try
            {
                EnsureConfigDir();
                var json = JsonUtility.ToJson(config, true);
                File.WriteAllText(ClientConfigPath, json);
                _clientConfig = config;
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to save client config: {e.Message}");
            }
        }

        public static SkaterKeybindConfig LoadSkaterConfig()
        {
            try
            {
                EnsureConfigDir();
                
                // Try to migrate legacy config if needed
                TryMigrateLegacyConfig(LegacySkaterPath, SkaterPath);
                
                if (File.Exists(SkaterPath))
                {
                    var json = File.ReadAllText(SkaterPath);
                    var cfg = JsonUtility.FromJson<SkaterKeybindConfig>(json);
                    
                    // Ensure all lists are initialized (old configs may be missing new fields)
                    cfg.divekey = cfg.divekey ?? new List<string>();
                    cfg.dashleftkey = cfg.dashleftkey ?? new List<string>();
                    cfg.dashrightkey = cfg.dashrightkey ?? new List<string>();
                    cfg.twistleftkey = cfg.twistleftkey ?? new List<string>();
                    cfg.twistrightkey = cfg.twistrightkey ?? new List<string>();
                    cfg.slideinfluenceleftkey = cfg.slideinfluenceleftkey ?? new List<string>();
                    cfg.slideinfluencerightkey = cfg.slideinfluencerightkey ?? new List<string>();
                    cfg.slideinfluenceforwardkey = cfg.slideinfluenceforwardkey ?? new List<string>();
                    cfg.slideinfluencebackwardkey = cfg.slideinfluencebackwardkey ?? new List<string>();
                    
                    // Ensure key types have proper defaults (DOUBLE PRESS for twists)
                    if (string.IsNullOrEmpty(cfg.divekeytype)) cfg.divekeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.dashleftkeytype)) cfg.dashleftkeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.dashrightkeytype)) cfg.dashrightkeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.twistleftkeytype)) cfg.twistleftkeytype = "DOUBLE PRESS";
                    if (string.IsNullOrEmpty(cfg.twistrightkeytype)) cfg.twistrightkeytype = "DOUBLE PRESS";
                    if (string.IsNullOrEmpty(cfg.slideinfluenceleftkeytype)) cfg.slideinfluenceleftkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluencerightkeytype)) cfg.slideinfluencerightkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluenceforwardkeytype)) cfg.slideinfluenceforwardkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluencebackwardkeytype)) cfg.slideinfluencebackwardkeytype = "CONTINUOUS";
                    
                    return cfg;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load skater config: {e.Message}");
            }

            // Create and save default config
            var defaultConfig = new SkaterKeybindConfig();
            defaultConfig.divekey.Add("F");
            defaultConfig.dashleftkey.Add("Q");
            defaultConfig.dashrightkey.Add("E");
            defaultConfig.twistleftkey.Add("Z");
            defaultConfig.twistrightkey.Add("C");
            defaultConfig.slideinfluenceleftkey.Add("Z");
            defaultConfig.slideinfluencerightkey.Add("C");
            defaultConfig.slideinfluenceforwardkey.Add("W");
            defaultConfig.slideinfluencebackwardkey.Add("S");
            SaveSkaterConfig(defaultConfig);
            return defaultConfig;
        }

        public static GoalieKeybindConfig LoadGoalieConfig()
        {
            try
            {
                EnsureConfigDir();
                
                // Try to migrate legacy config if needed
                TryMigrateLegacyConfig(LegacyGoaliePath, GoaliePath);
                
                if (File.Exists(GoaliePath))
                {
                    var json = File.ReadAllText(GoaliePath);
                    var cfg = JsonUtility.FromJson<GoalieKeybindConfig>(json);
                    // Ensure all lists are initialized (old configs may be missing new fields)
                    cfg.divekey = cfg.divekey ?? new List<string>();
                    cfg.standingdashleftkey = cfg.standingdashleftkey ?? new List<string>();
                    cfg.standingdashrightkey = cfg.standingdashrightkey ?? new List<string>();
                    cfg.twistleftkey = cfg.twistleftkey ?? new List<string>();
                    cfg.twistrightkey = cfg.twistrightkey ?? new List<string>();
                    cfg.slideinfluenceleftkey = cfg.slideinfluenceleftkey ?? new List<string>();
                    cfg.slideinfluencerightkey = cfg.slideinfluencerightkey ?? new List<string>();
                    cfg.slideinfluenceforwardkey = cfg.slideinfluenceforwardkey ?? new List<string>();
                    cfg.slideinfluencebackwardkey = cfg.slideinfluencebackwardkey ?? new List<string>();
                    
                    // Add default standing dash keys if empty (for configs created before this feature)
                    if (cfg.standingdashleftkey.Count == 0)
                        cfg.standingdashleftkey.Add("Q");
                    if (cfg.standingdashrightkey.Count == 0)
                        cfg.standingdashrightkey.Add("E");
                    
                    // Ensure key types have proper defaults (DOUBLE PRESS for twists)
                    if (string.IsNullOrEmpty(cfg.divekeytype)) cfg.divekeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.standingdashleftkeytype)) cfg.standingdashleftkeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.standingdashrightkeytype)) cfg.standingdashrightkeytype = "PRESS";
                    if (string.IsNullOrEmpty(cfg.twistleftkeytype)) cfg.twistleftkeytype = "DOUBLE PRESS";
                    if (string.IsNullOrEmpty(cfg.twistrightkeytype)) cfg.twistrightkeytype = "DOUBLE PRESS";
                    if (string.IsNullOrEmpty(cfg.slideinfluenceleftkeytype)) cfg.slideinfluenceleftkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluencerightkeytype)) cfg.slideinfluencerightkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluenceforwardkeytype)) cfg.slideinfluenceforwardkeytype = "CONTINUOUS";
                    if (string.IsNullOrEmpty(cfg.slideinfluencebackwardkeytype)) cfg.slideinfluencebackwardkeytype = "CONTINUOUS";
                    
                    return cfg;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load goalie config: {e.Message}");
            }

            // Create and save default config
            var defaultConfig = new GoalieKeybindConfig();
            defaultConfig.divekey.Add("F");
            defaultConfig.standingdashleftkey.Add("Q");
            defaultConfig.standingdashrightkey.Add("E");
            defaultConfig.twistleftkey.Add("Z");
            defaultConfig.twistrightkey.Add("C");
            defaultConfig.slideinfluenceleftkey.Add("Z");
            defaultConfig.slideinfluencerightkey.Add("C");
            defaultConfig.slideinfluenceforwardkey.Add("W");
            defaultConfig.slideinfluencebackwardkey.Add("S");
            SaveGoalieConfig(defaultConfig);
            return defaultConfig;
        }

        public static void SaveSkaterConfig(SkaterKeybindConfig config)
        {
            try
            {
                EnsureConfigDir();
                var json = JsonUtility.ToJson(config, true);
                File.WriteAllText(SkaterPath, json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to save skater config: {e.Message}");
            }
        }

        public static void SaveGoalieConfig(GoalieKeybindConfig config)
        {
            try
            {
                EnsureConfigDir();
                var json = JsonUtility.ToJson(config, true);
                File.WriteAllText(GoaliePath, json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to save goalie config: {e.Message}");
            }
        }
    }
}
