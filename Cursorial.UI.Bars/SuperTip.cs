using Cursorial.UI.Controls;

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
public class SuperTip : Control
{
    /// <summary>The tip title (typically the command name).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.Register<SuperTip, string?>(nameof(Title));

    /// <summary>The accelerator hint shown beside the title (display-only, e.g. "Ctrl+S").</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<SuperTip, string?>(nameof(InputGestureText));

    /// <summary>The multi-line body (a string or any content).</summary>
    public static readonly StyledProperty<object?> DescriptionProperty =
        UIProperty.Register<SuperTip, object?>(nameof(Description));

    /// <summary>An optional footer (e.g. "Press F1 for more help") — collapsed when null.</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        UIProperty.Register<SuperTip, object?>(nameof(Footer));

    /// <summary>The KeyTip accelerator hops to reach the command (e.g. <c>"Alt, H, F, B"</c>), shown in amber under
    /// the header. Auto-computed each time the tip is shown from <see cref="Anchor"/> via
    /// <see cref="KeyTip.GetHopSequence"/> (an anchored tip); an author-created tip (no <see cref="Anchor"/>) keeps
    /// its explicitly-set value. Collapsed when null.</summary>
    public static readonly StyledProperty<string?> KeyTipSequenceProperty =
        UIProperty.Register<SuperTip, string?>(nameof(KeyTipSequence), changed: static (o, _, _) => (o as SuperTip)?.UpdateHopsVisibility());

    private const string PartKeyTips = "PART_KeyTips";
    private TextBlock? _hops;

    static SuperTip()
    {
        Control.ThemeProperty.OverrideDefaultValue<SuperTip>(CursorialBarsTheme.SuperTipStyle());
    }

    /// <summary>The control this tip describes — the anchor the auto-computed <see cref="KeyTipSequence"/> walks from
    /// to find its ribbon tab/group. Set by the bar control when it provisions the tip.</summary>
    internal UIElement? Anchor { get; set; }

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

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        // Recompute the hop sequence EACH time the tip is shown (it attaches per show; OnApplyTemplate would run only
        // once per instance). An anchored tip re-derives from its control's live ribbon position; an author-created
        // tip (no anchor) keeps whatever KeyTipSequence was set explicitly.
        if (Anchor is { } anchor)
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
        if (_hops is { } hops)
            hops.Visibility = string.IsNullOrEmpty(KeyTipSequence) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Builds a SuperTip from a command's display metadata (title = Text, shortcut = InputGestureText,
    /// body = Description) — the "bound to the command" hover help.</summary>
    public static SuperTip FromCommand(BarCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new SuperTip
        {
            Title = command.Text is { } text ? AccessText.Parse(text).Text : null, // strip the access-key underscore
            InputGestureText = command.InputGestureText,
            Description = command.Description,
        };
    }
}
