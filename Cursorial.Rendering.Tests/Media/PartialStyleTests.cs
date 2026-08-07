using System.Reflection;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Media;

/// <summary>
/// The laws <see cref="PartialStyle"/> claims. Each is cheap to state and easy to get subtly wrong,
/// which is exactly the combination worth pinning.
/// </summary>
public class PartialStyleTests
{
    /// <summary>A base with every channel non-default, so "leaves it alone" is distinguishable from
    /// "sets it to the default" — the confusion this type exists to retire.</summary>
    private static CellStyle Busy => new(
        Foreground:     Color.FromRgb(10, 20, 30),
        Background:     Color.FromRgb(40, 50, 60),
        Attributes:     TextAttributes.Bold | TextAttributes.Inverse | TextAttributes.Strikethrough,
        UnderlineStyle: UnderlineStyle.Curly,
        UnderlineColor: Color.FromRgb(70, 80, 90));

    private static readonly TextAttributes[] BooleanAxes =
    [
        TextAttributes.Strikethrough, TextAttributes.Overline,
        TextAttributes.Inverse, TextAttributes.Blink, TextAttributes.Hidden,
    ];

    // ---- 1. identity ----

    [Fact]
    public void Default_IsTheIdentity()
    {
        Assert.True(PartialStyle.Default.IsIdentity);
        Assert.Equal(Busy, PartialStyle.Default.ApplyTo(Busy));
        Assert.Equal(default(CellStyle), PartialStyle.Default.ApplyTo(default));
    }

    // ---- 2. composition is a law, not a convenience ----

    public static TheoryData<PartialStyle, PartialStyle> Pairs()
    {
        PartialStyle[] deltas =
        [
            PartialStyle.Default,
            PartialStyle.WithForeground(Color.FromRgb(1, 2, 3)),
            PartialStyle.WithBackground(Color.Default),
            PartialStyle.Weighted(TextWeight.Bold),
            PartialStyle.Weighted(TextWeight.Faint),
            PartialStyle.Weighted(TextWeight.Normal),
            PartialStyle.Postured(TextStyle.Italic),
            PartialStyle.WithApplied(TextAttributes.Inverse),
            PartialStyle.WithRemoved(TextAttributes.Inverse),
            PartialStyle.WithToggled(TextAttributes.Inverse),
            PartialStyle.WithToggled(TextAttributes.Blink | TextAttributes.Overline),
            PartialStyle.WithUnderline(UnderlineStyle.Double, Color.FromRgb(9, 9, 9)),
            PartialStyle.WithUnderline(),
            PartialStyle.WithoutUnderline(),
            PartialStyle.WithHyperlink(default),
        ];

        var data = new TheoryData<PartialStyle, PartialStyle>();
        foreach (var a in deltas)
        foreach (var b in deltas)
            data.Add(a, b);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Then_IsApplicationInOrder(PartialStyle a, PartialStyle b)
    {
        // a.Then(b).ApplyTo(s) === b.ApplyTo(a.ApplyTo(s))
        Assert.Equal(b.ApplyTo(a.ApplyTo(Busy)), a.Then(b).ApplyTo(Busy));
        Assert.Equal(b.ApplyTo(a.ApplyTo(default)), a.Then(b).ApplyTo(default));
    }

    // ---- 3/4. the four attribute operations ----

    [Fact]
    public void Toggle_IsAnInvolution()
    {
        foreach (var f in BooleanAxes)
        {
            var twice = PartialStyle.WithToggled(f).Toggling(f);
            Assert.Equal(Busy.Attributes, twice.ApplyTo(Busy).Attributes);
        }
    }

    [Fact]
    public void ALaterSetOrClear_BeatsAnEarlierToggle()
    {
        foreach (var f in BooleanAxes)
        {
            Assert.True(PartialStyle.WithToggled(f).Applying(f).ApplyTo(Busy).Attributes.HasFlag(f));
            Assert.False(PartialStyle.WithToggled(f).Removing(f).ApplyTo(Busy).Attributes.HasFlag(f));

            // ...regardless of what the base had.
            Assert.True(PartialStyle.WithToggled(f).Applying(f).ApplyTo(default).Attributes.HasFlag(f));
            Assert.False(PartialStyle.WithToggled(f).Removing(f).ApplyTo(default).Attributes.HasFlag(f));
        }
    }

    [Fact]
    public void TogglesOnDifferentAxes_Accumulate()
    {
        var d = PartialStyle.WithToggled(TextAttributes.Inverse).Toggling(TextAttributes.Blink);
        var before = Busy.Attributes;
        var after = d.ApplyTo(Busy).Attributes;

        Assert.NotEqual(before.HasFlag(TextAttributes.Inverse), after.HasFlag(TextAttributes.Inverse));
        Assert.NotEqual(before.HasFlag(TextAttributes.Blink), after.HasFlag(TextAttributes.Blink));
    }

    // ---- 5. absence is not "the default value" ----

    [Fact]
    public void AnAbsentChannel_LeavesTheBaseAlone()
    {
        var onlyBackground = PartialStyle.WithBackground(Color.FromRgb(1, 1, 1));

        Assert.Equal(Busy.Foreground, onlyBackground.ApplyTo(Busy).Foreground);
        Assert.Equal(Busy.UnderlineColor, onlyBackground.ApplyTo(Busy).UnderlineColor);
    }

    [Fact]
    public void SettingAChannelToTheTerminalDefault_IsExpressible()
    {
        // The headline defect: `Color.Default` used to mean "no opinion", so this was unsayable.
        var toDefault = PartialStyle.WithBackground(Color.Default);

        Assert.Equal(Color.Default, toDefault.ApplyTo(Busy).Background);
        Assert.NotEqual(Busy.Background, toDefault.ApplyTo(Busy).Background);
    }

    // ---- 6. the decomposed axes project losslessly ----

    [Theory]
    [InlineData(TextWeight.Normal)]
    [InlineData(TextWeight.Faint)]
    [InlineData(TextWeight.Bold)]
    public void Weight_RoundTrips(TextWeight w)
    {
        Assert.Equal(w, PartialStyle.Weighted(w).Weight);
        Assert.Null(PartialStyle.Default.Weight);
        Assert.Null(PartialStyle.WithForeground(Color.Default).Weight);
    }

    [Fact]
    public void Weighted_ForcesTheOtherHalfOff_BecauseSgr22ResetsBoth()
    {
        // Busy is Bold. Asking for Faint must not leave both set.
        var faint = PartialStyle.Weighted(TextWeight.Faint).ApplyTo(Busy).Attributes;

        Assert.True(faint.HasFlag(TextAttributes.Faint));
        Assert.False(faint.HasFlag(TextAttributes.Bold));
    }

    [Theory]
    [InlineData(TextStyle.Normal)]
    [InlineData(TextStyle.Italic)]
    public void Posture_RoundTrips(TextStyle p)
    {
        Assert.Equal(p, PartialStyle.Postured(p).Posture);
        Assert.Null(PartialStyle.Default.Posture);
    }

    // ---- 7. IsIdentity is inert under COMPOSITION, not just application ----

    [Fact]
    public void AModeOnlyDelta_IsNotIdentity_BecauseThenPropagatesIt()
    {
        var dim = PartialStyle.WithBlending(BlendingModes.Multiply);

        Assert.False(dim.IsIdentity);                       // ...even though:
        Assert.Equal(Busy, dim.ApplyTo(Busy));              // it is inert applied directly,
        Assert.NotEqual(                                    // but NOT inert composed.
            PartialStyle.WithForeground(Color.FromRgb(200, 100, 50)).ApplyTo(Busy),
            dim.Then(PartialStyle.WithForeground(Color.FromRgb(200, 100, 50))).ApplyTo(Busy));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void AnIdentityDelta_ComposesAway(PartialStyle a, PartialStyle b)
    {
        if (!a.IsIdentity) return;
        Assert.Equal(b.ApplyTo(Busy), a.Then(b).ApplyTo(Busy));
    }

    // ---- 8. the axes reject the flag word ----

    [Theory]
    [InlineData(TextAttributes.Bold)]
    [InlineData(TextAttributes.Faint)]
    [InlineData(TextAttributes.Italic)]
    [InlineData(TextAttributes.Underline)]
    public void FlagFactories_RejectAxesThatHaveTheirOwnApi(TextAttributes f)
    {
        // Without this the decomposed vocabulary decays back into flags one call site at a time —
        // and `Bold | Faint`, a state the wire cannot represent, becomes writable again.
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialStyle.WithApplied(f));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialStyle.WithRemoved(f));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialStyle.WithToggled(f));
    }

    /// <summary>
    /// The guard is stated over the WHOLE public flag-word surface, by reflection, rather than over the
    /// three factories a reader happens to remember. A factory that took a <see cref="TextAttributes"/>
    /// and skipped <c>Require</c> would hand back exactly the states the guard exists to forbid —
    /// including <c>Bold | Faint</c>, which no terminal can render (the two share the SGR 22 reset, so
    /// the emitter's choice of byte depends on the predecessor cell) and which
    /// <see cref="PartialStyle.Weight"/> silently reports as plain <c>Bold</c>.
    /// </summary>
    /// <remarks>
    /// Reflection, not a hand-written list, because the failure mode is a member ADDED later. And it
    /// covers <see cref="StyleDeltaTemplate"/> too: the template's fluent methods delegate to
    /// <see cref="PartialStyle"/>'s, so a hole in either is a hole in both.
    /// </remarks>
    [Fact]
    public void EveryPublicFlagWordEntryPoint_RejectsTheAxisOwningFlags()
    {
        TextAttributes[] forbidden =
        [
            TextAttributes.Bold, TextAttributes.Faint, TextAttributes.Italic, TextAttributes.Underline,
            TextAttributes.Bold | TextAttributes.Faint,
            TextAttributes.Inverse | TextAttributes.Bold,   // a boolean smuggling an axis alongside it
        ];

        var accepted = new List<string>();
        var probed = new HashSet<string>();

        foreach (var type in new[] { typeof(PartialStyle), typeof(StyleDeltaTemplate) })
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            var parameters = method.GetParameters();
            if (parameters is not [{ ParameterType: var p }] || p != typeof(TextAttributes))
                continue;

            var instance = method.IsStatic ? null : Activator.CreateInstance(type);
            probed.Add($"{type.Name}.{method.Name}");

            foreach (var flags in forbidden)
            {
                try
                {
                    method.Invoke(instance, [flags]);
                    accepted.Add($"{type.Name}.{method.Name}({flags})");
                }
                catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException)
                {
                    // The guard fired, which is the whole point.
                }
            }
        }

        // Not vacuous: the members we KNOW take a flag word were actually reached and invoked.
        Assert.Superset(new HashSet<string>
                        {
                            "PartialStyle.WithApplied", "PartialStyle.WithRemoved", "PartialStyle.WithToggled",
                            "PartialStyle.Applying", "PartialStyle.Removing", "PartialStyle.Toggling",
                            "PartialStyle.Ignoring", "StyleDeltaTemplate.Applying", "StyleDeltaTemplate.Removing",
                            "StyleDeltaTemplate.Toggling", "StyleDeltaTemplate.Ignoring", "StyleDeltaTemplate.Keeping",
                            "StyleDeltaTemplate.Dropping"
                        },
                        probed);
        Assert.True(accepted.Count == 0,
                    $"public flag-word members accepted an axis-owning flag: {string.Join(", ", accepted)}");
    }

    /// <summary>
    /// ...and the same forbidden state is unreachable by COMPOSING legal deltas, which is the half a
    /// per-call guard cannot cover: two calls that are each individually fine must not add up to
    /// <c>Bold | Faint</c> either.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void NoCompositionOfPublicDeltas_ReachesBothWeightFlags(PartialStyle a, PartialStyle b)
    {
        const TextAttributes Both = TextAttributes.Bold | TextAttributes.Faint;

        // Bases that each carry ONE weight (and one that carries neither), so a delta that merely
        // passes the base through cannot manufacture the state on its own.
        CellStyle[] bases =
        [
            Busy,                                                                       // Bold
            Busy with { Attributes = (Busy.Attributes & ~Both) | TextAttributes.Faint }, // Faint
            default,                                                                    // neither
        ];

        foreach (var s in bases)
            Assert.NotEqual(Both, a.Then(b).ApplyTo(s).Attributes & Both);
    }

    // ---- 9. the underline shape implies the flag, structurally ----

    [Fact]
    public void AnUnderlineShape_TurnsTheFlagOn_HoweverTheDeltaWasBuilt()
    {
        var viaFactory = PartialStyle.WithUnderline(UnderlineStyle.Dotted, Color.Default);
        var viaWith    = PartialStyle.Default with { UnderlineShape = UnderlineStyle.Dotted };

        foreach (var d in new[] { viaFactory, viaWith })
        {
            var applied = d.ApplyTo(default);
            Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
            Assert.Equal(UnderlineStyle.Dotted, applied.UnderlineStyle);
        }
    }

    [Fact]
    public void WithoutUnderline_ClearsTheFlag_AndLeavesNoShapeOpinion()
    {
        var applied = PartialStyle.WithoutUnderline().ApplyTo(Busy);

        Assert.False(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Null(PartialStyle.WithoutUnderline().UnderlineShape);
    }

    /// <summary>
    /// The shapeless underline states the FLAG and nothing else: the base's shape survives it, exactly
    /// as an absent colour channel leaves the base's colour alone.
    /// </summary>
    [Fact]
    public void ABareUnderline_ForcesTheFlag_AndInheritsTheBaseShape()
    {
        var bare = PartialStyle.WithUnderline();
        Assert.Null(bare.UnderlineShape);

        var applied = bare.ApplyTo(default(CellStyle) with { UnderlineStyle = UnderlineStyle.Dotted });

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(UnderlineStyle.Dotted, applied.UnderlineStyle);
    }

    /// <summary>
    /// "No opinion on the shape" must not READ as "no shape". Composing a bare underline after one that
    /// states a shape has to leave that shape standing — the case that made a shape-or-mask predicate
    /// the wrong thing for <c>Then</c> to key on.
    /// </summary>
    [Fact]
    public void ABareUnderline_DoesNotErase_AnEarlierShape()
    {
        var composed = PartialStyle.WithUnderline(UnderlineStyle.Curly).Underlining();

        Assert.Equal(UnderlineStyle.Curly, composed.UnderlineShape);
        Assert.Equal(UnderlineStyle.Curly, composed.ApplyTo(default).UnderlineStyle);
    }

    /// <summary>
    /// The converse: a removal after a shape still removes, and leaves no remnant behind to resurrect it.
    /// </summary>
    [Fact]
    public void ARemoval_AfterAShape_Removes_AndKeepsNoRemnant()
    {
        var composed = PartialStyle.WithUnderline(UnderlineStyle.Curly).RemovingUnderline();

        Assert.Null(composed.UnderlineShape);
        Assert.False(composed.ApplyTo(default).Attributes.HasFlag(TextAttributes.Underline));
    }

    [Fact]
    public void LeavingTheUnderlineAlone_IsDistinctFromRemovingIt()
    {
        var underlined = Busy with { Attributes = Busy.Attributes | TextAttributes.Underline };

        Assert.True(PartialStyle.Default.ApplyTo(underlined).Attributes.HasFlag(TextAttributes.Underline));
        Assert.False(PartialStyle.WithoutUnderline().ApplyTo(underlined).Attributes.HasFlag(TextAttributes.Underline));
    }

    // ---- 9b. underline COLOUR is conditional; underline SHAPE is assertive ----

    [Fact]
    public void WithUnderlineColor_DoesNotForceAnUnderline()
    {
        // Deliberate asymmetry, not an oversight. "Whatever underline exists is this colour" is a
        // distinct operation from "underline it in this colour" — a theme wants the first (error
        // underlines are red) without asserting that everything is underlined. Making the colour
        // imply the flag would make that unsayable.
        var recolour = PartialStyle.WithUnderlineColor(Color.FromRgb(200, 0, 0));
        var plain = default(CellStyle);

        Assert.False(recolour.ApplyTo(plain).Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(Color.FromRgb(200, 0, 0), recolour.ApplyTo(plain).UnderlineColor);

        // ...and it does recolour one that IS there, leaving the shape alone.
        var underlined = Busy with { Attributes = Busy.Attributes | TextAttributes.Underline };
        var applied = recolour.ApplyTo(underlined);

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(UnderlineStyle.Curly, applied.UnderlineStyle);
        Assert.Equal(Color.FromRgb(200, 0, 0), applied.UnderlineColor);
    }

    [Fact]
    public void WithUnderline_DoesForceOne()
    {
        // The contrast that makes the asymmetry legible.
        var assertive = PartialStyle.WithUnderline(UnderlineStyle.Dashed, Color.FromRgb(200, 0, 0));

        Assert.True(assertive.ApplyTo(default).Attributes.HasFlag(TextAttributes.Underline));
    }

    // ---- 10. the intent triple is the interface, and it is faithful ----

    [Fact]
    public void IntentProperties_ReportWhatTheDeltaActuallyDoes()
    {
        var d = PartialStyle.WithApplied(TextAttributes.Inverse)
                            .Removing(TextAttributes.Blink)
                            .Toggling(TextAttributes.Overline);

        Assert.Equal(TextAttributes.Inverse,  d.AppliedAttributes);
        Assert.Equal(TextAttributes.Blink,    d.RemovedAttributes);
        Assert.Equal(TextAttributes.Overline, d.ToggledAttributes);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TheIntentSetsArePartitioned(PartialStyle a, PartialStyle b)
    {
        // Disjoint and exhaustive over everything the mask touches — so a reader never has to
        // reconstruct intent from the encoding, and no axis can appear in two of them at once.
        var d = a.Then(b);

        Assert.Equal(default, d.AppliedAttributes & d.RemovedAttributes);
        Assert.Equal(default, d.AppliedAttributes & d.ToggledAttributes);
        Assert.Equal(default, d.RemovedAttributes & d.ToggledAttributes);

        foreach (var f in BooleanAxes)
        {
            var touched = (d.AppliedAttributes | d.RemovedAttributes | d.ToggledAttributes).HasFlag(f);
            var moved = d.ApplyTo(Busy).Attributes.HasFlag(f) != Busy.Attributes.HasFlag(f)
                     || d.ApplyTo(default).Attributes.HasFlag(f) != default(CellStyle).Attributes.HasFlag(f);

            // An axis that moves for SOME base must appear in one of the three sets.
            if (moved) Assert.True(touched, $"{f} changed a base but is in none of the intent sets");
        }
    }

    // ---- 11. `with` is complete: there is no presence to maintain separately ----

    [Fact]
    public void APlainWithExpression_IsEquivalentToTheFactory()
    {
        var c = Color.FromRgb(3, 4, 5);

        Assert.Equal(PartialStyle.WithForeground(c), PartialStyle.Default with { Foreground = c });
        Assert.Equal(PartialStyle.WithBackground(c), PartialStyle.Default with { Background = c });

        // The trap the presence-mask design had: setting the value while leaving the channel absent.
        // With nullability there is no second thing to set, so this cannot be written wrong.
        Assert.Equal(c, (PartialStyle.Default with { Foreground = c }).ApplyTo(Busy).Foreground);
    }
}
