using System.Text;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// A minimal VT-output screen emulator for tests. Unlike <c>SyntheticTerminalHost</c> (which only
/// CAPTURES the emitted bytes), this EXECUTES them into a cell grid + cursor — CUP / CUU-CUD-CUF-CUB /
/// CHA / VPA / CR / LF-with-scroll / SU-SD / ED / EL / print — so a test can assert what actually lands
/// ON SCREEN. That is the verification byte-substring assertions cannot give for relative
/// (position-dependent) moves: <c>CSI 4 A</c> means "up 4 from wherever the cursor is", so only a model
/// that tracks the cursor and scroll can say where the region ends up.
///
/// SGR (<c>m</c>), cursor show/hide + other DEC private modes, DECSCUSR (<c> q</c>), OSC, and the
/// synchronized-output brackets are parsed and skipped — none move the cursor or change a cell. DECAWM
/// (autowrap) is honored because the renderer disables it. Wide glyphs are out of scope (tests use ASCII).
/// </summary>
internal sealed class VtScreen
{
    private readonly char[] _cells; // row-major; ' ' is blank
    private int _savedRow, _savedCol;
    private bool _autowrap = true;

    public int Rows { get; }
    public int Cols { get; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }

    public VtScreen(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _cells = new char[cols * rows];
        Array.Fill(_cells, ' ');
    }

    public char At(int row, int col) => _cells[row * Cols + col];
    public string Line(int row) => new(_cells, row * Cols, Cols);
    public string LineTrimmed(int row) => Line(row).TrimEnd();

    /// <summary>Place the cursor directly (seed the shell prompt position before the app runs).</summary>
    public void SetCursor(int row, int col) { CursorRow = Clamp(row, Rows); CursorCol = Clamp(col, Cols); }

    /// <summary>Write text at the cursor (seed pre-existing shell content).</summary>
    public void Print(string text) => FeedString(text);

    /// <summary>Execute a stream the application emitted.</summary>
    public void Feed(ReadOnlySpan<byte> bytes) => FeedString(Encoding.UTF8.GetString(bytes));

    private static int Clamp(int v, int max) => Math.Max(0, Math.Min(max - 1, v));

    private void FeedString(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            switch (c)
            {
                case '\x1b': i = Escape(s, i + 1); continue;
                case '\r': CursorCol = 0; i++; continue;
                case '\n': LineFeed(); i++; continue;
                case '\b': CursorCol = Math.Max(0, CursorCol - 1); i++; continue;
                default:
                    if (c < 0x20) { i++; continue; } // other C0 — no effect here
                    Put(c); i++; continue;
            }
        }
    }

    private int Escape(string s, int i)
    {
        if (i >= s.Length) return i;
        switch (s[i])
        {
            case '[': return Csi(s, i + 1);
            case ']': return Osc(s, i + 1);
            case '7': _savedRow = CursorRow; _savedCol = CursorCol; return i + 1; // DECSC
            case '8': CursorRow = _savedRow; CursorCol = _savedCol; return i + 1; // DECRC
            case '(': case ')': case '*': case '+': return i + 2;                 // charset designator (skip next)
            default: return i + 1;                                                // =, >, and any other single-char escape
        }
    }

    private int Csi(string s, int i)
    {
        bool priv = false;
        if (i < s.Length && (s[i] == '?' || s[i] == '>' || s[i] == '!' || s[i] == '=')) { priv = s[i] == '?'; i++; }

        int paramStart = i;
        // ';' separates parameters, ':' separates sub-parameters (SGR RGB / underline style / colour).
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == ';' || s[i] == ':')) i++;
        var paramStr = s.Substring(paramStart, i - paramStart);

        while (i < s.Length && s[i] >= 0x20 && s[i] <= 0x2f) i++; // intermediates (e.g. the space in DECSCUSR)
        if (i >= s.Length) return i;

        char final = s[i];
        Dispatch(priv, paramStr, final);
        return i + 1;
    }

    private static int Osc(string s, int i)
    {
        while (i < s.Length)
        {
            if (s[i] == '\x07') return i + 1;                                       // BEL
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '\\') return i + 2; // ST
            i++;
        }
        return i;
    }

    private void Dispatch(bool priv, string paramStr, char final)
    {
        int Param(int idx, int def)
        {
            var parts = paramStr.Split(';');
            return idx < parts.Length && int.TryParse(parts[idx], out var v) ? v : def;
        }

        if (priv)
        {
            if (Param(0, 0) == 7) _autowrap = final == 'h'; // DECAWM
            return;                                          // other DEC private modes — no grid effect
        }

        switch (final)
        {
            case 'H': case 'f': CursorRow = Clamp(Param(0, 1) - 1, Rows); CursorCol = Clamp(Param(1, 1) - 1, Cols); break;
            case 'A': CursorRow = Math.Max(0, CursorRow - Math.Max(1, Param(0, 1))); break;
            case 'B': CursorRow = Math.Min(Rows - 1, CursorRow + Math.Max(1, Param(0, 1))); break;
            case 'C': CursorCol = Math.Min(Cols - 1, CursorCol + Math.Max(1, Param(0, 1))); break;
            case 'D': CursorCol = Math.Max(0, CursorCol - Math.Max(1, Param(0, 1))); break;
            case 'G': CursorCol = Clamp(Param(0, 1) - 1, Cols); break;
            case 'd': CursorRow = Clamp(Param(0, 1) - 1, Rows); break;
            case 'J': EraseDisplay(Param(0, 0)); break;
            case 'K': EraseLine(Param(0, 0)); break;
            case 'S': ScrollUp(Math.Max(1, Param(0, 1))); break;
            case 'T': ScrollDown(Math.Max(1, Param(0, 1))); break;
            // 'm' (SGR), ' q' (DECSCUSR), 'n' (DSR), 'r' (DECSTBM — unused inline) etc.: no grid effect
        }
    }

    private void Put(char c)
    {
        if (CursorCol >= Cols)
        {
            if (!_autowrap) return; // exact-width emission with DECAWM off never needs to wrap
            CursorCol = 0;
            LineFeed();
        }
        _cells[CursorRow * Cols + CursorCol] = c;
        CursorCol++;
    }

    private void LineFeed()
    {
        if (CursorRow >= Rows - 1) ScrollUp(1); // at the bottom margin: scroll the whole screen up
        else CursorRow++;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, Rows);
        Array.Copy(_cells, n * Cols, _cells, 0, (Rows - n) * Cols);
        Array.Fill(_cells, ' ', (Rows - n) * Cols, n * Cols);
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, Rows);
        Array.Copy(_cells, 0, _cells, n * Cols, (Rows - n) * Cols);
        Array.Fill(_cells, ' ', 0, n * Cols);
    }

    private void EraseDisplay(int mode)
    {
        int cur = CursorRow * Cols + CursorCol;
        switch (mode)
        {
            case 0: Array.Fill(_cells, ' ', cur, _cells.Length - cur); break; // cursor → end of screen
            case 1: Array.Fill(_cells, ' ', 0, cur + 1); break;               // start → cursor
            case 2: Array.Fill(_cells, ' '); break;                           // whole screen
        }
    }

    private void EraseLine(int mode)
    {
        int rowStart = CursorRow * Cols;
        switch (mode)
        {
            case 0: Array.Fill(_cells, ' ', rowStart + CursorCol, Cols - CursorCol); break;
            case 1: Array.Fill(_cells, ' ', rowStart, CursorCol + 1); break;
            case 2: Array.Fill(_cells, ' ', rowStart, Cols); break;
        }
    }
}
