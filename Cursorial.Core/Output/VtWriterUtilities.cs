namespace Cursorial.Output;

/// <summary>
/// Shared internal helpers used by the byte-level writers (<see cref="SgrEncoder"/>,
/// <c>CursorWriter</c>, <c>ScreenWriter</c>, …). Kept internal so consumers don't develop
/// dependencies on the helpers themselves rather than the writers that compose them.
/// </summary>
internal static class VtWriterUtilities
{
    /// <summary>Append the decimal ASCII representation of <paramref name="value"/> to the buffer.</summary>
    internal static void WriteAsciiInt(uint value, Span<byte> buffer, ref int written)
    {
        Span<byte> tmp = stackalloc byte[10];
        int idx = tmp.Length;
        do
        {
            tmp[--idx] = (byte)('0' + value % 10);
            value /= 10;
        } while (value > 0);

        int len = tmp.Length - idx;
        tmp.Slice(idx, len).CopyTo(buffer[written..]);
        written += len;
    }

    /// <summary>
    /// Append the decimal ASCII representation of <paramref name="value"/>, treating negative
    /// values as clamped to zero. Convenience for CSI parameters that are conceptually unsigned
    /// (positions, counts) but might be invoked with int-typed arguments.
    /// </summary>
    internal static void WriteAsciiInt(int value, Span<byte> buffer, ref int written)
    {
        WriteAsciiInt(value < 0 ? 0u : (uint)value, buffer, ref written);
    }
}
