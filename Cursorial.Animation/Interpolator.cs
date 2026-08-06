using Cursorial.Media;

namespace Cursorial.Animation;

/// <summary>
/// The process-global interpolator registry (design doc §9.11): maps a value type <c>T</c> to the
/// <see cref="IInterpolator{T}"/> the animation layer uses when a track/animation does not specify one.
/// </summary>
/// <remarks>
/// <para>
/// Pre-seeded with the <c>Cursorial.Animation</c> primitives (<see cref="double"/>, <see cref="int"/>,
/// <see cref="Color"/>); <c>Cursorial.Drawing</c> auto-registers its value types (<c>PointD</c>,
/// <c>Size</c>, <c>Rect</c>, <c>RelativePoint</c>, <c>IBrush</c>, <c>Pen</c>, <c>CompositeParameters</c>,
/// <c>Margins</c>) via a module initializer when the assembly loads, so they are available without app code.
/// Apps register their own value types with <see cref="Register{T}"/> at startup.
/// </para>
/// <para>
/// <b>Threading:</b> reads are lock-free over an immutable snapshot (a published dictionary is never
/// mutated — <see cref="Register{T}"/> swaps in a fresh copy under a write lock), so <see cref="For{T}"/>
/// is safe to call from the UI thread every frame while startup registration races resolve consistently.
/// </para>
/// </remarks>
public static class Interpolator
{
    private static readonly object WriteLock = new();
    private static Dictionary<Type, object> _registry = Seed();

    /// <summary>The interpolator registered for <typeparamref name="T"/>.</summary>
    /// <exception cref="System.InvalidOperationException">No interpolator is registered for <typeparamref name="T"/>.</exception>
    public static IInterpolator<T> For<T>()
        => TryGet<T>(out var interpolator)
            ? interpolator
            : throw new InvalidOperationException(
                $"No interpolator is registered for '{typeof(T)}'. Register one with " +
                $"Interpolator.Register<{typeof(T).Name}>(…) at startup, or pass an explicit interpolator to the animation/track.");

    /// <summary>The interpolator registered for <typeparamref name="T"/>, or <see langword="null"/> when none is.</summary>
    public static IInterpolator<T>? ForOrNull<T>()
        => TryGet<T>(out var interpolator) ? interpolator : null;

    /// <summary>Whether an interpolator is registered for <typeparamref name="T"/>.</summary>
    public static bool IsRegistered<T>() => Volatile.Read(ref _registry).ContainsKey(typeof(T));

    /// <summary>
    /// Registers (or replaces) the interpolator for <typeparamref name="T"/>. Intended at startup; the
    /// copy-on-write swap keeps concurrent <see cref="For{T}"/> reads consistent.
    /// </summary>
    public static void Register<T>(IInterpolator<T> interpolator)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        lock (WriteLock)
        {
            var next = new Dictionary<Type, object>(Volatile.Read(ref _registry)) { [typeof(T)] = interpolator };
            Volatile.Write(ref _registry, next);
        }
    }

    private static bool TryGet<T>(out IInterpolator<T> interpolator)
    {
        if (Volatile.Read(ref _registry).TryGetValue(typeof(T), out var found))
        {
            interpolator = (IInterpolator<T>)found;
            return true;
        }

        interpolator = null!;
        return false;
    }

    private static Dictionary<Type, object> Seed() => new()
    {
        [typeof(double)] = DoubleInterpolator.Instance,
        [typeof(int)] = Int32Interpolator.Instance,
        [typeof(Color)] = ColorInterpolator.Instance,
    };
}
