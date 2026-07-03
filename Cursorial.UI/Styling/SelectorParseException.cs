// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Thrown by <see cref="Selector.Parse"/> on malformed selector text, on unresolvable or ambiguous
/// type tokens, and on the deliberately unsupported grammar constructs (sibling combinators,
/// <c>:not()</c>, <c>:nth-*</c>, <c>:first-child</c>/<c>:last-child</c>, attribute selectors —
/// design doc §3.10; those messages state the construct is unsupported <em>by design</em>).
/// Carries the zero-based character <see cref="Position"/> of the offending token in the original
/// text (style matrix SD3). <see cref="Exception.Message"/> is the bare failure description —
/// callers rendering errors compose <see cref="Position"/> themselves; <see cref="ToString"/>
/// appends it to the header line for log output.
/// </summary>
public sealed class SelectorParseException : FormatException
{
    /// <summary>Creates a parse exception for the token at <paramref name="position"/>.</summary>
    /// <param name="message">The failure description, naming the offending token or construct.</param>
    /// <param name="position">The zero-based character offset into the original selector text.</param>
    public SelectorParseException(string message, int position)
        : base(message)
    {
        Position = position;
    }

    /// <summary>The zero-based character offset of the offending token in the parsed text.</summary>
    public int Position { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        // Splice the position onto the "{Type}: {Message}" header line, ahead of any stack trace.
        var text = base.ToString();
        var newline = text.IndexOf(Environment.NewLine, StringComparison.Ordinal);

        return newline < 0
            ? $"{text} (position {Position})"
            : $"{text[..newline]} (position {Position}){text[newline..]}";
    }
}
