using Cursorial.Drawing.Media;
using Cursorial.Output;

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
/// The inherited text-attribute spine (design doc §12.1): attached properties <c>AddOwner</c>'d onto
/// <see cref="Control"/> and <see cref="TextBlock"/> so foreground brush and text attributes flow
/// down the logical tree. Setting <see cref="ForegroundProperty"/> high in the tree colors every
/// descendant text element that does not override it.
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

    /// <summary>The inherited text attributes (bold/italic/underline/…) (<c>Inherits | AffectsRender</c>).</summary>
    public static readonly AttachedProperty<TextAttributes> TextAttributesProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, TextAttributes>("TextAttributes", inherits: true);

    static TextElement()
    {
        // Attached properties land on arbitrary host types, so AffectsRender rides the global
        // effects lane (A1) — not a per-owner-type registration. (Foreground/TextAttributes
        // additionally fan out to descendants via inheritance; the per-axis properties are
        // non-inheriting and re-render only their own element.)
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ForegroundProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextAttributesProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextWeightProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ItalicProperty);
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

    /// <summary>Reads the inherited text attributes attached to <paramref name="element"/>.</summary>
    public static TextAttributes GetTextAttributes(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TextAttributesProperty);
    }

    /// <summary>Sets the text attributes on <paramref name="element"/> (inherits to its descendants).</summary>
    public static void SetTextAttributes(UIElement element, TextAttributes value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextAttributesProperty, value);
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

    /// <summary>Italic (SGR 3/23). Non-inheriting.</summary>
    public static readonly AttachedProperty<bool> ItalicProperty =
        UIProperty.RegisterAttached<TextElement, UIElement, bool>("Italic");

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

    /// <summary>Reads the italic axis attached to <paramref name="element"/>.</summary>
    public static bool GetItalic(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ItalicProperty);
    }

    /// <summary>Sets the italic axis on <paramref name="element"/>.</summary>
    public static void SetItalic(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ItalicProperty, value);
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
        if (element.GetValue(ItalicProperty))             flags |= TextAttributes.Italic;
        if (element.GetValue(StrikethroughProperty))      flags |= TextAttributes.Strikethrough;
        if (element.GetValue(OverlineProperty))           flags |= TextAttributes.Overline;
        if (element.GetValue(InverseProperty))            flags |= TextAttributes.Inverse;
        if (element.GetValue(BlinkProperty))              flags |= TextAttributes.Blink;
        if (element.GetValue(ConcealedProperty))          flags |= TextAttributes.Hidden;

        // The migration bridge (proposal §4.1): unmigrated aggregate producers keep working
        // bit-identically until P5 deletes the property and this term with it.
        flags |= element.GetValue(TextAttributesProperty);

        return new ResolvedTextAttributes(flags, underline ?? UnderlineStyle.Single);
    }
}
