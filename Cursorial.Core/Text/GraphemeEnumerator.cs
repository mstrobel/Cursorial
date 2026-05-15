using System.Collections;
using System.Globalization;

namespace Cursorial.Text;

public struct GraphemeEnumerator : IEnumerator<ReadOnlySpan<char>>
{
    private const string? CannotEnumerateMessage = "No grapheme is available, or the end of the string has been reached.";
    private readonly string _text;
    private readonly int _textStartIndex; // where in _text the enumeration should begin

    private int _currentTextElementOffset;
    private int _currentTextElementLength = -1;

    internal GraphemeEnumerator(string text, int startIndex)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text), $"{nameof(text)} cannot be null");

        if (startIndex < 0 || startIndex > text.Length)
            throw new ArgumentOutOfRangeException(nameof(startIndex), $"{nameof(startIndex)} must be within the bounds of the text");

        _text = text;
        _textStartIndex = startIndex;

        Reset();
    }

    public bool MoveNext()
    {
        int newOffset = _currentTextElementOffset + _currentTextElementLength;

        _currentTextElementOffset = newOffset; // advance
        _currentTextElementLength = -1;        // prevent future calls to MoveNext() or get_Current from succeeding if we've hit end of data

        if (newOffset >= _text.Length)
            return false; // reached the end of the data

        _currentTextElementLength = NextGraphemeClusterLength(_text.AsSpan(newOffset));
        return true;
    }

    public ReadOnlySpan<char> GetCurrentGrapheme()
    {
        // Generate and return a substring slice.

        if (_currentTextElementLength < 0)
            throw new InvalidOperationException(CannotEnumerateMessage);

        return _text.AsSpan(_currentTextElementOffset).Slice(0, _currentTextElementLength);
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