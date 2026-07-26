using Cursorial.Input;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Controls;

/// <summary>
/// The container for one <see cref="CompletionEntry"/> in a <see cref="CompletionList"/> — the row of
/// the completion popup: <c>[icon] display-with-the-match-bolded … description  kind</c>.
/// <para>
/// It is a <see cref="ListBoxItem"/> with two deliberate departures. First it is <b>not focusable</b>
/// and not a tab stop: the completion popup is a hint overlay, keyboard focus never leaves the text
/// field it decorates, and a row that could take focus would break that the moment the user clicked
/// one. (<see cref="ListBoxItem.OnMouseDown"/> still calls <c>Focus()</c>; with
/// <see cref="UIElement.Focusable"/> false that call is a no-op, which is exactly the behaviour we
/// want and why it does not need suppressing.) Second, a left press <em>accepts</em> the row rather
/// than merely selecting it — a completion list has no notion of a highlighted-but-uncommitted pick
/// the way a <see cref="ListBox"/> does.
/// </para>
/// <para>
/// The four columns are separate parts rather than one composed string so a host can restyle any of
/// them (dim the kind label, colour the icon, drop the description) without re-authoring the row, and
/// each collapses to zero width when its value is absent — an icon-less candidate costs no icon
/// column. There is no WPF or Avalonia counterpart; the row layout follows the completion lists users
/// know from VS Code and IntelliJ.
/// </para>
/// </summary>
[TemplatePart(PartIcon, typeof(ContentPresenter))]
[TemplatePart(PartDisplay, typeof(TextBlock), IsRequired = true)]
[TemplatePart(PartDescription, typeof(TextBlock))]
[TemplatePart(PartKindLabel, typeof(TextBlock))]
public class CompletionListItem : ListBoxItem
{
    private const string PartIcon = "PART_Icon";
    private const string PartDisplay = "PART_Display";
    private const string PartDescription = "PART_Description";
    private const string PartKindLabel = "PART_KindLabel";

    /// <summary>The row's display text as BBCode with the matched runs bolded (<see cref="CompletionEntry.DisplayMarkup"/>).</summary>
    public static readonly StyledProperty<string?> DisplayMarkupProperty =
        UIProperty.Register<CompletionListItem, string?>(nameof(DisplayMarkup), changed: OnTextColumnChanged);

    /// <summary>The row's icon, or <see langword="null"/> to collapse the icon column.</summary>
    public static readonly StyledProperty<IconCarrier?> IconProperty =
        UIProperty.Register<CompletionListItem, IconCarrier?>(nameof(Icon), changed: OnIconChanged);

    /// <summary>The right-aligned category label (<c>folder</c>, <c>file</c>, <c>folder · fuzzy</c>), or <see langword="null"/> to collapse it.</summary>
    public static readonly StyledProperty<string?> KindLabelProperty =
        UIProperty.Register<CompletionListItem, string?>(nameof(KindLabel), changed: OnTextColumnChanged);

    /// <summary>Optional secondary text shown between the display text and the kind label, or <see langword="null"/> to collapse it.</summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        UIProperty.Register<CompletionListItem, string?>(nameof(Description), changed: OnTextColumnChanged);

    private ContentPresenter? _icon;
    private TextBlock? _display;
    private TextBlock? _description;
    private TextBlock? _kindLabel;

    static CompletionListItem()
    {
        // The row is a data readout, not a control: none of the four columns changes the row's
        // structure, but all of them change its desired width, so they all measure.
        AffectsMeasure<CompletionListItem>(DisplayMarkupProperty, IconProperty, KindLabelProperty, DescriptionProperty);
    }

    /// <summary>Creates a completion row (never focusable — focus stays in the completed text field).</summary>
    public CompletionListItem()
    {
        Focusable = false;
        IsTabStop = false;
    }

    /// <inheritdoc cref="DisplayMarkupProperty"/>
    public string? DisplayMarkup { get => GetValue(DisplayMarkupProperty); set => SetValue(DisplayMarkupProperty, value); }

    /// <inheritdoc cref="IconProperty"/>
    public IconCarrier? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="KindLabelProperty"/>
    public string? KindLabel { get => GetValue(KindLabelProperty); set => SetValue(KindLabelProperty, value); }

    /// <inheritdoc cref="DescriptionProperty"/>
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>The entry this row presents, or <see langword="null"/> before the container is prepared.</summary>
    public CompletionEntry? Entry => Content as CompletionEntry;

    // Test/inspection seams (template-private parts).
    internal TextBlock? DisplayPart => _display;
    internal TextBlock? KindLabelPart => _kindLabel;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _icon = GetTemplatePart<ContentPresenter>(PartIcon);
        _display = GetTemplatePart<TextBlock>(PartDisplay);
        _description = GetTemplatePart<TextBlock>(PartDescription);
        _kindLabel = GetTemplatePart<TextBlock>(PartKindLabel);

        UpdateColumns(); // push the current state into freshly-realized parts
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _icon = null;
        _display = null;
        _description = null;
        _kindLabel = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); // selects through the owning selector and marks the event handled

        if (e.Button != MouseButton.Left)
            return;

        // A press on a completion row IS the accept (there is no separate commit gesture in a hint
        // overlay). The walk to the owner runs through the LOGICAL parent chain — generated containers
        // are logical children of the list — because the popup's template makes the list a VISUAL part,
        // so a visual walk would stop at the popup surface root.
        for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
        {
            if (node is CompletionList { Owner: { } owner })
            {
                owner.Accept(Entry, CompletionAcceptReason.Pointer);
                return;
            }
        }
    }

    private static void OnTextColumnChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as CompletionListItem)?.UpdateColumns();

    private static void OnIconChanged(UIObject sender, IconCarrier? oldValue, IconCarrier? newValue)
        => (sender as CompletionListItem)?.UpdateColumns();

    // Each column's part carries its own leading/trailing margin in the template, so it has to
    // COLLAPSE (not merely blank) when its value is absent — a Hidden part still spends its margin
    // and would indent every icon-less row by the icon column's width.
    private void UpdateColumns()
    {
        if (_icon is not null)
        {
            var icon = Icon;
            _icon.Content = icon;
            _icon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_display is not null)
            _display.Markup = DisplayMarkup ?? "";

        if (_description is not null)
        {
            var description = Description;
            _description.Text = description;
            _description.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_kindLabel is not null)
        {
            var kind = KindLabel;
            _kindLabel.Text = kind;
            _kindLabel.Visibility = string.IsNullOrEmpty(kind) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
