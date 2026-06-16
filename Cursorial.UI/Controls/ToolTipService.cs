using System.Runtime.CompilerServices;

using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The process-wide tooltip behavior (design doc §12.7): attach <see cref="TipProperty"/> to any element and a
/// per-application controller shows a <see cref="ToolTip"/> popup when the pointer rests on it (after
/// <see cref="InitialDelayProperty"/>, default 500 ms), riding S3's <c>InputDispatcher.HoverChanged</c> hook and
/// an S5 <see cref="UITimer"/>. The tooltip closes on hover-leave, any button press, any non-modifier key press,
/// and terminal focus-out. There are no per-element timers — one controller observes the hover-chain stream.
/// </summary>
/// <remarks>
/// <see cref="ShowOnFocusProperty"/> is declared (<c>bool?</c>; <see langword="null"/> = auto, enabled when the
/// terminal cannot report motion so hover cannot exist). The focus-triggered show itself is a recorded deferral —
/// the hover path is the v1 behavior.
/// </remarks>
public sealed class ToolTipService
{
    private ToolTipService() { } // non-instantiable; the attached-property owner type (mirrors KeyboardNavigation)

    /// <summary>The tooltip content (any object; a string is common). Attaching a non-null value arms the controller.</summary>
    public static readonly AttachedProperty<object?> TipProperty =
        UIProperty.RegisterAttached<ToolTipService, UIElement, object?>("Tip", changed: OnTipChanged);

    /// <summary>The hover dwell before the tooltip shows (default 500 ms).</summary>
    public static readonly AttachedProperty<TimeSpan> InitialDelayProperty =
        UIProperty.RegisterAttached<ToolTipService, UIElement, TimeSpan>("InitialDelay", defaultValue: TimeSpan.FromMilliseconds(500));

    /// <summary>Whether to show on keyboard focus instead of hover — <c>null</c> (default) = auto from motion capability.</summary>
    public static readonly AttachedProperty<bool?> ShowOnFocusProperty =
        UIProperty.RegisterAttached<ToolTipService, UIElement, bool?>("ShowOnFocus");

    /// <summary>Gets <see cref="TipProperty"/>.</summary>
    public static object? GetTip(UIElement element) => element.GetValue(TipProperty);

    /// <summary>Sets <see cref="TipProperty"/>.</summary>
    public static void SetTip(UIElement element, object? value) => element.SetValue(TipProperty, value);

    /// <summary>Gets <see cref="InitialDelayProperty"/>.</summary>
    public static TimeSpan GetInitialDelay(UIElement element) => element.GetValue(InitialDelayProperty);

    /// <summary>Sets <see cref="InitialDelayProperty"/>.</summary>
    public static void SetInitialDelay(UIElement element, TimeSpan value) => element.SetValue(InitialDelayProperty, value);

    /// <summary>Gets <see cref="ShowOnFocusProperty"/>.</summary>
    public static bool? GetShowOnFocus(UIElement element) => element.GetValue(ShowOnFocusProperty);

    /// <summary>Sets <see cref="ShowOnFocusProperty"/>.</summary>
    public static void SetShowOnFocus(UIElement element, bool? value) => element.SetValue(ShowOnFocusProperty, value);

    private static void OnTipChanged(UIObject sender, object? oldValue, object? newValue)
    {
        // Arming a tip ensures the controller for the running application exists + is subscribed (the hover
        // stream it observes is app-wide, so one controller serves every tip-bearing element).
        if (newValue is not null && UIApplication.Current is { } app)
            ToolTipController.Ensure(app);
    }
}

/// <summary>
/// The per-application tooltip controller (design doc §12.7) — one instance per <see cref="UIApplication"/>,
/// created lazily when the first <see cref="ToolTipService.TipProperty"/> is set. It observes the dispatcher's
/// hover-chain stream and owns one reusable hit-test-transparent <see cref="Popup"/>.
/// </summary>
internal sealed class ToolTipController
{
    private static readonly ConditionalWeakTable<UIApplication, ToolTipController> Controllers = new();
    private static readonly TimeSpan QuickShowWindow = TimeSpan.FromMilliseconds(100);

    private readonly ToolTip _toolTip = new();
    private readonly Popup _popup;

    private UIElement? _target;        // the tip element we are pending-or-showing for
    private bool _shown;               // whether the popup is currently open
    private bool _recentlyClosed;      // a tooltip closed < QuickShowWindow ago ⇒ the next shows immediately
    private UITimer? _openTimer;
    private UITimer? _quickShowTimer;

    private ToolTipController(UIApplication app)
    {
        _popup = new Popup { Child = _toolTip, StaysOpen = true, IsHitTestTransparent = true };

        var dispatcher = app.InputDispatcher;
        dispatcher.HoverChanged += OnHoverChanged;
        dispatcher.DismissTransients += Reset;                 // any button/non-modifier-key press dismisses
        dispatcher.TerminalFocusChanged += OnTerminalFocusChanged;
    }

    /// <summary>Ensures the controller for <paramref name="app"/> exists and is subscribed (idempotent).</summary>
    internal static void Ensure(UIApplication app)
    {
        if (!Controllers.TryGetValue(app, out _))
            Controllers.Add(app, new ToolTipController(app));
    }

    private void OnHoverChanged(HoverChainSnapshot removed, HoverChainSnapshot added)
    {
        // Left the tracked element (its subtree, or it detached — both truncate the chain) ⇒ cancel/close.
        if (_target is not null && Contains(removed, _target))
            Reset();

        // Entered a (different) tip-bearing element ⇒ arm. Intra-element moves don't change the chain, so the
        // open timer is never reset by them (doc §12.7).
        if (InnermostTipOwner(added) is { } owner && !ReferenceEquals(owner, _target))
            Arm(owner);
    }

    private void OnTerminalFocusChanged(bool focused)
    {
        if (!focused)
            Reset(); // focus-out closes the tooltip (it must not outlive the focused terminal)
    }

    private void Arm(UIElement owner)
    {
        Reset(); // cancel any prior pending/shown tooltip
        _target = owner;
        var delay = _recentlyClosed ? TimeSpan.Zero : ToolTipService.GetInitialDelay(owner); // quick-show
        _openTimer = UITimer.Start(delay, () => Show(owner));
    }

    private void Show(UIElement owner)
    {
        _openTimer = null;
        if (!ReferenceEquals(_target, owner) || !owner.IsAttachedToTree)
            return; // re-targeted or detached while the timer was pending

        _toolTip.Content = ToolTipService.GetTip(owner);
        _popup.PlacementTarget = owner;
        _popup.Placement = PlacementMode.Pointer; // below-right of the pointer cell
        _popup.SetCurrentValue(Popup.HorizontalOffsetProperty, 1);
        _popup.SetCurrentValue(Popup.VerticalOffsetProperty, 1);
        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
        _shown = true;
    }

    // Cancels a pending open and closes a shown tooltip; clears the tracked element.
    private void Reset()
    {
        _openTimer?.Stop();
        _openTimer = null;

        if (_shown)
        {
            _shown = false;
            _popup.SetCurrentValue(Popup.IsOpenProperty, false);
            StartQuickShowWindow(); // a re-hover within 100 ms shows immediately
        }

        _target = null;
    }

    private void StartQuickShowWindow()
    {
        _recentlyClosed = true;
        _quickShowTimer?.Stop();
        _quickShowTimer = UITimer.Start(QuickShowWindow, () => { _recentlyClosed = false; _quickShowTimer = null; });
    }

    // The innermost (deepest) tip-bearing element in a root-first chain snapshot, or null.
    private static UIElement? InnermostTipOwner(HoverChainSnapshot chain)
    {
        UIElement? owner = null;
        for (var i = 0; i < chain.Count; i++)
            if (ToolTipService.GetTip(chain[i]) is not null)
                owner = chain[i];
        return owner;
    }

    private static bool Contains(HoverChainSnapshot chain, UIElement element)
    {
        for (var i = 0; i < chain.Count; i++)
            if (ReferenceEquals(chain[i], element))
                return true;
        return false;
    }
}
