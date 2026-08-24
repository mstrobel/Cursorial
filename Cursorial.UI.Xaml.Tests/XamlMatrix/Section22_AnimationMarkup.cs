using Cursorial.Animation;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Animation markup (the W1 XAML-friendliness sweep fixes — design doc §9.10's Fork C wiring): Storyboard /
/// BeginStoryboard / StopStoryboard content properties (XA1–XA4), the Easing converter over
/// <c>Easings.TryParse</c> (catalog + <c>cubic-bezier</c>, XA5–XA7), the RepeatBehavior converter over
/// <c>RepeatBehavior.TryParse</c> (XA8–XA9), Optional&lt;T&gt; inner conversion THROUGH the ladder (the
/// <c>Optional&lt;Color&gt;</c> fix — the type-level BCL converter's TypeDescriptor lookup can't see
/// ladder-only inner grammars, XA10–XA11), the unprefixed <c>Cursorial.Animation</c> xmlns seed
/// (<c>{x:Static Easings.…}</c>, XA12), the <c>Transition.Transitions</c> attached-collection get-or-create
/// fill (XA13), and the composed end-to-end document (XA14). Error rows assert CUR2401 with 1-based positions.
/// </summary>
public sealed class Section22_AnimationMarkup : LoaderTestBase
{
    // ── Content properties (XA1–XA4) ─────────────────────────────────────────────────────────────

    [Fact] // XA1: <Storyboard> takes tracks as implicit content ([ContentProperty("Children")])
    public void XA1_Storyboard_ImplicitContent_SingleTrack()
    {
        var sb = Load<Storyboard>(
            "<Storyboard><DoubleTrack TargetPath=\"Opacity\" To=\"1\" Duration=\"0:0:0.2\"/></Storyboard>");

        var track = Assert.IsType<DoubleTrack>(Assert.Single(sb.Children));
        Assert.True(track.To.HasValue);
        Assert.Equal(1.0, track.To.Value);
        Assert.Equal(TimeSpan.FromSeconds(0.2), track.Duration);
    }

    [Fact] // XA2: multiple implicit tracks fill Children in document order
    public void XA2_Storyboard_ImplicitContent_MultipleTracks()
    {
        var sb = Load<Storyboard>(
            "<Storyboard>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\"/>" +
              "<Int32Track TargetPath=\"Row\" To=\"3\"/>" +
            "</Storyboard>");

        Assert.Equal(2, sb.Children.Count);
        Assert.IsType<DoubleTrack>(sb.Children[0]);
        Assert.IsType<Int32Track>(sb.Children[1]);
    }

    [Fact] // XA3: <BeginStoryboard> takes its storyboard as implicit content (the WPF idiom)
    public void XA3_BeginStoryboard_ImplicitContent()
    {
        var style = Load<Style>(
            "<Style TargetType=\"Border\">" +
              "<Style.Enter>" +
                "<BeginStoryboard><Storyboard><DoubleTrack TargetPath=\"Opacity\" To=\"1\"/></Storyboard></BeginStoryboard>" +
              "</Style.Enter>" +
            "</Style>");

        var begin = Assert.IsType<BeginStoryboard>(Assert.Single(style.Enter));
        Assert.NotNull(begin.Storyboard);
        Assert.Single(begin.Storyboard!.Children);
    }

    [Fact] // XA4: <StopStoryboard> takes its storyboard reference as implicit content
    public void XA4_StopStoryboard_ImplicitContent()
    {
        var style = Load<Style>(
            "<Style TargetType=\"Border\">" +
              "<Style.Exit><StopStoryboard><Storyboard/></StopStoryboard></Style.Exit>" +
            "</Style>");

        var stop = Assert.IsType<StopStoryboard>(Assert.Single(style.Exit));
        Assert.NotNull(stop.Storyboard);
    }

    // ── Easing (XA5–XA7) ─────────────────────────────────────────────────────────────────────────

    [Fact] // XA5: a catalog name converts via Easings.TryParse — case-insensitive, singleton-identical
    public void XA5_Easing_CatalogName()
    {
        var sb = Load<Storyboard>(
            "<Storyboard>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\" Easing=\"QuadInOut\"/>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\" Easing=\"quadinout\"/>" +
            "</Storyboard>");

        Assert.Same(Easings.QuadInOut, ((DoubleTrack)sb.Children[0]).Easing); // the catalog delegate itself
        Assert.Same(Easings.QuadInOut, ((DoubleTrack)sb.Children[1]).Easing); // …resolved case-insensitively
    }

    [Fact] // XA6: the cubic-bezier(x1,y1,x2,y2) functional form builds a real easing
    public void XA6_Easing_CubicBezier()
    {
        var sb = Load<Storyboard>(
            "<Storyboard><DoubleTrack TargetPath=\"Opacity\" To=\"1\" Easing=\"cubic-bezier(0.4,0,0.2,1)\"/></Storyboard>");

        var easing = ((DoubleTrack)sb.Children[0]).Easing;
        Assert.NotNull(easing);
        Assert.Equal(0.0, easing!(0.0), precision: 9); // bezier endpoints are exact
        Assert.Equal(1.0, easing(1.0), precision: 9);
    }

    [Fact] // XA7: an unknown easing is a positioned CUR2401 naming the grammar — never a raw-string crash
    public void XA7_Easing_Unknown_IsPositionedConversionError()
    {
        var ex = ThrowsLoad("CUR2401", () => Load(
            "<Storyboard><DoubleTrack TargetPath=\"Opacity\" Easing=\"bogus\"/></Storyboard>"));

        Assert.Contains("cubic-bezier", ex.Message); // the diagnostic teaches the grammar
    }

    // ── RepeatBehavior (XA8–XA9) ─────────────────────────────────────────────────────────────────

    [Fact] // XA8: "Forever", "3x", and a bare count all convert via RepeatBehavior.TryParse
    public void XA8_Repeat_ForeverCountedAndBare()
    {
        var sb = Load<Storyboard>(
            "<Storyboard>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\" Repeat=\"Forever\"/>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\" Repeat=\"3x\"/>" +
              "<DoubleTrack TargetPath=\"Opacity\" To=\"1\" Repeat=\"2\"/>" +
            "</Storyboard>");

        Assert.True(((DoubleTrack)sb.Children[0]).Repeat.IsForever);
        Assert.Equal(3, ((DoubleTrack)sb.Children[1]).Repeat.IterationCount);
        Assert.Equal(2, ((DoubleTrack)sb.Children[2]).Repeat.IterationCount);
    }

    [Fact] // XA9: an invalid repeat is a positioned CUR2401
    public void XA9_Repeat_Invalid_IsPositionedConversionError()
        => ThrowsLoad("CUR2401", () => Load(
            "<Storyboard><DoubleTrack TargetPath=\"Opacity\" Repeat=\"sometimes\"/></Storyboard>"));

    // ── Optional<T> through the ladder (XA10–XA11) ───────────────────────────────────────────────

    [Fact] // XA10: Optional<Color> converts the INNER type through the ladder (#RRGGBBAA incl. alpha) —
    // the type-level BCL OptionalConverter's TypeDescriptor lookup cannot see the ladder's Color grammar
    public void XA10_OptionalColor_InnerConvertsThroughLadder()
    {
        var sb = Load<Storyboard>(
            "<Storyboard><ColorTrack TargetPath=\"Background\" From=\"#FF000080\" To=\"#00FF00\"/></Storyboard>");

        var track = (ColorTrack)sb.Children[0];
        Assert.True(track.From.HasValue);
        Assert.Equal((255, 0, 0, 128), (track.From.Value.Red, track.From.Value.Green, track.From.Value.Blue, track.From.Value.Alpha));
        Assert.True(track.To.HasValue);
        Assert.Equal((0, 255, 0, 255), (track.To.Value.Red, track.To.Value.Green, track.To.Value.Blue, track.To.Value.Alpha));
    }

    [Fact] // XA11: Optional<double>/<int> keep working through the new ladder rung (regression over the BCL path)
    public void XA11_OptionalDouble_StillConverts()
    {
        var sb = Load<Storyboard>(
            "<Storyboard>" +
              "<DoubleTrack TargetPath=\"Opacity\" From=\"0.2\" To=\"1.0\"/>" +
              "<Int32Track TargetPath=\"Row\" From=\"1\" To=\"5\"/>" +
            "</Storyboard>");

        var doubles = (DoubleTrack)sb.Children[0];
        Assert.Equal(0.2, doubles.From.Value);
        Assert.Equal(1.0, doubles.To.Value);
        var ints = (Int32Track)sb.Children[1];
        Assert.Equal(1, ints.From.Value);
        Assert.Equal(5, ints.To.Value);
    }

    // ── The Cursorial.Animation xmlns seed (XA12) ────────────────────────────────────────────────

    [Fact] // XA12: Easings resolves UNPREFIXED in the default xmlns (the Cursorial.Animation seed) — the
    // {x:Static} escape hatch works without a clr-namespace declaration
    public void XA12_EasingsResolvesUnprefixed_ViaXStatic()
    {
        var sb = Load<Storyboard>(
            "<Storyboard><DoubleTrack TargetPath=\"Opacity\" To=\"1\" Easing=\"{x:Static Easings.CubicOut}\"/></Storyboard>");

        Assert.Same(Easings.CubicOut, ((DoubleTrack)sb.Children[0]).Easing);
    }

    // ── Transition.Transitions get-or-create fill (XA13) ─────────────────────────────────────────

    [Fact] // XA13: the <Transition.Transitions> property element is loadable (the CUR2105 no-getter-to-fill
    // rejection is gone — the loader probes the GetOrCreate{Name} fill hook first, so the PUBLIC
    // GetTransitions stays a pure style-respecting read; the child-bearing path is exercised by the
    // Interaction.Behaviors suite). An EMPTY property element is dropped by the parser (no member record ⇒
    // no fill — nothing to attach), and transition CHILDREN stay unexpressible until the W2 API reshape
    // makes the leaves constructible; the child-bearing fill row lands there. The accessor split is pinned
    // in AnimationMatrix Section17 (N155–N159).
    public void XA13_TransitionsAttachedCollection_LoadsAndFillHookCreates()
    {
        var border = Load<Border>(
            "<Border><Transition.Transitions></Transition.Transitions></Border>");

        Assert.Null(border.GetValue(Transition.TransitionsProperty)); // empty element dropped — no phantom fill
        Assert.Null(Transition.GetTransitions(border));               // the pure read never creates

        var created = Transition.GetOrCreateTransitions(border);      // the fill hook the loader probes
        Assert.Empty(created);
        Assert.Same(created, border.GetValue(Transition.TransitionsProperty)); // created AND attached
    }

    // ── Composed end-to-end (XA14) ───────────────────────────────────────────────────────────────

    [Fact] // XA14: the full W1 surface composes in one document — a style-edge storyboard authored the WPF
    // way (implicit content, easing + repeat + Optional From/To attributes, a StaticResource reference)
    public void XA14_ComposedStyleEdgeStoryboard()
    {
        var panel = Load<StackPanel>(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<Storyboard x:Key=\"pulse\">" +
                  "<DoubleTrack TargetPath=\"Opacity\" From=\"0.2\" To=\"1\" Duration=\"0:0:0.3\" Easing=\"QuadInOut\" Repeat=\"Forever\"/>" +
                "</Storyboard>" +
                "<Style x:Key=\"pulsing\" TargetType=\"Border\">" +
                  "<Style.Enter><BeginStoryboard Storyboard=\"{StaticResource pulse}\"/></Style.Enter>" +
                  "<Style.Exit><StopStoryboard Storyboard=\"{StaticResource pulse}\"/></Style.Exit>" +
                "</Style>" +
              "</StackPanel.Resources>" +
            "</StackPanel>");

        var storyboard = Assert.IsType<Storyboard>(panel.Resources!["pulse"]);
        var track = Assert.IsType<DoubleTrack>(Assert.Single(storyboard.Children));
        Assert.Equal(0.2, track.From.Value);
        Assert.Same(Easings.QuadInOut, track.Easing);
        Assert.True(track.Repeat.IsForever);

        var style = Assert.IsType<Style>(panel.Resources!["pulsing"]);
        Assert.Same(storyboard, Assert.IsType<BeginStoryboard>(Assert.Single(style.Enter)).Storyboard);
        Assert.Same(storyboard, Assert.IsType<StopStoryboard>(Assert.Single(style.Exit)).Storyboard);
    }

    // ── Audit rows (XA15–XA16) ───────────────────────────────────────────────────────────────────

    [Fact] // XA15 (audit): the Storyboard attribute + implicit content on one BeginStoryboard is a
    // positioned CUR1101 — pre-audit the implicit-content lane bypassed duplicate detection and the
    // StaticResource attribute was silently last-wins-overwritten by the inline storyboard
    public void XA15_ContentPropertyPlusAttribute_IsDuplicateAssignment()
        => ThrowsLoad("CUR1101", () => Load(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<Storyboard x:Key=\"s\"/>" +
                "<Style x:Key=\"st\" TargetType=\"Border\">" +
                  "<Style.Enter>" +
                    "<BeginStoryboard Storyboard=\"{StaticResource s}\"><Storyboard/></BeginStoryboard>" +
                  "</Style.Enter>" +
                "</Style>" +
              "</StackPanel.Resources>" +
            "</StackPanel>"));

    private readonly record struct RegisterProbeInner(int Value);

    private sealed class RegisterProbeConverter(int result) : ITypeConverter
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext ctx) => new RegisterProbeInner(result);
    }

    [Fact] // XA16 (audit): the Optional rung re-resolves its INNER converter per conversion — a later
    // Register() override for the inner type wins exactly as it does for a bare inner-typed member (a
    // creation-time snapshot would freeze the first-resolved converter into the cached rung forever)
    public void XA16_OptionalRung_HonorsLateInnerRegister()
    {
        XamlConverters.Register(typeof(RegisterProbeInner), new RegisterProbeConverter(1));
        var rung = XamlConverters.For(typeof(Cursorial.UI.Optional<RegisterProbeInner>));
        Assert.NotNull(rung);

        var ctx = default(XamlValueContext);
        var first = (Cursorial.UI.Optional<RegisterProbeInner>)rung!.ConvertFromString("x", in ctx);
        Assert.Equal(1, first.Value.Value);

        XamlConverters.Register(typeof(RegisterProbeInner), new RegisterProbeConverter(2)); // late override
        var second = (Cursorial.UI.Optional<RegisterProbeInner>)rung.ConvertFromString("x", in ctx);
        Assert.Equal(2, second.Value.Value); // the rung consulted the LIVE ladder, not a snapshot
    }
}
