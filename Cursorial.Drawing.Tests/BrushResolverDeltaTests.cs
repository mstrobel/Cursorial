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
/// the formatter already resolved, with the brush still unsampled. The foreground legs are a LADDER,
/// weakest first (preference → document default → block → run): the strongest rung that STATES a
/// foreground wins, at the scope of its own declaration site, and no leg compares colour values. Each
/// leg owns exactly the channels it has an opinion about, so "this run keeps what it had" is the
/// identity rather than a copy of the base, and the element-attribute leg is visibly a SET rather than
/// a replace. The factory derives the document rung from the FORMATTED TEXT itself: its brush off the
/// document's carrier, and its sampling rect — the rect the document and preference legs return — as
/// the document's derived extent within the paint bounds, never the paint bounds themselves.
/// </summary>
public class BrushResolverDeltaTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Green = Color.FromRgb(0, 200, 0);

    // Four deliberately-DISTINCT rects, so "sampled the right scope" is distinguishable from "sampled
    // some scope": the paint bounds, the context-carried block and inline rects, and the fixture
    // document's derived extent — a centred 4-wide run in the 10-wide bounds, so the extent matches
    // none of the other three (and, pointedly, not Bounds: the doc/pref legs must return the extent).
    private static readonly Rect Bounds = new(0, 0, 10, 4);
    private static readonly Rect Block = new(1, 1, 8, 2);
    private static readonly Rect Inline = new(3, 1, 20, 1);
    private static readonly Rect Extent = new(3, 0, 4, 1);

    /// <summary>The fixture document the factory derives the document rung from: a centred "wxyz"
    /// formatted at the bounds' 10 columns, carrying <paramref name="documentForeground"/> as the
    /// document default's declared brush (null = the document declares none).</summary>
    private static FormattedText Formatted(IBrush? documentForeground) =>
        new TextFormatter().Format(
            new RichTextBuilder(new BrushedStyle { Foreground = documentForeground })
                .Paragraph(alignment: TextAlignment.Center, margin: Margins.Zero)
                .Run("wxyz")
                .Build(),
            Bounds.Columns);

    private static BrushedTextResolver Resolver(in BrushedStyle preference = default, IBrush? documentForeground = null)
        => DrawingContext.CreateBrushResolver(preference, Formatted(documentForeground), Bounds);

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

    // The context carries the run's own carrier and the block's declared foreground — the two ladder
    // rungs the painter reads off the document — plus the run's RESOLVED underline shape, which the
    // attribute leg re-states when the Underline presence bit merges. `run` stands in for the style
    // the formatter resolved; only its underline shape reaches the resolver.
    private static BrushedTextContext Context(in CellStyle run, in BrushedStyle style = default, IBrush? blockForeground = null) =>
        new(style, blockForeground, Block, Inline, run.UnderlineStyle);

    // ───────────────────────────── the foreground ladder ─────────────────────────────

    /// <summary>No rung states a foreground anywhere: the brush half of the delta is the identity,
    /// whatever the resolved base looks like — the base is not a declaration.</summary>
    [Fact]
    public void NoForegroundDeclaredAnywhere_IsTheIdentityDelta()
    {
        var resolver = Resolver();

        Assert.True(resolver(Context(Rich(Red))).Style.IsIdentity);
        Assert.True(resolver(Context(CellStyle.Default)).Style.IsIdentity);
    }

    /// <summary>
    /// The preference is the WEAKEST rung: it colors a run only when no level of the document declared
    /// a foreground — and then it owns nothing but the foreground, sampled against the document's
    /// derived extent. The resolved base's own (inherited) colour is not a declaration and never blocks it.
    /// </summary>
    [Theory]
    [InlineData(false)]  // resolved base carries no foreground of its own
    [InlineData(true)]   // resolved base carries an inherited, non-default foreground
    public void PreferenceBrush_ColorsAnUndeclaredForegroundAndNothingElse(bool nonDefaultBase)
    {
        var resolver = Resolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) });

        var baseStyle = nonDefaultBase ? Rich(Red) : Rich(Color.Default);
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

        // The preference samples against the DOCUMENT's derived extent — the rung it stands in for —
        // and the extent really is derived: the fixture pins it as a fourth distinct rect.
        Assert.Equal(Extent, Formatted(null).ComputeExtent(Bounds));
        Assert.Equal(Extent, brushed.Bounds);
    }

    /// <summary>A document default that states a foreground beats the preference, sampling the
    /// document's derived extent — the ladder's document rung.</summary>
    [Fact]
    public void DocumentForeground_BeatsThePreference_AtTheDocumentExtent()
    {
        var resolver = Resolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                documentForeground: new SolidColorBrush(Blue));

        var brushed = resolver(Context(Rich(Color.Default)));
        var delta = brushed.Resolve(column: 2, row: 1);

        Assert.Equal(Blue, delta.Foreground);
        Assert.Equal(Extent, brushed.Bounds);
    }

    /// <summary>A block-declared foreground beats the document default and the preference, sampling the
    /// block's 2-D rect — the ladder's block rung.</summary>
    [Fact]
    public void BlockForeground_BeatsTheDocumentAndPreference_AtTheBlockRect()
    {
        var resolver = Resolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                documentForeground: new SolidColorBrush(Red));

        var brushed = resolver(Context(Rich(Color.Default), blockForeground: new SolidColorBrush(Blue)));
        var delta = brushed.Resolve(column: 2, row: 1);

        Assert.Equal(Blue, delta.Foreground);
        Assert.Equal(Block, brushed.Bounds);
    }

    /// <summary>A run's own carrier out-votes every lower rung, samples the run's wrap-invariant
    /// reading-order strip, and is likewise foreground-only.</summary>
    [Fact]
    public void CarrierForeground_WinsOverEveryLowerRung_AtTheInlineStrip()
    {
        var resolver = Resolver(new BrushedStyle { Foreground = new SolidColorBrush(Green) },
                                documentForeground: new SolidColorBrush(Red));

        var brushed = resolver(Context(Rich(Color.Default),
                                       new BrushedStyle { Foreground = new SolidColorBrush(Blue) },
                                       blockForeground: new SolidColorBrush(Green)));
        var delta = brushed.Resolve(column: 2, row: 1);

        Assert.Equal(Blue, delta.Foreground);
        Assert.Null(delta.Background);
        Assert.Equal(default, delta.AppliedAttributes);
        Assert.Equal(Inline, brushed.Bounds);
    }

    /// <summary>
    /// Declaration wins by POLICY, not by value: a run-declared solid equal in colour to the document
    /// default's still wins at its own scope. Phase 6's restatement sentinel read this as a restatement
    /// and let the lower rungs colour the run; the ladder has no value comparison anywhere.
    /// </summary>
    [Fact]
    public void CarrierSolid_EqualToTheDocumentDefaultsColour_StillWinsAtItsOwnScope()
    {
        var resolver = Resolver(documentForeground: new SolidColorBrush(Red));

        var brushed = resolver(Context(Rich(Red), new BrushedStyle { Foreground = new SolidColorBrush(Red) }));

        Assert.Equal(Red, brushed.Resolve(column: 2, row: 1).Foreground);
        Assert.Equal(Inline, brushed.Bounds);
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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Bold | TextAttributes.Inverse));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Bold));

        var run = CellStyle.Default.WithAttributes(TextAttributes.Faint);
        var applied = resolver(Context(run)).ApplyTo(2, 1, run);

        Assert.Equal(TextAttributes.Bold, applied.Attributes);
    }

    /// <summary>The converse, so the rule is a weight axis and not a Bold special case.</summary>
    [Fact]
    public void BaseAttributes_Faint_ImposesTheWeight_ClearingTheRunsBold()
    {
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Faint));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Bold | TextAttributes.Faint));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Strikethrough));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Italic));

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
        var resolver = Resolver();

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(baseAttributes, baseShape));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Underline, UnderlineStyle.Dotted));

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
        var resolver = Resolver(BrushedStyle.Identity.Imposing(TextAttributes.Underline, baseShape));

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
    /// half-migration would lose either the base or the per-cell sampling. The paint rect is tightened to
    /// the document's own width so the ramp spans the ink it colors.
    /// </summary>
    [Fact]
    public void FigletFace_SamplesTheBrushPerCellAndKeepsTheRunsOtherChannels()
    {
        var style = CellStyle.Default.WithForeground(Color.Default).WithBackground(Blue);
        var doc = new RichTextBuilder().Figlet("HI", FigletFonts.Standard, style).Build();
        var ft = new TextFormatter().Format(doc, 40, maxRows: null, OutputCapabilities.None);

        var gradient = new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, ft.Size.Columns, 10), gradient,
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
