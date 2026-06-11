using System.Runtime.CompilerServices;

namespace Cursorial.UI;

/// <summary>
/// The untyped lane's box-interning helper (matrix M267 mechanism): well-known values — booleans,
/// small integers, enum zeros — box to process-shared singletons, so the untyped read/observe
/// surfaces don't drizzle gen0 garbage for the most common property values. Box <em>identity</em>
/// is an allocation behavior, never a semantic contract (matrix M225/M228).
/// </summary>
internal static class ValueBoxes
{
    private const int SmallIntMin = -1;
    private const int SmallIntCount = 130; // −1 … 128: grid coordinates, indices, small sizes

    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;
    private static readonly object[] SmallInts = CreateSmallInts();

    /// <summary>
    /// Boxes <paramref name="value"/>, returning an interned singleton for booleans, integers in
    /// the small range, and enum zero values. Reference-typed <typeparamref name="T"/> passes
    /// through unboxed (identity).
    /// </summary>
    public static object? Box<T>(T value)
    {
        if (typeof(T) == typeof(bool))
            return Unsafe.As<T, bool>(ref value) ? BoxedTrue : BoxedFalse;

        if (typeof(T) == typeof(int))
        {
            var i = Unsafe.As<T, int>(ref value);
            if ((uint)(i - SmallIntMin) < SmallIntCount)
                return SmallInts[i - SmallIntMin];
        }
        else if (typeof(T).IsValueType && ZeroBox<T>.Box is { } zero && EqualityComparer<T>.Default.Equals(value, default!))
        {
            return zero;
        }

        return value;
    }

    private static object[] CreateSmallInts()
    {
        var boxes = new object[SmallIntCount];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = SmallIntMin + i;
        return boxes;
    }

    /// <summary>The per-enum-type zero box (<see langword="null"/> for non-enum types).</summary>
    private static class ZeroBox<T>
    {
        public static readonly object? Box = typeof(T).IsEnum ? default(T) : null;
    }
}
