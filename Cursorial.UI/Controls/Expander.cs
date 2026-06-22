using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A collapsible header/content region (the WPF/Avalonia <c>Expander</c>): a <see cref="HeaderedContentControl"/>
/// whose <see cref="Header"/> rides a clickable header (with a <c>&gt;</c>/<c>v</c> twisty) and whose
/// <see cref="ContentControl.Content"/> shows only while <see cref="IsExpanded"/> (gated via the <c>PART_Content</c>
/// visibility; <c>:expanded</c>). Clicking the header — or Space/Enter while the expander has focus — toggles;
/// <see cref="Expanded"/>/<see cref="Collapsed"/> bubble on the transition. (v1 expands downward.)
/// </summary>
public class Expander : HeaderedContentControl
{
    private const string PartHeader = "PART_Header";
    private const string PartGlyph = "PART_Glyph";
    private const string PartContent = "PART_Content";

    private UIElement? _header;
    private TextBlock? _glyph;
    private UIElement? _content;

    /// <summary>Whether the content is shown (<c>:expanded</c>; gates the <c>PART_Content</c> visibility + twisty).</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        UIProperty.Register<Expander, bool>(nameof(IsExpanded), defaultValue: false, changed: OnIsExpandedChanged);

    /// <summary>Bubbles when the expander opens.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ExpandedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Expanded), RoutingStrategy.Bubble, typeof(Expander));

    /// <summary>Bubbles when the expander closes.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CollapsedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Collapsed), RoutingStrategy.Bubble, typeof(Expander));

    static Expander()
    {
        FocusableProperty.OverrideDefaultValue<Expander>(true); // the header takes keyboard focus to toggle
        PseudoClassMapping.Register<Expander>(IsExpandedProperty, ":expanded");
    }

    /// <inheritdoc cref="IsExpandedProperty"/>
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>CLR sugar over <see cref="ExpandedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Expanded { add => AddHandler(ExpandedEvent, value!); remove => RemoveHandler(ExpandedEvent, value!); }

    /// <summary>CLR sugar over <see cref="CollapsedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Collapsed { add => AddHandler(CollapsedEvent, value!); remove => RemoveHandler(CollapsedEvent, value!); }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _header = GetTemplatePart<UIElement>(PartHeader);
        _glyph = GetTemplatePart<TextBlock>(PartGlyph);
        _content = GetTemplatePart<UIElement>(PartContent);
        UpdateExpansionVisuals();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _header = null;
        _glyph = null;
        _content = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // Only a click on the header toggles (a click in the content does not).
        if (IsWithin(e.OriginalSource, _header))
        {
            Focus();
            Toggle();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsFocused || e.Modifiers != KeyModifiers.None)
            return;

        // Enter or the spacebar (the modifier-free character form, ND10) toggles.
        if (e.Key == Key.Enter || (e.Key == Key.Character && e.Text.Length == 1 && e.Text.Span[0] == ' '))
        {
            Toggle();
            e.Handled = true;
        }
    }

    private void Toggle() => IsExpanded = !IsExpanded;

    private static void OnIsExpandedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        var expander = (Expander)sender;
        expander.UpdateExpansionVisuals();
        expander.RaiseEvent(new RoutedEventArgs(newValue ? ExpandedEvent : CollapsedEvent, expander));
    }

    // The twisty glyph (>/v) + the content visibility, kept in one place. The content is Collapsed when closed so a
    // collapsed expander takes only its header's space (ASCII-safe glyphs — the ambiguous-width memory).
    private void UpdateExpansionVisuals()
    {
        if (_glyph is not null)
            _glyph.Text = IsExpanded ? "v" : ">";
        if (_content is not null)
            _content.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsWithin(UIElement? node, UIElement? ancestor)
    {
        if (ancestor is null)
            return false;
        for (; node is not null; node = node.VisualParent)
            if (ReferenceEquals(node, ancestor))
                return true;
        return false;
    }
}
