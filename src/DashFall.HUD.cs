// DashFall.HUD.cs - Player state indicator HUD (like Battlefield stance icons)

using System;
using System.Collections.Generic;
using UnityEngine;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    public enum PlayerStateIcon
    {
        None,
        Standing,   // Normal skating
        Dashing,    // Mid-dash
        Crouching,  // Sliding on the ground
        Twisting,   // Twisting while sliding
        Fallen      // Fallen/diving
    }

    public partial class DashFallClientRunner
    {
        // HUD elements
        private UITK.VisualElement _hudRoot;
        private UITK.VisualElement _stateIcon;
        private UITK.VisualElement _stateIconContainer;
        
        // Icon textures
        private Texture2D _iconStanding;
        private Texture2D _iconDashing;
        private Texture2D _iconCrouching;
        private Texture2D _iconTwisting;
        private Texture2D _iconFallen;
        
        // State tracking
        private PlayerStateIcon _currentState = PlayerStateIcon.None;
        private float _dashIconEndTime;
        private float _twistIconEndTime;
        private const float DashIconDuration = 0.3f;
        private const float TwistIconDuration = 0.3f;
        
        // NOTHING IN THIS FILE CURRENTLY RUNS. UpdateHUD's only call site is commented out in
        // DashFall.ClientRunner.cs with a "re-enable when the HUD is ready" TODO, and CleanupHUD's
        // is commented out beside it, so _stateIconContainer never leaves display None. The styling
        // below was brought onto the shared design system along with the rest of the UI so that
        // re-enabling it is a one-line change rather than a second retheme, but read every comment
        // in here as describing what it WOULD do, not what a player sees today.

        // Icon settings
        private const int IconSize = 128;
        private const int IconMargin = 20;

        // The plate borrows PanelBg's neutral tint but not its 245 alpha: this sits on the ice during
        // play, so it has to stay a plate the rink reads through rather than an opaque block. 0.67 is
        // a touch heavier than the old flat black so the tint is actually visible behind the glyph.
        private static readonly Color HudPlateBg =
            new Color(DashFallTheme.PanelBg.r, DashFallTheme.PanelBg.g, DashFallTheme.PanelBg.b, 0.67f);

        // Width of the state band along the bottom edge, borrowed from the tab strip's active
        // underline so the accent reads the same way here as it does in the config panel. It never
        // changes with state, only its colour does, so the glyph's content box cannot shift.
        private const float HudBandWidth = 3f;

        // The plate's own corner radius. Panel internals use 6 and the popup card uses 10, so 8 is
        // deliberately left where it was: nothing in the family dictates a HUD radius.
        private const float HudRadius = 8f;

        private void InitializeHUD()
        {
            LoadIconTextures();
            BuildHUD();
        }
        
        private void LoadIconTextures()
        {
            try
            {
                _iconStanding = LoadEmbeddedIcon("standing.png");
                _iconDashing = LoadEmbeddedIcon("dashing.png");
                _iconCrouching = LoadEmbeddedIcon("crouching.png");
                _iconTwisting = LoadEmbeddedIcon("twisting.png");
                _iconFallen = LoadEmbeddedIcon("fallen.png");
                
                int loaded = (_iconStanding != null ? 1 : 0) + (_iconDashing != null ? 1 : 0) + 
                             (_iconCrouching != null ? 1 : 0) + (_iconTwisting != null ? 1 : 0) + 
                             (_iconFallen != null ? 1 : 0);
                if (ConfigManager.Config.EnableDebugLogs)
                    ConfigManager.Dbg($"HUD: Loaded {loaded}/5 icons from embedded resources");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load icons: {ex.Message}");
            }
        }
        
        private Texture2D LoadEmbeddedIcon(string filename)
        {
            try
            {
                var assembly = GetType().Assembly;
                
                // Try different resource name patterns
                string[] possibleNames = new[]
                {
                    $"DashFallGameMod.playerstateicons.{filename}",
                    $"Dashfall.playerstateicons.{filename}",
                    $"DashFallMod.playerstateicons.{filename}",
                    $"playerstateicons.{filename}",
                    filename
                };
                
                System.IO.Stream stream = null;
                foreach (var name in possibleNames)
                {
                    stream = assembly.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        if (ConfigManager.Config.EnableDebugLogs)
                            ConfigManager.Dbg($"HUD: Found resource: {name}");
                        break;
                    }
                }
                
                if (stream == null)
                {
                    // List available resources for debugging
                    var names = assembly.GetManifestResourceNames();
                    Debug.LogWarning($"[COMPADJUST] Resource not found for {filename}. Available: {string.Join(", ", names)}");
                    return null;
                }
                
                using (stream)
                {
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    return LoadTextureFromBytes(data);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[COMPADJUST] Failed to load embedded icon {filename}: {ex.Message}");
                return null;
            }
        }
        
        private Texture2D LoadTextureFromBytes(byte[] data)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            
            // Use reflection to call ImageConversion.LoadImage to avoid assembly reference issues
            var imgConvType = typeof(Texture2D).Assembly.GetType("UnityEngine.ImageConversion");
            if (imgConvType == null)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    imgConvType = asm.GetType("UnityEngine.ImageConversion");
                    if (imgConvType != null) break;
                }
            }
            
            if (imgConvType != null)
            {
                var loadMethod = imgConvType.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (loadMethod != null)
                {
                    bool success = (bool)loadMethod.Invoke(null, new object[] { tex, data });
                    if (success) return tex;
                }
            }
            
            Debug.LogWarning($"[COMPADJUST] ImageConversion not available");
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        
        private void BuildHUD()
        {
            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root == null) return;
            
            // Container in bottom-left
            _hudRoot = new UITK.VisualElement { name = "DashFall_HUD" };
            _hudRoot.style.position = UITK.Position.Absolute;
            _hudRoot.style.left = 180;
            _hudRoot.style.bottom = IconMargin;
            _hudRoot.style.width = IconSize;
            _hudRoot.style.height = IconSize;
            _hudRoot.pickingMode = UITK.PickingMode.Ignore;
            
            // Icon container with background
            _stateIconContainer = new UITK.VisualElement();
            _stateIconContainer.style.width = IconSize;
            _stateIconContainer.style.height = IconSize;
            _stateIconContainer.style.backgroundColor = HudPlateBg;
            DashFallTheme.SetUniformRadius(_stateIconContainer, HudRadius);

            // Every surface in the shared system is a fill plus a 1px edge, and the edge matters more
            // out here than it does in the panel: without it a translucent plate has no boundary
            // against bright ice. Border width is drawn inside the box, so the 128 square set above is
            // still the outer footprint and the glyph below just scales to the smaller content box.
            DashFallTheme.SetUniformBorder(_stateIconContainer, 1f, DashFallTheme.PanelBorder);
            _stateIconContainer.style.borderBottomWidth = HudBandWidth;
            _stateIconContainer.style.borderBottomColor = StateBandColor(_currentState);

            _stateIconContainer.style.display = UITK.DisplayStyle.None;
            _stateIconContainer.pickingMode = UITK.PickingMode.Ignore;
            _hudRoot.Add(_stateIconContainer);
            
            // The actual icon image
            _stateIcon = new UITK.VisualElement();
            _stateIcon.style.width = new UITK.Length(100, UITK.LengthUnit.Percent);
            _stateIcon.style.height = new UITK.Length(100, UITK.LengthUnit.Percent);
            // Use background-size instead of deprecated unityBackgroundScaleMode
            _stateIcon.style.backgroundPositionX = new UITK.BackgroundPosition(UITK.BackgroundPositionKeyword.Center);
            _stateIcon.style.backgroundPositionY = new UITK.BackgroundPosition(UITK.BackgroundPositionKeyword.Center);
            _stateIcon.style.backgroundRepeat = new UITK.BackgroundRepeat(UITK.Repeat.NoRepeat, UITK.Repeat.NoRepeat);
            _stateIcon.style.backgroundSize = new UITK.BackgroundSize(UITK.BackgroundSizeType.Contain);
            _stateIcon.pickingMode = UITK.PickingMode.Ignore;
            _stateIconContainer.Add(_stateIcon);
            
            root.Add(_hudRoot);
        }
        
        private void UpdateHUD()
        {
            if (_hudRoot == null || _stateIconContainer == null) return;
            
            var newState = DetermineCurrentState();
            
            if (newState != _currentState)
            {
                _currentState = newState;
                UpdateIconDisplay();
            }
        }
        
        private PlayerStateIcon DetermineCurrentState()
        {
            // Get local player
            var localPlayer = GetLocalPlayer();
            if (localPlayer == null || localPlayer.PlayerBody == null)
                return PlayerStateIcon.None;
            
            var body = localPlayer.PlayerBody;
            float now = Time.time;
            
            // Check diving/fallen first (from DiveMod state)
            if (DiveMod.IsPlayerDiving(localPlayer.NetworkObjectId))
                return PlayerStateIcon.Fallen;
            
            // Check if sliding
            if (body.IsSliding.Value)
            {
                // Check if twisting while sliding (show briefly after twist)
                if (now < _twistIconEndTime)
                    return PlayerStateIcon.Twisting;
                    
                return PlayerStateIcon.Crouching;
            }
            
            // Check if just dashed (show briefly after dash)
            if (now < _dashIconEndTime)
                return PlayerStateIcon.Dashing;
            
            // Default - standing/skating
            // Only show if moving at decent speed
            var rb = body.GetComponent<Rigidbody>();
            if (rb != null && rb.linearVelocity.magnitude > 2f)
                return PlayerStateIcon.Standing;
            
            return PlayerStateIcon.None;
        }
        
        private void UpdateIconDisplay()
        {
            if (_stateIconContainer == null || _stateIcon == null) return;
            
            Texture2D tex = GetIconForState(_currentState);
            
            if (tex == null || _currentState == PlayerStateIcon.None)
            {
                _stateIconContainer.style.display = UITK.DisplayStyle.None;
            }
            else
            {
                _stateIconContainer.style.display = UITK.DisplayStyle.Flex;
                _stateIconContainer.style.borderBottomColor = StateBandColor(_currentState);
                _stateIcon.style.backgroundImage = new UITK.StyleBackground(tex);
            }
        }

        // Paints the bottom band for a state. This is the only per-state styling in the HUD and it
        // rides UpdateIconDisplay, which only runs when the state actually changes, so nothing here
        // costs a frame. The ramp stays inside the mod's two hues: quiet accent while the skater is
        // simply upright, full accent while one of our abilities is mid-flight, and Danger once they
        // are down, which is the one state the player needs to read without looking at the glyph.
        private static Color StateBandColor(PlayerStateIcon state)
        {
            switch (state)
            {
                case PlayerStateIcon.Dashing:
                case PlayerStateIcon.Twisting:
                    return DashFallTheme.Accent;
                case PlayerStateIcon.Fallen:
                    return DashFallTheme.Danger;
                default:
                    return DashFallTheme.AccentDim;
            }
        }

        private Texture2D GetIconForState(PlayerStateIcon state)
        {
            switch (state)
            {
                case PlayerStateIcon.Standing: return _iconStanding;
                case PlayerStateIcon.Dashing: return _iconDashing;
                case PlayerStateIcon.Crouching: return _iconCrouching;
                case PlayerStateIcon.Twisting: return _iconTwisting;
                case PlayerStateIcon.Fallen: return _iconFallen;
                default: return null;
            }
        }
        
        private Player GetLocalPlayer()
        {
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (p != null && p.IsOwner)
                        return p;
                }
            }
            catch { }
            return null;
        }
        
        // Called by DashMod when a dash occurs
        public void OnLocalDash()
        {
            _dashIconEndTime = Time.time + DashIconDuration;
        }
        
        // Called when local player twists while sliding
        public void OnLocalTwist()
        {
            _twistIconEndTime = Time.time + TwistIconDuration;
        }
        
        private void CleanupHUD()
        {
            if (_hudRoot != null)
            {
                _hudRoot.RemoveFromHierarchy();
                _hudRoot = null;
            }
            
            // Cleanup textures
            if (_iconStanding != null) UnityEngine.Object.Destroy(_iconStanding);
            if (_iconDashing != null) UnityEngine.Object.Destroy(_iconDashing);
            if (_iconCrouching != null) UnityEngine.Object.Destroy(_iconCrouching);
            if (_iconTwisting != null) UnityEngine.Object.Destroy(_iconTwisting);
            if (_iconFallen != null) UnityEngine.Object.Destroy(_iconFallen);
        }
    }
}
