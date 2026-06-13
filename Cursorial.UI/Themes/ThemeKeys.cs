namespace Cursorial.UI.Themes;

/// <summary>
/// The <c>"Theme.*"</c> resource-key naming convention (design doc §11.8) — string constants that
/// are typo-proof from C# and verbatim from XAML. S8 adds control-specific keys here as its content
/// lands.
/// <para>
/// <b>Palette-spine status (R2 — wired):</b> the color-bearing setters in the built-in control themes
/// (<c>ControlThemes</c>) are <see cref="ResourceReference"/>s into these keys (via
/// <see cref="Style.SetResource{T}"/>), so overriding a single key at a nearer chain scope — or a
/// <c>RequestedThemeBase</c>/<c>RequestedColorTier</c> flip — re-skins every default-look control with
/// zero template work: the styling frame re-resolves the key per element on the resource pulse and the
/// AffectsRender change re-rasters only the affected zone. The setters arm at the
/// <c>ControlTheme</c> layer (below <see cref="BindingPriority.LocalValue"/>), so an explicit local
/// value the consumer sets always wins. Two deliberate exclusions stay constants: the <c>:focus</c> /
/// <c>:default</c> border-WEIGHT escalation pens (render-only, NoColor-safe — they are weight bumps,
/// not colors) and a control's resting <c>Background</c> (an unset/transparent face is the
/// WPF/Avalonia default — the surface paints only when the consumer sets it). The <see cref="GlyphSetCarrier"/>
/// keys (<see cref="CheckBoxGlyphs"/>/<see cref="RadioGlyphs"/>/<see cref="ScrollArrowGlyphs"/>) have
/// always been live resource reads.
/// </para>
/// </summary>
public static class ThemeKeys
{
    /// <summary>The default surface (panel/window) background brush.</summary>
    public const string SurfaceBrush = "Theme.SurfaceBrush";

    /// <summary>The default text/foreground brush.</summary>
    public const string TextBrush = "Theme.TextBrush";

    /// <summary>The accent (selection / emphasis) brush.</summary>
    public const string AccentBrush = "Theme.AccentBrush";

    /// <summary>The focus-ring pen.</summary>
    public const string FocusPen = "Theme.FocusPen";

    /// <summary>The default border pen.</summary>
    public const string BorderPen = "Theme.BorderPen";

    /// <summary>The modal-dimming overlay brush (the <c>^.obscured</c> rule, design doc §11.8).</summary>
    public const string ObscuredOverlayBrush = "Theme.ObscuredOverlayBrush";

    /// <summary>The access-key underline brush (the <c>:access-keys</c> cue, design doc §11.8).</summary>
    public const string AccessKeyUnderlineBrush = "Theme.AccessKeyUnderlineBrush";

    /// <summary>
    /// The <see cref="Controls.CheckBox"/> glyph triple — a <see cref="GlyphSetCarrier"/> of
    /// <c>(Unchecked, Checked, Indeterminate)</c> strings (design doc §12.7; spec line 660). The
    /// true-ASCII default is <c>[ ] [x] [-]</c>; a <c>caps-unicode</c> dictionary may swap to
    /// <c>☐ ☑ ◪</c>-class glyphs.
    /// </summary>
    public const string CheckBoxGlyphs = "Theme.CheckBoxGlyphs";

    /// <summary>The <see cref="Controls.RadioButton"/> glyph triple (<c>( ) (*) ( )</c> ASCII default; design doc §12.7).</summary>
    public const string RadioGlyphs = "Theme.RadioGlyphs";

    /// <summary>The <see cref="Controls.ScrollBar"/> line-button arrow glyphs (<c>(Up/Left, Down/Right)</c>; ASCII <c>^ v</c> / <c>&lt; &gt;</c>; design doc §12.7).</summary>
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
/// <param name="Unchecked">The first glyph (unchecked box / up-or-left arrow).</param>
/// <param name="Checked">The second glyph (checked box / down-or-right arrow).</param>
/// <param name="Indeterminate">The third glyph (the indeterminate box; unused for a two-glyph arrow pair).</param>
public readonly record struct GlyphSetCarrier(string Unchecked, string Checked, string Indeterminate = "")
{
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
        _ => Unchecked,
    };
}
