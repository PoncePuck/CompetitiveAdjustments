// DashFall.UI.cs - Full UI Panel with keybind editing (copied from PlayerInput style)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner
    {
        // UI Toolkit elements
        private UITK.VisualElement _dfPanel;
        private UITK.VisualElement _dfBackdrop;
        private UITK.VisualElement _captureOverlay;
        private UITK.Label _captureLabel;
        private UITK.ScrollView _scrollView;
        private UITK.VisualElement _actionsSection;

        // Search/filter: a persistent box above the scroll view that hides rows on
        // the active tab whose label does not contain the query.
        private UITK.TextField _searchField;
        private string _searchQuery = "";
        
        // Tab system
        private UITK.Button _skaterTabBtn;
        private UITK.Button _goalieTabBtn;
        private UITK.Button _serverTabBtn;
        private UITK.Button _settingsTabBtn;
        private enum ActiveTab { Skater, Goalie, Server, Settings }
        private ActiveTab _activeTab = ActiveTab.Skater;

        // Admin config editor (SERVER tab) state
        private bool _serverAutoAuthSent;   // empty-password auto-unlock attempted this panel session
        private bool _serverUserLocked;     // user pressed LOCK to drop back to read-only locally
        private string _serverStatusText = ""; // transient status under the lock bar
        private bool _serverPasswordRevealed; // host pressed SHOW to reveal the editor password
        private bool _serverResetArmed;       // RESET pressed once; waiting for the confirm press
        private CompetitiveAdjustments.ServerConfig _serverEditCfg; // isolated editor copy; null = re-clone from live
        
        private Action<string> _onChordCaptured;
        private bool _panelHiddenForCapture;
        private readonly List<UITK.VisualElement> _hiddenMenuButtons = new List<UITK.VisualElement>();

        // UI palette (matching base game)
        private static readonly Color32 TextFieldBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 RowBg = new Color32(61, 61, 61, 255);
        private static readonly Color32 DisabledRowBg = new Color32(40, 40, 40, 255);
        private static readonly Color32 PanelBg = new Color32(48, 48, 47, 255);
        private static readonly Color32 TabActiveBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 TabInactiveBg = new Color32(66, 66, 66, 255);
        private const int BTN_W = 80;

        // Font
        private static Font _uiFont;
        private static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try { _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            if (_uiFont == null)
            {
                try { _uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI" }, 16); } catch { }
            }
            return _uiFont;
        }
        
        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont();
            if (f != null) ve.style.unityFont = f;
        }

        private static void MakeReadable(UITK.Label l)
        {
            l.style.color = Color.white;
            ForceUIFont(l);
        }

        private static void MakeReadable(UITK.Button b)
        {
            b.style.color = Color.white;
            ForceUIFont(b);
        }

        // PoncePlayerInput / PlayerQOL toggle look: recolor the checkbox frame to
        // a dark fill with a medium-gray border so it reads clearly against the
        // dark rows.  The default Unity USS draws a light box that disappears
        // against this panel, which is why the SERVER toggles looked blank.
        // Applied on AttachToPanel because the inner ".unity-toggle__input"
        // element only exists once the toggle is parented.
        private static void StyleConfigCheckbox(UITK.Toggle toggle)
        {
            if (toggle == null) return;
            toggle.RegisterCallback<UITK.AttachToPanelEvent>(_ =>
            {
                var input = toggle.Q(className: "unity-toggle__input");
                if (input == null) return;
                input.style.backgroundColor   = new UITK.StyleColor(new Color(0.15f, 0.15f, 0.15f));
                input.style.borderTopColor    = new UITK.StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderBottomColor = new UITK.StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderLeftColor   = new UITK.StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderRightColor  = new UITK.StyleColor(new Color(0.4f, 0.4f, 0.4f));
            });
        }

        // Tags a row so the SEARCH box can show/hide it by its label text.  The
        // searchable text is stored in userData; the "cfg-row" class lets
        // ApplySearchFilter collect every row regardless of which tab or container
        // built it.
        private static void MarkSearchable(UITK.VisualElement row, string title)
        {
            if (row == null) return;
            row.userData = title ?? "";
            row.AddToClassList("cfg-row");
        }

        // Filters the active tab's rows by the SEARCH query.  An empty query shows
        // everything; a non-empty query hides non-matching rows and the section
        // headers, so the results read as one flat list.  Re-run after every
        // BuildActionsUI so a rebuilt tab keeps the current filter.
        private void ApplySearchFilter()
        {
            if (_actionsSection == null) return;
            string q = (_searchQuery ?? "").Trim().ToLowerInvariant();
            bool searching = q.Length > 0;

            foreach (var row in _actionsSection.Query(className: "cfg-row").ToList())
            {
                string title = (row.userData as string ?? "").ToLowerInvariant();
                bool match = !searching || title.Contains(q);
                row.style.display = match ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            }

            foreach (var header in _actionsSection.Query(className: "cfg-header").ToList())
                header.style.display = searching ? UITK.DisplayStyle.None : UITK.DisplayStyle.Flex;
        }

        // ========== PANEL BUILD ==========
        private void BuildDashFallPanel()
        {
            if (_dfPanel != null) return;

            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root == null) return;

            // Backdrop (semi-transparent overlay)
            _dfBackdrop = new UITK.VisualElement { name = "DashFall_Backdrop" };
            _dfBackdrop.style.position = UITK.Position.Absolute;
            _dfBackdrop.style.left = 0;
            _dfBackdrop.style.top = 0;
            _dfBackdrop.style.right = 0;
            _dfBackdrop.style.bottom = 0;
            _dfBackdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0.0f));
            _dfBackdrop.style.display = UITK.DisplayStyle.None;
            _dfBackdrop.pickingMode = UITK.PickingMode.Position;
            _dfBackdrop.RegisterCallback<UITK.PointerUpEvent>(e =>
            {
                // Close panel when clicking backdrop
                CloseDashFallPanel();
            });

            // Main panel
            _dfPanel = new UITK.VisualElement { name = "DashFall_Panel" };
            _dfPanel.style.position = UITK.Position.Absolute;
            _dfPanel.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            _dfPanel.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            _dfPanel.style.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
            int targetW = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 680, 980);
            _dfPanel.style.width = targetW;
            _dfPanel.style.height = new UITK.Length(84, UITK.LengthUnit.Percent);
            _dfPanel.style.minHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
            _dfPanel.style.maxHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
            _dfPanel.style.overflow = UITK.Overflow.Hidden;
            _dfPanel.style.flexDirection = UITK.FlexDirection.Column;
            _dfPanel.style.backgroundColor = new UITK.StyleColor(PanelBg);
            _dfPanel.style.paddingLeft = 8; _dfPanel.style.paddingRight = 8;
            _dfPanel.style.paddingTop = 8; _dfPanel.style.paddingBottom = 8;
            _dfPanel.style.display = UITK.DisplayStyle.None;
            _dfPanel.pickingMode = UITK.PickingMode.Position;
            _dfPanel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());

            // Title
            var bigTitle = new UITK.Label("COMPADJUST");
            bigTitle.style.fontSize = 50;
            bigTitle.style.marginBottom = 16;
            MakeReadable(bigTitle);
            _dfPanel.Add(bigTitle);

            // Tab bar
            var tabBar = new UITK.VisualElement();
            tabBar.style.flexDirection = UITK.FlexDirection.Row;
            tabBar.style.marginBottom = 8;
            tabBar.style.height = 50;

            _skaterTabBtn = MakeTabButton("SKATER", true, () => SwitchToTab(ActiveTab.Skater));
            _goalieTabBtn = MakeTabButton("GOALIE", false, () => SwitchToTab(ActiveTab.Goalie));
            _serverTabBtn = MakeTabButton("SERVER", false, () => SwitchToTab(ActiveTab.Server));
            _settingsTabBtn = MakeTabButton("SETTINGS", false, () => SwitchToTab(ActiveTab.Settings));
            // MakeTabButton gives every tab an 8px right margin for spacing; the
            // last tab must not, or SETTINGS sits 8px off the right edge while
            // SKATER hugs the left.  Zero it so both ends are flush.
            _settingsTabBtn.style.marginRight = 0;

            tabBar.Add(_skaterTabBtn);
            tabBar.Add(_goalieTabBtn);
            tabBar.Add(_serverTabBtn);
            tabBar.Add(_settingsTabBtn);
            _dfPanel.Add(tabBar);

            // Search box: filters the rows on the active tab by label text.  It
            // sits above the scroll view so it stays put while the list scrolls.
            // Styled as a row (RowBg + 12px inset) so SEARCH lines up with the
            // row labels below it instead of sitting flush against the panel edge.
            var searchRow = new UITK.VisualElement();
            searchRow.style.flexDirection = UITK.FlexDirection.Row;
            searchRow.style.alignItems = UITK.Align.Center;
            searchRow.style.flexShrink = 0;
            searchRow.style.height = 50;
            searchRow.style.marginBottom = 8;
            searchRow.style.paddingLeft = 12;
            searchRow.style.paddingRight = 12;
            searchRow.style.backgroundColor = new UITK.StyleColor(RowBg);

            var searchLabel = new UITK.Label("SEARCH");
            searchLabel.style.fontSize = 18;
            searchLabel.style.marginRight = 8;
            MakeReadable(searchLabel);
            searchRow.Add(searchLabel);

            _searchField = new TextField();
            _searchField.value = _searchQuery;
            _searchField.style.flexGrow = 1;
            _searchField.style.height = 34;
            _searchField.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            _searchField.style.color = Color.white;
            ForceUIFont(_searchField);
            _searchField.RegisterValueChangedCallback(e =>
            {
                _searchQuery = e.newValue ?? "";
                ApplySearchFilter();
            });
            searchRow.Add(_searchField);
            _dfPanel.Add(searchRow);

            // Scroll view for content
            _scrollView = new UITK.ScrollView
            {
                verticalScrollerVisibility = UITK.ScrollerVisibility.Auto,
                horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden
            };
            _scrollView.style.flexGrow = 1;
            // A flex item's default min-height is its content size, so a long list
            // keeps the scroll view tall and pushes the footer past the panel's
            // clipped bottom (the buttons then overlap the window edge).  Pin
            // min-height to 0 so the scroll view shrinks and the footer stays in.
            _scrollView.style.flexShrink = 1;
            _scrollView.style.minHeight = 0;
            _dfPanel.Add(_scrollView);

            _actionsSection = new UITK.VisualElement();
            _scrollView.Add(_actionsSection);

            // Build the action rows
            BuildActionsUI();

                // PlayerQoL footer buttons: no bottom margins of their own - the
                // 8px gap under the row comes from the panel's bottom padding.
                UITK.Button MakeDonateButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.paddingLeft = 18; b.style.paddingRight = 18;
                    b.style.backgroundColor = new UITK.StyleColor(RowBg);
                    MakeReadable(b);
                    AddButtonFlash(b);
                    return b;
                }
                UITK.Button MakeResetButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.marginLeft = 8;
                    b.style.paddingLeft = 18; b.style.paddingRight = 18;
                    b.style.backgroundColor = new UITK.StyleColor(RowBg);
                    MakeReadable(b);
                    AddButtonFlash(b);
                    return b;
                }
                UITK.Button MakeCloseButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.marginLeft = 8;
                    b.style.paddingLeft = 18; b.style.paddingRight = 182;
                    b.style.backgroundColor = new UITK.StyleColor(RowBg);
                    MakeReadable(b);
                    AddButtonFlash(b);
                    return b;
                }

            var donate = MakeDonateButton("COFFEE?", () =>
            {
                Application.OpenURL("https://buymeacoffee.com/amikiir");
            });

            var resetBtn = MakeResetButton("RESET TO DEFAULTS", () =>
            {
                ResetToDefaults();
                ResetInputActions();
                RefreshActionsUI();
            });

            var closeBtn = MakeCloseButton("CLOSE", () =>
            {
                DashFallConfigLoader.SaveSkaterConfig(_skater);
                DashFallConfigLoader.SaveGoalieConfig(_goalie);
                RebuildLookups();
                ResetInputActions();
                CloseDashFallPanel();
            });

            // Button row at bottom (PlayerQoL layout): COFFEE alone on the left;
            // RESET + CLOSE grouped together on the right.
            var buttonRow = new UITK.VisualElement();
            buttonRow.style.flexDirection = UITK.FlexDirection.Row;
            buttonRow.style.justifyContent = UITK.Justify.SpaceBetween;
            buttonRow.style.marginTop = 8;
            buttonRow.style.flexShrink = 0;   // footer keeps its size; the list shrinks instead
            buttonRow.Add(donate);
            var rightButtons = new UITK.VisualElement();
            rightButtons.style.flexDirection = UITK.FlexDirection.Row;
            rightButtons.Add(resetBtn);
            rightButtons.Add(closeBtn);
            buttonRow.Add(rightButtons);
            _dfPanel.Add(buttonRow);

            // Add to root
            root.Add(_dfBackdrop);
            _dfBackdrop.Add(_dfPanel);
        }

        private UITK.Button MakeTabButton(string text, bool isActive, Action onClick)
        {
            var btn = new UITK.Button(onClick) { text = text };
            btn.style.height = 50;
            btn.style.flexGrow = 1;
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.style.marginRight = 8;
            // Spacing below the tab strip is owned by tabBar.marginBottom; the
            // button keeps no bottom margin of its own (it used to stack a second
            // gap and bleed past the strip into the search row).
            btn.style.marginBottom = 0;
            btn.style.fontSize = 24;
            btn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            btn.style.borderTopLeftRadius = 6;
            btn.style.borderTopRightRadius = 6;
            btn.style.borderBottomLeftRadius = 0;
            btn.style.borderBottomRightRadius = 0;
            btn.style.borderBottomWidth = isActive ? 3 : 0;
            btn.style.borderBottomColor = new UITK.StyleColor(Color.white);
            btn.style.backgroundColor = new UITK.StyleColor(isActive ? TabActiveBg : TabInactiveBg);
            btn.style.color = isActive ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            ForceUIFont(btn);
            
            // Add hover effect - white background on hover (unless active)
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ => {
                // Check if this tab is currently active by checking border
                float borderWidth = btn.resolvedStyle.borderBottomWidth;
                if (borderWidth < 1)
                {
                    btn.style.backgroundColor = new UITK.StyleColor(Color.white);
                    btn.style.color = Color.black;
                }
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ => {
                // Restore based on active state (check border)
                float borderWidth = btn.resolvedStyle.borderBottomWidth;
                if (borderWidth < 1)
                {
                    btn.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
                    btn.style.color = new Color(0.7f, 0.7f, 0.7f);
                }
            });
            
            return btn;
        }

        private void SwitchToTab(ActiveTab tab)
        {
            _serverResetArmed = false; // leaving the tab cancels a pending reset confirm
            _activeTab = tab;
            UpdateTabStyles();
            RefreshActionsUI();
        }

        private void UpdateTabStyles()
        {
            if (_skaterTabBtn != null)
            {
                bool active = _activeTab == ActiveTab.Skater;
                _skaterTabBtn.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
                _skaterTabBtn.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                _skaterTabBtn.style.borderBottomWidth = active ? 3 : 0;
            }
            if (_goalieTabBtn != null)
            {
                bool active = _activeTab == ActiveTab.Goalie;
                _goalieTabBtn.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
                _goalieTabBtn.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                _goalieTabBtn.style.borderBottomWidth = active ? 3 : 0;
            }
            if (_serverTabBtn != null)
            {
                bool active = _activeTab == ActiveTab.Server;
                _serverTabBtn.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
                _serverTabBtn.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                _serverTabBtn.style.borderBottomWidth = active ? 3 : 0;
            }
            if (_settingsTabBtn != null)
            {
                bool active = _activeTab == ActiveTab.Settings;
                _settingsTabBtn.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
                _settingsTabBtn.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                _settingsTabBtn.style.borderBottomWidth = active ? 3 : 0;
            }
        }

        private void DontWrap(UITK.Label l)
        {
            l.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            l.style.textOverflow = UITK.TextOverflow.Ellipsis;
        }

        private void BuildActionsUI()
        {
            _actionsSection.Clear();
            
            // Get server features (if connected)
            var features = PoncePuck.Keybinds.ServerBridge.ReceivedFeatures;
            bool hasFeatures = PoncePuck.Keybinds.ServerBridge.HasReceivedFeatures;

            if (_activeTab == ActiveTab.Skater)
            {
                // Skater section
                _actionsSection.Add(MakeBindRow("DIVE", () => _skater.divekey, v => _skater.divekey = v,
                    () => _skater.divekeytype, v => _skater.divekeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterDiveEnabled));
                _actionsSection.Add(MakeBindRow("TWIST LEFT", () => _skater.twistleftkey, v => _skater.twistleftkey = v,
                    () => _skater.twistleftkeytype, v => _skater.twistleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterTwistEnabled));
                _actionsSection.Add(MakeBindRow("TWIST RIGHT", () => _skater.twistrightkey, v => _skater.twistrightkey = v,
                    () => _skater.twistrightkeytype, v => _skater.twistrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterTwistEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI LEFT", () => _skater.slideinfluenceleftkey, v => _skater.slideinfluenceleftkey = v,
                    () => _skater.slideinfluenceleftkeytype, v => _skater.slideinfluenceleftkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI RIGHT", () => _skater.slideinfluencerightkey, v => _skater.slideinfluencerightkey = v,
                    () => _skater.slideinfluencerightkeytype, v => _skater.slideinfluencerightkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI FORWARD", () => _skater.slideinfluenceforwardkey, v => _skater.slideinfluenceforwardkey = v,
                    () => _skater.slideinfluenceforwardkeytype, v => _skater.slideinfluenceforwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI BACKWARD", () => _skater.slideinfluencebackwardkey, v => _skater.slideinfluencebackwardkey = v,
                    () => _skater.slideinfluencebackwardkeytype, v => _skater.slideinfluencebackwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
            }
            else if (_activeTab == ActiveTab.Goalie)
            {
                // Goalie section
                _actionsSection.Add(MakeBindRow("DIVE", () => _goalie.divekey, v => _goalie.divekey = v,
                    () => _goalie.divekeytype, v => _goalie.divekeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieDiveEnabled));
                _actionsSection.Add(MakeBindRow("STANDING DASH LEFT", () => _goalie.standingdashleftkey, v => _goalie.standingdashleftkey = v,
                    () => _goalie.standingdashleftkeytype, v => _goalie.standingdashleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieStandingDashEnabled));
                _actionsSection.Add(MakeBindRow("STANDING DASH RIGHT", () => _goalie.standingdashrightkey, v => _goalie.standingdashrightkey = v,
                    () => _goalie.standingdashrightkeytype, v => _goalie.standingdashrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieStandingDashEnabled));
                _actionsSection.Add(MakeBindRow("TWIST LEFT", () => _goalie.twistleftkey, v => _goalie.twistleftkey = v,
                    () => _goalie.twistleftkeytype, v => _goalie.twistleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieTwistEnabled));
                _actionsSection.Add(MakeBindRow("TWIST RIGHT", () => _goalie.twistrightkey, v => _goalie.twistrightkey = v,
                    () => _goalie.twistrightkeytype, v => _goalie.twistrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieTwistEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI LEFT", () => _goalie.slideinfluenceleftkey, v => _goalie.slideinfluenceleftkey = v,
                    () => _goalie.slideinfluenceleftkeytype, v => _goalie.slideinfluenceleftkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI RIGHT", () => _goalie.slideinfluencerightkey, v => _goalie.slideinfluencerightkey = v,
                    () => _goalie.slideinfluencerightkeytype, v => _goalie.slideinfluencerightkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI FORWARD", () => _goalie.slideinfluenceforwardkey, v => _goalie.slideinfluenceforwardkey = v,
                    () => _goalie.slideinfluenceforwardkeytype, v => _goalie.slideinfluenceforwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                _actionsSection.Add(MakeBindRow("SLIDE DI BACKWARD", () => _goalie.slideinfluencebackwardkey, v => _goalie.slideinfluencebackwardkey = v,
                    () => _goalie.slideinfluencebackwardkeytype, v => _goalie.slideinfluencebackwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
            }
            else if (_activeTab == ActiveTab.Server)
            {
                // Server config display
                BuildServerConfigUI();
            }
            else if (_activeTab == ActiveTab.Settings)
            {
                // Settings tab
                BuildSettingsUI();
            }

            // Re-apply the active search filter to the freshly built rows.
            ApplySearchFilter();
        }

        // Reflection-driven, admin-gated live editor for the whole server config.
        // Edits mutate the local ConfigManager.Config mirror only; nothing leaves
        // this client until SAVE & APPLY.  Non-admins see the same editor greyed
        // out behind a lock bar.
        private void BuildServerConfigUI()
        {
            bool hasFeatures = PoncePuck.Keybinds.ServerBridge.HasReceivedFeatures;
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool isServer = nm != null && nm.IsServer;

            if (!hasFeatures && !isServer)
            {
                var noDataLabel = new UITK.Label("Not connected to a server with CompetitiveAdjustments.");
                noDataLabel.style.fontSize = 24;
                noDataLabel.style.marginTop = 20;
                noDataLabel.style.marginBottom = 20;
                noDataLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                MakeReadable(noDataLabel);
                _actionsSection.Add(noDataLabel);
                return;
            }

            // The host is server-authoritative; a remote client is unlocked once
            // the server granted it.  A local LOCK press forces read-only.
            bool authed = isServer || PoncePuck.Keybinds.ServerBridge.AdminUnlocked;
            bool unlocked = authed && !_serverUserLocked;

            // Auto-unlock attempt (covers Steam allowlist and OpenConfigChanges):
            // on first build of this tab, ask the server with an empty password.
            if (!authed && !isServer && hasFeatures && !_serverUserLocked && !_serverAutoAuthSent)
            {
                _serverAutoAuthSent = true;
                PoncePuck.Keybinds.ServerBridge.SendAdminAuth("");
            }

            BuildServerLockBar(unlocked, authed, isServer);

            // The host always has the live config; a client needs the broadcast.
            bool hasFullConfig = isServer || PoncePuck.Keybinds.ServerBridge.HasReceivedFullConfig;
            if (!hasFullConfig)
            {
                // Nudge the server now (the Update loop also retries every 2s) so
                // opening the tab pulls the config promptly instead of waiting.
                PoncePuck.Keybinds.ServerBridge.RequestConfigFull();

                var waitLabel = new UITK.Label("Waiting for server config...");
                waitLabel.style.fontSize = 20;
                waitLabel.style.marginTop = 16;
                waitLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                MakeReadable(waitLabel);
                _actionsSection.Add(waitLabel);
                return;
            }

            // Edit an isolated clone, not the live mirror, so in-progress edits
            // never leak into the client's Effective config reads (e.g. toggling
            // a master enable would otherwise change local behavior before SAVE)
            // and RESET survives a rebuild.  Committed to the live config only on
            // SAVE.  Persists across tab switches; re-cloned on panel open and
            // whenever a fresh full config arrives.
            if (_serverEditCfg == null)
                _serverEditCfg = CloneServerCfgForEdit();
            var cfg = _serverEditCfg;
            if (cfg == null) return;

            // Editor body: greyed out and disabled when locked.
            var body = new UITK.VisualElement();
            body.SetEnabled(unlocked);
            body.style.opacity = unlocked ? 1f : 0.45f;

            // SAVE & APPLY / RESET pinned at the top of the editor, directly under
            // the lock bar, so they are reachable without scrolling past every
            // section to the bottom of a long list.
            var btnRow = new UITK.VisualElement();
            btnRow.style.flexDirection = UITK.FlexDirection.Row;
            btnRow.style.marginTop = 4;
            btnRow.style.marginBottom = 8;
            btnRow.Add(MakeServerEditorButton("SAVE & APPLY", OnServerSaveApply));
            btnRow.Add(MakeServerEditorButton("EXPORT", OnServerExport));
            var resetBtn = MakeServerEditorButton(_serverResetArmed ? "CONFIRM RESET" : "RESET TO DEFAULTS", OnServerResetDefaults);
            if (_serverResetArmed) // tint red on the armed/confirm press
                resetBtn.style.backgroundColor = new UITK.StyleColor(new Color(0.6f, 0.25f, 0.25f));
            btnRow.Add(resetBtn);
            body.Add(btnRow);

            AddSectionHeaderTo(body, "MASTER ENABLES");
            body.Add(MakeEditorToggleRow("Enable Dashfall", cfg.EnableDashfall, v => cfg.EnableDashfall = v));
            body.Add(MakeEditorToggleRow("Enable CompAdjust", cfg.EnableCompAdjust, v => cfg.EnableCompAdjust = v));
            body.Add(MakeEditorToggleRow("Enable CompTweaks", cfg.EnableCompTweaks, v => cfg.EnableCompTweaks = v));

            AddSectionHeaderTo(body, "DASHFALL");
            BuildEditableSection(body, cfg.Dashfall);
            AddSectionHeaderTo(body, "COMPADJUST");
            BuildEditableSection(body, cfg.CompAdjust);
            AddSectionHeaderTo(body, "COMPTWEAKS");
            BuildEditableSection(body, cfg.CompTweaks);

            _actionsSection.Add(body);
        }

        // Lock/status bar at the top of the SERVER tab.
        private void BuildServerLockBar(bool unlocked, bool authed, bool isServer)
        {
            var bar = new UITK.VisualElement();
            bar.style.flexDirection = UITK.FlexDirection.Column;
            bar.style.marginTop = 4;
            bar.style.marginBottom = 8;
            bar.style.paddingLeft = 12; bar.style.paddingRight = 12;
            bar.style.paddingTop = 8; bar.style.paddingBottom = 8;
            bar.style.backgroundColor = new UITK.StyleColor(RowBg);
            bar.style.borderTopLeftRadius = 4; bar.style.borderTopRightRadius = 4;
            bar.style.borderBottomLeftRadius = 4; bar.style.borderBottomRightRadius = 4;

            var topRow = new UITK.VisualElement();
            topRow.style.flexDirection = UITK.FlexDirection.Row;
            topRow.style.alignItems = UITK.Align.Center;

            var title = new UITK.Label(unlocked ? "ADMIN EDITOR - UNLOCKED" : "ADMIN EDITOR - LOCKED");
            title.style.fontSize = 22;
            title.style.flexGrow = 1;
            MakeReadable(title);
            // Set the lock-state color AFTER MakeReadable, which forces white;
            // otherwise the green/orange coding here is silently overwritten.
            title.style.color = unlocked ? new Color(0.5f, 0.9f, 0.5f) : new Color(0.95f, 0.75f, 0.4f);
            topRow.Add(title);

            if (unlocked)
            {
                // Drop back to read-only locally without disconnecting.
                topRow.Add(MakeCompactButton("LOCK", () =>
                {
                    _serverUserLocked = true;
                    RefreshActionsUI();
                }));
            }
            else if (authed)
            {
                // Locked only because the user pressed LOCK; no password needed.
                topRow.Add(MakeCompactButton("UNLOCK", () =>
                {
                    _serverUserLocked = false;
                    RefreshActionsUI();
                }));
            }
            bar.Add(topRow);

            // The host owns the live config, so show the shareable editor
            // password here; handing it to a non-admin lets them unlock the
            // editor without being a game admin.
            if (isServer)
            {
                string pwd = CompetitiveAdjustments.ConfigManager.Config?.Admin?.EditorPassword ?? "";
                if (!string.IsNullOrEmpty(pwd))
                {
                    var pwRow = new UITK.VisualElement();
                    pwRow.style.flexDirection = UITK.FlexDirection.Row;
                    pwRow.style.alignItems = UITK.Align.Center;
                    pwRow.style.marginTop = 8;

                    // Masked by default (fixed length, so it does not even leak how
                    // long the password is) until the host presses SHOW.
                    string shown = _serverPasswordRevealed ? pwd : "••••••••";
                    var pwLabel = new UITK.Label("Editor password: " + shown);
                    pwLabel.style.fontSize = 16;
                    pwLabel.style.flexGrow = 1;
                    MakeReadable(pwLabel);
                    pwRow.Add(pwLabel);

                    pwRow.Add(MakeCompactButton(_serverPasswordRevealed ? "HIDE" : "SHOW", () =>
                    {
                        _serverPasswordRevealed = !_serverPasswordRevealed;
                        RefreshActionsUI();
                    }));

                    bar.Add(pwRow);
                }
            }

            // Genuinely un-authed: offer a password prompt.
            if (!authed && !isServer)
            {
                var entry = new UITK.VisualElement();
                entry.style.flexDirection = UITK.FlexDirection.Row;
                entry.style.alignItems = UITK.Align.Center;
                entry.style.marginTop = 8;

                var pw = new TextField { isPasswordField = true };
                pw.style.flexGrow = 1;
                pw.style.height = 34;
                pw.style.marginRight = 8;
                pw.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
                pw.style.color = Color.white;
                ForceUIFont(pw);
                entry.Add(pw);

                entry.Add(MakeCompactButton("UNLOCK", () =>
                {
                    _serverUserLocked = false;
                    _serverStatusText = "Checking...";
                    PoncePuck.Keybinds.ServerBridge.SendAdminAuth(pw.value ?? "");
                }));
                bar.Add(entry);
            }

            string status = !string.IsNullOrEmpty(_serverStatusText)
                ? _serverStatusText
                : PoncePuck.Keybinds.ServerBridge.AdminAuthReason;
            if (!string.IsNullOrEmpty(status))
            {
                var st = new UITK.Label(status);
                st.style.fontSize = 16;
                st.style.marginTop = 8;
                st.style.whiteSpace = UITK.WhiteSpace.Normal;
                st.style.color = new Color(0.8f, 0.8f, 0.8f);
                MakeReadable(st);
                bar.Add(st);
            }

            _actionsSection.Add(bar);
        }

        // Emits an editable row per public bool/float/int field on a config
        // section, in declaration order, so new fields appear automatically.
        private void BuildEditableSection(UITK.VisualElement parent, object sectionObj)
        {
            if (sectionObj == null) return;

            var fields = sectionObj.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(f => f.MetadataToken);

            foreach (var field in fields)
            {
                string label = HumanizeBoolFieldName(field.Name);
                var captured = field; // avoid the modified-closure pitfall

                if (field.FieldType == typeof(bool))
                {
                    bool cur = false;
                    try { cur = (bool)captured.GetValue(sectionObj); } catch { continue; }
                    parent.Add(MakeEditorToggleRow(label, cur, v => captured.SetValue(sectionObj, v)));
                }
                else if (field.FieldType == typeof(float))
                {
                    float cur = 0f;
                    try { cur = (float)captured.GetValue(sectionObj); } catch { continue; }
                    // Wide bounds make MakeFloatRow's focus-out clamp a no-op, so
                    // this behaves as a free-entry numeric field.
                    parent.Add(MakeFloatRow(label, "", cur, float.NegativeInfinity, float.PositiveInfinity,
                        v => captured.SetValue(sectionObj, v)));
                }
                else if (field.FieldType == typeof(int))
                {
                    int cur = 0;
                    try { cur = (int)captured.GetValue(sectionObj); } catch { continue; }
                    parent.Add(MakeFloatRow(label, "", cur, float.NegativeInfinity, float.PositiveInfinity,
                        v => captured.SetValue(sectionObj, Mathf.RoundToInt(v))));
                }
                // string / string[] fields (none in the three sections) are skipped.
            }
        }

        // Editor toggle row: like MakeToggleRow but mutates only and does NOT
        // rebuild the whole tab on change (the editor is batched until SAVE).
        private UITK.VisualElement MakeEditorToggleRow(string title, bool currentValue, Action<bool> onChanged)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, title);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            // Match MakeFloatRow (50 / 24) so toggle and value rows in the editor
            // are the same height and the list reads consistently.
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            var label = new UITK.Label(title);
            label.style.flexGrow = 1;
            label.style.fontSize = 24;
            MakeReadable(label);
            row.Add(label);

            var toggle = new Toggle { value = currentValue };
            StyleConfigCheckbox(toggle);
            toggle.RegisterValueChangedCallback(evt => onChanged?.Invoke(evt.newValue));
            row.Add(toggle);

            return row;
        }

        private UITK.Button MakeServerEditorButton(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.flexGrow = 1;
            b.style.marginLeft = 4; b.style.marginRight = 4;
            b.style.paddingLeft = 18; b.style.paddingRight = 18;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }

        // Compact, non-stretching button for the lock bar (next to the title /
        // password field), so it does not expand to half the row width.
        private UITK.Button MakeCompactButton(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 34;
            b.style.flexGrow = 0;
            b.style.flexShrink = 0;
            b.style.minWidth = 110;
            b.style.paddingLeft = 14; b.style.paddingRight = 14;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }

        // Deep clone of the live config's three sections plus the three enables,
        // for isolated editing.  The Admin block is intentionally not copied (it
        // is never edited here and never serialized to the wire).
        private CompetitiveAdjustments.ServerConfig CloneServerCfgForEdit()
        {
            var src = CompetitiveAdjustments.ConfigManager.Config;
            var c = new CompetitiveAdjustments.ServerConfig();
            if (src == null) return c;

            c.EnableDashfall = src.EnableDashfall;
            c.EnableCompAdjust = src.EnableCompAdjust;
            c.EnableCompTweaks = src.EnableCompTweaks;
            if (src.Dashfall != null)
                c.Dashfall = JsonUtility.FromJson<CompetitiveAdjustments.DashfallConfig>(JsonUtility.ToJson(src.Dashfall));
            if (src.CompAdjust != null)
                c.CompAdjust = JsonUtility.FromJson<CompetitiveAdjustments.CompAdjustConfig>(JsonUtility.ToJson(src.CompAdjust));
            if (src.CompTweaks != null)
                c.CompTweaks = JsonUtility.FromJson<CompetitiveAdjustments.CompTweaksConfig>(JsonUtility.ToJson(src.CompTweaks));
            return c;
        }

        private void OnServerSaveApply()
        {
            _serverResetArmed = false; // another action cancels a pending reset confirm
            var cfg = _serverEditCfg;
            if (cfg == null) return;

            string json = cfg.SerializeForWire(); // never contains the Admin block
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                // Host shortcut: apply in-process (SendNamedMessageToAll does not
                // self-deliver, so a host must not round-trip its own edits).
                CompetitiveAdjustments.ConfigApplyService.ApplyServerConfigEdit(json);
                _serverStatusText = "Applied.";
            }
            else
            {
                PoncePuck.Keybinds.ServerBridge.SendAdminConfigSet(json);
                _serverStatusText = "Saving...";
            }
            RefreshActionsUI();
        }

        // Export the config currently shown in the editor (the working copy) as
        // readable JSON with the password redacted: copied to the clipboard for
        // an immediate paste and written next to the live config for a backup.
        private void OnServerExport()
        {
            _serverResetArmed = false; // another action cancels a pending reset confirm
            try
            {
                string json = CompetitiveAdjustments.ConfigManager.ExportConfigJson(_serverEditCfg);
                GUIUtility.systemCopyBuffer = json;
                string path = CompetitiveAdjustments.ConfigManager.ExportConfigToFile(_serverEditCfg);
                _serverStatusText = string.IsNullOrEmpty(path)
                    ? "Config copied to clipboard (password redacted)."
                    : "Config copied to clipboard and saved to " + path + " (password redacted).";
            }
            catch (Exception e)
            {
                _serverStatusText = "Export failed: " + e.Message;
            }
            RefreshActionsUI();
        }

        private void OnServerResetDefaults()
        {
            // First press only arms the button; the second press actually resets,
            // so a stray click can't wipe the values.
            if (!_serverResetArmed)
            {
                _serverResetArmed = true;
                _serverStatusText = "Press CONFIRM RESET again to discard all values and load defaults.";
                RefreshActionsUI();
                return;
            }

            _serverResetArmed = false;
            // Load fresh defaults into the editor copy; not applied until SAVE.
            var fresh = new CompetitiveAdjustments.ServerConfig();
            _serverEditCfg = fresh;
            _serverStatusText = "Reset to defaults. Press SAVE & APPLY to apply.";
            RefreshActionsUI();
        }

        // Network-event callbacks: refresh the SERVER tab when the full config
        // mirror updates or the auth/apply status changes.
        private void OnFullConfigReceived()
        {
            // New authoritative values arrived; re-clone the editor copy so it
            // shows live values (discards any unsaved local edits: last write
            // wins, per spec).
            _serverEditCfg = null;
            RefreshServerTabIfOpen();
        }

        private void OnAdminAuthResult(bool granted, string reason)
        {
            _serverStatusText = reason;
            RefreshServerTabIfOpen();
        }

        // True only while the SERVER tab is the visible tab.  Gates the client's
        // config-request retry so a player who never opens the editor never polls
        // the server for the full config.
        private bool IsServerTabOpen()
        {
            return _activeTab == ActiveTab.Server
                && _dfPanel != null
                && _dfPanel.style.display == UITK.DisplayStyle.Flex;
        }

        private void RefreshServerTabIfOpen()
        {
            if (_activeTab == ActiveTab.Server
                && _dfPanel != null
                && _dfPanel.style.display == UITK.DisplayStyle.Flex)
            {
                RefreshActionsUI();
            }
        }

        private void BuildSettingsUI()
        {
            var header = new UITK.Label("SETTINGS");
            header.style.fontSize = 24;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 16;
            header.style.marginTop = 8;
            MakeReadable(header);
            _actionsSection.Add(header);

            var clientConfig = DashFallConfigLoader.ClientConfig;

            // MINIMAP TWEAKS, PUCK SCALE (+X/Y/Z) and BUTTERFLY PAD OFFSET used to sit here.
            // The minimap rescale is unconditional now, because the vanilla minimap is
            // simply wrong on a resized rink. The puck and pad values are server state that
            // the sync path overwrites, so a local edit held only until the next packet
            // while disagreeing with every other client in the meantime; the config fields
            // remain as the sync slots they always were.

            _actionsSection.Add(MakeToggleRow("FREE BLADE SPIN LOCK", "Lock blade spin to the range below (off = full free spin)", clientConfig.FreeBladeSpinLockEnabled, (val) =>
            {
                clientConfig.FreeBladeSpinLockEnabled = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            // One row, two draggers. Two independent float rows let the user push MIN above
            // MAX, which produced an empty range that Mathf.Clamp resolves to a single
            // value, and the blade froze there. A range control cannot express that state.
            _actionsSection.Add(MakeRangeSliderRow(
                "FREE BLADE SPIN RANGE",
                "Lower and upper bound for the blade spin lock (client-side)",
                clientConfig.FreeBladeSpinMin, clientConfig.FreeBladeSpinMax, -127f, 127f,
                (lo, hi) =>
                {
                    clientConfig.FreeBladeSpinMin = lo;
                    clientConfig.FreeBladeSpinMax = hi;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                }));

            // Sprint shoulder trail toggle (client preference)
            _actionsSection.Add(MakeToggleRow("SPRINT SHOULDER TRAIL", "Show white shoulder trails while sprinting", clientConfig.EnableSprintShoulderTrail, (val) =>
            {
                clientConfig.EnableSprintShoulderTrail = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            _actionsSection.Add(MakeFloatRow("TRAIL TIME", "Seconds the trail persists", clientConfig.SprintShoulderTrailTime, 0.05f, 3f, (val) =>
            {
                clientConfig.SprintShoulderTrailTime = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            _actionsSection.Add(MakeFloatRow("TRAIL WIDTH", "Trail width in meters", clientConfig.SprintShoulderTrailWidth, 0.01f, 0.5f, (val) =>
            {
                clientConfig.SprintShoulderTrailWidth = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            // Colour and opacity are one choice, so they are one control: the swatch shows
            // the colour at its actual alpha over a checkerboard, and the hex field stays as
            // the typeable path for someone matching an exact team colour.
            _actionsSection.Add(MakeColorPickerRow(
                "TRAIL START COLOR", "Colour and opacity at the trail head",
                clientConfig.SprintShoulderTrailStartColorHex, clientConfig.SprintShoulderTrailStartAlpha,
                (hex, alpha) =>
                {
                    clientConfig.SprintShoulderTrailStartColorHex = hex;
                    clientConfig.SprintShoulderTrailStartAlpha = alpha;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                }));

            _actionsSection.Add(MakeColorPickerRow(
                "TRAIL END COLOR", "Colour and opacity at the trail tail",
                clientConfig.SprintShoulderTrailEndColorHex, clientConfig.SprintShoulderTrailEndAlpha,
                (hex, alpha) =>
                {
                    clientConfig.SprintShoulderTrailEndColorHex = hex;
                    clientConfig.SprintShoulderTrailEndAlpha = alpha;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                }));

            // Debug block at the bottom. CLIENT DEBUG LOG is the gate and is always visible;
            // everything below it only appears once debug is on, because those toggles paint
            // collider geometry over the rink and exist for diagnosing this mod, not for
            // playing with.
            _actionsSection.Add(MakeToggleRow("CLIENT DEBUG LOG", "Enable debug logging and show the debug tools below", clientConfig.EnableClientDebug, (val) =>
            {
                clientConfig.EnableClientDebug = val;

                // Turning debug off retracts the visuals it gates. Without this the brushes
                // stay painted over the rink with no row left to switch them off, and the
                // only way back is to hand-edit the config file.
                if (!val && (clientConfig.ShowArenaClipBrushes || clientConfig.ShowPlayerClipBrushes))
                {
                    clientConfig.ShowArenaClipBrushes = false;
                    clientConfig.ShowPlayerClipBrushes = false;
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyArena(false);
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyPlayer(false);
                }

                DashFallConfigLoader.SaveClientConfig(clientConfig);
                // MakeToggleRow already calls RefreshActionsUI, which rebuilds this list
                // against the new flag, so the rows below appear or disappear immediately.
            }));

            if (clientConfig.EnableClientDebug)
            {
                _actionsSection.Add(MakeToggleRow("SHOW ARENA CLIP BRUSHES", "Visualise arena/board collider geometry (debug)", clientConfig.ShowArenaClipBrushes, (val) =>
                {
                    clientConfig.ShowArenaClipBrushes = val;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyArena(val);
                }));

                _actionsSection.Add(MakeToggleRow("SHOW PLAYER CLIP BRUSHES", "Visualise player body collider geometry (debug)", clientConfig.ShowPlayerClipBrushes, (val) =>
                {
                    clientConfig.ShowPlayerClipBrushes = val;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyPlayer(val);
                }));

                // Preview the out-of-date version popup without a real Workshop update.
                _actionsSection.Add(MakeButtonRow("TEST VERSION POPUP", "Preview the 'mod out of date' popup", "SHOW",
                    () => ForceShowVersionPopupForTest()));
            }

            // Check if connected to server
            var features = PoncePuck.Keybinds.ServerBridge.ReceivedFeatures;
            bool hasFeatures = PoncePuck.Keybinds.ServerBridge.HasReceivedFeatures;
            
            if (!hasFeatures)
            {
                var noServerLabel = new UITK.Label("Connect to a server to see settings.");
                noServerLabel.style.fontSize = 18;
                noServerLabel.style.marginTop = 20;
                noServerLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                MakeReadable(noServerLabel);
                _actionsSection.Add(noServerLabel);
            }
            else
            {
                var infoLabel = new UITK.Label("Keybinds for features are in the\nSKATER and GOALIE tabs.\n\nSee SERVER tab for enabled features.");
                infoLabel.style.fontSize = 18;
                infoLabel.style.marginTop = 20;
                infoLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                infoLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
                MakeReadable(infoLabel);
                _actionsSection.Add(infoLabel);
            }
        }

        private UITK.VisualElement MakeToggleRow(string title, string description, bool currentValue, Action<bool> onChanged)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, title);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            var textContainer = new UITK.VisualElement();
            textContainer.style.flexGrow = 1;
            textContainer.style.flexDirection = UITK.FlexDirection.Column;
            textContainer.style.justifyContent = UITK.Justify.Center;

            var label = new UITK.Label(title);
            label.style.fontSize = 24;
            MakeReadable(label);
            textContainer.Add(label);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new UITK.Label(description);
                descLabel.style.fontSize = 16;
                descLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                ForceUIFont(descLabel);
                textContainer.Add(descLabel);
            }
            
            row.Add(textContainer);

            var toggle = new Toggle();
            toggle.value = currentValue;
            StyleConfigCheckbox(toggle);
            toggle.RegisterValueChangedCallback(evt => {
                onChanged?.Invoke(evt.newValue);
                RefreshActionsUI(); // Refresh to show/hide keybind section
            });
            row.Add(toggle);

            return row;
        }

        // A label/description row with a single action button on the right. Same visual
        // frame as MakeToggleRow but the control is a Button instead of a checkbox.
        private UITK.VisualElement MakeButtonRow(string title, string description, string buttonText, Action onClick)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, title);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            var textContainer = new UITK.VisualElement();
            textContainer.style.flexGrow = 1;
            textContainer.style.flexDirection = UITK.FlexDirection.Column;
            textContainer.style.justifyContent = UITK.Justify.Center;

            var label = new UITK.Label(title);
            label.style.fontSize = 24;
            MakeReadable(label);
            textContainer.Add(label);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new UITK.Label(description);
                descLabel.style.fontSize = 16;
                descLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                ForceUIFont(descLabel);
                textContainer.Add(descLabel);
            }

            row.Add(textContainer);

            var btn = new UITK.Button { text = buttonText };
            btn.style.height = 34;
            btn.style.minWidth = 90;
            btn.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
            btn.style.color = Color.white;
            ForceUIFont(btn);
            btn.clicked += () => onClick?.Invoke();
            row.Add(btn);

            return row;
        }

        private UITK.VisualElement MakeFloatRow(string title, string description, float currentValue, float min, float max, Action<float> onChanged)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, title);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            var textContainer = new UITK.VisualElement();
            textContainer.style.flexGrow = 1;
            textContainer.style.flexShrink = 1;
            textContainer.style.minWidth = 0;
            textContainer.style.flexDirection = UITK.FlexDirection.Column;
            textContainer.style.justifyContent = UITK.Justify.Center;

            var label = new UITK.Label(title);
            label.style.fontSize = 24;
            MakeReadable(label);
            textContainer.Add(label);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new UITK.Label(description);
                descLabel.style.fontSize = 16;
                descLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                descLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                descLabel.style.textOverflow = UITK.TextOverflow.Ellipsis;
                ForceUIFont(descLabel);
                textContainer.Add(descLabel);
            }

            row.Add(textContainer);

            var input = new TextField();
            input.value = currentValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            input.style.width = 110;
            input.style.minWidth = 110;
            input.style.flexShrink = 0;
            input.style.height = 34;
            input.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            input.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            input.style.color = Color.white;
            ForceUIFont(input);
            input.RegisterCallback<FocusInEvent>(_ => input.schedule.Execute(() => input.SelectAll()));
            input.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (!float.TryParse(input.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    input.value = currentValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    return;
                }

                parsed = Mathf.Clamp(parsed, min, max);
                currentValue = parsed;
                input.value = parsed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                onChanged?.Invoke(parsed);
            });
            row.Add(input);

            return row;
        }

        // MakeSliderRow was the single-value slider row. Its last callers were the trail alpha
        // rows, which are now part of MakeColorPickerRow. StyleSliderControl survives
        // because the colour picker's channel sliders use it.

                /// <summary>
        /// One row carrying BOTH ends of a range on a single track with two draggers.
        ///
        /// This replaces the pair of independent float rows that used to set the free blade
        /// spin bounds. Two separate rows let the user put the lower bound above the upper
        /// one, and Mathf.Clamp(value, min, max) with min &gt; max collapses to a single
        /// number, so the blade stopped responding and sat at one angle. A MinMaxSlider
        /// cannot represent a crossed range at all, so the failure is designed out rather
        /// than validated against.
        ///
        /// The two numeric fields stay editable for exact values, and each one re-asserts
        /// the ordering on commit so typing cannot recreate what the draggers prevent.
        /// </summary>
        private UITK.VisualElement MakeRangeSliderRow(
            string title, string description,
            float currentMin, float currentMax,
            float limitMin, float limitMax,
            Action<float, float> onChanged)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, title);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            // Same 300px label column the other value rows use, so controls line up down the page.
            var textContainer = new UITK.VisualElement();
            textContainer.style.minWidth = 300;
            textContainer.style.maxWidth = 300;
            textContainer.style.flexDirection = UITK.FlexDirection.Column;
            textContainer.style.justifyContent = UITK.Justify.Center;

            var label = new UITK.Label(title);
            label.style.fontSize = 24;
            MakeReadable(label);
            textContainer.Add(label);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new UITK.Label(description);
                descLabel.style.fontSize = 16;
                descLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                descLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                descLabel.style.textOverflow = UITK.TextOverflow.Ellipsis;
                ForceUIFont(descLabel);
                textContainer.Add(descLabel);
            }

            row.Add(textContainer);

            // Order the incoming pair rather than trusting it: a config written by the old
            // two-row UI can already be crossed, and that saved state is exactly the bug.
            float startLo = Mathf.Clamp(Mathf.Min(currentMin, currentMax), limitMin, limitMax);
            float startHi = Mathf.Clamp(Mathf.Max(currentMin, currentMax), limitMin, limitMax);

            var lowField = MakeRangeNumberField(startLo);
            var highField = MakeRangeNumberField(startHi);

            var slider = new UITK.MinMaxSlider(startLo, startHi, limitMin, limitMax);
            slider.style.flexGrow = 1;
            slider.style.flexBasis = 0;
            slider.style.marginLeft = 6;
            slider.style.marginRight = 6;
            StyleMinMaxSliderControl(slider);

            bool syncing = false;

            void Commit(float lo, float hi, bool writeSlider)
            {
                lo = Mathf.Clamp(lo, limitMin, limitMax);
                hi = Mathf.Clamp(hi, limitMin, limitMax);
                if (lo > hi) { float swap = lo; lo = hi; hi = swap; }

                syncing = true;
                lowField.value = FormatRangeNumber(lo);
                highField.value = FormatRangeNumber(hi);
                if (writeSlider) slider.value = new Vector2(lo, hi);
                syncing = false;

                onChanged?.Invoke(lo, hi);
            }

            slider.RegisterValueChangedCallback(evt =>
            {
                if (syncing) return;
                Commit(evt.newValue.x, evt.newValue.y, writeSlider: false);
            });

            // Committed on focus loss, matching the other numeric rows: parsing every keystroke would
            // reformat the text out from under someone halfway through typing "-12".
            lowField.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (syncing) return;
                if (!TryParseRangeNumber(lowField.value, out float parsed))
                {
                    lowField.value = FormatRangeNumber(slider.value.x);
                    return;
                }
                Commit(parsed, slider.value.y, writeSlider: true);
            });

            highField.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (syncing) return;
                if (!TryParseRangeNumber(highField.value, out float parsed))
                {
                    highField.value = FormatRangeNumber(slider.value.y);
                    return;
                }
                Commit(slider.value.x, parsed, writeSlider: true);
            });

            row.Add(lowField);
            row.Add(slider);
            row.Add(highField);

            // Normalise a crossed or out-of-range config on first build, so the file stops
            // carrying the bad state as soon as the user opens the settings page.
            if (!Mathf.Approximately(startLo, currentMin) || !Mathf.Approximately(startHi, currentMax))
                onChanged?.Invoke(startLo, startHi);

            return row;
        }

        private static string FormatRangeNumber(float value)
            => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static bool TryParseRangeNumber(string text, out float value)
            => float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);

        private TextField MakeRangeNumberField(float initial)
        {
            var field = new TextField();
            field.value = FormatRangeNumber(initial);
            field.style.minWidth = 58;
            field.style.maxWidth = 58;
            field.style.maxHeight = 30;
            field.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleRight);
            field.style.marginLeft = 6;
            field.style.marginRight = 6;
            field.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            field.style.color = Color.white;
            ForceUIFont(field);
            return field;
        }

        /// <summary>
        /// MinMaxSlider styled to match StyleSliderControl. It has a tracker, a dragger for
        /// the selected span, and two end handles, and Unity has renamed these classes
        /// between versions, so every known spelling is probed exactly as the single-value
        /// slider does.
        /// </summary>
        private static void StyleMinMaxSliderControl(UITK.MinMaxSlider s)
        {
            s.style.height = 26;

            var tracker = s.Q<UITK.VisualElement>(className: "unity-min-max-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-base-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-tracker");
            if (tracker != null)
            {
                tracker.style.height = 4;
                tracker.style.marginTop = 11;
                tracker.style.backgroundColor = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.35f));
                tracker.style.borderTopLeftRadius = 2;
                tracker.style.borderTopRightRadius = 2;
                tracker.style.borderBottomLeftRadius = 2;
                tracker.style.borderBottomRightRadius = 2;
            }

            // The span between the two handles. Tinted rather than white so the selected
            // range reads as a filled bar and the handles stay the brightest thing on it.
            var dragger = s.Q<UITK.VisualElement>(className: "unity-min-max-slider__dragger")
                       ?? s.Q<UITK.VisualElement>(className: "unity-dragger");
            if (dragger != null)
            {
                dragger.style.height = 8;
                dragger.style.marginTop = 9;
                dragger.style.backgroundColor = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.6f));
                dragger.style.borderTopLeftRadius = 4;
                dragger.style.borderTopRightRadius = 4;
                dragger.style.borderBottomLeftRadius = 4;
                dragger.style.borderBottomRightRadius = 4;
            }

            StyleRangeHandle(s.Q<UITK.VisualElement>(className: "unity-min-max-slider__min-thumb")
                          ?? s.Q<UITK.VisualElement>(className: "unity-min-max-slider__dragger-min"));
            StyleRangeHandle(s.Q<UITK.VisualElement>(className: "unity-min-max-slider__max-thumb")
                          ?? s.Q<UITK.VisualElement>(className: "unity-min-max-slider__dragger-max"));
        }

        private static void StyleRangeHandle(UITK.VisualElement handle)
        {
            if (handle == null) return;
            handle.style.width = 16;
            handle.style.height = 16;
            handle.style.backgroundColor = new UITK.StyleColor(Color.white);
            handle.style.borderTopLeftRadius = 8;
            handle.style.borderTopRightRadius = 8;
            handle.style.borderBottomLeftRadius = 8;
            handle.style.borderBottomRightRadius = 8;
        }

        // PlayerQoL slider look: thin translucent white rail with a large round
        // white thumb.
        private static void StyleSliderControl(UITK.Slider s)
        {
            s.style.height = 26;
            s.style.marginLeft = 6;
            s.style.marginRight = 6;

            // Cover legacy, current, and base-slider USS class names - Unity
            // renames these between versions and only one will match per build.
            var tracker = s.Q<UITK.VisualElement>(className: "unity-base-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-tracker");
            var dragger = s.Q<UITK.VisualElement>(className: "unity-base-slider__dragger")
                       ?? s.Q<UITK.VisualElement>(className: "unity-slider__dragger")
                       ?? s.Q<UITK.VisualElement>(className: "unity-dragger");
            if (tracker != null)
            {
                tracker.style.height = 4;
                tracker.style.marginTop = 11; // vertically center the 4px rail in 26px
                tracker.style.backgroundColor = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.35f));
                tracker.style.borderTopLeftRadius = 2;
                tracker.style.borderTopRightRadius = 2;
                tracker.style.borderBottomLeftRadius = 2;
                tracker.style.borderBottomRightRadius = 2;
            }
            if (dragger != null)
            {
                dragger.style.width = 18;
                dragger.style.height = 18;
                dragger.style.marginTop = 4; // center on the rail
                dragger.style.backgroundColor = new UITK.StyleColor(Color.white);
                dragger.style.borderTopLeftRadius = 9;
                dragger.style.borderTopRightRadius = 9;
                dragger.style.borderBottomLeftRadius = 9;
                dragger.style.borderBottomRightRadius = 9;
            }
        }

        /// <summary>
        /// A colour swatch that opens an inline HSV picker, plus the hex field it replaces.
        ///
        /// UnityEditor.UIElements.ColorField is editor-only, so there is no stock runtime
        /// colour control to reach for; this is built from the primitives UITK does ship at
        /// runtime. Hue/saturation/value rather than raw RGB because picking a colour by
        /// dragging three independent channel sliders is guesswork, while hue-then-shade is
        /// how people actually think about it.
        ///
        /// Alpha is folded in because colour and opacity are one decision, and the swatch
        /// can only tell the truth about a translucent trail if it shows both. The config
        /// keeps them as the separate hex and float fields it always had, so nothing
        /// downstream changes.
        ///
        /// The picker starts collapsed. Two of these expanded at once would push the rest of
        /// the settings page off screen, so opening one is a deliberate click.
        /// </summary>
        private UITK.VisualElement MakeColorPickerRow(
            string title, string description, string currentHex, float currentAlpha, Action<string, float> onChanged)
        {
            Color startColor = ParseHexOrWhite(currentHex);
            float alpha = Mathf.Clamp01(currentAlpha);

            // The row is a column: the header line, then the picker that expands under it.
            var container = new UITK.VisualElement();
            MarkSearchable(container, title);
            container.style.flexDirection = UITK.FlexDirection.Column;
            container.style.marginBottom = 8;
            container.style.backgroundColor = new UITK.StyleColor(RowBg);
            container.style.paddingLeft = 12;
            container.style.paddingRight = 12;

            var header = new UITK.VisualElement();
            header.style.flexDirection = UITK.FlexDirection.Row;
            header.style.alignItems = UITK.Align.Center;
            header.style.height = 50;

            var textContainer = new UITK.VisualElement();
            textContainer.style.flexGrow = 1;
            textContainer.style.flexShrink = 1;
            textContainer.style.minWidth = 0;
            textContainer.style.flexDirection = UITK.FlexDirection.Column;
            textContainer.style.justifyContent = UITK.Justify.Center;

            var label = new UITK.Label(title);
            label.style.fontSize = 24;
            MakeReadable(label);
            textContainer.Add(label);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new UITK.Label(description);
                descLabel.style.fontSize = 16;
                descLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                descLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                descLabel.style.textOverflow = UITK.TextOverflow.Ellipsis;
                ForceUIFont(descLabel);
                textContainer.Add(descLabel);
            }

            header.Add(textContainer);

            // Alpha is shown by layering the colour over a light plate: at alpha 0 the
            // swatch reads as the plate, which is the honest preview of an invisible trail.
            var swatchPlate = new UITK.VisualElement();
            swatchPlate.style.width = 54;
            swatchPlate.style.height = 34;
            swatchPlate.style.flexShrink = 0;
            swatchPlate.style.backgroundColor = new UITK.StyleColor(new Color(0.55f, 0.55f, 0.55f));
            StyleSwatchBorder(swatchPlate);

            var swatchFill = new UITK.VisualElement();
            swatchFill.style.flexGrow = 1;
            swatchPlate.Add(swatchFill);

            var hexField = new TextField();
            hexField.value = NormalizeHex(currentHex) ?? "#FFFFFF";
            hexField.style.width = 110;
            hexField.style.minWidth = 110;
            hexField.style.flexShrink = 0;
            hexField.style.height = 34;
            hexField.style.marginLeft = 8;
            hexField.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            hexField.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            hexField.style.color = Color.white;
            hexField.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            ForceUIFont(hexField);

            var toggleButton = new Button { text = "PICK" };
            toggleButton.style.width = 76;
            toggleButton.style.height = 34;
            toggleButton.style.flexShrink = 0;
            toggleButton.style.marginLeft = 8;
            toggleButton.style.marginRight = 0;
            ForceUIFont(toggleButton);

            header.Add(swatchPlate);
            header.Add(hexField);
            header.Add(toggleButton);
            container.Add(header);

            var picker = new UITK.VisualElement();
            picker.style.flexDirection = UITK.FlexDirection.Column;
            picker.style.display = UITK.DisplayStyle.None;
            picker.style.paddingBottom = 10;
            container.Add(picker);

            Color.RGBToHSV(startColor, out float h, out float s, out float v);

            var hueSlider = MakePickerSlider("HUE", h, 0f, 1f, picker);
            var satSlider = MakePickerSlider("SATURATION", s, 0f, 1f, picker);
            var valSlider = MakePickerSlider("BRIGHTNESS", v, 0f, 1f, picker);
            var alphaSlider = MakePickerSlider("OPACITY", alpha, 0f, 1f, picker);

            bool syncing = false;

            // Single place that writes the swatch, the hex text and the config, so the
            // sliders and the typed field can never disagree about what the colour is.
            void Apply(Color rgb, float a, bool writeHexField, bool writeSliders)
            {
                a = Mathf.Clamp01(a);
                string hex = "#" + ColorUtility.ToHtmlStringRGB(rgb);

                syncing = true;
                swatchFill.style.backgroundColor = new UITK.StyleColor(new Color(rgb.r, rgb.g, rgb.b, a));
                if (writeHexField) hexField.value = hex;
                if (writeSliders)
                {
                    Color.RGBToHSV(rgb, out float nh, out float ns, out float nv);
                    hueSlider.value = nh;
                    satSlider.value = ns;
                    valSlider.value = nv;
                }
                alphaSlider.value = a;
                syncing = false;

                onChanged?.Invoke(hex, a);
            }

            void ApplyFromSliders()
            {
                if (syncing) return;
                Apply(Color.HSVToRGB(hueSlider.value, satSlider.value, valSlider.value),
                      alphaSlider.value, writeHexField: true, writeSliders: false);
            }

            hueSlider.RegisterValueChangedCallback(_ => ApplyFromSliders());
            satSlider.RegisterValueChangedCallback(_ => ApplyFromSliders());
            valSlider.RegisterValueChangedCallback(_ => ApplyFromSliders());
            alphaSlider.RegisterValueChangedCallback(_ => ApplyFromSliders());

            hexField.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (syncing) return;

                // NormalizeHex stays the only parser, so a typed value is accepted on exactly
                // the same terms as it was before this row grew a picker.
                string normalized = NormalizeHex(hexField.value);
                if (normalized == null)
                {
                    hexField.value = "#" + ColorUtility.ToHtmlStringRGB(
                        Color.HSVToRGB(hueSlider.value, satSlider.value, valSlider.value));
                    return;
                }

                Apply(ParseHexOrWhite(normalized), alphaSlider.value, writeHexField: true, writeSliders: true);
            });

            toggleButton.clicked += () =>
            {
                bool open = picker.style.display == UITK.DisplayStyle.None;
                picker.style.display = open ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
                toggleButton.text = open ? "CLOSE" : "PICK";
            };

            // Paint the initial swatch without reporting a change, so merely opening the
            // settings page does not rewrite the config file.
            swatchFill.style.backgroundColor = new UITK.StyleColor(new Color(startColor.r, startColor.g, startColor.b, alpha));

            return container;
        }

        private static Color ParseHexOrWhite(string hex)
        {
            string normalized = NormalizeHex(hex);
            if (normalized != null && ColorUtility.TryParseHtmlString(normalized, out var parsed)) return parsed;
            return Color.white;
        }

        private static void StyleSwatchBorder(UITK.VisualElement element)
        {
            element.style.borderTopWidth = 2;
            element.style.borderBottomWidth = 2;
            element.style.borderLeftWidth = 2;
            element.style.borderRightWidth = 2;
            var border = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.45f));
            element.style.borderTopColor = border;
            element.style.borderBottomColor = border;
            element.style.borderLeftColor = border;
            element.style.borderRightColor = border;
        }

        /// <summary>One labelled channel slider inside an expanded colour picker.</summary>
        private UITK.Slider MakePickerSlider(
            string caption, float value, float min, float max, UITK.VisualElement parent)
        {
            var line = new UITK.VisualElement();
            line.style.flexDirection = UITK.FlexDirection.Row;
            line.style.alignItems = UITK.Align.Center;
            line.style.height = 34;

            var caLabel = new UITK.Label(caption);
            caLabel.style.fontSize = 16;
            caLabel.style.minWidth = 120;
            caLabel.style.maxWidth = 120;
            caLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            ForceUIFont(caLabel);
            line.Add(caLabel);

            var slider = new UITK.Slider(min, max);
            slider.style.flexGrow = 1;
            slider.style.flexBasis = 0;
            slider.value = Mathf.Clamp(value, min, max);
            StyleSliderControl(slider);
            line.Add(slider);

            parent.Add(line);
            return slider;
        }

        // MakeHexColorRow was replaced by MakeColorPickerRow, which keeps the same hex field
        // and the same NormalizeHex round-trip but adds the swatch and the HSV sliders.

                // MirrorPuckScaleToCompanion and ApplyLocalPuckScale lived here to give the puck
        // scale sliders live feedback while dragging. Both existed only for those rows.
        // The sync receive path in Companion.PluginCore does its own mirroring and its own
        // re-apply, so nothing else needed them.

        private static string NormalizeHex(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string normalized = value.Trim();
            if (!normalized.StartsWith("#")) normalized = "#" + normalized;
            if (!ColorUtility.TryParseHtmlString(normalized, out var parsed)) return null;
            return "#" + ColorUtility.ToHtmlStringRGB(parsed);
        }

        private void AddSectionHeader(string text) => AddSectionHeaderTo(_actionsSection, text);

        private void AddSectionHeaderTo(UITK.VisualElement parent, string text)
        {
            var header = new UITK.Label(text);
            header.AddToClassList("cfg-header");
            header.style.fontSize = 24;
            header.style.marginTop = 16;
            header.style.marginBottom = 8;
            header.style.color = new Color(0.9f, 0.9f, 0.5f);
            ForceUIFont(header);
            parent.Add(header);
        }

        private void AddSubHeader(string text) => AddSubHeaderTo(_actionsSection, text);

        private void AddSubHeaderTo(UITK.VisualElement parent, string text)
        {
            var header = new UITK.Label(text);
            header.style.fontSize = 24;
            header.style.marginTop = 10;
            header.style.marginBottom = 6;
            header.style.marginLeft = 8;
            header.style.color = new Color(0.7f, 0.7f, 0.9f);
            ForceUIFont(header);
            parent.Add(header);
        }

        // Turns a config field name into a readable label (also used for float /
        // int rows, despite the historical "Bool" in the name).
        private static string HumanizeBoolFieldName(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) return "Unknown";
            string text = fieldName;
            if (text.StartsWith("Enable", StringComparison.Ordinal)) text = text.Substring("Enable".Length);
            if (text.StartsWith("Disable", StringComparison.Ordinal)) text = "Disable " + text.Substring("Disable".Length);

            var chars = new List<char>(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]))))
                    chars.Add(' ');
                chars.Add(c);
            }

            return new string(chars.ToArray()).Trim();
        }

        private void RefreshActionsUI()
        {
            BuildActionsUI();
        }

        // Enum to distinguish between pressable (action) and holdable (movement) controls
        private enum BindRowType { Pressable, Holdable }

        private UITK.VisualElement MakeBindRow(string action, Func<List<string>> getter, Action<List<string>> setter, 
            Func<string> typeGetter, Action<string> typeSetter, BindRowType rowType, bool enabled = true)
        {
            var row = new UITK.VisualElement();
            MarkSearchable(row, action);
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(enabled ? RowBg : DisabledRowBg);
            row.style.paddingLeft = 12; row.style.paddingRight = 12;
            row.style.paddingTop = 8; row.style.paddingBottom = 8;
            row.style.opacity = enabled ? 1f : 0.5f;

            // Label
            var lab = new UITK.Label(action + (enabled ? "" : " <size=12><color=red><b>DISABLED BY SERVER</b></color></size>"));
            lab.style.fontSize = 24;
            // 220 min keeps the chip column aligned for short names; let long
            // names ("STANDING DASH RIGHT") grow rather than ellipsis-truncate at
            // 24px, so no maxWidth and no shrink.
            lab.style.minWidth = 220;
            lab.style.flexShrink = 0;
            lab.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            lab.style.color = enabled ? Color.white : new Color(0.6f, 0.6f, 0.6f);
            lab.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            lab.style.textOverflow = UITK.TextOverflow.Ellipsis;
            ForceUIFont(lab);
            row.Add(lab);

            // Chips container (shows bound keys)
            var chipsRoot = new UITK.VisualElement();
            chipsRoot.style.flexDirection = UITK.FlexDirection.Row;
            chipsRoot.style.justifyContent = UITK.Justify.FlexEnd;
            chipsRoot.style.alignItems = UITK.Align.Center;
            chipsRoot.style.flexGrow = 1;
            chipsRoot.style.flexShrink = 1;
            chipsRoot.style.minWidth = 0;
            chipsRoot.style.marginLeft = 4;
            chipsRoot.style.marginRight = 8;
            row.Add(chipsRoot);

            // Buttons container
            var right = new UITK.VisualElement();
            right.style.flexDirection = UITK.FlexDirection.Row;
            right.style.alignItems = UITK.Align.Center;
            right.style.flexShrink = 0;
            row.Add(right);

            // BIND button
            var bindBtn = new UITK.Button(() =>
            {
                if (!enabled) return;
                StartChordCapture($"Press keys for {action}", spec =>
                {
                    if (string.IsNullOrEmpty(spec)) return;
                    var cur = getter() ?? new List<string>();
                    if (!cur.Contains(spec))
                    {
                        cur.Add(spec);
                        setter(cur);
                        RefreshChips();
                    }
                });
            });
            StyleRowButton(bindBtn, BTN_W, "BIND");
            bindBtn.SetEnabled(enabled);
            right.Add(bindBtn);

            // Create the appropriate dropdown based on row type
            List<string> choices;
            if (rowType == BindRowType.Pressable)
            {
                choices = new List<string> { "PRESS", "RELEASE", "DOUBLE PRESS", "HOLD" };
            }
            else
            {
                choices = new List<string> { "CONTINUOUS", "TOGGLE" };
            }

            // Get current value and find its index
            var currentType = typeGetter() ?? choices[0];
            int currentIndex = choices.IndexOf(currentType);
            if (currentIndex < 0) currentIndex = 0;

            var dropdown = new UITK.DropdownField(choices, currentIndex);
            dropdown.style.width = 206;
            dropdown.style.height = 34;
            dropdown.style.marginLeft = 4;
            dropdown.SetEnabled(enabled);
            StyleDropdown(dropdown);
            
            // Wire up value change using INotifyValueChanged interface
            dropdown.RegisterCallback<UITK.ChangeEvent<string>>(evt =>
            {
                typeSetter(evt.newValue);
            });
            right.Add(dropdown);

            void RefreshChips()
            {
                chipsRoot.Clear();
                var list = getter() ?? new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    var idx = i;
                    chipsRoot.Add(MakeChip(list[i], enabled, () =>
                    {
                        if (!enabled) return;
                        var cur = getter() ?? new List<string>();
                        if (idx >= 0 && idx < cur.Count) cur.RemoveAt(idx);
                        setter(cur);
                        RefreshChips();
                    }));
                }
            }
            RefreshChips();

            return row;
        }

        private void StyleRowButton(UITK.Button btn, int width, string text)
        {
            btn.text = text;
            btn.style.width = width;
            btn.style.height = 34;
            btn.style.marginLeft = 4;
            btn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            btn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            MakeReadable(btn);
            AddButtonFlash(btn);
        }

        private void StyleDropdown(UITK.DropdownField dropdown)
        {
            dropdown.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            dropdown.style.color = Color.white;
            ForceUIFont(dropdown);
            
            // Style the label inside the dropdown using Query
            var label = UITK.UQueryExtensions.Q<UITK.Label>(dropdown);
            if (label != null)
            {
                label.style.color = Color.white;
                ForceUIFont(label);
            }
        }

        private UITK.VisualElement MakeChip(string text, bool enabled, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            chip.style.flexDirection = UITK.FlexDirection.Row;
            chip.style.alignItems = UITK.Align.Center;
            chip.style.backgroundColor = new UITK.StyleColor(new Color32(80, 80, 80, 255));
            chip.style.paddingLeft = 8; chip.style.paddingRight = 4;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            chip.style.marginRight = 4;
            chip.style.borderTopLeftRadius = 4; chip.style.borderTopRightRadius = 4;
            chip.style.borderBottomLeftRadius = 4; chip.style.borderBottomRightRadius = 4;
            chip.style.opacity = enabled ? 1f : 0.6f;

            var label = new UITK.Label(text);
            label.style.fontSize = 14;
            MakeReadable(label);
            chip.Add(label);

            var xBtn = new UITK.Button(onRemove) { text = "×" };
            xBtn.style.width = 20; xBtn.style.height = 20;
            xBtn.style.marginLeft = 4;
            xBtn.style.backgroundColor = new UITK.StyleColor(new Color32(100, 100, 100, 255));
            xBtn.style.fontSize = 14;
            xBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            xBtn.style.paddingLeft = 0; xBtn.style.paddingRight = 0;
            xBtn.style.paddingTop = 0; xBtn.style.paddingBottom = 0;
            xBtn.SetEnabled(enabled);
            MakeReadable(xBtn);
            if (enabled) AddChipButtonFlash(xBtn);
            chip.Add(xBtn);

            return chip;
        }

        private static readonly Color32 ChipXBg = new Color32(100, 100, 100, 255);
        
        private static void AddChipButtonFlash(UITK.Button btn)
        {
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(new Color32(180, 80, 80, 255));
                btn.style.color = Color.white;
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(ChipXBg);
                btn.style.color = Color.white;
            });
        }

        private static void AddButtonFlash(UITK.Button btn)
        {
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                btn.style.backgroundColor = Color.white;
                btn.style.color = Color.black;
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                btn.style.color = Color.white;
            });
        }

        private void ResetToDefaults()
        {
            // Skater keybinds
            _skater.divekey = new List<string> { "F" };
            _skater.twistleftkey = new List<string> { "Z" };
            _skater.twistrightkey = new List<string> { "C" };
            _skater.slideinfluenceleftkey = new List<string> { "Z" };
            _skater.slideinfluencerightkey = new List<string> { "C" };
            _skater.slideinfluenceforwardkey = new List<string> { "W" };
            _skater.slideinfluencebackwardkey = new List<string> { "S" };
            
            // Skater action types
            _skater.divekeytype = "PRESS";
            _skater.twistleftkeytype = "DOUBLE PRESS";
            _skater.twistrightkeytype = "DOUBLE PRESS";
            _skater.slideinfluenceleftkeytype = "CONTINUOUS";
            _skater.slideinfluencerightkeytype = "CONTINUOUS";
            _skater.slideinfluenceforwardkeytype = "CONTINUOUS";
            _skater.slideinfluencebackwardkeytype = "CONTINUOUS";
            
            // Goalie keybinds
            _goalie.divekey = new List<string> { "F" };
            _goalie.standingdashleftkey = new List<string> { "Q" };
            _goalie.standingdashrightkey = new List<string> { "E" };
            _goalie.twistleftkey = new List<string> { "Z" };
            _goalie.twistrightkey = new List<string> { "C" };
            _goalie.slideinfluenceleftkey = new List<string> { "Z" };
            _goalie.slideinfluencerightkey = new List<string> { "C" };
            _goalie.slideinfluenceforwardkey = new List<string> { "W" };
            _goalie.slideinfluencebackwardkey = new List<string> { "S" };
            
            // Goalie action types
            _goalie.divekeytype = "PRESS";
            _goalie.standingdashleftkeytype = "PRESS";
            _goalie.standingdashrightkeytype = "PRESS";
            _goalie.twistleftkeytype = "DOUBLE PRESS";
            _goalie.twistrightkeytype = "DOUBLE PRESS";
            _goalie.slideinfluenceleftkeytype = "CONTINUOUS";
            _goalie.slideinfluencerightkeytype = "CONTINUOUS";
            _goalie.slideinfluenceforwardkeytype = "CONTINUOUS";
            _goalie.slideinfluencebackwardkeytype = "CONTINUOUS";
        }

        // ========== PANEL OPEN/CLOSE ==========
        private void OpenDashFallPanel()
        {
            BuildDashFallPanel();
            if (_dfPanel == null) return;

            _dfBackdrop.style.display = UITK.DisplayStyle.Flex;
            _dfPanel.style.display = UITK.DisplayStyle.Flex;

            // Fresh panel session: re-attempt auto-unlock and clear any prior
            // local LOCK / status so the SERVER tab reflects current auth.
            _serverAutoAuthSent = false;
            _serverUserLocked = false;
            _serverStatusText = "";
            _serverEditCfg = null; // re-clone editor copy from live on next build

            // Refresh chips to show current bindings
            RefreshActionsUI();

            // Unlock cursor
            SaveCursorState();
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            // Menu buttons are already hidden by hub, no need to hide them again
        }

        private void CloseDashFallPanel()
        {
            if (_isCapturing)
            {
                CancelChordCapture();
                return;
            }

            // Check if panel is already closed - don't re-open hub if so
            bool wasVisible = _dfPanel != null && _dfPanel.style.display == UITK.DisplayStyle.Flex;

            if (_dfPanel != null) _dfPanel.style.display = UITK.DisplayStyle.None;
            if (_dfBackdrop != null) _dfBackdrop.style.display = UITK.DisplayStyle.None;

            // Only return to hub if the panel was actually visible
            if (!wasVisible) return;

            // Don't restore cursor state or menu buttons - hub will manage them
            ConfigManager.Dbg("Panel closed, returning to hub");
            
            // Return to ModMenuHub
            try
            {
                PonceMods.Shared.ModMenuHub.OpenPanel();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to open hub: {e}");
                // Fallback: restore state manually
                RestoreCursorState();
                HideBackgroundMenuButtons(false);
            }
        }
        
        /// <summary>
        /// Fully close panel without returning to hub (ESC behavior).
        /// </summary>
        private void FullCloseDashFallPanel()
        {
            if (_isCapturing)
            {
                CancelChordCapture();
                return;
            }

            if (_dfPanel != null) _dfPanel.style.display = UITK.DisplayStyle.None;
            if (_dfBackdrop != null) _dfBackdrop.style.display = UITK.DisplayStyle.None;

            ConfigManager.Dbg("Panel fully closed via ESC");
            
            // Use ModMenuHub's FullClose to handle cursor and menu buttons properly
            try
            {
                PonceMods.Shared.ModMenuHub.FullClose();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[COMPADJUST] Failed to full close: {e}");
                // Fallback: restore state manually
                RestoreCursorState();
                HideBackgroundMenuButtons(false);
            }
        }

        private void HideBackgroundMenuButtons(bool hide)
        {
            if (hide)
            {
                var root = _doc?.rootVisualElement ?? _lastRoot;
                if (root == null) return;

                _hiddenMenuButtons.Clear();
                foreach (var b in UITK.UQueryExtensions.Query<UITK.Button>(root).ToList())
                {
                    if (b == null) continue;
                    if ((_dfPanel != null && IsUnder(b, _dfPanel)) ||
                        (_dfBackdrop != null && IsUnder(b, _dfBackdrop)) ||
                        (_captureOverlay != null && IsUnder(b, _captureOverlay)))
                        continue;

                    if (b.resolvedStyle.display != UITK.DisplayStyle.None)
                    {
                        _hiddenMenuButtons.Add(b);
                        b.style.display = UITK.DisplayStyle.None;
                    }
                }
            }
            else
            {
                // Restore hidden buttons - don't need root for this
                foreach (var b in _hiddenMenuButtons)
                    if (b != null) b.style.display = UITK.DisplayStyle.Flex;
                _hiddenMenuButtons.Clear();
            }
        }

        private static bool IsUnder(UITK.VisualElement child, UITK.VisualElement ancestor)
        {
            for (var p = child; p != null; p = p.parent)
                if (p == ancestor) return true;
            return false;
        }

        // ========== CHORD CAPTURE ==========
        private void EnsureCaptureOverlay()
        {
            if (_captureOverlay != null) return;

            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root == null) return;

            _captureOverlay = new UITK.VisualElement();
            _captureOverlay.style.position = UITK.Position.Absolute;
            _captureOverlay.style.left = 0; _captureOverlay.style.right = 0;
            _captureOverlay.style.top = 0; _captureOverlay.style.bottom = 0;
            _captureOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0.1f, 0.1f, 0.15f, 0.95f));
            _captureOverlay.style.display = UITK.DisplayStyle.None;
            _captureOverlay.pickingMode = UITK.PickingMode.Position;
            ForceUIFont(_captureOverlay);

            var centerContainer = new UITK.VisualElement();
            centerContainer.style.position = UITK.Position.Absolute;
            centerContainer.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            centerContainer.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            centerContainer.style.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0);
            centerContainer.style.alignItems = UITK.Align.Center;
            centerContainer.style.justifyContent = UITK.Justify.Center;
            centerContainer.style.flexDirection = UITK.FlexDirection.Column;

            var title = new UITK.Label("KEY REBIND");
            title.style.fontSize = 72;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            title.style.marginBottom = 32;
            MakeReadable(title);
            centerContainer.Add(title);

            _captureLabel = new UITK.Label("Press a key or combination to bind.");
            _captureLabel.style.fontSize = 24;
            _captureLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _captureLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
            _captureLabel.style.maxWidth = 600;
            MakeReadable(_captureLabel);
            centerContainer.Add(_captureLabel);

            _captureOverlay.Add(centerContainer);
            root.Add(_captureOverlay);
            _captureOverlay.BringToFront();
        }

        private void StartChordCapture(string prompt, Action<string> onCaptured)
        {
            _onChordCaptured = onCaptured;
            _isCapturing = true;

            EnsureCaptureOverlay();
            HidePanelDuringCapture(true);
            // Don't call HideBackgroundMenuButtons here - they're already hidden from panel open

            if (_captureLabel != null) _captureLabel.text = "Press a key or combination to bind.";
            _captureOverlay.style.display = UITK.DisplayStyle.Flex;
            StartCoroutine(CaptureChordRoutine());
        }

        private void CancelChordCapture()
        {
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
            HidePanelDuringCapture(false);
            _onChordCaptured = null;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        private void HidePanelDuringCapture(bool hide)
        {
            if (_dfPanel == null) return;

            if (hide)
            {
                _panelHiddenForCapture = (_dfPanel.style.display == UITK.DisplayStyle.Flex);
                _dfPanel.style.display = UITK.DisplayStyle.None;
                if (_dfBackdrop != null) _dfBackdrop.style.display = UITK.DisplayStyle.Flex;
            }
            else
            {
                if (_panelHiddenForCapture)
                {
                    _dfPanel.style.display = UITK.DisplayStyle.Flex;
                    _panelHiddenForCapture = false;
                }
            }
        }

        private static bool IsModifierKey(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift ||
            k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private KeyChord SnapshotChord()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool ctrl = (kb?.leftCtrlKey?.isPressed ?? false) || (kb?.rightCtrlKey?.isPressed ?? false);
            bool shift = (kb?.leftShiftKey?.isPressed ?? false) || (kb?.rightShiftKey?.isPressed ?? false);
            bool alt = (kb?.leftAltKey?.isPressed ?? false) || (kb?.rightAltKey?.isPressed ?? false);

            var keys = new List<KeyCode>();
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (!IsAllowedKey(k) || IsModifierKey(k)) continue;
                if (IsKeyDown(k)) keys.Add(k);
            }
            keys.Sort((a, b) => a.CompareTo(b));
            return new KeyChord { Keys = keys.ToArray(), Ctrl = ctrl, Shift = shift, Alt = alt };
        }

        private static bool IsAllowedKey(KeyCode k)
        {
            if (k == KeyCode.None || k == KeyCode.Escape) return false;
            // Allow mouse buttons (Mouse0-Mouse6 = 323-329)
            return true;
        }

        private static string KeyChordToSpec(KeyChord kc)
        {
            var sb = new System.Text.StringBuilder();
            if (kc.Ctrl) sb.Append("Ctrl+");
            if (kc.Shift) sb.Append("Shift+");
            if (kc.Alt) sb.Append("Alt+");
            if (kc.Keys != null && kc.Keys.Length > 0)
                sb.Append(string.Join("+", kc.Keys.Select(k => GetFriendlyKeyName(k))));
            return sb.ToString();
        }
        
        private static string GetFriendlyKeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "MB4";
                case KeyCode.Mouse4: return "MB5";
                default: return k.ToString();
            }
        }

        private IEnumerator CaptureChordRoutine()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            float startTimeout = Time.unscaledTime + 5f;
            bool started = false;
            float windowEnd = 2f;

            KeyChord best = default;
            int bestWeight = -1;
            float lastBestAt = 0f;

            bool HasAnyInputDown() =>
                (kb != null && kb.anyKey.isPressed) ||
                (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed ||
                                   (mouse.forwardButton?.isPressed ?? false) || (mouse.backButton?.isPressed ?? false)));

            int Weight(KeyChord kc)
            {
                int w = (kc.Keys?.Length ?? 0);
                if (kc.Ctrl) w++;
                if (kc.Shift) w++;
                if (kc.Alt) w++;
                return w;
            }

            while (_isCapturing && Time.unscaledTime < startTimeout)
            {
                if (kb?.escapeKey?.wasPressedThisFrame ?? false)
                {
                    CancelChordCapture();
                    yield break;
                }

                if (!started)
                {
                    if ((kb?.anyKey.wasPressedThisFrame ?? false) ||
                        (mouse?.leftButton.wasPressedThisFrame ?? false) ||
                        (mouse?.rightButton.wasPressedThisFrame ?? false) ||
                        (mouse?.middleButton.wasPressedThisFrame ?? false) ||
                        (mouse?.forwardButton?.wasPressedThisFrame ?? false) ||
                        (mouse?.backButton?.wasPressedThisFrame ?? false))
                    {
                        started = true;
                        windowEnd = Time.unscaledTime + 1.0f;
                        if (_captureLabel != null) _captureLabel.text = "Release keys to confirm...";
                    }
                }
                else
                {
                    var kc = SnapshotChord();
                    bool any = (kc.Keys?.Length ?? 0) > 0 || kc.Ctrl || kc.Shift || kc.Alt;
                    if (any)
                    {
                        int w = Weight(kc);
                        if (w > bestWeight)
                        {
                            best = kc; bestWeight = w; lastBestAt = Time.unscaledTime;
                            if (_captureLabel != null) _captureLabel.text = KeyChordToSpec(kc) + " - Release to confirm";
                        }
                    }

                    bool allReleased = !HasAnyInputDown();
                    if (bestWeight >= 0 && (allReleased || Time.unscaledTime >= windowEnd || Time.unscaledTime - lastBestAt > 0.15f))
                    {
                        _onChordCaptured?.Invoke(KeyChordToSpec(best));
                        _isCapturing = false;
                        if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
                        HidePanelDuringCapture(false);
                        yield break;
                    }
                }

                yield return null;
            }

            CancelChordCapture();
        }
    }
}

