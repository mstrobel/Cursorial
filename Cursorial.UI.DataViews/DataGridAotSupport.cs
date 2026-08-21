using System.Diagnostics.CodeAnalysis;

using Cursorial.UI.DataViews.Shaping;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The Native-AOT seeding seam for <see cref="DataGrid"/>'s shaping engine (the counterpart of the
/// <c>RequiresDynamicCode</c> annotation on <see cref="DataGrid"/>): the engine closes generics —
/// <c>ShapedColumn&lt;TRow,TKey&gt;</c>, typed expression lambdas, formatters, comparers,
/// aggregators — over the ROW type at runtime, and ILC only ships instantiations it can see
/// statically. Reference-typed keys ride shared code; every VALUE-typed key (numeric, date, enum)
/// needs its instantiation seeded. An AOT app calls <see cref="Seed{TRow}"/> once per row type
/// (plus <see cref="Seed{TRow,TKey}"/> per enum/exotic key) — the calls are no-ops at runtime; the
/// STATIC references are what matter.
/// </summary>
public static class DataGridAotSupport
{
    /// <summary>Seeds the common key types for <typeparamref name="TRow"/> (strings ride shared code
    /// but are included for the typed side channels; numerics, dates, and their nullables).</summary>
    public static void Seed<TRow>() where TRow : notnull
    {
        Seed<TRow, string>();
        Seed<TRow, int>(); Seed<TRow, int?>();
        Seed<TRow, long>(); Seed<TRow, long?>();
        Seed<TRow, double>(); Seed<TRow, double?>();
        Seed<TRow, decimal>(); Seed<TRow, decimal?>();
        Seed<TRow, bool>(); Seed<TRow, bool?>();
        Seed<TRow, DateTime>(); Seed<TRow, DateTime?>();
        Seed<TRow, DateOnly>(); Seed<TRow, DateOnly?>();
        Seed<TRow, TimeSpan>(); Seed<TRow, TimeSpan?>();
    }

    /// <summary>Seeds one (row, key) pairing — needed explicitly for enum keys and any key type
    /// outside the common set.</summary>
    public static void Seed<TRow, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors)] TKey>() where TRow : notnull
    {
        ShapingCodegen.SeedAot<TRow, TKey>();
        ColumnAggregator.SeedAot<TRow, TKey>();
    }
}
