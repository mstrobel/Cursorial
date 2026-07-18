using System.Globalization;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The §2.2 span-formatter panel amendment: <see cref="ShapingCodegen.CreateSpanFormatter{TKey}"/>
/// builds the band cache's allocation-free per-cell lane — <see cref="ISpanFormattable"/> keys
/// (incl. <see cref="Nullable{T}"/>, null → 0 chars) format via a constrained call, strings copy,
/// −1 signals a too-small destination (caller grows), non-span-formattable keys fall back to
/// ToString-then-copy (documented cold), and the hot lanes hold the 0 B steady-state contract.
/// </summary>
public class SpanFormatterTests
{
    [Fact]
    public void Decimal_formats_with_format_and_culture()
    {
        var format = ShapingCodegen.CreateSpanFormatter<decimal>("$#,##0", CultureInfo.InvariantCulture);
        Span<char> buffer = stackalloc char[32];

        int written = format(12450m, buffer);
        Assert.Equal("$12,450", buffer[..written].ToString());
    }

    [Fact]
    public void Nullable_double_formats_and_null_writes_zero_chars()
    {
        var format = ShapingCodegen.CreateSpanFormatter<double?>("0.0%", CultureInfo.InvariantCulture);
        Span<char> buffer = stackalloc char[32];

        int written = format(0.38, buffer);
        Assert.Equal("38.0%", buffer[..written].ToString());

        Assert.Equal(0, format(null, buffer)); // null unwraps to "nothing to write", NOT −1
    }

    [Fact]
    public void String_keys_copy_and_null_writes_zero_chars()
    {
        var format = ShapingCodegen.CreateSpanFormatter<string>();
        Span<char> buffer = stackalloc char[32];

        int written = format("hello band", buffer);
        Assert.Equal("hello band", buffer[..written].ToString());

        Assert.Equal(0, format(null!, buffer));
    }

    [Fact]
    public void Too_small_destination_returns_minus_one_then_succeeds_after_growth()
    {
        var money = ShapingCodegen.CreateSpanFormatter<decimal>("$#,##0", CultureInfo.InvariantCulture);
        var text = ShapingCodegen.CreateSpanFormatter<string>();
        Span<char> tiny = stackalloc char[3];
        Span<char> grown = stackalloc char[32];

        // Both lanes signal −1 (never a partial write), and the SAME call succeeds after growth —
        // the band cache's grow-and-retry contract.
        Assert.Equal(-1, money(1234567m, tiny));
        int written = money(1234567m, grown);
        Assert.Equal("$1,234,567", grown[..written].ToString());

        Assert.Equal(-1, text("overflow", tiny));
        written = text("overflow", grown);
        Assert.Equal("overflow", grown[..written].ToString());
    }

    [Fact]
    public void Int_formats_via_the_constrained_lane()
    {
        var plain = ShapingCodegen.CreateSpanFormatter<int>(culture: CultureInfo.InvariantCulture);
        var grouped = ShapingCodegen.CreateSpanFormatter<int>("#,##0", CultureInfo.InvariantCulture);
        Span<char> buffer = stackalloc char[32];

        int written = plain(7, buffer);
        Assert.Equal("7", buffer[..written].ToString());

        written = grouped(1234567, buffer);
        Assert.Equal("1,234,567", buffer[..written].ToString());
    }

    private sealed class Opaque
    {
        public override string ToString() => "opaque-value";
    }

    [Fact]
    public void Non_span_formattable_keys_fall_back_to_ToString_copy()
    {
        var format = ShapingCodegen.CreateSpanFormatter<Opaque>();
        Span<char> buffer = stackalloc char[32];
        Span<char> tiny = stackalloc char[4];

        int written = format(new Opaque(), buffer);
        Assert.Equal("opaque-value", buffer[..written].ToString());

        Assert.Equal(-1, format(new Opaque(), tiny)); // the fallback honors the grow contract too
        Assert.Equal(0, format(null!, buffer));       // CreateFormatter's null → "" convention
    }

    [Fact]
    public void Hot_lanes_allocate_nothing_after_warmup()
    {
        // The whole point of the kit (§2.2): a band fill formats 1–5k cells per re-anchor/tick with
        // ZERO per-cell strings. Gate the constrained + nullable lanes (the fallback lane is
        // documented cold and excluded by design).
        var money = ShapingCodegen.CreateSpanFormatter<decimal>("$#,##0", CultureInfo.InvariantCulture);
        var percent = ShapingCodegen.CreateSpanFormatter<double?>("0.0%", CultureInfo.InvariantCulture);
        Span<char> buffer = stackalloc char[64];

        for (int i = 0; i < 1_000; i++)
        {
            money(i * 997m, buffer);
            percent(i / 1000.0, buffer);
            percent(null, buffer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            money(i * 997m, buffer);
            percent(i / 10_000.0, buffer);
            percent(null, buffer);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
