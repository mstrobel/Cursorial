
using Cursorial.Output;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// The paint-time resolution of the <see cref="TextElement"/> attribute properties into the Drawing
/// tier's vocabulary (proposal-TextAttributes-decomposition §3.1): the folded flag bitset (including
/// the <see cref="TextAttributes.Underline"/> presence bit) plus the underline shape, meaningful only
/// while the presence bit is set. Renderers obtain one per <c>Render</c> call via
/// <see cref="TextElement.ComposeAttributes"/> — the single meeting point of the per-axis properties
/// and <see cref="TextAttributes"/>.
/// </summary>
/// <param name="Flags">The folded bitset (the wire/cell vocabulary — <c>Output.TextAttributes</c>).</param>
/// <param name="UnderlineShape">The underline shape; meaningful only when <paramref name="Flags"/> carries <see cref="TextAttributes.Underline"/>.</param>
public readonly record struct ResolvedTextAttributes(TextAttributes Flags, UnderlineStyle UnderlineShape)
{
    /// <summary>Whether the folded flags carry <see cref="TextAttributes.Inverse"/> (the Border fill's one-flag read).</summary>
    public bool Inverse => (Flags & TextAttributes.Inverse) != 0;
}

/// <summary>
/// The text-styling spine (design doc §12.1): the inherited <see cref="ForegroundProperty"/> brush
/// (set high in the tree, it colors every descendant text element that does not override it) plus
/// the per-axis text-attribute properties (<see cref="TextWeightProperty"/>/<see cref="TextStyleProperty"/>/
/// <see cref="UnderlineProperty"/>/…), which are NON-inheriting and "flow like <c>Background</c>":
/// element-level values delivered to template parts and generated leaves by explicit forwards
/// (proposal-TextAttributes-decomposition §2.1). Renderers read the folded effective attributes via
/// <see cref="ComposeAttributes"/>.
/// </summary>
public abstract class TextElement
{
    private TextElement() => throw new InvalidOperationException($"Class '{nameof(TextElement)}' is not instantiatable.");

    /// <summary>
    /// The inherited text foreground brush (<c>Inherits | AffectsRender</c>). Defaults through the
    /// theme's <see cref="Themes.ThemeKeys.TextBrush"/> (a theme-reactive default): a bare text
    /// element is legible with zero ambient setup — a design-surface root, a UserControl previewed
    /// alone — while ANY real contribution, an inherited value from a window or template included,
    /// beats it by lane arithmetic.
    /// </summary>
    public static readonly AttachedProperty<IBrush?> ForegroundProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, IBrush?>(
            "Foreground",
            new PropertyMetadata<IBrush?>(Brushes.Default) { DefaultResourceKey = Themes.ThemeKeys.TextBrush },
            inherits: true);

    static TextElement()
    {
        // Attached properties land on arbitrary host types, so AffectsRender rides the global
        // effects lane (A1) — not a per-owner-type registration. (Foreground fans out to
        // descendants via inheritance; the per-axis attribute properties are non-inheriting and
        // re-render only their own element — proposal-TextAttributes-decomposition §2.1.)
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ForegroundProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextWeightProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextStyleProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, UnderlineProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, UnderlineBrushProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, StrikethroughProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, OverlineProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, InverseProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, BlinkProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ConcealedProperty);

        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextTrimmingProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsMeasure, TextTrimmingProperty);

        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextWrappingProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsMeasure, TextWrappingProperty);
        
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, SizingProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsMeasure, SizingProperty);
        
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, FontProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsMeasure, FontProperty);
    }

    /// <summary>Reads the inherited foreground brush attached to <paramref name="element"/>.</summary>
    public static IBrush? GetForeground(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ForegroundProperty);
    }

    /// <summary>Sets the foreground brush on <paramref name="element"/> (inherits to its descendants).</summary>
    public static void SetForeground(UIElement element, IBrush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ForegroundProperty, value);
    }

    // ─────────────────────────────────────── text formatting properties ────────────────────────────────────────

    /// <summary>The wrap mode (<c>AffectsMeasure | AffectsRender</c>).</summary>
    public static readonly AttachedProperty<WrapMode> TextWrappingProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, WrapMode>("TextWrapping",
                                                                      defaultValue: WrapMode.NoWrap);

    /// <summary>The trimming mode for overflowing lines (<c>AffectsRender</c>).</summary>
    public static readonly AttachedProperty<TextTrimming> TextTrimmingProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextTrimming>(nameof(TextTrimming),
                                                                          defaultValue: TextTrimming.CharacterEllipsis);

    /// <inheritdoc cref="IsTrimmedProperty"/>
    internal static readonly UIPropertyKey<bool> IsTrimmedPropertyKey =
        UIProperty.RegisterAttachedReadOnly<TextElement, UIElement, bool>(nameof(TextBlock.IsTrimmed));

    /// <summary>Indicates whether any of the text content had trimming applied.</summary>
    public static readonly AttachedProperty<bool> IsTrimmedProperty =
        (AttachedProperty<bool>)IsTrimmedPropertyKey.Property;

    /// <summary>Gets a value indicating whether the text content of the given element has trimmed.</summary>
    public static bool GetIsTrimmed(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsTrimmedProperty);
    }

    /// <summary>Sets a value indicating whether the text content of the given element has trimmed.</summary>
    internal static void SetIsTrimmed(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsTrimmedProperty, value);
    }

    /// <summary>
    /// The OSC 66 sizing for the element's text (proposal-glyph-runs Phase 3): a non-normal
    /// sizing renders the text scaled on supporting terminals and through the bundled fallback
    /// face elsewhere (resolution happens at layout — <c>GlyphSource.ResolveFor</c>). Styleable
    /// like any setter value, including via bindings (B15). <c>AffectsMeasure</c> semantics are
    /// the consumer's: text controls re-measure on change via the changed callback.
    /// </summary>
    public static readonly AttachedProperty<TextSizing> SizingProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextSizing>("Sizing", inherits: false);

    /// <summary>
    /// The glyph font for the element's text (proposal-glyph-runs Phase 3): a FIGlet (or other
    /// <see cref="Cursorial.Rendering.Fonts.IGlyphFont"/>) face the text renders through — words
    /// kern at the face's rules, word gaps stay rigid, editing is glyph-atomic. Combines with
    /// <see cref="SizingProperty"/>: a sizing that the terminal supports wins, with the font as
    /// its fallback face.
    /// </summary>
    public static readonly AttachedProperty<IGlyphFont?> FontProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, IGlyphFont?>("Font", inherits: false);

    /// <summary>Reads the glyph font attached to <paramref name="element"/>.</summary>
    public static IGlyphFont? GetFont(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(FontProperty);
    }

    /// <summary>Sets the glyph font on <paramref name="element"/> (inherits to its descendants).</summary>
    public static void SetFont(UIElement element, IGlyphFont? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FontProperty, value);
    }

    /// <summary>Reads the text sizing attached to <paramref name="element"/>.</summary>
    public static TextSizing GetSizing(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SizingProperty);
    }

    /// <summary>Sets the text sizing on <paramref name="element"/>.</summary>
    public static void SetSizing(UIElement element, TextSizing value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SizingProperty, value);
    }

    /// <summary>Reads the text wrapping mode attached to <paramref name="element"/>.</summary>
    public static WrapMode GetTextWrapping(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextWrappingProperty);
    }

    /// <summary>Sets the text wrapping mode on <paramref name="element"/>.</summary>
    public static void SetTextWrapping(UIElement element, WrapMode value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextWrappingProperty, value);
    }

    /// <summary>Reads the text trimming mode attached to <paramref name="element"/>.</summary>
    public static TextTrimming GetTextTrimming(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextTrimmingProperty);
    }

    /// <summary>Sets the text trimming mode on <paramref name="element"/>.</summary>
    public static void SetTextTrimming(UIElement element, TextTrimming value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextTrimmingProperty, value);
    }

    // ───────────────────────────── the per-axis attribute properties (proposal §1) ─────────────────────────────
    //
    // NON-inheriting by design (owner decision ③, 2026-07-13): the axes flow like Background, not
    // like Foreground — element-level values, per-axis TemplateBinding forwards on template parts,
    // ContentPresenter forwards onto the presentation leaves it GENERATES (never onto DataTemplate
    // content — app content is app-styleable). Reads are own-entry-or-default: no inheritance walk,
    // no reparent-diff participation. AffectsRender rides the global effects lane because the
    // properties attach to arbitrary host types (same rationale as the aggregate's registration).

    /// <summary>The text weight axis (SGR 1/2, shared reset 22). Non-inheriting; flows like <c>Background</c> (proposal §2.1).</summary>
    public static readonly AttachedProperty<TextWeight> TextWeightProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextWeight>("TextWeight");

    /// <summary>The text posture axis (SGR 3/23). Non-inheriting.</summary>
    public static readonly AttachedProperty<TextStyle> TextStyleProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextStyle>("TextStyle");

    /// <summary>
    /// Underline presence + shape unified (SGR 4 / 4:n / 24): <see langword="null"/> = no underline;
    /// a value = underlined in that shape (SGR encodes presence AS shape — <c>4:0</c> is off — so a
    /// shape-with-no-presence state is unrepresentable). Non-inheriting. The shape renders end-to-end
    /// through the widened formatted-text seam (owner decision ②).
    /// </summary>
    public static readonly AttachedProperty<UnderlineStyle?> UnderlineProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, UnderlineStyle?>("Underline");

    /// <summary>Underline brush (SGR 58/59). Non-inheriting.</summary>
    public static readonly AttachedProperty<IBrush?> UnderlineBrushProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, IBrush?>("UnderlineBrush");

    /// <summary>Strikethrough (SGR 9/29). Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> StrikethroughProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Strikethrough");

    /// <summary>Overline (SGR 53/55). Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> OverlineProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Overline");

    /// <summary>Reverse video (SGR 7/27) — the NoColor-tier interactive-cue axis. Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> InverseProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Inverse");

    /// <summary>Blink (SGR 5/25). Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> BlinkProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Blink");

    /// <summary>Conceal (SGR 8/28 — <see cref="TextAttributes.Hidden"/>; named for ANSI's "conceal"
    /// because <c>Visibility.Hidden</c> already means something else in this framework). Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> ConcealedProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Concealed");

    /// <summary>Reads the text weight axis attached to <paramref name="element"/>.</summary>
    public static TextWeight GetTextWeight(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextWeightProperty);
    }

    /// <summary>Sets the text weight axis on <paramref name="element"/>.</summary>
    public static void SetTextWeight(UIElement element, TextWeight value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextWeightProperty, value);
    }

    /// <summary>Reads the text posture axis attached to <paramref name="element"/>.</summary>
    public static TextStyle GetTextStyle(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextStyleProperty);
    }

    /// <summary>Sets the text posture axis on <paramref name="element"/>.</summary>
    public static void SetTextStyle(UIElement element, TextStyle value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextStyleProperty, value);
    }

    /// <summary>Reads the underline presence + shape attached to <paramref name="element"/> (<see langword="null"/> = none).</summary>
    public static UnderlineStyle? GetUnderline(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(UnderlineProperty);
    }

    /// <summary>Sets the underline presence + shape on <paramref name="element"/> (<see langword="null"/> = none).</summary>
    public static void SetUnderline(UIElement element, UnderlineStyle? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(UnderlineProperty, value);
    }

    /// <summary>Reads the underline brush attached property to <paramref name="element"/> (<see langword="null"/> = none).</summary>
    public static IBrush? GetUnderlineBrush(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(UnderlineBrushProperty);
    }

    /// <summary>Sets the underline brush attached property on <paramref name="element"/> (<see langword="null"/> = none).</summary>
    public static void SetUnderlineBrush(UIElement element, IBrush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(UnderlineBrushProperty, value);
    }

    /// <summary>Reads the strikethrough axis attached to <paramref name="element"/>.</summary>
    public static bool GetStrikethrough(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(StrikethroughProperty);
    }

    /// <summary>Sets the strikethrough axis on <paramref name="element"/>.</summary>
    public static void SetStrikethrough(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(StrikethroughProperty, value);
    }

    /// <summary>Reads the overline axis attached to <paramref name="element"/>.</summary>
    public static bool GetOverline(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(OverlineProperty);
    }

    /// <summary>Sets the overline axis on <paramref name="element"/>.</summary>
    public static void SetOverline(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(OverlineProperty, value);
    }

    /// <summary>Reads the reverse-video axis attached to <paramref name="element"/>.</summary>
    public static bool GetInverse(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(InverseProperty);
    }

    /// <summary>Sets the reverse-video axis on <paramref name="element"/>.</summary>
    public static void SetInverse(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(InverseProperty, value);
    }

    /// <summary>Reads the blink axis attached to <paramref name="element"/>.</summary>
    public static bool GetBlink(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(BlinkProperty);
    }

    /// <summary>Sets the blink axis on <paramref name="element"/>.</summary>
    public static void SetBlink(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BlinkProperty, value);
    }

    /// <summary>Reads the 'conceal' axis attached to <paramref name="element"/>.</summary>
    public static bool GetConcealed(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ConcealedProperty);
    }

    /// <summary>Sets the 'conceal' axis on <paramref name="element"/>.</summary>
    public static void SetConcealed(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ConcealedProperty, value);
    }

    /// <summary>
    /// The paint-time fold (proposal-TextAttributes-decomposition §3.1): resolves the element's
    /// effective text-attribute properties into the Drawing tier's vocabulary. The single
    /// composition point every text-bearing renderer reads — one call per <c>Render</c>; nine
    /// own-value reads (non-inheriting — no walks) plus, during the migration bridge, the legacy
    /// aggregate 'OR'd in (the bridge term is deleted at P5). <c>Concealed</c> maps to
    /// <see cref="TextAttributes.Hidden"/> (SGR 8 "conceal").
    /// </summary>
    public static ResolvedTextAttributes ComposeAttributes(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var flags = element.GetValue(TextWeightProperty) switch
        {
            TextWeight.Bold  => TextAttributes.Bold,
            TextWeight.Faint => TextAttributes.Faint,
            _                => TextAttributes.None,
        };

        // @formatter:off
        var underline = element.GetValue(UnderlineProperty);
        if (underline is not null)                                   flags |= TextAttributes.Underline;
        if (element.GetValue(TextStyleProperty) == TextStyle.Italic) flags |= TextAttributes.Italic;
        if (element.GetValue(StrikethroughProperty))                 flags |= TextAttributes.Strikethrough;
        if (element.GetValue(OverlineProperty))                      flags |= TextAttributes.Overline;
        if (element.GetValue(InverseProperty))                       flags |= TextAttributes.Inverse;
        if (element.GetValue(BlinkProperty))                         flags |= TextAttributes.Blink;
        if (element.GetValue(ConcealedProperty))                     flags |= TextAttributes.Hidden;
        // @formatter:on

        return new ResolvedTextAttributes(flags, underline ?? UnderlineStyle.Single);
    }

    /// <summary>
    /// The eight per-axis attribute properties, in fold order — the delivery/forwarding surface
    /// (proposal §2.1). The presenter forward (<c>ContentRealization</c>) binds all eight onto a
    /// GENERATED text leaf; a template forwards the subset its part renders.
    /// </summary>
    internal static readonly UIProperty[] AllAxisProperties =
    [
        // NOTE: IF YOU ADD TO THESE, UPDATE THE FORWARDING METHODS!
        TextWeightProperty, TextStyleProperty, UnderlineProperty, StrikethroughProperty,
        OverlineProperty, InverseProperty, BlinkProperty, ConcealedProperty, UnderlineBrushProperty
    ];

    /// <summary>
    /// The two text formatting properties: trimming and wrapping modes. The presenter forward
    /// (<c>ContentRealization</c>) binds both onto a text leaf from the PRESENTER--not its
    /// templated parent. These require more explicit intent than the <see cref="AllAxisProperties">
    /// axis properties</see>, by design.
    /// </summary>
    internal static readonly UIProperty[] AllFormattingProperties =
    [
        // NOTE: IF YOU ADD TO THESE, UPDATE THE FORWARDING METHODS!
        TextTrimmingProperty, TextWrappingProperty
    ];

    /// <summary>
    /// The two typography properties: sizing and font. The presenter forward (<c>ContentRealization</c>)
    /// binds both onto a text leaf from the PRESENTER--not its templated parent. These require more explicit
    /// intent than the <see cref="AllAxisProperties"> axis properties</see>, by design.
    /// </summary>
    internal static readonly UIProperty[] AllTypographyProperties =
    [
        // NOTE: IF YOU ADD TO THESE, UPDATE THE FORWARDING METHODS!
        FontProperty, SizingProperty
    ];

    /// <summary>
    /// Forwards every per-axis attribute from a template's templated parent onto a text-rendering
    /// <paramref name="part"/> (a caret/glyph/icon leaf), via <c>TemplateBinding</c> — so a
    /// control-level cue reaches the part at the Template lane (pierceable by conditional rules,
    /// PD26). Call INSIDE the control-template build. Faces honor only <see cref="InverseProperty"/>
    /// (the fill's one flag), so a face forwards Inverse alone via <see cref="ForwardInverse"/>.
    /// </summary>
    public static void ForwardAllAxes(UIObject part, UIObject? source = null, bool forwardInverse = true)
    {
        ArgumentNullException.ThrowIfNull(part);

        var relativeSource = source is null ? RelativeSource.TemplatedParent : null;

        #if DEBUG
        System.Diagnostics.Debug.Assert(AllAxisProperties.Length == 9,
                                        $"{nameof(AllAxisProperties)} was updated without updating ForwardAllAxes!");
        #endif

        part.SetBinding(
            TextWeightProperty,
            CompiledBinding.For(TextWeightProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            TextStyleProperty,
            CompiledBinding.For(TextStyleProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            UnderlineProperty,
            CompiledBinding.For(UnderlineProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            StrikethroughProperty,
            CompiledBinding.For(StrikethroughProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            OverlineProperty,
            CompiledBinding.For(OverlineProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            BlinkProperty,
            CompiledBinding.For(BlinkProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            ConcealedProperty,
            CompiledBinding.For(ConcealedProperty,
                                source: source,
                                relativeSource: relativeSource));

        part.SetBinding(
            UnderlineBrushProperty,
            CompiledBinding.For(UnderlineBrushProperty,
                                source: source,
                                relativeSource: relativeSource));

        if (forwardInverse)
        {
            part.SetBinding(
                InverseProperty,
                CompiledBinding.For(InverseProperty,
                                    source: source,
                                    relativeSource: relativeSource));
        }
    }

    /// <summary>
    /// Forwards every formatting property (trimming, wrapping) from a template's templated parent onto a
    /// text-rendering <paramref name="part"/> (a caret/glyph/icon leaf), via <c>TemplateBinding</c> — so
    /// a control-level cue reaches the part at the Template lane (pierceable by conditional rules, PD26).
    /// Call INSIDE the control-template build.
    /// </summary>
    public static void ForwardFormatting(UIObject part, UIObject? source = null)
    {
        ArgumentNullException.ThrowIfNull(part);

        var relativeSource = source is null ? RelativeSource.TemplatedParent : null;

#if DEBUG
        System.Diagnostics.Debug.Assert(AllFormattingProperties.Length == 2,
                                        $"{nameof(AllFormattingProperties)} was updated without updating ForwardFormatting!");
#endif
        
        part.SetBinding(
            TextTrimmingProperty,
            CompiledBinding.For(TextTrimmingProperty,
                                source: source,
                                relativeSource: relativeSource));
        part.SetBinding(
            TextWrappingProperty,
            CompiledBinding.For(TextWrappingProperty,
                                source: source,
                                relativeSource: relativeSource));

    }

    /// <summary>
    /// Forwards every typography property (sizing, font) from a template's templated parent onto a
    /// text-rendering <paramref name="part"/> (a caret/glyph/icon leaf), via <c>TemplateBinding</c> — so
    /// a control-level cue reaches the part at the Template lane (pierceable by conditional rules, PD26).
    /// Call INSIDE the control-template build.
    /// </summary>
    public static void ForwardTypography(UIObject part, UIObject? source = null)
    {
        ArgumentNullException.ThrowIfNull(part);

        ArgumentNullException.ThrowIfNull(part);

        var relativeSource = source is null ? RelativeSource.TemplatedParent : null;

#if DEBUG
        System.Diagnostics.Debug.Assert(AllTypographyProperties.Length == 2,
                                        $"{nameof(AllTypographyProperties)} was updated without updating ForwardTypography!");
#endif
        
        part.SetBinding(
            FontProperty,
            CompiledBinding.For(FontProperty,
                                source: source,
                                relativeSource: relativeSource));
        part.SetBinding(
            SizingProperty,
            CompiledBinding.For(SizingProperty,
                                source: source,
                                relativeSource: relativeSource));
    }

    /// <summary>Forwards <see cref="InverseProperty"/> alone from the templated parent onto a face part (the fill's one flag).</summary>
    public static BindingExpressionBase ForwardInverse(UIObject part, UIObject? source = null)
    {
        ArgumentNullException.ThrowIfNull(part);

        return part.SetBinding(
            InverseProperty,
            CompiledBinding.For(InverseProperty,
                                source: source,
                                relativeSource: source is null ? RelativeSource.TemplatedParent : null));
    }
}
