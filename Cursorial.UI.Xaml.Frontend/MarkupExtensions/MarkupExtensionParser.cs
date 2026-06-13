using System;
using System.Collections.Generic;
using System.Text;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The hand-rolled recursive-descent parser for the markup-extension grammar (matrix §4 / design
/// doc §4.3). Pure and type-system-free: it produces a <see cref="MarkupExtensionNode"/> and never
/// resolves a type — that is the loader's (X2) job. Every malformed input throws a <c>CUR13xx</c>
/// <see cref="XamlParseException"/> with a 1-based line+column; the parser never crashes, hangs, or
/// reads out of range (the fuzz gate, X58).
/// </summary>
/// <remarks>
/// <code>
/// Extension := '{' Name ( WS Positional (',' Positional)* )? ( ',' Named )* '}'
/// Named     := Name '=' Value
/// Value     := Extension | "'" QuotedChars "'" | BareChars      // '\' escapes within both
/// Literal   := '{}' rest-of-text                                 // brace-escape prefix (handled by the caller)
/// </code>
/// The leading <c>{}</c> escape (X46) is recognized by the caller (<see cref="LooksLikeExtension"/>)
/// before this parser runs.
/// </remarks>
internal static class MarkupExtensionParser
{
    /// <summary>
    /// True when an attribute value is a markup extension (starts with <c>{</c>) and is NOT the
    /// <c>{}</c> literal escape. A leading non-<c>{</c> character (even whitespace) means a literal
    /// (the WPF rule — matrix X112).
    /// </summary>
    public static bool LooksLikeExtension(string value)
        => value.Length > 0 && value[0] == '{' && !(value.Length >= 2 && value[1] == '}');

    /// <summary>
    /// Returns the literal text behind a <c>{}</c> escape (everything after the leading two chars),
    /// or the input unchanged if it is not an escape. Matrix X46/X110.
    /// </summary>
    public static string UnescapeLiteral(string value)
        => value.Length >= 2 && value[0] == '{' && value[1] == '}' ? value.Substring(2) : value;

    /// <summary>
    /// Parses a markup-extension string into a node. <paramref name="baseLine"/>/<paramref name="baseColumn"/>
    /// are the 1-based position of the opening <c>{</c> in the source document, used to map an internal
    /// offset back to a source position (single-line attribute values, the common case, map exactly;
    /// the position always lands on the attribute when an attribute value spans lines).
    /// </summary>
    public static MarkupExtensionNode Parse(string text, Uri? source, int baseLine, int baseColumn)
    {
        var parser = new Cursor(text, source, baseLine, baseColumn);
        var node = parser.ParseExtension();
        parser.ExpectEnd();
        return node;
    }

    /// <summary>The mutable parse cursor. A ref struct: never escapes a parse.</summary>
    private ref struct Cursor
    {
        private readonly string _text;
        private readonly Uri? _source;
        private readonly int _baseLine;
        private readonly int _baseColumn;
        private int _pos;

        public Cursor(string text, Uri? source, int baseLine, int baseColumn)
        {
            _text = text;
            _source = source;
            _baseLine = baseLine;
            _baseColumn = baseColumn;
            _pos = 0;
        }

        private bool AtEnd => _pos >= _text.Length;
        private char Current => _text[_pos];

        // Map an offset within the (single-attribute-value) string to a 1-based source position.
        // Attribute values may contain newlines; we map line breaks faithfully and otherwise advance
        // the column from the base.
        private (int Line, int Column) PositionAt(int offset)
        {
            int line = _baseLine;
            int column = _baseColumn;
            for (int i = 0; i < offset && i < _text.Length; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return (line, column);
        }

        private XamlParseException Fail(string code, string message, int offset)
        {
            var (line, column) = PositionAt(offset);
            return new XamlParseException(XamlDiagnostic.Error(code, message, _source, line, column));
        }

        public MarkupExtensionNode ParseExtension()
        {
            int openOffset = _pos;
            if (AtEnd || Current != '{')
                throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Expected '{' to begin a markup extension.", openOffset);
            _pos++; // consume '{'

            SkipWhitespace();
            int nameOffset = _pos;
            string name = ReadExtensionName();
            if (name.Length == 0)
                throw Fail(XamlDiagnosticCodes.MalformedExtensionArgument, "A markup extension must name a type.", nameOffset);

            var positionals = new List<MarkupExtensionArgumentValue>();
            var named = new List<MarkupExtensionNamedArgument>();

            SkipWhitespace();

            // Empty body: {Binding} — valid (X51).
            if (!AtEnd && Current == '}')
            {
                _pos++; // consume '}'
                var (l, c) = PositionAt(openOffset);
                return new MarkupExtensionNode(name, positionals, named, l, c);
            }

            // Arguments: a comma-separated list of positionals and named pairs.
            // Per the grammar, positionals precede named; we accept any order of completed
            // named pairs but a positional after a named is treated as a positional value too
            // (WPF leniency); ambiguity resolution (which member) is the loader's.
            bool first = true;
            while (true)
            {
                if (!first)
                {
                    // expect a comma separator
                    SkipWhitespace();
                    if (AtEnd)
                        throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Unterminated markup extension; expected '}'.", openOffset);
                    if (Current == '}')
                        break;
                    if (Current != ',')
                        throw Fail(XamlDiagnosticCodes.MalformedExtensionArgument, $"Expected ',' or '}}' in markup extension, found '{Current}'.", _pos);
                    _pos++; // consume ','
                }
                first = false;

                SkipWhitespace();
                if (AtEnd)
                    throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Unterminated markup extension; expected '}'.", openOffset);
                if (Current == '}')
                {
                    // trailing comma before close → treat as benign end
                    break;
                }

                // A named argument begins with an identifier followed (after optional WS) by '='.
                // A leading '=' with no name is malformed (X54).
                if (Current == '=')
                    throw Fail(XamlDiagnosticCodes.MalformedExtensionArgument, "Markup-extension argument name is empty before '='.", _pos);

                if (Current == '{')
                {
                    // a positional nested extension
                    positionals.Add(MarkupExtensionArgumentValue.FromNested(ParseExtension()));
                    continue;
                }

                if (Current == '\'')
                {
                    // a positional quoted value
                    positionals.Add(MarkupExtensionArgumentValue.FromText(ReadQuoted()));
                    continue;
                }

                // Read an identifier-or-bare-value run; if it is followed by '=', it is a named arg.
                int tokenOffset = _pos;
                string token = ReadNameOrBareUntil(stopAtEquals: true);
                SkipWhitespace();
                if (!AtEnd && Current == '=')
                {
                    if (token.Length == 0)
                        throw Fail(XamlDiagnosticCodes.MalformedExtensionArgument, "Markup-extension argument name is empty before '='.", _pos);
                    _pos++; // consume '='
                    SkipWhitespace();
                    var value = ReadValue(openOffset);
                    var (nl, nc) = PositionAt(tokenOffset);
                    named.Add(new MarkupExtensionNamedArgument(token, value, nl, nc));
                }
                else
                {
                    // a positional bare value; we already consumed trailing whitespace, so trim it
                    positionals.Add(MarkupExtensionArgumentValue.FromText(token.TrimEnd()));
                }
            }

            if (AtEnd || Current != '}')
                throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Unterminated markup extension; expected '}'.", openOffset);
            _pos++; // consume '}'

            var (line, col) = PositionAt(openOffset);
            return new MarkupExtensionNode(name, positionals, named, line, col);
        }

        // A value after '=': nested extension, quoted, or bare.
        private MarkupExtensionArgumentValue ReadValue(int openOffset)
        {
            if (AtEnd)
                throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Unterminated markup extension; expected a value.", openOffset);
            if (Current == '{')
                return MarkupExtensionArgumentValue.FromNested(ParseExtension());
            if (Current == '\'')
                return MarkupExtensionArgumentValue.FromText(ReadQuoted());
            // bare: read until ',' or '}' (the value), then trim trailing whitespace (X50).
            string bare = ReadNameOrBareUntil(stopAtEquals: false);
            return MarkupExtensionArgumentValue.FromText(bare.TrimEnd());
        }

        // Reads a quoted string with '\' escapes (X48/X49). The opening quote is at the cursor.
        private string ReadQuoted()
        {
            int quoteOffset = _pos;
            _pos++; // consume opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Unterminated quoted markup-extension value.", quoteOffset);
                char c = Current;
                if (c == '\\')
                {
                    _pos++;
                    if (AtEnd)
                        throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Dangling escape in quoted markup-extension value.", _pos);
                    sb.Append(Current); // the escaped char verbatim ('\}' → '}', '\\' → '\')
                    _pos++;
                    continue;
                }
                if (c == '\'')
                {
                    _pos++; // consume closing quote
                    return sb.ToString();
                }
                sb.Append(c);
                _pos++;
            }
        }

        // The extension type name: an identifier optionally namespace-prefixed (x:Static).
        private string ReadExtensionName()
        {
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                char c = Current;
                if (char.IsWhiteSpace(c) || c == ',' || c == '}' || c == '=')
                    break;
                sb.Append(c);
                _pos++;
            }
            return sb.ToString();
        }

        // Reads a name or bare value, honoring '\' escapes, stopping at ',', '}', and optionally '='.
        private string ReadNameOrBareUntil(bool stopAtEquals)
        {
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                char c = Current;
                if (c == '\\')
                {
                    _pos++;
                    if (AtEnd)
                        throw Fail(XamlDiagnosticCodes.UnterminatedExtension, "Dangling escape in markup-extension value.", _pos);
                    sb.Append(Current);
                    _pos++;
                    continue;
                }
                if (c == ',' || c == '}')
                    break;
                if (c == '=' && stopAtEquals)
                    break;
                if (c == '{')
                    break; // a nested extension cannot start mid-token; let the caller handle it
                sb.Append(c);
                _pos++;
            }
            return sb.ToString();
        }

        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
                _pos++;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (!AtEnd)
                throw Fail(XamlDiagnosticCodes.MalformedExtensionArgument, "Unexpected trailing characters after a markup extension.", _pos);
        }
    }
}
