// DashFall.UI.cs - the client config panel, on the Ponce shared design system.
//
// Every colour and every widget recipe now comes from DashFallTheme, which is the same design
// system OWP, MaxPractice and PonceArenaTweaks use. Only the accent differs between the four,
// and ours is orange, so a player can tell which mod's panel is open at a glance. Nothing in
// this file declares a palette of its own any more; if a colour is missing, it belongs in
// DashFallTheme.
//
// The panel is opened and closed by F4 alone. It used to be a ModMenuHub entry, which meant the
// hub owned the cursor and the vanilla menu buttons on our behalf; with the hub gone this file
// owns both, so the open/close pair below snapshots and restores the cursor and the game's
// mouse-required flag itself.

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
        private bool _footerResetArmed;       // footer RESET pressed once; waiting for the confirm press
        private bool _serverResetArmed;       // RESET pressed once; waiting for the confirm press
        private CompetitiveAdjustments.ServerConfig _serverEditCfg; // isolated editor copy; null = re-clone from live

        private Action<string> _onChordCaptured;
        private bool _panelHiddenForCapture;
        private UITK.Button _captureButton;   // the BIND button currently listening, so it can be un-painted

        // Authoritative open/closed state. Deliberately NOT derived from the panel's display,
        // because HidePanelDuringCapture hides the panel while it is logically still open, and a
        // display-driven toggle would open a second session on top of a rebind.
        private bool _panelVisible;

        // The game's own mouse-required flag, which ModMenuHub used to own for us. Typing in the
        // SEARCH box or an admin field otherwise also drives the skater.
        private bool _savedMouseRequired;
        private bool _prevMouseRequired;

        // Font. Kept as thin wrappers over DashFallTheme so the other partials that already call
        // ForceUIFont (the version popup) keep compiling, and so there is one font resolve.
        private static Font GetUIFont() => DashFallTheme.GetUIFont();

        private static void ForceUIFont(UITK.VisualElement ve) => DashFallTheme.ForceUIFont(ve);

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

        // A themed section card that the search filter understands: "cfg-section" on the card and
        // "cfg-header" on the header block inside it, so a query can drop the header without
        // losing the rows and drop the whole card when none of its rows match.
        private static UITK.VisualElement AddCfgSection(UITK.VisualElement parent, string title, string subtitle = null)
        {
            var card = DashFallTheme.AddSection(parent, title, subtitle);
            card.AddToClassList("cfg-section");
            if (card.childCount > 0) card[0].AddToClassList("cfg-header");
            return card;
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

            // A card whose every row was filtered out would leave an empty stub of padding
            // behind, so the card goes with its rows; only cards that still have a hit survive,
            // and their headers stay hidden so the results read as one flat list.
            foreach (var card in _actionsSection.Query(className: "cfg-section").ToList())
            {
                var header = card.childCount > 0 ? card[0] : null;
                if (header != null && header.ClassListContains("cfg-header"))
                    header.style.display = searching ? UITK.DisplayStyle.None : UITK.DisplayStyle.Flex;

                bool anyVisible = false;
                foreach (var row in card.Query(className: "cfg-row").ToList())
                {
                    if (row.style.display.value == UITK.DisplayStyle.Flex) { anyVisible = true; break; }
                }
                card.style.display = (!searching || anyVisible) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            }
        }

        // ========== PANEL BUILD ==========
        private void BuildDashFallPanel()
        {
            if (_dfPanel != null) return;

            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root == null) return;

            _dfBackdrop = DashFallTheme.MakeBackdrop();
            _dfBackdrop.RegisterCallback<UITK.PointerUpEvent>(_ => CloseDashFallPanel());

            // MakePanelRoot registers the inside-click StopPropagation itself, so a click on a row
            // cannot reach the backdrop's close handler.
            _dfPanel = DashFallTheme.MakePanelRoot();

            // Search box: filters the rows on the active tab by label text. It lives in the header's
            // top-right dead space rather than in a row of its own above the list, because the title
            // block only fills the left half of the header and a full-width search row was spending
            // a whole line of vertical space to say very little.
            var searchAside = new UITK.VisualElement();
            searchAside.style.flexDirection = UITK.FlexDirection.Row;
            searchAside.style.alignItems = UITK.Align.Center;

            var searchLabel = DashFallTheme.MakeLabel("SEARCH", 11, DashFallTheme.TextMuted);
            searchLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            searchLabel.style.letterSpacing = 3;
            searchLabel.style.marginRight = 8;
            searchLabel.style.flexShrink = 0;
            searchAside.Add(searchLabel);

            _searchField = new TextField { value = _searchQuery };
            _searchField.style.width = 200;
            _searchField.style.height = 28;
            _searchField.style.marginLeft = 0;
            _searchField.style.marginRight = 0;
            DashFallTheme.StyleTextField(_searchField);
            _searchField.RegisterValueChangedCallback(e =>
            {
                _searchQuery = e.newValue ?? "";
                ApplySearchFilter();
            });
            searchAside.Add(_searchField);

            // "COMPADJUST" has no space, so the accent half is split explicitly rather than by the
            // first-space rule, and it keeps its leading space because the two halves are separate
            // Labels sitting flush against each other.
            _dfPanel.Add(DashFallTheme.MakeHeader("COMP", " ADJUST",
                DashFallTheme.SubtitleText("Keybinds and config", "F4"), searchAside));

            // Tab strip
            var tabStrip = DashFallTheme.MakeTabStrip();
            _skaterTabBtn = DashFallTheme.MakeTab("SKATER", _activeTab == ActiveTab.Skater, () => SwitchToTab(ActiveTab.Skater));
            _goalieTabBtn = DashFallTheme.MakeTab("GOALIE", _activeTab == ActiveTab.Goalie, () => SwitchToTab(ActiveTab.Goalie));
            _serverTabBtn = DashFallTheme.MakeTab("SERVER", _activeTab == ActiveTab.Server, () => SwitchToTab(ActiveTab.Server));
            _settingsTabBtn = DashFallTheme.MakeTab("SETTINGS", _activeTab == ActiveTab.Settings, () => SwitchToTab(ActiveTab.Settings));

            DashFallTheme.AddTabHover(_skaterTabBtn, () => _activeTab == ActiveTab.Skater);
            DashFallTheme.AddTabHover(_goalieTabBtn, () => _activeTab == ActiveTab.Goalie);
            DashFallTheme.AddTabHover(_serverTabBtn, () => _activeTab == ActiveTab.Server);
            DashFallTheme.AddTabHover(_settingsTabBtn, () => _activeTab == ActiveTab.Settings);

            // Every tab carries a right margin for spacing; the last one must not, or SETTINGS
            // sits short of the right edge while SKATER hugs the left.
            _settingsTabBtn.style.marginRight = 0;

            tabStrip.Add(_skaterTabBtn);
            tabStrip.Add(_goalieTabBtn);
            tabStrip.Add(_serverTabBtn);
            tabStrip.Add(_settingsTabBtn);
            _dfPanel.Add(tabStrip);

            // Scrolling body. StyleScrollView pins min-height to 0, which is what stops a long
            // list pushing the footer past the panel's clipped bottom edge.
            _scrollView = new UITK.ScrollView();
            DashFallTheme.StyleScrollView(_scrollView);
            _dfPanel.Add(_scrollView);

            _actionsSection = new UITK.VisualElement();
            _scrollView.Add(_actionsSection);

            BuildActionsUI();

            // Footer: COFFEE alone on the left, then the closing actions on the right. Every footer
            // button is neutral, including CLOSE. An accent fill there read as the thing to press
            // when it is only the way out, and it put the loudest colour on the panel next to the
            // one destructive action.
            var footer = DashFallTheme.MakeFooter();

            var donate = new UITK.Button(() => Application.OpenURL("https://buymeacoffee.com/amikiir")) { text = "Coffee?" };
            DashFallTheme.StyleFooterButton(donate);
            donate.style.marginLeft = 0;
            DashFallTheme.AddButtonFlash(donate);
            footer.Add(donate);
            footer.Add(DashFallTheme.MakeSpacer());

            // Two-click, matching the SERVER tab's reset. This wipes all seven skater bind lists,
            // all nine goalie ones and every trigger type, and the next close on any path writes
            // that to disk, so it cannot sit one misaimed click away from CLOSE looking identical
            // to it. No accent flash for the same reason the SERVER one has none: the flash caches
            // a base colour and repaints over the armed fill on the next pointer-leave.
            UITK.Button resetBtn = null;
            resetBtn = new UITK.Button(() =>
            {
                if (!_footerResetArmed)
                {
                    _footerResetArmed = true;
                    resetBtn.text = "Confirm reset";
                    DashFallTheme.SetArmedLook(resetBtn, true);
                    return;
                }

                _footerResetArmed = false;
                resetBtn.text = "Reset to defaults";
                DashFallTheme.SetArmedLook(resetBtn, false);

                ResetToDefaults();
                ResetInputActions();
                RefreshActionsUI();
            }) { text = _footerResetArmed ? "Confirm reset" : "Reset to defaults" };
            DashFallTheme.StyleFooterButton(resetBtn);
            resetBtn.style.paddingLeft = 14; resetBtn.style.paddingRight = 14;
            DashFallTheme.SetArmedLook(resetBtn, _footerResetArmed);
            footer.Add(resetBtn);

            var closeBtn = new UITK.Button(() =>
            {
                DashFallConfigLoader.SaveSkaterConfig(_skater);
                DashFallConfigLoader.SaveGoalieConfig(_goalie);
                RebuildLookups();
                ResetInputActions();
                CloseDashFallPanel();
            }) { text = "Close" };
            DashFallTheme.StyleFooterButton(closeBtn);
            DashFallTheme.AddButtonFlash(closeBtn);
            footer.Add(closeBtn);

            _dfPanel.Add(footer);

            // Backdrop first so the panel paints on top of it; the panel is its child, so the two
            // displays are flipped together.
            root.Add(_dfBackdrop);
            _dfBackdrop.Add(_dfPanel);
        }

        private void SwitchToTab(ActiveTab tab)
        {
            _serverResetArmed = false; // leaving the tab cancels a pending reset confirm
            _footerResetArmed = false; // and so does the footer's, since the footer is rebuilt too
            DashFallTheme.CloseAllPickers(); // the rows a list was anchored to are about to be torn down
            _activeTab = tab;
            UpdateTabStyles();
            RefreshActionsUI();
        }

        private void UpdateTabStyles()
        {
            DashFallTheme.SetTabVisual(_skaterTabBtn, _activeTab == ActiveTab.Skater);
            DashFallTheme.SetTabVisual(_goalieTabBtn, _activeTab == ActiveTab.Goalie);
            DashFallTheme.SetTabVisual(_serverTabBtn, _activeTab == ActiveTab.Server);
            DashFallTheme.SetTabVisual(_settingsTabBtn, _activeTab == ActiveTab.Settings);
        }

        /// <summary>
        /// The free blade rows for one role. Both tabs call this, so the two positions get the
        /// same two controls backed by their own pair of fields.
        ///
        /// These used to be one shared pair on the SETTINGS tab. A goalie holding an angle across
        /// the crease and a skater carrying the puck are not asking the same thing of the blade,
        /// and one range had to serve both. They live on the role tabs now because that is where
        /// the player is already thinking about that position.
        /// </summary>
        private void AddFreeBladeSection(bool goalie)
        {
            var clientConfig = DashFallConfigLoader.ClientConfig;
            if (clientConfig == null) return;

            string who = goalie ? "goalie" : "skater";
            var blade = AddCfgSection(_actionsSection, "Blade",
                "Applies only while playing " + who + ". The other position has its own pair.");

            bool locked; float min, max;
            clientConfig.GetFreeBlade(goalie, out locked, out min, out max);

            blade.Add(MakeToggleRow("FREE BLADE SPIN LOCK",
                "On (default) keeps the vanilla blade range. Turn OFF for endless spin with no stop at either end",
                locked, (val) =>
            {
                if (goalie) clientConfig.FreeBladeSpinLockEnabledGoalie = val;
                else        clientConfig.FreeBladeSpinLockEnabledSkater = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            // One row, two draggers. Two independent float rows let the user push MIN above
            // MAX, which produced an empty range that Mathf.Clamp resolves to a single
            // value, and the blade froze there. A range control cannot express that state.
            blade.Add(MakeRangeSliderRow(
                "FREE BLADE SPIN RANGE",
                "Lower and upper bound, used only while the lock above is on",
                min, max,
                FreeBladeSpinRange.LimitMin, FreeBladeSpinRange.LimitMax,
                (lo, hi) =>
                {
                    if (goalie) { clientConfig.FreeBladeSpinMinGoalie = lo; clientConfig.FreeBladeSpinMaxGoalie = hi; }
                    else        { clientConfig.FreeBladeSpinMinSkater = lo; clientConfig.FreeBladeSpinMaxSkater = hi; }
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                }));
        }

        private void BuildActionsUI()
        {
            _actionsSection.Clear();

            // Get server features (if connected)
            var features = PoncePuck.Keybinds.ServerBridge.ReceivedFeatures;
            bool hasFeatures = PoncePuck.Keybinds.ServerBridge.HasReceivedFeatures;

            if (_activeTab == ActiveTab.Skater)
            {
                AddFreeBladeSection(goalie: false);

                var sec = AddCfgSection(_actionsSection, "Skater binds", "Used while skating out. A row greyed out is one the server has switched off.");
                sec.Add(MakeBindRow("DIVE", () => _skater.divekey, v => _skater.divekey = v,
                    () => _skater.divekeytype, v => _skater.divekeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterDiveEnabled));
                sec.Add(MakeBindRow("TWIST LEFT", () => _skater.twistleftkey, v => _skater.twistleftkey = v,
                    () => _skater.twistleftkeytype, v => _skater.twistleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterTwistEnabled));
                sec.Add(MakeBindRow("TWIST RIGHT", () => _skater.twistrightkey, v => _skater.twistrightkey = v,
                    () => _skater.twistrightkeytype, v => _skater.twistrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.SkaterTwistEnabled));
                sec.Add(MakeBindRow("SLIDE DI LEFT", () => _skater.slideinfluenceleftkey, v => _skater.slideinfluenceleftkey = v,
                    () => _skater.slideinfluenceleftkeytype, v => _skater.slideinfluenceleftkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI RIGHT", () => _skater.slideinfluencerightkey, v => _skater.slideinfluencerightkey = v,
                    () => _skater.slideinfluencerightkeytype, v => _skater.slideinfluencerightkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI FORWARD", () => _skater.slideinfluenceforwardkey, v => _skater.slideinfluenceforwardkey = v,
                    () => _skater.slideinfluenceforwardkeytype, v => _skater.slideinfluenceforwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI BACKWARD", () => _skater.slideinfluencebackwardkey, v => _skater.slideinfluencebackwardkey = v,
                    () => _skater.slideinfluencebackwardkeytype, v => _skater.slideinfluencebackwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.SkaterSlideInfluenceEnabled));
            }
            else if (_activeTab == ActiveTab.Goalie)
            {
                AddFreeBladeSection(goalie: true);

                var sec = AddCfgSection(_actionsSection, "Goalie binds", "Used while in net. A row greyed out is one the server has switched off.");
                sec.Add(MakeBindRow("DIVE", () => _goalie.divekey, v => _goalie.divekey = v,
                    () => _goalie.divekeytype, v => _goalie.divekeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieDiveEnabled));
                sec.Add(MakeBindRow("STANDING DASH LEFT", () => _goalie.standingdashleftkey, v => _goalie.standingdashleftkey = v,
                    () => _goalie.standingdashleftkeytype, v => _goalie.standingdashleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieStandingDashEnabled));
                sec.Add(MakeBindRow("STANDING DASH RIGHT", () => _goalie.standingdashrightkey, v => _goalie.standingdashrightkey = v,
                    () => _goalie.standingdashrightkeytype, v => _goalie.standingdashrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieStandingDashEnabled));
                sec.Add(MakeBindRow("TWIST LEFT", () => _goalie.twistleftkey, v => _goalie.twistleftkey = v,
                    () => _goalie.twistleftkeytype, v => _goalie.twistleftkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieTwistEnabled));
                sec.Add(MakeBindRow("TWIST RIGHT", () => _goalie.twistrightkey, v => _goalie.twistrightkey = v,
                    () => _goalie.twistrightkeytype, v => _goalie.twistrightkeytype = v, BindRowType.Pressable,
                    !hasFeatures || features.GoalieTwistEnabled));
                sec.Add(MakeBindRow("SLIDE DI LEFT", () => _goalie.slideinfluenceleftkey, v => _goalie.slideinfluenceleftkey = v,
                    () => _goalie.slideinfluenceleftkeytype, v => _goalie.slideinfluenceleftkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI RIGHT", () => _goalie.slideinfluencerightkey, v => _goalie.slideinfluencerightkey = v,
                    () => _goalie.slideinfluencerightkeytype, v => _goalie.slideinfluencerightkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI FORWARD", () => _goalie.slideinfluenceforwardkey, v => _goalie.slideinfluenceforwardkey = v,
                    () => _goalie.slideinfluenceforwardkeytype, v => _goalie.slideinfluenceforwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
                sec.Add(MakeBindRow("SLIDE DI BACKWARD", () => _goalie.slideinfluencebackwardkey, v => _goalie.slideinfluencebackwardkey = v,
                    () => _goalie.slideinfluencebackwardkeytype, v => _goalie.slideinfluencebackwardkeytype = v, BindRowType.Holdable,
                    !hasFeatures || features.GoalieSlideInfluenceEnabled));
            }
            else if (_activeTab == ActiveTab.Server)
            {
                BuildServerConfigUI();
            }
            else if (_activeTab == ActiveTab.Settings)
            {
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
                _actionsSection.Add(DashFallTheme.MakeNote(
                    "Not connected to a server with CompetitiveAdjustments.", DashFallTheme.TextDim));
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
                _actionsSection.Add(DashFallTheme.MakeNote("Waiting for server config...", DashFallTheme.TextDim));
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
            btnRow.style.marginBottom = 12;
            btnRow.Add(MakeServerEditorButton("Save & apply", OnServerSaveApply, DashFallTheme.ButtonVariant.Primary));
            btnRow.Add(MakeServerEditorButton("Export", OnServerExport, DashFallTheme.ButtonVariant.Secondary));

            // The reset button is the one two-click destructive action here, so it carries the
            // armed fill rather than the accent flash: the flash caches a base colour and would
            // repaint over the armed state on the next pointer-leave.
            var resetBtn = MakeServerEditorButton(_serverResetArmed ? "Confirm reset" : "Reset to defaults",
                OnServerResetDefaults, DashFallTheme.ButtonVariant.Secondary, addFlash: false);
            DashFallTheme.SetArmedLook(resetBtn, _serverResetArmed);
            btnRow.Add(resetBtn);
            body.Add(btnRow);

            var masters = AddCfgSection(body, "Master enables");
            masters.Add(MakeEditorToggleRow("Enable Dashfall", cfg.EnableDashfall, v => cfg.EnableDashfall = v));
            masters.Add(MakeEditorToggleRow("Enable CompAdjust", cfg.EnableCompAdjust, v => cfg.EnableCompAdjust = v));
            masters.Add(MakeEditorToggleRow("Enable CompTweaks", cfg.EnableCompTweaks, v => cfg.EnableCompTweaks = v));

            BuildEditableSection(AddCfgSection(body, "Dashfall"), cfg.Dashfall);
            BuildEditableSection(AddCfgSection(body, "CompAdjust"), cfg.CompAdjust);
            BuildEditableSection(AddCfgSection(body, "CompTweaks"), cfg.CompTweaks);

            _actionsSection.Add(body);
        }

        // Lock/status bar at the top of the SERVER tab. It is a section card rather than a row,
        // because it is the header for everything below it, not a setting.
        private void BuildServerLockBar(bool unlocked, bool authed, bool isServer)
        {
            var bar = new UITK.VisualElement();
            bar.style.flexDirection = UITK.FlexDirection.Column;
            bar.style.marginBottom = 14;
            bar.style.paddingLeft = 12; bar.style.paddingRight = 12;
            bar.style.paddingTop = 12; bar.style.paddingBottom = 12;
            bar.style.backgroundColor = DashFallTheme.SectionBg;
            DashFallTheme.SetUniformRadius(bar, DashFallTheme.SECTION_RADIUS);
            DashFallTheme.SetUniformBorder(bar, 1f, DashFallTheme.SectionBorder);

            var topRow = new UITK.VisualElement();
            topRow.style.flexDirection = UITK.FlexDirection.Row;
            topRow.style.alignItems = UITK.Align.Center;

            // The lock state is carried twice on purpose, by the section bar's hue and by the
            // pill, because the bar reads at a glance while the pill spells it out. Danger for
            // locked rather than a second warm tone: with orange as the ambient accent a warm
            // "locked" and an orange "unlocked" would be the same colour to a quick look.
            topRow.Add(DashFallTheme.MakeAccentBar(unlocked ? DashFallTheme.Accent : DashFallTheme.Danger));

            var title = DashFallTheme.MakeLabel("ADMIN EDITOR", 16, DashFallTheme.TextPrimary);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = 3;
            title.style.flexGrow = 1;
            topRow.Add(title);

            var statePill = DashFallTheme.MakeStatePill(unlocked ? "UNLOCKED" : "LOCKED", unlocked);
            statePill.style.minWidth = 76;
            statePill.style.marginLeft = 8;
            topRow.Add(statePill);

            if (unlocked)
            {
                // Drop back to read-only locally without disconnecting.
                topRow.Add(MakeCompactButton("Lock", () =>
                {
                    _serverUserLocked = true;
                    RefreshActionsUI();
                }));
            }
            else if (authed)
            {
                // Locked only because the user pressed LOCK; no password needed.
                topRow.Add(MakeCompactButton("Unlock", () =>
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
                    pwRow.style.marginTop = 10;

                    // Masked by default (fixed length, so it does not even leak how
                    // long the password is) until the host presses SHOW.
                    string shown = _serverPasswordRevealed ? pwd : "••••••••";
                    var pwLabel = DashFallTheme.MakeLabel("Editor password: " + shown, 12, DashFallTheme.TextMuted);
                    pwLabel.style.flexGrow = 1;
                    pwRow.Add(pwLabel);

                    pwRow.Add(MakeCompactButton(_serverPasswordRevealed ? "Hide" : "Show", () =>
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
                entry.style.marginTop = 10;

                var pw = new TextField { isPasswordField = true };
                pw.style.flexGrow = 1;
                pw.style.height = 28;
                DashFallTheme.StyleTextField(pw);
                entry.Add(pw);

                entry.Add(MakeCompactButton("Unlock", () =>
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
                // Muted, not danger: this line carries progress ("Checking...", "Applied.") as
                // often as it carries a refusal, and painting it red either way would cry wolf.
                var st = DashFallTheme.MakeNote(status, DashFallTheme.TextMuted);
                st.style.marginTop = 10;
                st.style.marginBottom = 0;
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
            var row = DashFallTheme.MakeRow();
            MarkSearchable(row, title);
            row.Add(DashFallTheme.MakeRowText(title, null));

            var toggle = new Toggle { value = currentValue };
            DashFallTheme.StyleToggle(toggle);
            toggle.RegisterValueChangedCallback(evt => onChanged?.Invoke(evt.newValue));
            row.Add(toggle);

            return row;
        }

        private UITK.Button MakeServerEditorButton(string text, Action onClick,
            DashFallTheme.ButtonVariant variant, bool addFlash = true)
        {
            var b = new UITK.Button(onClick) { text = text };
            DashFallTheme.StyleButton(b, variant);
            b.style.flexGrow = 1;
            b.style.marginLeft = 0;
            b.style.marginRight = DashFallTheme.GAP;
            // StylePrimaryButton already carries the flash, so adding another would register a
            // second pair of handlers on the same button.
            if (addFlash && variant != DashFallTheme.ButtonVariant.Primary) DashFallTheme.AddButtonFlash(b);
            return b;
        }

        // Compact, non-stretching button for the lock bar (next to the title /
        // password field), so it does not expand to half the row width.
        private UITK.Button MakeCompactButton(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            DashFallTheme.StyleCompactButton(b);
            DashFallTheme.AddButtonFlash(b);
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
            return _activeTab == ActiveTab.Server && _panelVisible;
        }

        private void RefreshServerTabIfOpen()
        {
            if (IsServerTabOpen()) RefreshActionsUI();
        }

        private void BuildSettingsUI()
        {
            var clientConfig = DashFallConfigLoader.ClientConfig;

            // MINIMAP TWEAKS, PUCK SCALE (+X/Y/Z) and BUTTERFLY PAD OFFSET used to sit here.
            // The minimap rescale is unconditional now, because the vanilla minimap is
            // simply wrong on a resized rink. The puck and pad values are server state that
            // the sync path overwrites, so a local edit held only until the next packet
            // while disagreeing with every other client in the meantime; the config fields
            // remain as the sync slots they always were.

            var trail = AddCfgSection(_actionsSection, "Sprint shoulder trail");

            trail.Add(MakeToggleRow("SPRINT SHOULDER TRAIL", "Show white shoulder trails while sprinting", clientConfig.EnableSprintShoulderTrail, (val) =>
            {
                clientConfig.EnableSprintShoulderTrail = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            trail.Add(MakeFloatRow("TRAIL TIME", "Seconds the trail persists", clientConfig.SprintShoulderTrailTime, 0.05f, 3f, (val) =>
            {
                clientConfig.SprintShoulderTrailTime = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            trail.Add(MakeFloatRow("TRAIL WIDTH", "Trail width in meters", clientConfig.SprintShoulderTrailWidth, 0.01f, 0.5f, (val) =>
            {
                clientConfig.SprintShoulderTrailWidth = val;
                DashFallConfigLoader.SaveClientConfig(clientConfig);
            }));

            // Colour and opacity are one choice, so they are one control: the swatch shows
            // the colour at its actual alpha over a light plate, and the hex field stays as
            // the typeable path for someone matching an exact team colour.
            trail.Add(MakeColorPickerRow(
                "TRAIL START COLOR", "Colour and opacity at the trail head",
                clientConfig.SprintShoulderTrailStartColorHex, clientConfig.SprintShoulderTrailStartAlpha,
                (hex, alpha) =>
                {
                    clientConfig.SprintShoulderTrailStartColorHex = hex;
                    clientConfig.SprintShoulderTrailStartAlpha = alpha;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                }));

            trail.Add(MakeColorPickerRow(
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
            var debug = AddCfgSection(_actionsSection, "Debug", "Diagnostics for this mod. The clip brush rows paint collider geometry over the rink.");

            debug.Add(MakeToggleRow("CLIENT DEBUG LOG", "Enable debug logging and show the debug tools below", clientConfig.EnableClientDebug, (val) =>
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
                debug.Add(MakeToggleRow("SHOW ARENA CLIP BRUSHES", "Visualise arena/board collider geometry (debug)", clientConfig.ShowArenaClipBrushes, (val) =>
                {
                    clientConfig.ShowArenaClipBrushes = val;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyArena(val);
                }));

                debug.Add(MakeToggleRow("SHOW PLAYER CLIP BRUSHES", "Visualise player body collider geometry (debug)", clientConfig.ShowPlayerClipBrushes, (val) =>
                {
                    clientConfig.ShowPlayerClipBrushes = val;
                    DashFallConfigLoader.SaveClientConfig(clientConfig);
                    CompetitivePuckTweaks.src.ClientClipBrushes.ApplyPlayer(val);
                }));

                // Preview the out-of-date version popup without a real Workshop update.
                debug.Add(MakeButtonRow("TEST VERSION POPUP", "Preview the 'mod out of date' popup", "Show",
                    () => ForceShowVersionPopupForTest()));

                // Its own row, because the server-rejected wording is not reachable by
                // playing: see ForceShowServerRejectedForTest.
                debug.Add(MakeButtonRow("TEST SERVER-REJECTED POPUP", "Preview the 'this server needs a newer build' popup", "Show",
                    () => ForceShowServerRejectedForTest()));
            }

            // Closing note about where the rest of the settings live. Above the cards would be
            // wrong here: it is a footnote, not a preamble.
            bool hasFeatures = PoncePuck.Keybinds.ServerBridge.HasReceivedFeatures;
            var note = hasFeatures
                ? DashFallTheme.MakeNote(
                    "Keybinds for features are in the SKATER and GOALIE tabs. The SERVER tab shows which features this server has enabled.",
                    DashFallTheme.TextMuted)
                : DashFallTheme.MakeNote("Connect to a server to see which features are enabled.", DashFallTheme.TextDim);
            note.style.marginTop = 4;
            _actionsSection.Add(note);
        }

        private UITK.VisualElement MakeToggleRow(string title, string description, bool currentValue, Action<bool> onChanged)
        {
            var row = DashFallTheme.MakeRow();
            MarkSearchable(row, title);
            row.Add(DashFallTheme.MakeRowText(title, description));

            var toggle = new Toggle { value = currentValue };
            DashFallTheme.StyleToggle(toggle);
            toggle.RegisterValueChangedCallback(evt =>
            {
                onChanged?.Invoke(evt.newValue);
                RefreshActionsUI(); // Refresh to show/hide dependent rows
            });
            row.Add(toggle);

            return row;
        }

        // A label/description row with a single action button on the right. Same visual
        // frame as MakeToggleRow but the control is a Button instead of a checkbox.
        private UITK.VisualElement MakeButtonRow(string title, string description, string buttonText, Action onClick)
        {
            var row = DashFallTheme.MakeRow();
            MarkSearchable(row, title);
            row.Add(DashFallTheme.MakeRowText(title, description));

            var btn = new UITK.Button(() => onClick?.Invoke()) { text = buttonText };
            DashFallTheme.StyleCompactButton(btn);
            DashFallTheme.AddButtonFlash(btn);
            row.Add(btn);

            return row;
        }

        private UITK.VisualElement MakeFloatRow(string title, string description, float currentValue, float min, float max, Action<float> onChanged)
        {
            var row = DashFallTheme.MakeRow();
            MarkSearchable(row, title);
            row.Add(DashFallTheme.MakeRowText(title, description));

            var input = new TextField();
            input.value = currentValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            DashFallTheme.StyleValueField(input, 96f, true);
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
            var row = DashFallTheme.MakeRow();
            MarkSearchable(row, title);
            row.Add(DashFallTheme.MakeRowText(title, description));

            // Order the incoming pair rather than trusting it: a config written by the old
            // two-row UI can already be crossed, and that saved state is exactly the bug.
            float startLo = Mathf.Clamp(Mathf.Min(currentMin, currentMax), limitMin, limitMax);
            float startHi = Mathf.Clamp(Mathf.Max(currentMin, currentMax), limitMin, limitMax);

            var lowField = MakeRangeNumberField(startLo);
            var highField = MakeRangeNumberField(startHi);

            var slider = new UITK.MinMaxSlider(startLo, startHi, limitMin, limitMax);
            slider.style.flexGrow = 1;
            slider.style.flexBasis = 0;
            // Wider than the single sliders' 6, because each handle overhangs its end of the
            // track by half its width and would otherwise touch the number fields.
            slider.style.marginLeft = DashFallTheme.SLIDER_THUMB / 2f + 2f;
            slider.style.marginRight = DashFallTheme.SLIDER_THUMB / 2f + 2f;
            DashFallTheme.StyleMinMaxSlider(slider);

            // A fixed-width control cluster, because a MinMaxSlider maps pointer x to a value
            // and needs a stable track; the text column takes whatever is left.
            var controls = new UITK.VisualElement();
            controls.style.flexDirection = UITK.FlexDirection.Row;
            controls.style.alignItems = UITK.Align.Center;
            controls.style.flexShrink = 0;
            controls.style.width = 330;

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

            controls.Add(lowField);
            controls.Add(slider);
            controls.Add(highField);
            row.Add(controls);

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
            DashFallTheme.StyleValueField(field, 54f, true);
            return field;
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

            // The row is a column: the header line, then the picker that expands under it. The
            // hover swap is left off, because a row that grows a panel under it is already
            // signposted by its PICK button.
            var container = DashFallTheme.MakeRow(false);
            container.style.flexDirection = UITK.FlexDirection.Column;
            container.style.alignItems = UITK.Align.Stretch;
            MarkSearchable(container, title);

            var header = new UITK.VisualElement();
            header.style.flexDirection = UITK.FlexDirection.Row;
            header.style.alignItems = UITK.Align.Center;
            header.Add(DashFallTheme.MakeRowText(title, description));

            // Alpha is shown by layering the colour over a light plate: at alpha 0 the
            // swatch reads as the plate, which is the honest preview of an invisible trail.
            // The plate is TextMuted rather than a chrome tone because its job is to be light
            // enough for a translucent colour to visibly sit on.
            var swatchPlate = new UITK.VisualElement();
            swatchPlate.style.width = 48;
            swatchPlate.style.height = 28;
            swatchPlate.style.flexShrink = 0;
            swatchPlate.style.backgroundColor = DashFallTheme.TextMuted;
            DashFallTheme.SetUniformRadius(swatchPlate, 4f);
            DashFallTheme.SetUniformBorder(swatchPlate, 1f, DashFallTheme.PanelBorder);

            var swatchFill = new UITK.VisualElement();
            swatchFill.style.flexGrow = 1;
            swatchPlate.Add(swatchFill);

            var hexField = new TextField();
            hexField.value = NormalizeHex(currentHex) ?? "#FFFFFF";
            DashFallTheme.StyleValueField(hexField, 88f, true);
            hexField.style.whiteSpace = UITK.WhiteSpace.NoWrap;

            var toggleButton = new Button { text = "Pick" };
            DashFallTheme.StyleCompactButton(toggleButton);
            toggleButton.style.minWidth = 76;
            DashFallTheme.AddButtonFlash(toggleButton);

            header.Add(swatchPlate);
            header.Add(hexField);
            header.Add(toggleButton);
            container.Add(header);

            var picker = new UITK.VisualElement();
            picker.style.flexDirection = UITK.FlexDirection.Column;
            picker.style.display = UITK.DisplayStyle.None;
            picker.style.marginTop = 8;
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
                swatchFill.style.backgroundColor = new Color(rgb.r, rgb.g, rgb.b, a);
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
            swatchFill.style.backgroundColor = new Color(startColor.r, startColor.g, startColor.b, alpha);

            return container;
        }

        private static Color ParseHexOrWhite(string hex)
        {
            string normalized = NormalizeHex(hex);
            if (normalized != null && ColorUtility.TryParseHtmlString(normalized, out var parsed)) return parsed;
            return Color.white;
        }

        /// <summary>One labelled channel slider inside an expanded colour picker.</summary>
        private UITK.Slider MakePickerSlider(
            string caption, float value, float min, float max, UITK.VisualElement parent)
        {
            var line = new UITK.VisualElement();
            line.style.flexDirection = UITK.FlexDirection.Row;
            line.style.alignItems = UITK.Align.Center;
            line.style.height = 30;

            var caLabel = DashFallTheme.MakeLabel(caption, 11, DashFallTheme.TextMuted);
            caLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            caLabel.style.letterSpacing = 1;
            caLabel.style.minWidth = 96;
            caLabel.style.maxWidth = 96;
            line.Add(caLabel);

            var slider = new UITK.Slider(min, max);
            slider.style.flexGrow = 1;
            slider.style.flexBasis = 0;
            slider.value = Mathf.Clamp(value, min, max);
            DashFallTheme.StyleSlider(slider);
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
            // Hover only when the row is live: a disabled row that lights up under the cursor
            // invites a click, and the hover swap would also repaint over the disabled fill.
            var row = DashFallTheme.MakeRow(enabled);
            MarkSearchable(row, action);
            DashFallTheme.SetRowEnabledLook(row, enabled);

            var text = DashFallTheme.MakeRowText(action, null);
            text.style.minWidth = 150;
            if (!enabled)
            {
                // The one place a row description is Danger: the server has taken this bind away
                // and no amount of local editing will bring it back.
                var why = DashFallTheme.MakeRowDescription("DISABLED BY SERVER");
                why.style.color = DashFallTheme.DangerFaded;
                why.style.unityFontStyleAndWeight = FontStyle.Bold;
                text.Add(why);
            }
            row.Add(text);

            // Chips container (shows bound keys)
            var chipsRoot = new UITK.VisualElement();
            chipsRoot.style.flexDirection = UITK.FlexDirection.Row;
            chipsRoot.style.flexWrap = UITK.Wrap.Wrap;
            chipsRoot.style.justifyContent = UITK.Justify.FlexEnd;
            chipsRoot.style.alignItems = UITK.Align.Center;
            chipsRoot.style.flexGrow = 1;
            chipsRoot.style.flexShrink = 1;
            chipsRoot.style.minWidth = 0;
            chipsRoot.style.marginRight = 4;
            row.Add(chipsRoot);

            // Buttons container
            var right = new UITK.VisualElement();
            right.style.flexDirection = UITK.FlexDirection.Row;
            right.style.alignItems = UITK.Align.Center;
            right.style.flexShrink = 0;
            row.Add(right);

            // BIND button
            UITK.Button bindBtn = null;
            bindBtn = new UITK.Button(() =>
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
                }, bindBtn);
            });
            DashFallTheme.StyleRowButton(bindBtn, 84f, "BIND");
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

            // MakePicker, not a real DropdownField: this row is exactly the case that broke one.
            // The trigger names are long ("DOUBLE PRESS") and the slot is narrow because the row
            // already carries a label column, a chip strip and BIND, so the built-in control's
            // unlaid-out arrow ended up printed on top of the value text, and its popup list came
            // up as unstyled light grey. See the MakePicker docs in DashFall.Theme.cs.
            var dropdown = DashFallTheme.MakePicker(choices, currentIndex, typeSetter, 150f);
            dropdown.SetEnabled(enabled);
            right.Add(dropdown);

            void RefreshChips()
            {
                chipsRoot.Clear();
                var list = getter() ?? new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    var idx = i;
                    chipsRoot.Add(DashFallTheme.MakeChip(list[i], enabled, () =>
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

        /// <summary>
        /// The shipped bind set. Everything is unbound except the two dives and the goalie's two
        /// standing dashes.
        ///
        /// This used to seed F, Z, C, W and S across twists and slide influence. Those keys are not
        /// free: Z and C were handed to both twist and slide influence at once, and W and S are the
        /// player's own forward and back. A new player got a set of double-press and continuous
        /// binds layered onto movement keys they were already using, without having asked for any of
        /// it. Dives and standing dashes are the two everyone wants bound, so they are the two that
        /// ship bound, and the rest is opt in from the panel.
        ///
        /// Trigger types are still filled in. They cost nothing while a list is empty and they mean
        /// the first key a player binds behaves the way that action is meant to behave, rather than
        /// falling back to PRESS on something that wants CONTINUOUS.
        /// </summary>
        private void ResetToDefaults()
        {
            // Skater keybinds. Dive is the only one that ships bound.
            _skater.divekey = new List<string> { "F" };

            _skater.dashleftkey = new List<string>();
            _skater.dashrightkey = new List<string>();
            _skater.powercarvekey = new List<string>();
            _skater.twistleftkey = new List<string>();
            _skater.twistrightkey = new List<string>();
            _skater.slideinfluenceleftkey = new List<string>();
            _skater.slideinfluencerightkey = new List<string>();
            _skater.slideinfluenceforwardkey = new List<string>();
            _skater.slideinfluencebackwardkey = new List<string>();

            // Skater action types
            _skater.divekeytype = "PRESS";
            _skater.dashleftkeytype = "PRESS";
            _skater.dashrightkeytype = "PRESS";
            _skater.powercarvekeytype = "HOLD";
            _skater.twistleftkeytype = "DOUBLE PRESS";
            _skater.twistrightkeytype = "DOUBLE PRESS";
            _skater.slideinfluenceleftkeytype = "CONTINUOUS";
            _skater.slideinfluencerightkeytype = "CONTINUOUS";
            _skater.slideinfluenceforwardkeytype = "CONTINUOUS";
            _skater.slideinfluencebackwardkeytype = "CONTINUOUS";

            // Goalie keybinds. Dive plus the two standing dashes ship bound.
            _goalie.divekey = new List<string> { "F" };
            _goalie.standingdashleftkey = new List<string> { "Q" };
            _goalie.standingdashrightkey = new List<string> { "E" };

            _goalie.twistleftkey = new List<string>();
            _goalie.twistrightkey = new List<string>();
            _goalie.slideinfluenceleftkey = new List<string>();
            _goalie.slideinfluencerightkey = new List<string>();
            _goalie.slideinfluenceforwardkey = new List<string>();
            _goalie.slideinfluencebackwardkey = new List<string>();

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
        //
        // The panel is its own way in and out now, so it owns the cursor and the game's
        // mouse-required flag on both edges. ModMenuHub used to do that for us, which is why the
        // old close paths handed control back to it instead of restoring anything themselves.

        /// <summary>
        /// True while the panel is logically open, including while a rebind overlay is covering
        /// it. Do not read the panel's display for this: a capture hides the panel without
        /// closing it.
        /// </summary>
        public bool IsDashFallPanelOpen => _panelVisible;

        /// <summary>
        /// The F4 handler. Safe to call before the panel has ever been built, and a no-op while a
        /// rebind is listening, so F4 can be captured as a keybind instead of toggling the panel
        /// out from under the capture overlay.
        /// </summary>
        public void ToggleDashFallPanel()
        {
            if (_isCapturing) return;

            if (_panelVisible)
            {
                // Same commit sequence the CLOSE button and the ESC path use, so a panel closed
                // with F4 cannot silently drop edited keybinds.
                DashFallConfigLoader.SaveSkaterConfig(_skater);
                DashFallConfigLoader.SaveGoalieConfig(_goalie);
                RebuildLookups();
                ResetInputActions();
                FullCloseDashFallPanel();
            }
            else
            {
                OpenDashFallPanel();
            }
        }

        public void OpenDashFallPanel()
        {
            // The 0.5s UI probe is what normally assigns _doc and _lastRoot, so a press in the
            // window right after a root swap would otherwise be a silent no-op.
            EnsureUIRoot();

            BuildDashFallPanel();
            if (_dfPanel == null) return;

            _dfBackdrop.style.display = UITK.DisplayStyle.Flex;
            _dfPanel.style.display = UITK.DisplayStyle.Flex;
            _dfBackdrop.BringToFront();
            _panelVisible = true;

            // Fresh panel session: re-attempt auto-unlock and clear any prior
            // local LOCK / status so the SERVER tab reflects current auth.
            _serverAutoAuthSent = false;
            _serverUserLocked = false;
            _serverStatusText = "";
            _serverEditCfg = null; // re-clone editor copy from live on next build

            // SHOW is a per-session reveal, not a preference. Without this a host who revealed the
            // editor password once reopens the panel with it still printed in clear, which is a
            // shoulder-surfing leak rather than a convenience.
            _serverPasswordRevealed = false;

            // Both armed states are per-session too, and their explanatory status line is cleared
            // just above, so leaving either armed would present a confirm button with nothing left
            // on screen saying what it is about to confirm.
            _serverResetArmed = false;
            _footerResetArmed = false;

            // Refresh chips to show current bindings
            RefreshActionsUI();

            SaveCursorState();
            SuppressPlayerInput();
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        public void CloseDashFallPanel()
        {
            if (_isCapturing)
            {
                CancelChordCapture();
                return;
            }

            // Before anything is hidden. A picker's list lives on the UIDocument root rather than
            // inside the panel, so hiding the panel would strand it over the game with an invisible
            // full-rect click catcher still live.
            DashFallTheme.CloseAllPickers();

            // A pending "Confirm reset" must not outlive the session that armed it, or the next
            // open presents a footer button one click away from wiping every bind.
            _footerResetArmed = false;

            if (_dfPanel != null) _dfPanel.style.display = UITK.DisplayStyle.None;
            if (_dfBackdrop != null) _dfBackdrop.style.display = UITK.DisplayStyle.None;

            // Guard the restore, not the hide: a second close must not replay a cursor snapshot
            // that the first one already consumed.
            if (!_panelVisible) return;
            _panelVisible = false;

            // Hand the mouse-required flag back to the game and let it drive the cursor. When the
            // recompute lands, SetMouseVisibility has already set the cursor to match whatever the
            // game's own views now want, so replaying our snapshot on top of that would undo it.
            // The snapshot restore is only for the fallback path where the recompute is unavailable.
            if (!RestorePlayerInput()) RestoreCursorState();

            ConfigManager.Dbg("Panel closed");
        }

        /// <summary>
        /// The ESC path. Identical teardown to CloseDashFallPanel now that there is no hub to
        /// hand control back to; kept as its own name because ESC is a distinct intent and the
        /// ClientRunner's key handler calls it by that name.
        /// </summary>
        private void FullCloseDashFallPanel()
        {
            if (_isCapturing)
            {
                CancelChordCapture();
                return;
            }

            CloseDashFallPanel();
        }

        // Resolves the UIDocument root on demand, the same way the periodic probe does, so the
        // first F4 press after a scene or root change opens the panel instead of doing nothing.
        // _lastRoot is written too, or the next probe would see a changed root and tear the
        // freshly built panel back down.
        private void EnsureUIRoot()
        {
            if (_doc != null && _doc.rootVisualElement != null) return;
            if (_lastRoot != null) return;

            try
            {
                var uiMgr = UnityEngine.Object.FindFirstObjectByType<UIManager>(UnityEngine.FindObjectsInactive.Include);
                _doc = uiMgr != null
                    ? uiMgr.UIDocument
                    : UnityEngine.Object.FindFirstObjectByType<UITK.UIDocument>(UnityEngine.FindObjectsInactive.Include);
                var root = _doc != null ? _doc.rootVisualElement : null;
                if (root != null) _lastRoot = root;
            }
            catch (Exception e) { ConfigManager.Dbg("EnsureUIRoot failed: " + e.Message); }
        }

        // The game's own flag for "a UI wants the mouse". Without it, typing in the SEARCH box or
        // an admin config field also drives the skater, because vanilla PlayerInput keeps reading
        // the keyboard while our panel is up.
        private void SuppressPlayerInput()
        {
            if (_savedMouseRequired) return;
            try
            {
                _prevMouseRequired = GlobalStateManager.UIState.IsMouseRequired;
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = true;
                GlobalStateManager.UIState = uiState;
                _savedMouseRequired = true;
            }
            catch (Exception e) { ConfigManager.Dbg("SuppressPlayerInput failed: " + e.Message); }
        }

        /// <summary>
        /// Re-asserts the suppression every frame the panel is up.
        ///
        /// Setting the flag once on open is not enough. UIManager.CheckMouseRequirement recomputes
        /// IsMouseRequired from its own list of UIViews whenever any mouse-requiring view changes
        /// visibility or focus, and this panel is not a UIView, so it contributes nothing to that
        /// sum. Opening and closing the game's chat window while the panel is up therefore
        /// recomputes the flag to false and hands keystrokes straight back to the skater, which is
        /// how typing in SEARCH ended up driving the player around.
        ///
        /// Only written when it actually differs, because the setter raises Event_OnUIStateChanged
        /// and ApplicationManager.SetMouseVisibility listens to it.
        /// </summary>
        private void HoldPlayerInputSuppressed()
        {
            if (!_savedMouseRequired) return;
            try
            {
                var uiState = GlobalStateManager.UIState;
                if (uiState.IsMouseRequired) return;
                uiState.IsMouseRequired = true;
                GlobalStateManager.UIState = uiState;
            }
            catch (Exception e) { ConfigManager.Dbg("HoldPlayerInputSuppressed failed: " + e.Message); }
        }

        /// <summary>
        /// Hands the flag back to the game. Returns true when the game recomputed it, in which case
        /// the caller must NOT also restore a cursor snapshot, because SetMouseVisibility will have
        /// already driven the cursor from the recomputed value.
        ///
        /// Replaying the value sampled at open time is what this used to do and it was wrong in
        /// both directions. ESC reaches the game as well as us, and the game's input phase runs
        /// first, so by the time this ran the pause menu had already opened and our stale false
        /// re-hid the cursor underneath it. Opening the panel on top of an already-visible pause
        /// menu inverted it: the snapshot was true, the game had computed false, and writing true
        /// back made PlayerInput.UpdateInputs early-return so the skater took no input at all.
        /// </summary>
        private bool RestorePlayerInput()
        {
            if (!_savedMouseRequired) return false;
            _savedMouseRequired = false;
            try
            {
                if (TryRecomputeMouseRequirement()) return true;

                // Only reached on a build where the private method moved. Replaying the snapshot is
                // the old behaviour, which is wrong in the ways described above but is still better
                // than leaving the flag stuck on and the skater unable to move.
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = _prevMouseRequired;
                GlobalStateManager.UIState = uiState;
            }
            catch (Exception e) { ConfigManager.Dbg("RestorePlayerInput failed: " + e.Message); }
            return false;
        }

        // UIManager.CheckMouseRequirement is private, so it is reflected once and cached the same
        // way the minimap fields are in ClientRunner. Confirmed present in Puck.dll for this build.
        private static MethodInfo _miCheckMouseRequirement;
        private static bool _checkMouseRequirementResolved;

        private bool TryRecomputeMouseRequirement()
        {
            if (!_checkMouseRequirementResolved)
            {
                _checkMouseRequirementResolved = true;
                try
                {
                    _miCheckMouseRequirement = typeof(UIManager).GetMethod(
                        "CheckMouseRequirement", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch { }
                if (_miCheckMouseRequirement == null)
                    ConfigManager.Dbg("UIManager.CheckMouseRequirement not found, falling back to the snapshot restore");
            }

            if (_miCheckMouseRequirement == null) return false;

            var mgr = _cachedUIManager != null
                ? _cachedUIManager
                : MonoBehaviourSingleton<UIManager>.Instance;
            if (mgr == null) return false;

            try
            {
                _miCheckMouseRequirement.Invoke(mgr, null);
                return true;
            }
            catch (Exception e)
            {
                ConfigManager.Dbg("CheckMouseRequirement invoke failed: " + e.Message);
                return false;
            }
        }

        // ========== CHORD CAPTURE ==========
        private void EnsureCaptureOverlay()
        {
            if (_captureOverlay != null) return;

            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root == null) return;

            _captureOverlay = DashFallTheme.MakeCaptureOverlay();

            // The card, its border and the kicker are the only three places the cool capture hue
            // appears, which is what keeps it reading as a mode rather than as a second accent.
            var card = DashFallTheme.MakeCaptureCard();
            card.Add(DashFallTheme.MakeCaptureKicker("REBIND"));

            var title = new UITK.Label("PRESS A KEY");
            title.style.fontSize = 56;
            title.style.color = DashFallTheme.TextPrimary;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = 4;
            title.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            title.style.marginBottom = 18;
            ForceUIFont(title);
            card.Add(title);

            _captureLabel = new UITK.Label("Press a key or combination to bind.");
            _captureLabel.style.fontSize = 16;
            _captureLabel.style.color = DashFallTheme.TextMuted;
            _captureLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _captureLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
            _captureLabel.style.maxWidth = 520;
            ForceUIFont(_captureLabel);
            card.Add(_captureLabel);

            // TextMuted, not OWP's TextDim. OWP draws this on a near-black card; ours sits on the
            // lifted CaptureBg, where TextDim falls to roughly 2.8:1 and stops being readable. Muted
            // puts it back to about 5.4:1, the same correction MakeHeaderColumn already makes.
            var hint = DashFallTheme.MakeLabel("ESC to cancel", 11, DashFallTheme.TextMuted);
            hint.style.letterSpacing = 2;
            hint.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            hint.style.marginTop = 22;
            card.Add(hint);

            _captureOverlay.Add(card);
            root.Add(_captureOverlay);
            _captureOverlay.BringToFront();
        }

        private void StartChordCapture(string prompt, Action<string> onCaptured, UITK.Button armed = null)
        {
            _onChordCaptured = onCaptured;
            _isCapturing = true;

            EnsureCaptureOverlay();

            // A picker list open on the row being rebound would otherwise sit on top of the capture
            // overlay, since both are parented to the UIDocument root and the list was brought to
            // front after the overlay.
            DashFallTheme.CloseAllPickers();

            HidePanelDuringCapture(true);

            // Paint the BIND button that started this so the row itself says which one is
            // listening, in case the overlay is dismissed and the panel comes back.
            _captureButton = armed;
            DashFallTheme.StyleCaptureButton(_captureButton, true);

            // The caller passes "Press keys for SLIDE DI LEFT" and so on. This used to overwrite it
            // with a constant, which left sixteen near-identical bind rows all producing the same
            // anonymous prompt, four of them called SLIDE DI something. The armed BIND button was
            // supposed to disambiguate, but HidePanelDuringCapture has already hidden the panel by
            // the time it is painted, so it is never on screen to be read.
            if (_captureLabel != null)
                _captureLabel.text = string.IsNullOrEmpty(prompt)
                    ? "Press a key or combination to bind."
                    : prompt;
            _captureOverlay.style.display = UITK.DisplayStyle.Flex;
            _captureOverlay.BringToFront();
            StartCoroutine(CaptureChordRoutine());
        }

        private void CancelChordCapture()
        {
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
            HidePanelDuringCapture(false);
            ClearCaptureButtonLook();
            _onChordCaptured = null;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        // Restores the listening button to its resting look. StyleCaptureButton puts back the
        // ink as well as the fill, which is the bug OWP has: it restores only the fill and leaves
        // dark text on a dark button after a cancelled rebind.
        private void ClearCaptureButtonLook()
        {
            if (_captureButton == null) return;
            DashFallTheme.StyleCaptureButton(_captureButton, false);
            _captureButton = null;
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

            // F4 opens and closes this panel and is not rebindable, so it cannot also be captured as
            // an action bind. Binding it used to half-work in a way that was worse than refusing:
            // with the panel closed the press dispatched the bound action in EarlyUpdate and then
            // opened the panel in Update, while the press that should have closed the panel was
            // swallowed because ShouldBlockBinds sees the panel displayed by then.
            if (k == KeyCode.F4) return false;

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
                        ClearCaptureButtonLook();
                        yield break;
                    }
                }

                yield return null;
            }

            CancelChordCapture();
        }
    }
}
