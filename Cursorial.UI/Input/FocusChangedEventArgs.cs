namespace Cursorial.UI.Input;

/// <summary>How keyboard focus arrived at an element — feeds the <c>:focus-visible</c> policy (design doc §7.7).</summary>
public enum FocusNavigationMethod : byte
{
    /// <summary>A direct <see cref="UIElement.Focus"/> call (focus visual shown only under keyboard modality).</summary>
    Programmatic,

    /// <summary>A pointer press (no focus visual).</summary>
    Pointer,

    /// <summary>Tab / Shift+Tab navigation.</summary>
    Tab,

    /// <summary>Arrow-key directional navigation.</summary>
    Directional,

    /// <summary>Access-key (mnemonic) activation.</summary>
    AccessKey,

    /// <summary>Window-activation scope-memory restore (always shows the focus visual — recorded divergence from Chrome heuristics).</summary>
    Restore,
}

/// <summary>Args for the <c>GotFocus</c>/<c>LostFocus</c> events (design doc §7.2). State commits before these raise.</summary>
public sealed class FocusChangedEventArgs : RoutedEventArgs
{
    private UIElement? _oldFocus;
    private UIElement? _newFocus;
    private FocusNavigationMethod _method;

    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public FocusChangedEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args.</summary>
    /// <param name="routedEvent">The focus event to raise.</param>
    /// <param name="source">The dispatch target.</param>
    /// <param name="oldFocus">The previously focused element.</param>
    /// <param name="newFocus">The newly focused element.</param>
    /// <param name="method">How focus moved.</param>
    public FocusChangedEventArgs(
        RoutedEvent<FocusChangedEventArgs> routedEvent,
        UIElement source,
        UIElement? oldFocus,
        UIElement? newFocus,
        FocusNavigationMethod method)
        : base(routedEvent, source)
    {
        _oldFocus = oldFocus;
        _newFocus = newFocus;
        _method = method;
    }

    /// <summary>The element losing focus (null when focus was empty).</summary>
    public UIElement? OldFocus
    {
        get
        {
            ThrowIfStale();
            return _oldFocus;
        }
    }

    /// <summary>The element gaining focus (null on a clear).</summary>
    public UIElement? NewFocus
    {
        get
        {
            ThrowIfStale();
            return _newFocus;
        }
    }

    /// <summary>How focus moved.</summary>
    public FocusNavigationMethod Method
    {
        get
        {
            ThrowIfStale();
            return _method;
        }
    }

    /// <summary>Framework-dispatch payload initialization.</summary>
    internal void InitializeFocus(UIElement? oldFocus, UIElement? newFocus, FocusNavigationMethod method)
    {
        _oldFocus = oldFocus;
        _newFocus = newFocus;
        _method = method;
    }

    /// <inheritdoc/>
    internal override void ResetPooledState()
    {
        _oldFocus = null;
        _newFocus = null;
        _method = FocusNavigationMethod.Programmatic;
    }
}
