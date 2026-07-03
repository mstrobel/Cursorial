namespace Cursorial.UI.Input;

/// <summary>
/// The per-concrete-type routed-event args free-list (design doc §7.2). Per-UI-thread
/// (<see cref="ThreadStaticAttribute"/>) so parallel test hosts never share instances; a nested
/// <c>RaiseEvent</c> simply rents a distinct instance because the outer one has not been returned.
/// Steady-state dispatch is allocation-free once each type's pool is warm.
/// </summary>
internal static class EventArgsPool<TArgs>
    where TArgs : RoutedEventArgs, new()
{
    private const int MaxPooled = 16; // bounded by dispatch nesting depth (doc §7.2 debug cap)

    [ThreadStatic]
    private static Stack<TArgs>? _pool;

    private static readonly Action<RoutedEventArgs> ReturnDelegate = static args => Return((TArgs)args);

    /// <summary>Rents an instance (allocating only when the free-list is empty) and re-arms it.</summary>
    internal static TArgs Rent()
    {
        var pool = _pool ??= new Stack<TArgs>();
        var args = pool.Count > 0 ? pool.Pop() : new TArgs();
        args.OnRented(ReturnDelegate);
        return args;
    }

    private static void Return(TArgs args)
    {
        var pool = _pool ??= new Stack<TArgs>();
        if (pool.Count < MaxPooled)
            pool.Push(args);
    }
}
