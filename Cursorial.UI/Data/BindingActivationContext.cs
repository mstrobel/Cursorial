namespace Cursorial.UI.Data;

/// <summary>
/// The per-install context handed to <c>BindingBase.CreateExpression</c> (design doc §6.1) — the
/// target object, the target property (or the watch-only sentinel), the anchor element, an optional
/// host <see cref="ValueFrame"/> for frame-hosted installs, and the watch callback for watch-only
/// arming. An internal readonly struct; never escapes the engine.
/// </summary>
internal readonly struct BindingActivationContext
{
    public BindingActivationContext(
        UIObject target,
        UIProperty targetProperty,
        UIElement? anchor,
        ValueFrame? hostFrame,
        Action<object?>? watchCallback)
    {
        Target = target;
        TargetProperty = targetProperty;
        Anchor = anchor;
        HostFrame = hostFrame;
        WatchCallback = watchCallback;
    }

    /// <summary>The object whose property the binding produces into (or the watch anchor for watch-only).</summary>
    public UIObject Target { get; }

    /// <summary>The target property, or <see cref="UIProperty.UnsetTargetProperty"/> for watch-only.</summary>
    public UIProperty TargetProperty { get; }

    /// <summary>The element used to resolve the default-source DataContext / element-name / relative anchors.</summary>
    public UIElement? Anchor { get; }

    /// <summary>The host frame for frame-hosted installs; <see langword="null"/> for free-standing / watch-only.</summary>
    public ValueFrame? HostFrame { get; }

    /// <summary>The callback for watch-only arming; <see langword="null"/> for producing installs.</summary>
    public Action<object?>? WatchCallback { get; }

    /// <summary>Whether this is a watch-only arming (no store entry).</summary>
    public bool IsWatchOnly => WatchCallback is not null;
}
