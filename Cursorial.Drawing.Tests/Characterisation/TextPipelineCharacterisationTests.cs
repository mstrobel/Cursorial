// ---------------------------------------------------------------------------------------------------
// MIGRATION SCAFFOLDING — see Characterisation/README-scaffolding.md.
//
// A characterisation ("golden master") harness pinning the CURRENT behaviour of Cursorial's text
// pipeline, captured before the FormattedTextRun style-carrier migration: runs today carry a resolved
// CellStyle, and are to carry a StyleDeltaTemplate plus a sampling frame instead, so the UI layer stops
// naming the back-buffer format. That migration is supposed to be PURELY STRUCTURAL — not one glyph
// moves, not one output byte changes — and these baselines are what makes "supposed to" verifiable.
//
// These tests assert nothing about whether the current behaviour is RIGHT. They assert only that it is
// UNCHANGED. Do not treat a baseline as a specification, and do not add cases here that belong in a
// behavioural test.
//
// WHEN TO DELETE: once the carrier migration has landed and its own tests cover the behaviour, delete
// the whole Cursorial.Drawing.Tests/Characterisation directory (and, in Cursorial.Rendering.Tests, the
// FragmentEmissionSlicer link from Cursorial.Drawing.Tests.csproj — the slicer itself stays, it is used
// by FrameRendererDefaultColorTests). A characterisation harness that outlives its migration is a
// maintenance tax and a source of spurious diffs.
//
// See CharacterisationBaseline for how to regenerate a baseline deliberately.
// ---------------------------------------------------------------------------------------------------

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Text;
using Cursorial.Terminal;
using Cursorial.Tests.Rendering;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing.Characterisation;

/// <summary>
/// Three tiers of dump over one shared corpus (<see cref="TextCorpus.All"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 1 — geometry.</b> <see cref="FormattedText.ToPlainText"/>, one string per case. It calls the
/// REAL paint path (<c>Paint(view, bounds, OutputCapabilities.None)</c>), not a plain-text shortcut, so it
/// exercises run dispatch, wrapping, trimming and the FIGlet arm; any glyph that moves shows up as a diff.
/// Cheapest and most total of the three. Its limit is in its capabilities: at
/// <see cref="OutputCapabilities.None"/> every capability-dependent arm takes its fallback route.
/// </para>
/// <para>
/// <b>Tier 2 — styling.</b> Every cell of a painted <see cref="CellBuffer"/>, at truecolor and at Ansi256:
/// grapheme, foreground, background, attributes, underline shape, underline colour (plus cell kind and the
/// OSC 8 anchor, which ride the same <c>CellStyle</c>). This is what tier 1 cannot see. Painting does not
/// quantize — quantization is an emission-time concern — so the two tiers are expected to agree today, and
/// that agreement is itself a property worth pinning: a carrier migration must not leak colour depth into
/// paint. Text sizing is OFF at this tier so sized text paints its FALLBACK arm into cells; tier 3 covers
/// the fragment arm.
/// </para>
/// <para>
/// <b>Tier 3 — sized text / fragments.</b> Sized text rides an OSC 66 fragment and is never painted into
/// cells, so it cannot be inspected by reading a buffer. What IS verifiable is what we emit: the DECSC…DECRC
/// brackets a <see cref="FrameRenderer"/> wraps each fragment in, sliced by the shared
/// <see cref="FragmentEmissionSlicer"/>. The dump captures the backdrop SGR as well as the OSC 66 payload —
/// getting the right background onto sized text is the historically fragile part (it will not take
/// transparent, and a brush cannot be sampled at normal cell resolution, so FrameRenderer hands it the
/// style of its ANCHOR CELL), and that path was last changed by commit 2137e58b.
/// </para>
/// <para>
/// <b>Determinism.</b> No timestamps, no absolute paths, no culture-sensitive formatting (every number goes
/// through <see cref="CultureInfo.InvariantCulture"/>), and no hash-ordered enumeration: the corpus is a
/// hand-ordered array, and fragment emissions — whose renderer-side order follows a
/// <see cref="Dictionary{TKey,TValue}"/> — are re-sorted by their parsed anchor before being dumped.
/// </para>
/// </remarks>
public class TextPipelineCharacterisationTests
{
    // A terminal that reports its own default colours, so the buffer's derived blank is a real RGB pair
    // (the same shape FrameRendererDefaultColorTests uses) rather than Color.Default.
    private static readonly Color TerminalDefaultForeground = Color.FromRgb(205, 214, 244);
    private static readonly Color TerminalDefaultBackground = Color.FromRgb(30, 30, 46);

    /// <summary>
    /// A capability tier. <paramref name="textSizing"/> is off for the styling tiers so sized text paints
    /// its fallback into cells, and on for the emission tier so it produces a fragment.
    /// </summary>
    private static TerminalCapabilities Capabilities(ColorDepth depth, bool textSizing) =>
        TerminalCapabilities.None with
        {
            Output = OutputCapabilities.None with
                     {
                         Color = ColorCapabilities.None with
                                 {
                                     Depth = depth,
                                     DefaultColorReset = true,
                                     DefaultForeground = TerminalDefaultForeground,
                                     DefaultBackground = TerminalDefaultBackground
                                 },
                         Styling = new TextStylingCapabilities(
                             Italic: true, Underline: true, ExtendedUnderline: true,
                             ColoredUnderline: true, Strikethrough: true, Overline: true, Hyperlinks: true),
                         TextSizing = textSizing
                                          ? new TextSizingCapabilities(Width: true, Scale: true)
                                          : TextSizingCapabilities.None
                     }
        };

    // ---- Tier 1 — geometry ------------------------------------------------------------------------

    [Fact]
    public void Tier1_Geometry_MatchesBaseline()
    {
        var dump = new StringBuilder();
        Header(dump,
               "TIER 1 — GEOMETRY",
               "FormattedText.ToPlainText() per corpus case, through the real paint path at",
               "OutputCapabilities.None. Metadata is deliberately limited to block- and line-level geometry:",
               "run-level shape is exactly what the carrier migration is allowed to change.");

        foreach (var textCase in TextCorpus.All)
        {
            Section(dump, textCase);

            var formatted = Format(textCase, OutputCapabilities.None);
            AppendGeometry(dump, textCase, formatted);
        }

        CharacterisationBaseline.Approve("tier1-geometry.txt", dump.ToString());
    }

    // ---- Tier 2 — styling -------------------------------------------------------------------------

    [Theory]
    [InlineData(ColorDepth.Truecolor, "tier2-styling-truecolor.txt")]
    [InlineData(ColorDepth.Ansi256, "tier2-styling-ansi256.txt")]
    public void Tier2_Styling_MatchesBaseline(ColorDepth depth, string baselineName)
    {
        var capabilities = Capabilities(depth, textSizing: false);

        var dump = new StringBuilder();
        Header(dump,
               "TIER 2 — STYLING (" + depth + ")",
               "Every cell of the painted CellBuffer: kind, grapheme, foreground, background, attributes,",
               "underline shape, underline colour, OSC 8 anchor. Text sizing is OFF at this tier, so sized",
               "text paints its FALLBACK arm into cells; the fragment arm is tier 3.");

        foreach (var textCase in TextCorpus.All)
        {
            Section(dump, textCase);

            var formatted = Format(textCase, capabilities.Output);
            var bounds = PaintBounds(textCase);
            var buffer = new CellBuffer(textCase.PaintColumns, textCase.PaintRows, capabilities);

            var painted = formatted.Paint(buffer.AsView(), bounds, capabilities.Output,
                                          Resolver(textCase, formatted, bounds));

            dump.Append("paint: bounds=").Append(Format(bounds))
                .Append(" returned=").Append(Format(painted))
                .Append(" fragments=").Append(Int(buffer.Fragments.Count))
                .Append('\n');

            AppendCells(dump, buffer);
        }

        CharacterisationBaseline.Approve(baselineName, dump.ToString());
    }

    // ---- Tier 3 — sized text / fragment emission ---------------------------------------------------

    [Fact]
    public void Tier3_FragmentEmission_MatchesBaseline()
    {
        var dump = new StringBuilder();
        Header(dump,
               "TIER 3 — SIZED TEXT / FRAGMENT EMISSION",
               "The VT bytes. Sized text rides an OSC 66 fragment and is never painted into cells, so the",
               "only verifiable artefact is what we emit: the DECSC…DECRC bracket per fragment, captured at",
               "truecolor and Ansi256. The backdrop SGR is captured alongside the payload — it is the anchor",
               "cell's style, and the historically fragile part.",
               "Only the corpus cases that register a fragment (TextCase.EmitsFragments) appear below.");

        foreach (var textCase in TextCorpus.All)
        {
            if (!textCase.EmitsFragments) continue;

            Section(dump, textCase);

            foreach (var depth in (ReadOnlySpan<ColorDepth>) [ColorDepth.Truecolor, ColorDepth.Ansi256])
                AppendEmission(dump, textCase, depth);
        }

        CharacterisationBaseline.Approve("tier3-fragment-emission.txt", dump.ToString());
    }

    // ---- Shared pipeline drive ---------------------------------------------------------------------

    private static FormattedText Format(TextCase textCase, OutputCapabilities capabilities)
        => textCase.Formatter().Format(textCase.Document(), textCase.Columns, textCase.MaxRows,
                                       capabilities, textCase.FillEntireBounds);

    private static Rect PaintBounds(TextCase textCase) => new(0, 0, textCase.PaintColumns, textCase.PaintRows);

    /// <summary>
    /// The real Drawing-layer resolver, built exactly as <c>DrawingContext.DrawFormattedText</c> builds it —
    /// so the brush cases exercise the production consumer of <c>BrushedTextResolver</c> rather than a
    /// test-local stand-in, without dragging a Scene and its compositor into the dump.
    /// </summary>
    private static BrushedTextResolver? Resolver(TextCase textCase, FormattedText formatted, in Rect bounds)
        => textCase.UsesResolver
               ? DrawingContext.CreateBrushResolver(textCase.DocumentBrush, formatted.DefaultStyle.Foreground,
                                                   bounds, textCase.BaseAttributes, textCase.BaseUnderlineShape)
               : null;

    // ---- Tier 1 dump -------------------------------------------------------------------------------

    private static void AppendGeometry(StringBuilder dump, TextCase textCase, FormattedText formatted)
    {
        dump.Append("format: columns=").Append(Int(textCase.Columns))
            .Append(" maxRows=").Append(textCase.MaxRows is { } rows ? Int(rows) : "-")
            .Append(" fillEntireBounds=").Append(textCase.FillEntireBounds ? "yes" : "no")
            .Append('\n');

        dump.Append("result: size=").Append(Format(formatted.Size))
            .Append(" provided=").Append(Int(formatted.ProvidedColumns))
            .Append(" hasTrimmedLines=").Append(formatted.HasTrimmedLines ? "yes" : "no")
            .Append(" blocks=").Append(Int(formatted.Blocks.Length))
            .Append('\n');

        for (int i = 0; i < formatted.Blocks.Length; i++)
        {
            var block = formatted.Blocks[i];

            dump.Append("  block[").Append(Int(i)).Append("] ").Append(block.GetType().Name)
                .Append(" size=").Append(Format(block.Size))
                .Append(" align=").Append(block.Alignment.ToString())
                .Append(" margin=").Append(Format(block.Margin))
                .Append(" trimmed=").Append(block.HasTrimmedLines ? "yes" : "no");

            if (block is FormattedParagraph paragraph)
            {
                dump.Append(" valign=").Append(paragraph.VerticalAlignment.ToString())
                    .Append(" lines=").Append(Int(paragraph.Lines.Length)).Append('\n');

                for (int l = 0; l < paragraph.Lines.Length; l++)
                {
                    var line = paragraph.Lines[l];
                    dump.Append("    line[").Append(Int(l)).Append("] columns=").Append(Int(line.Columns))
                        .Append(" rows=").Append(Int(line.Rows))
                        .Append(" trimmed=").Append(line.Trimmed ? "yes" : "no")
                        .Append('\n');
                }
            }
            else if (block is FormattedHorizontalRule rule)
            {
                dump.Append(" glyph=").Append(Quote(rule.Glyph)).Append('\n');
            }
            else
            {
                dump.Append('\n');
            }
        }

        // The real paint path: ToPlainText paints into a scratch buffer at OutputCapabilities.None.
        // Bracketed per line so trailing whitespace is visible in the baseline.
        var bounds = PaintBounds(textCase);
        dump.Append("plain(bounds=").Append(Format(bounds)).Append("):\n");

        foreach (var line in Render(() => formatted.ToPlainText(bounds)).Split('\n'))
            dump.Append("  |").Append(Escape(line)).Append("|\n");

        // The no-argument overload sizes its own scratch buffer from ProvidedColumns × Size.Rows, which
        // is a different code path (and throws outright on a degenerate document). Capture it too — it is
        // the overload most call sites use.
        dump.Append("plain(natural):\n");

        foreach (var line in Render(() => formatted.ToPlainText()).Split('\n'))
            dump.Append("  |").Append(Escape(line)).Append("|\n");
    }

    /// <summary>Runs a dump producer, recording a thrown exception's type + message rather than failing.</summary>
    /// <remarks>
    /// A characterisation harness records what the code DOES. <c>ToPlainText()</c> throws on a document with
    /// no rows (it allocates a scratch buffer of <c>ProvidedColumns × Size.Rows</c>), and pinning that is
    /// strictly better than excluding the empty-document case: if the migration changes it, we see it.
    /// </remarks>
    private static string Render(Func<string> producer)
    {
        try
        {
            return producer();
        }
        catch (Exception ex)
        {
            return "<throws " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim() + ">";
        }
    }

    // ---- Tier 2 dump -------------------------------------------------------------------------------

    private static void AppendCells(StringBuilder dump, CellBuffer buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int column = 0; column < buffer.Columns; column++)
            {
                var cell = buffer[column, row];

                dump.Append("  r").Append(Coordinate(row)).Append(" c").Append(Coordinate(column))
                    .Append(' ').Append(Kind(cell.Kind))
                    .Append(' ').Append(Quote(cell.Grapheme))
                    .Append(' ').Append(Format(cell.Style))
                    .Append('\n');
            }
        }
    }

    // ---- Tier 3 dump -------------------------------------------------------------------------------

    private static void AppendEmission(StringBuilder dump, TextCase textCase, ColorDepth depth)
    {
        var capabilities = Capabilities(depth, textSizing: true);
        var formatted = Format(textCase, capabilities.Output);
        var bounds = PaintBounds(textCase);
        var buffer = new CellBuffer(textCase.PaintColumns, textCase.PaintRows, capabilities);

        formatted.Paint(buffer.AsView(), bounds, capabilities.Output, Resolver(textCase, formatted, bounds));

        dump.Append("tier: ").Append(depth.ToString()).Append('\n');

        // The renderer walks a Dictionary, so re-sort by anchor before dumping. Each bracket is
        // self-contained (it opens with an absolute SGR), so ordering the dump cannot change its content.
        // FragmentDictionary is a ref struct, so this is a hand-rolled projection rather than LINQ.
        var registered = new List<(int Column, int Row, CellBuffer.FragmentEntry Entry)>();

        foreach (var (anchor, entry) in buffer.Fragments)
            registered.Add((anchor.Column, anchor.Row, entry));

        registered.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));

        dump.Append("  registered: ").Append(Int(registered.Count)).Append('\n');

        foreach (var (column, row, entry) in registered)
        {
            dump.Append("    anchor=(").Append(Int(column)).Append(',').Append(Int(row)).Append(") ")
                .Append(entry.Fragment.GetType().Name)
                .Append(" size=").Append(Format(entry.Fragment.GetSize()))
                .Append(" supported=").Append(entry.Fragment.IsSupported(capabilities.Output) ? "yes" : "no")
                .Append(" anchorStyle{").Append(Format(entry.AnchorStyle)).Append("}\n");

            if (entry.Fragment is SizedTextFragment sized)
            {
                dump.Append("      sizing: scale=").Append(Int(sized.Sizing.Scale))
                    .Append(" width=").Append(Int(sized.Sizing.Width))
                    .Append(" n=").Append(Int(sized.Sizing.Numerator))
                    .Append(" d=").Append(Int(sized.Sizing.Denominator))
                    .Append(" v=").Append(sized.Sizing.Vertical.ToString())
                    .Append(" h=").Append(sized.Sizing.Horizontal.ToString())
                    .Append('\n');

                dump.Append("      style: ").Append(Format(sized.Style)).Append('\n');

                for (int i = 0; i < sized.Lines.Length; i++)
                    dump.Append("      line[").Append(Int(i)).Append("] ").Append(Quote(sized.Lines[i])).Append('\n');
            }
        }

        var renderer = new FrameRenderer(capabilities.Output);
        var writer = new ArrayBufferWriter<byte>();
        renderer.Render(buffer, writer);
        string output = Encoding.UTF8.GetString(writer.WrittenSpan);

        var emissions = FragmentEmissionSlicer.All(output)
                                              .Select(bracket => (Anchor: ParseAnchor(bracket), Bracket: bracket))
                                              .OrderBy(e => e.Anchor.Row).ThenBy(e => e.Anchor.Column)
                                              .ThenBy(e => e.Bracket, StringComparer.Ordinal)
                                              .ToImmutableArray();

        dump.Append("  emissions: ").Append(Int(emissions.Length)).Append('\n');

        for (int i = 0; i < emissions.Length; i++)
        {
            var (anchor, bracket) = emissions[i];

            dump.Append("    [").Append(Int(i)).Append("] cup=(row=").Append(Int(anchor.Row))
                .Append(",col=").Append(Int(anchor.Column)).Append(")\n");

            // The SGR parameter lists, in emission order — the backdrop write, then any delta to the
            // fragment's own anchor style. This is the half that is NOT the OSC 66 payload, and the half
            // that moved most recently.
            dump.Append("      sgr: ").Append(string.Join(" | ", Sgr(bracket))).Append('\n');
            dump.Append("      bytes: ").Append(EscapeVt(bracket)).Append('\n');
        }
    }

    /// <summary>The parameter list of every SGR sequence in <paramref name="output"/>, in emission order.</summary>
    private static string[] Sgr(string output) =>
        Regex.Matches(output, "\x1b\\[([0-9;:]*)m").Select(m => m.Groups[1].Value).ToArray();

    /// <summary>
    /// The 1-based CUP coordinates the bracket opens with, converted back to 0-based cells. Used only to
    /// order the dump deterministically; a bracket without one sorts first.
    /// </summary>
    private static (int Row, int Column) ParseAnchor(string bracket)
    {
        var match = Regex.Match(bracket, "\x1b\\[([0-9]+);([0-9]+)H");
        if (!match.Success) return (-1, -1);

        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1,
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1);
    }

    // ---- Deterministic formatting ------------------------------------------------------------------

    private static void Header(StringBuilder dump, string title, params string[] notes)
    {
        dump.Append("# ").Append(title).Append('\n');
        dump.Append("#\n");
        dump.Append("# MIGRATION SCAFFOLDING — a characterisation baseline, not a specification. Regenerate with\n");
        dump.Append("# ").Append(CharacterisationBaseline.RegenerateVariable).Append("=1 and review the diff.\n");
        dump.Append("#\n");

        foreach (var note in notes)
            dump.Append("# ").Append(note).Append('\n');

        dump.Append("# corpus: ").Append(Int(TextCorpus.All.Length)).Append(" cases\n");
    }

    private static void Section(StringBuilder dump, TextCase textCase)
    {
        dump.Append("\n== ").Append(textCase.Id).Append(" ==\n");
        dump.Append("desc: ").Append(textCase.Description).Append('\n');
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Coordinate(int value) => value.ToString("D2", CultureInfo.InvariantCulture);

    private static string Format(in Rect rect) =>
        Int(rect.Column) + "," + Int(rect.Row) + " " + Int(rect.Columns) + "x" + Int(rect.Rows);

    private static string Format(in Size size) => Int(size.Columns) + "x" + Int(size.Rows);

    private static string Format(in Margins margins) =>
        "(" + Int(margins.Left) + "," + Int(margins.Top) + "," + Int(margins.Right) + "," + Int(margins.Bottom) + ")";

    private static string Format(in CellStyle style) =>
        "fg=" + Format(style.Foreground) +
        " bg=" + Format(style.Background) +
        " at=" + Format(style.Attributes) +
        " ul=" + style.UnderlineStyle +
        " uc=" + Format(style.UnderlineColor) +
        " hl=" + Format(style.Hyperlink);

    /// <summary>
    /// A colour, written by KIND: <c>.</c> for the terminal default, <c>@N</c> for a palette index,
    /// <c>#rrggbb</c> (plus alpha when not opaque) for truecolor. Palette 12 and rgb(0,0,255) are
    /// different values that must never dump the same way.
    /// </summary>
    private static string Format(in Color color) => color.Kind switch
    {
        ColorKind.Default => ".",
        ColorKind.Palette => "@" + Int(color.PaletteIndex),
        _ => "#" + color.Red.ToString("x2", CultureInfo.InvariantCulture)
                 + color.Green.ToString("x2", CultureInfo.InvariantCulture)
                 + color.Blue.ToString("x2", CultureInfo.InvariantCulture)
                 + (color.Alpha == 255 ? "" : color.Alpha.ToString("x2", CultureInfo.InvariantCulture))
    };

    /// <summary>
    /// Set attribute flags, in declaration order and joined with <c>|</c>. Spelled out rather than
    /// delegated to <see cref="Enum.ToString()"/> so the separator and the ordering are ours.
    /// </summary>
    private static string Format(TextAttributes attributes)
    {
        if (attributes == TextAttributes.None) return ".";

        var names = new List<string>();

        foreach (var flag in s_attributeOrder)
        {
            if ((attributes & flag) == flag) names.Add(flag.ToString());
        }

        // Anything the enum gains later still shows up, rather than silently vanishing from the dump.
        var covered = s_attributeOrder.Aggregate(TextAttributes.None, static (a, b) => a | b);
        if ((attributes & ~covered) != TextAttributes.None)
            names.Add("+0x" + ((ushort) (attributes & ~covered)).ToString("x", CultureInfo.InvariantCulture));

        return string.Join("|", names);
    }

    private static readonly TextAttributes[] s_attributeOrder = Enum.GetValues<TextAttributes>()
                                                                   .Where(static a => a != TextAttributes.None)
                                                                   .OrderBy(static a => (ushort) a)
                                                                   .ToArray();

    private static string Format(in Hyperlink hyperlink) =>
        hyperlink.IsEmpty ? "." : Quote(hyperlink.Uri) + (hyperlink.Id is { } id ? "#" + Quote(id) : "");

    private static string Kind(CellKind kind) => kind switch
    {
        CellKind.WideLeft => "WL",
        CellKind.WideContinuation => "WC",
        _ => "S "
    };

    private static string Quote(string? text) => text is null ? "<null>" : "\"" + Escape(text) + "\"";

    /// <summary>Escapes quotes, backslashes and anything non-printable so a dump line stays one line.</summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c == '­' || c == '​')
                        escaped.Append("\\u").Append(((int) c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>
    /// VT bytes rendered readable: control characters become mnemonics so the escape structure of an OSC 66
    /// emission is legible in a diff, and nothing can break the line.
    /// </summary>
    private static string EscapeVt(string bytes)
    {
        var escaped = new StringBuilder(bytes.Length * 2);

        foreach (var c in bytes)
        {
            switch (c)
            {
                case '\u001b': escaped.Append("<ESC>"); break;
                case '\u0007': escaped.Append("<BEL>"); break;
                case '\n': escaped.Append("<LF>"); break;
                case '\r': escaped.Append("<CR>"); break;
                default:
                    if (char.IsControl(c))
                        escaped.Append("<0x").Append(((int) c).ToString("x2", CultureInfo.InvariantCulture)).Append('>');
                    else
                        escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    // ---- Determinism guard --------------------------------------------------------------------------

    /// <summary>
    /// Generating each tier twice in one process must produce identical bytes. A flaky characterisation
    /// test is worse than a missing one — it trains people to ignore diffs — so the harness proves its own
    /// repeatability rather than relying on the approval comparison to notice.
    /// </summary>
    [Fact]
    public void EveryTierIsReproducibleWithinAProcess()
    {
        Assert.Equal(GeometryDump(), GeometryDump());
        Assert.Equal(StylingDump(ColorDepth.Truecolor), StylingDump(ColorDepth.Truecolor));
        Assert.Equal(StylingDump(ColorDepth.Ansi256), StylingDump(ColorDepth.Ansi256));
        Assert.Equal(EmissionDump(), EmissionDump());
    }

    /// <summary>Corpus ids must be unique and non-empty — they are how a diff report names the damage.</summary>
    [Fact]
    public void CorpusIdsAreUniqueAndStable()
    {
        Assert.All(TextCorpus.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
        Assert.All(TextCorpus.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.Equal(TextCorpus.All.Length, TextCorpus.All.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    private static string GeometryDump()
    {
        var dump = new StringBuilder();
        foreach (var textCase in TextCorpus.All)
        {
            Section(dump, textCase);
            AppendGeometry(dump, textCase, Format(textCase, OutputCapabilities.None));
        }

        return dump.ToString();
    }

    private static string StylingDump(ColorDepth depth)
    {
        var capabilities = Capabilities(depth, textSizing: false);
        var dump = new StringBuilder();

        foreach (var textCase in TextCorpus.All)
        {
            Section(dump, textCase);
            var formatted = Format(textCase, capabilities.Output);
            var bounds = PaintBounds(textCase);
            var buffer = new CellBuffer(textCase.PaintColumns, textCase.PaintRows, capabilities);
            formatted.Paint(buffer.AsView(), bounds, capabilities.Output, Resolver(textCase, formatted, bounds));
            AppendCells(dump, buffer);
        }

        return dump.ToString();
    }

    private static string EmissionDump()
    {
        var dump = new StringBuilder();

        foreach (var textCase in TextCorpus.All)
        {
            if (!textCase.EmitsFragments) continue;

            Section(dump, textCase);
            AppendEmission(dump, textCase, ColorDepth.Truecolor);
            AppendEmission(dump, textCase, ColorDepth.Ansi256);
        }

        return dump.ToString();
    }
}
