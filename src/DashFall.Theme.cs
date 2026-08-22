// DashFall.Theme.cs - the Ponce shared design system, ported for CompetitiveAdjustments.
//
// OWP (OpenWorldPracticeMod/src/UI/OWPUI.cs), MaxPractice (MaxPractice/src/MaxPracticeUI.cs) and
// PonceArenaTweaks (PonceArena/Mod/PonceArenaTweaks/src/TweaksUI.cs) are one design system
// implemented three times. The neutrals are byte identical in all three; only the accent hue
// differs, and that hue is what tells the player which mod they are looking at. OWP is green,
// ArenaTweaks is blue, MaxPractice is red, so this mod is ORANGE. There is exactly one accent
// field below and no green anywhere, deliberately.
//
// Every number in this file is copied from those three files rather than re-derived, because the
// point of a shared system is that a row in this panel is the same height as a row in theirs.
// Where the three disagree the majority wins and the choice is recorded in a comment.
//
// Two things you must know before styling anything with this:
//
//   Fonts are not inherited in UI Toolkit. Every Label, Button and text-bearing element needs the
//   font pushed onto it or it renders with no glyphs at all. Every factory here ends with a
//   ForceUIFont call; if you build an element by hand, call ForceUIFont on it yourself.
//
//   The reference mods build their toggles, sliders and dropdowns out of raw VisualElements
//   because the built-in controls have no stylesheet in the mod context. That holds for some
//   controls and not others, and the split is worth knowing before you pick one.
//
//   Toggle, Slider and MinMaxSlider are fine: their parts live inside the control, so
//   StyleToggle/StyleSlider/StyleMinMaxSlider reach them and recolor them successfully.
//
//   DropdownField is NOT fine, and this was confirmed in game rather than reasoned about. Its
//   arrow has no layout without the missing stylesheet so it collides with the value text, and
//   its open list is built as a separate hierarchy under the panel root where styling the field
//   cannot reach it at all, leaving an unstyled light grey popup. There is no StyleDropdown here
//   any more, deliberately: it existed, it could not be made to look right, and leaving it would
//   have invited the next person to try again. Use MakePicker, which builds the control from raw
//   elements the way the reference mods do.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace DashFallMod.Client
{
    /// <summary>
    /// Palette, geometry and widget factories for the CompetitiveAdjustments client UI.
    /// Static and stateless apart from the cached font, so any partial of DashFallClientRunner
    /// (panel, HUD, version popup) can theme itself from here with no plumbing.
    /// </summary>
    public static class DashFallTheme
    {
        // ========== PALETTE ==========
        //
        // Stored as Color, not Color32, for the reason OWP records at OWPUI.cs:265. Color has an
        // implicit conversion to StyleColor and Color32 does not, so Color lets every assignment
        // below read as a plain style write instead of a StyleColor wrap at every call site.

        /// <summary>Panel fill. Three tones deep on purpose: panel 20 &lt; section 26 &lt; row 34.
        /// The section card only reads as a group because it sits between the two it separates,
        /// so do not flatten these onto each other.</summary>
        public static readonly Color PanelBg       = new Color32(20, 20, 24, 245);
        public static readonly Color PanelBorder   = new Color32(48, 48, 56, 255);
        public static readonly Color SectionBg     = new Color32(26, 26, 32, 255);
        public static readonly Color SectionBorder = new Color32(38, 38, 46, 255);
        public static readonly Color RowBg         = new Color32(34, 34, 40, 255);
        public static readonly Color RowBgHover    = new Color32(44, 44, 52, 255);
        public static readonly Color TextFieldBg   = new Color32(28, 28, 34, 255);
        public static readonly Color ButtonBg      = new Color32(48, 48, 56, 255);
        public static readonly Color TabActiveBg   = new Color32(34, 34, 40, 255);
        public static readonly Color TabInactiveBg = new Color32(24, 24, 30, 255);
        public static readonly Color Backdrop      = new Color32(0, 0, 0, 140);
        public static readonly Color Danger        = new Color32(220, 80, 80, 255);

        /// <summary>
        /// Danger for text that sits inside a row the panel has faded out. Danger itself is a fill
        /// and border colour first; read through SetRowEnabledLook's opacity it composites down to
        /// roughly 2.0:1, which was the least readable text on the panel. This is pre-brightened so
        /// it lands near 4.5:1 once faded.
        /// </summary>
        public static readonly Color DangerFaded   = new Color32(255, 148, 148, 255);
        public static readonly Color TextPrimary   = new Color32(240, 240, 245, 255);
        public static readonly Color TextMuted     = new Color32(155, 155, 165, 255);
        public static readonly Color TextDim       = new Color32(110, 110, 120, 255);

        /// <summary>THE accent, and the only one. Orange is this mod's identity in the Ponce
        /// family the way green is OWP's and blue is ArenaTweaks'. AccentDim keeps the
        /// relationship the reference pairs have, roughly 55 to 70 percent of Accent per channel
        /// (150/224 = 0.670, 80/122 = 0.656, 42/63 = 0.667), which is what makes a filled slider
        /// track read as the same colour as its thumb rather than as a different element.</summary>
        public static readonly Color Accent    = new Color32(224, 122, 63, 255);
        public static readonly Color AccentDim = new Color32(150, 80, 42, 255);

        /// <summary>A disabled row. The system has no such tone of its own, so this borrows
        /// SectionBg, which is what MaxPractice uses for a disabled surface (MaxPracticeUI.cs:702).
        /// One step darker than RowBg, so a server-disabled bind row sinks instead of standing
        /// out.</summary>
        public static readonly Color DisabledRowBg = new Color32(26, 26, 32, 255);

        /// <summary>
        /// One step darker than ButtonBg (OWPUI.cs:270). Currently UNUSED: StyleFooterButton is a
        /// pass-through to the secondary recipe, so the footer renders in ButtonBg. That is a
        /// defensible pick rather than an oversight, because the two references disagree here, OWP
        /// painting a darker footer and PonceArenaTweaks painting ButtonBg. Kept as the value to
        /// reach for if the footer is ever wanted a shade back.
        /// </summary>
        public static readonly Color FooterBtnBg = new Color32(40, 40, 48, 255);
        public static readonly Color ChipBg      = new Color32(48, 48, 56, 255);
        public static readonly Color ChipXBg     = new Color32(70, 70, 80, 255);

        // ---- The listening-for-a-keypress signal ----
        //
        // The reference mods use a warm amber for this, OWP a light Color32(220,150,70) and
        // MaxPractice a dark Color32(100,80,40). Both are unusable here: against an orange accent
        // an amber "listening" button reads as an ordinary active button. So the capture state
        // moves to the only cool hue in a palette that is otherwise orange, red and greys, which
        // makes it a mode change rather than a second accent. Use it in exactly three places so it
        // stays a mode and does not become a theme: the armed capture button's fill, the capture
        // overlay card's border, and the overlay's kicker text. Everything else on that overlay
        // stays neutral.

        /// <summary>The listening state. Cool on purpose, see the note above.</summary>
        public static readonly Color Capture = new Color32(96, 176, 255, 255);

        /// <summary>Dark ink for text sitting on a Capture fill, copying OWP's trick at
        /// OWPUI.cs:4952. The bright blue is too light for TextPrimary to hold against.</summary>
        public static readonly Color CaptureInk = new Color32(20, 20, 25, 255);

        /// <summary>Deep cool fill for a capture surface that is a panel rather than a button,
        /// for instance the overlay card behind "PRESS A KEY".</summary>
        public static readonly Color CaptureBg = new Color32(28, 44, 64, 255);

        /// <summary>Wash behind the whole capture overlay, from OWPUI.cs:1093. Nearly opaque
        /// because the panel underneath must stop competing for attention while a key is being
        /// bound.</summary>
        public static readonly Color CaptureOverlayBg = new Color(0.06f, 0.07f, 0.1f, 0.97f);

        /// <summary>Backdrop for the version popup, deliberately heavier than Backdrop. The
        /// config panel and that modal can be on screen together, and the modal has to out-dim the
        /// panel or it stops reading as a stop sign. Named separately so the divergence is
        /// intentional rather than looking like a missed swap.</summary>
        public static readonly Color ModalBackdrop = new Color32(0, 0, 0, 190);

        /// <summary>Armed state for a two-click destructive button, from OWPUI.cs:1340. Darker
        /// than Danger so "CONFIRM?" reads as a held breath rather than as an error.</summary>
        public static readonly Color DangerArmed = new Color(0.55f, 0.16f, 0.16f, 1f);

        // ========== GEOMETRY ==========

        public const float PANEL_RADIUS   = 12f;
        public const float SECTION_RADIUS = 8f;
        public const float ROW_RADIUS     = 6f;
        public const float BTN_RADIUS     = 5f;
        public const float PILL_RADIUS    = 16f;

        /// <summary>Standard button height and the gap between siblings (OWPUI.cs:290).</summary>
        public const float BTN_H = 38f;
        public const float GAP   = 8f;

        /// <summary>Default width for an in-row button (OWPUI.cs:291). Pass an explicit width to
        /// StyleRowButton for anything narrower, the way the BIND button does.</summary>
        public const float BTN_W = 124f;

        /// <summary>Hand-built slider track width, and the thumb that rides it
        /// (TweaksUI.cs:1463). Only needed if you build a slider out of raw VisualElements; the
        /// real-control path in StyleSlider derives its own numbers from the four below.</summary>
        public const float TRACK_W = 160f;
        public const float THUMB_W = 14f;

        // Slider geometry, shared by StyleSlider and StyleMinMaxSlider so the two controls sit on
        // the same rail. Row height is this mod's existing 26px; the rail, thumb and radii are the
        // family's 6px track with a 14px thumb, and the offsets are derived from them rather than
        // hand-tuned, which is how the thumb top lands on the reference's -4.
        public const float SLIDER_ROW_H     = 26f;
        public const float SLIDER_RAIL_H    = 6f;
        public const float SLIDER_THUMB     = 14f;
        public const float SLIDER_RAIL_TOP  = (SLIDER_ROW_H - SLIDER_RAIL_H) / 2f;      // 10
        public const float SLIDER_THUMB_TOP = -(SLIDER_THUMB - SLIDER_RAIL_H) / 2f;     // -4

        /// <summary>Which recipe StyleButton should apply. See the individual Style* methods for
        /// what each one is for; they are all callable directly too.</summary>
        public enum ButtonVariant
        {
            /// <summary>Neutral footer or dialog button, 38 tall and 124 wide minimum.</summary>
            Secondary,
            /// <summary>The one affirmative action on a surface: AccentDim fill, Accent border.</summary>
            Primary,
            /// <summary>Destructive. Neutral at rest, Danger on hover only.</summary>
            Danger,
            /// <summary>In-row verb button, a 32 tall pill so it lines up with the toggle pills.</summary>
            Compact,
            /// <summary>Same recipe as Secondary. Kept as its own name because the footer is a
            /// distinct place even though the reference mods style it identically.</summary>
            Footer,
            /// <summary>Fixed-width button inside a row, 38 tall with no border.</summary>
            Row,
            /// <summary>ON/OFF pill. Follow with SetPillState to paint it.</summary>
            Pill,
            /// <summary>Square 32x32 stepper for a prev/next picker.</summary>
            Step
        }

        // ========== FONT ==========

        private static Font _uiFont;

        /// <summary>
        /// The game's own font, so the panel does not read as a foreign window bolted onto Puck.
        /// Same three-step resolve as ModMenuHub.GetUIFont: the PanelSettings fallback list first
        /// because that is what the game itself renders with, then any loaded font named Puck or
        /// Montserrat, then Arial as a last resort so text at least appears.
        /// </summary>
        public static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try
            {
                var ps = UnityEngine.Object.FindFirstObjectByType<UITK.PanelSettings>(FindObjectsInactive.Include);
                if (ps != null)
                {
                    var fi = typeof(UITK.PanelSettings).GetField("m_FallbackDynamicFonts",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi != null && fi.GetValue(ps) is System.Collections.IList list && list.Count > 0)
                        _uiFont = list[0] as Font;
                }
                if (_uiFont == null)
                {
                    _uiFont = Resources.FindObjectsOfTypeAll<Font>()
                        .FirstOrDefault(f => f != null && f.name != null
                                          && (f.name.Contains("Puck") || f.name.Contains("Montserrat")));
                }
                if (_uiFont == null)
                {
                    try { _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
                }
                if (_uiFont == null)
                    _uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI" }, 16);
            }
            catch { }
            return _uiFont;
        }

        /// <summary>Pushes the resolved font onto one element. Not inherited, so this has to
        /// happen on every text-bearing element individually.</summary>
        public static void ForceUIFont(UITK.VisualElement ve)
        {
            if (ve == null) return;
            var f = GetUIFont();
            if (f != null) ve.style.unityFont = f;
        }

        // ========== BORDER AND RADIUS HELPERS ==========
        //
        // Every widget spec below is expressed in terms of these, because UI Toolkit has no
        // shorthand for either and writing four lines per edge inline is how a 1px border ends up
        // being 1px on three sides.

        public static void SetUniformRadius(UITK.VisualElement ve, float radius)
        {
            if (ve == null) return;
            ve.style.borderTopLeftRadius = radius;
            ve.style.borderTopRightRadius = radius;
            ve.style.borderBottomLeftRadius = radius;
            ve.style.borderBottomRightRadius = radius;
        }

        public static void SetBorderWidth(UITK.VisualElement ve, float w)
        {
            if (ve == null) return;
            ve.style.borderTopWidth = w;
            ve.style.borderBottomWidth = w;
            ve.style.borderLeftWidth = w;
            ve.style.borderRightWidth = w;
        }

        public static void SetBorderColor(UITK.VisualElement ve, Color c)
        {
            if (ve == null) return;
            ve.style.borderTopColor = c;
            ve.style.borderBottomColor = c;
            ve.style.borderLeftColor = c;
            ve.style.borderRightColor = c;
        }

        /// <summary>Width and colour in one call, which is how almost every surface in the system
        /// wants its border set.</summary>
        public static void SetUniformBorder(UITK.VisualElement ve, float w, Color c)
        {
            SetBorderWidth(ve, w);
            SetBorderColor(ve, c);
        }

        /// <summary>Strips the runtime theme's own border off a control part. The shipped theme
        /// gives sliders and toggles an outline that reads as a grey halo once the part underneath
        /// is recoloured.</summary>
        public static void SetNoBorder(UITK.VisualElement ve)
        {
            SetBorderWidth(ve, 0f);
        }

        private static void SetPadding(UITK.VisualElement ve, float left, float right, float top, float bottom)
        {
            ve.style.paddingLeft = left;
            ve.style.paddingRight = right;
            ve.style.paddingTop = top;
            ve.style.paddingBottom = bottom;
        }

        // ========== SHELL ==========

        /// <summary>
        /// The click-catching dim behind the panel. Add it to the UIDocument root BEFORE the panel
        /// so the panel paints on top, and register your own PointerUpEvent on it to close.
        /// Starts hidden.
        /// </summary>
        public static UITK.VisualElement MakeBackdrop(string name = "DashFall_Backdrop")
        {
            var backdrop = new UITK.VisualElement { name = name };
            var s = backdrop.style;
            s.position = UITK.Position.Absolute;
            s.left = 0; s.top = 0; s.right = 0; s.bottom = 0;
            s.backgroundColor = Backdrop;
            s.display = UITK.DisplayStyle.None;
            backdrop.pickingMode = UITK.PickingMode.Position;
            return backdrop;
        }

        /// <summary>
        /// The panel shell: centred, clipped, flex column, starting hidden.
        ///
        /// Centring is entirely left/top 50 percent plus a translate of minus half its own size,
        /// so the panel never has to know how wide it ended up. Width is a pixel int rather than a
        /// percentage so the clamp actually bites on both very small and very large screens.
        ///
        /// One quirk reproduced verbatim rather than tidied: height 84 percent is dead because
        /// maxHeight clamps it to 78, so the panel is always 78 percent tall. All three reference
        /// mods ship it that way and matching them matters more than the two lines.
        ///
        /// A PointerUpEvent that stops propagation is registered here, because without it every
        /// click inside the panel reaches the backdrop's close handler.
        /// </summary>
        public static UITK.VisualElement MakePanelRoot(string name = "DashFall_Panel")
        {
            var panel = new UITK.VisualElement { name = name };
            var s = panel.style;
            s.position = UITK.Position.Absolute;
            s.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            s.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            s.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
            s.width = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 720, 1040);
            s.height = new UITK.Length(84, UITK.LengthUnit.Percent);
            s.minHeight = new UITK.Length(60, UITK.LengthUnit.Percent);
            s.maxHeight = new UITK.Length(78, UITK.LengthUnit.Percent);
            s.overflow = UITK.Overflow.Hidden;
            s.flexDirection = UITK.FlexDirection.Column;
            s.backgroundColor = PanelBg;
            SetPadding(panel, 18, 18, 16, 14);
            SetUniformRadius(panel, PANEL_RADIUS);
            SetUniformBorder(panel, 1f, PanelBorder);
            s.display = UITK.DisplayStyle.None;
            panel.pickingMode = UITK.PickingMode.Position;
            panel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
            ForceUIFont(panel);
            return panel;
        }

        /// <summary>
        /// Two-tone title, subtitle and the accent rule under them, as one column ready to be
        /// added to the panel. The title splits at its first space so the second word carries the
        /// accent, which is the family's signature: "PONCE ARENA", "r_607's PUCK TRAINING MOD".
        /// A single-word title is drawn entirely in TextPrimary rather than guessing where to cut
        /// it. Use SubtitleText to build the subtitle so the separator glyph and spacing match.
        /// </summary>
        public static UITK.VisualElement MakeHeader(string title, string subtitle)
        {
            string primary = title ?? "";
            string accent = null;
            int split = primary.IndexOf(' ');
            if (split > 0)
            {
                accent = primary.Substring(split);   // keeps the leading space, the words are separate Labels
                primary = primary.Substring(0, split);
            }
            return MakeHeader(primary, accent, subtitle);
        }

        /// <summary>
        /// MakeHeader with the split made explicitly, for a title the space rule would cut in the
        /// wrong place. Pass accent as null for a single-tone title, and remember it needs its own
        /// leading space because the two halves are separate Labels with no gap between them.
        /// </summary>
        public static UITK.VisualElement MakeHeader(string titlePrimary, string titleAccent, string subtitle)
        {
            return MakeHeader(titlePrimary, titleAccent, subtitle, null);
        }

        /// <summary>
        /// MakeHeader with something parked in the dead space to the right of the title.
        ///
        /// The title block only ever fills the left of the header, so on a 720px-plus panel the
        /// right half is empty. A control that belongs to the whole panel rather than to any one
        /// tab, the search box being the obvious one, reads better there than as a full-width row
        /// stealing a line of vertical space above the list.
        ///
        /// The aside is bottom-aligned so it sits on the subtitle's baseline rather than floating
        /// level with the much taller title text, and the separator still spans the full width
        /// underneath both.
        /// </summary>
        public static UITK.VisualElement MakeHeader(string titlePrimary, string titleAccent,
            string subtitle, UITK.VisualElement aside)
        {
            if (aside == null) return MakeHeaderColumn(titlePrimary, titleAccent, subtitle);

            var outer = new UITK.VisualElement();
            outer.style.flexDirection = UITK.FlexDirection.Column;
            outer.style.flexShrink = 0;

            var top = new UITK.VisualElement();
            top.style.flexDirection = UITK.FlexDirection.Row;
            top.style.alignItems = UITK.Align.FlexEnd;
            top.style.flexShrink = 0;

            // The title column keeps its own separator out of this row: it is added to the outer
            // column below so it spans the aside too.
            var titleCol = MakeHeaderColumn(titlePrimary, titleAccent, subtitle, false);
            titleCol.style.flexGrow = 1;
            titleCol.style.flexShrink = 1;
            titleCol.style.minWidth = 0;
            top.Add(titleCol);

            // marginBottom matches the subtitle's, so the aside's bottom edge and the subtitle's
            // bottom edge land on the same line.
            aside.style.flexShrink = 0;
            aside.style.marginBottom = 12;
            top.Add(aside);

            outer.Add(top);
            outer.Add(MakeSeparator());
            return outer;
        }

        private static UITK.VisualElement MakeHeaderColumn(string titlePrimary, string titleAccent,
            string subtitle, bool withSeparator = true)
        {
            var wrap = new UITK.VisualElement();
            wrap.style.flexDirection = UITK.FlexDirection.Column;
            wrap.style.flexShrink = 0;

            var titleRow = new UITK.VisualElement();
            titleRow.style.flexDirection = UITK.FlexDirection.Row;
            titleRow.style.alignItems = UITK.Align.Center;
            titleRow.style.marginBottom = 4;
            titleRow.Add(MakeTitleLabel(titlePrimary ?? "", TextPrimary));
            if (!string.IsNullOrEmpty(titleAccent)) titleRow.Add(MakeTitleLabel(titleAccent, Accent));
            wrap.Add(titleRow);

            if (!string.IsNullOrEmpty(subtitle))
            {
                // TextMuted rather than OWP's TextDim: two of the three reference mods use muted,
                // and at 12px the dim tone loses the key name this line exists to advertise.
                var sub = new UITK.Label(subtitle);
                sub.style.fontSize = 12;
                sub.style.color = TextMuted;
                sub.style.marginBottom = 12;
                ForceUIFont(sub);
                wrap.Add(sub);
            }

            // Suppressed by the aside overload, which owns the separator so it can span the full
            // header width instead of stopping where the title column stops.
            if (withSeparator) wrap.Add(MakeSeparator());
            return wrap;
        }

        /// <summary>One half of the two-tone title. 42px is the size in all three mods.</summary>
        public static UITK.Label MakeTitleLabel(string text, Color color)
        {
            var l = new UITK.Label(text);
            l.style.fontSize = 42;
            l.style.color = color;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 2;
            ForceUIFont(l);
            return l;
        }

        /// <summary>
        /// The subtitle string, with the family's separator glyph (U+00B7 with two spaces either
        /// side) so it matches the sibling panels character for character.
        /// </summary>
        public static string SubtitleText(string what, string toggleKey)
        {
            return what + "  ·  " + toggleKey + " to toggle  ·  Esc to close";
        }

        /// <summary>
        /// The 2px rule under the header. This is the one place the accent appears as a full-width
        /// band, so with an orange accent it is the strongest single accent statement on the
        /// panel. Only OWP has it; it is kept because it is what makes the header feel finished.
        /// Bottom corners stay square so it reads as a rule rather than a floating pill.
        /// </summary>
        public static UITK.VisualElement MakeSeparator()
        {
            var bar = new UITK.VisualElement();
            bar.style.height = 2;
            bar.style.backgroundColor = AccentDim;
            bar.style.marginBottom = 14;
            bar.style.borderTopLeftRadius = 1;
            bar.style.borderTopRightRadius = 1;
            return bar;
        }

        // ========== TABS ==========

        /// <summary>
        /// The container for the tab buttons. Wraps, because a tab that will not fit on one line
        /// is silently clipped mid-word otherwise, and the panel spends most of its life at or
        /// near its 720px floor rather than at its 1040px ceiling.
        /// </summary>
        public static UITK.VisualElement MakeTabStrip()
        {
            var strip = new UITK.VisualElement();
            strip.style.flexDirection = UITK.FlexDirection.Row;
            strip.style.flexWrap = UITK.Wrap.Wrap;
            strip.style.flexShrink = 0;
            return strip;
        }

        /// <summary>
        /// A tab button, painted for its active state immediately. The whole active signal is
        /// three properties: a one-step-lighter background, brighter text, and a 3px accent
        /// underline. There is no glow and no top border, and the underline is the second most
        /// prominent accent surface on the panel after the section bars.
        ///
        /// Sized with the four-tab recipe (17px, letterSpacing 2, 14px padding), which is what OWP
        /// and MaxPractice use, with the seven-tab file's flexShrink, minWidth 0 and NoWrap kept as
        /// insurance. The last tab in a strip should have its marginRight zeroed by the caller so
        /// the row sits flush.
        /// </summary>
        public static UITK.Button MakeTab(string text, bool isActive)
        {
            return MakeTab(text, isActive, null);
        }

        public static UITK.Button MakeTab(string text, bool isActive, Action onClick)
        {
            var b = onClick != null ? new UITK.Button(onClick) : new UITK.Button();
            b.text = (text ?? "").ToUpperInvariant();
            var s = b.style;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            s.height = 44;
            s.flexGrow = 1;
            s.flexShrink = 1;
            s.minWidth = 0;
            s.paddingLeft = 14; s.paddingRight = 14;
            s.marginRight = 6;
            s.marginBottom = 14;
            s.fontSize = 17;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.letterSpacing = 2;
            s.whiteSpace = UITK.WhiteSpace.NoWrap;
            s.borderTopLeftRadius = 8; s.borderTopRightRadius = 8;
            s.borderBottomLeftRadius = 0; s.borderBottomRightRadius = 0;
            SetBorderWidth(b, 0f);
            b.focusable = false;
            ForceUIFont(b);
            SetTabVisual(b, isActive);
            return b;
        }

        /// <summary>Repaints a tab for its active state. Safe to call on every rebuild.</summary>
        public static void SetTabVisual(UITK.Button b, bool active)
        {
            if (b == null) return;
            b.style.backgroundColor = active ? TabActiveBg : TabInactiveBg;
            b.style.color = active ? TextPrimary : TextMuted;
            b.style.borderBottomWidth = active ? 3 : 0;
            b.style.borderBottomColor = Accent;
        }

        /// <summary>
        /// Hover lift for an inactive tab, from OWPUI.cs:5215. A subtle background raise and
        /// brighter text, never a full invert, and the state is re-applied on leave, attach and
        /// geometry change so it cannot drift out of sync with the real active tab.
        /// </summary>
        public static void AddTabHover(UITK.Button b, Func<bool> isActive)
        {
            if (b == null || isActive == null) return;
            b.focusable = false;
            Action apply = () => SetTabVisual(b, isActive());
            apply();
            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                if (isActive()) return;
                b.style.backgroundColor = RowBgHover;
                b.style.color = TextPrimary;
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ => apply());
            b.RegisterCallback<UITK.AttachToPanelEvent>(_ => apply());
            b.RegisterCallback<UITK.GeometryChangedEvent>(_ => apply());
        }

        // ========== SECTIONS ==========

        /// <summary>
        /// A titled card. Sections are containers rather than free-standing headers because a
        /// header alone does not survive a long tab: with fifteen identical rows under it the
        /// header scrolls away and everything after it reads as one list. The card's own
        /// background is what keeps the grouping visible.
        ///
        /// Bottom padding is 4 rather than 12 on purpose, because rows each carry 8 of their own
        /// below, so the visual gap reads as 12.
        /// </summary>
        public static UITK.VisualElement MakeSection(string title)
        {
            return MakeSection(title, null);
        }

        public static UITK.VisualElement MakeSection(string title, string subtitle)
        {
            var card = new UITK.VisualElement();
            var s = card.style;
            s.flexDirection = UITK.FlexDirection.Column;
            s.marginBottom = 14;
            SetPadding(card, 12, 12, 12, 4);
            s.backgroundColor = SectionBg;
            SetUniformRadius(card, SECTION_RADIUS);
            SetUniformBorder(card, 1f, SectionBorder);
            card.Add(MakeSectionHeader(title, subtitle));
            return card;
        }

        /// <summary>MakeSection plus the parenting, returned so rows can go straight into it.</summary>
        public static UITK.VisualElement AddSection(UITK.VisualElement parent, string title, string subtitle = null)
        {
            var card = MakeSection(title, subtitle);
            if (parent != null) parent.Add(card);
            return card;
        }

        /// <summary>
        /// The accent bar plus heading that opens a section. The 4x18 bar repeated once per
        /// section is the highest-frequency accent surface on the panel, so it is where the orange
        /// will mostly be read from. The optional subtitle is indented past the bar so the block
        /// hangs together.
        /// </summary>
        public static UITK.VisualElement MakeSectionHeader(string label, string subtitle = null)
        {
            var wrap = new UITK.VisualElement();
            wrap.style.flexDirection = UITK.FlexDirection.Column;
            wrap.style.marginBottom = 12;

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.marginBottom = 4;
            row.Add(MakeAccentBar(Accent));

            var head = new UITK.Label((label ?? "").ToUpperInvariant());
            head.style.color = TextPrimary;
            head.style.fontSize = 16;
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.letterSpacing = 3;
            ForceUIFont(head);
            row.Add(head);
            wrap.Add(row);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new UITK.Label(subtitle);
                sub.style.color = TextMuted;
                sub.style.fontSize = 12;
                sub.style.marginLeft = 14;
                sub.style.whiteSpace = UITK.WhiteSpace.Normal;
                ForceUIFont(sub);
                wrap.Add(sub);
            }
            return wrap;
        }

        /// <summary>
        /// The 4x18 bar on its own, colour passed in. Painted Accent it is an ordinary section
        /// header; painted Danger it is a severity band, which is how a warning surface can borrow
        /// the section geometry and still read as visibly different from every normal header.
        /// </summary>
        public static UITK.VisualElement MakeAccentBar(Color color)
        {
            var bar = new UITK.VisualElement();
            bar.style.width = 4;
            bar.style.height = 18;
            bar.style.backgroundColor = color;
            bar.style.marginRight = 10;
            bar.style.flexShrink = 0;
            SetUniformRadius(bar, 2f);
            return bar;
        }

        // ========== ROWS ==========

        /// <summary>
        /// One setting. No border and no fixed height, so the row grows to fit a wrapped
        /// description instead of clipping it. Hover is a plain colour swap with no easing,
        /// matching MaxPractice; the system has no animated transitions anywhere.
        /// </summary>
        public static UITK.VisualElement MakeRow()
        {
            return MakeRow(true);
        }

        public static UITK.VisualElement MakeRow(bool withHover)
        {
            var row = new UITK.VisualElement();
            var s = row.style;
            s.flexDirection = UITK.FlexDirection.Row;
            s.alignItems = UITK.Align.Center;
            s.marginBottom = 8;
            s.backgroundColor = RowBg;
            SetPadding(row, 14, 10, 10, 10);
            SetUniformRadius(row, ROW_RADIUS);
            if (withHover) AddRowHover(row);
            return row;
        }

        /// <summary>Hover swap for a row. Applied by MakeRow already; call it directly only on a
        /// row you built by hand.</summary>
        public static void AddRowHover(UITK.VisualElement row)
        {
            if (row == null) return;
            row.RegisterCallback<UITK.PointerEnterEvent>(_ => row.style.backgroundColor = RowBgHover);
            row.RegisterCallback<UITK.PointerLeaveEvent>(_ => row.style.backgroundColor = RowBg);
        }

        /// <summary>
        /// Sinks a row that the server has switched off. Both halves matter: the darker fill says
        /// this is not a live control, and the opacity carries that through to the widget on the
        /// right, which has its own colours and would otherwise still look enabled. Disables the
        /// hover swap too, because a row that lights up under the cursor invites a click.
        /// </summary>
        public static void SetRowEnabledLook(UITK.VisualElement row, bool enabled)
        {
            if (row == null) return;
            row.style.backgroundColor = enabled ? RowBg : DisabledRowBg;
            // 0.7, not the reference's 0.5. DisabledRowBg is the same colour as SectionBg, so a
            // disabled row already reads as recessed from its fill alone; the opacity is only there
            // to mute the widgets. At 0.5 it also dragged the DISABLED BY SERVER reason down to
            // about 2.0:1, hiding the one line that explains why the row cannot be used.
            row.style.opacity = enabled ? 1f : 0.7f;
        }

        /// <summary>
        /// The text column that opens almost every row: a bold 14px title over an optional 11px
        /// muted description. minWidth 0 is the load-bearing line, it is what lets the description
        /// wrap instead of forcing the row wider than the panel.
        /// </summary>
        public static UITK.VisualElement MakeRowText(string title, string description)
        {
            var col = new UITK.VisualElement();
            col.style.flexDirection = UITK.FlexDirection.Column;
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.minWidth = 0;
            col.style.marginRight = 12;
            col.Add(MakeRowTitle(title));
            if (!string.IsNullOrEmpty(description)) col.Add(MakeRowDescription(description));
            return col;
        }

        public static UITK.Label MakeRowTitle(string text)
        {
            var l = new UITK.Label(text ?? "");
            l.style.fontSize = 14;
            l.style.color = TextPrimary;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 1;
            ForceUIFont(l);
            return l;
        }

        public static UITK.Label MakeRowDescription(string text)
        {
            var l = new UITK.Label(text ?? "");
            l.style.fontSize = 11;
            l.style.color = TextMuted;
            l.style.marginTop = 2;
            l.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(l);
            return l;
        }

        /// <summary>
        /// An inline message. Danger for a hard fault or an unavailable subsystem, TextDim for a
        /// neutral empty state, TextMuted for an explanatory preamble. A note about a whole tab
        /// goes above the section cards, not inside one.
        /// </summary>
        public static UITK.Label MakeNote(string text, Color color)
        {
            var l = new UITK.Label(text ?? "");
            l.style.fontSize = 12;
            l.style.color = color;
            l.style.whiteSpace = UITK.WhiteSpace.Normal;
            l.style.marginBottom = 8;
            ForceUIFont(l);
            return l;
        }

        /// <summary>A plain label at an arbitrary size, for the odd one-off that is not a row
        /// title, a description or a note.</summary>
        public static UITK.Label MakeLabel(string text, float fontSize, Color color)
        {
            var l = new UITK.Label(text ?? "");
            l.style.fontSize = fontSize;
            l.style.color = color;
            ForceUIFont(l);
            return l;
        }

        /// <summary>
        /// The read-only state pill, accent at 15 percent alpha behind accent text. The reference
        /// hardcodes its own accent as floats, so this recomputes the tint from Accent instead of
        /// inheriting MaxPractice's red.
        /// </summary>
        public static UITK.Label MakeStatePill(string text, bool on)
        {
            var l = new UITK.Label(text ?? "");
            l.style.fontSize = 11;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 1;
            l.style.color = on ? Accent : TextDim;
            l.style.minWidth = 42;
            l.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            l.style.paddingTop = 4;
            l.style.paddingBottom = 4;
            SetUniformRadius(l, 4f);
            l.style.backgroundColor = on
                ? new Color(Accent.r, Accent.g, Accent.b, 0.15f)
                : new Color(1f, 1f, 1f, 0.05f);
            ForceUIFont(l);
            return l;
        }

        // ========== BUTTONS ==========

        /// <summary>Applies one of the named recipes. Every variant is also callable directly if
        /// you need to pass a width or a state.</summary>
        public static void StyleButton(UITK.Button b, ButtonVariant variant)
        {
            if (b == null) return;
            switch (variant)
            {
                case ButtonVariant.Primary:  StylePrimaryButton(b); break;
                case ButtonVariant.Danger:   StyleDangerButton(b); break;
                case ButtonVariant.Compact:  StyleCompactButton(b); break;
                case ButtonVariant.Row:      StyleRowButton(b, BTN_W); break;
                case ButtonVariant.Pill:     StylePillButton(b); break;
                case ButtonVariant.Step:     StyleStepButton(b); break;
                case ButtonVariant.Footer:
                case ButtonVariant.Secondary:
                default:                     StyleSecondaryButton(b); break;
            }
        }

        /// <summary>
        /// The neutral workhorse, used for footers and for any button that is not the one
        /// affirmative action on its surface. Text is upper-cased in place because the whole system
        /// shouts its button labels.
        /// </summary>
        public static void StyleSecondaryButton(UITK.Button b)
        {
            if (b == null) return;
            b.text = (b.text ?? "").ToUpperInvariant();
            var s = b.style;
            s.height = BTN_H;
            s.minWidth = 124;
            s.marginLeft = GAP;
            s.fontSize = 13;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.letterSpacing = 1;
            s.backgroundColor = ButtonBg;
            s.color = TextPrimary;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            SetUniformRadius(b, BTN_RADIUS);
            SetUniformBorder(b, 1f, PanelBorder);
            b.focusable = false;
            ForceUIFont(b);
        }

        /// <summary>Alias for the secondary recipe. The reference mods style the footer
        /// identically to any other neutral button; the name exists so call sites can say what
        /// they mean.</summary>
        public static void StyleFooterButton(UITK.Button b)
        {
            StyleSecondaryButton(b);
        }

        /// <summary>
        /// The one affirmative action on a surface. AccentDim fill inside an Accent border is the
        /// system's "this is the accent one" pair, the same combination the ON pill and the
        /// affirmative popup button use, so the three read as one idea. Gets the accent flash,
        /// which lerps further toward Accent on hover.
        /// </summary>
        public static void StylePrimaryButton(UITK.Button b)
        {
            if (b == null) return;
            StyleSecondaryButton(b);
            b.style.backgroundColor = AccentDim;
            SetBorderColor(b, Accent);
            AddButtonFlash(b);
        }

        /// <summary>
        /// Destructive. The system never paints one of these red at rest, only under the cursor,
        /// because a permanently red button in a list of grey ones reads as an error message
        /// rather than as an action. Deliberately gets no accent flash, so the only colour it can
        /// ever show is Danger.
        /// </summary>
        public static void StyleDangerButton(UITK.Button b)
        {
            if (b == null) return;
            StyleSecondaryButton(b);
            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                b.style.backgroundColor = Danger;
                b.style.color = TextPrimary;
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                b.style.backgroundColor = ButtonBg;
                b.style.color = TextPrimary;
            });
        }

        /// <summary>
        /// Paints a two-click destructive button for its armed half. The caller owns the text
        /// ("RESET" against "CONFIRM RESET") and the auto-disarm timer; this is only the fill, so
        /// an armed button does not have to be rebuilt to look armed.
        /// </summary>
        public static void SetArmedLook(UITK.Button b, bool armed)
        {
            if (b == null) return;
            b.style.backgroundColor = armed ? DangerArmed : ButtonBg;
            b.style.color = TextPrimary;
            SetBorderColor(b, armed ? Danger : PanelBorder);
        }

        /// <summary>
        /// An in-row verb button (OPEN, RELOAD, LOCK, UNLOCK). A 32px pill on purpose, matching
        /// the toggle pill's shape, so the right-hand column of a section stays visually aligned
        /// whether a given row ends in a switch or an action.
        /// </summary>
        public static void StyleCompactButton(UITK.Button b)
        {
            if (b == null) return;
            b.text = (b.text ?? "").ToUpperInvariant();
            var s = b.style;
            s.height = 32;
            s.minWidth = 96;
            s.marginLeft = GAP;
            s.fontSize = 12;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.letterSpacing = 2;
            s.backgroundColor = ButtonBg;
            s.color = TextPrimary;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            s.whiteSpace = UITK.WhiteSpace.NoWrap;
            s.flexShrink = 0;
            SetUniformRadius(b, PILL_RADIUS);
            SetUniformBorder(b, 1f, PanelBorder);
            b.focusable = false;
            ForceUIFont(b);
        }

        /// <summary>
        /// A fixed-width button living inside a row, such as BIND. Both minWidth and maxWidth are
        /// set so it cannot stretch when the row's text column is short, which is what keeps a
        /// column of them aligned down the tab.
        /// </summary>
        public static void StyleRowButton(UITK.Button b, float width, string label = null)
        {
            if (b == null) return;
            if (!string.IsNullOrEmpty(label)) b.text = label;
            var s = b.style;
            s.height = BTN_H;
            s.minWidth = width;
            s.maxWidth = width;
            s.fontSize = 12;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.letterSpacing = 2;
            s.whiteSpace = UITK.WhiteSpace.NoWrap;
            s.flexShrink = 0;
            s.marginLeft = GAP;
            s.paddingLeft = 10; s.paddingRight = 10;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            SetUniformRadius(b, BTN_RADIUS);
            SetBorderWidth(b, 0f);
            s.backgroundColor = ButtonBg;
            s.color = TextPrimary;
            ForceUIFont(b);
            AddButtonFlash(b);
        }

        /// <summary>
        /// The ON/OFF pill's geometry. Both minWidth and maxWidth are 72 so it never stretches.
        /// Follow with SetPillState to paint it, and repaint on every click; do not add the button
        /// flash to a pill, because the flash caches a base colour and would fight the state.
        /// </summary>
        public static void StylePillButton(UITK.Button b)
        {
            if (b == null) return;
            var s = b.style;
            s.height = 32;
            s.minWidth = 72;
            s.maxWidth = 72;
            SetPadding(b, 0, 0, 0, 0);
            s.marginLeft = GAP;
            SetUniformRadius(b, PILL_RADIUS);
            SetBorderWidth(b, 1f);
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.fontSize = 12;
            s.letterSpacing = 2;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            s.whiteSpace = UITK.WhiteSpace.NoWrap;
            s.flexShrink = 0;
            b.focusable = false;
            ForceUIFont(b);
        }

        /// <summary>Paints a pill for its state. Off is neutral all the way through, including the
        /// text, so a column of pills reads as a column of switches at a glance.</summary>
        public static void SetPillState(UITK.Button b, bool on)
        {
            if (b == null) return;
            b.text = on ? "ON" : "OFF";
            b.style.backgroundColor = on ? AccentDim : ButtonBg;
            b.style.color = on ? TextPrimary : TextMuted;
            SetBorderColor(b, on ? Accent : PanelBorder);
        }

        /// <summary>The square stepper either side of a prev/next picker value.</summary>
        public static void StyleStepButton(UITK.Button b)
        {
            if (b == null) return;
            var s = b.style;
            s.width = 32;
            s.height = 32;
            s.paddingLeft = 0; s.paddingRight = 0;
            s.marginLeft = 4; s.marginRight = 4;
            s.fontSize = 14;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.backgroundColor = ButtonBg;
            s.color = TextPrimary;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            s.flexShrink = 0;
            SetUniformRadius(b, BTN_RADIUS);
            SetUniformBorder(b, 1f, PanelBorder);
            b.focusable = false;
            ForceUIFont(b);
        }

        /// <summary>
        /// Hover and click feedback that brightens toward the accent instead of washing to white.
        /// The base colour is read off the button as it currently stands, so call this LAST, after
        /// the fill is set.
        ///
        /// Do not use it on a button whose fill changes with state, such as a pill or an armed
        /// destructive button: the handlers restore the cached base on leave and on every geometry
        /// change, which would undo the state paint.
        /// </summary>
        public static void AddButtonFlash(UITK.Button b, int flashMs = 120)
        {
            if (b == null) return;
            Color baseBg = b.style.backgroundColor.keyword != UITK.StyleKeyword.Null
                ? b.style.backgroundColor.value
                : b.resolvedStyle.backgroundColor;
            AddButtonFlash(b, baseBg, flashMs);
        }

        public static void AddButtonFlash(UITK.Button b, Color baseBg, int flashMs = 120)
        {
            if (b == null) return;

            // Deliberately NOT setting focusable here. OWPUI.cs:5324 does, and this was a faithful
            // port of it, but every button recipe in this file sets focusable = false immediately
            // before calling this, so the line silently undid the recipe that had just run. Nothing
            // the flash does needs focus: PointerEnter, PointerLeave and PointerUp all fire on a
            // non-focusable element, and the inline border writes mean no focus ring is drawn
            // anyway. Leaving it set risked a focused button re-activating on a navigation submit.
            Color hoverBg = Color.Lerp(baseBg, Accent, 0.35f);
            Color flashBg = Color.Lerp(baseBg, Accent, 0.6f);

            Action setBase = () =>
            {
                b.style.backgroundColor = baseBg;
                b.style.color = TextPrimary;
            };
            setBase();

            bool hover = false, flashing = false;
            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                hover = true;
                b.style.backgroundColor = hoverBg;
                b.style.color = TextPrimary;
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                hover = false;
                if (!flashing) setBase();
            });
            b.RegisterCallback<UITK.GeometryChangedEvent>(_ => setBase());
            b.RegisterCallback<UITK.PointerUpEvent>(_ =>
            {
                flashing = true;
                b.style.backgroundColor = flashBg;
                b.style.color = TextPrimary;
                b.schedule.Execute(() =>
                {
                    flashing = false;
                    if (!hover) setBase();
                }).StartingIn(flashMs);
            });
        }

        /// <summary>The chip's remove button, which flips to Danger under the cursor. Its own
        /// helper because it is the one button in the system whose hover is red rather than
        /// accent.</summary>
        public static void AddChipButtonFlash(UITK.Button b)
        {
            if (b == null) return;
            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                b.style.backgroundColor = Danger;
                b.style.color = TextPrimary;
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                b.style.backgroundColor = ChipXBg;
                b.style.color = TextPrimary;
            });
        }

        // ========== CHIPS ==========

        /// <summary>A bound key, with no way to remove it.</summary>
        public static UITK.VisualElement MakeChip(string text)
        {
            return MakeChip(text, true, null);
        }

        public static UITK.VisualElement MakeChip(string text, Action onRemove)
        {
            return MakeChip(text, true, onRemove);
        }

        /// <summary>
        /// A chip with an optional remove button. The AccentDim outline is what separates a chip
        /// from a neutral button of the same size: it says this is a value the user put here, not
        /// a control.
        /// </summary>
        public static UITK.VisualElement MakeChip(string text, bool enabled, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            var s = chip.style;
            s.flexDirection = UITK.FlexDirection.Row;
            s.alignItems = UITK.Align.Center;
            s.backgroundColor = ChipBg;
            SetPadding(chip, 10, 6, 4, 4);
            s.marginRight = 4;
            s.flexShrink = 0;
            s.opacity = enabled ? 1f : 0.6f;
            SetUniformRadius(chip, 6f);
            SetUniformBorder(chip, 1f, AccentDim);

            var label = new UITK.Label(text ?? "");
            label.style.fontSize = 13;
            label.style.color = TextPrimary;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 1;
            ForceUIFont(label);
            chip.Add(label);

            if (onRemove != null)
            {
                var x = new UITK.Button(() => onRemove()) { text = "×" };
                var xs = x.style;
                xs.width = 22;
                xs.height = 22;
                xs.marginLeft = 6;
                xs.marginTop = 0; xs.marginBottom = 0; xs.marginRight = 0;
                SetPadding(x, 0, 0, 0, 0);
                xs.backgroundColor = ChipXBg;
                xs.color = TextPrimary;
                xs.fontSize = 16;
                xs.unityFontStyleAndWeight = FontStyle.Bold;
                xs.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                SetUniformRadius(x, 4f);
                SetBorderWidth(x, 0f);
                x.focusable = false;
                ForceUIFont(x);
                if (enabled) AddChipButtonFlash(x);
                chip.Add(x);
            }

            return chip;
        }

        // ========== REAL UITK CONTROLS ==========

        /// <summary>
        /// Recolors a real UITK.Toggle to the palette: a dark well with a neutral outline and an
        /// accent tick. Applied on AttachToPanel as well as immediately, because the inner
        /// ".unity-toggle__input" and ".unity-toggle__checkmark" elements only exist once the
        /// toggle is parented, and the default USS draws a light box that vanishes against these
        /// rows.
        /// </summary>
        public static void StyleToggle(UITK.Toggle t)
        {
            if (t == null) return;
            t.style.marginLeft = GAP;
            t.style.marginRight = 0;
            t.style.flexShrink = 0;
            ForceUIFont(t);

            Action apply = () =>
            {
                var input = t.Q(className: "unity-toggle__input");
                if (input != null)
                {
                    input.style.backgroundColor = TextFieldBg;
                    SetUniformRadius(input, 4f);
                    SetUniformBorder(input, 1f, PanelBorder);
                }
                // The tick is a background image, so the accent has to arrive as a tint. Left
                // untinted it renders in the theme's own near-white and the checked state reads
                // as neutral rather than as this mod's colour.
                var check = t.Q(className: "unity-toggle__checkmark");
                if (check != null) check.style.unityBackgroundImageTintColor = Accent;
            };
            apply();
            t.RegisterCallback<UITK.AttachToPanelEvent>(_ => apply());
        }

        /// <summary>
        /// A real UITK.Slider on the family's rail: a 6px TextFieldBg track with a 14px Accent
        /// thumb. Both parts are looked up across three USS class-name generations because Unity
        /// renames them between versions and only one will match per build.
        ///
        /// A real Slider has no fill element, so the AccentDim filled portion the hand-built
        /// version shows is simply absent. The thumb carries the accent instead, which is why it
        /// is Accent here and not AccentDim.
        /// </summary>
        public static void StyleSlider(UITK.Slider s)
        {
            if (s == null) return;
            s.style.height = SLIDER_ROW_H;
            s.style.marginLeft = 6;
            s.style.marginRight = 6;

            var tracker = s.Q<UITK.VisualElement>(className: "unity-base-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-slider__tracker")
                       ?? s.Q<UITK.VisualElement>(className: "unity-tracker");
            if (tracker != null)
            {
                tracker.style.height = SLIDER_RAIL_H;
                tracker.style.marginTop = SLIDER_RAIL_TOP;
                tracker.style.backgroundColor = TextFieldBg;
                SetUniformRadius(tracker, SLIDER_RAIL_H / 2f);
                SetNoBorder(tracker);
            }

            var dragger = s.Q<UITK.VisualElement>(className: "unity-base-slider__dragger")
                       ?? s.Q<UITK.VisualElement>(className: "unity-slider__dragger")
                       ?? s.Q<UITK.VisualElement>(className: "unity-dragger");
            if (dragger != null)
            {
                dragger.style.width = SLIDER_THUMB;
                dragger.style.height = SLIDER_THUMB;
                dragger.style.marginTop = (SLIDER_ROW_H - SLIDER_THUMB) / 2f;
                dragger.style.backgroundColor = Accent;
                SetUniformRadius(dragger, SLIDER_THUMB / 2f);
                SetNoBorder(dragger);
            }
        }

        /// <summary>
        /// A real UITK.MinMaxSlider on the same rail as StyleSlider, with the selected span in
        /// AccentDim between two Accent thumbs. No reference mod uses this control at all, so the
        /// look is derived: rail equals the slider's track, span equals the hand-built slider's
        /// fill, thumbs equal its thumb.
        ///
        /// Two structural facts drive the odd numbers. The __input container is the box the other
        /// parts are positioned inside, so leaving it at its default height makes the rail measure
        /// against something other than the row. And the two thumbs are children of the DRAGGER,
        /// not of the input, so the dragger cannot simply be made thumb-height without dragging
        /// them with it; it stays rail-height and the thumbs are pulled back out to centre with an
        /// explicit negative top, anchored half their width past each edge of the span.
        /// </summary>
        public static void StyleMinMaxSlider(UITK.MinMaxSlider s)
        {
            if (s == null) return;
            s.style.height = SLIDER_ROW_H;

            var input = s.Q<UITK.VisualElement>(className: "unity-min-max-slider__input")
                     ?? s.Q<UITK.VisualElement>(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.height = SLIDER_ROW_H;
                input.style.flexGrow = 1;
                input.style.marginLeft = 0;
                input.style.marginRight = 0;
                input.style.paddingLeft = 0;
                input.style.paddingRight = 0;
                // The thumbs overhang the span by half their width at each end, and this is their
                // clipping box.
                input.style.overflow = UITK.Overflow.Visible;
            }

            var tracker = s.Q<UITK.VisualElement>(className: "unity-min-max-slider__tracker");
            if (tracker != null)
            {
                tracker.style.position = UITK.Position.Absolute;
                tracker.style.left = 0;
                tracker.style.right = 0;
                tracker.style.top = SLIDER_RAIL_TOP;
                tracker.style.height = SLIDER_RAIL_H;
                tracker.style.marginTop = 0;
                tracker.style.backgroundColor = TextFieldBg;
                SetUniformRadius(tracker, SLIDER_RAIL_H / 2f);
                SetNoBorder(tracker);
            }

            // The selected span. Left and right are owned by the drag logic and must not be
            // touched here, only the vertical box and the paint.
            var dragger = s.Q<UITK.VisualElement>(className: "unity-min-max-slider__dragger");
            if (dragger != null)
            {
                dragger.style.top = SLIDER_RAIL_TOP;
                dragger.style.height = SLIDER_RAIL_H;
                dragger.style.marginTop = 0;
                dragger.style.backgroundColor = AccentDim;
                SetUniformRadius(dragger, SLIDER_RAIL_H / 2f);
                SetNoBorder(dragger);
                dragger.style.overflow = UITK.Overflow.Visible;
            }

            StyleRangeHandle(s.Q<UITK.VisualElement>(className: "unity-min-max-slider__min-thumb"), true);
            StyleRangeHandle(s.Q<UITK.VisualElement>(className: "unity-min-max-slider__max-thumb"), false);
        }

        /// <summary>One end of a MinMaxSlider's span. Centred on its end rather than butted
        /// against it, so the pair reads as two grab points on one bar.</summary>
        public static void StyleRangeHandle(UITK.VisualElement handle, bool isMin)
        {
            if (handle == null) return;
            handle.style.position = UITK.Position.Absolute;
            handle.style.width = SLIDER_THUMB;
            handle.style.height = SLIDER_THUMB;
            handle.style.top = SLIDER_THUMB_TOP;
            if (isMin) handle.style.left = -SLIDER_THUMB / 2f;
            else handle.style.right = -SLIDER_THUMB / 2f;
            handle.style.backgroundColor = Accent;
            SetUniformRadius(handle, SLIDER_THUMB / 2f);
            SetNoBorder(handle);
        }

        /// <summary>
        /// The value label that sits under or beside a slider. Accent, because the number is the
        /// slider's readout and the accent is what ties the two together.
        /// </summary>
        public static UITK.Label MakeSliderValueLabel()
        {
            var l = new UITK.Label();
            l.style.fontSize = 11;
            l.style.color = Accent;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 1;
            l.style.marginTop = 4;
            ForceUIFont(l);
            return l;
        }

        /// <summary>
        /// Paint only, no geometry, because the panel's text fields range from a full-width search
        /// box to a 58px range field and each one owns its own size.
        ///
        /// The inner TextElement is the load-bearing part: in UI Toolkit 1.x it holds the actual
        /// glyph colour and alignment, so styling the outer field alone does not reach the
        /// rendered text in every Unity build.
        /// </summary>
        public static void StyleTextField(UITK.TextField tf)
        {
            StyleTextField(tf, TextPrimary);
        }

        public static void StyleTextField(UITK.TextField tf, Color ink)
        {
            if (tf == null) return;
            tf.style.backgroundColor = TextFieldBg;
            tf.style.color = ink;
            tf.style.justifyContent = UITK.Justify.Center;   // vertically centres the input row
            SetUniformRadius(tf, BTN_RADIUS);
            SetUniformBorder(tf, 1f, PanelBorder);
            ForceUIFont(tf);

            // A TextField is three nested elements and only the outer one was being styled, which
            // is why text sat hard against the border and descenders clipped on the bottom edge.
            // This is the same missing-stylesheet cause as the dropdown: without the default USS
            // the input row and the text element inside it get no padding, no height and no
            // vertical alignment, so they render at whatever the text happens to measure.
            //
            // "unity-base-text-field__input" is TextInputBaseField.inputUssClassName, read off the
            // decompiled UIElements module rather than guessed. A guessed class name silently
            // matches nothing, which is exactly how this went unnoticed.
            var input = tf.Q(className: "unity-base-text-field__input");
            if (input != null)
            {
                var ins = input.style;
                ins.backgroundColor = TextFieldBg;
                ins.paddingLeft = 8; ins.paddingRight = 8;
                ins.paddingTop = 0; ins.paddingBottom = 0;
                ins.marginTop = 0; ins.marginBottom = 0;
                ins.marginLeft = 0; ins.marginRight = 0;
                ins.justifyContent = UITK.Justify.Center;
                ins.overflow = UITK.Overflow.Hidden;
                SetNoBorder(input);
                SetUniformRadius(input, BTN_RADIUS);
                ForceUIFont(input);
            }

            // Queried by TYPE, not by class. The inner renderer carries a generated modifier class
            // whose exact spelling varies by Unity version, and the type is stable.
            var inner = tf.Q<UITK.TextElement>();
            if (inner != null)
            {
                var innerStyle = inner.style;
                innerStyle.color = ink;
                // Transparent, not TextFieldBg: the input row already paints the fill, and a second
                // opaque box on top of it is what produced the hard-edged block behind the glyphs.
                innerStyle.backgroundColor = Color.clear;
                innerStyle.fontSize = 13;
                innerStyle.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
                innerStyle.whiteSpace = UITK.WhiteSpace.NoWrap;
                innerStyle.overflow = UITK.Overflow.Hidden;
                innerStyle.paddingTop = 0; innerStyle.paddingBottom = 0;
                innerStyle.marginTop = 0; innerStyle.marginBottom = 0;
                innerStyle.flexGrow = 1;
                ForceUIFont(inner);
            }
        }

        /// <summary>
        /// The numeric value field from OWPUI.cs:2541: a fixed-width centred field whose text is
        /// Accent while it is editable and TextDim when it is not, so a locked value looks locked
        /// rather than merely unresponsive.
        /// </summary>
        public static void StyleValueField(UITK.TextField tf, float width, bool editable)
        {
            if (tf == null) return;
            Color ink = editable ? Accent : TextDim;
            StyleTextField(tf, ink);
            var s = tf.style;
            s.minWidth = width;
            s.maxWidth = width;
            s.height = 28;
            s.fontSize = 12;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.letterSpacing = 1;
            s.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            s.marginLeft = 6;
            s.marginRight = 6;
            s.flexShrink = 0;
            tf.isReadOnly = !editable;
            tf.selectAllOnFocus = true;

            // By type, matching StyleTextField. This runs after it, so it is overriding the
            // MiddleLeft the base recipe sets, which is the point: a numeric value reads centred.
            var inner = tf.Q<UITK.TextElement>();
            if (inner != null)
                inner.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
        }


        /// <summary>
        /// Every picker list currently open, as its own close delegate.
        ///
        /// A picker parents its list to the UIDocument root so the list can paint outside the
        /// panel's clip, which means the list is NOT a descendant of the panel and does not go away
        /// when the panel is hidden. Every close path in this mod is display-only, so without this
        /// registry an open list survives the panel and keeps a full-rect invisible click catcher
        /// over the whole game. DetachFromPanelEvent does not save it either: that fires on
        /// hierarchy removal, and hiding is not removal.
        ///
        /// A list, not a single reference: there is nothing stopping two pickers being open at once
        /// if a future row builds one outside the panel.
        /// </summary>
        private static readonly List<Action> _openPickers = new List<Action>();

        /// <summary>
        /// Dismisses every open picker list. Call from any path that hides or tears down a surface
        /// containing pickers. Safe to call when nothing is open.
        /// </summary>
        public static void CloseAllPickers()
        {
            if (_openPickers.Count == 0) return;
            // Snapshot first: each Close removes itself from the list as it runs.
            var pending = _openPickers.ToArray();
            for (int i = 0; i < pending.Length; i++)
            {
                try { pending[i](); } catch { }
            }
            _openPickers.Clear();
        }

        /// <summary>
        /// A chevron drawn out of borders instead of a text glyph.
        ///
        /// The obvious way to mark a dropdown is a Label holding "▼", and that is what this used to
        /// be. The problem is glyph coverage: the panel forces the game's own font onto every text
        /// element, that font is Montserrat, and display faces routinely omit the Geometric Shapes
        /// block. A missing glyph renders as nothing at all, so the arrow was liable to simply not
        /// be there, with no error to explain why.
        ///
        /// A square given two adjacent borders and rotated 45 degrees turns its corner into a
        /// point, which needs no font. The tick is wrapped in a fixed-size box so rotating it
        /// cannot disturb the row's layout.
        /// </summary>
        public static UITK.VisualElement MakeCaret(Color color, float size = 7f, float thickness = 1.5f)
        {
            var box = new UITK.VisualElement { name = "CompAdjust_Caret" };
            box.style.width = size * 2f;
            box.style.height = size * 2f;
            box.style.flexShrink = 0;
            box.style.alignItems = UITK.Align.Center;
            box.style.justifyContent = UITK.Justify.Center;
            box.pickingMode = UITK.PickingMode.Ignore;

            var tick = new UITK.VisualElement { name = "CompAdjust_CaretTick" };
            tick.style.width = size;
            tick.style.height = size;
            tick.style.borderTopWidth = 0;
            tick.style.borderLeftWidth = 0;
            tick.style.borderRightWidth = thickness;
            tick.style.borderBottomWidth = thickness;
            tick.style.borderRightColor = color;
            tick.style.borderBottomColor = color;
            tick.style.rotate = new UITK.StyleRotate(new UITK.Rotate(45f));
            tick.pickingMode = UITK.PickingMode.Ignore;
            box.Add(tick);
            return box;
        }

        /// <summary>Flips a MakeCaret between pointing down (closed) and up (open).</summary>
        public static void SetCaretDirection(UITK.VisualElement caret, bool pointingDown)
        {
            if (caret == null) return;
            var tick = caret.Q<UITK.VisualElement>("CompAdjust_CaretTick");
            if (tick == null) return;
            tick.style.rotate = new UITK.StyleRotate(new UITK.Rotate(pointingDown ? 45f : -135f));
        }

        /// <summary>Recolors a MakeCaret, for hover and disabled states.</summary>
        public static void SetCaretColor(UITK.VisualElement caret, Color color)
        {
            if (caret == null) return;
            var tick = caret.Q<UITK.VisualElement>("CompAdjust_CaretTick");
            if (tick == null) return;
            tick.style.borderRightColor = color;
            tick.style.borderBottomColor = color;
        }

        /// <summary>
        /// A hand-built replacement for UITK.DropdownField, and the control you should reach for.
        ///
        /// StyleDropdown above recolors a real DropdownField and it is kept only for anything
        /// already wired to one. It cannot be made to look right, for two reasons that both trace
        /// back to the same cause: Unity's default stylesheet for this control does not load in the
        /// mod context, exactly as OWPUI.cs:2674 says it does not.
        ///
        ///   The closed field has no layout for its arrow, so the arrow and the value text land on
        ///   top of each other as soon as the text is wider than the slot. "DOUBLE PRESS" in a
        ///   150px slot is wider than the slot.
        ///
        ///   The open list is worse and is not reachable at all. Unity builds it as a separate
        ///   hierarchy under the panel root, not as a child of the field, so no amount of styling
        ///   on the field touches it. It renders as unstyled light grey with clipped items.
        ///
        /// So this builds the whole control out of raw elements, which is what all three reference
        /// mods do. The closed state is a flex row of value plus chevron, so the two can never
        /// overlap however long the value is. The open list is our own element parented to the
        /// UIDocument root, which puts it outside the panel's overflow clip and lets it paint over
        /// the rows below it.
        /// </summary>
        /// <param name="choices">Option labels, shown verbatim.</param>
        /// <param name="currentIndex">Index selected on build. Out-of-range falls back to 0.</param>
        /// <param name="onChanged">Fired on user selection only, never on build.</param>
        /// <param name="minWidth">Closed width floor. The open list matches the button's real width.</param>
        public static UITK.Button MakePicker(List<string> choices, int currentIndex,
            Action<string> onChanged, float minWidth = 150f)
        {
            const float ITEM_H = 30f;
            const float LIST_PAD = 4f;

            var opts = choices ?? new List<string>();
            int index = (currentIndex >= 0 && currentIndex < opts.Count) ? currentIndex : 0;

            var btn = new UITK.Button();
            btn.text = "";                       // the value is a child Label, not the Button's own text
            var s = btn.style;
            s.height = BTN_H;
            s.minWidth = minWidth;
            s.flexGrow = 0;
            s.flexShrink = 1;
            s.marginLeft = GAP;
            s.marginRight = 0;
            s.paddingLeft = 12; s.paddingRight = 10;
            s.paddingTop = 0; s.paddingBottom = 0;
            s.flexDirection = UITK.FlexDirection.Row;
            s.alignItems = UITK.Align.Center;
            s.backgroundColor = TextFieldBg;
            SetUniformRadius(btn, BTN_RADIUS);
            SetUniformBorder(btn, 1f, PanelBorder);
            btn.focusable = false;
            ForceUIFont(btn);

            // minWidth 0 plus Hidden is what makes a too-long value clip at the chevron instead of
            // pushing it out of the button.
            var value = new UITK.Label(index < opts.Count ? opts[index] : "");
            value.style.flexGrow = 1;
            value.style.flexShrink = 1;
            value.style.minWidth = 0;
            value.style.overflow = UITK.Overflow.Hidden;
            value.style.color = TextPrimary;
            value.style.fontSize = 13;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            value.style.letterSpacing = 1;
            value.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            value.pickingMode = UITK.PickingMode.Ignore;
            ForceUIFont(value);
            btn.Add(value);

            // Accent rather than TextMuted: this is the one mark that says the control opens, and
            // at 7px a muted grey tick on TextFieldBg was there but easy to miss.
            var chevron = MakeCaret(Accent);
            chevron.style.marginLeft = 8;
            btn.Add(chevron);

            // The caret brightens with the row so the whole control reads as one hoverable thing.
            btn.RegisterCallback<UITK.MouseEnterEvent>(_ =>
            {
                btn.style.backgroundColor = RowBgHover;
                SetCaretColor(chevron, TextPrimary);
            });
            btn.RegisterCallback<UITK.MouseLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = TextFieldBg;
                SetCaretColor(chevron, Accent);
            });

            UITK.VisualElement overlay = null;
            Action closeSelf = null;

            void Close()
            {
                if (overlay != null) { overlay.RemoveFromHierarchy(); overlay = null; }
                if (closeSelf != null) _openPickers.Remove(closeSelf);
                SetCaretDirection(chevron, true);
            }
            closeSelf = Close;

            void Open()
            {
                // panel.visualTree is the UIDocument root. Parenting there rather than to our own
                // panel is the whole point: the panel sets overflow Hidden, so a list added inside
                // it would be clipped at the panel edge instead of floating over the rows.
                var root = btn.panel != null ? btn.panel.visualTree : null;
                if (root == null || opts.Count == 0) return;

                overlay = new UITK.VisualElement { name = "CompAdjust_PickerOverlay" };
                var os = overlay.style;
                os.position = UITK.Position.Absolute;
                os.left = 0; os.top = 0; os.right = 0; os.bottom = 0;
                overlay.pickingMode = UITK.PickingMode.Position;

                // Full-rect catcher. Both edges matter and the order is not interchangeable.
                //
                // Dismissing on the DOWN edge was the original mistake: Close() removed the overlay
                // immediately, so the matching UP edge was re-picked against whatever was underneath.
                // Outside the panel that is the mod's backdrop, whose own close is registered on
                // PointerUp, so clicking away from an open list closed the entire panel. Nothing
                // captures the pointer here to prevent that; only Clickable does, and this is a bare
                // VisualElement.
                //
                // So the down edge is swallowed but does not dismiss, and the up edge dismisses.
                overlay.RegisterCallback<UITK.PointerDownEvent>(e => e.StopPropagation());
                overlay.RegisterCallback<UITK.PointerUpEvent>(e => { Close(); e.StopPropagation(); });

                var list = new UITK.VisualElement();
                var ls = list.style;
                ls.position = UITK.Position.Absolute;
                ls.flexDirection = UITK.FlexDirection.Column;
                ls.backgroundColor = SectionBg;
                ls.paddingTop = LIST_PAD; ls.paddingBottom = LIST_PAD;
                SetUniformRadius(list, BTN_RADIUS);
                SetUniformBorder(list, 1f, PanelBorder);
                list.RegisterCallback<UITK.PointerDownEvent>(e => e.StopPropagation());

                var wb = btn.worldBound;
                float listH = opts.Count * ITEM_H + LIST_PAD * 2f + 2f;
                ls.width = Mathf.Max(wb.width, minWidth);
                ls.left = wb.xMin;

                // Drop down if there is room, otherwise drop up. Without the flip, a picker on the
                // last visible row opens off the bottom of the screen.
                float rootH = root.worldBound.height > 1f ? root.worldBound.height : Screen.height;
                float below = wb.yMax + 4f;
                ls.top = (below + listH <= rootH) ? below : Mathf.Max(4f, wb.yMin - listH - 4f);

                for (int i = 0; i < opts.Count; i++)
                {
                    int pick = i;
                    bool selected = (i == index);

                    var item = new UITK.Button(() =>
                    {
                        index = pick;
                        value.text = opts[pick];
                        Close();
                        if (onChanged != null) onChanged(opts[pick]);
                    }) { text = opts[i] };

                    var its = item.style;
                    its.height = ITEM_H;
                    its.marginTop = 0; its.marginBottom = 0;
                    its.marginLeft = 0; its.marginRight = 0;
                    its.paddingLeft = 12; its.paddingRight = 12;
                    its.backgroundColor = selected ? AccentDim : Color.clear;
                    its.color = selected ? TextPrimary : TextMuted;
                    its.fontSize = 13;
                    its.unityFontStyleAndWeight = FontStyle.Bold;
                    its.letterSpacing = 1;
                    its.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
                    SetUniformRadius(item, 3f);
                    SetNoBorder(item);
                    item.focusable = false;
                    ForceUIFont(item);

                    // Hover only repaints the unselected rows, so the selection stays legible as
                    // the pointer travels over it.
                    if (!selected)
                    {
                        item.RegisterCallback<UITK.MouseEnterEvent>(_ =>
                        {
                            item.style.backgroundColor = RowBgHover;
                            item.style.color = TextPrimary;
                        });
                        item.RegisterCallback<UITK.MouseLeaveEvent>(_ =>
                        {
                            item.style.backgroundColor = Color.clear;
                            item.style.color = TextMuted;
                        });
                    }

                    list.Add(item);
                }

                overlay.Add(list);
                root.Add(overlay);
                overlay.BringToFront();
                if (!_openPickers.Contains(closeSelf)) _openPickers.Add(closeSelf);
                SetCaretDirection(chevron, false);
            }

            btn.clicked += () =>
            {
                // While open, the full-rect overlay sits above the button and swallows the click,
                // so this only ever runs from the closed state. The null check is belt and braces.
                if (overlay != null) Close(); else Open();
            };

            // A picker left open while its row is torn down would strand the overlay on the root
            // with nothing to dismiss it, since the overlay is not our child.
            btn.RegisterCallback<UITK.DetachFromPanelEvent>(_ => Close());

            return btn;
        }

        /// <summary>
        /// The scrolling body. The system does not restyle Unity's scrollbars anywhere, so this
        /// deliberately only sets the flex box and the scroller visibility.
        ///
        /// minHeight 0 is not cosmetic: without it a flex child refuses to shrink below its
        /// content, and inside a clipped panel that pushes the footer off the bottom edge.
        /// </summary>
        public static void StyleScrollView(UITK.ScrollView sv)
        {
            if (sv == null) return;
            sv.verticalScrollerVisibility = UITK.ScrollerVisibility.Auto;
            sv.horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden;
            sv.style.flexGrow = 1;
            sv.style.flexShrink = 1;
            sv.style.minHeight = 0;
        }

        // ========== KEY CAPTURE ==========

        /// <summary>
        /// The full-rect wash behind a rebind prompt. Added to the UIDocument ROOT rather than to
        /// the panel, so it covers everything, and it swallows pointer-up so a stray click during
        /// a rebind cannot reach whatever is underneath. Starts hidden; BringToFront it on show.
        /// </summary>
        public static UITK.VisualElement MakeCaptureOverlay(string name = "DashFall_CaptureOverlay")
        {
            var overlay = new UITK.VisualElement { name = name };
            var s = overlay.style;
            s.position = UITK.Position.Absolute;
            s.left = 0; s.top = 0; s.right = 0; s.bottom = 0;
            s.backgroundColor = CaptureOverlayBg;
            s.display = UITK.DisplayStyle.None;
            overlay.pickingMode = UITK.PickingMode.Position;
            overlay.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
            ForceUIFont(overlay);
            return overlay;
        }

        /// <summary>
        /// The centred card inside the capture overlay. Its 2px border is Capture, not Accent:
        /// while the mod is listening for a keypress the surface has to look like a different mode
        /// rather than like another orange panel.
        /// </summary>
        public static UITK.VisualElement MakeCaptureCard()
        {
            var card = new UITK.VisualElement();
            var s = card.style;
            s.position = UITK.Position.Absolute;
            s.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            s.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            s.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
            s.flexDirection = UITK.FlexDirection.Column;
            s.alignItems = UITK.Align.Center;
            s.justifyContent = UITK.Justify.Center;
            SetPadding(card, 60, 60, 40, 40);
            s.backgroundColor = CaptureBg;
            SetUniformRadius(card, 16f);
            SetUniformBorder(card, 2f, Capture);
            return card;
        }

        /// <summary>The small all-caps line above a capture title, in the capture hue.</summary>
        public static UITK.Label MakeCaptureKicker(string text)
        {
            var l = new UITK.Label((text ?? "").ToUpperInvariant());
            l.style.fontSize = 14;
            l.style.color = Capture;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 6;
            l.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            l.style.marginBottom = 8;
            ForceUIFont(l);
            return l;
        }

        /// <summary>
        /// Paints a BIND button for whether it is currently listening. Fill goes to the cool
        /// capture hue with dark ink on it, copying OWP's light-amber trick, because TextPrimary
        /// cannot hold against a colour that bright.
        ///
        /// Both halves are restored on the way out, including the ink. OWP restores only the fill,
        /// which leaves a dark-inked button sitting on a dark fill after a cancelled rebind.
        /// </summary>
        public static void StyleCaptureButton(UITK.Button b, bool listening)
        {
            if (b == null) return;
            b.style.backgroundColor = listening ? Capture : ButtonBg;
            b.style.color = listening ? CaptureInk : TextPrimary;
            SetBorderColor(b, listening ? Capture : PanelBorder);
        }

        // ========== FOOTER ==========

        /// <summary>
        /// The footer strip under the scrolling body. Right-aligned, because the footer holds
        /// closing actions and the eye finishes a panel on that side. Anything that belongs on the
        /// left goes in with a flexGrow 1 spacer after it.
        /// </summary>
        public static UITK.VisualElement MakeFooter()
        {
            var footer = new UITK.VisualElement();
            footer.style.flexDirection = UITK.FlexDirection.Row;
            footer.style.alignItems = UITK.Align.Center;
            footer.style.justifyContent = UITK.Justify.FlexEnd;
            footer.style.marginTop = 12;
            footer.style.flexShrink = 0;
            return footer;
        }

        /// <summary>A flexGrow 1 gap, for pushing one cluster of footer buttons away from
        /// another.</summary>
        public static UITK.VisualElement MakeSpacer()
        {
            var sp = new UITK.VisualElement();
            sp.style.flexGrow = 1;
            return sp;
        }
    }
}
