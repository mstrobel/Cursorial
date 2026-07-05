using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A top-level window (design doc §8.2): a templated <see cref="ContentControl"/> the
/// <see cref="WindowManager"/> hosts on its own <see cref="TopLevelSurface"/>. <c>Content</c> is
/// <see cref="ContentControl.Content"/> (the chrome template's <see cref="ContentPresenter"/> auto-aliases
/// to it). Behavior keys on chrome <see cref="WindowHitTestRole"/>s, not part names (§8.3).
/// </summary>
/// <remarks>
/// <b>P7-W1 scope:</b> modeless <see cref="Show()"/>/<see cref="Close()"/>, activation, the interim
/// occluding chrome template, and the ✕ (<see cref="WindowHitTestRole.Close"/>) action. Modality +
/// <c>ShowDialogAsync</c> are W2; drag-move/resize (the <see cref="WindowHitTestRole.Drag"/>/
/// <see cref="WindowHitTestRole.ResizeSE"/> interactions) and shadow rendering are W5. The interim chrome
/// template is provided as the <see cref="Control.Template"/> default for <see cref="Window"/> (overridable
/// by S8's themed control theme at C4, which arms at the higher <c>ControlTheme</c> layer).
/// </remarks>
public partial class Window : ContentControl
{
    /// <summary>The window title (<c>AffectsRender</c>; the active main window mirrors it to OSC 2 — §8.8, W5).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.Register<Window, string?>(nameof(Title));

    /// <summary>The chrome style (title bar vs chrome-less); both occlude.</summary>
    public static readonly StyledProperty<WindowStyle> WindowStyleProperty =
        UIProperty.Register<Window, WindowStyle>(nameof(WindowStyle));

    /// <summary>The window's drop shadow (rendered at W5; default <see cref="WindowShadow.None"/>).</summary>
    public static readonly StyledProperty<WindowShadow> ShadowProperty =
        UIProperty.Register<Window, WindowShadow>(nameof(Shadow), defaultValue: WindowShadow.Default);

    /// <summary>The window's screen-space left column (signed; <c>AffectsComposite</c> — moving a window is composite-only).</summary>
    public static readonly StyledProperty<int> LeftProperty =
        UIProperty.Register<Window, int>(nameof(Left));

    /// <summary>The window's screen-space top row (signed; <c>AffectsComposite</c>).</summary>
    public static readonly StyledProperty<int> TopProperty =
        UIProperty.Register<Window, int>(nameof(Top));

    /// <summary>Auto-sizing policy (default <see cref="SizeToContent.WidthAndHeight"/>).</summary>
    public static readonly StyledProperty<SizeToContent> SizeToContentProperty =
        UIProperty.Register<Window, SizeToContent>(nameof(SizeToContent), defaultValue: SizeToContent.WidthAndHeight);

    /// <summary>Normal vs maximized.</summary>
    public static readonly StyledProperty<WindowState> WindowStateProperty =
        UIProperty.Register<Window, WindowState>(nameof(WindowState), changed: OnWindowStateChanged);

    /// <summary>Where the window first appears when shown.</summary>
    public static readonly StyledProperty<WindowStartupLocation> WindowStartupLocationProperty =
        UIProperty.Register<Window, WindowStartupLocation>(nameof(WindowStartupLocation));

    /// <summary>Whether the user may drag the window (default true).</summary>
    public static readonly StyledProperty<bool> CanMoveProperty =
        UIProperty.Register<Window, bool>(nameof(CanMove), defaultValue: true);

    /// <summary>Whether the user may resize the window (default true).</summary>
    public static readonly StyledProperty<bool> CanResizeProperty =
        UIProperty.Register<Window, bool>(nameof(CanResize), defaultValue: true);

    /// <summary>Whether the window's ✕ closes it (default true).</summary>
    public static readonly StyledProperty<bool> CanCloseProperty =
        UIProperty.Register<Window, bool>(nameof(CanClose), defaultValue: true);

    private static readonly UIPropertyKey<bool> IsActivePropertyKey =
        UIProperty.RegisterReadOnly<Window, bool>(nameof(IsActive));

    /// <summary>Whether this window is the active (focused) top-level window — read-only.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty = IsActivePropertyKey.Property;

    private static readonly UIPropertyKey<bool> IsClippedByViewportPropertyKey =
        UIProperty.RegisterReadOnly<Window, bool>(nameof(IsClippedByViewport));

    /// <summary>Whether the window currently overhangs the viewport on any edge (the WM recomputes it each
    /// frame) — read-only; the fit-to-viewport affordance keys off it (§8.7).</summary>
    public static readonly StyledProperty<bool> IsClippedByViewportProperty = IsClippedByViewportPropertyKey.Property;

    /// <summary>A code-behind seam fired when a blocked window is poked (the theme flash rides §8.6's pulse, W2).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ModalAttentionEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(ModalAttention), RoutingStrategy.Bubble, typeof(Window));

    /// <summary>A code-behind seam fired when a window is first shown.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ShownEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Shown), RoutingStrategy.Direct, typeof(Window));

    private Window? _owner;
    private bool _closing; // reentrancy guard
    private TaskCompletionSource<object?>? _dialogTcs;
    private CancellationToken _dialogCancellation;
    private CancellationTokenRegistration _dialogRegistration;

    // The chrome active-look handlers, wired in OnApplyTemplate / unhooked in OnTemplateDetaching (no leak on
    // re-template; the P9-closeout relocation of the interim's lifetime-of-the-window subscription).
    private EventHandler? _chromeActivatedHandler;
    private EventHandler? _chromeDeactivatedHandler;

    static Window()
    {
        // The window chrome is a themed control template (C4): CursorialTheme.BuiltIn keys a "Theme.Window"
        // control theme by typeof(Window), resolved through the control-theme chain like every other control
        // (the backstop, so a custom app theme still falls back to it). No Template default on the type —
        // an app Window style/template overrides the chrome cleanly (design doc §8.3, punch 36).
        AffectsRender<Window>(TitleProperty);
        AffectsComposite<Window>(LeftProperty, TopProperty);
        AddGlobalEffects(PropertyEffects.AffectsRender, ShadowProperty);
    }

    /// <summary>Creates a window.</summary>
    public Window() {}

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <inheritdoc cref="WindowStyleProperty"/>
    public WindowStyle WindowStyle
    {
        get => GetValue(WindowStyleProperty);
        set => SetValue(WindowStyleProperty, value);
    }

    /// <inheritdoc cref="ShadowProperty"/>
    public WindowShadow Shadow
    {
        get => GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    /// <inheritdoc cref="LeftProperty"/>
    public int Left
    {
        get => GetValue(LeftProperty);
        set => SetValue(LeftProperty, value);
    }

    /// <inheritdoc cref="TopProperty"/>
    public int Top
    {
        get => GetValue(TopProperty);
        set => SetValue(TopProperty, value);
    }

    /// <inheritdoc cref="SizeToContentProperty"/>
    public SizeToContent SizeToContent
    {
        get => GetValue(SizeToContentProperty);
        set => SetValue(SizeToContentProperty, value);
    }

    /// <inheritdoc cref="WindowStateProperty"/>
    public WindowState WindowState
    {
        get => GetValue(WindowStateProperty);
        set => SetValue(WindowStateProperty, value);
    }

    /// <inheritdoc cref="WindowStartupLocationProperty"/>
    public WindowStartupLocation WindowStartupLocation
    {
        get => GetValue(WindowStartupLocationProperty);
        set => SetValue(WindowStartupLocationProperty, value);
    }

    /// <inheritdoc cref="CanMoveProperty"/>
    public bool CanMove
    {
        get => GetValue(CanMoveProperty);
        set => SetValue(CanMoveProperty, value);
    }

    /// <inheritdoc cref="CanResizeProperty"/>
    public bool CanResize
    {
        get => GetValue(CanResizeProperty);
        set => SetValue(CanResizeProperty, value);
    }

    /// <inheritdoc cref="CanCloseProperty"/>
    public bool CanClose
    {
        get => GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    /// <inheritdoc cref="IsActiveProperty"/>
    public bool IsActive => GetValue(IsActiveProperty);

    /// <inheritdoc cref="IsClippedByViewportProperty"/>
    public bool IsClippedByViewport => GetValue(IsClippedByViewportProperty);

    /// <summary>WM-driven: flips <see cref="IsClippedByViewport"/> (recomputed each frame in OnLayoutCompleted).</summary>
    internal void SetClippedByViewport(bool clipped)
    {
        if (IsClippedByViewport != clipped)
            SetValue(IsClippedByViewportPropertyKey, clipped);
    }

    /// <summary>The owner window. Settable until the window is first shown; throws thereafter.</summary>
    public Window? Owner
    {
        get => _owner;
        set
        {
            if (IsShown)
                throw new InvalidOperationException("Window.Owner is immutable once the window has been shown.");

            _owner = value;
        }
    }

    /// <summary>The window manager hosting this window — non-null while shown.</summary>
    public WindowManager? Manager { get; private set; }

    /// <summary>The surface the manager hosts this window on while shown (WM-owned).</summary>
    internal TopLevelSurface? HostSurface { get; set; }

    /// <summary>Whether the window is currently shown.</summary>
    public bool IsShown => Manager is not null;

    /// <summary>Whether the window is shown modally (set by <see cref="ShowDialogAsync(CancellationToken)"/>).</summary>
    public bool IsModal { get; private set; }

    /// <summary>The realized content size (excludes shadow); valid while shown.</summary>
    public Size ActualSize { get; internal set; }

    /// <summary>A dialog result a modal window may set before closing (W2 consumes it).</summary>
    public object? DialogResult { get; set; }

    /// <summary>Fired before the window closes; a handler may veto a cancelable close.</summary>
    public event EventHandler<WindowClosingEventArgs>? Closing;

    /// <summary>Fired after the window has closed.</summary>
    public event EventHandler? Closed;

    /// <summary>Fired when the window becomes active.</summary>
    public event EventHandler? Activated;

    /// <summary>Fired when the window stops being active.</summary>
    public event EventHandler? Deactivated;

    /// <summary>The CLR sugar over <see cref="ShownEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Shown
    {
        add => AddHandler(ShownEvent, value!);
        remove => RemoveHandler(ShownEvent, value!);
    }

    /// <summary>The CLR sugar over <see cref="ModalAttentionEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? ModalAttention
    {
        add => AddHandler(ModalAttentionEvent, value!);
        remove => RemoveHandler(ModalAttentionEvent, value!);
    }

    // ── Show / Activate / Close ──────────────────────────────────────────────────────────────────

    /// <summary>Shows the window modelessly on the default manager (the owner's, else the application's).</summary>
    public void Show()
    {
        var manager = _owner?.Manager
                      ?? UIApplication.Current?.WindowManager
                      ?? throw new InvalidOperationException("No window manager is available to show the window (the application is not running).");

        Show(manager);
    }

    /// <summary>Shows the window modelessly on <paramref name="manager"/> and activates it (WPF semantics).</summary>
    public void Show(WindowManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (IsShown)
        {
            Activate();
            return;
        }

        Manager = manager;
        manager.ShowWindow(this);
        RaiseEvent(new RoutedEventArgs(ShownEvent, this));
    }

    /// <summary>Activates the window (brings it to the top of its band). Returns false when no manager hosts it (or it is modal-blocked).</summary>
    public bool Activate()
    {
        if (Manager is not {} manager)
            return false;

        return manager.ActivateWindow(this);
    }

    /// <summary>
    /// Shows the window <b>modally</b> and returns a task that completes with its <see cref="DialogResult"/>
    /// when it closes. The running frame loop is the pump (no nested dispatcher loop — §8.9): <c>await</c>ing
    /// this does not block the UI thread. Cancellation posts a forced close to the UI thread (invariant 6) and
    /// completes the task canceled; a pre-canceled token returns a canceled task with no side effects.
    /// </summary>
    public Task<object?> ShowDialogAsync(CancellationToken cancellationToken = default)
    {
        if (IsShown)
            throw new InvalidOperationException("The window is already shown.");

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<object?>(cancellationToken);

        var manager = _owner?.Manager ??
                      UIApplication.Current?.WindowManager ??
                      throw new InvalidOperationException("No window manager is available to show the dialog.");

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;
        _dialogCancellation = cancellationToken;
        IsModal = true;
        Manager = manager;
        manager.ShowDialog(this);

        if (cancellationToken.CanBeCanceled)
        {
            // Close mutates WM/styling state — marshal it to the UI thread (never the canceling thread).
            _dialogRegistration = cancellationToken.Register(
                static self =>
                {
                    var window = (Window) self!;

                    UIApplication.Current?.Dispatcher.Post(
                        () =>
                        {
                            if (window.IsShown)
                                window.Close(WindowCloseReason.Programmatic);
                        });
                },
                this);
        }

        return tcs.Task;
    }

    /// <summary>
    /// The typed <see cref="ShowDialogAsync(CancellationToken)"/>: cancellation <b>throws</b>
    /// (an <see cref="OperationCanceledException"/> propagates through the await — the outer task is
    /// Canceled, never a default return); a normal close returns the <see cref="DialogResult"/> cast to
    /// <typeparamref name="TResult"/>, or <c>default(TResult)</c> when it is null or not a <typeparamref name="TResult"/>.
    /// </summary>
    public async Task<TResult?> ShowDialogAsync<TResult>(CancellationToken cancellationToken = default)
    {
        // ConfigureAwait(false): the projection below is a pure cast — it needs no UI-thread affinity, and the
        // caller's own await re-captures their context. (The cancellation rethrow happens at the await above.)
        var result = await ShowDialogAsync(cancellationToken).ConfigureAwait(false);
        return result is TResult typed ? typed : default;
    }

    /// <summary>Closes the window (a cancelable <see cref="WindowCloseReason.Programmatic"/> close).</summary>
    public void Close() => Close(WindowCloseReason.Programmatic);

    /// <summary>Sets <see cref="DialogResult"/> and closes the window.</summary>
    public void Close(object? dialogResult)
    {
        DialogResult = dialogResult;
        Close(WindowCloseReason.Programmatic);
    }

    /// <summary>
    /// Closes the window for <paramref name="reason"/>: raises <see cref="Closing"/> (cancelable per reason),
    /// then removes the surface and raises <see cref="Closed"/>. Reentrancy-guarded; a no-op when not shown.
    /// </summary>
    internal void Close(WindowCloseReason reason)
    {
        if (_closing || !IsShown)
            return;

        var closing = new WindowClosingEventArgs(reason);
        _closing = true;

        try
        {
            Closing?.Invoke(this, closing);

            if (closing.CanCancel && closing.Cancel)
                return; // vetoed

            Manager!.CloseOwnedAndHostedOf(this); // owned windows + hosted popups close first (§8.8)
            Manager!.CloseWindow(this);
            Manager = null;
            IsModal = false;
            SetActiveInternal(false);
            Closed?.Invoke(this, EventArgs.Empty);

            // Complete the modal task (if any): canceled when the close was cancellation-driven, else the result.
            _dialogRegistration.Dispose();

            if (_dialogTcs is {} tcs)
            {
                _dialogTcs = null;

                if (_dialogCancellation.IsCancellationRequested)
                    tcs.TrySetCanceled(_dialogCancellation);
                else
                    tcs.TrySetResult(DialogResult);
            }
        }
        finally
        {
            _closing = false;
        }
    }

    // ── WM-driven internal hooks ───────────────────────────────────────────────────────────────────

    /// <summary>Sets/clears the transient <c>:modal-attention</c> pseudo-class on this window's root (WM pulse, §8.6).</summary>
    internal void SetModalAttention(bool active) => SetInteractionState(InteractionState.ModalAttention, active);

    /// <summary>Flips <see cref="IsActive"/> and raises <see cref="Activated"/>/<see cref="Deactivated"/> (WM-driven).</summary>
    internal void SetActiveInternal(bool active)
    {
        if (IsActive == active)
            return;

        SetValue(IsActivePropertyKey, active);
        SetInteractionState(InteractionState.ActiveWindow, active); // wire :active-window (style-matrix §0.3; was never set)
        (active ? Activated : Deactivated)?.Invoke(this, EventArgs.Empty);
    }

    // ── chrome active-look (relocated out of the template closure so re-template never leaks) ───────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The themed chrome bands/ink track IsActive. A chrome-less template (WindowStyle.None) has no title
        // band → GetTemplatePart returns null and the active look degrades to nothing.
        if (GetTemplatePart<Border>("PART_TitleBar") is not { } titleBar)
            return;

        var closeButton = GetTemplatePart<Button>("PART_CloseButton");
        var maximizeButton = GetTemplatePart<Button>("PART_MaximizeButton");

        void ApplyActiveLook(bool active)
        {
            // Propagate :active-window pseudoclass to title bar elements so their
            // styles can trigger on it.
            titleBar.SetInteractionStateInternal(InteractionState.ActiveWindow, active);
            closeButton?.SetInteractionStateInternal(InteractionState.ActiveWindow, active);
            maximizeButton?.SetInteractionStateInternal(InteractionState.ActiveWindow, active);
        }

        ApplyActiveLook(IsActive); // resting look for the current state
        _chromeActivatedHandler = (_, _) => ApplyActiveLook(true);
        _chromeDeactivatedHandler = (_, _) => ApplyActiveLook(false);
        Activated += _chromeActivatedHandler;
        Deactivated += _chromeDeactivatedHandler;
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_chromeActivatedHandler is { } activated)
        {
            Activated -= activated;
            _chromeActivatedHandler = null;
        }

        if (_chromeDeactivatedHandler is { } deactivated)
        {
            Deactivated -= deactivated;
            _chromeDeactivatedHandler = null;
        }

        base.OnTemplateDetaching(old);
    }

    /// <summary>
    /// Event raised the first time the content of the window is fully rendered and ready for interaction.
    /// </summary>
    public event EventHandler<EventArgs>? ContentRendered; 
    
    /// <summary>Raises the <see cref="ContentRendered"/> event.</summary>
    internal void RaiseContentRendered()
    {
        ContentRendered?.Invoke(this, EventArgs.Empty);
    }
}