using System;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>The argument to <c>Popup.Closed</c> — why the popup closed (design doc §8.4).</summary>
public sealed class PopupClosedEventArgs(PopupCloseReason reason) : EventArgs
{
    /// <summary>Why the popup closed.</summary>
    public PopupCloseReason Reason { get; } = reason;
}

/// <summary>
/// A light-dismiss popup primitive (design doc §8.4): the <see cref="Popup"/> lives in the host's LOGICAL
/// tree (so its <c>Child</c> inherits DataContext/resources/styles and key/Escape routing flows back to the
/// host), but contributes nothing to the host's visual layout — when <see cref="IsOpen"/>, its
/// <see cref="Child"/> roots a separate <see cref="TopLevelSurface"/> the window manager places in the popup
/// band above all windows. The S8 menu/combobox/tooltip controls build on this.
/// </summary>
/// <remarks>
/// <b>P7-W4 scope:</b> open/close, placement (relative to the target, clamped to the screen), light dismiss,
/// Escape (the cross-tree route is free — the route builder hops the logical parent at surface roots, and the
/// Child is the Popup's logical child), and the two-way <see cref="IsOpen"/> write-back. Content-swap scene
/// reuse, hit-test-transparent tooltip surfaces, anchor tracking, and chain semantics are W4-b refinements.
/// </remarks>
public class Popup : UIElement
{
    /// <summary>The element shown in the popup surface (swappable while open).</summary>
    public static readonly StyledProperty<UIElement?> ChildProperty =
        UIProperty.Register<Popup, UIElement?>(nameof(Child), changed: OnChildChanged);

    /// <summary>Whether the popup is open (two-way by default; every close reason writes back via <c>SetCurrentValue</c>).</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        UIProperty.Register<Popup, bool>(nameof(IsOpen), changed: OnIsOpenChanged);

    /// <summary>Where the surface places relative to the target (default <see cref="PlacementMode.Bottom"/>).</summary>
    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        UIProperty.Register<Popup, PlacementMode>(nameof(Placement));

    /// <summary>The placement anchor (default: the logical parent).</summary>
    public static readonly StyledProperty<UIElement?> PlacementTargetProperty =
        UIProperty.Register<Popup, UIElement?>(nameof(PlacementTarget));

    /// <summary>A target-local anchor rect override (W4-b; reserved).</summary>
    public static readonly StyledProperty<Rect?> PlacementRectProperty =
        UIProperty.Register<Popup, Rect?>(nameof(PlacementRect));

    /// <summary>Extra horizontal offset applied after placement.</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<Popup, int>(nameof(HorizontalOffset));

    /// <summary>Extra vertical offset applied after placement.</summary>
    public static readonly StyledProperty<int> VerticalOffsetProperty =
        UIProperty.Register<Popup, int>(nameof(VerticalOffset));

    /// <summary>When true the popup is NOT light-dismissed by an outside press (default false).</summary>
    public static readonly StyledProperty<bool> StaysOpenProperty =
        UIProperty.Register<Popup, bool>(nameof(StaysOpen));

    /// <summary>Whether Escape closes the popup (default true).</summary>
    public static readonly StyledProperty<bool> CloseOnEscapeProperty =
        UIProperty.Register<Popup, bool>(nameof(CloseOnEscape), defaultValue: true);

    /// <summary>The popup's drop shadow (default <see cref="WindowShadow.Default"/>).</summary>
    public static readonly StyledProperty<WindowShadow> ShadowProperty =
        UIProperty.Register<Popup, WindowShadow>(nameof(Shadow), defaultValue: WindowShadow.Default);

    private bool _open;
    private WindowManager? _manager;

    static Popup()
    {
        AddGlobalEffects(PropertyEffects.BindsTwoWayByDefault, IsOpenProperty);
    }

    /// <summary>Creates a popup.</summary>
    public Popup() { }

    /// <inheritdoc cref="ChildProperty"/>
    public UIElement? Child { get => GetValue(ChildProperty); set => SetValue(ChildProperty, value); }

    /// <inheritdoc cref="IsOpenProperty"/>
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <inheritdoc cref="PlacementProperty"/>
    public PlacementMode Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }

    /// <inheritdoc cref="PlacementTargetProperty"/>
    public UIElement? PlacementTarget { get => GetValue(PlacementTargetProperty); set => SetValue(PlacementTargetProperty, value); }

    /// <inheritdoc cref="PlacementRectProperty"/>
    public Rect? PlacementRect { get => GetValue(PlacementRectProperty); set => SetValue(PlacementRectProperty, value); }

    /// <inheritdoc cref="HorizontalOffsetProperty"/>
    public int HorizontalOffset { get => GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }

    /// <inheritdoc cref="VerticalOffsetProperty"/>
    public int VerticalOffset { get => GetValue(VerticalOffsetProperty); set => SetValue(VerticalOffsetProperty, value); }

    /// <inheritdoc cref="StaysOpenProperty"/>
    public bool StaysOpen { get => GetValue(StaysOpenProperty); set => SetValue(StaysOpenProperty, value); }

    /// <inheritdoc cref="CloseOnEscapeProperty"/>
    public bool CloseOnEscape { get => GetValue(CloseOnEscapeProperty); set => SetValue(CloseOnEscapeProperty, value); }

    /// <inheritdoc cref="ShadowProperty"/>
    public WindowShadow Shadow { get => GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    /// <summary>The surface hosting the open popup's child (WM-owned; null while closed).</summary>
    internal TopLevelSurface? PopupSurface { get; set; }

    /// <summary>The effective placement anchor — explicit <see cref="PlacementTarget"/> or the logical parent.</summary>
    internal UIElement? EffectiveTarget => PlacementTarget ?? LogicalParent;

    /// <summary>Raised after the popup opens.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the popup closes, with the reason.</summary>
    public event EventHandler<PopupClosedEventArgs>? Closed;

    /// <summary>Opens the popup.</summary>
    public void Open() => SetCurrentValue(IsOpenProperty, true);

    /// <summary>Closes the popup (reason <see cref="PopupCloseReason.Programmatic"/>).</summary>
    public void Close() => SetCurrentValue(IsOpenProperty, false);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => default; // logical-only: zero in the host layout

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && _open && CloseOnEscape && e.Key == Key.Escape)
        {
            CloseCore(PopupCloseReason.EscapeKey);
            e.Handled = true;
        }
    }

    /// <summary>Closes the popup for <paramref name="reason"/> (the WM's light-dismiss/host-event path + Escape).</summary>
    internal void CloseCore(PopupCloseReason reason)
    {
        if (!_open)
            return;

        _open = false;
        _manager?.ClosePopup(this);
        _manager = null;
        if (IsOpen)
            SetCurrentValue(IsOpenProperty, false); // write-back; _open is already false so this does not re-enter
        Closed?.Invoke(this, new PopupClosedEventArgs(reason));
    }

    private void OpenCore()
    {
        if (Child is null)
            return; // nothing to show — defer until a Child is set

        _manager = UIApplication.Current?.WindowManager;
        if (_manager is null)
            return;

        _open = true;
        _manager.OpenPopup(this);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private static void OnIsOpenChanged(UIObject sender, bool oldValue, bool newValue)
    {
        var popup = (Popup)sender;
        if (newValue && !popup._open)
            popup.OpenCore();
        else if (!newValue && popup._open)
            popup.CloseCore(PopupCloseReason.Programmatic);
    }

    private static void OnChildChanged(UIObject sender, UIElement? oldValue, UIElement? newValue)
    {
        var popup = (Popup)sender;

        // The Child is the Popup's LOGICAL child (so it inherits DataContext + key/Escape routing flows back
        // through the logical parent at the surface root); it is the popup surface's visual root, not a visual
        // child of the Popup.
        if (oldValue is { } oldChild && ReferenceEquals(oldChild.LogicalParent, popup))
            popup.RemoveLogicalChild(oldChild);
        if (newValue is { } newChild && newChild.LogicalParent is null)
            popup.AddLogicalChild(newChild);

        // Content swap while open: W4-a re-hosts the new child (scene-reusing swap is W4-b). Clearing the
        // Child while open closes the popup cleanly (write-back + Closed) — there is nothing left to show.
        if (popup._open)
        {
            if (newValue is null)
                popup.CloseCore(PopupCloseReason.Programmatic);
            else if (popup._manager is { } manager)
            {
                manager.ClosePopup(popup);
                manager.OpenPopup(popup);
            }
        }
    }
}
