// ReSharper disable once CheckNamespace
namespace Cursorial.UI;

/// <summary>How the application occupies the terminal (design doc §3.1).</summary>
public enum ApplicationModel
{
    /// <summary>The whole screen, on the alternate buffer when the terminal has one.</summary>
    FullScreen,

    /// <summary>An inline region at the shell cursor, sized to content; the screen stays the shell's.</summary>
    Inline,

    /// <summary>
    /// Inline until a <b>window</b> opens (MessageBox, TaskDialog, a tool window — anything the
    /// <see cref="WindowManager"/> tracks as a window), fullscreen on the alternate buffer
    /// until the last window closes, then back to the inline region. Popups (drop-downs, completion,
    /// context menus) never escalate: <c>window ⇒ escalate, popup ⇒ inline</c>.
    /// <see cref="UIApplication.IsPresentingInline"/> reports the live side; the
    /// <c>app-inline</c>/<c>app-fullscreen</c> stamps flip with it.
    /// </summary>
    InlineWithSwitching,
}
