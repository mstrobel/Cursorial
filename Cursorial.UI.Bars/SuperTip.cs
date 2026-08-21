using Cursorial.UI.Controls;
using Cursorial.UI.Data;

namespace Cursorial.UI.Bars;

/// <summary>
/// A rich hover tip (the bars guide's <b>SuperTip</b>): a titled, multi-line tip carrying the command name
/// (<see cref="Title"/>), its accelerator (<see cref="InputGestureText"/>), a <see cref="Description"/> body, and an
/// optional <see cref="Footer"/> — richer than a one-line tooltip. It is ordinary <c>ToolTipService.Tip</c> content
/// (the tip may be any object; the shared <c>ToolTip</c> hosts it), so a SuperTip shows through the SAME hover
/// controller as a plain tip — no parallel machinery. A <see cref="BarCommand"/> with a <see cref="BarCommand.Description"/>
/// auto-provisions one on every bound bar control (<see cref="FromCommand"/>), so the rich help is identical wherever
/// the command appears ("define once, bind everywhere").
/// </summary>
[TemplatePart(PartKeyTips, typeof(TextBlock))]
public class SuperTip : Control
{
    private const string PartKeyTips = "PART_KeyTips";

    /// <summary>The tip title (typically the command name).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.RegisterAttached<SuperTip, UIElement, string?>(nameof(Title));

    /// <summary>The accelerator hint shown beside the title (display-only, e.g. "Ctrl+S").</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.RegisterAttached<SuperTip, UIElement, string?>(nameof(InputGestureText));

    /// <summary>The multi-line body (a string or any content).</summary>
    public static readonly StyledProperty<object?> DescriptionProperty =
        UIProperty.RegisterAttached<SuperTip, UIElement, object?>(nameof(Description));

    /// <summary>An optional footer (e.g. "Press F1 for more help") — collapsed when null.</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        UIProperty.RegisterAttached<SuperTip, UIElement, object?>(nameof(Footer));

    /// <summary>
    /// The control this tip describes — the anchor the auto-computed <see cref="KeyTipSequence"/> walks from
    /// to find its ribbon tab/group. Set by the bar control when it provisions the tip.
    /// </summary>
    public static readonly DirectProperty<SuperTip, UIElement?> AnchorProperty =
        UIProperty.RegisterDirect<SuperTip, UIElement?>(nameof(Anchor), o => o.Anchor);

    /// <summary>
    /// Whether the SuperTip should automatically wire up bindings for its properties against
    /// its <see cref="Anchor">anchor element</see>
    /// </summary>
    public static readonly StyledProperty<bool> PopulateFromAnchorProperty =
        UIProperty.Register<SuperTip, bool>(
            nameof(PopulateFromAnchor),
            defaultValue: false,
            changed: (s, oldValue, _) => (s as SuperTip)?.UpdateAnchorBindings(oldValue));

    /// <summary>
    /// Whether the SuperTip should present its contents in a compact layout with minimal or no margins
    /// between its elements.
    /// </summary>
    public static readonly StyledProperty<bool> UseCompactLayoutProperty =
        UIProperty.Register<SuperTip, bool>(nameof(UseCompactLayout), defaultValue: false);

    /// <summary>The KeyTip accelerator hops to reach the command (e.g. <c>"Alt, H, F, B"</c>), shown in amber under
    /// the header. Auto-computed each time the tip is shown from <see cref="Anchor"/> via
    /// <see cref="KeyTip.GetHopSequence"/> (an anchored tip); an author-created tip (no <see cref="Anchor"/>) keeps
    /// its explicitly-set value. Collapsed when null.</summary>
    public static readonly StyledProperty<string?> KeyTipSequenceProperty =
        UIProperty.RegisterAttached<SuperTip, UIElement, string?>(
            nameof(KeyTipSequence),
            changed: static (o, _, _) => (o as SuperTip)?.UpdateHopsVisibility());

    /// <summary>Attached property getter for <see cref="TitleProperty"/>.</summary>
    public static string? GetTitle(UIElement element) => element.GetValue(TitleProperty);

    /// <summary>Attached property setter for <see cref="TitleProperty"/>.</summary>
    public static void SetTitle(UIElement element, string? value) => element.SetValue(TitleProperty, value);

    /// <summary>Attached property getter for <see cref="InputGestureTextProperty"/>.</summary>
    public static string? GetInputGestureText(UIElement element) => element.GetValue(InputGestureTextProperty);

    /// <summary>Attached property setter for <see cref="InputGestureTextProperty"/>.</summary>
    public static void SetInputGestureText(UIElement element, string? value) => element.SetValue(InputGestureTextProperty, value);

    /// <summary>Attached property getter for <see cref="DescriptionProperty"/>.</summary>
    public static object? GetDescription(UIElement element) => element.GetValue(DescriptionProperty);

    /// <summary>Attached property setter for <see cref="DescriptionProperty"/>.</summary>
    public static void SetDescription(UIElement element, object? value) => element.SetValue(DescriptionProperty, value);

    /// <summary>Attached property getter for <see cref="FooterProperty"/>.</summary>
    public static object? GetFooter(UIElement element) => element.GetValue(FooterProperty);

    /// <summary>Attached property setter for <see cref="FooterProperty"/>.</summary>
    public static void SetFooter(UIElement element, object? value) => element.SetValue(FooterProperty, value);

    private TextBlock? _hops;

    /// <inheritdoc cref="AnchorProperty"/>
    public UIElement? Anchor
    {
        get;
        set
        {
            var oldValue = field;
            if (ReferenceEquals(oldValue, value)) return;
            field = value;
            OnAnchorChanged(oldValue, value);
        }
    }

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc cref="DescriptionProperty"/>
    public object? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <inheritdoc cref="FooterProperty"/>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <inheritdoc cref="KeyTipSequenceProperty"/>
    public string? KeyTipSequence { get => GetValue(KeyTipSequenceProperty); set => SetValue(KeyTipSequenceProperty, value); }

    /// <inheritdoc cref="PopulateFromAnchorProperty"/>
    public bool PopulateFromAnchor { get => GetValue(PopulateFromAnchorProperty); set => SetValue(PopulateFromAnchorProperty, value); }

    /// <inheritdoc cref="UseCompactLayoutProperty"/>
    public bool UseCompactLayout { get => GetValue(UseCompactLayoutProperty); set => SetValue(UseCompactLayoutProperty, value); }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        // Recompute the hop sequence EACH time the tip is shown (it attaches per show; OnApplyTemplate would run only
        // once per instance). An anchored tip re-derives from its control's live ribbon position; an author-created
        // tip (no anchor) keeps whatever KeyTipSequence was set explicitly.
        if (Anchor is {} anchor)
            SetCurrentValue(KeyTipSequenceProperty, KeyTip.GetHopSequence(anchor));
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _hops = GetTemplatePart<TextBlock>(PartKeyTips);
        UpdateHopsVisibility();
    }

    // Collapse the amber hops line when there is no sequence (KeyTips off / not reachable); shown otherwise.
    private void UpdateHopsVisibility()
    {
        if (_hops is {} hops)
            hops.Visibility = string.IsNullOrEmpty(KeyTipSequence) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Builds a SuperTip from a command's display metadata (title = Text, shortcut = InputGestureText,
    /// body = Description) — the "bound to the command" hover help.</summary>
    public static SuperTip FromCommand(BarCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new SuperTip
        {
            Title = command.Text is {} text ? AccessText.Parse(text).Text : null, // strip the access-key underscore
            InputGestureText = command.InputGestureText,
            Description = command.Description,
        };
    }
    
    private void OnAnchorChanged(UIElement? oldAnchor, UIElement? newAnchor)
    {
        DispatchPropertyChanged(AnchorProperty, null, oldAnchor, newAnchor, BindingPriority.LocalValue);
        UpdateAnchorBindings(PopulateFromAnchor);
    }

    private void UpdateAnchorBindings(bool wasWired)
    {
        if (wasWired)
        {
            ClearValue(TitleProperty);
            ClearValue(InputGestureTextProperty);
            ClearValue(DescriptionProperty);
            ClearValue(FooterProperty);
            ClearValue(KeyTipSequenceProperty);
        }

        if (PopulateFromAnchor && Anchor is {} anchor)
        {
            this.SetBinding(TitleProperty, CompiledBinding.For(TitleProperty, source: anchor));
            this.SetBinding(InputGestureTextProperty, CompiledBinding.For(InputGestureTextProperty, source: anchor));
            this.SetBinding(DescriptionProperty, CompiledBinding.For(DescriptionProperty, source: anchor));
            this.SetBinding(FooterProperty, CompiledBinding.For(FooterProperty, source: anchor));
            this.SetBinding(KeyTipSequenceProperty, CompiledBinding.For(KeyTipSequenceProperty, source: anchor));
        }
    }

    public static void SetAutoPopulatedTip(UIElement? anchor, bool useCompactLayout = false)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        SetTipCore(anchor, new SuperTip { PopulateFromAnchor = true, UseCompactLayout = useCompactLayout });
    } 

    public static void SetTip(UIElement? anchor, SuperTip tip)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(tip);
        
        SetTipCore(anchor, tip);
    }

    private static void SetTipCore(UIElement anchor, SuperTip tip)
    {
        tip.Anchor = anchor;
        ToolTipService.SetTip(anchor, tip);
    }
}
