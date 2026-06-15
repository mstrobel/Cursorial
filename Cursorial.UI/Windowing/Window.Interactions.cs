using System;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

public partial class Window
{
    // Chrome interactions (design doc §8.3/§8.7) — the window interprets bubbling left-button gestures on
    // its chrome by WindowHitTestRole, never by part name. Move/resize are screen-anchored at press
    // (delta = screenNow − screenAtPress applied to the press-time snapshot — surface-local deltas are
    // forbidden because the origin moves under the gesture). The window captures the mouse for the gesture's
    // duration, so motion outside its own bounds still drives the drag (and bypasses the WM, §8.6).
    private bool _isMoving;
    private bool _isResizing;
    private CellPosition _gestureAnchor;     // screen position at press
    private (int Left, int Top) _moveOrigin; // Left/Top at press
    private Size _resizeOrigin;              // ActualSize at press
    private (int Left, int Top, int? Width, int? Height)? _restoreBounds; // pre-maximize bounds (property values)

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // Innermost role wins (the ✕ button's Close beats the title bar's Drag); Close is the button's own
        // Click handler, so it is intentionally not a case here.
        switch (ResolveHitTestRole(e.OriginalSource))
        {
            case WindowHitTestRole.Drag when e.ClickCount >= 2 && CanResize:
                ToggleMaximize(); // double-click the title bar maximizes / restores
                e.Handled = true;
                break;

            case WindowHitTestRole.Drag when CanMove && WindowState == WindowState.Normal:
                BeginGesture(e, resizing: false);
                e.Handled = true;
                break;

            case WindowHitTestRole.Maximize when CanResize:
                ToggleMaximize();
                e.Handled = true;
                break;

            case WindowHitTestRole.ResizeSE when CanResize && WindowState == WindowState.Normal:
                BeginGesture(e, resizing: true);
                e.Handled = true;
                break;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isMoving && !_isResizing)
            return;

        var screen = e.ScreenPosition;
        var deltaColumn = screen.Column - _gestureAnchor.Column;
        var deltaRow = screen.Row - _gestureAnchor.Row;

        if (_isMoving)
        {
            var (left, top) = Manager?.ClampPosition(_moveOrigin.Left + deltaColumn, _moveOrigin.Top + deltaRow, ActualSize)
                              ?? (_moveOrigin.Left + deltaColumn, _moveOrigin.Top + deltaRow);
            SetCurrentValue(LeftProperty, left); // user-gesture write: bindings survive (§8.2)
            SetCurrentValue(TopProperty, top);
        }
        else // _isResizing
        {
            var size = Manager?.ClampSize(_resizeOrigin.Columns + deltaColumn, _resizeOrigin.Rows + deltaRow)
                       ?? new Size(_resizeOrigin.Columns + deltaColumn, _resizeOrigin.Rows + deltaRow);
            SetCurrentValue(WidthProperty, (int?)size.Columns);
            SetCurrentValue(HeightProperty, (int?)size.Rows);
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if ((_isMoving || _isResizing) && e.Button == MouseButton.Left)
        {
            EndGesture();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // A forced release (e.g. a modal blocking this window mid-drag — §8.6) ends the gesture cleanly.
        _isMoving = false;
        _isResizing = false;
    }

    private void BeginGesture(MouseButtonEventArgs e, bool resizing)
    {
        if (!CaptureMouse())
            return;

        _isMoving = !resizing;
        _isResizing = resizing;
        _gestureAnchor = e.ScreenPosition;
        _moveOrigin = (Left, Top);
        _resizeOrigin = ActualSize;
    }

    private void EndGesture()
    {
        _isMoving = false;
        _isResizing = false;
        ReleaseMouseCapture();
    }

    /// <summary>Toggles between <see cref="WindowState.Normal"/> and <see cref="WindowState.Maximized"/>,
    /// snapshotting the bounds to restore (the WM's <c>SyncSurfaceSize</c> applies the new size next frame).</summary>
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (_restoreBounds is { } restore)
            {
                _restoreBounds = null;
                SetCurrentValue(LeftProperty, restore.Left);
                SetCurrentValue(TopProperty, restore.Top);
                SetCurrentValue(WidthProperty, restore.Width);
                SetCurrentValue(HeightProperty, restore.Height);
            }
        }
        else
        {
            _restoreBounds = (Left, Top, Width, Height);
            WindowState = WindowState.Maximized;
            SetCurrentValue(LeftProperty, 0); // maximized fills from the origin …
            SetCurrentValue(TopProperty, 0);
            // … and the element stretches to the whole surface — its explicit Width/Height would otherwise
            // pin it inside the maximized surface (the WM sizes the surface to the viewport regardless).
            SetCurrentValue(WidthProperty, (int?)null);
            SetCurrentValue(HeightProperty, (int?)null);
        }
    }

    /// <summary>
    /// Moves — and, only if larger than the viewport, shrinks — this window so it sits fully inside the
    /// screen (§8.7). The deliberate, on-demand counterpart to the resize-time clamp (which never shrinks):
    /// a temporary terminal resize leaves the window's size intact, and the user invokes this when they want
    /// it refit. No-op while not shown or <see cref="WindowState.Maximized"/>.
    /// </summary>
    public void FitToViewport()
    {
        if (Manager is not { } manager || WindowState == WindowState.Maximized)
            return;

        var screen = manager.ScreenSize;
        var width = ActualSize.Columns;
        var height = ActualSize.Rows;

        if (CanResize) // shrink to fit only when oversized — never enlarge
        {
            if (width > screen.Columns)
            {
                SetCurrentValue(WidthProperty, (int?)screen.Columns);
                width = screen.Columns;
            }

            if (height > screen.Rows)
            {
                SetCurrentValue(HeightProperty, (int?)screen.Rows);
                height = screen.Rows;
            }
        }

        SetCurrentValue(LeftProperty, Math.Clamp(Left, 0, Math.Max(0, screen.Columns - width)));
        SetCurrentValue(TopProperty, Math.Clamp(Top, 0, Math.Max(0, screen.Rows - height)));
    }

    /// <summary>Walks visual parents from the hit element to the nearest chrome role (innermost wins; §8.3).</summary>
    private static WindowHitTestRole ResolveHitTestRole(UIElement? source)
    {
        for (var element = source; element is not null; element = element.VisualParent)
        {
            var role = WindowChrome.GetHitTestRole(element);
            if (role != WindowHitTestRole.None)
                return role;
        }

        return WindowHitTestRole.None;
    }
}
