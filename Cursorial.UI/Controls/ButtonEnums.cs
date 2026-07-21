using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>When a <see cref="ButtonBase"/> raises its click (design doc §12.7).</summary>
public enum ClickMode
{
    /// <summary>Click on the mouse/keyboard release over the button (the default — WPF <c>Release</c>).</summary>
    Release,

    /// <summary>Click on the mouse/keyboard press (<c>RepeatButton</c>'s default).</summary>
    Press,

    /// <summary>Click on pointer hover (no v1 control uses it; kept for parity).</summary>
    Hover
}

public enum InvokeMethod : byte
{
    /// <summary>A direct call to an invoke/toggle method or assignment to a toggle property.</summary>
    Programmatic,

    /// <summary>A pointer press.</summary>
    Pointer,

    /// <summary>A press of the 'Enter' key.</summary>
    KeyboardEnter,

    /// <summary>A press of the 'Space' key.</summary>
    KeyboardSpace,

    /// <summary>Access-key (mnemonic) activation.</summary>
    AccessKey
}

/// <summary>The args for <see cref="ButtonBase.ClickEvent"/> (a bubbling routed event, S8-owned — doc §12.7).</summary>
public sealed class ClickEventArgs : RoutedEventArgs
{
    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public ClickEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    public ClickEventArgs(RoutedEvent routedEvent, UIElement source) : base(routedEvent, source)
    {
    }
}
