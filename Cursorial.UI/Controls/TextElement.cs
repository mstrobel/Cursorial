using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// The text weight axis (proposal-textattributes-decomposition §1). One axis, three values — Bold
/// and Faint share the terminal's SGR 22 reset, so they are alternatives on a single dial, not
/// independent flags: mutual exclusion by construction, and a weight conflict ("disabled says
/// Faint, heading says Bold") arbitrates deterministically through the lattice like any
/// single-valued property. The axis of WPF's <c>FontWeight</c> / CSS <c>font-weight</c> — not the
/// type (no font-object model, no 100–900 numeric weights; the deviated name signals the deviated
/// domain, the design doc's "no font types" pin refined).
/// </summary>
public enum TextWeight : byte
{
    /// <summary>No weight attribute (neither SGR 1 nor 2; the shared reset 22 state).</summary>
    Normal = 0,

    /// <summary>SGR 2 — faint / dim.</summary>
    Faint,

    /// <summary>SGR 1 — bold / increased intensity.</summary>
    Bold,
}

/// <summary>
/// The text posture axis (proposal-textattributes-decomposition §1, amended 2026-07-13): the enum
/// shape (rather than a bare bool) keeps the <c>Text*</c> property family discoverable as a set
/// (<see cref="TextWeight"/>/<see cref="TextStyle"/>) and leaves headroom for future terminal
/// posture standards (SGR 20 fraktur is the historical precedent) — while still refusing WPF's
/// <c>Oblique</c>, which has no terminal encoding.
/// </summary>
public enum TextStyle : byte
{
    /// <summary>Upright text (SGR 23 — the reset state).</summary>
    Normal = 0,

    /// <summary>SGR 3 — italic.</summary>
    Italic,
}

/// <summary>
/// The paint-time resolution of the <see cref="TextElement"/> attribute properties into the Drawing
/// tier's vocabulary (proposal-textattributes-decomposition §3.1): the folded flag bitset (including
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
/// (proposal-textattributes-decomposition §2.1). Renderers read the folded effective attributes via
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
        // re-render only their own element — proposal-textattributes-decomposition §2.1.)
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ForegroundProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextWeightProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextStyleProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, UnderlineProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, StrikethroughProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, OverlineProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, InverseProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, BlinkProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ConcealedProperty);
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

    /// <summary>Reads the conceal axis attached to <paramref name="element"/>.</summary>
    public static bool GetConcealed(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ConcealedProperty);
    }

    /// <summary>Sets the conceal axis on <paramref name="element"/>.</summary>
    public static void SetConcealed(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ConcealedProperty, value);
    }

    /// <summary>
    /// The paint-time fold (proposal-textattributes-decomposition §3.1): resolves the element's
    /// effective text-attribute properties into the Drawing tier's vocabulary. The single
    /// composition point every text-bearing renderer reads — one call per <c>Render</c>; nine
    /// own-value reads (non-inheriting — no walks) plus, during the migration bridge, the legacy
    /// aggregate OR'd in (the bridge term is deleted at P5). <c>Concealed</c> maps to
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

        var underline = element.GetValue(UnderlineProperty);
        if (underline is not null)                        flags |= TextAttributes.Underline;
        if (element.GetValue(TextStyleProperty) == TextStyle.Italic) flags |= TextAttributes.Italic;
        if (element.GetValue(StrikethroughProperty))      flags |= TextAttributes.Strikethrough;
        if (element.GetValue(OverlineProperty))           flags |= TextAttributes.Overline;
        if (element.GetValue(InverseProperty))            flags |= TextAttributes.Inverse;
        if (element.GetValue(BlinkProperty))              flags |= TextAttributes.Blink;
        if (element.GetValue(ConcealedProperty))          flags |= TextAttributes.Hidden;

        return new ResolvedTextAttributes(flags, underline ?? UnderlineStyle.Single);
    }

    /// <summary>
    /// The eight per-axis attribute properties, in fold order — the delivery/forwarding surface
    /// (proposal §2.1). The presenter forward (<c>ContentRealization</c>) binds all eight onto a
    /// GENERATED text leaf; a template forwards the subset its part renders.
    /// </summary>
    internal static readonly UIProperty[] AllAxisProperties =
    [
        TextWeightProperty, TextStyleProperty, UnderlineProperty, StrikethroughProperty,
        OverlineProperty, InverseProperty, BlinkProperty, ConcealedProperty,
    ];

    /// <summary>
    /// Forwards every per-axis attribute from a template's templated parent onto a text-rendering
    /// <paramref name="part"/> (a caret/glyph/icon leaf), via <c>TemplateBinding</c> — so a
    /// control-level cue reaches the part at the Template lane (pierceable by conditional rules,
    /// PD26). Call INSIDE the control-template build. Faces honor only <see cref="InverseProperty"/>
    /// (the fill's one flag), so a face forwards Inverse alone via <see cref="ForwardInverse"/>.
    /// </summary>
    public static void ForwardAllAxes(UIElement part)
    {
        ArgumentNullException.ThrowIfNull(part);
        foreach (var axis in AllAxisProperties)
            part.SetBinding(axis, new TemplateBinding(axis));
    }

    /// <summary>Forwards <see cref="InverseProperty"/> alone from the templated parent onto a face part (the fill's one flag).</summary>
    public static void ForwardInverse(UIElement facePart)
    {
        ArgumentNullException.ThrowIfNull(facePart);
        facePart.SetBinding(InverseProperty, new TemplateBinding(InverseProperty));
    }
}
