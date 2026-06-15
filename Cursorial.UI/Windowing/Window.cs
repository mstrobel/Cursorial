using System;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
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
public class Window : ContentControl
{
    /// <summary>The window title (<c>AffectsRender</c>; the active main window mirrors it to OSC 2 — §8.8, W5).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.Register<Window, string?>(nameof(Title));

    /// <summary>The chrome style (title bar vs chrome-less); both occlude.</summary>
    public static readonly StyledProperty<WindowStyle> WindowStyleProperty =
        UIProperty.Register<Window, WindowStyle>(nameof(WindowStyle));

    /// <summary>The window's drop shadow (rendered at W5; default <see cref="WindowShadow.None"/>).</summary>
    public static readonly StyledProperty<WindowShadow> ShadowProperty =
        UIProperty.Register<Window, WindowShadow>(nameof(Shadow));

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
        UIProperty.Register<Window, WindowState>(nameof(WindowState));

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

    /// <summary>A code-behind seam fired when a blocked window is poked (the theme flash rides §8.6's pulse, W2).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ModalAttentionEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(ModalAttention), RoutingStrategy.Bubble, typeof(Window));

    private Window? _owner;
    private bool _closing; // reentrancy guard
    private TaskCompletionSource<object?>? _dialogTcs;
    private CancellationToken _dialogCancellation;
    private CancellationTokenRegistration _dialogRegistration;

    static Window()
    {
        // S4 ships the interim chrome as the Template DEFAULT for Window (the weakest priority), so S8's
        // themed control theme (C4) overrides it at the higher ControlTheme layer (design doc §8.3).
        TemplateProperty.OverrideDefaultValue<Window>(InterimChromeTemplate);
        AffectsRender<Window>(TitleProperty);
        AffectsComposite<Window>(LeftProperty, TopProperty);
    }

    /// <summary>Creates a window.</summary>
    public Window() { }

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="WindowStyleProperty"/>
    public WindowStyle WindowStyle { get => GetValue(WindowStyleProperty); set => SetValue(WindowStyleProperty, value); }

    /// <inheritdoc cref="ShadowProperty"/>
    public WindowShadow Shadow { get => GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    /// <inheritdoc cref="LeftProperty"/>
    public int Left { get => GetValue(LeftProperty); set => SetValue(LeftProperty, value); }

    /// <inheritdoc cref="TopProperty"/>
    public int Top { get => GetValue(TopProperty); set => SetValue(TopProperty, value); }

    /// <inheritdoc cref="SizeToContentProperty"/>
    public SizeToContent SizeToContent { get => GetValue(SizeToContentProperty); set => SetValue(SizeToContentProperty, value); }

    /// <inheritdoc cref="WindowStateProperty"/>
    public WindowState WindowState { get => GetValue(WindowStateProperty); set => SetValue(WindowStateProperty, value); }

    /// <inheritdoc cref="WindowStartupLocationProperty"/>
    public WindowStartupLocation WindowStartupLocation { get => GetValue(WindowStartupLocationProperty); set => SetValue(WindowStartupLocationProperty, value); }

    /// <inheritdoc cref="CanMoveProperty"/>
    public bool CanMove { get => GetValue(CanMoveProperty); set => SetValue(CanMoveProperty, value); }

    /// <inheritdoc cref="CanResizeProperty"/>
    public bool CanResize { get => GetValue(CanResizeProperty); set => SetValue(CanResizeProperty, value); }

    /// <inheritdoc cref="CanCloseProperty"/>
    public bool CanClose { get => GetValue(CanCloseProperty); set => SetValue(CanCloseProperty, value); }

    /// <inheritdoc cref="IsActiveProperty"/>
    public bool IsActive => GetValue(IsActiveProperty);

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
    }

    /// <summary>Activates the window (brings it to the top of its band). Returns false when no manager hosts it (or it is modal-blocked).</summary>
    public bool Activate()
    {
        if (Manager is not { } manager)
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

        var manager = _owner?.Manager
                      ?? UIApplication.Current?.WindowManager
                      ?? throw new InvalidOperationException("No window manager is available to show the dialog.");

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;
        _dialogCancellation = cancellationToken;
        IsModal = true;
        Manager = manager;
        manager.ShowDialog(this);

        if (cancellationToken.CanBeCanceled)
        {
            // Close mutates WM/styling state — marshal it to the UI thread (never the canceling thread).
            _dialogRegistration = cancellationToken.Register(static self =>
            {
                var window = (Window)self!;
                UIApplication.Current?.Dispatcher.Post(() =>
                {
                    if (window.IsShown)
                        window.Close(WindowCloseReason.Programmatic);
                });
            }, this);
        }

        return tcs.Task;
    }

    /// <summary>The typed <see cref="ShowDialogAsync(CancellationToken)"/> — the result is the <see cref="DialogResult"/> cast to <typeparamref name="TResult"/>.</summary>
    public async Task<TResult?> ShowDialogAsync<TResult>(CancellationToken cancellationToken = default)
    {
        var result = await ShowDialogAsync(cancellationToken).ConfigureAwait(true);
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

            Manager!.CloseWindow(this);
            Manager = null;
            IsModal = false;
            SetActiveInternal(false);
            Closed?.Invoke(this, EventArgs.Empty);

            // Complete the modal task (if any): canceled when the close was cancellation-driven, else the result.
            _dialogRegistration.Dispose();
            if (_dialogTcs is { } tcs)
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

    /// <summary>Flips <see cref="IsActive"/> and raises <see cref="Activated"/>/<see cref="Deactivated"/> (WM-driven).</summary>
    internal void SetActiveInternal(bool active)
    {
        if (IsActive == active)
            return;
        SetValue(IsActivePropertyKey, active);
        (active ? Activated : Deactivated)?.Invoke(this, EventArgs.Empty);
    }

    // ── The interim occluding chrome template (design doc §8.2/§8.3) ─────────────────────────────────

    private static readonly ControlTemplate InterimChromeTemplate = new(BuildInterimChrome);

    private static UIElement BuildInterimChrome(TemplateBuildContext ctx)
    {
        var window = (Window)(ctx.TemplatedParent
                              ?? throw new InvalidOperationException("The window chrome template requires a templated parent."));

        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);

        // The occluding root: windows must OVERWRITE what they cover, not tint it (the drawing-core
        // occluding-panel idiom — FillOpaque). A default opaque surface fill until S8's themed brush (C4).
        var root = new Border
        {
            Occludes = true,
            Background = window.Background ?? DefaultSurfaceBrush,
        };

        if (window.WindowStyle == WindowStyle.None)
        {
            // Chrome-less but still opaque: just the occluding frame around the content.
            root.Child = presenter;
            return root;
        }

        // Title-bar style: a titled occluding box + a close-button row docked above the content.
        root.BorderPen = Pens.Light;
        root.Title = window.Title;

        var layout = new DockPanel();

        var titleBar = new DockPanel();
        WindowChrome.SetHitTestRole(titleBar, WindowHitTestRole.Drag); // drag-move interaction wires at W5

        var closeButton = new Button { Content = "✕", Focusable = false, IsTabStop = false };
        WindowChrome.SetHitTestRole(closeButton, WindowHitTestRole.Close);
        closeButton.Click += (_, _) =>
        {
            if (window.CanClose)
                window.Close(WindowCloseReason.ChromeAction);
        };
        DockPanel.SetDock(closeButton, Dock.Right);
        titleBar.Children.Add(closeButton);
        ctx.RegisterName("PART_TitleBar", titleBar);
        ctx.RegisterName("PART_CloseButton", closeButton);

        DockPanel.SetDock(titleBar, Dock.Top);
        layout.Children.Add(titleBar);
        layout.Children.Add(presenter); // fills the remainder

        root.Child = layout;
        return root;
    }

    private static readonly IBrush DefaultSurfaceBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
}
