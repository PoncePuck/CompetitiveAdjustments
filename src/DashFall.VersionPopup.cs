// DashFall.VersionPopup.cs - Out-of-date Steam Workshop version popup.
//
// Puck loads mod DLLs directly from steamapps\workshop\content\<appid>\<publishedFileId>\.
// When the Workshop item is updated while the game is running, Windows keeps the loaded
// DLL locked, so Steam cannot apply the update until the game closes. The player keeps
// running stale code against changed files, which causes bugs. Steam exposes this exact
// situation through SteamUGC.GetItemState's k_EItemStateNeedsUpdate flag, which is set
// when the subscribed item on the Workshop differs from what is installed locally.
//
// This is client-only (the runner already destroys itself on dedicated servers).

using System;
using System.Reflection;
using UnityEngine;
using Steamworks;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner
    {
        private const uint PUCK_STEAM_APP_ID = 2994020;
        // Canonical Workshop item for COMPADJUST. Used both for the "Open Workshop" link and
        // for the out-of-date check (SteamUGC.GetItemState on this id), so detection works
        // whether the DLL runs from the Workshop folder or a local Plugins build.
        private const ulong WORKSHOP_FILE_ID = 3689734278UL;

        private bool _modOutOfDate;            // Workshop has a newer version than installed
        private bool _versionDismissed;        // user dismissed the popup; don't show again
        private bool _versionCheckLoggedSkip;  // logged the "Steam unavailable" reason once
        private bool _versionShowRequested;    // a trigger (enable / server join / test) asked to show
        private bool _versionStartupChecked;   // ran the one-shot post-launch check
        private float _versionStartupCheckAt;  // when the post-launch check is due
        private ulong _workshopFileId;         // derived published file id (0 = not a workshop install)
        private UITK.VisualElement _versionBackdrop;

        /// <summary>
        /// Called every frame from Update. The out-of-date popup is event-driven, not polled:
        /// it only appears when something requests it (the mod becoming active shortly after
        /// launch, joining a server running the mod, or the TEST button). This method runs the
        /// one-shot post-launch check and then keeps the popup attached while a show is pending.
        /// </summary>
        private void TickVersionPopup()
        {
            // "Enabled the mod": one-shot check a few seconds after the runner starts. Steam
            // needs a moment to settle its item state and the UI root needs to come up.
            if (!_versionStartupChecked)
            {
                if (_versionStartupCheckAt <= 0f) _versionStartupCheckAt = Time.unscaledTime + 5f;
                else if (Time.unscaledTime >= _versionStartupCheckAt)
                {
                    _versionStartupChecked = true;
                    if (CheckOutOfDate()) RequestVersionPopup();
                }
            }

            if (!_versionShowRequested || _versionDismissed || _lastRoot == null) return;

            // ESC dismisses the popup (the main panel's ESC handler is suppressed while
            // this is up, so a single press only closes the popup).
            if (_versionBackdrop != null)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                {
                    DismissVersionPopup();
                    return;
                }
            }

            // (Re)build if missing or still attached to a stale root that was swapped out.
            if (_versionBackdrop == null || _versionBackdrop.parent != _lastRoot)
            {
                _versionBackdrop?.RemoveFromHierarchy();
                _versionBackdrop = null;
                ShowVersionPopup(_lastRoot);
            }
        }

        /// <summary>
        /// Called when the server's feature/config sync arrives, i.e. the player joined a
        /// server running COMPADJUST. Re-checks Steam and surfaces the popup if out of date.
        /// </summary>
        public void OnJoinedModdedServer()
        {
            Debug.Log("[COMPADJUST] Joined modded server; running version check.");
            if (CheckOutOfDate()) RequestVersionPopup();
        }

        // Flag that a show was requested. The popup only ever appears as a result of this,
        // so it never interrupts an ongoing match at a random time.
        private void RequestVersionPopup()
        {
            if (!_modOutOfDate || _versionDismissed) return;
            _versionShowRequested = true;
        }

        // Ask Steam whether the COMPADJUST Workshop item needs an update. Sets and returns
        // _modOutOfDate. Queries the known published id directly so it works whether the DLL
        // is loaded from the Workshop folder or a local Plugins build. Fails silent (returns
        // false) if Steam is unavailable or the player is not subscribed, so it never blocks play.
        private bool CheckOutOfDate()
        {
            if (_modOutOfDate) return true;
            try
            {
                if (!SteamAPI.IsSteamRunning()) { Debug.Log("[COMPADJUST] Version check: Steam not running; skipping."); return false; }

                uint state = SteamUGC.GetItemState(new PublishedFileId_t(WORKSHOP_FILE_ID));
                bool subscribed = (state & (uint)EItemState.k_EItemStateSubscribed) != 0;
                bool installed  = (state & (uint)EItemState.k_EItemStateInstalled)  != 0;
                bool needs      = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0;
                Debug.Log($"[COMPADJUST] Version check: id={WORKSHOP_FILE_ID} state=0x{state:X} subscribed={subscribed} installed={installed} needsUpdate={needs}");
                if (needs)
                {
                    _modOutOfDate = true;
                    Debug.LogWarning($"[COMPADJUST] Workshop item {WORKSHOP_FILE_ID} reports NeedsUpdate; prompting player to update.");
                }
            }
            catch (Exception e)
            {
                if (!_versionCheckLoggedSkip)
                {
                    _versionCheckLoggedSkip = true;
                    Debug.LogWarning($"[COMPADJUST] Version check unavailable: {e.Message}");
                }
            }
            return _modOutOfDate;
        }

        private void DismissVersionPopup()
        {
            _versionDismissed = true;
            _versionShowRequested = false;
            _versionBackdrop?.RemoveFromHierarchy();
            _versionBackdrop = null;
        }

        /// <summary>
        /// Test entry point: force the out-of-date popup to appear immediately, regardless of
        /// the real Steam item state. Wired to the "TEST VERSION POPUP" button in the SETTINGS
        /// tab so the popup can be previewed without an actual pending Workshop update.
        /// </summary>
        public void ForceShowVersionPopupForTest()
        {
            if (_workshopFileId == 0) _workshopFileId = ResolveWorkshopFileId();
            _modOutOfDate = true;
            _versionDismissed = false;
            _versionShowRequested = true;
            _versionBackdrop?.RemoveFromHierarchy();
            _versionBackdrop = null;    // force a fresh build on the next Update tick
            Debug.Log($"[COMPADJUST] TEST popup requested (workshopId={_workshopFileId}, lastRoot={(_lastRoot != null)}).");
        }

        /// <summary>
        /// Parse the published file id out of the running assembly's path, expecting
        /// ...\steamapps\workshop\content\&lt;appid&gt;\&lt;publishedFileId&gt;\...  Returns 0 when the
        /// DLL is not loaded from a Workshop folder (e.g. a local Plugins dev deploy).
        /// </summary>
        private ulong ResolveWorkshopFileId()
        {
            string loc;
            try { loc = Assembly.GetExecutingAssembly().Location; }
            catch { return 0; }
            if (string.IsNullOrEmpty(loc)) return 0;

            string[] parts = loc.Replace('\\', '/').Split('/');
            for (int i = 0; i + 2 < parts.Length; i++)
            {
                if (string.Equals(parts[i], "content", StringComparison.OrdinalIgnoreCase)
                    && parts[i + 1] == PUCK_STEAM_APP_ID.ToString()
                    && ulong.TryParse(parts[i + 2], out ulong id))
                {
                    return id;
                }
            }
            return 0;
        }

        private void ShowVersionPopup(UITK.VisualElement root)
        {
            if (root == null) return;

            // Modal backdrop: dim the screen and swallow clicks so the game behind is inert.
            _versionBackdrop = new UITK.VisualElement { name = "COMPADJUST_VersionBackdrop" };
            _versionBackdrop.style.position = UITK.Position.Absolute;
            _versionBackdrop.style.left = 0; _versionBackdrop.style.top = 0;
            _versionBackdrop.style.right = 0; _versionBackdrop.style.bottom = 0;
            _versionBackdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0.72f));
            _versionBackdrop.style.alignItems = UITK.Align.Center;
            _versionBackdrop.style.justifyContent = UITK.Justify.Center;
            _versionBackdrop.pickingMode = UITK.PickingMode.Position;
            _versionBackdrop.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());

            var accent = new Color32(224, 122, 63, 255);   // warm amber warning accent

            var panel = new UITK.VisualElement();
            panel.style.width = 560;
            panel.style.maxWidth = new UITK.Length(92, UITK.LengthUnit.Percent);
            panel.style.backgroundColor = new UITK.StyleColor(new Color32(38, 38, 40, 255));
            panel.style.flexDirection = UITK.FlexDirection.Column;
            panel.style.paddingLeft = 28; panel.style.paddingRight = 28;
            panel.style.paddingTop = 24; panel.style.paddingBottom = 22;
            panel.style.borderTopLeftRadius = 10; panel.style.borderTopRightRadius = 10;
            panel.style.borderBottomLeftRadius = 10; panel.style.borderBottomRightRadius = 10;
            // Subtle outline with a thicker coloured accent along the top edge.
            var edge = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.10f));
            panel.style.borderTopWidth = 3;
            panel.style.borderLeftWidth = 1; panel.style.borderRightWidth = 1; panel.style.borderBottomWidth = 1;
            panel.style.borderTopColor = new UITK.StyleColor(accent);
            panel.style.borderLeftColor = edge; panel.style.borderRightColor = edge; panel.style.borderBottomColor = edge;

            var title = new UITK.Label("MOD OUT OF DATE");
            title.style.fontSize = 32;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new UITK.StyleColor(accent);
            title.style.marginBottom = 14;
            ForceUIFont(title);
            panel.Add(title);

            var body = new UITK.Label(
                "A newer version of COMPADJUST is available on the Steam Workshop.\n\n" +
                "Fully close the game so Steam can finish updating the mod, then relaunch. " +
                "Playing on the old build while the Workshop files have already changed can " +
                "cause bugs (physics glitches, desync, crashes).");
            body.style.whiteSpace = UITK.WhiteSpace.Normal;
            body.style.fontSize = 16;
            body.style.color = new UITK.StyleColor(new Color(0.85f, 0.85f, 0.86f));
            body.style.marginBottom = 22;
            ForceUIFont(body);
            panel.Add(body);

            var buttonRow = new UITK.VisualElement();
            buttonRow.style.flexDirection = UITK.FlexDirection.Row;
            buttonRow.style.alignItems = UITK.Align.Center;
            buttonRow.style.justifyContent = UITK.Justify.SpaceBetween;

            var hint = new UITK.Label("Press ESC to dismiss");
            hint.style.fontSize = 13;
            hint.style.color = new UITK.StyleColor(new Color(0.55f, 0.55f, 0.57f));
            ForceUIFont(hint);
            buttonRow.Add(hint);

            var btnContainer = new UITK.VisualElement();
            btnContainer.style.flexDirection = UITK.FlexDirection.Row;
            btnContainer.style.alignItems = UITK.Align.Center;

            var updateBtn = MakeVersionButton("Open Workshop & Quit",
                new Color32(176, 58, 52, 255), new Color32(208, 76, 68, 255));
            updateBtn.clicked += () =>
            {
                try
                {
                    // Open the COMPADJUST Workshop item in the Steam client (persists after the
                    // game exits); fall back to the web page if the steam:// handler is missing.
                    Application.OpenURL($"steam://url/CommunityFilePage/{WORKSHOP_FILE_ID}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[COMPADJUST] Failed to open Workshop page: {e.Message}");
                    try { Application.OpenURL($"https://steamcommunity.com/sharedfiles/filedetails/?id={WORKSHOP_FILE_ID}"); }
                    catch { }
                }
                Application.Quit();
            };

            var dismissBtn = MakeVersionButton("Dismiss",
                new Color32(70, 70, 74, 255), new Color32(96, 96, 100, 255));
            dismissBtn.clicked += () => DismissVersionPopup();

            btnContainer.Add(updateBtn);
            btnContainer.Add(dismissBtn);
            buttonRow.Add(btnContainer);
            panel.Add(buttonRow);

            _versionBackdrop.Add(panel);
            root.Add(_versionBackdrop);
            _versionBackdrop.BringToFront();
            Debug.Log($"[COMPADJUST] Version popup shown (root children={root.childCount}).");
        }

        private static UITK.Button MakeVersionButton(string text, Color32 bg, Color32 hover)
        {
            var b = new UITK.Button { text = text };
            // Height comes from symmetric vertical padding (not a fixed height) and the text
            // is explicitly middle-centred, so the label sits dead-centre and both buttons,
            // sharing the same font and padding, end up identical in height and aligned.
            b.style.minWidth = 110;
            b.style.marginTop = 0; b.style.marginBottom = 0; b.style.marginRight = 0;
            b.style.marginLeft = 12;
            b.style.paddingLeft = 20; b.style.paddingRight = 20;
            b.style.paddingTop = 10; b.style.paddingBottom = 10;
            b.style.fontSize = 17;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            b.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            b.style.color = Color.white;
            b.style.backgroundColor = new UITK.StyleColor(bg);
            b.style.borderTopLeftRadius = 6; b.style.borderTopRightRadius = 6;
            b.style.borderBottomLeftRadius = 6; b.style.borderBottomRightRadius = 6;
            // Strip the default UITK button border so the fill reads cleanly.
            b.style.borderTopWidth = 0; b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
            ForceUIFont(b);
            b.RegisterCallback<UITK.MouseEnterEvent>(_ => b.style.backgroundColor = new UITK.StyleColor(hover));
            b.RegisterCallback<UITK.MouseLeaveEvent>(_ => b.style.backgroundColor = new UITK.StyleColor(bg));
            return b;
        }
    }
}
