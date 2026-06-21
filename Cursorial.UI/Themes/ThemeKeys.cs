namespace Cursorial.UI.Themes;

/// <summary>
/// The <c>"Theme.*"</c> resource-key naming convention (design doc §11.8 / §11.8a) — string constants that
/// are typo-proof from C# and verbatim from XAML. S8 adds control-specific keys here as its content lands.
/// <para>
/// <b>Cell-faithful role-token spine (design doc §11.8a; <c>default-theme-adoption-spec.md</c>):</b> the
/// default theme is <i>fill-bounded</i>, not line-bounded. Control identity is carried by these whole-cell
/// fill/foreground role tokens — never by stroked borders. Each is a <see cref="Cursorial.Drawing.Media.IBrush"/>
/// authored per <c>(ThemeBase, ColorDepth)</c> variant in <see cref="CursorialTheme"/>; control themes
/// <see cref="ResourceReference"/> into them via <see cref="Style.SetResource{T}"/>, so overriding one token at a
/// nearer chain scope — or a <c>RequestedThemeBase</c>/<c>RequestedColorTier</c> flip — re-skins every
/// default-look control with zero template work (the <b>R2</b> spine wiring is landed). The setters arm at the
/// <c>ControlTheme</c> layer (below <see cref="BindingPriority.LocalValue"/>), so an explicit local value the
/// consumer sets always wins.
/// </para>
/// <para>
/// <b>Focus model:</b> reverse-video for pickable controls (a <see cref="TextBrush"/>/<see cref="WindowBackground"/>
/// brush pair), an intensified <see cref="WellBrush"/> + caret for text controls — both pure paint-only flips,
/// no sub-cell ring. <see cref="BorderPen"/>/<see cref="FocusPen"/> are <b>opt-in chrome keys only</b> (for
/// <c>Border</c>/GroupBox/Window frames), not spine members. The <see cref="GlyphSetCarrier"/> keys are live
/// resource reads.
/// </para>
/// </summary>
public static class ThemeKeys
{
    // ───────────────────────────── role tokens (the cell-faithful spine, §11.8a) ─────────────────────────────

    /// <summary>Page/window background; also the reverse-video <i>text</i> color on a focused pick control.</summary>
    public const string WindowBackground = "Theme.WindowBackground";

    /// <summary>Resting control fill (button, field, header).</summary>
    public const string SurfaceBrush = "Theme.SurfaceBrush";

    /// <summary>Popup / list / grid / menu surface fill.</summary>
    public const string PanelBrush = "Theme.PanelBrush";

    /// <summary>Intensified fill for a focused <i>text</i> field.</summary>
    public const string WellBrush = "Theme.WellBrush";

    /// <summary>Selection fill (selected item/text) in a focused container.</summary>
    public const string SelectionBrush = "Theme.SelectionBrush";

    /// <summary>Selection fill in an <b>unfocused</b> container (the neutral-grey inactive selection, spec --sel-inactive).</summary>
    public const string SelectionInactiveBrush = "Theme.SelectionInactiveBrush";

    /// <summary>Even-row zebra fill in lists/grids (spec --altrow; consumed by the opt-in <c>:alternate</c> row look).</summary>
    public const string AlternateRowBrush = "Theme.AlternateRowBrush";

    /// <summary>Shared pointer-over (hover) fill.</summary>
    public const string HoverBrush = "Theme.HoverBrush";

    /// <summary>Primary text / foreground; also the reverse-video <i>fill</i> on a focused pick control.</summary>
    public const string TextBrush = "Theme.TextBrush";

    /// <summary>Secondary (de-emphasized) text.</summary>
    public const string TextDimBrush = "Theme.TextDimBrush";

    /// <summary>Tertiary text and glyph ink.</summary>
    public const string MutedBrush = "Theme.MutedBrush";

    /// <summary>Inactive track / faint glyph (slider/scrollbar track, empty progress segment).</summary>
    public const string FaintBrush = "Theme.FaintBrush";

    /// <summary>Disabled control fill.</summary>
    public const string DisabledBackgroundBrush = "Theme.DisabledBackgroundBrush";

    /// <summary>Disabled text/glyph ink.</summary>
    public const string DisabledForegroundBrush = "Theme.DisabledForegroundBrush";

    /// <summary>Focus accent / links / pressed-or-default fill / today.</summary>
    public const string AccentBrush = "Theme.AccentBrush";

    /// <summary>Secondary accent (hover-link, folder glyph).</summary>
    public const string Accent2Brush = "Theme.Accent2Brush";

    /// <summary>Text drawn on an accent/colored fill (pressed-button text, badge text).</summary>
    public const string OnAccentBrush = "Theme.OnAccentBrush";

    /// <summary>Success / on.</summary>
    public const string GreenBrush = "Theme.GreenBrush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string AmberBrush = "Theme.AmberBrush";

    /// <summary>Error / danger.</summary>
    public const string RedBrush = "Theme.RedBrush";

    /// <summary>Pressed-slider thumb / visited link / file glyph.</summary>
    public const string PurpleBrush = "Theme.PurpleBrush";

    /// <summary>Status bar default background.</summary>
    public const string StatusBarBackground = "Theme.StatusBarBackground";

    /// <summary>Status bar alternate/branch background.</summary>
    public const string StatusBarAltBackground = "Theme.StatusBarAltBackground";

    /// <summary>Status bar alternate/branch foreground.</summary>
    public const string StatusBarAltForeground = "Theme.StatusBarAltForeground";

    /// <summary>Background at the Desktop elevation level.</summary>
    public const string ElevationDesktop = "Theme.ElevationDesktop";

    /// <summary>Background at the Window elevation level.</summary>
    public const string ElevationWindow = "Theme.ElevationWindow";

    /// <summary>Background at the Raised elevation level.</summary>
    public const string ElevationRaised = "Theme.ElevationRaised";
    
    /// <summary>Background at the Highest elevation level.</summary>
    public const string ElevationHighest = "Theme.ElevationHighest";
    
    /// <summary>Background at the Well (lowest) elevation level.</summary>
    public const string ElevationWell = "Theme.ElevationWell";

    // ───────────────────────────── opt-in chrome (NOT spine members, §11.8a) ─────────────────────────────

    /// <summary>
    /// Opt-in focus-ring pen — <b>not</b> the default focus mechanism (focus is reverse-video / well+caret).
    /// Retained for apps that re-introduce a bordered look; no common control reads it by default.
    /// </summary>
    public const string FocusPen = "Theme.FocusPen";

    /// <summary>
    /// Opt-in border pen — <b>not</b> a spine member (the cell-faithful default is fill-bounded). Survives for
    /// the line-drawn surfaces (<c>Border</c>/GroupBox/Expander/Window chrome) that genuinely want a frame.
    /// </summary>
    public const string BorderPen = "Theme.BorderPen";

    /// <summary>
    /// Heavy pen used for the Separator control outside of menu contexts.
    /// </summary>
    public const string SeparatorPen = "Theme.SeparatorPen";

    /// <summary>
    /// Light pen used for the Separator control within menu contexts.
    /// </summary>
    public const string MenuSeparatorPen = "Theme.MenuSeparatorPen";

    // ───────────────────────────── infrastructure (carried over) ─────────────────────────────

    /// <summary>The modal-dimming overlay brush (the <c>^.obscured</c> rule, design doc §11.8).</summary>
    public const string ObscuredOverlayBrush = "Theme.ObscuredOverlayBrush";

    /// <summary>The access-key underline brush (the <c>:access-keys</c> cue, design doc §11.8).</summary>
    public const string AccessKeyUnderlineBrush = "Theme.AccessKeyUnderlineBrush";

    /// <summary>
    /// The <see cref="Controls.CheckBox"/> glyph triple — a <see cref="GlyphSetCarrier"/> of
    /// <c>(Unchecked, Checked, Indeterminate)</c> strings (design doc §12.7; spec line 660). This resource
    /// is the caps-ascii <b>base</b> (<c>[ ] [x] [-]</c>); under <c>.caps-unicode</c> the marks are opted
    /// UP to colored Unicode (<c>[ ] [✓] [▪]</c>) via <c>ToggleGlyph.GlyphsProperty</c>, which is read
    /// <b>before</b> this resource and therefore WINS — so shadowing this resource at a nearer scope only
    /// takes effect on a caps-ascii terminal (caps-unicode is unconditionally stamped at P5; SD14).
    /// </summary>
    public const string CheckBoxGlyphs = "Theme.CheckBoxGlyphs";

    /// <summary>The <see cref="Controls.RadioButton"/> glyph triple (caps-ascii base <c>( ) (*) (-)</c>; caps-unicode opts up to <c>( ) (●) (-)</c> via the attached override — see <see cref="CheckBoxGlyphs"/>; design doc §12.7).</summary>
    public const string RadioGlyphs = "Theme.RadioGlyphs";

    /// <summary>The <see cref="Controls.ScrollBar"/> line-button arrow glyphs (the reserved ASCII base <c>^ v</c> / <c>&lt; &gt;</c>). NOTE: the BuiltIn ScrollBar template currently hardcodes Unicode arrows (◀▲▶▼) and does NOT yet read this resource — a caps-ascii opt-down is future work (same caps-ascii deferral as the Unicode check/radio marks; design doc §12.7).</summary>
    public const string ScrollArrowGlyphs = "Theme.ScrollArrowGlyphs";
}

/// <summary>
/// A small immutable carrier for a control's themeable glyph strings — a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
/// <c>(Unchecked, Checked, Indeterminate)</c> triple, a scroll-bar's arrow pair (design doc §12.7,
/// spec line 660). Glyphs are theme <b>resources</b> so a variant / capability dictionary can swap
/// them; the framework keys them under <see cref="ThemeKeys.CheckBoxGlyphs"/> /
/// <see cref="ThemeKeys.RadioGlyphs"/> / <see cref="ThemeKeys.ScrollArrowGlyphs"/>. The defaults are
/// true ASCII (zero ambiguous-width risk; cf. the ambiguous-width project memory).
/// </summary>
public record GlyphSetCarrier
{
    /// <summary>
    /// A small immutable carrier for a control's themeable glyph strings, such as a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
    /// <c>(Unchecked, Checked, Indeterminate)</c> triple or a scroll-bar's arrow pair. This allows glyphs to be defined as theme
    /// resources which can be dynamically swapped using a variant or capability dictionary. These glyphs are accessed using keys
    /// like <see cref="ThemeKeys.CheckBoxGlyphs"/>, <see cref="ThemeKeys.RadioGlyphs"/>, or <see cref="ThemeKeys.ScrollArrowGlyphs"/>.
    /// Default glyphs are designed to avoid ambiguous-width risks and typically leverage ASCII.
    /// </summary>
    public GlyphSetCarrier() {}

    /// <summary>
    /// A small immutable carrier for a control's themeable glyph strings — a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
    /// <c>(Unchecked, Checked, Indeterminate)</c> triple, a scroll-bar's arrow pair (design doc §12.7,
    /// spec line 660). Glyphs are theme <b>resources</b> so a variant / capability dictionary can swap
    /// them; the framework keys them under <see cref="ThemeKeys.CheckBoxGlyphs"/> /
    /// <see cref="ThemeKeys.RadioGlyphs"/> / <see cref="ThemeKeys.ScrollArrowGlyphs"/>. The defaults are
    /// true ASCII (zero ambiguous-width risk; cf. the ambiguous-width project memory).
    /// </summary>
    /// <param name="unchecked">The first glyph (unchecked box / up-or-left arrow).</param>
    /// <param name="checked">The second glyph (checked box / down-or-right arrow).</param>
    /// <param name="indeterminate">The third glyph (the indeterminate box; unused for a two-glyph arrow pair).</param>
    public GlyphSetCarrier(string @unchecked, string @checked, string @indeterminate = "")
    {
        Unchecked = @unchecked;
        Checked = @checked;
        Indeterminate = @indeterminate;
    }

    /// <summary>
    /// Selects the glyph for a three-state checked value (<see langword="false"/>/<see langword="true"/>/
    /// <see langword="null"/>). When this set carries no third glyph (the two-argument constructor leaves
    /// <see cref="Indeterminate"/> empty), the indeterminate (<see langword="null"/>) value falls back to
    /// the <see cref="Unchecked"/> glyph — so the indeterminate state is visually indistinguishable from
    /// unchecked unless a third glyph is supplied. The built-in <c>RadioGlyphs</c>/<c>CheckBoxGlyphs</c>
    /// defaults supply a distinct indeterminate glyph.
    /// </summary>
    public string ForChecked(bool? value) => value switch
                                             {
                                                 true => Checked,
                                                 null => Indeterminate.Length > 0 ? Indeterminate : Unchecked,
                                                 _    => Unchecked
                                             };

    /// <summary>The first glyph (unchecked box / up-or-left arrow).</summary>
    public string Unchecked { get; init; } = "";

    /// <summary>The second glyph (checked box / down-or-right arrow).</summary>
    public string Checked { get; init; } = "";

    /// <summary>The third glyph (the indeterminate box; unused for a two-glyph arrow pair).</summary>
    public string Indeterminate { get; init; } = "";

    public void Deconstruct(out string @unchecked, out string @checked, out string indeterminate)
    {
        @unchecked = Unchecked;
        @checked = Checked;
        indeterminate = Indeterminate;
    }
}
