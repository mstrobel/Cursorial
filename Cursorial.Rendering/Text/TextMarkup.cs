using System.Globalization;
using System.Text;

using Cursorial.Output;
using Cursorial.Rendering.Content;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Caller-supplied data the markup parser pulls from. Lets the lexically-static markup
/// reference dynamic things — most notably inline <see cref="IContent"/> instances embedded
/// via <c>[content=name/]</c>.
/// </summary>
public sealed class TextMarkupOptions
{
    /// <summary>
    /// Empty options — no registered content. <c>[content=name/]</c> in markup that hits this
    /// options instance throws <see cref="FormatException"/>.
    /// </summary>
    public static TextMarkupOptions Empty { get; } = new();

    /// <summary>
    /// Named content registry consulted by the <c>[content=name/]</c> self-closing tag.
    /// Keys are case-insensitive by convention; supply an
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> with a case-insensitive comparer for the
    /// most ergonomic call-site behavior.
    /// </summary>
    public IReadOnlyDictionary<string, IContent> Content { get; init; } =
        new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default style applied to all text.</summary>
    public Style DefaultStyle { get; init; }
}

/// <summary>
/// BBcode-style markup parser that drives a <see cref="RichTextBuilder"/>. Sugar over the
/// builder's fluent API — programmatic callers can use the builder directly; markup callers
/// (config files, server-supplied text, hand-authored templates) pass strings through this
/// parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tag inventory.</b>
/// </para>
/// <list type="bullet">
/// <item><c>[b][/b]</c>, <c>[i][/i]</c>, <c>[u][/u]</c>, <c>[s][/s]</c> — bold, italic, underline, strikethrough.</item>
/// <item><c>[fg=color][/fg]</c>, <c>[bg=color][/bg]</c> — foreground / background color. Color formats:
///       named (<c>red</c>, <c>brightblue</c>, …), palette index (<c>0</c>–<c>255</c>),
///       hex (<c>#ff0000</c> or <c>#f00</c>).</item>
/// <item><c>[link=uri][/link]</c> — OSC 8 hyperlink target.</item>
/// <item><c>[font=name][/font]</c> — per-grapheme remap; built-ins: <c>fullwidth</c>,
///       <c>doublestruck</c>, <c>smallcaps</c>, <c>superscript</c>, <c>subscript</c>.</item>
/// <item><c>[br/]</c> — hard line break inside a paragraph.</item>
/// <item><c>[content=name/]</c> — inline-content embedding. Resolves <c>name</c> against
///       <see cref="TextMarkupOptions.Content"/>; the content is placed atomically in the
///       paragraph flow (treated as an indivisible word at its measured width).</item>
/// <item><c>[p attrs]…[/p]</c> — explicit paragraph block. Attributes:
///       <c>wrap=word|character|nowrap|overflow</c>, <c>align=left|right|center|justify</c>,
///       <c>trim=none|clip|char|word</c>, <c>maxlines=N</c>.</item>
/// <item><c>[hr]</c> or <c>[hr=style]</c> — horizontal rule. Built-in styles: <c>light</c>,
///       <c>heavy</c>, <c>double</c>, <c>dashed</c>, <c>dotted</c>. Pass a literal grapheme to
///       use a custom glyph.</item>
/// </list>
/// <para>
/// <b>Escape sequences.</b> <c>\[</c>, <c>\]</c>, and <c>\\</c> produce a literal <c>[</c>,
/// <c>]</c>, and <c>\</c> respectively in the output text. All other backslashes are passed
/// through verbatim.
/// </para>
/// <para>
/// <b>Whitespace.</b> Source whitespace (including <c>\n</c>) is preserved as-is — the formatter
/// collapses runs of whitespace at format time. Use <c>[br/]</c> for hard breaks within a
/// paragraph and <c>[p]</c> for explicit paragraph boundaries.
/// </para>
/// <para>
/// <b>Errors.</b> Mismatched closing tags, unknown tag names, and malformed attributes throw
/// <see cref="FormatException"/> with a position-anchored message. The parser is strict by
/// design — silent fallback in markup tends to mask authoring bugs.
/// </para>
/// </remarks>
public static class TextMarkup
{
    /// <summary>Parse <paramref name="markup"/> into a fresh <see cref="RichText"/> document.</summary>
    public static RichText Parse(string markup) => Parse(markup, TextMarkupOptions.Empty);

    /// <summary>Parse with caller-supplied <paramref name="options"/> (named content registry, etc.).</summary>
    public static RichText Parse(string markup, TextMarkupOptions options)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(options);
        var builder = new RichTextBuilder(options.DefaultStyle);
        Parse(markup, builder, options);
        return builder.Build();
    }

    /// <summary>Parse <paramref name="markup"/> into an existing builder, appending to whatever it already holds.</summary>
    public static void Parse(string markup, RichTextBuilder builder) =>
        Parse(markup, builder, TextMarkupOptions.Empty);

    /// <summary>Parse into an existing builder with caller-supplied options.</summary>
    public static void Parse(string markup, RichTextBuilder builder, TextMarkupOptions options)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        new TextMarkupParser(markup, builder, options).Run();
    }

    // ---- Internal lexer ----

    private enum TokenKind { Text, OpenTag, CloseTag, SelfClosingTag }

    private readonly record struct Token(TokenKind Kind, string Name, string Value, string Text, int Position);

    private sealed class TextMarkupLexer(string input)
    {
        private int _pos;

        public Token? Next()
        {
            if (_pos >= input.Length) return null;

            // Tag?
            if (input[_pos] == '[')
            {
                int start = _pos;
                int end = input.IndexOf(']', _pos + 1);
                if (end < 0)
                {
                    // No closing bracket — treat as text from here to end.
                    return ReadText();
                }

                string inside = input[(start + 1)..end];
                _pos = end + 1;

                // Closing tag?
                if (inside.StartsWith('/'))
                    return new Token(TokenKind.CloseTag, NormalizeName(inside[1..]), Value: "", Text: "", start);

                // Self-closing?
                bool selfClosing = inside.EndsWith('/');
                if (selfClosing) inside = inside[..^1].TrimEnd();

                // Split name from attributes / value. Attributes follow either '=' (name=value
                // shorthand) or whitespace (multi-attr form).
                string name, value;
                int eq = inside.IndexOf('=');
                int ws = IndexOfWhitespace(inside);

                if (eq >= 0 && (ws < 0 || eq < ws))
                {
                    // [name=value] shorthand.
                    name = inside[..eq];
                    value = inside[(eq + 1)..];
                }
                else if (ws >= 0)
                {
                    // [name attrs...] form.
                    name = inside[..ws];
                    value = inside[(ws + 1)..].TrimStart();
                }
                else
                {
                    name = inside;
                    value = "";
                }

                return new Token(
                    selfClosing ? TokenKind.SelfClosingTag : TokenKind.OpenTag,
                    NormalizeName(name),
                    value.Trim(),
                    Text: "",
                    start);
            }

            return ReadText();
        }

        private Token ReadText()
        {
            int start = _pos;
            var sb = new StringBuilder();
            while (_pos < input.Length && input[_pos] != '[')
            {
                char c = input[_pos];
                if (c == '\\' && _pos + 1 < input.Length)
                {
                    char next = input[_pos + 1];
                    if (next is '[' or ']' or '\\')
                    {
                        sb.Append(next);
                        _pos += 2;
                        continue;
                    }
                }

                sb.Append(c);
                _pos++;
            }

            return new Token(TokenKind.Text, Name: "", Value: "", sb.ToString(), start);
        }

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return i;
            return -1;
        }

        private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();
    }

    // ---- Internal parser ----

    private sealed class TextMarkupParser(string input, RichTextBuilder builder, TextMarkupOptions options)
    {
        private readonly TextMarkupLexer _lexer = new(input);
        private readonly Stack<(string Name, StyleScope Scope)> _styleStack = new();
        private bool _inExplicitParagraph;

        public void Run()
        {
            while (_lexer.Next() is { } token)
                Dispatch(token);

            if (_styleStack.Count > 0)
                throw new FormatException(
                    $"Unclosed markup tags at end of input: {string.Join(", ", _styleStack.Select(s => '[' + s.Name + ']'))}.");
        }

        private void Dispatch(Token token)
        {
            switch (token.Kind)
            {
                case TokenKind.Text:
                    if (token.Text.Length > 0) builder.Run(token.Text);
                    break;
                case TokenKind.OpenTag:
                    HandleOpen(token);
                    break;
                case TokenKind.CloseTag:
                    HandleClose(token);
                    break;
                case TokenKind.SelfClosingTag:
                    HandleSelfClosing(token);
                    break;
            }
        }

        private void HandleOpen(Token token)
        {
            switch (token.Name)
            {
                case "b": Push(token.Name, builder.Push(options.DefaultStyle.WithAttributes(TextAttributes.Bold))); break;
                case "i": Push(token.Name, builder.Push(options.DefaultStyle.WithAttributes(TextAttributes.Italic))); break;
                case "u": Push(token.Name, builder.Push(options.DefaultStyle.WithAttributes(TextAttributes.Underline))); break;
                case "s": Push(token.Name, builder.Push(options.DefaultStyle.WithAttributes(TextAttributes.Strikethrough))); break;
                case "fg":
                    Push(token.Name, builder.Push(options.DefaultStyle.WithForeground(ParseColor(token.Value, token.Position))));
                    break;
                case "bg":
                    Push(token.Name, builder.Push(options.DefaultStyle.WithBackground(ParseColor(token.Value, token.Position))));
                    break;
                case "link":
                    if (string.IsNullOrEmpty(token.Value))
                        throw Error(token.Position, "[link] requires a URI: [link=https://example.com].");
                    Push(token.Name, builder.PushHyperlink(token.Value));
                    break;
                case "font":
                    Push(token.Name, builder.PushMap(ResolveGlyphMap(token.Value, token.Position)));
                    break;
                case "p":
                    OpenParagraph(token);
                    break;
                default:
                    throw Error(token.Position, $"Unknown markup tag [{token.Name}].");
            }
        }

        private void HandleClose(Token token)
        {
            if (token.Name == "p")
            {
                if (!_inExplicitParagraph)
                    throw Error(token.Position, "[/p] without a matching [p].");
                builder.EndParagraph();
                _inExplicitParagraph = false;
                return;
            }

            if (_styleStack.Count == 0)
                throw Error(token.Position, $"[/{token.Name}] with no matching opening tag.");

            var top = _styleStack.Pop();
            if (top.Name != token.Name)
                throw Error(token.Position, $"Mismatched closing tag: expected [/{top.Name}], got [/{token.Name}].");

            top.Scope.Dispose();
        }

        private void HandleSelfClosing(Token token)
        {
            switch (token.Name)
            {
                case "br":
                    builder.LineBreak();
                    break;
                case "hr":
                    EmitHorizontalRule(token);
                    break;
                case "content":
                    EmitContent(token);
                    break;
                default:
                    throw Error(token.Position, $"Unknown self-closing markup tag [{token.Name}/].");
            }
        }

        private void EmitContent(Token token)
        {
            if (string.IsNullOrEmpty(token.Value))
                throw Error(token.Position, "[content/] requires a name: [content=icon/]. Register names via TextMarkupOptions.Content.");

            if (!options.Content.TryGetValue(token.Value, out var content))
                throw Error(token.Position, $"No content registered under '{token.Value}'. Add it to TextMarkupOptions.Content before parsing.");

            builder.InlineContent(content);
        }

        private void Push(string name, StyleScope scope)
        {
            _styleStack.Push((name, scope));
        }

        private void OpenParagraph(Token token)
        {
            if (_inExplicitParagraph)
                throw Error(token.Position, "[p] cannot be nested inside another [p].");

            var attrs = ParseAttributes(token.Value, token.Position);
            var wrap = attrs.TryGetValue("wrap", out var w) ? ParseWrapMode(w, token.Position) : WrapMode.WordWrap;
            var align = attrs.TryGetValue("align", out var a) ? ParseAlignment(a, token.Position) : TextAlignment.Left;
            var trim = attrs.TryGetValue("trim", out var t) ? ParseTrimming(t, token.Position) : TextTrimming.None;

            int? maxLines = attrs.TryGetValue("maxlines", out var m)
                ? int.Parse(m, CultureInfo.InvariantCulture)
                : null;

            builder.Paragraph(wrap, align, trim, maxLines);
            _inExplicitParagraph = true;
        }

        private void EmitHorizontalRule(Token token)
        {
            // [hr] uses default light glyph; [hr=light|heavy|double|dashed|dotted] or [hr=<glyph>].
            string glyph = string.IsNullOrEmpty(token.Value) ? "─" : ResolveHorizontalRuleGlyph(token.Value);
            builder.HorizontalRule(glyph);
        }

        private static string ResolveHorizontalRuleGlyph(string value) =>
            value switch
            {
                "light"  => "─",
                "heavy"  => "━",
                "double" => "═",
                "dashed" => "┄",
                "dotted" => "┈",
                _        => value // assume a literal grapheme
            };

        private static Dictionary<string, string> ParseAttributes(string raw, int position)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            int i = 0;
            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                if (i >= raw.Length) break;

                int eq = raw.IndexOf('=', i);
                if (eq < 0)
                    throw Error(position, $"Malformed attribute list: expected key=value, got '{raw[i..]}'.");

                string key = raw[i..eq].Trim();
                int valueStart = eq + 1;

                if (valueStart < raw.Length && raw[valueStart] == '"')
                {
                    int closeQuote = raw.IndexOf('"', valueStart + 1);
                    if (closeQuote < 0)
                        throw Error(position, $"Unterminated quoted attribute value for '{key}'.");
                    string quoted = raw[(valueStart + 1)..closeQuote];
                    result[key] = quoted;
                    i = closeQuote + 1;
                    continue;
                }

                int valueEnd = valueStart;
                while (valueEnd < raw.Length && !char.IsWhiteSpace(raw[valueEnd])) valueEnd++;
                result[key] = raw[valueStart..valueEnd];
                i = valueEnd;
            }

            return result;
        }

        private static WrapMode ParseWrapMode(string value, int position)
            => value.ToLowerInvariant() switch
               {
                   "word"                => WrapMode.WordWrap,
                   "character" or "char" => WrapMode.CharacterWrap,
                   "nowrap" or "none"    => WrapMode.NoWrap,
                   "overflow"            => WrapMode.WordWrapOverflow,
                   _                     => throw Error(position, $"Unknown wrap mode '{value}'. Use word | character | nowrap | overflow.")
               };

        private static TextAlignment ParseAlignment(string value, int position)
            => value.ToLowerInvariant() switch
               {
                   "left"               => TextAlignment.Left,
                   "right"              => TextAlignment.Right,
                   "center" or "centre" => TextAlignment.Center,
                   "justify"            => TextAlignment.Justify,
                   _                    => throw Error(position, $"Unknown alignment '{value}'. Use left | right | center | justify.")
               };

        private static TextTrimming ParseTrimming(string value, int position)
            => value.ToLowerInvariant() switch
               {
                   "none"                => TextTrimming.None,
                   "clip"                => TextTrimming.ClipFromEnd,
                   "char" or "character" => TextTrimming.CharacterEllipsis,
                   "word"                => TextTrimming.WordEllipsis,
                   _                     => throw Error(position, $"Unknown trim mode '{value}'. Use none | clip | char | word.")
               };

        private static IGlyphMap ResolveGlyphMap(string name, int position)
            => name.ToLowerInvariant() switch
               {
                   "identity" or ""           => GlyphMaps.Identity,
                   "fullwidth" or "full"      => GlyphMaps.Fullwidth,
                   "doublestruck" or "double" => GlyphMaps.DoubleStruck,
                   "smallcaps" or "smcap"     => GlyphMaps.SmallCaps,
                   "superscript" or "super"   => GlyphMaps.Superscript,
                   "subscript" or "sub"       => GlyphMaps.Subscript,
                   _ => throw Error(position, 
                                    $"Unknown glyph map '{name}'. Built-ins: fullwidth (full), doublestruck (double), " +
                                    $"smallcaps (smcap), superscript (super), subscript (sub).")
               };

        // ---- Color parsing ----

        private static Color ParseColor(string raw, int position)
        {
            if (string.IsNullOrEmpty(raw))
                throw Error(position, "Color tag requires a value (named, palette index, or #hex).");

            // Hex: #rgb or #rrggbb.
            if (raw.StartsWith('#'))
                return ParseHexColor(raw, position);

            // Palette index?
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                if (index is < 0 or > 255)
                    throw Error(position, $"Palette index must be 0–255, got {index}.");
                return Color.FromPalette((byte) index);
            }

            // Named?
            if (NamedColors.TryGetValue(raw.ToLowerInvariant(), out byte palette))
                return Color.FromPalette(palette);

            throw Error(position, $"Unrecognized color '{raw}'. Use a name, palette index 0–255, or #hex.");
        }

        private static Color ParseHexColor(string raw, int position)
        {
            string hex = raw[1..];
            byte r, g, b;
            switch (hex.Length)
            {
                case 3:
                    r = (byte) (ParseHexDigit(hex[0], position) * 17);
                    g = (byte) (ParseHexDigit(hex[1], position) * 17);
                    b = (byte) (ParseHexDigit(hex[2], position) * 17);
                    return Color.FromRgb(r, g, b);
                case 6:
                    r = (byte) ((ParseHexDigit(hex[0], position) << 4) | ParseHexDigit(hex[1], position));
                    g = (byte) ((ParseHexDigit(hex[2], position) << 4) | ParseHexDigit(hex[3], position));
                    b = (byte) ((ParseHexDigit(hex[4], position) << 4) | ParseHexDigit(hex[5], position));
                    return Color.FromRgb(r, g, b);
                default:
                    throw Error(position, $"Hex color must be #rgb or #rrggbb, got '{raw}'.");
            }
        }

        private static int ParseHexDigit(char c, int position)
            => c switch
               {
                   >= '0' and <= '9' => c - '0',
                   >= 'a' and <= 'f' => c - 'a' + 10,
                   >= 'A' and <= 'F' => c - 'A' + 10,
                   _                 => throw Error(position, $"Invalid hex digit '{c}'.")
               };

        private static readonly Dictionary<string, byte> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = 0, ["red"] = 1, ["green"] = 2, ["yellow"] = 3,
            ["blue"] = 4, ["magenta"] = 5, ["cyan"] = 6, ["white"] = 7,
            ["brightblack"] = 8, ["gray"] = 8, ["grey"] = 8,
            ["brightred"] = 9, ["brightgreen"] = 10, ["brightyellow"] = 11,
            ["brightblue"] = 12, ["brightmagenta"] = 13, ["brightcyan"] = 14,
            ["brightwhite"] = 15
        };

        private static FormatException Error(int position, string message) =>
            new($"Markup parse error at position {position}: {message}");
    }
}
