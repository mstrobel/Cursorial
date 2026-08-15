using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Drawing.Media;

/// <summary>One deferred braille draw call's state, sampled at flush (mirrors <see cref="StrokeRecord"/>).</summary>
internal struct BrailleRecord
{
    public IBrush Brush;

    /// <summary>The brush sampling bounds in scene coordinates; signed origin, exactly as <see cref="StrokeRecord.Bounds"/>.</summary>
    public Rect Bounds;
    public TextAttributes Attributes;
    public GlyphSet GlyphSet;
    public bool Overwrite;
}

/// <summary>Per-cell callback invoked by <see cref="BrailleRaster.Flush"/> at draw-pass end.</summary>
internal delegate void BrailleCellEmitter(int column, int row, byte dots, BrailleRecord record);

/// <summary>
/// Accumulates braille dots at 2×4 sub-cell resolution for one scene draw pass, then replays them at
/// flush. Far simpler than <see cref="StrokeAccumulator"/>: braille has no weight, direction, junctions,
/// or figures — merging a cell is just <c>dots |= bit</c> (last-writer-wins for color, the one-foreground
/// terminal limit). Arrays allocate lazily, so a braille-free pass costs nothing.
/// </summary>
internal sealed class BrailleRaster
{
    private readonly int _columns;
    private readonly int _rows;
    private byte[]? _dots;
    private int[]? _recordId;   // 0 = untouched, else index+1 into _records
    private readonly List<BrailleRecord> _records = [];
    private readonly List<int> _touched = [];

    public BrailleRaster(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
    }

    public bool IsEmpty => _touched.Count == 0;

    public int AddRecord(in BrailleRecord record)
    {
        _records.Add(record);
        return _records.Count;
    }

    /// <summary>
    /// Set the dot at sub-cell (<paramref name="subX"/>, <paramref name="subY"/>) for stroke
    /// <paramref name="recordId"/>. Sub-coordinates span <c>[0, 2·columns)</c> × <c>[0, 4·rows)</c>;
    /// out-of-range plots are clipped.
    /// </summary>
    public void Plot(int subX, int subY, int recordId)
    {
        if ((uint) subX >= (uint) (2 * _columns) || (uint) subY >= (uint) (4 * _rows))
            return;

        _dots ??= new byte[_columns * _rows];
        _recordId ??= new int[_columns * _rows];

        int col = subX >> 1, row = subY >> 2;
        int idx = row * _columns + col;
        if (_recordId[idx] == 0)
            _touched.Add(idx);

        _dots[idx] |= (byte) (1 << BrailleGlyphs.Bit(subX & 1, subY & 3));
        _recordId[idx] = recordId;   // last-writer-wins for color
    }

    public void Flush(BrailleCellEmitter emit)
    {
        if (_dots is null || _recordId is null)
            return;

        foreach (int idx in _touched)
        {
            int recordId = _recordId[idx];
            if (recordId == 0)
                continue;
            emit(idx % _columns, idx / _columns, _dots[idx], _records[recordId - 1]);
        }
    }
}
