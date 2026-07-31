// DashFall.Config.cs - DashFall's own keybind configuration

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DashFallMod.Client
{
    [Serializable]
    public class DashFallClientConfig
    {
        public bool EnableClientDebug = false;
        public bool ShowArenaClipBrushes = false; // debug: visualise arena collider meshes
        public bool ShowPlayerClipBrushes = false; // debug: visualise player collider meshes
        public bool EnableMinimapTweaks = false;    // apply arena-scale minimap rescaling
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
        public float PuckScale = 1f;
        // Per-axis multipliers applied on top of PuckScale (server-synced).
        // Final localScale = PuckScale * (PuckScaleX, PuckScaleY, PuckScaleZ).
        public float PuckScaleX = 1f;
        public float PuckScaleY = 1f;
        public float PuckScaleZ = 1f;
        public float ButterflyPadOffset = 0f;
        public bool FreeBladeSpinLockEnabled = true;
        public float FreeBladeSpinMin = -4f;
        public float FreeBladeSpinMax = 4f;
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
                    return cfg ?? new DashFallClientConfig();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load client config: {e.Message}");
            }
            return new DashFallClientConfig();
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
