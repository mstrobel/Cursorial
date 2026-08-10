using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// <see cref="IGlyphFont"/>'s brushed paint takes a <see cref="BrushedStyle"/>, replacing the
/// <c>GlyphStyleProvider</c> callback that existed only because <see cref="IBrush"/> used to live above this
/// assembly. Two things follow, and both are pinned here: a delta states only the channels it owns — the
/// rest fall through to the destination cells, and a caller with a whole-style base restates it under the
/// delta via <see cref="BrushedStyle.From"/> / <see cref="BrushedStyle.FromInk"/> — and
/// <see cref="BrushedStyle.IsUniform"/> is READABLE — a callback was opaque, so every cell had to call it.
/// </summary>
public class GlyphBrushedStylePaintTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Green = Color.FromRgb(0, 200, 0);

    /// <summary>A base non-default on every channel a careless delta could drop.</summary>
    private static CellStyle Rich =>
        CellStyle.Default
                 .WithForeground(Color.FromRgb(255, 0, 0))
                 .WithBackground(Blue)
                 .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                 .WithUnderlineStyle(UnderlineStyle.Curly)
                 .WithUnderlineColor(Green)
                 .WithHyperlink("https://example.invalid");

    // ───────────────────────────── test brushes ─────────────────────────────

    /// <summary>A position-independent brush that counts how often it is asked.</summary>
    private sealed class UniformBrush(Color color) : IBrush
    {
        public int Samples { get; private set; }
        public bool IsUniform => true;
        public Color ColorAt(int column, int row, Rect bounds) { Samples++; return color; }
    }

    /// <summary>A brush whose red channel IS the cell's column offset within the sampling bounds — so the
    /// painted frame reports both that sampling happened per cell and which coordinate space it used.</summary>
    private sealed class ColumnRampBrush : IBrush
    {
        public int Samples { get; private set; }
        public Color ColorAt(int column, int row, Rect bounds)
        {
            Samples++;
            return Color.FromRgb((byte) Math.Clamp(column - bounds.Column, 0, 255), 0, 0);
        }
    }

    private static Rect Wide => new(0, 0, 64, 8);

    /// <summary>A 1-row FIGlet face, THREE columns per glyph with ink in every cell — so "sampled per cell"
    /// and "sampled per character" give visibly different frames.</summary>
    private static FigletFont WideFace() =>
        new("brushed-style-tests", '$', 1, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                ['A'] = new('A', ["###"], '$'),
                ['B'] = new('B', ["==="], '$'),
            });

    // ───────────────────────────── the fold: base + delta ─────────────────────────────

    [Fact]
    public void Monospace_RestatedBaseAlonePaintsTheBaseVerbatim()
    {
        var buffer = new CellBuffer(4, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "ab", BrushedStyle.From(Rich), Wide);

        Assert.Equal(Rich, buffer[0, 0].Style);
        Assert.Equal(Rich, buffer[1, 0].Style);
    }

    /// <summary>
    /// The operation the old signature could not express: state ONE channel and leave the rest. A
    /// <c>CellStyle</c>-returning provider had to rebuild the base to avoid wiping it — and any channel it
    /// forgot was silently reset. The base rides UNDER the delta now, restated via
    /// <see cref="BrushedStyle.From"/> + <see cref="BrushedStyle.Then"/>.
    /// </summary>
    [Fact]
    public void Monospace_ForegroundOnlyBrushedStyleKeepsEveryOtherChannel()
    {
        var buffer = new CellBuffer(4, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "ab",
                                    BrushedStyle.From(Rich)
                                                .Then(new BrushedStyle { Foreground = new UniformBrush(Green) }),
                                    Wide);

        Assert.Equal(Rich with { Foreground = Green }, buffer[0, 0].Style);
    }

    /// <summary>A channel the delta declines to state falls through to the DESTINATION CELL — the
    /// contract a base parameter used to intercept.</summary>
    [Fact]
    public void Monospace_UnstatedChannelsFallThroughToTheCellsUnderneath()
    {
        var buffer = new CellBuffer(4, 1);
        buffer.Fill(Cell.Blank with { Style = Rich });

        MonospaceFont.Default.Paint(buffer, 0, 0, "ab",
                                    new BrushedStyle { Foreground = new UniformBrush(Green) }, Wide);

        Assert.Equal(Rich with { Foreground = Green }, buffer[0, 0].Style);
    }

    [Fact]
    public void Monospace_SamplesANonUniformBrushedStylePerCell()
    {
        var buffer = new CellBuffer(4, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "abc",
                                    BrushedStyle.From(Rich).Then(new BrushedStyle { Foreground = new ColumnRampBrush() }),
                                    Wide);

        Assert.Equal(0, buffer[0, 0].Style.Foreground.Red);
        Assert.Equal(1, buffer[1, 0].Style.Foreground.Red);
        Assert.Equal(2, buffer[2, 0].Style.Foreground.Red);
    }

    /// <summary>The attribute algebra survives the seam — a delta can turn a flag OFF, which a whole-style
    /// return could only do by restating every other channel.</summary>
    [Fact]
    public void Monospace_BrushedStyleCanClearAnAttributeTheBaseCarries()
    {
        var buffer = new CellBuffer(4, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "ab",
                                    BrushedStyle.From(Rich.AddAttributes(TextAttributes.Inverse))
                                                .Then(default(BrushedStyle).Removing(TextAttributes.Inverse)),
                                    Wide);

        Assert.False(buffer[0, 0].Style.Attributes.HasFlag(TextAttributes.Inverse));
        Assert.True(buffer[0, 0].Style.Attributes.HasFlag(TextAttributes.Bold));   // untouched
    }

    // ───────────────────────────── the uniform fast path ─────────────────────────────

    /// <summary>
    /// The win a callback could never give: <see cref="BrushedStyle.IsUniform"/> is a readable property
    /// of the VALUE, so a solid brush is resolved once for the whole run instead of once per cell. A delegate
    /// is opaque — the font had no way to ask, so it had to call for every cell.
    /// </summary>
    [Fact]
    public void Monospace_ResolvesAUniformBrushedStyleOnce()
    {
        var brush = new UniformBrush(Green);
        var buffer = new CellBuffer(8, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "abcdef",
                                    BrushedStyle.From(Rich).Then(new BrushedStyle { Foreground = brush }), Wide);

        Assert.Equal(1, brush.Samples);
        Assert.Equal(Green, buffer[5, 0].Style.Foreground);   // ...and every cell still got the colour
    }

    [Fact]
    public void Monospace_ResolvesANonUniformBrushedStylePerCell()
    {
        var brush = new ColumnRampBrush();
        var buffer = new CellBuffer(8, 1);
        MonospaceFont.Default.Paint(buffer, 0, 0, "abcdef",
                                    BrushedStyle.From(Rich).Then(new BrushedStyle { Foreground = brush }), Wide);

        Assert.Equal(6, brush.Samples);
    }

    [Fact]
    public void Figlet_ResolvesAUniformBrushedStyleOnceForTheWholeRun()
    {
        var brush = new UniformBrush(Green);
        var buffer = new CellBuffer(12, 1);
        WideFace().Paint(buffer, 0, 0, "AB",
                         BrushedStyle.From(Rich).Then(new BrushedStyle { Foreground = brush }), Wide);

        Assert.Equal(1, brush.Samples);
        Assert.All(Enumerable.Range(0, 6), c => Assert.Equal(Green, buffer[c, 0].Style.Foreground));
    }

    // ───────────────────────────── FIGlet: the per-cell face ─────────────────────────────

    /// <summary>
    /// The reason a glyph face gets the style rather than a resolved style: one CHARACTER covers several
    /// cells, so only the face knows which cells exist to sample. A gradient must flow ACROSS a glyph, not
    /// band at its boundary.
    /// </summary>
    [Fact]
    public void Figlet_SamplesEveryPaintedCellNotEveryCharacter()
    {
        var buffer = new CellBuffer(12, 1);
        WideFace().Paint(buffer, 0, 0, "AB",
                         BrushedStyle.From(Rich).Then(new BrushedStyle { Foreground = new ColumnRampBrush() }),
                         Wide);

        for (int c = 0; c < 6; c++)
            Assert.Equal(c, buffer[c, 0].Style.Foreground.Red);

        // The restated base's own channels survive — the delta said "foreground" and nothing else.
        Assert.Equal(Blue, buffer[0, 0].Style.Background);
        Assert.Equal(UnderlineStyle.Curly, buffer[0, 0].Style.UnderlineStyle);
    }

    /// <summary>
    /// <see cref="IGlyphFont.EnsureCompatibleStyle"/> applies to the FOLDED style, not to the delta alone: the
    /// cell underneath is a second way for an attribute this face cannot render to arrive.
    /// </summary>
    [Fact]
    public void Figlet_StripsForbiddenAttributesFromTheFoldedStyle()
    {
        var buffer = new CellBuffer(6, 1);
        WideFace().Paint(buffer, 0, 0, "A",
                         default(BrushedStyle).Applying(TextAttributes.Strikethrough), Wide);

        Assert.False(buffer[0, 0].Style.Attributes.HasFlag(TextAttributes.Strikethrough));
    }

    // ───────────────────────────── the interface default ─────────────────────────────

    /// <summary>
    /// A face that does not override the brushed overload resolves ONCE at the anchor and forwards the
    /// resolved delta to the flat overload — the documented default. It needs the bounds for the resolve,
    /// which is why they joined the signature rather than being smuggled inside a closure.
    /// </summary>
    [Fact]
    public void DefaultOverload_ResolvesOnceAtTheAnchorAndForwardsTheDelta()
    {
        var face = new RecordingFace();
        var buffer = new CellBuffer(4, 1);

        ((IGlyphFont) face).Paint(buffer, 2, 0, "ab",
                                  BrushedStyle.From(Rich)
                                      .Then(new BrushedStyle { Foreground = new UniformBrush(Green) }
                                                .Applying(TextAttributes.Inverse)),
                                  Wide);

        Assert.Equal([(2, 0)], face.Anchors);

        // The delta reaches the flat overload unchanged — nothing is folded away and nothing is guessed —
        // so folding it onto the ground style must reproduce the restated base plus the overlay exactly.
        // A channel the default dropped shows up here as one gone default.
        Assert.Equal(Rich with
                     {
                         Foreground = Green,
                         Attributes = Rich.Attributes | TextAttributes.Inverse,
                     },
                     face.Painted.ApplyTo(CellStyle.Default));
    }

    /// <summary>
    /// The default's STAMP side: a channel the delta declines to state is still absent when it reaches the
    /// flat overload — not silently completed from <see cref="CellStyle.Default"/>. Absence is invisible to
    /// a fully-stated probe (folding a completed delta over Default reproduces it), so this test folds the
    /// forwarded delta over a NON-default ground and reads what survived.
    /// </summary>
    [Fact]
    public void DefaultOverload_KeepsAnUnstatedChannelUnstated()
    {
        var face = new RecordingFace();
        var buffer = new CellBuffer(4, 1);

        ((IGlyphFont) face).Paint(buffer, 0, 0, "ab",
                                  new BrushedStyle { Foreground = new UniformBrush(Green) }, Wide);

        var survived = face.Painted.ApplyTo(Rich);

        Assert.Equal(Green, survived.Foreground);
        Assert.Equal(Rich.Background, survived.Background);           // absent: the ground's, not Default
        Assert.Equal(Rich.Attributes, survived.Attributes);
        Assert.Equal(Rich.UnderlineColor, survived.UnderlineColor);
        Assert.Equal(Rich.Hyperlink, survived.Hyperlink);
    }

    private sealed class RecordingFace : IGlyphFont
    {
        public List<(int Column, int Row)> Anchors { get; } = [];
        public PartialStyle Painted { get; private set; }

        public string DisplayName => "recording";
        public CellStyle EnsureCompatibleStyle(in CellStyle style) => style;
        public Size Measure(ReadOnlySpan<char> text) => new(text.Length, 1);

        public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
        {
            Anchors.Add((column, row));
            Painted = style;
            return new Size(text.Length, 1);
        }
    }

    // ───────────────────────────── decorators ─────────────────────────────

    /// <summary>
    /// The brushed overload's style reaches the shadow pass, not just the glyph pass — under the callback
    /// form the shadow lost every channel the callback did not restate. The underline SHAPE is the visible
    /// one: <c>EnsureCompatibleShadowStyle</c> keeps the flag but the shape rides the style.
    /// </summary>
    [Fact]
    public void Shadowed_ShadowPassSeesTheRestatedBase()
    {
        var face = new ShadowedFont(MonospaceFont.Default, (1, 1),
                                    CellStyle.Default.WithForeground(Color.FromRgb(60, 60, 60))
                                             .WithBackground(Color.Transparent));

        var buffer = new CellBuffer(6, 3);
        var baseStyle = CellStyle.Default.WithAttributes(TextAttributes.Underline)
                                 .WithUnderlineStyle(UnderlineStyle.Dashed);

        face.Paint(buffer, 0, 0, "ab",
                   BrushedStyle.FromInk(baseStyle).Then(new BrushedStyle { Foreground = new UniformBrush(Green) }),
                   Wide);

        // (2,1) is shadow-only: the glyphs occupy (0,0)-(1,0), the shadow (1,1)-(2,1).
        Assert.Equal("b", buffer[2, 1].Grapheme);
        Assert.Equal(UnderlineStyle.Dashed, buffer[2, 1].Style.UnderlineStyle);

        // ...and the glyph pass still gets the delta over that same restated base.
        Assert.Equal(Green, buffer[0, 0].Style.Foreground);
        Assert.Equal(UnderlineStyle.Dashed, buffer[0, 0].Style.UnderlineStyle);
    }

    // ───────────────────────────── the resolver end of the chain ─────────────────────────────

    /// <summary>
    /// <c>FormattedText</c> asks the resolver ONCE per run and resolves the style per cell — the two seams
    /// are one chain, and the reshaping is what let the font end receive a brush instead of a callback.
    /// </summary>
    [Fact]
    public void FormattedText_CallsTheResolverOncePerRunAndFoldsOntoItsOwnStyle()
    {
        var ft = new TextFormatter().Format(new RichTextBuilder().Run("abcdef", Rich).Build(), 10);
        var bounds = new Rect(0, 0, 10, 2);

        // Differenced against the no-resolver frame: the formatter owns some of the run's style, so the
        // honest "everything else" is what it paints on its own, not the authored CellStyle.
        var plain = new CellBuffer(10, 2);
        ft.Paint(plain, bounds, OutputCapabilities.None);

        int calls = 0;
        var buffer = new CellBuffer(10, 2);
        ft.Paint(buffer, bounds, OutputCapabilities.None,
                 (in BrushedTextContext ctx) =>
                 {
                     calls++;
                     return new BrushedTextStyle(new BrushedStyle { Foreground = new UniformBrush(Green) }, ctx.Block);
                 });

        Assert.Equal(1, calls);
        Assert.NotEqual(Green, plain[0, 0].Style.Foreground);   // the delta is not a no-op
        Assert.Equal(plain[0, 0].Style with { Foreground = Green }, buffer[0, 0].Style);
    }

    /// <summary>An identity resolver is indistinguishable from no resolver at all — which is what makes
    /// "this leg has no opinion" expressible without reconstructing the base.</summary>
    [Fact]
    public void FormattedText_IdentityResolverPaintsExactlyWhatNoResolverDoes()
    {
        var ft = new TextFormatter().Format(new RichTextBuilder().Run("ab", Rich).Build(), 10);
        var bounds = new Rect(0, 0, 10, 2);

        var plain = new CellBuffer(10, 2);
        ft.Paint(plain, bounds, OutputCapabilities.None);

        var identity = new CellBuffer(10, 2);
        ft.Paint(identity, bounds, OutputCapabilities.None, (in BrushedTextContext _) => BrushedTextStyle.None);

        for (int c = 0; c < 10; c++)
            Assert.Equal(plain[c, 0].Style, identity[c, 0].Style);
    }

    /// <summary>
    /// The resolver still READS the base style — that is how a brush decides whether a run's foreground was
    /// inherited — it merely stops returning it. The context member is load-bearing.
    /// </summary>
    [Fact]
    public void FormattedText_ResolverStillSeesTheRunsBaseStyle()
    {
        var ft = new TextFormatter().Format(new RichTextBuilder().Run("ab", Rich).Build(), 10);

        var seen = new List<CellStyle>();
        var buffer = new CellBuffer(10, 2);
        ft.Paint(buffer, new Rect(0, 0, 10, 2), OutputCapabilities.None,
                 (in BrushedTextContext ctx) =>
                 {
                     seen.Add(ctx.BaseStyle);
                     return BrushedTextStyle.None;
                 });

        // The resolver was the identity, so the painted cell IS the base it was handed.
        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal(buffer[0, 0].Style, s));
        Assert.Equal(Blue, seen[0].Background);   // ...and it is the run's real style, not a blank
    }

    /// <summary>
    /// The inline scope is handed over as a REBASED RECT — a 1-row box whose origin is the column at which the
    /// source run's logical offset 0 sits — so a brush sampled at the cell's own coordinates against it sees a
    /// wrap-invariant reading-order position. Pinned by wrapping a run and checking the ramp continues across
    /// the break instead of restarting.
    /// </summary>
    [Fact]
    public void FormattedText_InlineScopeIsWrapInvariant()
    {
        // "aaaa bbbb" wraps at width 4 into two line-pieces of one source run.
        var ft = new TextFormatter().Format(new RichTextBuilder().Run("aaaa bbbb").Build(), 4);

        var buffer = new CellBuffer(4, 4);
        ft.Paint(buffer, new Rect(0, 0, 4, 4), OutputCapabilities.None,
                 (in BrushedTextContext ctx) =>
                     new BrushedTextStyle(new BrushedStyle { Foreground = new ColumnRampBrush() }, ctx.InlineScope));

        // Row 0 is logical offsets 0..3; row 1 continues at 5.. (the space at offset 4 is consumed by the wrap).
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4).Select(c => (int) buffer[c, 0].Style.Foreground.Red));
        Assert.Equal(5, buffer[0, 1].Style.Foreground.Red);
    }
}
