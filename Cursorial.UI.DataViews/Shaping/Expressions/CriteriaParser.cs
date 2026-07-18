using System.Globalization;

namespace Cursorial.UI.DataViews.Shaping.Expressions;

/// <summary>
/// The criteria-language parser (design doc §9.1): a hand-rolled tokenizer + recursive-descent
/// parser producing a positioned <see cref="CriteriaNode"/> AST and a diagnostics list — it never
/// throws on malformed input (the text editor's validation strip needs partial results with precise
/// columns). Keywords are case-insensitive. Precedence (loosest→tightest):
/// <c>Or</c> → <c>And</c> → <c>Not</c> → comparisons/<c>In</c>/<c>Between</c>/<c>Like</c> →
/// <c>+ -</c> → <c>* / %</c> → unary <c>-</c> → primary. <c>Not [A] = 1</c> therefore negates the
/// whole comparison (the DevExpress criteria semantic).
/// </summary>
public static class CriteriaParser
{
    /// <summary>The parse product: the root (null on a fatal parse failure) + diagnostics (empty = valid).</summary>
    public readonly record struct Result(CriteriaNode? Root, IReadOnlyList<CriteriaDiagnostic> Diagnostics)
    {
        public bool IsValid => Root is not null && Diagnostics.Count == 0;
    }

    public static Result Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var state = new State(text);
        var root = state.ParseOr();

        if (root is not null && state.Peek().Kind != TokenKind.End)
            state.Error(state.Peek().Start, state.Peek().Length, $"Unexpected '{state.Peek().Text(text)}' after the expression.");

        return new Result(state.Diagnostics.Count == 0 ? root : root, state.Diagnostics);
    }

    // ── Tokens ───────────────────────────────────────────────────────────────────────────────────

    private enum TokenKind
    {
        End, Error,
        Field, Number, String, Date, True, False, Null, Identifier,
        // punctuation/operators
        LeftParen, RightParen, Comma,
        Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
        Plus, Minus, Star, Slash, Percent,
        // keywords
        And, Or, Not, In, Between, Like,
    }

    private readonly record struct Token(TokenKind Kind, int Start, int Length, object? Value)
    {
        public string Text(string source) => source.Substring(Start, Math.Min(Length, source.Length - Start));
    }

    // ── The tokenizer + parser state ─────────────────────────────────────────────────────────────

    private sealed class State(string text)
    {
        private readonly string _text = text;
        private int _position;
        private Token? _lookahead;

        public readonly List<CriteriaDiagnostic> Diagnostics = [];

        public void Error(int start, int length, string message)
        {
            // One diagnostic per site; cap so a garbage input doesn't flood the strip.
            if (Diagnostics.Count < 8)
                Diagnostics.Add(new CriteriaDiagnostic(message, start, Math.Max(1, length)));
        }

        public Token Peek() => _lookahead ??= Lex();

        public Token Take()
        {
            var token = Peek();
            _lookahead = null;
            return token;
        }

        private bool TakeIf(TokenKind kind)
        {
            if (Peek().Kind != kind)
                return false;
            Take();
            return true;
        }

        // ── Lexing ───────────────────────────────────────────────────────────────────────────────

        private Token Lex()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
                _position++;

            if (_position >= _text.Length)
                return new Token(TokenKind.End, _text.Length, 0, null);

            int start = _position;
            char c = _text[_position];

            switch (c)
            {
                case '(': _position++; return new Token(TokenKind.LeftParen, start, 1, null);
                case ')': _position++; return new Token(TokenKind.RightParen, start, 1, null);
                case ',': _position++; return new Token(TokenKind.Comma, start, 1, null);
                case '+': _position++; return new Token(TokenKind.Plus, start, 1, null);
                case '-': _position++; return new Token(TokenKind.Minus, start, 1, null);
                case '*': _position++; return new Token(TokenKind.Star, start, 1, null);
                case '/': _position++; return new Token(TokenKind.Slash, start, 1, null);
                case '%': _position++; return new Token(TokenKind.Percent, start, 1, null);

                case '=': _position++; return new Token(TokenKind.Equal, start, 1, null);

                case '<':
                    _position++;
                    if (_position < _text.Length && _text[_position] == '>') { _position++; return new Token(TokenKind.NotEqual, start, 2, null); }
                    if (_position < _text.Length && _text[_position] == '=') { _position++; return new Token(TokenKind.LessOrEqual, start, 2, null); }
                    return new Token(TokenKind.Less, start, 1, null);

                case '>':
                    _position++;
                    if (_position < _text.Length && _text[_position] == '=') { _position++; return new Token(TokenKind.GreaterOrEqual, start, 2, null); }
                    return new Token(TokenKind.Greater, start, 1, null);

                case '[':
                {
                    int end = _text.IndexOf(']', _position + 1);
                    if (end < 0)
                    {
                        Error(start, _text.Length - start, "Unterminated field reference — expected ']'.");
                        _position = _text.Length;
                        return new Token(TokenKind.Error, start, _text.Length - start, null);
                    }
                    string name = _text[(_position + 1)..end];
                    _position = end + 1;
                    return new Token(TokenKind.Field, start, _position - start, name);
                }

                case '\'':
                {
                    // 'string' with '' escaping a literal quote.
                    var builder = new System.Text.StringBuilder();
                    _position++;
                    while (true)
                    {
                        if (_position >= _text.Length)
                        {
                            Error(start, _text.Length - start, "Unterminated string — expected the closing '.");
                            return new Token(TokenKind.Error, start, _text.Length - start, null);
                        }
                        char ch = _text[_position];
                        if (ch == '\'')
                        {
                            if (_position + 1 < _text.Length && _text[_position + 1] == '\'')
                            {
                                builder.Append('\'');
                                _position += 2;
                                continue;
                            }
                            _position++;
                            break;
                        }
                        builder.Append(ch);
                        _position++;
                    }
                    return new Token(TokenKind.String, start, _position - start, builder.ToString());
                }

                case '#':
                {
                    int end = _text.IndexOf('#', _position + 1);
                    if (end < 0)
                    {
                        Error(start, _text.Length - start, "Unterminated date literal — expected the closing '#'.");
                        _position = _text.Length;
                        return new Token(TokenKind.Error, start, _text.Length - start, null);
                    }
                    string body = _text[(_position + 1)..end];
                    int length = end + 1 - start;
                    _position = end + 1;

                    // ISO/invariant first (the interchange convention), then current culture.
                    if (DateOnly.TryParse(body, CultureInfo.InvariantCulture, out var dateOnly) ||
                        DateOnly.TryParse(body, CultureInfo.CurrentCulture, out dateOnly))
                    {
                        return new Token(TokenKind.Date, start, length, dateOnly);
                    }
                    if (DateTime.TryParse(body, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime) ||
                        DateTime.TryParse(body, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateTime))
                    {
                        return new Token(TokenKind.Date, start, length, dateTime);
                    }
                    Error(start, length, $"'{body}' is not a recognizable date.");
                    return new Token(TokenKind.Error, start, length, null);
                }
            }

            if (char.IsDigit(c))
            {
                int end = _position;
                bool fractional = false;
                while (end < _text.Length && (char.IsDigit(_text[end]) || (!fractional && _text[end] == '.')))
                {
                    if (_text[end] == '.')
                    {
                        // A trailing '.' with no digit is not part of the number.
                        if (end + 1 >= _text.Length || !char.IsDigit(_text[end + 1]))
                            break;
                        fractional = true;
                    }
                    end++;
                }
                string body = _text[_position..end];
                int length = end - _position;
                _position = end;

                // Invariant culture (the mockup's 0.10); decimal keeps exactness, double for overflow.
                if (decimal.TryParse(body, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
                    return new Token(TokenKind.Number, start, length, dec);
                if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
                    return new Token(TokenKind.Number, start, length, dbl);
                Error(start, length, $"'{body}' is not a valid number.");
                return new Token(TokenKind.Error, start, length, null);
            }

            if (char.IsLetter(c) || c == '_')
            {
                int end = _position;
                while (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] == '_'))
                    end++;
                string word = _text[_position..end];
                int length = end - _position;
                _position = end;

                return word.ToUpperInvariant() switch
                {
                    "AND" => new Token(TokenKind.And, start, length, null),
                    "OR" => new Token(TokenKind.Or, start, length, null),
                    "NOT" => new Token(TokenKind.Not, start, length, null),
                    "IN" => new Token(TokenKind.In, start, length, null),
                    "BETWEEN" => new Token(TokenKind.Between, start, length, null),
                    "LIKE" => new Token(TokenKind.Like, start, length, null),
                    "TRUE" => new Token(TokenKind.True, start, length, null),
                    "FALSE" => new Token(TokenKind.False, start, length, null),
                    "NULL" => new Token(TokenKind.Null, start, length, null),
                    _ => new Token(TokenKind.Identifier, start, length, word),
                };
            }

            Error(start, 1, $"Unexpected character '{c}'.");
            _position++;
            return new Token(TokenKind.Error, start, 1, null);
        }

        // ── Parsing (recursive descent per the §9.1 precedence ladder) ───────────────────────────

        public CriteriaNode? ParseOr()
        {
            var left = ParseAnd();
            while (left is not null && Peek().Kind == TokenKind.Or)
            {
                Take();
                var right = ParseAnd();
                if (right is null)
                    return null;
                left = Binary(CriteriaBinaryOperator.Or, left, right);
            }
            return left;
        }

        private CriteriaNode? ParseAnd()
        {
            var left = ParseNot();
            while (left is not null && Peek().Kind == TokenKind.And)
            {
                Take();
                var right = ParseNot();
                if (right is null)
                    return null;
                left = Binary(CriteriaBinaryOperator.And, left, right);
            }
            return left;
        }

        private CriteriaNode? ParseNot()
        {
            if (Peek().Kind == TokenKind.Not)
            {
                var not = Take();
                var operand = ParseNot();
                if (operand is null)
                    return null;
                return new CriteriaNotNode
                {
                    Operand = operand,
                    Start = not.Start,
                    Length = operand.Start + operand.Length - not.Start,
                };
            }
            return ParseComparison();
        }

        private CriteriaNode? ParseComparison()
        {
            var left = ParseAdditive();
            if (left is null)
                return null;

            var next = Peek();
            switch (next.Kind)
            {
                case TokenKind.Equal: return FinishBinary(CriteriaBinaryOperator.Equal, left);
                case TokenKind.NotEqual: return FinishBinary(CriteriaBinaryOperator.NotEqual, left);
                case TokenKind.Less: return FinishBinary(CriteriaBinaryOperator.LessThan, left);
                case TokenKind.LessOrEqual: return FinishBinary(CriteriaBinaryOperator.LessThanOrEqual, left);
                case TokenKind.Greater: return FinishBinary(CriteriaBinaryOperator.GreaterThan, left);
                case TokenKind.GreaterOrEqual: return FinishBinary(CriteriaBinaryOperator.GreaterThanOrEqual, left);
                case TokenKind.Like: return FinishBinary(CriteriaBinaryOperator.Like, left);

                case TokenKind.In:
                {
                    Take();
                    if (!TakeIf(TokenKind.LeftParen))
                    {
                        Error(Peek().Start, Peek().Length, "In requires a parenthesized value list.");
                        return null;
                    }
                    var items = new List<CriteriaNode>();
                    do
                    {
                        var item = ParseAdditive();
                        if (item is null)
                            return null;
                        items.Add(item);
                    }
                    while (TakeIf(TokenKind.Comma));
                    if (!TakeIf(TokenKind.RightParen))
                    {
                        Error(Peek().Start, Peek().Length, "Unbalanced parenthesis — expected ')'.");
                        return null;
                    }
                    var last = items[^1];
                    return new CriteriaInNode
                    {
                        Operand = left,
                        Items = items,
                        Start = left.Start,
                        Length = last.Start + last.Length + 1 - left.Start,
                    };
                }

                case TokenKind.Between:
                {
                    Take();
                    var low = ParseAdditive();
                    if (low is null)
                        return null;
                    if (!TakeIf(TokenKind.And))
                    {
                        Error(Peek().Start, Peek().Length, "Between requires 'And' between its bounds.");
                        return null;
                    }
                    var high = ParseAdditive();
                    if (high is null)
                        return null;
                    return new CriteriaBetweenNode
                    {
                        Operand = left,
                        Low = low,
                        High = high,
                        Start = left.Start,
                        Length = high.Start + high.Length - left.Start,
                    };
                }

                default:
                    return left; // a bare additive (boolean fields/functions type-check later)
            }

            CriteriaNode? FinishBinary(CriteriaBinaryOperator op, CriteriaNode leftNode)
            {
                Take();
                var right = ParseAdditive();
                return right is null ? null : Binary(op, leftNode, right);
            }
        }

        private CriteriaNode? ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (left is not null)
            {
                var kind = Peek().Kind;
                if (kind != TokenKind.Plus && kind != TokenKind.Minus)
                    break;
                Take();
                var right = ParseMultiplicative();
                if (right is null)
                    return null;
                left = Binary(kind == TokenKind.Plus ? CriteriaBinaryOperator.Add : CriteriaBinaryOperator.Subtract, left, right);
            }
            return left;
        }

        private CriteriaNode? ParseMultiplicative()
        {
            var left = ParseUnary();
            while (left is not null)
            {
                var kind = Peek().Kind;
                if (kind != TokenKind.Star && kind != TokenKind.Slash && kind != TokenKind.Percent)
                    break;
                Take();
                var right = ParseUnary();
                if (right is null)
                    return null;
                var op = kind switch
                {
                    TokenKind.Star => CriteriaBinaryOperator.Multiply,
                    TokenKind.Slash => CriteriaBinaryOperator.Divide,
                    _ => CriteriaBinaryOperator.Modulo,
                };
                left = Binary(op, left, right);
            }
            return left;
        }

        private CriteriaNode? ParseUnary()
        {
            if (Peek().Kind == TokenKind.Minus)
            {
                var minus = Take();
                var operand = ParseUnary();
                if (operand is null)
                    return null;
                return new CriteriaNegateNode
                {
                    Operand = operand,
                    Start = minus.Start,
                    Length = operand.Start + operand.Length - minus.Start,
                };
            }
            return ParsePrimary();
        }

        private CriteriaNode? ParsePrimary()
        {
            var token = Peek();
            switch (token.Kind)
            {
                case TokenKind.Field:
                    Take();
                    return new CriteriaFieldNode { Name = (string)token.Value!, Start = token.Start, Length = token.Length };

                case TokenKind.Number:
                case TokenKind.String:
                case TokenKind.Date:
                    Take();
                    return new CriteriaLiteralNode { Value = token.Value, Start = token.Start, Length = token.Length };

                case TokenKind.True:
                case TokenKind.False:
                    Take();
                    return new CriteriaLiteralNode { Value = token.Kind == TokenKind.True, Start = token.Start, Length = token.Length };

                case TokenKind.Null:
                    Take();
                    return new CriteriaLiteralNode { Value = null, Start = token.Start, Length = token.Length };

                case TokenKind.Identifier:
                {
                    Take();
                    if (!TakeIf(TokenKind.LeftParen))
                    {
                        Error(token.Start, token.Length,
                              $"Unknown token '{token.Value}' — field references use [brackets]; functions need '('.");
                        return null;
                    }
                    var arguments = new List<CriteriaNode>();
                    if (Peek().Kind != TokenKind.RightParen)
                    {
                        do
                        {
                            var argument = ParseOr(); // full expressions as arguments
                            if (argument is null)
                                return null;
                            arguments.Add(argument);
                        }
                        while (TakeIf(TokenKind.Comma));
                    }
                    if (!TakeIf(TokenKind.RightParen))
                    {
                        Error(Peek().Start, Peek().Length, "Unbalanced parenthesis — expected ')'.");
                        return null;
                    }
                    int end = _lookahead is null ? _position : Peek().Start;
                    return new CriteriaFunctionNode
                    {
                        Name = (string)token.Value!,
                        Arguments = arguments,
                        Start = token.Start,
                        Length = Math.Max(token.Length, end - token.Start),
                    };
                }

                case TokenKind.LeftParen:
                {
                    Take();
                    var inner = ParseOr();
                    if (inner is null)
                        return null;
                    if (!TakeIf(TokenKind.RightParen))
                    {
                        Error(Peek().Start, Peek().Length, "Unbalanced parenthesis — expected ')'.");
                        return null;
                    }
                    return inner;
                }

                case TokenKind.End:
                    Error(token.Start, 1, "Unexpected end of expression.");
                    return null;

                default:
                    Error(token.Start, token.Length, $"Unexpected '{token.Text(_text)}'.");
                    return null;
            }
        }

        private static CriteriaBinaryNode Binary(CriteriaBinaryOperator op, CriteriaNode left, CriteriaNode right) => new()
        {
            Operator = op,
            Left = left,
            Right = right,
            Start = left.Start,
            Length = right.Start + right.Length - left.Start,
        };
    }
}
