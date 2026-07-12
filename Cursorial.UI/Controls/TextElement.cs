using Cursorial.Drawing.Media;
using Cursorial.Output;

namespace Cursorial.UI.Controls;

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
        // Inherited attached properties fan out to arbitrary descendants, so AffectsRender rides the
        // global effects lane (A1) — not a per-owner-type registration.
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, ForegroundProperty);
        UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, TextAttributesProperty);
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
}
