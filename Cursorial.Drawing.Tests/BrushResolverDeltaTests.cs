using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// <see cref="DrawingContext.CreateBrushResolver"/> runs once per RUN and returns a
/// <see cref="BrushedStyle"/> paired with the rect its brushes sample against — a DELTA over the style
/// the formatter already resolved, with the brush still unsampled. Each leg owns exactly the channels it has
/// an opinion about, so "this run keeps what it had" is the identity rather than a copy of the base, and the
/// element-attribute leg is visibly a SET rather than a replace.
/// </summary>
public class BrushResolverDeltaTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Green = Color.FromRgb(0, 200, 0);

    private static readonly Rect Doc = new(0, 0, 10, 4);
    private static readonly Rect Block = new(1, 1, 8, 2);
    private static readonly Rect Inline = new(3, 1, 20, 1);

    /// <summary>A base that is non-default on EVERY channel the resolver could accidentally clobber, so
    /// "the delta left it alone" is distinguishable from "the delta rebuilt it identically".</summary>
    private static CellStyle Rich(Color foreground) =>
        CellStyle.Default
                 .WithForeground(foreground)
                 .WithBackground(Blue)
                 .WithAttributes(TextAttributes.Italic | TextAttributes.Strikethrough)
                 .WithUnderlineStyle(UnderlineStyle.Curly)
                 .WithUnderlineColor(Green)
                 .WithHyperlink("https://example.invalid");

    // The carrier defaults to the identity: these tests exercise the tag / document / attribute legs, and
    // an identity carrier states no brush for the carrier-first leg to read.
    private static BrushedTextContext Context(in CellStyle baseStyle, object? tag = null, in BrushedStyle style = default) =>
        new(baseStyle, style, Block, Inline, tag);

    // ───────────────────────────── the three colour legs ─────────────────────────────

    /// <summary>
    /// The leg that used to be the clearest evidence the return type was wrong: with no document brush the
    /// resolver reconstructed <c>ctx.BaseStyle</c> in full in order to mean "no change". It is now the
    /// identity, which composes and can be short-circuited.
    /// </summary>
    [Fact]
    public void NoDocumentBrush_IsTheIdentityDelta()
    {
        var resolver = DrawingContext.CreateBrushResolver(default, documentForeground: Red, Doc);

        Assert.True(resolver(Context(Rich(Red))).Style.IsIdentity);
        Assert.True(resolver(Context(CellStyle.Default)).Style.IsIdentity);
    }

    /// <summary>
    /// A run whose foreground is its OWN (neither unset nor the document default) out-votes the document
    /// brush — and, as a delta, says nothing at all rather than restating the run's style.
    /// </summary>
    [Fact]
    public void DocumentBrush_IsTheIdentityDeltaForANonInheritedForeground()
    {
        var resolver = DrawingContext.CreateBrushResolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                                          documentForeground: Red, Doc);

        Assert.True(resolver(Context(Rich(Blue))).Style.IsIdentity);
    }

    [Theory]
    [InlineData(false)]  // foreground unset — Color.Default, the "inherited" sentinel
    [InlineData(true)]   // foreground explicitly equal to the document default
    public void DocumentBrush_ColorsAnInheritedForegroundAndNothingElse(bool explicitDocumentColor)
    {
        var resolver = DrawingContext.CreateBrushResolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                                          documentForeground: Red, Doc);

        var baseStyle = explicitDocumentColor ? Rich(Red) : Rich(Color.Default);
        var brushed = resolver(Context(baseStyle));
        var delta = brushed.Resolve(column: 2, row: 1);

        Assert.Equal(Green, delta.Foreground);

        // ...and it owns NOTHING else: every other channel is absent, so the run keeps its own.
        Assert.Null(delta.Background);
        Assert.Null(delta.UnderlineColor);
        Assert.Null(delta.UnderlineShape);
        Assert.Null(delta.Hyperlink);
        Assert.Equal(default, delta.AppliedAttributes);
        Assert.Equal(default, delta.RemovedAttributes);
        Assert.Equal(default, delta.ToggledAttributes);

        Assert.Equal(baseStyle with { Foreground = Green }, delta.ApplyTo(baseStyle));

        // The document brush samples against the enclosing BLOCK.
        Assert.Equal(Block, brushed.Bounds);
    }

    /// <summary>A run declaring its own <c>ScopedBrush</c> wins over the document brush, and is likewise
    /// foreground-only.</summary>
    [Fact]
    public void RunBrush_WinsOverTheDocumentBrushAndIsForegroundOnly()
    {
        var resolver = DrawingContext.CreateBrushResolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                                          documentForeground: Red, Doc);

        var delta = resolver(Context(Rich(Color.Default), new ScopedBrush(new SolidColorBrush(Blue))))
                        .Resolve(column: 2, row: 1);

        Assert.Equal(Blue, delta.Foreground);
        Assert.Null(delta.Background);
        Assert.Equal(default, delta.AppliedAttributes);
    }

    /// <summary>
    /// The scope distinction survived the move from "sample here" to "sample against this rect": inline gets
    /// the run's wrap-invariant reading-order strip, block the laid-out box, document the whole document.
    /// Flattening these to one rect would silently restart every inline gradient at each line break.
    /// </summary>
    [Theory]
    [InlineData(DeclarationScope.Inline)]
    [InlineData(DeclarationScope.Block)]
    [InlineData(DeclarationScope.Document)]
    public void RunBrush_SamplesAgainstItsDeclarationScope(DeclarationScope scope)
    {
        var resolver = DrawingContext.CreateBrushResolver(default, documentForeground: Red, Doc);

        var brushed = resolver(Context(Rich(Color.Default), new ScopedBrush(new SolidColorBrush(Blue), scope)));

        Assert.Equal(scope switch
                     {
                         DeclarationScope.Inline   => Inline,
                         DeclarationScope.Document => Doc,
                         _                         => Block,
                     },
                     brushed.Bounds);
    }

    // ───────────────────────────── the base-attribute leg ─────────────────────────────

    /// <summary>
    /// The element's inherited attributes are FORCED ON and the run's own survive alongside — on every axis
    /// except weight, where "alongside" is not a state the terminal has (see below). A replace would be the
    /// obvious mistranslation.
    /// </summary>
    [Fact]
    public void BaseAttributes_AreSetNotReplaced()
    {
        var resolver = DrawingContext.CreateBrushResolver(
            BrushedStyle.Identity.Imposing(TextAttributes.Bold | TextAttributes.Inverse),
            documentForeground: Red, Doc);

        var brushed = resolver(Context(Rich(Red)));

        Assert.Equal(TextAttributes.Bold | TextAttributes.Inverse, brushed.Style.AppliedAttributes);
        Assert.Equal(default, brushed.Style.ToggledAttributes);

        // Bold arrives as a WEIGHT, and a weight is exclusive: Faint is forced off in the same breath.
        // That is the only thing the delta unsets, and it is visible in the intent triple rather than
        // hidden in the mask.
        Assert.Equal(TextAttributes.Faint, brushed.Style.RemovedAttributes);

        // The run's own Italic and Strikethrough are untouched; the element's two arrive on top.
        var applied = brushed.ApplyTo(2, 1, Rich(Red));
        Assert.Equal(TextAttributes.Italic | TextAttributes.Strikethrough |
                     TextAttributes.Bold | TextAttributes.Inverse,
                     applied.Attributes);
    }

    /// <summary>
    /// Bold and Faint share the SGR 22 reset, so they share an AXIS: a cell carrying both is not a cell with
    /// two attributes, it is a cell the encoder cannot spell. Reaching it emits <c>ESC[1m</c> from a Faint
    /// predecessor and <c>ESC[2m</c> from a Bold one — same destination, different bytes, and the terminal
    /// keeps whichever arrived last. So the base-attribute leg folds weight through the axis: an inherited
    /// Bold IMPOSES Bold, clearing the run's own Faint, rather than unioning into a state nothing can render.
    /// </summary>
    [Fact]
    public void BaseAttributes_Bold_ImposesTheWeight_ClearingTheRunsFaint()
    {
        var resolver = DrawingContext.CreateBrushResolver(BrushedStyle.Identity.Imposing(TextAttributes.Bold),
                                                          documentForeground: Red, Doc);

        var run = CellStyle.Default.WithAttributes(TextAttributes.Faint);
        var applied = resolver(Context(run)).ApplyTo(2, 1, run);

        Assert.Equal(TextAttributes.Bold, applied.Attributes);
    }

    /// <summary>The converse, so the rule is a weight axis and not a Bold special case.</summary>
    [Fact]
    public void BaseAttributes_Faint_ImposesTheWeight_ClearingTheRunsBold()
    {
        var resolver = DrawingContext.CreateBrushResolver(BrushedStyle.Identity.Imposing(TextAttributes.Faint),
                                                          documentForeground: Red, Doc);

        var run = CellStyle.Default.WithAttributes(TextAttributes.Bold);
        var applied = resolver(Context(run)).ApplyTo(2, 1, run);

        Assert.Equal(TextAttributes.Faint, applied.Attributes);
    }

    /// <summary>
    /// A flag WORD can carry both — <c>ComposeAttributes</c> cannot produce that, but a caller that ORs its
    /// own flags onto the composed word can. The leg resolves it deterministically rather than passing the
    /// contradiction through: Bold wins. The choice is arbitrary; being FIXED is not, because the alternative
    /// is a cell whose rendering depends on what was painted before it.
    /// </summary>
    [Fact]
    public void BaseAttributes_CarryingBothWeights_ResolveToBold()
    {
        var resolver = DrawingContext.CreateBrushResolver(
            BrushedStyle.Identity.Imposing(TextAttributes.Bold | TextAttributes.Faint),
            documentForeground: Red, Doc);

        foreach (var run in new[] { CellStyle.Default,
                                    CellStyle.Default.WithAttributes(TextAttributes.Bold),
                                    CellStyle.Default.WithAttributes(TextAttributes.Faint) })
            Assert.Equal(TextAttributes.Bold, resolver(Context(run)).ApplyTo(2, 1, run).Attributes);
    }

    /// <summary>
    /// The axis treatment is confined to the axes. The genuine booleans have no partner sharing a reset, so
    /// they still UNION — an inherited Strikethrough must not evict a run's Overline the way an inherited
    /// Bold evicts its Faint. This is the guard against over-applying the fix.
    /// </summary>
    [Fact]
    public void BaseAttributes_BooleanFlags_StillUnionWithTheRunsOwn()
    {
        var resolver = DrawingContext.CreateBrushResolver(BrushedStyle.Identity.Imposing(TextAttributes.Strikethrough),
                                                          documentForeground: Red, Doc);

        var run = CellStyle.Default.WithAttributes(TextAttributes.Overline);
        var applied = resolver(Context(run)).ApplyTo(2, 1, run);

        Assert.Equal(TextAttributes.Strikethrough | TextAttributes.Overline, applied.Attributes);
    }

    /// <summary>
    /// Italic owns an axis too, but a one-sided one — there is no attribute it excludes — so the union and
    /// the axis coincide and folding it through <c>Posturing</c> changes nothing observable.
    /// </summary>
    [Fact]
    public void BaseAttributes_Italic_FoldsExactlyAsAUnionWould()
    {
        var resolver = DrawingContext.CreateBrushResolver(BrushedStyle.Identity.Imposing(TextAttributes.Italic),
                                                          documentForeground: Red, Doc);

        foreach (var run in new[] { CellStyle.Default,
                                    CellStyle.Default.WithAttributes(TextAttributes.Italic),
                                    CellStyle.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Overline) })
            Assert.Equal(run.Attributes | TextAttributes.Italic,
                         resolver(Context(run)).ApplyTo(2, 1, run).Attributes);
    }

    /// <summary>
    /// End to end through the public painting API, on the cells actually in the frame — the seam-level tests
    /// above prove the delta, this proves the paint. A run styled Faint under an element-inherited Bold
    /// leaves exactly one weight flag in the buffer.
    /// </summary>
    [Fact]
    public void PaintedCells_CarryOneWeightFlag_NotBoth()
    {
        var doc = new RichTextBuilder().Run("hi", CellStyle.Default.WithAttributes(TextAttributes.Faint)).Build();
        var ft = new TextFormatter().Format(doc, 10, maxRows: null, OutputCapabilities.None);

        var b = DrawHarness.Render(10, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 10, 2),
                                                                       OutputCapabilities.None,
                                                                       BrushedStyle.Identity.Imposing(TextAttributes.Bold)));

        Assert.Equal("h", b[0, 0].Grapheme);
        Assert.Equal(TextAttributes.Bold, b[0, 0].Style.Attributes);
        Assert.Equal(TextAttributes.Bold, b[1, 0].Style.Attributes);
    }

    /// <summary>
    /// End to end through the public painting API, like the weight case above: an element-inherited
    /// Underline with a non-default shape lands on the painted cells — flag and shape both, through the
    /// paint preference and the per-run merge behind it.
    /// </summary>
    [Fact]
    public void PaintedCells_CarryTheInheritedUnderlineShape()
    {
        var doc = new RichTextBuilder().Run("hi").Build();
        var ft = new TextFormatter().Format(doc, 10, maxRows: null, OutputCapabilities.None);

        var b = DrawHarness.Render(10, 2, ctx => ctx.DrawFormattedText(
                                       ft, new Rect(0, 0, 10, 2), OutputCapabilities.None,
                                       BrushedStyle.Identity.Imposing(TextAttributes.Underline, UnderlineStyle.Dotted)));

        Assert.Equal("h", b[0, 0].Grapheme);
        Assert.True(b[0, 0].Style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(UnderlineStyle.Dotted, b[0, 0].Style.UnderlineStyle);
        Assert.Equal(UnderlineStyle.Dotted, b[1, 0].Style.UnderlineStyle);
    }

    [Fact]
    public void NoBaseAttributes_LeavesTheAttributeChannelsUntouched()
    {
        var resolver = DrawingContext.CreateBrushResolver(default, documentForeground: Red, Doc);

        Assert.Equal(Rich(Red), resolver(Context(Rich(Red))).ApplyTo(2, 1, Rich(Red)));
    }

    // ───────────────────────────── the underline-shape rider ─────────────────────────────

    /// <summary>
    /// The shape rides along only when the element carries the Underline PRESENCE bit and asks for a shape
    /// other than the <see cref="UnderlineStyle.Single"/> default. Both conditions, and both negatives,
    /// because the guard is an AND of two unrelated facts.
    /// </summary>
    [Theory]
    [InlineData(TextAttributes.Underline, UnderlineStyle.Dashed, UnderlineStyle.Dashed)]
    [InlineData(TextAttributes.Underline, UnderlineStyle.Single, UnderlineStyle.Curly)] // Single = "no shape stated"
    [InlineData(TextAttributes.Bold, UnderlineStyle.Dashed, UnderlineStyle.Curly)]      // no Underline bit
    public void UnderlineShapeRider(TextAttributes baseAttributes, UnderlineStyle baseShape, UnderlineStyle expected)
    {
        var resolver = DrawingContext.CreateBrushResolver(BrushedStyle.Identity.Imposing(baseAttributes, baseShape),
                                                          documentForeground: Red, Doc);

        // Rich() carries UnderlineStyle.Curly, so "the rider did not fire" is visible as the run's own shape.
        Assert.Equal(expected, resolver(Context(Rich(Red))).ApplyTo(2, 1, Rich(Red)).UnderlineStyle);
    }

    /// <summary>
    /// A shape implies the flag once resolved (§3), and the rider only fires when the element already carries
    /// Underline — so the two agree and the flag lands either way.
    /// </summary>
    [Fact]
    public void UnderlineShapeRider_KeepsTheUnderlineFlagOn()
    {
        var resolver = DrawingContext.CreateBrushResolver(
            BrushedStyle.Identity.Imposing(TextAttributes.Underline, UnderlineStyle.Dotted),
            documentForeground: Red, Doc);

        var applied = resolver(Context(CellStyle.Default)).ApplyTo(2, 1, CellStyle.Default);

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(UnderlineStyle.Dotted, applied.UnderlineStyle);
    }

    /// <summary>
    /// The Underline FLAG lands even when the element states no shape — the rider's guard is about the
    /// SHAPE, not about whether the underline arrives at all. Worth its own test because Underline owns an
    /// axis, so it cannot travel with the booleans, and the shape rider deliberately declines to fire here.
    /// </summary>
    [Theory]
    [InlineData(UnderlineStyle.Single)]   // no shape stated — the run keeps its own
    [InlineData(UnderlineStyle.Dotted)]   // a shape stated — the rider overrides
    public void BaseAttributes_Underline_TurnsTheFlagOnWhateverTheShape(UnderlineStyle baseShape)
    {
        var resolver = DrawingContext.CreateBrushResolver(
            BrushedStyle.Identity.Imposing(TextAttributes.Underline, baseShape),
            documentForeground: Red, Doc);

        // Rich() carries Curly WITHOUT the Underline flag, so the shape is inert until the flag arrives.
        var applied = resolver(Context(Rich(Red))).ApplyTo(2, 1, Rich(Red));

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(baseShape is UnderlineStyle.Single ? UnderlineStyle.Curly : baseShape, applied.UnderlineStyle);
    }

    // ───────────────────────────── the chain, end to end ─────────────────────────────

    /// <summary>
    /// The resolver and the glyph face are ONE chain: <c>FormattedText</c> hands the face the template the
    /// resolver returned, unsampled, so a face that paints many cells per character samples per CELL. Pinned
    /// through the public painting API rather than the seam, because the adapter between them is where a
    /// half-migration would lose either the base or the per-cell sampling.
    /// </summary>
    [Fact]
    public void FigletFace_SamplesTheBrushPerCellAndKeepsTheRunsOtherChannels()
    {
        var style = CellStyle.Default.WithForeground(Color.Default).WithBackground(Blue);
        var doc = new RichTextBuilder().Figlet("HI", FigletFonts.Standard, style).Build();
        var ft = new TextFormatter().Format(doc, 40, maxRows: null, OutputCapabilities.None);

        var gradient = new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 40, 10), gradient,
                                                                        OutputCapabilities.None));

        (int Column, int Row)? first = null, last = null;
        for (int r = 0; r < b.Rows; r++)
        for (int c = 0; c < b.Columns; c++)
        {
            var cell = b[c, r];
            if (string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme == " ") continue;
            if (first is null || c < first.Value.Column) first = (c, r);
            if (last is null || c > last.Value.Column) last = (c, r);
        }

        Assert.NotNull(first);
        Assert.NotNull(last);

        // The brush states a FOREGROUND only, so the run's own background survives the fold.
        Assert.True(b[first.Value.Column, first.Value.Row].Style.Foreground.Red > 128, "leftmost ink is red-dominant");
        Assert.True(b[last.Value.Column, last.Value.Row].Style.Foreground.Blue > 128, "rightmost ink is blue-dominant");
        Assert.Equal(Blue, b[first.Value.Column, first.Value.Row].Style.Background);
    }

    /// <summary>
    /// A decorating face resolves the template once at its anchor and paints its SHADOW pass with the result.
    /// Before the migration the seam carried no base style at all — the overload passed <c>default</c> — so
    /// the shadow lost every channel the callback did not restate, visibly the run's underline SHAPE.
    /// </summary>
    [Fact]
    public void ShadowedFace_ShadowKeepsTheRunsUnderlineShape()
    {
        var face = new ShadowedFont(MonospaceFont.Default, (1, 1),
                                    CellStyle.Default.WithForeground(Color.FromRgb(60, 60, 60))
                                             .WithBackground(Color.Transparent));

        var style = CellStyle.Default.WithForeground(Color.FromRgb(255, 255, 255))
                             .WithAttributes(TextAttributes.Underline)
                             .WithUnderlineStyle(UnderlineStyle.Curly);

        var doc = new RichTextBuilder().Figlet("AB", face, style).Build();
        var ft = new TextFormatter().Format(doc, 10, maxRows: null, OutputCapabilities.None);

        var b = DrawHarness.Render(10, 4, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 10, 4), OutputCapabilities.None));

        // (2,1) is shadow-only ink: the glyphs sit at (0,0)-(1,0), the shadow one cell down and right.
        Assert.Equal("B", b[2, 1].Grapheme);
        Assert.Equal(UnderlineStyle.Curly, b[2, 1].Style.UnderlineStyle);
    }
}
