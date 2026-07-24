using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A collapsible header/content region (the WPF/Avalonia <c>Expander</c>): a <see cref="HeaderedContentControl"/>
/// whose <see cref="HeaderedContentControl.Header"/> rides a clickable header (with a <c>▸</c>/<c>▾</c> twisty) and whose
/// <see cref="ContentControl.Content"/> shows only while <see cref="IsExpanded"/> (gated via the <c>PART_Content</c>
/// visibility; <c>:expanded</c>). Clicking the header — or Space/Enter while the expander has focus — toggles;
/// <see cref="Expanding"/>/<see cref="Collapsing"/> bubble first (a handler may veto via
/// <see cref="CancelRoutedEventArgs.Cancel"/>), then <see cref="Expanded"/>/<see cref="Collapsed"/> bubble
/// once the transition commits. (v1 expands downward.)
/// </summary>
[TemplatePart(PartHeader, typeof(ToggleButton))]
[TemplatePart(PartGlyph, typeof(TextBlock))]
[TemplatePart(PartContent, typeof(UIElement))]
public class Expander : HeaderedContentControl
{
    internal const string PartHeader = "PART_Header";
    internal const string PartGlyph = "PART_Glyph";
    internal const string PartContent = "PART_Content";

    internal const string ExpandedGlyph = "⏷";
    internal const string CollapsedGlyph = "⏵";

    private TextBlock? _glyph;
    private ToggleButton? _header;
    private UIElement? _content;

    /// <summary>Whether the content is shown (<c>:expanded</c>; gates the <c>PART_Content</c> visibility + twisty).</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        UIProperty.Register<Expander, bool>(nameof(IsExpanded), defaultValue: false, coerce: CoerceIsExpanded, changed: OnIsExpandedChanged);

    /// <summary>Bubbles <em>before</em> the expander opens; a handler may set <see cref="CancelRoutedEventArgs.Cancel"/> to veto (Avalonia — no WPF analogue).</summary>
    public static readonly RoutedEvent<CancelRoutedEventArgs> ExpandingEvent =
        RoutedEvent<CancelRoutedEventArgs>.Register(nameof(Expanding), RoutingStrategy.Bubble, typeof(Expander));

    /// <summary>Bubbles <em>before</em> the expander closes; a handler may set <see cref="CancelRoutedEventArgs.Cancel"/> to veto (Avalonia — no WPF analogue).</summary>
    public static readonly RoutedEvent<CancelRoutedEventArgs> CollapsingEvent =
        RoutedEvent<CancelRoutedEventArgs>.Register(nameof(Collapsing), RoutingStrategy.Bubble, typeof(Expander));

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
        BindsTwoWayByDefault<Expander>(IsExpandedProperty);
    }

    /// <inheritdoc cref="IsExpandedProperty"/>
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>CLR sugar over <see cref="ExpandingEvent"/>.</summary>
    public event EventHandler<CancelRoutedEventArgs>? Expanding { add => AddHandler(ExpandingEvent, value!); remove => RemoveHandler(ExpandingEvent, value!); }

    /// <summary>CLR sugar over <see cref="CollapsingEvent"/>.</summary>
    public event EventHandler<CancelRoutedEventArgs>? Collapsing { add => AddHandler(CollapsingEvent, value!); remove => RemoveHandler(CollapsingEvent, value!); }

    /// <summary>CLR sugar over <see cref="ExpandedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Expanded { add => AddHandler(ExpandedEvent, value!); remove => RemoveHandler(ExpandedEvent, value!); }

    /// <summary>CLR sugar over <see cref="CollapsedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Collapsed { add => AddHandler(CollapsedEvent, value!); remove => RemoveHandler(CollapsedEvent, value!); }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _header = GetTemplatePart<ToggleButton>(PartHeader);
        _glyph = GetTemplatePart<TextBlock>(PartGlyph);
        _content = GetTemplatePart<UIElement>(PartContent);

        _header?.Focusable = false;
        _header?.IsTabStop = false;

        UpdateExpansionVisuals();
        UpdateHeaderFocusVisuals();
    }

    private void UpdateHeaderFocusVisuals()
    {
        _header?.SetInteractionStateInternal(InteractionState.Focused, IsFocused);
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
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || !IsFocused || e.Modifiers != KeyModifiers.None)
            return;

        // Enter or the spacebar (the modifier-free character form, ND10) toggles.
        if (e.Key == Key.Enter || ButtonBase.IsActivationSpace(e))
        {
            Toggle();
            e.Handled = true;
        }
    }

    private void Toggle() => IsExpanded = !IsExpanded;

    // Pre-commit veto (Avalonia parity): Expanding/Collapsing must raise BEFORE IsExpanded flips, and a
    // handler that sets e.Cancel abandons the transition. The post-change OnIsExpandedChanged callback is too
    // late to cancel, so the veto rides coercion — the single choke point EVERY write path funnels through
    // (keyboard Toggle, the header's two-way IsChecked→IsExpanded binding, a direct set, XAML) — giving the
    // veto uniformly. A cancelling handler returns the current value (!baseValue for the toggle), which the
    // store's equality gate treats as no change, so Expanded/Collapsed never fire. The metadata default
    // (false) is never coerced (PD8), so loading a collapsed expander raises nothing.
    private static bool CoerceIsExpanded(UIObject sender, bool baseValue)
    {
        // Fire only on a real transition: during coercion the property still holds the OLD value, so
        // baseValue == IsExpanded means a redundant set (no pending change) — nothing to raise or veto.
        if (sender is not Expander expander || baseValue == expander.IsExpanded)
            return baseValue;

        var args = new CancelRoutedEventArgs(baseValue ? ExpandingEvent : CollapsingEvent, expander);
        expander.RaiseEvent(args);

        return args.Cancel ? !baseValue : baseValue;
    }

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
            _glyph.Text = IsExpanded ? "⏷" : "⏵"; // design-guide carets (U+23F7 / U+23F5)
        if (_content is not null)
            _content.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateHeaderFocusVisuals();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdateHeaderFocusVisuals();
    }
}
