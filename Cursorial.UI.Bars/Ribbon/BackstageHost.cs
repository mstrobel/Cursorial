using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// The batteries-included host that opens a <see cref="Backstage"/> the way its <see cref="Backstage.DisplayMode"/>
/// asks — so an app can wire the Ribbon's <see cref="Ribbon.BackstageRequestedEvent"/> straight to
/// <see cref="ShowAsync"/> without hand-rolling the Window/Popup plumbing. <see cref="BackstageDisplayMode.FullScreen"/>
/// ⇒ a chrome-less MAXIMIZED modal <see cref="Window"/> (the Office 2010 File screen); <see cref="BackstageDisplayMode.Menu"/>
/// ⇒ a File-anchored, light-dismiss <see cref="Popup"/> (the Office 2007 dropdown). Either way the <c>◂</c> back button
/// and Escape (the <see cref="Backstage.BackRequestedEvent"/>) close the surface; the returned <see cref="Task"/>
/// completes on dismissal (fire-and-forget is fine).
/// <para>The Backstage does not host itself (an app is free to host it any other way — this is only the convenience
/// path). The hosted Backstage is released from the surface on close, so the same instance can be re-shown.</para>
/// </summary>
public static class BackstageHost
{
    /// <summary>Opens <paramref name="backstage"/> over <paramref name="anchor"/> per its
    /// <see cref="Backstage.DisplayMode"/> and completes when it is dismissed (◂ / Escape / light-dismiss).</summary>
    /// <param name="backstage">The File view to show.</param>
    /// <param name="anchor">The element the Menu-mode popup anchors under (typically the ribbon or its File tab). The
    /// FullScreen surface ignores it (it takes the whole viewport).</param>
    public static Task ShowAsync(Backstage backstage, UIElement anchor)
    {
        ArgumentNullException.ThrowIfNull(backstage);
        ArgumentNullException.ThrowIfNull(anchor);

        return backstage.DisplayMode == BackstageDisplayMode.Menu
            ? ShowMenu(backstage, anchor)
            : ShowFullScreen(backstage);
    }

    // FullScreen: a chrome-less, maximized modal window that takes over the whole terminal. Back/Escape closes it;
    // the running frame loop is the modal pump (ShowDialogAsync — no nested loop). Completion + cleanup ride the
    // window's Closed event (which fires on the UI thread), NOT the ShowDialogAsync await continuation — an awaited
    // continuation would resume on whatever SynchronizationContext the caller happened to have (often none), and the
    // cleanup touches UI objects (invariant 6). This mirrors the Menu path's Popup.Closed handling.
    private static Task ShowFullScreen(Backstage backstage)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Content = backstage,
        };

        void OnBack(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            window.Close();
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            backstage.BackRequested -= OnBack;
            window.Content = null; // release so the same Backstage can be re-shown
            tcs.TrySetResult();
        }

        backstage.BackRequested += OnBack;
        window.Closed += OnClosed;
        _ = window.ShowDialogAsync(); // modal; the frame loop is the pump. Closed drives our completion, not this task.
        return tcs.Task;
    }

    // Menu: a File-anchored, light-dismiss popup (the compact Office 2007 form). Back/Escape closes it; a click-away
    // light-dismiss closes it too. Either path fires Popup.Closed, which completes the task and detaches the Backstage.
    private static Task ShowMenu(Backstage backstage, UIElement anchor)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Child = backstage,
        };

        void OnBack(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            popup.Close();
        }

        void OnClosed(object? sender, PopupClosedEventArgs e)
        {
            popup.Closed -= OnClosed;
            backstage.BackRequested -= OnBack;
            popup.Child = null; // release so the same Backstage can be re-shown
            tcs.TrySetResult();
        }

        backstage.BackRequested += OnBack;
        popup.Closed += OnClosed;
        popup.Open();
        return tcs.Task;
    }
}
