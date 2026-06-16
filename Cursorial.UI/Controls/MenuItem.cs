using System.Windows.Input;

using Cursorial.Input;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// An item in a <see cref="Menu"/> / submenu / <c>ContextMenu</c> (design doc §12.7). A <b>leaf</b> (no sub-items)
/// invokes on click — raising <see cref="Click"/>, executing <see cref="Command"/>, toggling <see cref="IsChecked"/>
/// when <see cref="IsCheckable"/> — and dismisses the menu; a <b>submenu header</b> (has sub-items) toggles its
/// submenu <see cref="Popup"/> instead. Derives from <see cref="HeaderedItemsControl"/> (the header is the label,
/// the items are the submenu); it cannot also extend <see cref="ButtonBase"/> (single inheritance), so it carries
/// its own click/command surface. <c>:highlighted</c>/<c>:open</c> are <c>DirectProperty</c>-backed (flipped via
/// <see cref="PseudoClasses"/>); <c>:checked</c> mirrors <see cref="IsChecked"/> via <see cref="PseudoClassMapping"/>.
/// </summary>
public class MenuItem : HeaderedItemsControl
{
    /// <summary>The command invoked when a leaf item is clicked/activated.</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<MenuItem, ICommand?>(nameof(Command), changed: OnCommandChanged);

    /// <summary>The parameter passed to <see cref="Command"/>.</summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<MenuItem, object?>(nameof(CommandParameter), changed: OnCommandParameterChanged);

    /// <summary>The display-only gesture hint (e.g. "Ctrl+S") shown right-aligned, faint. Not a live binding.</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<MenuItem, string?>(nameof(InputGestureText));

    /// <summary>Whether the item shows a check column and toggles <see cref="IsChecked"/> on click.</summary>
    public static readonly StyledProperty<bool> IsCheckableProperty =
        UIProperty.Register<MenuItem, bool>(nameof(IsCheckable));

    /// <summary>The checked state of a <see cref="IsCheckable"/> item (<c>:checked</c> mirrors it).</summary>
    public static readonly StyledProperty<bool> IsCheckedProperty =
        UIProperty.Register<MenuItem, bool>(nameof(IsChecked));

    /// <summary>Whether this item's submenu is open (<c>:open</c>; two-way with the submenu <see cref="Popup"/>).</summary>
    public static readonly DirectProperty<MenuItem, bool> IsSubmenuOpenProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsSubmenuOpen), static m => m._isSubmenuOpen, static (m, v) => m.SetSubmenuOpen(v));

    /// <summary>Whether this item is the highlighted (current) item — hover or keyboard cursor (<c>:highlighted</c>).</summary>
    public static readonly DirectProperty<MenuItem, bool> IsHighlightedProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsHighlighted), static m => m._isHighlighted, static (m, v) => m.SetHighlighted(v));

    /// <summary>The bubbling event raised when a leaf item is invoked.</summary>
    public static readonly RoutedEvent<ClickEventArgs> ClickEvent =
        RoutedEvent<ClickEventArgs>.Register(nameof(Click), RoutingStrategy.Bubble, typeof(MenuItem));

    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(250);

    private bool _isSubmenuOpen;
    private bool _isHighlighted;
    private Popup? _popup;
    private UITimer? _hoverTimer;

    static MenuItem() => PseudoClassMapping.Register<MenuItem>(IsCheckedProperty, ":checked");

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc cref="IsCheckableProperty"/>
    public bool IsCheckable { get => GetValue(IsCheckableProperty); set => SetValue(IsCheckableProperty, value); }

    /// <inheritdoc cref="IsCheckedProperty"/>
    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    /// <inheritdoc cref="IsSubmenuOpenProperty"/>
    public bool IsSubmenuOpen { get => _isSubmenuOpen; set => SetSubmenuOpen(value); }

    /// <inheritdoc cref="IsHighlightedProperty"/>
    public bool IsHighlighted { get => _isHighlighted; set => SetHighlighted(value); }

    /// <summary>Whether this item has sub-items (a submenu header) rather than being an invoke-on-click leaf.</summary>
    public bool HasItems => ItemContainerGenerator.ContainerCount > 0;

    /// <summary>CLR sugar over <see cref="ClickEvent"/>.</summary>
    public event EventHandler<ClickEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value!);
        remove => RemoveHandler(ClickEvent, value!);
    }

    /// <summary>The command-aware enabled gate (CD25): enabled unless a non-null <see cref="Command"/> reports it can't execute.</summary>
    protected override bool IsEnabledCore => Command is not { } command || command.CanExecute(CommandParameter);

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        SubscribeCanExecute(); // CD25: a live CanExecuteChanged must re-gate IsEnabledCore (matches ButtonBase)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        StopHoverTimer();       // owners stop timers on detach (§12.2)
        SetSubmenuOpen(false);  // close the submenu so its Popup surface doesn't leak on detach
        UnsubscribeCanExecute();
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;

        _popup = GetTemplatePart<Popup>("PART_Popup");
        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = OwnerItemsControl is Menu ? PlacementMode.Bottom : PlacementMode.Right; // bar → down, nested → right
            _popup.Closed += OnPopupClosed;
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isSubmenuOpen); // sync the part to current state
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        if (HasItems)
        {
            if (_isSubmenuOpen)
                SetSubmenuOpen(false); // toggle closed
            else
                OpenSubmenu();         // open (closing any sibling submenu)
        }
        else
        {
            Invoke(); // a leaf invokes + dismisses
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHighlighted(true);

        if (!HasItems || _isSubmenuOpen)
            return;

        if (MenuIsActive())
            OpenSubmenu();      // a sibling submenu is already open ⇒ switch immediately (no delay)
        else
            StartHoverTimer();  // otherwise open after the hover delay
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHighlighted(false);
        StopHoverTimer(); // cancel a pending hover-open (an already-open submenu stays open)
    }

    /// <summary>Invokes a leaf item: raise <see cref="Click"/>, toggle <see cref="IsChecked"/> (if checkable),
    /// execute <see cref="Command"/>, then dismiss the whole menu.</summary>
    protected virtual void Invoke()
    {
        RaiseEvent(RentEvent(ClickEvent));

        if (IsCheckable)
            SetCurrentValue(IsCheckedProperty, !IsChecked); // SetCurrentValue preserves a two-way IsChecked binding

        if (Command is { } command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);

        CloseMenuChain();
    }

    // Opens this submenu, first closing any sibling submenu (only one branch of a level is open at a time).
    private void OpenSubmenu()
    {
        StopHoverTimer();
        CloseSiblings();
        SetSubmenuOpen(true);
    }

    private void StartHoverTimer()
    {
        StopHoverTimer();
        _hoverTimer = UITimer.Start(HoverOpenDelay, OpenSubmenu); // leave cancels it before it fires
    }

    private void StopHoverTimer()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
    }

    // Whether any sibling at this level already has its submenu open (the menu is "active").
    private bool MenuIsActive()
    {
        if (OwnerItemsControl is not { } owner)
            return false;

        for (var i = 0; i < owner.ItemContainerGenerator.ContainerCount; i++)
            if (owner.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { _isSubmenuOpen: true })
                return true;
        return false;
    }

    private void CloseSiblings()
    {
        if (OwnerItemsControl is not { } owner)
            return;

        for (var i = 0; i < owner.ItemContainerGenerator.ContainerCount; i++)
            if (owner.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { _isSubmenuOpen: true } sibling && !ReferenceEquals(sibling, this))
                sibling.SetSubmenuOpen(false);
    }

    private void SetSubmenuOpen(bool value)
    {
        if (!SetAndRaise(IsSubmenuOpenProperty, ref _isSubmenuOpen, value))
            return;

        PseudoClasses.Set(":open", value);
        _popup?.SetCurrentValue(Popup.IsOpenProperty, value); // drive the part (light-dismiss writes back via OnPopupClosed)
    }

    private void SetHighlighted(bool value)
    {
        if (SetAndRaise(IsHighlightedProperty, ref _isHighlighted, value))
            PseudoClasses.Set(":highlighted", value);
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e) => SetSubmenuOpen(false); // light-dismiss / Esc

    // Walk up the menu ownership chain closing every open submenu so a leaf invoke dismisses the whole menu.
    private void CloseMenuChain()
    {
        for (UIElement? node = this; node is not null; node = (node as MenuItem)?.OwnerItemsControl)
            if (node is MenuItem item && item._isSubmenuOpen)
                item.SetSubmenuOpen(false);
    }

    // The ItemsControl (Menu or parent MenuItem) that generated this container — its logical parent (punch 43).
    private ItemsControl? OwnerItemsControl
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is ItemsControl owner)
                    return owner;
            return null;
        }
    }

    private void SubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged += OnCanExecuteChanged;
    }

    private void UnsubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged -= OnCanExecuteChanged;
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => InvalidateIsEnabledCore();

    private static void OnCommandChanged(UIObject sender, ICommand? oldValue, ICommand? newValue)
    {
        if (sender is not MenuItem item)
            return;

        // Re-point the CanExecuteChanged subscription (on Command change AND, via attach/detach, lifetime) — CD25.
        if (oldValue is { } old && item.IsAttachedToTree)
            old.CanExecuteChanged -= item.OnCanExecuteChanged;
        if (newValue is { } @new && item.IsAttachedToTree)
            @new.CanExecuteChanged += item.OnCanExecuteChanged;

        item.InvalidateIsEnabledCore();
    }

    private static void OnCommandParameterChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is MenuItem item)
            item.InvalidateIsEnabledCore(); // the gate reads CanExecute(CommandParameter)
    }
}
