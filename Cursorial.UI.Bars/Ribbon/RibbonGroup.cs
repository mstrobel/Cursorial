using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A labeled group inside a <see cref="RibbonTab"/> (the guide's <c>.rg</c>): a horizontal row of bar controls over a
/// muted group name, with an optional <c>⋰</c> dialog-launcher. It derives from <see cref="HeaderedItemsControl"/> —
/// <see cref="HeaderedItemsControl.Header"/> is the bottom group name; the group's items are the SAME bar controls a
/// <see cref="Toolbar"/> hosts, used directly (no wrapper). Set <see cref="Ribbon.ButtonSizeProperty"/> on a control
/// (or on the group as a default) to pick its Large/Medium/Small face.
/// </summary>
[TemplatePart(PartLauncher, typeof(ButtonBase))]
[TemplatePart(PartSeparator, typeof(UIElement))]
public class RibbonGroup : HeaderedItemsControl
{
    private const string PartLauncher = "PART_Launcher";
    private const string PartSeparator = "PART_GroupSeparator";

    /// <summary>Raised (bubbling) when the <c>⋰</c> dialog-launcher is invoked — the app opens the group's full dialog.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> DialogLauncherRequestedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(DialogLauncherRequested), RoutingStrategy.Bubble, typeof(RibbonGroup));

    /// <summary>Whether the group shows a <c>⋰</c> dialog-launcher (the guide's optional <c>.launch</c> corner cell).</summary>
    public static readonly StyledProperty<bool> HasDialogLauncherProperty =
        UIProperty.Register<RibbonGroup, bool>(nameof(HasDialogLauncher), defaultValue: false, changed: OnHasDialogLauncherChanged);

    private ButtonBase? _launcher;
    private UIElement? _separator;
    private bool _isLastInBand;

    internal ButtonBase? DialogLauncherForTests => _launcher;

    static RibbonGroup()
    {
        Control.ThemeProperty.OverrideDefaultValue<RibbonGroup>(CursorialBarsTheme.RibbonGroupStyle());
    }

    /// <summary>Creates a ribbon group hosting bar controls.</summary>
    public RibbonGroup()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new RibbonGroupPanel());
        // A group is TRANSPARENT to keyboard navigation — neither a directional-nav container, a focus scope, nor a
        // single Tab stop. The selected tab's whole content is one seamless nav plane: arrow (directional) nav flows
        // across group boundaries through the owning RibbonBand (the single directional container), and Tab flows
        // control-by-control through the default (Continue) tab order — so there is no "arrows within a group, Tab to
        // cross" mode switch. The owning Ribbon is the single returning focus scope, so Escape returns focus to before
        // the ribbon was entered — from any group control OR the tab strip (see Ribbon.OnKeyDown). A group being Once
        // (a single Tab stop) would also stop the band's directional collection at the group edge — the coupling that
        // made arrows unable to cross groups; leaving it Continue lets directional descend into every control.
    }

    /// <inheritdoc cref="DialogLauncherRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? DialogLauncherRequested
    {
        add => AddHandler(DialogLauncherRequestedEvent, value!);
        remove => RemoveHandler(DialogLauncherRequestedEvent, value!);
    }

    /// <inheritdoc cref="HasDialogLauncherProperty"/>
    public bool HasDialogLauncher { get => GetValue(HasDialogLauncherProperty); set => SetValue(HasDialogLauncherProperty, value); }

    // The band panel calls this on its last group so the trailing │ separator doesn't dangle at the band edge. It only
    // STORES the flag + invalidates self; the actual separator Visibility write happens in this group's own
    // MeasureOverride, so a Visibility flip's invalidation stays self-contained rather than walking back up into the
    // band mid-measure. Guarded so a stable flag is a no-op (no per-measure churn).
    internal void SetIsLastInBand(bool last)
    {
        if (_isLastInBand == last)
            return;
        _isLastInBand = last;
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        ApplySeparatorVisibility(); // idempotent; the separator is this group's own template part
        return base.MeasureOverride(availableSize);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_launcher is not null)
            _launcher.Click -= OnLauncherClick;
        _launcher = GetTemplatePart<ButtonBase>(PartLauncher);
        if (_launcher is not null)
            _launcher.Click += OnLauncherClick;

        _separator = GetTemplatePart<UIElement>(PartSeparator);
        ApplySeparatorVisibility();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_launcher is not null)
            _launcher.Click -= OnLauncherClick;
        _launcher = null;
        _separator = null;
        base.OnTemplateDetaching(old);
    }

    private void ApplySeparatorVisibility()
    {
        if (_separator is not null)
            _separator.Visibility = _isLastInBand ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLauncherClick(object? sender, ClickEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(DialogLauncherRequestedEvent, this));
        e.Handled = true;
    }

    private static void OnHasDialogLauncherChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is RibbonGroup group)
            group.PseudoClasses.Set(":has-launcher", newValue);
    }
}
