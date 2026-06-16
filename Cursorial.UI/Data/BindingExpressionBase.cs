namespace Cursorial.UI.Data;

/// <summary>
/// The runtime machinery for one armed binding on one target (design doc §6.2). Subclasses are the
/// reflection lane (<see cref="ReflectionBindingExpression"/>) and, at B2, the compiled lane. The
/// base owns the public read surface, the lane classification, and the disposal flags.
/// </summary>
public abstract class BindingExpressionBase : IDisposable
{
    /// <summary>The disposal-state flags (BD18).</summary>
    [Flags]
    private protected enum ExpressionFlags : byte
    {
        None = 0,
        Disposing = 1 << 0,
        Disposed = 1 << 1
    }

    private protected ExpressionFlags Flags;

    /// <summary>The descriptor this expression was built from.</summary>
    public abstract BindingBase ParentBinding { get; }

    /// <summary>The object whose property this expression produces into (or the watch anchor).</summary>
    public abstract UIObject Target { get; }

    /// <summary>The target property, or <see cref="UIProperty.UnsetTargetProperty"/> for watch-only.</summary>
    public abstract UIProperty TargetProperty { get; }

    /// <summary>The expression's lifecycle status.</summary>
    public BindingStatus Status { get; private protected set; } = BindingStatus.Inactive;

    /// <summary>The resolved binding mode (<see cref="BindingMode.Default"/> resolved via metadata).</summary>
    public abstract BindingMode EffectiveMode { get; }

    /// <summary>The registry lane this expression occupies.</summary>
    internal abstract BindingLane Lane { get; }

    /// <summary>Whether this expression has been disposed.</summary>
    public bool IsDisposed => (Flags & ExpressionFlags.Disposed) != 0;

    /// <summary>Forces a re-read of the source into the target.</summary>
    public abstract void UpdateTarget();

    /// <summary>Flushes the target value to the source (the <c>Explicit</c> trigger; also forces a flush otherwise).</summary>
    public abstract void UpdateSource();

    /// <summary>Detaches the expression: unsubscribes everything, disposes the entry. Idempotent, reentrancy-safe.</summary>
    public void Dispose() => Dispose(fromEviction: false);

    /// <summary>The eviction-aware disposal (BD18): an eviction-initiated dispose skips <c>entry.Dispose()</c>.</summary>
    internal void Dispose(bool fromEviction)
    {
        if ((Flags & (ExpressionFlags.Disposing | ExpressionFlags.Disposed)) != 0)
            return;

        Flags |= ExpressionFlags.Disposing;
        try
        {
            DisposeCore(fromEviction);
        }
        finally
        {
            Flags = (Flags & ~ExpressionFlags.Disposing) | ExpressionFlags.Disposed;
            Status = BindingStatus.Detached;
        }
    }

    /// <summary>Builds the diagnostics line for this expression (design doc §6.10).</summary>
    internal abstract BindingExpressionExplanation Explain();

    /// <summary>Subclass disposal: unsubscribe, dispose the entry (unless <paramref name="fromEviction"/>), unregister.</summary>
    private protected abstract void DisposeCore(bool fromEviction);
}
