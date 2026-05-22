using System.Collections;
using System.Globalization;

namespace Cursorial.Text;

public ref struct GraphemeEnumerator : IEnumerator<ReadOnlySpan<char>>
{
    private const string? CannotEnumerateMessage = "No grapheme is available, or the end of the string has been reached.";
    private readonly ReadOnlySpan<char> _text;
    private readonly int _textStartIndex; // where in _text the enumeration should begin

    private int _currentTextElementOffset;
    private int _currentTextElementLength;
    private bool _exhausted;

    internal GraphemeEnumerator(ReadOnlySpan<char> text, int startIndex)
    {
        if (startIndex < 0 || startIndex > text.Length)
            throw new ArgumentOutOfRangeException(nameof(startIndex), $"{nameof(startIndex)} must be within the bounds of the text");

        _text = text;
        _textStartIndex = startIndex;

        Reset();
    }

    public bool MoveNext()
    {
        // Once the enumerator has walked past the last grapheme, subsequent MoveNext calls must
        // stay false — that's the IEnumerator contract. A separate flag is necessary because the
        // Reset state legitimately leaves _currentTextElementLength negative (it's the offset
        // delta back to the start position).
        if (_exhausted) return false;

        int newOffset = _currentTextElementOffset + _currentTextElementLength;
        _currentTextElementOffset = newOffset;
        _currentTextElementLength = -1; // mark invalid until we successfully read the next grapheme

        if (newOffset >= _text.Length)
        {
            _exhausted = true;
            return false;
        }

        _currentTextElementLength = NextGraphemeClusterLength(_text[newOffset..]);
        return true;
    }

    public ReadOnlySpan<char> GetCurrentGrapheme()
    {
        // Generate and return a substring slice.

        if (_currentTextElementLength <= 0)
            throw new InvalidOperationException(CannotEnumerateMessage);

        return _text.Slice(_currentTextElementOffset, _currentTextElementLength);
    }

    public int ElementIndex
    {
        get
        {
            if (_currentTextElementOffset >= _text.Length)
                throw new InvalidOperationException(CannotEnumerateMessage);

            return _currentTextElementOffset - _textStartIndex;
        }
    }

    public void Reset()
    {
        // These first two fields are set to intentionally out-of-range values.
        // They'll be fixed up once the enumerator starts.

        _currentTextElementOffset = _text.Length;
        _currentTextElementLength = _textStartIndex - _text.Length;
        _exhausted = false;
    }

    object IEnumerator.Current => throw new NotSupportedException($"Use IEnumerator<ReadOnlySpan<char>>.Current or {nameof(GetCurrentGrapheme)}()");

    public ReadOnlySpan<char> Current => GetCurrentGrapheme();

    private static int NextGraphemeClusterLength(ReadOnlySpan<char> text)
    {
        // Single-call grapheme advance. StringInfo doesn't expose a no-allocation form yet, so
        // use the index-stepping helper and recover the length from the position delta.
        int next = StringInfo.GetNextTextElementLength(text);
        return next > 0 ? next : 1; // Defensive: should not be zero for valid input.
    }

    void IDisposable.Dispose()
    {
        _currentTextElementLength = 0;
        _currentTextElementLength = -1;
    }
}