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

        /// <summary>Why we believe the running build is stale. Drives the popup's wording.</summary>
        private enum StaleReason
        {
            None = 0,
            /// <summary>Our own DLL on disk is no longer the bytes we are executing.</summary>
            FilesReplaced,
            /// <summary>Steam says the Workshop item still needs downloading.</summary>
            WorkshopNeedsUpdate,
            /// <summary>The TEST button.</summary>
            Test,
        }

        private bool _modOutOfDate;            // the running build is stale, by either signal
        private StaleReason _staleReason;      // which signal fired
        private bool _versionDismissed;        // user dismissed the popup; don't show again
        private bool _versionCheckLoggedSkip;  // logged the "Steam unavailable" reason once

        // Log suppression for the repeating query. These exist because the check became a
        // poll: as a one-shot each line below ran at most once per session, but at one
        // query a minute, and with _modOutOfDate false for almost every client forever,
        // an unguarded line is ~240 identical entries per four-hour session in Player.log.
        // Both report a STATE, so both are logged on change rather than on occurrence, and
        // a state that never changes is worth exactly one line.
        private bool _versionCheckLoggedNotRunning;  // Steam was down last time we looked
        private bool _loggedAnyItemState;            // have we reported an item state at all
        private uint _lastLoggedItemState;           // the last one reported
        private bool _versionShowRequested;    // a trigger (enable / server join / test) asked to show
        private bool _versionStartupChecked;   // armed the initial delay
        private float _nextVersionCheckAt;     // when the next check is due

        // On-disk identity of the assembly we are executing, captured once at startup.
        //
        // STATIC, and that is not incidental. The thing this baseline describes is the loaded
        // assembly, whose lifetime is the PROCESS: BepInEx never reloads the domain, so once
        // Mono has an image for a path it hands the same one back forever. The runner's
        // lifetime is much shorter. Puck's BasePlugin.Disable nulls its plugin instance and
        // Enable re-runs Activator.CreateInstance, so toggling the mod off and on in the mod
        // list builds a fresh runner with fresh instance fields while the process carries on
        // executing the old code.
        //
        // As instance fields these were re-stamped by that toggle. If the DLL had already
        // been replaced, the new baseline was the NEW file and the check then compared it
        // against itself and returned false for the rest of the session. Toggling a mod off
        // and on is the first thing anyone does when a mod "isn't working", which is exactly
        // the symptom this feature exists to explain, so that was the case most likely to
        // reach it. Static, the baseline outlives the toggle and the check re-latches on the
        // next poll.
        private static string _assemblyPath;
        private static long _assemblyLength = -1;
        private static DateTime _assemblyWriteTimeUtc;
        private static bool _assemblyStampProbed;

        /// <summary>Grace period before the first query, so Steam and the UI root can settle.</summary>
        private const float VersionFirstCheckDelay = 5f;

        /// <summary>
        /// Gap between repeat queries. A minute is far below the time it takes anyone to
        /// notice a stale build, and the query stops entirely once out-of-date latches.
        /// </summary>
        private const float VersionRecheckInterval = 60f;
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
            // First check runs a few seconds in: Steam needs a moment to settle its item
            // state and the UI root needs to come up. After that it REPEATS.
            //
            // Repeating is the whole point, not belt and braces. The situation this feature
            // exists for, spelled out at the top of this file, is the Workshop item being
            // updated WHILE THE GAME IS RUNNING, and that is precisely the case a single
            // check at launch cannot see: NeedsUpdate flips to true minutes after the one
            // check already ran and returned false. Before this, the only other trigger was
            // joining a modded server, so a player already in a server when the update
            // published was never told, and a player who never joins one never found out
            // at all.
            //
            // The poll is cheap and self-limiting. SteamUGC.GetItemState is a local read of
            // client state with no callback and no network round trip, it runs once a minute
            // rather than per frame, and CheckOutOfDate returns immediately once _modOutOfDate
            // latches, so a client that IS out of date stops querying entirely.
            if (!_modOutOfDate && Time.unscaledTime >= _nextVersionCheckAt)
            {
                _nextVersionCheckAt = Time.unscaledTime
                    + (_versionStartupChecked ? VersionRecheckInterval : VersionFirstCheckDelay);

                if (_versionStartupChecked)
                {
                    if (CheckOutOfDate()) RequestVersionPopup();
                }
                else
                {
                    // The first tick arms the delay and stamps the DLL. Stamping here rather
                    // than after the grace period keeps the baseline as close to load time as
                    // possible, so an update that lands during those first seconds is still
                    // seen as a change rather than baked in as the starting state.
                    _versionStartupChecked = true;
                    CaptureAssemblyStamp();
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
        /// <summary>
        /// Records what our own DLL looked like on disk when we started. Called once.
        ///
        /// Assembly.Location is empty for an assembly loaded from a byte[] rather than a
        /// path, which some loaders do. That is not an error, it just means this signal is
        /// unavailable and the Steam one has to carry the check alone.
        /// </summary>
        private void CaptureAssemblyStamp()
        {
            if (_assemblyStampProbed) return;
            _assemblyStampProbed = true;

            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    Debug.Log("[COMPADJUST] Stale-build check: this assembly has no file on disk " +
                              "(loaded from memory), so only the Steam Workshop state is watched.");
                    return;
                }

                // Read the metadata BEFORE arming the check. FileInfo is lazy, so
                // info.Length is the first call that actually stats the file, and it can
                // throw if the file is deleted or renamed in the window since File.Exists
                // above. Steam replaces files by delete-plus-rename, so that window is the
                // very thing this feature watches for rather than a hypothetical.
                //
                // _assemblyPath != null is the only readiness guard CheckAssemblyReplaced
                // has. Assigning it first meant a throw here left the check ARMED against
                // the sentinel baseline (-1 bytes, DateTime.MinValue), which no real file
                // can ever match, so the next poll raised a false "RESTART TO FINISH
                // UPDATING" on a build that was perfectly current. Path last: a partial
                // stamp now leaves the signal simply unavailable, which is what the catch
                // below already reports.
                var info = new System.IO.FileInfo(path);
                long length = info.Length;
                DateTime writeTimeUtc = info.LastWriteTimeUtc;

                _assemblyLength = length;
                _assemblyWriteTimeUtc = writeTimeUtc;
                _assemblyPath = path;

                Debug.Log($"[COMPADJUST] Stale-build check armed on '{path}' " +
                          $"({_assemblyLength} bytes, {_assemblyWriteTimeUtc:u}).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COMPADJUST] Could not stamp this assembly for the stale-build check: {e.Message}");
            }
        }

        /// <summary>
        /// True once our DLL on disk stops matching the bytes we are running.
        ///
        /// THIS, not Steam, is the signal that catches the real case. SteamUGC's
        /// k_EItemStateNeedsUpdate reports whether STEAM has work left to do, and Steam
        /// happily finishes writing the new DLL while the game is running, at which point
        /// the flag CLEARS. The process keeps executing the assembly it loaded at launch
        /// until it restarts, so the interesting window is exactly the one where Steam
        /// reports everything is fine. That is not a hypothetical: a client running a
        /// months-old build, failing to parse the server's config sync, logged
        /// "needsUpdate=False" while doing it.
        ///
        /// Comparing length and last-write-time against the values captured at startup asks
        /// the question that actually matters, which is whether the file backing this
        /// assembly has been replaced since we read it. It needs no Steam bookkeeping and
        /// works for a Workshop install and a local build alike.
        /// </summary>
        private bool CheckAssemblyReplaced()
        {
            if (_assemblyPath == null) return false;

            try
            {
                if (!System.IO.File.Exists(_assemblyPath)) return false;   // mid-write; try again next poll

                var info = new System.IO.FileInfo(_assemblyPath);
                if (info.Length == _assemblyLength && info.LastWriteTimeUtc == _assemblyWriteTimeUtc) return false;

                Debug.LogWarning(
                    $"[COMPADJUST] The mod's DLL on disk has changed since it was loaded: " +
                    $"{_assemblyLength} bytes @ {_assemblyWriteTimeUtc:u} -> {info.Length} bytes @ {info.LastWriteTimeUtc:u}. " +
                    "The game is still running the OLD build and must be restarted to pick the new one up.");
                return true;
            }
            catch
            {
                // A partially written file can throw here. Never treat that as stale.
                return false;
            }
        }

        private bool CheckOutOfDate()
        {
            if (_modOutOfDate) return true;

            // Checked FIRST, and deliberately: it is the signal that stays true once it
            // fires, whereas the Steam flag below goes false again the moment Steam finishes
            // its download and is therefore silent for most of the window we care about.
            if (CheckAssemblyReplaced())
            {
                _modOutOfDate = true;
                _staleReason = StaleReason.FilesReplaced;
                return true;
            }

            try
            {
                if (!SteamAPI.IsSteamRunning())
                {
                    if (!_versionCheckLoggedNotRunning)
                    {
                        _versionCheckLoggedNotRunning = true;
                        Debug.Log("[COMPADJUST] Version check: Steam not running; skipping. " +
                                  "Silenced until it comes back.");
                    }
                    return false;
                }

                // Say so when it returns, so the silence above has a visible end.
                if (_versionCheckLoggedNotRunning)
                {
                    _versionCheckLoggedNotRunning = false;
                    Debug.Log("[COMPADJUST] Version check: Steam is back; resuming.");
                }

                uint state = SteamUGC.GetItemState(new PublishedFileId_t(WORKSHOP_FILE_ID));
                bool subscribed = (state & (uint)EItemState.k_EItemStateSubscribed) != 0;
                bool installed  = (state & (uint)EItemState.k_EItemStateInstalled)  != 0;
                bool needs      = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0;

                // On change only. The first query always logs, so the "did the check run at
                // all" question the line was added to answer is still answered, and a client
                // whose state never moves pays one line instead of one a minute.
                if (!_loggedAnyItemState || state != _lastLoggedItemState)
                {
                    _loggedAnyItemState = true;
                    _lastLoggedItemState = state;
                    Debug.Log($"[COMPADJUST] Version check: id={WORKSHOP_FILE_ID} state=0x{state:X} subscribed={subscribed} installed={installed} needsUpdate={needs}");
                }
                if (needs)
                {
                    _modOutOfDate = true;
                    _staleReason = StaleReason.WorkshopNeedsUpdate;
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
            // Previews the Workshop wording. The files-replaced variant is reachable by
            // rebuilding the DLL while the game runs, which is also how to test it for real.
            _staleReason = StaleReason.Test;
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

            // The two cases need different instructions. "Close the game so Steam can finish
            // updating" is actively wrong once the files are ALREADY updated, which is the
            // common case: at that point there is nothing left to download and the only thing
            // standing between the player and the new build is the restart. Saying otherwise
            // sends the player to wait on a download that finished long ago.
            bool alreadyDownloaded = _staleReason == StaleReason.FilesReplaced;

            var title = new UITK.Label(alreadyDownloaded ? "RESTART TO FINISH UPDATING" : "MOD OUT OF DATE");
            title.style.fontSize = 32;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new UITK.StyleColor(accent);
            title.style.marginBottom = 14;
            title.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(title);
            panel.Add(title);

            var body = new UITK.Label(alreadyDownloaded
                ? "COMPADJUST has already been updated on disk, but the game is still running "
                  + "the version it loaded at launch.\n\n"
                  + "Restart the game to pick it up. Carrying on runs old code against updated "
                  + "files, which shows up as settings not applying, physics that disagree with "
                  + "the server, or a mod that silently does nothing."
                : "A newer version of COMPADJUST is available on the Steam Workshop.\n\n"
                  + "Fully close the game so Steam can finish updating the mod, then relaunch. "
                  + "Playing on the old build while the Workshop files have already changed can "
                  + "cause bugs (physics glitches, desync, crashes).");
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

            // Both variants open the Workshop item, and only the caption differs.
            //
            // The files-replaced case does not NEED the page to get the new build: the
            // download already happened and quitting is what picks it up, which is why the
            // caption drops the promise of opening anything. But the page is still the right
            // place to land. It is where the player checks which version they are about to
            // restart into, reads what changed, and unsubscribes or rolls back if the update
            // is the reason they are here. The steam:// handler also brings the Steam client
            // forward, which is where any of that gets done.
            //
            // An earlier comment here claimed there was nothing worth opening. There is.
            var updateBtn = MakeVersionButton(alreadyDownloaded ? "Quit to Update" : "Open Workshop & Quit",
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
