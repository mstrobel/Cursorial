using System.Globalization;

namespace Cursorial.Text;

/// <summary>
/// The packing arithmetic behind <see cref="TextSizing.Packed"/>: how a run of text is cut into OSC 66
/// sequences and how many cells each sequence's <c>w</c> claims. A cluster of natural width <c>c</c> at
/// fractional scale <c>n/d</c> costs <c>c·n/d</c> of a cell; a sequence greedily takes clusters, on cluster
/// boundaries, while its rounded-up cost stays within the spec's seven-cell maximum for <c>w</c>, and claims
/// <c>w = ⌈cost⌉</c>. The measurement (<see cref="PackedCells"/>) and the writer walk the same chunks, so a
/// packed run's footprint is exactly what the terminal is told to draw.
/// </summary>
public static class TextSizingPacking
{
    /// <summary>The spec's maximum for the <c>w</c> key (0–7): one sequence never claims more than seven cells ("all the text in that escape code must be rendered in s·w cells").</summary>
    public const int MaxCellsPerSequence = 7;

    /// <summary>One packed sequence: the text slice and the cells its <c>w</c> claims.</summary>
    public readonly record struct Chunk(int Start, int Length, int Cells);

    /// <summary>The cells (unscaled) the packed sequences for <paramref name="text"/> claim in total.</summary>
    public static int PackedCells(in TextSizing sizing, ReadOnlySpan<char> text)
    {
        int cells = 0;
        foreach (var chunk in Chunks(sizing, text))
            cells += chunk.Cells;
        return cells;
    }

    /// <summary>Cuts <paramref name="text"/> into the sequences the writer emits under <paramref name="sizing"/>.</summary>
    public static List<Chunk> Chunks(in TextSizing sizing, ReadOnlySpan<char> text)
    {
        var chunks = new List<Chunk>();
        if (text.IsEmpty)
            return chunks;

        // Cost is tracked in d-ths of a cell (d = 1 when no fraction is set), so rounding happens once per chunk.
        int n = sizing.IsFractional ? sizing.Numerator : 1;
        int d = sizing.IsFractional ? sizing.Denominator : 1;
        int maxUnits = MaxCellsPerSequence * d;

        int chunkStart = 0;
        int chunkLength = 0;
        int chunkUnits = 0;
        var remaining = text;
        int offset = 0;

        while (!remaining.IsEmpty)
        {
            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0)
                break;

            int units = Math.Max(1, GraphemeWidth.ClusterWidth(remaining[..len])) * n;
            if (chunkLength > 0 && chunkUnits + units > maxUnits)
            {
                chunks.Add(new Chunk(chunkStart, chunkLength, CeilCells(chunkUnits, d)));
                chunkStart = offset;
                chunkLength = 0;
                chunkUnits = 0;
            }

            chunkLength += len;
            chunkUnits += units;
            offset += len;
            remaining = remaining[len..];
        }

        if (chunkLength > 0)
            chunks.Add(new Chunk(chunkStart, chunkLength, Math.Min(MaxCellsPerSequence, CeilCells(chunkUnits, d))));

        return chunks;
    }

    private static int CeilCells(int units, int d) => (units + d - 1) / d;
}
