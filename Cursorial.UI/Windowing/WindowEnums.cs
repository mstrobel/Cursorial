using System;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>A window's sizing/maximization state (design doc §8.2). <c>Minimized</c> is deferred.</summary>
public enum WindowState : byte
{
    /// <summary>A normal, freely positioned/sized window.</summary>
    Normal,

    /// <summary>Maximized to fill the screen (re-sized on viewport resize).</summary>
    Maximized,
}

/// <summary>Auto-sizing policy for a window's realized size (design doc §8.2). Default <see cref="WidthAndHeight"/>.</summary>
public enum SizeToContent : byte
{
    /// <summary>Explicit size; content does not drive it.</summary>
    Manual,

    /// <summary>Width tracks content; height explicit.</summary>
    Width,

    /// <summary>Height tracks content; width explicit.</summary>
    Height,

    /// <summary>Both dimensions track content.</summary>
    WidthAndHeight,
}

/// <summary>Window chrome selection (design doc §8.2).</summary>
public enum WindowStyle : byte
{
    /// <summary>The default chrome: a title bar (drag/maximize/close) + content + resize grip.</summary>
    TitleBar,

    /// <summary>Chrome-less but still opaque (occluding) — a bare content frame.</summary>
    None,
}

/// <summary>Where a window first appears when shown (design doc §8.2).</summary>
public enum WindowStartupLocation : byte
{
    /// <summary>At the window's explicit <c>Left</c>/<c>Top</c>.</summary>
    Manual,

    /// <summary>Centered on the screen.</summary>
    CenterScreen,

    /// <summary>Centered over the owner window (falls back to the screen when there is no owner).</summary>
    CenterOwner,
}

/// <summary>Why a window is closing — surfaced on <c>WindowClosingEventArgs</c> (design doc §8.2/§8.6/§8.8).</summary>
public enum WindowCloseReason : byte
{
    /// <summary>An explicit <c>Close()</c> call (cancelable).</summary>
    Programmatic,

    /// <summary>A chrome action (the ✕ button) (cancelable).</summary>
    ChromeAction,

    /// <summary>The owner window closed, cascading to this owned window (not cancelable).</summary>
    OwnerClosed,

    /// <summary>The window manager is shutting down — <c>CloseAllAsync</c> (not cancelable).</summary>
    ManagerShutdown,
}

/// <summary>Where a <c>Popup</c> places its surface relative to its placement target (design doc §8.4). Default <see cref="Bottom"/>.</summary>
public enum PlacementMode : byte
{
    /// <summary>Below the target's bottom edge, left-aligned.</summary>
    Bottom,

    /// <summary>Above the target's top edge, left-aligned.</summary>
    Top,

    /// <summary>To the right of the target's right edge, top-aligned.</summary>
    Right,

    /// <summary>To the left of the target's left edge, top-aligned.</summary>
    Left,

    /// <summary>Centered over the target.</summary>
    Center,

    /// <summary>At the last pointer position.</summary>
    Pointer,
}

/// <summary>Why a <c>Popup</c> closed — surfaced on <c>PopupClosedEventArgs</c> (design doc §8.4).</summary>
public enum PopupCloseReason : byte
{
    /// <summary>An explicit <c>Close()</c> / <c>IsOpen = false</c>.</summary>
    Programmatic,

    /// <summary>An uncaptured press landed outside the popup (and its chain).</summary>
    LightDismiss,

    /// <summary>The Escape key (when <c>CloseOnEscape</c>).</summary>
    EscapeKey,

    /// <summary>The host window was deactivated.</summary>
    HostDeactivated,

    /// <summary>The host window became modal-blocked.</summary>
    HostBlocked,

    /// <summary>The host window closed.</summary>
    HostClosed,

    /// <summary>The screen was resized (a light-dismiss popup; <c>StaysOpen</c> popups re-place instead).</summary>
    ScreenResized,
}

/// <summary>
/// The behavior a chrome part requests of its host window via <c>WindowChrome.HitTestRole</c> (design doc
/// §8.3). The window interprets bubbling <c>ButtonDown</c>/drag on a part by its role — never by part name.
/// </summary>
[Flags]
public enum WindowHitTestRole : byte
{
    /// <summary>No window role (ordinary content).</summary>
    None = 0,

    /// <summary>Dragging the part moves the window (the title bar).</summary>
    Drag = 1,

    /// <summary>Activating the part closes the window (the ✕ button).</summary>
    Close = 2,

    /// <summary>Activating the part toggles <see cref="WindowState.Maximized"/>.</summary>
    Maximize = 4,

    /// <summary>Dragging the part resizes the window from the lower-right corner (the grip).</summary>
    ResizeSE = 8,
}
