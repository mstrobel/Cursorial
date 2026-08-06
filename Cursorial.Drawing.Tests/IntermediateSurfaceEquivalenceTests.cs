using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The migration safety net for opacity groups. Routing <b>one</b> layer through an intermediate group
/// surface and blending that surface down once at opacity <c>g</c> must reproduce, cell for cell, what
/// compositing that layer directly at <c>g</c> produced. While that holds, the group path is a strict
/// generalisation of the flat path: any pixel difference a group later introduces is a genuine
/// group-semantics difference (the ancestor blending once instead of once per overlapping descendant),
/// never a compositing regression.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it holds.</b> For a single source the intermediate is a near-copy: its base is transparent, so
/// every <see cref="Color.CompositeOver"/> call short-circuits on the transparent backdrop and no blend
/// mode or alpha arithmetic runs at all. A glyph-bearing cell lands on the surface verbatim (foreground
/// included — that is the rule this file exists to defend); a background-only cell lands as
/// <c>fg == bg == source background</c>, and the flat path never reads a background-only cell's own
/// foreground either. The one composite that does real work is therefore the final one, and it sees the
/// same source style, the same backdrop and the same opacity on both paths.
/// </para>
/// <para>
/// <b>Preconditions the shipping code must keep.</b> The group surface must contain the member's whole
/// target footprint (guaranteed upstream by clip containment — every boundary's clip is intersected with
/// its ancestors'), and the group layer must carry the same blend mode as the member it replaces. The
/// generated matrix honours both; violating either is a group-geometry bug, not a compositing one.
/// </para>
/// <para>
/// <b>Cell reclassification, and the bit that closes it.</b> An intermediate surface can only hand a
/// contribution onwards as a <i>cell</i>, and <c>CompositeCell</c> classifies a source cell by whether it
/// carries a grapheme: glyph-bearing <b>replaces</b> the destination outright (grapheme, kind, foreground,
/// attributes, underline, hyperlink), glyphless <b>merges</b> into it (background composited, destination
/// grapheme and underline/hyperlink kept, foreground tinted). Several writes produce a <i>glyphless</i> cell
/// that must nevertheless replace — the WideLeft edge degrade, the emoji stomp, and the blank the buffer's
/// wide-pair hygiene leaves next door. On the flat path those writes are final and replacing; a surface has
/// to say so explicitly, which is what <c>SceneCompositor</c>'s replacing-blank marker does. Without it the
/// background survived but the foreground, attributes, underline and hyperlink came from the group's
/// backdrop, and a destination glyph the flat path erased survived. Pinned by
/// <see cref="WideGlyphCutAtTheFootprintEdge_KeepsReplaceSemanticsThroughAnIdentityGroup"/>,
/// <see cref="EmojiStompedByAGroupMember_KeepsReplaceSemanticsThroughAnIdentityGroup"/>,
/// <see cref="MemberOverwritingAnotherMembersWidePair_ErasesTheDestinationGlyphThroughAGroup"/> and
/// <see cref="MemberWidePairLandingOneColumnShortOfAnother_MatchesTheFlatPath"/>, and — because the marker
/// must never reach a buffer a renderer emits — by
/// <see cref="DurableEmptyCells_RideThroughAGroupUnchanged"/>,
/// <see cref="NestedGroups_TranslateTheReplacingBlankExactlyOnce"/> and
/// <see cref="ReplacingBlankMarker_NeverReachesAFinalTarget"/>. One field of one cell shape is left
/// over, for an unrelated reason: <see cref="WidePairSurvivor_ResolvesItsUnderlineColorAtTheSurface"/>.
/// </para>
/// <para>
/// <b>The marker is a noncharacter, compared by value.</b> U+FFFF is permanently reserved and cannot occur
/// in valid interchanged text, so a VALUE comparison cannot collide with real content — which is what let
/// the compositor stop relying on string reference identity, an unwritten runtime guarantee whose loss
/// would have been silent. The two properties that licenses are
/// <see cref="ReplacingBlankMarker_IsANoncharacterThatBehavesLikeABlankInTheBuffer"/> (the value cannot
/// arrive as content, and behaves as a single-width blank while it sits in a buffer) and
/// <see cref="DurableEmptyCells_RideThroughAGroupUnchanged"/> (NBSP — the old marker value, and what every
/// <c>FillOpaque</c> writes — is content and rides through untranslated). The leak the translation cannot
/// close, a base layer that is itself a surface, is reported by a DEBUG diagnostic:
/// <see cref="ReplacingBlankMarkerOnTheBaseLayer_IsReportedByTheDebugDiagnostic"/>. Readers outside the
/// compositor go through <see cref="SceneCompositor.ResolveSurfaceCell"/>
/// (<see cref="ResolveSurfaceCell_TranslatesTheMarkerAndLeavesRealContentAlone"/>).
/// </para>
/// <para>
/// <b>Multi-contributor groups are a weaker claim than the plan assumed.</b> "An identity group (opacity 1)
/// is bit-identical to the flat stack" — the property that licenses making group-ness sticky — holds
/// exactly only when the members' cell backgrounds are <b>opaque</b>
/// (<see cref="MultiContributorGroupAtFullOpacity_WithOpaqueBackgrounds_IsBitIdenticalToTheFlatStack"/>).
/// Otherwise there are three further divergences, each characterised by its own test:
/// over-associativity rounding, bounded at 2 LSB
/// (<see cref="MultiContributorGroup_OverlappingTranslucentBackgrounds_DivergeOnlyByIntegerRounding"/>);
/// a <b>non-RGB backdrop</b> — including the terminal default, so this is common — moving where alpha is
/// flattened (<see cref="MultiContributorGroupOverANonRgbBackdrop_MovesWhereAlphaIsFlattened"/>); and the
/// resolved-destination-foreground tint running against the group's own background rather than the backdrop
/// the group will land on (<see cref="GlyphOverATranslucentCellBackground_CoveredByAScrim_DivergesInTheForeground"/>).
/// The last two are visible color changes, not rounding. None of them touch the single-layer property this
/// file is named for — which is the property that makes the group path a strict generalisation.
/// </para>
/// </remarks>
public class IntermediateSurfaceEquivalenceTests
{
    private const int Seed = 20260710;

    // The value of SceneCompositor's replacing-blank marker: U+FFFF, a permanently-reserved Unicode
    // NONCHARACTER. Only the value is observable from here (the field is private) and that is now the whole
    // contract — the compositor compares by value, which it can only do because nothing in valid text is
    // equal to this. Duplicating the literal here is deliberate: these tests must fail if the shipping value
    // changes, not silently follow it.
    private const string ReplacingBlank = "\uFFFF";

    private static readonly byte[] GroupOpacities = [0, 64, 128, 192, 255];

    private static readonly IBlendingMode?[] Modes =
        [null, BlendingModes.Multiply, BlendingModes.Screen, BlendingModes.Lighten];

    // null means "background only". The first five entries are narrow (a generator asked for
    // narrowGlyphsOnly takes that prefix); the wide and emoji-presentation clusters after them reach the
    // WideLeft pair path and the emoji stomp.
    private static readonly string?[] Graphemes = [null, null, "A", "g", "é", "漢", "漢", "🙂"];

    private static readonly Color Green = Color.FromRgb(0, 255, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color RedHalf = Color.FromRgba(255, 0, 0, 128);
    private static readonly Color BlackHalf = Color.FromRgba(0, 0, 0, 128);

    // ---- The headline property ----

    [Fact]
    public void SingleLayerThroughAnIntermediateSurface_IsBitIdenticalToCompositingItDirectly()
    {
        var random = new Random(Seed);
        var coverage = new Coverage();

        for (int index = 0; index < 150; index++)
        {
            var setup = RandomSetup(random, index, coverage);
            foreach (var opacity in GroupOpacities)
                AssertPathsAgree(setup, opacity);
        }

        // A generator that silently degenerates (all-opaque colors, no wide pairs, full coverage everywhere)
        // would keep this test green while testing a fraction of the matrix. Pin what it actually produced.
        coverage.AssertMatrixWasExercised();
    }

    [Fact]
    public void OpacityCarriedByTheMemberInsteadOfTheGroup_IsAlsoBitIdentical()
    {
        // The mirror image of the headline property: the member enters the surface at opacity m and the
        // surface goes down at full strength. This is the arrangement a group root's own scene uses (it
        // enters its group at 255) inverted, and it is the only path that runs ScaleSourceAlpha inside
        // intermediate mode. It must still agree with compositing the member directly at m.
        var random = new Random(Seed + 1);
        var coverage = new Coverage();

        for (int index = 0; index < 60; index++)
        {
            var setup = RandomSetup(random, index, coverage);
            foreach (var opacity in GroupOpacities)
                AssertPathsAgree(setup, opacity, opacityOnTheMember: true);
        }
    }

    // ---- Focused branch pins (so a regression localises instead of just failing the sweep) ----

    [Fact]
    public void WorkedExample_TranslucentGlyphForegroundInATranslucentGroup_MatchesTheFlatPath()
    {
        // The concrete case that rejects folding the foreground at the intermediate. A glyph with a
        // translucent foreground over its own opaque background, in a group at 0.5, over green.
        using var member = Scene.Create(1, 1);
        member.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(RedHalf).WithBackground(Blue)));

        var flat = Composite(Style.Default.WithBackground(Green), 1, 1,
                             [new SceneLayer(member, new CompositeParameters(opacity: 128))]);

        using var surface = Scene.Create(1, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member)]);
        var grouped = Composite(Style.Default.WithBackground(Green), 1, 1,
                                [new SceneLayer(surface, new CompositeParameters(opacity: 128))]);

        Assert.Equal(Color.FromRgb(64, 95, 95), flat[0, 0].Style.Foreground);
        Assert.Equal(flat[0, 0], grouped[0, 0]);

        // The surface holds the foreground verbatim, alpha intact — the property above is a consequence.
        Assert.Equal(RedHalf, surface.GetCell(0, 0).Style.Foreground);
        Assert.Equal(Blue, surface.GetCell(0, 0).Style.Background);

        // What folding at the intermediate would have produced instead: the group's backdrop applied twice.
        using var prefolded = Scene.Create(1, 1);
        var folded = Color.Composite(RedHalf, Blue, BlendingModes.Default);
        prefolded.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(folded).WithBackground(Blue)));
        var wrong = Composite(Style.Default.WithBackground(Green), 1, 1,
                              [new SceneLayer(prefolded, new CompositeParameters(opacity: 128))]);

        Assert.Equal(Color.FromRgb(64, 63, 127), wrong[0, 0].Style.Foreground);
    }

    [Fact]
    public void WorkedExample_ScrimOverATranslucentGlyphForeground_MatchesTheFlatPath()
    {
        // The case that forces intermediate mode to RESOLVE the destination foreground before tinting.
        // Two contributors, group at full opacity: a glyph with a translucent foreground, then a
        // translucent black scrim above it. Tinting against the surface's unresolved (still translucent)
        // foreground lands on the wrong color and the final pass folds the background in a second time.
        using var member = Scene.Create(1, 1);
        member.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(RedHalf).WithBackground(Blue)));

        using var scrim = Scene.Create(1, 1);
        scrim.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(BlackHalf)));

        var flat = Composite(Style.Default.WithBackground(Green), 1, 1,
                             [new SceneLayer(member), new SceneLayer(scrim)]);

        using var surface = Scene.Create(1, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member), new SceneLayer(scrim)]);
        var grouped = Composite(Style.Default.WithBackground(Green), 1, 1, [new SceneLayer(surface)]);

        Assert.Equal(Color.FromRgb(63, 0, 63), flat[0, 0].Style.Foreground);
        Assert.Equal(Color.FromRgb(0, 0, 127), flat[0, 0].Style.Background);
        Assert.Equal(flat[0, 0], grouped[0, 0]);
    }

    [Fact]
    public void BackdropEmojiUnderABackgroundOnlySource_StompsIdenticallyThroughAGroup()
    {
        // The emoji stomp only fires when the DESTINATION already carries an emoji, and for a single
        // source layer the destination is the base — so it is unreachable without a backdrop base. It is
        // also the one branch that writes a cell the source's footprint does not cover (the pair partner
        // repair), which is exactly the kind of asymmetry this file exists to catch.
        var backdrop = new CellBuffer(6, 1);
        backdrop.Fill(new Cell(null, CellKind.Single, Style.Default.WithBackground(Green)));
        backdrop.Set(2, 0, "🙂", Style.Default.WithForeground(Blue).WithBackground(Green));

        foreach (var opacity in GroupOpacities)
        foreach (var (column, width) in new[] { (0, 3), (3, 3), (2, 2), (0, 6) })
        {
            using var source = Scene.Create(width, 1);
            source.Draw(ctx =>
            {
                for (int c = 0; c < width; c++)
                    ctx.Set(c, 0, null, Style.Default.WithBackground(Color.FromRgba(255, 0, 0, 200)));
            });

            var parameters = new CompositeParameters(offsetColumn: column);
            AssertPathsAgree(new Setup
                             {
                                 Description = $"emoji stomp: source [{column},{column + width}) opacity {opacity}",
                                 Source = source,
                                 SourceParameters = parameters,
                                 Columns = 6,
                                 Rows = 1,
                                 GroupRect = new Rect(0, 0, 6, 1),
                                 Backdrop = backdrop
                             },
                             opacity);
        }
    }

    [Fact]
    public void WideGlyphPairInsideTheFootprint_IsBitIdenticalThroughAGroup()
    {
        // An uncut wide pair rides through as a pair: the intermediate stores the WideLeft verbatim and
        // the final pass replaces, exactly as the flat path does. Sweep the clip across every column that
        // does not cut a pair in half; the cut columns are the documented divergence below.
        foreach (var clipEnd in new[] { 2, 4, 6 })
        foreach (var opacity in GroupOpacities)
        {
            using var source = Scene.Create(6, 1);
            source.Draw(ctx =>
            {
                ctx.Set(0, 0, "漢", Style.Default.WithForeground(Blue).WithBackground(RedHalf));
                ctx.Set(2, 0, "漢", Style.Default.WithForeground(Green).WithBackground(Blue));
                ctx.Set(4, 0, "漢", Style.Default.WithForeground(Blue).WithBackground(Green));
            });

            AssertPathsAgree(new Setup
                             {
                                 Description = $"wide pair, clip end {clipEnd}",
                                 Source = source,
                                 SourceParameters = new CompositeParameters(clip: new Rect(0, 0, clipEnd, 1)),
                                 Columns = 6,
                                 Rows = 1,
                                 GroupRect = new Rect(0, 0, 6, 1),
                                 BaseStyle = Style.Default.WithBackground(Green)
                             },
                             opacity);
        }
    }

    /// <summary>
    /// <b>Replace semantics through a surface, 1 of 2.</b> A wide glyph cut in half by its layer's clip
    /// degrades to a blank single carrying the glyph's full style (<c>SceneCompositor.CompositeCell</c>, the
    /// <c>column + 1 &gt;= wideColumnEnd</c> branch). On the flat path that write lands on the target and
    /// <b>replaces</b> the destination cell. Through a group it lands on the surface, where a glyphless cell
    /// would be read back as an ordinary background-only contribution and <i>merged</i> — so intermediate
    /// mode stores it as a replacing blank and the final pass replaces with it, translating the marker back
    /// to a null grapheme. At group opacity 255 the two paths must agree cell for cell.
    /// </summary>
    /// <remarks>
    /// Reachable in production wherever a CJK/emoji glyph straddles a descendant boundary's clip edge inside
    /// a translucent ancestor — ordinary inside a <c>ScrollContentPresenter</c> / <c>TextPresenter</c>, which
    /// are boundaries from attach. Before the marker this cost the glyph's foreground, attributes and
    /// underline and left a destination glyph standing; the assertions below are the same properties, now
    /// asserted equal.
    /// </remarks>
    [Fact]
    public void WideGlyphCutAtTheFootprintEdge_KeepsReplaceSemanticsThroughAnIdentityGroup()
    {
        var baseStyle = Style.Default.WithBackground(Green).WithUnderlineStyle(UnderlineStyle.Curly);
        var glyphStyle = Style.Default.WithForeground(Blue).WithBackground(Red)
                              .WithUnderlineStyle(UnderlineStyle.Dashed).WithUnderlineColor(Blue)
                              .AddAttributes(TextAttributes.Bold);

        using var source = Scene.Create(4, 1);
        source.Draw(ctx => ctx.Set(0, 0, "漢", glyphStyle));
        Assert.Equal(CellKind.WideLeft, source.GetCell(0, 0).Kind);

        var parameters = new CompositeParameters(clip: new Rect(0, 0, 1, 1));   // cuts the pair in half

        var flat = Composite(baseStyle, 4, 1, [new SceneLayer(source, parameters)]);

        using var surface = Scene.Create(4, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(source, parameters)]);
        var grouped = Composite(baseStyle, 4, 1, [new SceneLayer(surface)]);

        // Both degrade at the same column — the intermediate sees the same layer geometry — and the surface
        // holds the cell the flat path wrote to the target, marked: same kind, same style, and a grapheme
        // that is glyph-bearing to the next pass but reads as a space anywhere else.
        Assert.Equal(CellKind.Single, surface.GetCell(0, 0).Kind);
        Assert.Equal(ReplacingBlank, surface.GetCell(0, 0).Grapheme);
        Assert.Equal(flat[0, 0].Style, surface.GetCell(0, 0).Style);

        // Every property the merge used to take from the destination now comes from the glyph, as it does
        // on the flat path — and the marker does not reach the target.
        Assert.Equal(flat[0, 0], grouped[0, 0]);
        Assert.Null(grouped[0, 0].Grapheme);
        Assert.Equal(Red, grouped[0, 0].Style.Background);
        Assert.Equal(Blue, grouped[0, 0].Style.Foreground);
        Assert.Equal(UnderlineStyle.Dashed, grouped[0, 0].Style.UnderlineStyle);
        Assert.Equal(Blue, grouped[0, 0].Style.UnderlineColor);
        Assert.Equal(TextAttributes.Bold, grouped[0, 0].Style.Attributes);

        // And a destination glyph the flat path erases is erased through the group too — at every opacity,
        // not just at the identity, since the degrade is a single-layer property.
        var backdrop = new CellBuffer(4, 1);
        backdrop.Fill(new Cell(null, CellKind.Single, baseStyle));
        backdrop.Set(0, 0, "M", baseStyle);

        var setup = new Setup
                    {
                        Description = "cut wide glyph over a backdrop glyph",
                        Source = source,
                        SourceParameters = parameters,
                        Columns = 4,
                        Rows = 1,
                        GroupRect = new Rect(0, 0, 4, 1),
                        Backdrop = backdrop
                    };

        foreach (var opacity in GroupOpacities)
            AssertPathsAgree(setup, opacity);

        var flatOverGlyph = NewTarget(setup);
        NewCompositor(setup).Composite([new SceneLayer(source, parameters)], flatOverGlyph.AsView());

        using var surfaceOverGlyph = Scene.Create(4, 1);
        surfaceOverGlyph.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(source, parameters)]);
        var groupedOverGlyph = NewTarget(setup);
        NewCompositor(setup).Composite([new SceneLayer(surfaceOverGlyph)], groupedOverGlyph.AsView());

        Assert.Null(flatOverGlyph[0, 0].Grapheme);
        Assert.Null(groupedOverGlyph[0, 0].Grapheme);
    }

    /// <summary>
    /// <b>Replace semantics through a surface, 2 of 2.</b> The same reclassification, reached through the
    /// emoji stomp instead of the wide degrade, and only when the stomped emoji belongs to a group
    /// <i>member</i> (an emoji in the compositor's own base is stomped identically on both paths —
    /// <see cref="BackdropEmojiUnderABackgroundOnlySource_StompsIdenticallyThroughAGroup"/>). The stomp
    /// writes a blank, and the buffer's pair hygiene writes a second one next door for the emoji's
    /// surviving half; on a surface <b>both</b> must carry replace semantics, which is why the compositor
    /// marks the hygiene's blank too.
    /// </summary>
    [Fact]
    public void EmojiStompedByAGroupMember_KeepsReplaceSemanticsThroughAnIdentityGroup()
    {
        var baseStyle = Style.Default.WithBackground(Green);

        // The explicit underline color keeps this test on the reclassification and off the residual pinned by
        // WidePairSurvivor_ResolvesItsUnderlineColorAtTheSurface (a wide pair's DEFAULT underline color is
        // resolved wherever the pair is stored, which for a group is the surface).
        using var lower = Scene.Create(2, 1);
        lower.Draw(ctx => ctx.Set(0, 0, "🙂", Style.Default.WithForeground(Blue).WithBackground(Red)
                                                   .WithUnderlineStyle(UnderlineStyle.Dashed)
                                                   .WithUnderlineColor(Blue)));
        Assert.Equal(CellKind.WideLeft, lower.GetCell(0, 0).Kind);

        using var upper = Scene.Create(2, 1);
        upper.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Blue)));

        var layers = new[] { new SceneLayer(lower), new SceneLayer(upper) };

        var flat = Composite(baseStyle, 2, 1, layers);

        using var surface = Scene.Create(2, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);
        var grouped = Composite(baseStyle, 2, 1, [new SceneLayer(surface)]);

        // The bitmap is gone on both paths — the stomp itself composes correctly through a group.
        Assert.Null(flat[0, 0].Grapheme);
        Assert.Null(grouped[0, 0].Grapheme);
        Assert.Equal(CellKind.Single, flat[1, 0].Kind);
        Assert.Equal(CellKind.Single, grouped[1, 0].Kind);

        // Backgrounds agree at both halves; the stomped cell's own background is the cover's, the surviving
        // partner keeps the emoji's.
        Assert.Equal(Blue, flat[0, 0].Style.Background);
        Assert.Equal(Blue, grouped[0, 0].Style.Background);
        Assert.Equal(Red, flat[1, 0].Style.Background);
        Assert.Equal(Red, grouped[1, 0].Style.Background);

        // The partner is where the reclassification used to show: it keeps the emoji cell's own foreground
        // and underline on both paths now, because the hygiene's blank carries the marker on the surface.
        Assert.Equal(ReplacingBlank, surface.GetCell(1, 0).Grapheme);
        Assert.Equal(Blue, grouped[1, 0].Style.Foreground);
        Assert.Equal(UnderlineStyle.Dashed, grouped[1, 0].Style.UnderlineStyle);
        Assert.Equal(flat[0, 0], grouped[0, 0]);
        Assert.Equal(flat[1, 0], grouped[1, 0]);
    }

    [Fact]
    public void MemberOverwritingAnotherMembersWidePair_ErasesTheDestinationGlyphThroughAGroup()
    {
        // Not one of the compositor's own blanking branches: this blank is written by the BUFFER. A member's
        // narrow glyph lands on the left half of a lower member's wide pair and the pair hygiene strips the
        // orphaned right half to a blank single. On the flat path that strip lands on the target and erases
        // whatever the base held there; on a surface it has to replace too, or the base's glyph survives
        // under the group.
        var backdrop = new CellBuffer(4, 1);
        backdrop.Fill(new Cell(null, CellKind.Single, Style.Default.WithBackground(Green)));
        backdrop.Set(1, 0, "M", Style.Default.WithForeground(Blue).WithBackground(Green));

        // An explicit OPAQUE underline color: a wide pair's underline color is resolved wherever the pair is
        // stored, and for a default/translucent one that is the surface rather than the screen — the residual
        // pinned by WidePairSurvivor_ResolvesItsUnderlineColorAtTheSurface, orthogonal to what this test is
        // about.
        using var lower = Scene.Create(2, 1);
        lower.Draw(ctx => ctx.Set(0, 0, "漢", Style.Default.WithForeground(Blue).WithBackground(Red)
                                                  .WithUnderlineStyle(UnderlineStyle.Dashed)
                                                  .WithUnderlineColor(Blue)));

        using var upper = Scene.Create(1, 1);
        upper.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(Green).WithBackground(Blue)));

        var layers = new[] { new SceneLayer(lower), new SceneLayer(upper) };

        var flat = TargetOver(backdrop);
        new SceneCompositor(backdrop).Composite(layers, flat.AsView());

        using var surface = Scene.Create(4, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);
        var grouped = TargetOver(backdrop);
        new SceneCompositor(backdrop).Composite([new SceneLayer(surface)], grouped.AsView());

        Assert.Equal(ReplacingBlank, surface.GetCell(1, 0).Grapheme);
        Assert.Null(flat[1, 0].Grapheme);      // the wide pair covered "M", and the strip left a blank
        Assert.Null(grouped[1, 0].Grapheme);
        AssertBuffersEqual(flat, grouped, "member overwriting another member's wide pair");
    }

    [Fact]
    public void MemberWidePairLandingOneColumnShortOfAnother_MatchesTheFlatPath()
    {
        // The buffer's third blanking site: storing a wide pair whose right half lands on ANOTHER pair's
        // WideLeft strips that pair's continuation TWO columns over (CellBuffer.WriteWidePair). Same rule,
        // same consequence on a surface — and the only orphan the compositor's own write does not sit
        // adjacent to.
        var backdrop = new CellBuffer(5, 1);
        backdrop.Fill(new Cell(null, CellKind.Single, Style.Default.WithBackground(Green)));
        backdrop.Set(2, 0, "M", Style.Default.WithForeground(Blue).WithBackground(Green));

        using var lower = Scene.Create(3, 1);
        lower.Draw(ctx => ctx.Set(1, 0, "漢", Style.Default.WithForeground(Blue).WithBackground(Red)
                                                  .WithUnderlineColor(Blue)));   // see the test above

        using var upper = Scene.Create(2, 1);
        upper.Draw(ctx => ctx.Set(0, 0, "漢", Style.Default.WithForeground(Green).WithBackground(Blue)));

        var layers = new[] { new SceneLayer(lower), new SceneLayer(upper) };

        var flat = TargetOver(backdrop);
        new SceneCompositor(backdrop).Composite(layers, flat.AsView());

        using var surface = Scene.Create(5, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);
        var grouped = TargetOver(backdrop);
        new SceneCompositor(backdrop).Composite([new SceneLayer(surface)], grouped.AsView());

        Assert.Equal(ReplacingBlank, surface.GetCell(2, 0).Grapheme);
        Assert.Null(flat[2, 0].Grapheme);
        Assert.Null(grouped[2, 0].Grapheme);
        AssertBuffersEqual(flat, grouped, "wide pair landing one column short of another");
    }

    /// <summary>
    /// <b>RESIDUAL DIVERGENCE — not cell reclassification, and not closed by the replacing-blank marker.</b>
    /// <see cref="CellBuffer.Set"/> resolves the style it stores against the destination cell
    /// (<c>Style.BlendOver</c>): an underline color that is not <see cref="Color.Default"/> composites over the
    /// destination's <i>background</i>, one that is inherits the destination's underline color. A wide pair is
    /// the only write the compositor makes through <c>Set</c>, so a wide glyph's underline color is resolved
    /// wherever its pair is stored — through a group that is the surface, whose base is
    /// <see cref="Style.Transparent"/>. An intact pair is re-resolved by the outer <c>Set</c> and agrees; the
    /// SURVIVING HALF of a pair the hygiene broke leaves the surface as a replacing blank, written through the
    /// indexer, and carries the surface-resolved value to the screen.
    /// </summary>
    /// <remarks>
    /// Bounded to <see cref="Style.UnderlineColor"/> — grapheme, kind, background, foreground, attributes and
    /// underline <i>style</i> all agree. It needs an underline color that is still translucent when it reaches
    /// the compositor, which is what a scene's transparent clear turns <see cref="Color.Default"/> into for a
    /// cell carrying no <see cref="TextAttributes.Underline"/> — and SGR 58 is only emitted for a cell that
    /// carries one. A genuinely underlined cell keeps <see cref="Color.Default"/> through the clear and is
    /// bit-identical on both paths (asserted below), as is any cell with an opaque underline color. Closing it
    /// means not routing the compositor's already-composited style back through <c>Set</c>'s blend — a change
    /// to what the flat path stores, and so out of scope for the group work.
    /// </remarks>
    [Fact]
    public void WidePairSurvivor_ResolvesItsUnderlineColorAtTheSurface()
    {
        var pairStyle = Style.Default.WithForeground(Blue).WithBackground(Red)
                             .WithUnderlineStyle(UnderlineStyle.Dashed);   // underline color left at Default

        var (flat, grouped) = BrokenPairOverGreen(pairStyle);

        // Everything except the underline color agrees, including the erasure the marker is there for.
        Assert.Equal(flat[1, 0] with { Style = flat[1, 0].Style.WithUnderlineColor(Color.Default) },
                     grouped[1, 0] with { Style = grouped[1, 0].Style.WithUnderlineColor(Color.Default) });

        // Flat resolved it against the screen cell's background; the group against Style.Transparent's.
        Assert.Equal(Green, flat[1, 0].Style.UnderlineColor);
        Assert.Equal(Color.Transparent, grouped[1, 0].Style.UnderlineColor);

        // A cell that is actually underlined keeps Color.Default through the scene's clear, so both paths
        // resolve it to Color.Default and the divergence is unreachable for any cell that can emit SGR 58.
        var (flatUnderlined, groupedUnderlined) =
            BrokenPairOverGreen(pairStyle.AddAttributes(TextAttributes.Underline));

        Assert.Equal(Color.Default, flatUnderlined[1, 0].Style.UnderlineColor);
        AssertBuffersEqual(flatUnderlined, groupedUnderlined, "underlined wide pair survivor");
    }

    // A lower member's wide pair at (0, 1) with the left half overwritten by an upper member's narrow glyph,
    // over an opaque green base: the pair hygiene's blank lands at (1, 0) on both paths.
    private static (CellBuffer Flat, CellBuffer Grouped) BrokenPairOverGreen(Style pairStyle)
    {
        var baseStyle = Style.Default.WithBackground(Green);

        using var lower = Scene.Create(2, 1);
        lower.Draw(ctx => ctx.Set(0, 0, "漢", pairStyle));

        using var upper = Scene.Create(1, 1);
        upper.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(Green).WithBackground(Blue)));

        var layers = new[] { new SceneLayer(lower), new SceneLayer(upper) };

        var flat = Composite(baseStyle, 3, 1, layers);

        using var surface = Scene.Create(3, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);

        return (flat, Composite(baseStyle, 3, 1, [new SceneLayer(surface)]));
    }

    /// <summary>
    /// <b>The regression pin for the marker's value.</b> <see cref="CellBuffer.DurableEmptyGrapheme"/> (NBSP)
    /// is the glyph <c>FillOpaque</c> stores in every panel fill — real content that must reach the target
    /// intact, not a compositor signal to translate away. It used to be the marker's value too, which is the
    /// only reason the comparison was ever by reference; a value comparison against NBSP would silently turn
    /// every opaque fill inside a group back into an occludable blank. The marker is now a noncharacter, so
    /// the two cannot collide by value — and this test is what says so.
    /// </summary>
    [Fact]
    public void DurableEmptyCells_RideThroughAGroupUnchanged()
    {
        var baseStyle = Style.Default.WithBackground(Green);

        using var panel = Scene.Create(3, 1);
        panel.Draw(ctx => ctx.FillOpaque(new Rect(0, 0, 3, 1), Blue));
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, panel.GetCell(0, 0).Grapheme);

        var flat = Composite(baseStyle, 3, 1, [new SceneLayer(panel)]);

        using var surface = Scene.Create(3, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(panel)]);

        // The durable empty is on the SURFACE untranslated — the intermediate stored the content it was
        // handed, not a marker. (Were the marker NBSP-valued and compared by value, the surface would look
        // identical here and the divergence would only show on the target below.)
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, surface.GetCell(0, 0).Grapheme);

        var grouped = Composite(baseStyle, 3, 1, [new SceneLayer(surface)]);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, flat[0, 0].Grapheme);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, grouped[0, 0].Grapheme);
        AssertBuffersEqual(flat, grouped, "durable empties through a group");

        // And through NESTED groups: every intermediate pass is a chance to mistake it for the marker.
        using var inner = Scene.Create(3, 1);
        inner.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(panel)]);
        using var outer = Scene.Create(3, 1);
        outer.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(inner)]);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, outer.GetCell(0, 0).Grapheme);
        AssertBuffersEqual(flat, Composite(baseStyle, 3, 1, [new SceneLayer(outer)]),
                           "durable empties through nested groups");

        // The durable empty also still OCCLUDES through a group — the property FillOpaque wants it for. A
        // glyph on a lower layer must not show through the panel's blank cells.
        using var lower = Scene.Create(3, 1);
        lower.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(Red).WithBackground(Red)));

        var stackedFlat = Composite(baseStyle, 3, 1, [new SceneLayer(lower), new SceneLayer(panel)]);
        using var stackedSurface = Scene.Create(3, 1);
        stackedSurface.CompositeInto(SceneCompositor.ForIntermediate(),
                                     [new SceneLayer(lower), new SceneLayer(panel)]);
        var stackedGrouped = Composite(baseStyle, 3, 1, [new SceneLayer(stackedSurface)]);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, stackedFlat[0, 0].Grapheme);   // "X" is covered
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, stackedGrouped[0, 0].Grapheme);
        AssertBuffersEqual(stackedFlat, stackedGrouped, "durable empty occluding a glyph through a group");
    }

    /// <summary>
    /// The marker's value is a Unicode <b>noncharacter</b> (U+FFFF), which is what licenses comparing it by
    /// value instead of by reference. Two things have to hold for that: the value can never arrive as real
    /// content, and while it sits in an intermediate buffer it must behave like the plain single-width blank
    /// it stands in for — no width, cluster or emoji-classification surprise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which case is true for "content equal to the marker".</b> Not "it round-trips" — the compositor
    /// CLAIMS the value. Unicode guarantees noncharacters never occur in valid interchanged text, so no
    /// scene, font, clipboard paste or layout path can produce one; but <c>DrawingContext.Set</c> does not
    /// validate, so a caller who deliberately writes U+FFFF gets a cell the final pass blanks. That is the
    /// better of the two outcomes available — the alternative is emitting a codepoint no font draws — and it
    /// is asserted below rather than left to chance.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReplacingBlankMarker_IsANoncharacterThatBehavesLikeABlankInTheBuffer()
    {
        // Width 1, exactly like the NBSP it replaced: the marker must never perturb the pair arithmetic of
        // the buffer it sits in. (It reaches CellBuffer.Set only as a `previous` cell, but a surface cell is
        // measured by anything that walks the buffer.)
        Assert.Equal(1, GraphemeWidth.ClusterWidth(ReplacingBlank.AsSpan()));
        Assert.Equal(1, GraphemeWidth.CodepointWidth(0xFFFF));
        Assert.Equal(1, GraphemeWidth.StringWidth(ReplacingBlank.AsSpan()));
        Assert.Equal(1, GraphemeWidth.ClusterCount(ReplacingBlank.AsSpan()));

        // Not emoji-presentation: the compositor's stomp classifier must not see the marker as a bitmap
        // glyph to erase, and the width table must not promote it to two cells through that path.
        Assert.False(GraphemeWidth.IsEmojiPresentation(ReplacingBlank.AsSpan()));

        // Stored, it is an ordinary single cell — not a wide left, not a continuation, not dropped.
        var probe = new CellBuffer(2, 1);
        Assert.Equal(1, probe.Set(0, 0, ReplacingBlank, Style.Default));
        Assert.Equal(CellKind.Single, probe[0, 0].Kind);
        Assert.Equal(ReplacingBlank, probe[0, 0].Grapheme);

        // A caller CAN write the value as content (nothing validates it), and the compositor claims it: the
        // final pass translates it to a blank rather than emitting a noncharacter to the terminal.
        using var source = Scene.Create(1, 1);
        source.Draw(ctx => ctx.Set(0, 0, ReplacingBlank, Style.Default.WithForeground(Blue).WithBackground(Red)));
        Assert.Equal(ReplacingBlank, source.GetCell(0, 0).Grapheme);

        var target = Composite(Style.Default.WithBackground(Green), 1, 1, [new SceneLayer(source)]);

        Assert.Null(target[0, 0].Grapheme);
        Assert.Equal(Red, target[0, 0].Style.Background);   // it still REPLACED — it is a replacing blank
        Assert.Equal(Blue, target[0, 0].Style.Foreground);
    }

    /// <summary>
    /// The marker never reaches a buffer a <c>FrameRenderer</c> would emit — across the whole generated
    /// matrix, and through nested groups. This is the property the DEBUG diagnostic
    /// (<see cref="DrawingDiagnosticKind.ReplacingBlankReachedFinalTarget"/>) exists to catch in the field;
    /// here it is asserted directly, on the cells, as well as through the diagnostic channel.
    /// </summary>
    [Fact]
    public void ReplacingBlankMarker_NeverReachesAFinalTarget()
    {
        var events = new List<DrawingDiagnosticEvent>();
        void Sink(DrawingDiagnosticEvent e)
        {
            if (e.Kind == DrawingDiagnosticKind.ReplacingBlankReachedFinalTarget) lock (events) events.Add(e);
        }

        DrawingDiagnostics.DiagnosticRaised += Sink;
        try
        {
            var random = new Random(Seed + 7);
            var coverage = new Coverage();

            for (int index = 0; index < 60; index++)
            {
                var setup = RandomSetup(random, index, coverage);

                foreach (var opacity in GroupOpacities)
                {
                    var grouped = NewTarget(setup);
                    using var surface = Scene.Create(setup.GroupRect.Columns, setup.GroupRect.Rows);
                    surface.CompositeInto(SceneCompositor.ForIntermediate(),
                                          [new SceneLayer(setup.Source, Rebase(setup.SourceParameters, setup.GroupRect))]);

                    // A second, nested surface: the marker has to survive one more intermediate hop and
                    // still be translated exactly once.
                    using var outer = Scene.Create(setup.GroupRect.Columns, setup.GroupRect.Rows);
                    outer.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(surface)]);

                    NewCompositor(setup).Composite(
                        [
                            new SceneLayer(outer, new CompositeParameters(setup.GroupRect.Column, setup.GroupRect.Row,
                                                                          opacity, mode: setup.SourceParameters.Mode))
                        ],
                        grouped.AsView());

                    AssertNoMarker(grouped, $"{setup.Description}, opacity {opacity}");
                }

                setup.Source.Dispose();
            }

            Assert.Empty(events);
        }
        finally
        {
            DrawingDiagnostics.DiagnosticRaised -= Sink;
        }
    }

    /// <summary>
    /// The DEBUG tripwire itself. A base layer that is a group SURFACE — the one leak the translate path
    /// cannot close, because reset-to-base copies its cells verbatim — must be reported, not silently carried
    /// to the screen. Release builds compile the check away, so the assertion is conditioned on DEBUG.
    /// </summary>
    [Fact]
    public void ReplacingBlankMarkerOnTheBaseLayer_IsReportedByTheDebugDiagnostic()
    {
        var events = new List<DrawingDiagnosticEvent>();
        void Sink(DrawingDiagnosticEvent e)
        {
            if (e.Kind == DrawingDiagnosticKind.ReplacingBlankReachedFinalTarget) lock (events) events.Add(e);
        }

        // A surface holding a marker: a wide glyph cut in half by its layer's clip.
        using var member = Scene.Create(4, 1);
        member.Draw(ctx => ctx.Set(0, 0, "漢", Style.Default.WithForeground(Blue).WithBackground(Red)));

        using var surface = Scene.Create(4, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(),
                              [new SceneLayer(member, new CompositeParameters(clip: new Rect(0, 0, 1, 1)))]);
        Assert.Equal(ReplacingBlank, surface.GetCell(0, 0).Grapheme);

        // Hand that surface to a FINAL compositor as its stored backdrop — reset-to-base copies the cell
        // straight through, so nothing translates it.
        var poisoned = new CellBuffer(4, 1);
        for (int column = 0; column < 4; column++)
            poisoned[column, 0] = surface.GetCell(column, 0);

        using var content = Scene.Create(1, 1);
        content.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Color.FromRgba(0, 0, 255, 128))));

        DrawingDiagnostics.DiagnosticRaised += Sink;
        try
        {
            var target = new CellBuffer(4, 1);
            new SceneCompositor(poisoned).Composite([new SceneLayer(content, new CompositeParameters(offsetColumn: 3))],
                                                    target.AsView());

            Assert.Equal(ReplacingBlank, target[0, 0].Grapheme);   // the leak the diagnostic is reporting

#if DEBUG
            var reported = Assert.Single(events);
            Assert.Contains("(0, 0)", reported.Message);
#else
            Assert.Empty(events);   // the check compiles away in release
#endif
        }
        finally
        {
            DrawingDiagnostics.DiagnosticRaised -= Sink;
        }
    }

    /// <summary>
    /// <see cref="SceneCompositor.ResolveSurfaceCell"/> — the seam every non-compositor reader of a scene
    /// that might be a group surface goes through (today: <c>WindowManager.SampleCell</c>, the designer's
    /// composition inspector). It must report the cell the screen shows, and it must be safe to apply
    /// unconditionally to cells that are ordinary content.
    /// </summary>
    [Fact]
    public void ResolveSurfaceCell_TranslatesTheMarkerAndLeavesRealContentAlone()
    {
        var glyphStyle = Style.Default.WithForeground(Blue).WithBackground(Red)
                              .WithUnderlineStyle(UnderlineStyle.Dashed).AddAttributes(TextAttributes.Bold);

        using var member = Scene.Create(4, 1);
        member.Draw(ctx => ctx.Set(0, 0, "漢", glyphStyle));

        var parameters = new CompositeParameters(clip: new Rect(0, 0, 1, 1));

        using var surface = Scene.Create(4, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member, parameters)]);

        var raw = surface.GetCell(0, 0);
        Assert.Equal(ReplacingBlank, raw.Grapheme);   // the raw accessor stays honest about what is stored

        var resolved = SceneCompositor.ResolveSurfaceCell(raw);

        // Exactly the cell the flat path puts on screen — only the grapheme moves.
        var flat = Composite(Style.Default.WithBackground(Green), 4, 1, [new SceneLayer(member, parameters)]);
        Assert.Equal(flat[0, 0], resolved);
        Assert.Null(resolved.Grapheme);
        Assert.Equal(raw with { Grapheme = null }, resolved);

        // Real content is returned untouched — including the durable empty, which the NBSP-valued marker
        // would have eaten here.
        Assert.Equal(Cell.DurableEmpty, SceneCompositor.ResolveSurfaceCell(Cell.DurableEmpty));
        Assert.Equal(surface.GetCell(3, 0), SceneCompositor.ResolveSurfaceCell(surface.GetCell(3, 0)));

        var glyph = new Cell("A", CellKind.Single, glyphStyle);
        Assert.Equal(glyph, SceneCompositor.ResolveSurfaceCell(glyph));
    }

    private static void AssertNoMarker(CellBuffer target, string context)
    {
        for (int row = 0; row < target.Rows; row++)
        for (int column = 0; column < target.Columns; column++)
        {
            if (target[column, row].Grapheme != ReplacingBlank) continue;
            Assert.Fail($"{context}: the compositor's replacing-blank marker reached the final target at " +
                        $"({column}, {row})");
        }
    }

    [Fact]
    public void NestedGroups_TranslateTheReplacingBlankExactlyOnce()
    {
        // A group inside a group. The marker has to survive the inner surface being composited onto the
        // outer one (still an intermediate) and be translated exactly once, by the pass that writes the
        // buffer a FrameRenderer diffs: translating earlier loses replace semantics again, later leaks a
        // compositor-internal grapheme into the terminal's buffer.
        var baseStyle = Style.Default.WithBackground(Green).WithUnderlineStyle(UnderlineStyle.Curly);
        var glyphStyle = Style.Default.WithForeground(Blue).WithBackground(Red)
                              .WithUnderlineStyle(UnderlineStyle.Dashed).AddAttributes(TextAttributes.Bold);

        using var member = Scene.Create(4, 1);
        member.Draw(ctx => ctx.Set(0, 0, "漢", glyphStyle));

        var parameters = new CompositeParameters(clip: new Rect(0, 0, 1, 1));   // cuts the pair in half

        var flat = Composite(baseStyle, 4, 1, [new SceneLayer(member, parameters)]);

        using var inner = Scene.Create(4, 1);
        inner.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member, parameters)]);

        using var outer = Scene.Create(4, 1);
        outer.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(inner)]);

        Assert.Equal(ReplacingBlank, inner.GetCell(0, 0).Grapheme);
        Assert.Equal(ReplacingBlank, outer.GetCell(0, 0).Grapheme);   // carried, not translated

        var grouped = Composite(baseStyle, 4, 1, [new SceneLayer(outer)]);

        Assert.Null(grouped[0, 0].Grapheme);
        AssertBuffersEqual(flat, grouped, "nested identity groups");
    }

    // ---- Multi-contributor groups: where the property stops being bit-exact ----

    [Fact]
    public void MultiContributorGroupAtFullOpacity_WithOpaqueBackgrounds_IsBitIdenticalToTheFlatStack()
    {
        // The identity-group property, on the members it is exact for. An opaque member's background
        // makes every CompositeOver on the surface take the opaque-backdrop fast path, which is
        // bit-identical to Composite by construction — so the surface holds exactly what the flat target
        // would have held, and blending it down at 255 is the identity.
        //
        // Narrow glyphs only, and no longer because of the reclassification — that is closed, and the
        // single-layer sweeps now cover cut pairs and stomped emoji bit-exactly. What survives here is the
        // underline-color residual (WidePairSurvivor_ResolvesItsUnderlineColorAtTheSurface): these members
        // carry randomly translucent underline colors, and a wide pair's is resolved wherever the pair is
        // stored. Widening this generator reproduces exactly that one field and nothing else — verified, and
        // orthogonal to opacity grouping, so excluding it is what keeps this an exact assertion.
        var random = new Random(Seed + 2);

        for (int index = 0; index < 80; index++)
        {
            int columns = random.Next(4, 9);
            int rows = random.Next(1, 4);
            var baseStyle = RandomStyle(random, translucent: false);
            var layers = new List<SceneLayer>();
            var scenes = new List<Scene>();

            for (int layer = 0; layer < random.Next(2, 5); layer++)
            {
                var scene = Scene.Create(random.Next(1, columns + 1), random.Next(1, rows + 1));
                PaintRandomContent(scene, random, new Coverage(), opaqueBackgrounds: true, narrowGlyphsOnly: true);
                scenes.Add(scene);
                layers.Add(new SceneLayer(scene, new CompositeParameters(random.Next(0, columns), random.Next(0, rows))));
            }

            var flat = Composite(baseStyle, columns, rows, CollectionsMarshal.AsSpan(layers));

            using var surface = Scene.Create(columns, rows);
            surface.CompositeInto(SceneCompositor.ForIntermediate(), CollectionsMarshal.AsSpan(layers));
            var grouped = Composite(baseStyle, columns, rows, [new SceneLayer(surface)]);

            AssertBuffersEqual(flat, grouped, $"identity group, opaque members, case {index}");

            foreach (var scene in scenes)
                scene.Dispose();
        }
    }

    /// <summary>
    /// Overlapping translucent member <i>backgrounds</i> over an RGB backdrop: the benign case. The group
    /// evaluates <c>Composite(CompositeOver(B, A), base)</c> where the flat path evaluates
    /// <c>Composite(B, Composite(A, base))</c> — the two associations of the same Porter-Duff "over". They
    /// agree in exact arithmetic and cannot agree bit-for-bit in 8-bit integers, because each rounds to a
    /// byte at a different point. Neither is more correct pointwise; the deviation is bounded by the
    /// rounding, and the bound below is measured over this matrix rather than assumed.
    /// </summary>
    /// <remarks>
    /// Background-only members over an opaque RGB base, deliberately: a glyph in the stack reaches the
    /// resolved-tint divergence and a non-RGB base reaches the alpha-flattening divergence, both of which
    /// are characterised by their own tests below and would swamp this measurement.
    /// </remarks>
    [Fact]
    public void MultiContributorGroup_OverlappingTranslucentBackgrounds_DivergeOnlyByIntegerRounding()
    {
        var random = new Random(Seed + 3);
        int worstChannel = 0;
        int worstAlpha = 0;
        int compared = 0;
        int differing = 0;
        string worstContext = "none";

        for (int index = 0; index < 120; index++)
        {
            int columns = random.Next(3, 8);
            int rows = random.Next(1, 4);
            var baseStyle = OpaqueRgbStyle(random);
            var layers = new List<SceneLayer>();
            var scenes = new List<Scene>();

            for (int layer = 0; layer < random.Next(2, 4); layer++)
            {
                var scene = Scene.Create(columns, rows);
                PaintTranslucentBackgrounds(scene, random);
                scenes.Add(scene);
                layers.Add(new SceneLayer(scene));
            }

            var flat = Composite(baseStyle, columns, rows, CollectionsMarshal.AsSpan(layers));

            using var surface = Scene.Create(columns, rows);
            surface.CompositeInto(SceneCompositor.ForIntermediate(), CollectionsMarshal.AsSpan(layers));
            var grouped = Composite(baseStyle, columns, rows, [new SceneLayer(surface)]);

            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                // Structure — grapheme, cell kind, color kind, attributes — is never allowed to drift.
                var (channel, alpha) = Deviation(flat[column, row], grouped[column, row],
                                                 $"case {index} at ({column}, {row})");
                compared++;
                if (channel > 0 || alpha > 0) differing++;
                if (channel > worstChannel)
                {
                    worstContext = $"case {index} at ({column}, {row}): flat {Describe(flat[column, row])} " +
                                   $"vs grouped {Describe(grouped[column, row])}";
                }

                worstChannel = Math.Max(worstChannel, channel);
                worstAlpha = Math.Max(worstAlpha, alpha);
            }

            foreach (var scene in scenes)
                scene.Dispose();
        }

        Assert.True(worstChannel <= 2 && worstAlpha == 0,
                    $"worst deviation {worstChannel} channel / {worstAlpha} alpha over {differing} of " +
                    $"{compared} cells: {worstContext}");

        // Pinned as an exact maximum, not an upper bound: a change that widens it fails here, and a change
        // that narrows it has to come and re-pin deliberately.
        Assert.Equal(2, worstChannel);
    }

    /// <summary>
    /// <b>DOCUMENTED DIVERGENCE 3 of 4.</b> Over a <b>non-RGB backdrop</b> — a palette color or the
    /// terminal default, which is the compositor's own <see cref="Style.Default"/> base — grouping moves
    /// where alpha is flattened. <see cref="Color.Composite"/> and <see cref="Color.CompositeOver"/> both
    /// discard the backdrop and return the blended source at alpha 255 when either operand is non-RGB
    /// (deliberately: there is no lossless RGB for a palette entry). Flat, that happens at the
    /// <i>first</i> composite, so every member above it blends over an already-opaque color. Grouped, the
    /// members blend among themselves with their alpha intact and the flattening happens <i>last</i>.
    /// </summary>
    /// <remarks>
    /// The group's answer is the better-founded one — the members really do combine with each other before
    /// meeting the backdrop — but it is a visible color change, not rounding, and the terminal default
    /// background makes it common rather than exotic. It is worth deciding deliberately before the UI layer
    /// starts producing groups.
    /// </remarks>
    [Fact]
    public void MultiContributorGroupOverANonRgbBackdrop_MovesWhereAlphaIsFlattened()
    {
        var baseStyle = Style.Default;   // foreground and background both Color.Default

        using var lower = Scene.Create(1, 1);
        lower.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Color.FromRgba(255, 0, 0, 128))));

        using var upper = Scene.Create(1, 1);
        upper.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Color.FromRgba(0, 0, 255, 128))));

        var layers = new[] { new SceneLayer(lower), new SceneLayer(upper) };

        var flat = Composite(baseStyle, 1, 1, layers);

        using var surface = Scene.Create(1, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);
        var grouped = Composite(baseStyle, 1, 1, [new SceneLayer(surface)]);

        // Flat: red@128 over Default flattens to opaque red, then blue@128 over that.
        Assert.Equal(Color.FromRgb(127, 0, 128), flat[0, 0].Style.Background);

        // Grouped: blue@128 over red@128 keeps its alpha on the surface, and only the last step flattens.
        Assert.Equal(Color.FromRgba(85, 0, 170, 192), surface.GetCell(0, 0).Style.Background);
        Assert.Equal(Color.FromRgb(85, 0, 170), grouped[0, 0].Style.Background);

        // A single translucent member over the same backdrop is unaffected — the property this file is
        // named for holds, and it is stacking that moves the flattening point.
        var flatOne = Composite(baseStyle, 1, 1, [new SceneLayer(lower)]);
        using var surfaceOne = Scene.Create(1, 1);
        surfaceOne.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(lower)]);
        var groupedOne = Composite(baseStyle, 1, 1, [new SceneLayer(surfaceOne)]);
        Assert.Equal(flatOne[0, 0], groupedOne[0, 0]);
    }

    /// <summary>
    /// <b>DOCUMENTED DIVERGENCE 4 of 4.</b> Intermediate mode resolves the destination foreground before
    /// tinting a background-only member over it (<c>CompositeOver(dst.Foreground, dst.Background)</c>) —
    /// the rule that makes an identity group exact when the covered glyph's cell background is
    /// <b>opaque</b>. When that background is translucent the surface has no backdrop behind it yet, so the
    /// resolve runs against the member's own background alone, while the flat path resolved against that
    /// background <i>already composited onto the group's backdrop</i>. The cell background still agrees
    /// exactly; the foreground does not.
    /// </summary>
    /// <remarks>
    /// Requires all three of: a glyph-bearing member, a translucent foreground on it, a translucent
    /// background on the same cell, and a background-only member above it. A glyph over an opaque panel —
    /// the shape of essentially every real translucent dialog — is exact.
    /// </remarks>
    [Fact]
    public void GlyphOverATranslucentCellBackground_CoveredByAScrim_DivergesInTheForeground()
    {
        var baseStyle = Style.Default.WithForeground(Red).WithBackground(Red);

        using var text = Scene.Create(1, 1);
        text.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(Color.FromRgba(255, 255, 255, 128))
                                                 .WithBackground(Color.FromRgba(0, 0, 0, 64))));

        using var scrim = Scene.Create(1, 1);
        scrim.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Color.FromRgba(0, 0, 255, 128))));

        var layers = new[] { new SceneLayer(text), new SceneLayer(scrim) };

        var flat = Composite(baseStyle, 1, 1, layers);

        using var surface = Scene.Create(1, 1);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), layers);
        var grouped = Composite(baseStyle, 1, 1, [new SceneLayer(surface)]);

        Assert.Equal("X", flat[0, 0].Grapheme);
        Assert.Equal("X", grouped[0, 0].Grapheme);

        // The background is exact — the divergence is confined to the foreground.
        Assert.Equal(Color.FromRgb(95, 0, 128), flat[0, 0].Style.Background);
        Assert.Equal(flat[0, 0].Style.Background, grouped[0, 0].Style.Background);

        // Flat resolves the glyph over black@25%-over-red; the group resolves it over black@25% alone and
        // folds the difference in at the end.
        Assert.Equal(Color.FromRgb(111, 63, 191), flat[0, 0].Style.Foreground);
        Assert.Equal(Color.FromRgb(81, 63, 215), grouped[0, 0].Style.Foreground);

        // The same stack with an OPAQUE cell background under the glyph is bit-identical — which is what
        // the resolved-tint rule was verified against, and what makes sticky group-ness safe in practice.
        using var opaqueText = Scene.Create(1, 1);
        opaqueText.Draw(ctx => ctx.Set(0, 0, "X", Style.Default.WithForeground(Color.FromRgba(255, 255, 255, 128))
                                                        .WithBackground(Blue)));

        var opaqueLayers = new[] { new SceneLayer(opaqueText), new SceneLayer(scrim) };
        var flatOpaque = Composite(baseStyle, 1, 1, opaqueLayers);
        using var surfaceOpaque = Scene.Create(1, 1);
        surfaceOpaque.CompositeInto(SceneCompositor.ForIntermediate(), opaqueLayers);
        var groupedOpaque = Composite(baseStyle, 1, 1, [new SceneLayer(surfaceOpaque)]);

        Assert.Equal(flatOpaque[0, 0], groupedOpaque[0, 0]);
    }

    // ---- The non-cell divergence: damage granularity ----

    [Fact]
    public void GroupPath_MarksAtLeastAsMuchDirtyAsTheFlatPath_AndUsuallyMore()
    {
        // Not a cell divergence, but the reviewer should see it: on a STEADY-STATE frame the flat path
        // marks the member's footprint dirty and the group path marks the whole group layer's footprint,
        // because the outer compositor's only change signal is a bumped RasterVersion and the damage it can
        // infer from that is the layer's full footprint. Over-reporting is safe (a renderer repaints more);
        // under-reporting would be a stale-frame bug, so the containment direction is what is asserted.
        // Narrowing this is the scheduled SceneLayer.Damage work, which is why Scene.CompositeInto already
        // reports the region it rewrote.
        using var source = Scene.Create(2, 1);
        source.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Blue)));

        var setup = new Setup
                    {
                        Description = "damage granularity",
                        Source = source,
                        SourceParameters = new CompositeParameters(offsetColumn: 3, offsetRow: 2),
                        Columns = 10,
                        Rows = 5,
                        GroupRect = new Rect(1, 1, 8, 4),
                        BaseStyle = Style.Default.WithBackground(Green)
                    };

        var memberParameters = setup.SourceParameters.WithOpacity(128);
        var groupParameters = new CompositeParameters(setup.GroupRect.Column, setup.GroupRect.Row, 128);
        var rebased = Rebase(setup.SourceParameters, setup.GroupRect);

        var flat = NewTarget(setup);
        var flatCompositor = NewCompositor(setup);
        flatCompositor.Composite([new SceneLayer(source, memberParameters)], flat.AsView());

        using var surface = Scene.Create(setup.GroupRect.Columns, setup.GroupRect.Rows);
        var surfaceCompositor = SceneCompositor.ForIntermediate();   // retained across frames, like the real one
        var groupedCompositor = NewCompositor(setup);
        var grouped = NewTarget(setup);
        surface.CompositeInto(surfaceCompositor, [new SceneLayer(source, rebased)]);
        groupedCompositor.Composite([new SceneLayer(surface, groupParameters)], grouped.AsView());

        AssertBuffersEqual(flat, grouped, "damage granularity, first frame");

        // Frame 2: repaint the member with different content. The first frame is a full reset on both
        // paths (the layer set is new), so only a steady-state frame shows the granularity difference.
        flat.ClearDirty();
        grouped.ClearDirty();
        source.Invalidate();
        source.Draw(ctx => ctx.Set(0, 0, null, Style.Default.WithBackground(Red)));

        flatCompositor.Composite([new SceneLayer(source, memberParameters)], flat.AsView());
        var rewritten = surface.CompositeInto(surfaceCompositor, [new SceneLayer(source, rebased)]);
        groupedCompositor.Composite([new SceneLayer(surface, groupParameters)], grouped.AsView());

        AssertBuffersEqual(flat, grouped, "damage granularity, second frame");

        var flatDamage = Assert.Single(flat.DirtyRegions);
        var groupDamage = Assert.Single(grouped.DirtyRegions);

        Assert.Equal(new Rect(3, 2, 2, 1), flatDamage);          // just the member
        Assert.Equal(setup.GroupRect, groupDamage);              // the whole group surface
        Assert.True(groupDamage.Contains(flatDamage), $"{groupDamage} must contain {flatDamage}");

        // The surface itself reports the tight region it rewrote, in surface-local coordinates — the raw
        // material for narrowing the outer mark. Translated back it is exactly the flat path's damage.
        Assert.Equal(new Rect(2, 1, 2, 1), rewritten);
    }

    // ---- Harness ----

    private sealed class Setup
    {
        public required string Description { get; init; }
        public required Scene Source { get; init; }
        public required CompositeParameters SourceParameters { get; init; }
        public required int Columns { get; init; }
        public required int Rows { get; init; }

        /// <summary>Where the group surface sits on the target; must contain the source's footprint.</summary>
        public required Rect GroupRect { get; init; }

        public Style BaseStyle { get; init; }
        public CellBuffer? Backdrop { get; init; }
    }

    private static void AssertPathsAgree(Setup setup, byte opacity, bool opacityOnTheMember = false)
    {
        var memberOpacity = opacityOnTheMember ? opacity : (byte) 255;
        var groupOpacity = opacityOnTheMember ? (byte) 255 : opacity;

        var flat = NewTarget(setup);
        NewCompositor(setup).Composite(
            [new SceneLayer(setup.Source, setup.SourceParameters.WithOpacity(opacity))], flat.AsView());

        var grouped = NewTarget(setup);
        using var surface = Scene.Create(setup.GroupRect.Columns, setup.GroupRect.Rows);
        surface.CompositeInto(SceneCompositor.ForIntermediate(),
                              [new SceneLayer(setup.Source, Rebase(setup.SourceParameters, setup.GroupRect).WithOpacity(memberOpacity))]);

        // The group layer carries the member's blend mode. It has to: for a single source the intermediate
        // runs no mode arithmetic at all (every CompositeOver short-circuits on the transparent base), so
        // the mode has to ride the composite that actually meets the backdrop.
        NewCompositor(setup).Composite(
            [
                new SceneLayer(surface, new CompositeParameters(setup.GroupRect.Column, setup.GroupRect.Row,
                                                                groupOpacity, mode: setup.SourceParameters.Mode))
            ],
            grouped.AsView());

        AssertBuffersEqual(flat, grouped, $"{setup.Description}, opacity {opacity}" +
                                          (opacityOnTheMember ? " (carried by the member)" : ""));
    }

    private static CompositeParameters Rebase(in CompositeParameters parameters, in Rect groupRect)
    {
        var clip = parameters.Clip is not { } c
                       ? (Rect?) null
                       : TranslateClip(c, groupRect.Column, groupRect.Row);

        return new CompositeParameters(parameters.OffsetColumn - groupRect.Column,
                                       parameters.OffsetRow - groupRect.Row,
                                       parameters.Opacity, clip, parameters.Mode);
    }

    // Rect coordinates are unsigned, so a clip that starts left of the group origin clamps. Containment
    // makes that unreachable in the shipping code; clamping the END alongside the origin keeps a clamped
    // clip from silently growing if it ever is.
    private static Rect TranslateClip(in Rect clip, int columnDelta, int rowDelta)
    {
        int column = Math.Max(0, clip.Column - columnDelta);
        int row = Math.Max(0, clip.Row - rowDelta);
        int columnEnd = Math.Max(column, clip.ColumnEnd - columnDelta);
        int rowEnd = Math.Max(row, clip.RowEnd - rowDelta);
        return new Rect(column, row, columnEnd - column, rowEnd - row);
    }

    private static SceneCompositor NewCompositor(Setup setup) =>
        setup.Backdrop is { } backdrop ? new SceneCompositor(backdrop) : new SceneCompositor(setup.BaseStyle);

    // The compositor treats its target as a RETAINED buffer it maintains incrementally, and each path
    // touches a different region on its first frame (the flat path only the member's footprint, the group
    // path the whole group surface). Starting both from the base is the only apples-to-apples comparison;
    // anything else would be comparing cells the compositor has never owned.
    private static CellBuffer NewTarget(Setup setup)
    {
        if (setup.Backdrop is { } backdrop) return TargetOver(backdrop);

        var buffer = new CellBuffer(setup.Columns, setup.Rows);
        buffer.Fill(new Cell(null, CellKind.Single, setup.BaseStyle));
        return buffer;
    }

    // A target pre-loaded with the backdrop, cell by cell — the pair-copy pattern the buffer's indexer
    // supports, so wide pairs survive the copy intact.
    private static CellBuffer TargetOver(CellBuffer backdrop)
    {
        var buffer = new CellBuffer(backdrop.Columns, backdrop.Rows);

        for (int row = 0; row < backdrop.Rows; row++)
        for (int column = 0; column < backdrop.Columns; column++)
            buffer[column, row] = backdrop[column, row];

        return buffer;
    }

    private static CellBuffer Composite(Style baseStyle, int columns, int rows, ReadOnlySpan<SceneLayer> layers)
    {
        var buffer = new CellBuffer(columns, rows);
        buffer.Fill(new Cell(null, CellKind.Single, baseStyle));
        new SceneCompositor(baseStyle).Composite(layers, buffer.AsView());
        return buffer;
    }

    private static void AssertBuffersEqual(CellBuffer expected, CellBuffer actual, string context)
    {
        for (int row = 0; row < expected.Rows; row++)
        for (int column = 0; column < expected.Columns; column++)
        {
            if (expected[column, row] == actual[column, row]) continue;

            Assert.Fail($"""
                         {context}
                           at ({column}, {row})
                           flat    = {Describe(expected[column, row])}
                           grouped = {Describe(actual[column, row])}
                         """);
        }
    }

    // Structural equality is non-negotiable — only color channels are allowed to move, and only by
    // rounding. Returns (worst RGB channel delta, worst alpha delta).
    private static (int Channel, int Alpha) Deviation(in Cell expected, in Cell actual, string context)
    {
        Assert.Equal(expected.Grapheme, actual.Grapheme);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Style.Attributes, actual.Style.Attributes);
        Assert.Equal(expected.Style.UnderlineStyle, actual.Style.UnderlineStyle);
        Assert.Equal(expected.Style.Hyperlink, actual.Style.Hyperlink);

        int channel = 0, alpha = 0;
        Compare(expected.Style.Foreground, actual.Style.Foreground);
        Compare(expected.Style.Background, actual.Style.Background);
        Compare(expected.Style.UnderlineColor, actual.Style.UnderlineColor);
        return (channel, alpha);

        void Compare(Color a, Color b)
        {
            Assert.True(a.Kind == b.Kind, $"{context}: color kind drifted, {a} vs {b}");
            if (a.Kind != ColorKind.Rgb) return;

            channel = Math.Max(channel, Math.Abs(a.Red - b.Red));
            channel = Math.Max(channel, Math.Abs(a.Green - b.Green));
            channel = Math.Max(channel, Math.Abs(a.Blue - b.Blue));
            alpha = Math.Max(alpha, Math.Abs(a.Alpha - b.Alpha));
        }
    }

    private static string Describe(in Cell cell) =>
        $"{{glyph={cell.Grapheme ?? "-"}, {cell.Kind}, fg={cell.Style.Foreground}, bg={cell.Style.Background}, " +
        $"attrs={cell.Style.Attributes}, underline={cell.Style.UnderlineStyle}/{cell.Style.UnderlineColor}}}";

    // ---- Generators ----

    private static Setup RandomSetup(Random random, int index, Coverage coverage)
    {
        int columns = random.Next(6, 12);
        int rows = random.Next(2, 5);

        var source = Scene.Create(random.Next(1, columns + 3), random.Next(1, rows + 3));
        PaintRandomContent(source, random, coverage);

        // No carve-out for a clip that cuts a wide glyph in half: the degrade it triggers now carries replace
        // semantics through the surface, so the sweeps assert bit-identity over cut pairs too. Deleting that
        // carve-out is the strongest single statement the marker fix makes.
        var parameters = new CompositeParameters(random.Next(-2, columns),
                                                 random.Next(-1, rows),
                                                 opacity: 255,
                                                 clip: random.Next(3) == 0 ? RandomClip(random, columns, rows) : null,
                                                 mode: Modes[random.Next(Modes.Length)]);

        if (parameters.Clip is not null) coverage.Clipped++;
        if (parameters.Mode is not null) coverage.NonDefaultBlendMode++;

        // The group surface must CONTAIN the member's target footprint — the containment invariant the
        // clip machinery guarantees upstream. Pick a rect that honours it, sometimes tight, sometimes the
        // whole target, so the offset/clip rebasing is exercised rather than assumed away.
        var footprint = Footprint(source.Columns, source.Rows, parameters, columns, rows);
        var groupRect = footprint.IsEmpty
                            ? new Rect(0, 0, columns, rows)
                            : Between(random, footprint, columns, rows);

        if (groupRect.Columns < columns || groupRect.Rows < rows) coverage.SmallerThanTargetSurface++;
        if (footprint.Columns < columns || footprint.Rows < rows) coverage.PartialCoverage++;

        var backdrop = random.Next(10) < 4 ? RandomBackdrop(random, columns, rows) : null;
        if (backdrop is not null) coverage.BackdropBase++;

        return new Setup
               {
                   Description = $"case {index} (seed {Seed})",
                   Source = source,
                   SourceParameters = parameters,
                   Columns = columns,
                   Rows = rows,
                   GroupRect = groupRect,
                   BaseStyle = backdrop is null ? RandomStyle(random, translucent: true) : default,
                   Backdrop = backdrop
               };
    }

    private static Rect Between(Random random, in Rect inner, int columns, int rows)
    {
        int column = random.Next(0, inner.Column + 1);
        int row = random.Next(0, inner.Row + 1);
        int columnEnd = random.Next(inner.ColumnEnd, columns + 1);
        int rowEnd = random.Next(inner.RowEnd, rows + 1);
        return new Rect(column, row, columnEnd - column, rowEnd - row);
    }

    // Mirrors SceneCompositor.TryFootprint — the region of the target a layer can write.
    private static Rect Footprint(int sceneColumns, int sceneRows, in CompositeParameters p, int targetColumns, int targetRows)
    {
        int columnStart = Math.Max(0, p.OffsetColumn);
        int rowStart = Math.Max(0, p.OffsetRow);
        int columnEnd = Math.Min(targetColumns, p.OffsetColumn + sceneColumns);
        int rowEnd = Math.Min(targetRows, p.OffsetRow + sceneRows);

        if (p.Clip is { } clip)
        {
            columnStart = Math.Max(columnStart, clip.Column);
            rowStart = Math.Max(rowStart, clip.Row);
            columnEnd = Math.Min(columnEnd, clip.ColumnEnd);
            rowEnd = Math.Min(rowEnd, clip.RowEnd);
        }

        return columnStart >= columnEnd || rowStart >= rowEnd
                   ? Rect.Empty
                   : new Rect(columnStart, rowStart, columnEnd - columnStart, rowEnd - rowStart);
    }

    private static Rect RandomClip(Random random, int columns, int rows)
    {
        int column = random.Next(0, columns);
        int row = random.Next(0, rows);
        return new Rect(column, row, random.Next(1, columns - column + 1), random.Next(1, rows - row + 1));
    }

    private static CellBuffer RandomBackdrop(Random random, int columns, int rows)
    {
        var backdrop = new CellBuffer(columns, rows);
        backdrop.Fill(new Cell(null, CellKind.Single, RandomStyle(random, translucent: false)));

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            // Emoji and CJK in the BASE are what make the stomp and the pair-partner repair reachable at
            // all for a single source layer — the destination is the base and nothing else.
            var grapheme = random.Next(4) switch
            {
                0 => "🙂",
                1 => "漢",
                2 => "M",
                _ => null
            };

            column += backdrop.Set(column, row, grapheme, RandomStyle(random, translucent: false)) - 1;
        }

        return backdrop;
    }

    private static void PaintRandomContent(Scene scene, Random random, Coverage coverage,
                                           bool opaqueBackgrounds = false, bool narrowGlyphsOnly = false)
    {
        scene.Draw(ctx =>
        {
            for (int row = 0; row < scene.Rows; row++)
            for (int column = 0; column < scene.Columns; column++)
            {
                if (random.Next(5) == 0)
                {
                    coverage.UnpaintedCells++;   // leave the transparent clear showing
                    continue;
                }

                var grapheme = Graphemes[random.Next(narrowGlyphsOnly ? 5 : Graphemes.Length)];
                var style = RandomStyle(random, translucent: true);

                // Foregrounds stay translucent even where backgrounds are forced opaque: a translucent
                // glyph over an opaque panel is exactly what the resolved-destination-foreground rule
                // exists for, and it is the shape of every real translucent dialog.
                if (opaqueBackgrounds && !style.Background.IsOpaque)
                    style = style.WithBackground(RandomRgb(random, 255));

                ctx.Set(column, row, grapheme, style);
                column += grapheme is not null && GraphemeWidth.ClusterWidth(grapheme.AsSpan()) == 2 ? 1 : 0;
            }
        });

        coverage.Observe(scene);
    }

    // Background-only cells with translucent RGB backgrounds: the population that isolates the
    // over-associativity rounding from every other divergence.
    private static void PaintTranslucentBackgrounds(Scene scene, Random random) =>
        scene.Draw(ctx =>
        {
            for (int row = 0; row < scene.Rows; row++)
            for (int column = 0; column < scene.Columns; column++)
            {
                if (random.Next(5) == 0) continue;
                ctx.Set(column, row, null, Style.Default.WithBackground(RandomRgb(random, (byte) random.Next(1, 255))));
            }
        });

    private static Style OpaqueRgbStyle(Random random) =>
        new(RandomRgb(random, 255), RandomRgb(random, 255), TextAttributes.None, UnderlineStyle.Single,
            RandomRgb(random, 255));

    private static Color RandomRgb(Random random, byte alpha) =>
        Color.FromRgba((byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256), alpha);

    private static Style RandomStyle(Random random, bool translucent) =>
        new(RandomColor(random, translucent),
            RandomColor(random, translucent),
            (TextAttributes) random.Next(0, 1 << 4),
            (UnderlineStyle) random.Next(0, 5),
            RandomColor(random, translucent),
            random.Next(8) == 0 ? new Hyperlink("https://example.invalid/") : Hyperlink.None);

    private static Color RandomColor(Random random, bool translucent) =>
        (translucent ? random.Next(6) : random.Next(2, 6)) switch
        {
            0 => Color.Transparent,
            1 => Color.FromRgba((byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256),
                                (byte) random.Next(1, 255)),
            2 => Color.Default,
            3 => Color.FromPalette((byte) random.Next(16)),
            _ => Color.FromRgb((byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256))
        };

    // ---- Coverage ----

    private sealed class Coverage
    {
        public int Glyphs;
        public int WidePairs;
        public int Emoji;
        public int BackgroundOnly;
        public int UnpaintedCells;
        public int TranslucentBackgrounds;
        public int OpaqueTruecolorBackgrounds;
        public int PaletteColors;
        public int DefaultColors;
        public int TransparentBackgrounds;
        public int PartialCoverage;
        public int Clipped;
        public int BackdropBase;
        public int NonDefaultBlendMode;
        public int SmallerThanTargetSurface;

        public void Observe(Scene scene)
        {
            for (int row = 0; row < scene.Rows; row++)
            for (int column = 0; column < scene.Columns; column++)
            {
                var cell = scene.GetCell(column, row);
                if (cell.Kind == CellKind.WideContinuation) continue;
                if (cell.Kind == CellKind.WideLeft) WidePairs++;

                if (cell.Grapheme is { Length: > 0 } glyph)
                {
                    Glyphs++;
                    if (GraphemeWidth.IsEmojiPresentation(glyph)) Emoji++;
                }
                else if (!cell.Style.Background.IsTransparent)
                {
                    BackgroundOnly++;
                }

                var background = cell.Style.Background;
                if (background.IsTransparent) TransparentBackgrounds++;
                else if (background.Kind == ColorKind.Default) DefaultColors++;
                else if (background.Kind == ColorKind.Palette) PaletteColors++;
                else if (background.IsOpaque) OpaqueTruecolorBackgrounds++;
                else TranslucentBackgrounds++;

                if (cell.Style.Foreground.Kind == ColorKind.Palette) PaletteColors++;
                if (cell.Style.Foreground.Kind == ColorKind.Default) DefaultColors++;
            }
        }

        public void AssertMatrixWasExercised()
        {
            Assert.True(Glyphs >= 200, $"glyph-bearing cells: {Glyphs}");
            Assert.True(WidePairs >= 50, $"wide glyph pairs: {WidePairs}");
            Assert.True(Emoji >= 20, $"emoji-presentation glyphs: {Emoji}");
            Assert.True(BackgroundOnly >= 100, $"background-only cells: {BackgroundOnly}");
            Assert.True(UnpaintedCells >= 100, $"unpainted cells: {UnpaintedCells}");
            Assert.True(TranslucentBackgrounds >= 50, $"translucent backgrounds: {TranslucentBackgrounds}");
            Assert.True(OpaqueTruecolorBackgrounds >= 50, $"opaque truecolor backgrounds: {OpaqueTruecolorBackgrounds}");
            Assert.True(PaletteColors >= 50, $"palette colors: {PaletteColors}");
            Assert.True(DefaultColors >= 50, $"default colors: {DefaultColors}");
            Assert.True(TransparentBackgrounds >= 50, $"transparent backgrounds: {TransparentBackgrounds}");
            Assert.True(PartialCoverage >= 100, $"setups where the source only partly covers the target: {PartialCoverage}");
            Assert.True(Clipped >= 20, $"clipped setups: {Clipped}");
            Assert.True(BackdropBase >= 30, $"setups over a stored backdrop: {BackdropBase}");
            Assert.True(NonDefaultBlendMode >= 60, $"setups with a non-default blend mode: {NonDefaultBlendMode}");
            Assert.True(SmallerThanTargetSurface >= 60, $"setups whose group surface is smaller than the target: {SmallerThanTargetSurface}");
        }
    }
}
