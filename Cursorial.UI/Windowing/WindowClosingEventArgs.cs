

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The argument to <c>Window.Closing</c> (design doc §8.2): carries why the window is closing and lets a
/// handler veto a <see cref="CanCancel"/> close by setting <see cref="Cancel"/>. Manager-driven closes
/// (<see cref="WindowCloseReason.OwnerClosed"/>, <see cref="WindowCloseReason.ManagerShutdown"/>) are not
/// cancelable — <see cref="CanCancel"/> is <see langword="false"/> and <see cref="Cancel"/> is ignored.
/// </summary>
public sealed class WindowClosingEventArgs : EventArgs
{
    /// <summary>Creates the args for the given close reason.</summary>
    public WindowClosingEventArgs(WindowCloseReason reason)
    {
        Reason = reason;
        CanCancel = reason is WindowCloseReason.Programmatic or WindowCloseReason.ChromeAction;
    }

    /// <summary>Why the window is closing.</summary>
    public WindowCloseReason Reason { get; }

    /// <summary>Whether a handler may veto this close (true only for <see cref="WindowCloseReason.Programmatic"/>/<see cref="WindowCloseReason.ChromeAction"/>).</summary>
    public bool CanCancel { get; }

    /// <summary>Set to <see langword="true"/> to veto a cancelable close; ignored when <see cref="CanCancel"/> is <see langword="false"/>.</summary>
    public bool Cancel { get; set; }
}
