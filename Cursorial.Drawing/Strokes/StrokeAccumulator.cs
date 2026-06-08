using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>Per-cell callback invoked by <see cref="StrokeAccumulator.Flush"/> at draw-pass end.</summary>
internal delegate void StrokeCellEmitter(int column, int row, byte arms, StrokeRecord record);

/// <summary>
/// Accumulates box/line strokes for one scene draw pass so junctions resolve correctly across separate
/// draw calls, then replays them once at flush. Each cell holds a packed arm code (2 bits/direction)
/// plus a 1-based record id; merging is governed by a three-level hierarchy:
/// <list type="number">
///   <item><b>figure</b> — different figures never merge (the later figure overwrites the cell);</item>
///   <item><b>record</b> — different calls in the same figure obey the incoming pen's <see cref="JunctionMode"/>;</item>
///   <item><b>self</b> — the same call's cells (a box's own corners) always merge unconditionally.</item>
/// </list>
/// Arm merging is per-direction <b>MAX</b> (not bitwise-OR — OR on a 2-bit ordinal would fabricate a
/// <see cref="StrokeWeight.Double"/> from a light+heavy crossing). Arrays are allocated lazily on the
/// first deposit, so a stroke-free draw pass costs nothing.
/// </summary>
internal sealed class StrokeAccumulator
{
    private readonly int _columns;
    private readonly int _rows;
    private byte[]? _arms;            // per cell: 2 bits/direction (Up/Right/Down/Left)
    private int[]? _recordId;         // per cell: 0 = untouched, else index+1 into _records
    private readonly List<StrokeRecord> _records = [];
    private readonly List<int> _touched = [];   // cell indices, each added once on first touch

    private int _currentFigureId;               // 0 = implicit root
    private int _nextFigureId = 1;
    private int _openFigureFirstRecord = -1;     // index of the open figure's first record, or -1
    private Rect? _figureExplicitBounds;

    public StrokeAccumulator(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
    }

    public bool IsEmpty => _touched.Count == 0;

    /// <summary>Record a stroke call (stamping it with the current figure) and return its 1-based id.</summary>
    public int AddRecord(StrokeRecord record)
    {
        record.FigureId = _currentFigureId;
        _records.Add(record);
        return _records.Count;
    }

    /// <summary>Open a figure; returns its id token. <paramref name="explicitBounds"/> null = auto-union.</summary>
    public int BeginFigure(Rect? explicitBounds)
    {
        _currentFigureId = _nextFigureId++;
        _openFigureFirstRecord = _records.Count;
        _figureExplicitBounds = explicitBounds;
        return _currentFigureId;
    }

    /// <summary>Close the open figure if <paramref name="figureId"/> matches; back-patch its brush bounds.</summary>
    public void EndFigure(int figureId)
    {
        if (_openFigureFirstRecord < 0 || figureId != _currentFigureId)
            return;

        BackPatchBounds();
        _currentFigureId = 0;
        _openFigureFirstRecord = -1;
        _figureExplicitBounds = null;
    }

    /// <summary>Whether a figure is currently open (for the flush-time leak guard).</summary>
    public bool HasOpenFigure => _openFigureFirstRecord >= 0;

    /// <summary>The id of the currently open figure (valid only when <see cref="HasOpenFigure"/>).</summary>
    public int CurrentFigureId => _currentFigureId;

    private void BackPatchBounds()
    {
        int start = _openFigureFirstRecord;
        if (start >= _records.Count)
            return;   // empty figure — nothing drawn

        Rect bounds;
        if (_figureExplicitBounds is { } explicitBounds)
        {
            bounds = explicitBounds;
        }
        else
        {
            bounds = _records[start].Bounds;
            for (int i = start + 1; i < _records.Count; i++)
                bounds = Union(bounds, _records[i].Bounds);
        }

        for (int i = start; i < _records.Count; i++)
        {
            var record = _records[i];
            record.Bounds = bounds;
            _records[i] = record;
        }
    }

    private static Rect Union(in Rect a, in Rect b)
    {
        int left = Math.Min(a.Column, b.Column);
        int top = Math.Min(a.Row, b.Row);
        int right = Math.Max(a.ColumnEnd, b.ColumnEnd);
        int bottom = Math.Max(a.RowEnd, b.RowEnd);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Deposit <paramref name="armBits"/> at (<paramref name="column"/>, <paramref name="row"/>) for
    /// the stroke <paramref name="recordId"/>, resolving conflicts by the figure/record/self hierarchy.
    /// Cells outside the scene are clipped.
    /// </summary>
    public void Deposit(int column, int row, byte armBits, int recordId, JunctionMode mode)
    {
        if (armBits == 0)
            return;
        if ((uint) column >= (uint) _columns || (uint) row >= (uint) _rows)
            return;

        _arms ??= new byte[_columns * _rows];
        _recordId ??= new int[_columns * _rows];

        int idx = row * _columns + column;
        int existing = _recordId[idx];

        if (existing == 0)   // first touch
        {
            _arms[idx] = armBits;
            _recordId[idx] = recordId;
            _touched.Add(idx);
            return;
        }

        if (existing == recordId)   // same call → unconditional self-merge (corners close)
        {
            _arms[idx] = MergeArms(_arms[idx], armBits);
            return;
        }

        if (_records[existing - 1].FigureId != _records[recordId - 1].FigureId)   // cross-figure → later wins
        {
            _arms[idx] = armBits;
            _recordId[idx] = recordId;
            return;
        }

        switch (mode)   // same figure, different call → JunctionMode
        {
            case JunctionMode.Merge:
                _arms[idx] = MergeArms(_arms[idx], armBits);
                _recordId[idx] = recordId;   // last-writer-wins for color / decoration
                break;
            case JunctionMode.Overlay:
                _arms[idx] = armBits;
                _recordId[idx] = recordId;
                break;
            case JunctionMode.Break:
                break;                        // incoming yields — deposit nothing
        }
    }

    /// <summary>Replay every touched cell (in first-touch order) to <paramref name="emit"/>.</summary>
    public void Flush(StrokeCellEmitter emit)
    {
        if (_arms is null || _recordId is null)
            return;

        foreach (int idx in _touched)
        {
            int recordId = _recordId[idx];
            if (recordId == 0)
                continue;
            emit(idx % _columns, idx / _columns, _arms[idx], _records[recordId - 1]);
        }
    }

    /// <summary>Pack a single direction's weight into an arm-code byte (centralizes the weight+1 offset).</summary>
    internal static byte ArmBits(Arm direction, StrokeWeight weight) =>
        (byte) (((int) weight + 1) << ((int) direction * 2));

    /// <summary>Per-direction MAX of two arm codes.</summary>
    internal static byte MergeArms(byte a, byte b)
    {
        int result = 0;
        for (int direction = 0; direction < 4; direction++)
        {
            int weightA = (a >> (direction * 2)) & 3;
            int weightB = (b >> (direction * 2)) & 3;
            result |= Math.Max(weightA, weightB) << (direction * 2);
        }
        return (byte) result;
    }
}
