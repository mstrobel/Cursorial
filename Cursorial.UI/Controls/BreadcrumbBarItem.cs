using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// One segment ("chip") of a <see cref="BreadcrumbBar"/> — the container the bar generates per item, and the
/// element a host may place directly in <see cref="ItemsControl.Items"/> when it wants to author segments by hand
/// (it is its own container, WPF/Avalonia <c>ItemsControl</c> parity).
/// <para>
/// A chip is a <see cref="ButtonBase"/>, deliberately: activating an ancestor segment IS a command invocation, so
/// the whole <see cref="ButtonBase.Command"/> coupling comes for free and behaves exactly as it does on a
/// <see cref="Button"/> — <see cref="ButtonBase.Click"/> bubbles, <c>CanExecute</c> folds into
/// <c>IsEnabledCore</c> so a non-navigable segment greys out by itself, and
/// <c>Enter</c>/<c>Space</c> activation is the base's (there is no second activation path to keep in sync). On
/// top of that the chip adds the breadcrumb-specific pieces: the <c>:current</c> pseudo-class for the deepest
/// segment (which also hides its trailing separator — the trail never ends in a "▸"), and the
/// <c>PART_Separator</c> affordance, whose press asks the owner for the Explorer-style sibling drop-down rather
/// than activating the chip.
/// </para>
/// </summary>
[TemplatePart(PartSeparator, typeof(UIElement))]
public class BreadcrumbBarItem : ButtonBase
{
    private const string PartSeparator = "PART_Separator";

    /// <summary>
    /// Whether this is the <b>current</b> (deepest, trailing) segment — <c>:current</c>. Owner-written with
    /// <c>SetCurrentValue</c> whenever the item collection or the container set changes, so a two-way binding or an
    /// explicit author-set value survives. A current chip hides its <c>PART_Separator</c>.
    /// </summary>
    public static readonly StyledProperty<bool> IsCurrentProperty =
        UIProperty.Register<BreadcrumbBarItem, bool>(nameof(IsCurrent), defaultValue: false, changed: OnIsCurrentChanged);

    private UIElement? _separator;

    static BreadcrumbBarItem()
    {
        PseudoClassMapping.Register<BreadcrumbBarItem>(IsCurrentProperty, ":current");
    }

    /// <summary>Creates a breadcrumb chip.</summary>
    public BreadcrumbBarItem()
    {
    }

    /// <inheritdoc cref="IsCurrentProperty"/>
    public bool IsCurrent { get => GetValue(IsCurrentProperty); set => SetValue(IsCurrentProperty, value); }

    /// <summary>
    /// The owning bar: the nearest <see cref="BreadcrumbBar"/> up the <c>UIParent</c> bridge. A generated chip is a
    /// LOGICAL child of the bar (punch 43), while the bar template's own leading ellipsis chip is a visual-only
    /// template child whose <c>TemplatedParent</c> is the bar — <c>UIParent</c> (<c>LogicalParent ?? TemplatedParent</c>)
    /// is the one walk that resolves both.
    /// </summary>
    public BreadcrumbBar? OwnerBar
    {
        get
        {
            for (var node = UIParent; node is not null; node = node.UIParent)
            {
                if (node is BreadcrumbBar bar)
                    return bar;
            }

            return null;
        }
    }

    /// <summary>The <c>PART_Separator</c> affordance, when the template provides one (test/inspection seam).</summary>
    internal UIElement? SeparatorPart => _separator;

    // ───────────────────────────── template wiring ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_separator is not null)
            _separator.MouseDown -= OnSeparatorMouseDown;

        _separator = GetTemplatePart<UIElement>(PartSeparator);

        if (_separator is not null)
        {
            _separator.MouseDown += OnSeparatorMouseDown;
            UpdateSeparatorVisibility(); // push the current :current state into the freshly-realized part
        }
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_separator is not null)
            _separator.MouseDown -= OnSeparatorMouseDown;

        _separator = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── activation ─────────────────────────────

    /// <summary>
    /// Raises <see cref="ButtonBase.Click"/> + invokes <see cref="ButtonBase.Command"/> (the base), then asks the
    /// owner to raise <see cref="BreadcrumbBar.ItemActivated"/> for this segment. Both fire for every activation
    /// path — pointer, <c>Enter</c>, <c>Space</c>, access key, programmatic — because they all funnel through
    /// <c>ButtonBase.OnClick</c>. The command runs first (the base), so a host that binds only
    /// <see cref="ButtonBase.Command"/> and a host that listens on the bar see the same ordering as a
    /// <see cref="Button"/> in a <see cref="Menu"/>.
    /// </summary>
    /// <param name="method">How the activation was invoked.</param>
    protected override void OnClick(InvokeMethod method = InvokeMethod.Programmatic)
    {
        base.OnClick(method);
        OwnerBar?.NotifyItemActivated(this);
    }

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        // The bar's "active chip" is whatever chip actually holds focus — resolved from the live focus event, never
        // a cached index a collection edit could leave stale (the ListBox keyboard-cursor rule, CD-P9-16).
        OwnerBar?.NotifyItemFocused(this);
    }

    // The separator is a child of this chip, so its press bubbles HERE. Handling it on the separator itself (an
    // instance handler at the source) claims the event before ButtonBase.OnMouseDown runs at this element — that
    // class handler early-outs on e.Handled — so pressing "▸" opens the sibling list INSTEAD of navigating to the
    // segment. (Pressing the chip face is unaffected: the separator is not on that route.)
    private void OnSeparatorMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.Button != MouseButton.Left || !IsEffectivelyEnabled)
            return;

        e.Handled = true;
        OwnerBar?.RequestDropDown(this);
    }

    private static void OnIsCurrentChanged(UIObject sender, bool oldValue, bool newValue)
        => (sender as BreadcrumbBarItem)?.UpdateSeparatorVisibility();

    // The trail never ends in a separator: the current (deepest) chip collapses its "▸". Driven imperatively rather
    // than by a `^:current /template/ #PART_Separator` theme rule so both theme halves (the code-first
    // ControlThemes and the declarative Themes/*/Controls.xaml) get the behavior from ONE place and can't drift.
    private void UpdateSeparatorVisibility()
    {
        _separator?.SetCurrentValue(VisibilityProperty, IsCurrent ? Visibility.Collapsed : Visibility.Visible);
    }
}
