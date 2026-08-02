using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The integer-cell layout arithmetic contract (design doc §5.2). All layout arithmetic goes
/// through these helpers (never raw <c>+</c>) so <see cref="Unbounded"/> ± margin can never
/// overflow: <see cref="Add(int,int)"/>/<see cref="Sub(int,int)"/> saturate, <see cref="Unbounded"/>
/// absorbs, and results stay within <c>[0, Unbounded]</c>. Render-time arithmetic on already-finite
/// sizes may use raw ints.
/// </summary>
public static class LayoutMath
{
    /// <summary>The measure-constraint "infinity" — the only encoding (replaces WPF's <c>double.PositiveInfinity</c>).</summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// The hard ceiling for any arrange extent. Arrange extents clamp to <c>[0, MaxExtent]</c> and positions to
    /// <c>[−MaxExtent, MaxExtent]</c> (signed origins, LD19) before <c>LayoutRect</c> construction (with a DEBUG
    /// diagnostic) so a misbehaving panel can never detonate a constructor or overflow downstream arithmetic.
    /// <para>
    /// <b>Decoupled from <see cref="Rect.MaxDimension"/>:</b> the <c>Rect</c> geometry type is <see cref="int"/>
    /// -backed and holds the full range, but the LAYOUT cap is deliberately <c>int.MaxValue / 2</c> — it must stay
    /// strictly below the <see cref="Unbounded"/> sentinel (<see cref="int.MaxValue"/>) so a real extent is never
    /// mistaken for "infinity", and it bounds positions/extents so a layout-produced <c>Rect</c>'s
    /// <c>edge + extent</c> (e.g. <see cref="Rect.RowEnd"/>) can never overflow <see cref="int"/>. Half the range
    /// (≈ 1.07 billion cells) satisfies both and is effectively unbounded for any terminal surface.
    /// </para>
    /// </summary>
    public const int MaxExtent = int.MaxValue / 2;

    /// <summary>Whether <paramref name="value"/> is the <see cref="Unbounded"/> encoding.</summary>
    public static bool IsUnbounded(int value) => value == Unbounded;

    /// <summary>Whether <paramref name="value"/> is the <see cref="Unbounded"/> encoding.</summary>
    public static bool IsUnbounded(Size value) => value is { Rows: Unbounded, Columns: Unbounded };

    /// <summary>Whether <paramref name="value"/> is the <see cref="Unbounded"/> encoding.</summary>
    public static bool IsUnbounded(Rect value) => IsUnbounded(value.Size);

    /// <summary>
    /// Whether <paramref name="value"/> is both bounded and non-empty, e.g., neither zero nor infinitely sized. 
    /// </summary>
    public static bool IsBoundedNonEmpty(Size? value) =>
        value is { Rows: > 0 and < Unbounded, Columns: > 0 and < Unbounded };

    /// <summary>
    /// Whether <paramref name="value"/> is both bounded and non-empty, e.g., neither zero nor infinitely sized. 
    /// </summary>
    public static bool IsBoundedNonEmpty(Rect? value) => IsBoundedNonEmpty(value?.Size);

    /// <summary>
    /// Saturating add: <see cref="Unbounded"/> absorbs; a finite overflow becomes
    /// <see cref="Unbounded"/>; results floor at 0 (matrix LD18). <paramref name="b"/> may be
    /// <b>negative</b> (signed margins, LD19) — the 0 floor is exactly the
    /// <c>DesiredSize = content + margin</c> clamp.
    /// </summary>
    public static int Add(int a, int b)
    {
        if (a == Unbounded || b == Unbounded)
            return Unbounded;

        return (int)Math.Clamp((long)a + b, 0, Unbounded);
    }

    /// <summary>
    /// Saturating subtract: <see cref="Unbounded"/> absorbs on the left
    /// (<c>Unbounded − anything = Unbounded</c>); <c>finite − Unbounded = 0</c>; results floor at 0
    /// (matrix LD18). <paramref name="b"/> may be <b>negative</b> (signed margins, LD19) — the
    /// margin-deflate then <em>enlarges</em>, saturating into <c>[0, Unbounded]</c> (a pathological
    /// enlargement becomes <see cref="Unbounded"/> and behaves as such from there).
    /// </summary>
    public static int Sub(int a, int b)
    {
        if (a == Unbounded)
            return Unbounded;
        if (b == Unbounded)
            return 0;

        return (int)Math.Clamp((long)a - b, 0, Unbounded);
    }

    /// <summary>Per-axis saturating <see cref="Add(int,int)"/> of a size and a margin's combined thickness (signed — LD19).</summary>
    public static Size Add(Size size, Margins margins)
        => new(Add(size.Columns, margins.Horizontal), Add(size.Rows, margins.Vertical));

    /// <summary>Per-axis saturating <see cref="Sub(int,int)"/> of a margin's combined thickness from a size (signed — a negative sum enlarges, LD19).</summary>
    public static Size Sub(Size size, Margins margins)
        => new(Sub(size.Columns, margins.Horizontal), Sub(size.Rows, margins.Vertical));

    /// <summary>
    /// <c>Max(min, Min(value, max))</c> — min is applied <em>last</em>, so min wins a min &gt; max
    /// conflict (the WPF <c>MinMax</c> shape; LD1 depends on this — <c>Math.Clamp</c> would throw).
    /// </summary>
    /// <remarks>
    /// <c>min &gt; max</c> is a <b>legal input here by design</b>, not a misconfiguration to
    /// detect: LD1's resolve produces it every measure pass when an element sets
    /// <c>MinWidth &gt; MaxWidth</c> (WPF resolves the same conflict silently, min-wins), so no
    /// DEBUG diagnostic is emitted — it would fire on spec-mandated arithmetic at frame rate.
    /// </remarks>
    public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    /// <summary>
    /// The centering offset: <c>Max(0, slot − size) / 2</c> — floor, so the spare cell goes
    /// right/bottom; never negative (overflowing content pins to the leading edge).
    /// </summary>
    public static int CenterOffset(int slot, int size) => Math.Max(0, slot - size) / 2;
}

/// <summary>Cross-subsystem layout limits (design doc §5.2) — one named constant per cap.</summary>
public static class LayoutLimits
{
    /// <summary>
    /// The scroll-extent cap per axis: <c>ScrollContentPresenter</c> measures its content with this
    /// (not <see cref="LayoutMath.Unbounded"/>) on scrollable axes and clamps the published extent
    /// to it (doc §5.7 / §12; matrix L202/L215).
    /// </summary>
    public const int MaxScrollExtent = 32_000;
}
