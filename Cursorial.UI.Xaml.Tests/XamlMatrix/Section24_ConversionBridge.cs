using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The CR7 conversion-bridge rung (W2d, design doc <c>xaml-conversion-routes.md</c>): a member type with
/// NO converter converts through a single-parameter route declared on it — implicit operator &gt;
/// explicit operator &gt; constructor &gt; static <c>Parse(string)</c> — from a ladder-convertible source.
/// Within a kind exactly one viable candidate is required (XC5 pins the loud ambiguity rule); registered
/// converters keep precedence (XC6). The rung is the LAST fallback — no existing conversion changes.
/// </summary>
// ── Fixture wrapper types (one per route kind; namespace-scope so they resolve as elements) ──────

public sealed class ImplicitWrapped
    {
    public double Value { get; private init; }
    public static implicit operator ImplicitWrapped(double value) => new() { Value = value };
    }

public sealed class ExplicitWrapped
    {
        public int Value { get; private init; }
        public static explicit operator ExplicitWrapped(int value) => new() { Value = value };
    }

public sealed class CtorWrapped(double value)
    {
        public double Value { get; } = value;
    }

public sealed class ParseWrapped
    {
        public string? Raw { get; private init; }
        public static ParseWrapped Parse(string text) => new() { Raw = "parsed:" + text };
    }

public sealed class AmbiguousWrapped
    {
        public static implicit operator AmbiguousWrapped(double value) => new();
        public static implicit operator AmbiguousWrapped(int value) => new();
    }

public readonly struct StructWrapped(double value)
{
    public double Value { get; } = value;
}

public sealed class ThrowingParseWrapped
{
    public static ThrowingParseWrapped Parse(string text) => throw new FormatException($"bad payload '{text}'");
}

public sealed class Unconvertible
{
    // No single-param ctor, no operators, no Parse — the ROUTE PROBE finds nothing.
}

public sealed class BridgeHost : Control
    {
        public ImplicitWrapped? Implicit { get; set; }
        public ExplicitWrapped? Explicit { get; set; }
        public CtorWrapped? Ctor { get; set; }
        public ParseWrapped? Parsed { get; set; }
        public AmbiguousWrapped? Ambiguous { get; set; }
        public StructWrapped? NullableCtor { get; set; }
        public ThrowingParseWrapped? Throwing { get; set; }
        public Unconvertible? Dead { get; set; }
    }

public sealed class Section24_ConversionBridge : LoaderTestBase
{
    [Fact] // XC1: an implicit operator from a ladder-convertible source bridges
    public void XC1_ImplicitOperator_Bridges()
    {
        var host = Load<BridgeHost>("<BridgeHost Implicit=\"0.5\"/>");
        Assert.Equal(0.5, host.Implicit!.Value);
    }

    [Fact] // XC2: an explicit operator bridges (implicit beats it only when both exist)
    public void XC2_ExplicitOperator_Bridges()
    {
        var host = Load<BridgeHost>("<BridgeHost Explicit=\"7\"/>");
        Assert.Equal(7, host.Explicit!.Value);
    }

    [Fact] // XC3: a single-parameter constructor bridges
    public void XC3_Constructor_Bridges()
    {
        var host = Load<BridgeHost>("<BridgeHost Ctor=\"2.5\"/>");
        Assert.Equal(2.5, host.Ctor!.Value);
    }

    [Fact] // XC4: static T Parse(string) bridges (the Avalonia sibling convention)
    public void XC4_ParseMethod_Bridges()
    {
        var host = Load<BridgeHost>("<BridgeHost Parsed=\"hello\"/>");
        Assert.Equal("parsed:hello", host.Parsed!.Raw);
    }

    [Fact] // XC5: two viable routes of ONE kind is a loud positioned error — never a silent pick. W2e
    // upgraded it from a LOAD-time CUR2401 to a PARSE-time CUR2402 (the route probe judges at parse, so
    // both lanes reject the document before any instantiation — the G4 shape).
    public void XC5_AmbiguousRoutes_IsPositionedError()
    {
        var ex = ThrowsLoad("CUR2402", () => Load(
            "<BridgeHost Ambiguous=\"1\"/>"));

        Assert.Contains("Ambiguous conversion routes", ex.Message);
        Assert.Contains("add a converter", ex.Message);
    }

    [Fact] // XC6: a registered converter keeps precedence — the bridge is the LAST rung
    public void XC6_RegisteredConverter_BeatsBridge()
    {
        XamlConverters.Register(typeof(CtorWrapped), new FixedCtorConverter());
        try
        {
            var host = Load<BridgeHost>("<BridgeHost Ctor=\"2.5\"/>");
            Assert.Equal(99.0, host.Ctor!.Value); // the registered converter's value, not the bridged ctor's
        }
        finally
        {
            XamlConverters.Register(typeof(CtorWrapped), new BridgePassthrough()); // restore bridge-like behavior
        }
    }

    [Fact] // XC7 (audit ×2): Style is DENIED in the probe AND the bridge — its Selector ctor would
    // silently construct an empty setterless style from a text attribute. The W2e probe upgraded the
    // failure from an unpositioned load-time ArgumentException to a positioned parse-time CUR2402 — the
    // exact member class (Style/Theme) a bare selector-string typo lands on.
    public void XC7_StyleDenied_IsPositionedParseError()
    {
        var ex = ThrowsLoad("CUR2402", () => Load("<Border Style=\".card\"/>"));
        Assert.Contains("Style", ex.Message);
    }

    [Fact] // XC11 (audit — the §1a consistency assert): the RECORDED route and the EXECUTING bridge must
    // agree for every framework member value type — a probe-says-route/bridge-denies split (the Style
    // finding) or the inverse would silently reopen the G4 hole.
    public void XC11_RecordedRoutes_AgreeWithBridgeExecution()
    {
        var uiNs = "https://cursorial.dev/ui";
        string[] elements = ["Border", "Button", "TextBlock", "ListBox", "Window", "ProgressBar"];
        string[] members = ["Style", "Theme", "Background", "Opacity", "Visibility"];

        foreach (var element in elements)
        {
            var resolution = ReflectionXamlMetadata.Instance.TryGetType(uiNs, element);
            Assert.True(resolution.IsResolved);

            foreach (var memberName in members)
            {
                var member = resolution.Type!.TryGetMember(memberName);
                if (member is null)
                    continue;

                var kind = member.Route.Kind;
                if (kind is not (RouteKind.ImplicitOp or RouteKind.ExplicitOp or RouteKind.Constructor or RouteKind.ParseMethod))
                    continue; // only bridge-kind routes assert against the bridge

                var clr = member.ValueType.UnderlyingSystemType!;
                Assert.True(XamlConverters.BridgeConverterForType(clr) is not null,
                            $"{element}.{memberName}: recorded route {kind} but ConversionBridge denies '{clr.Name}'");
            }
        }
    }

    [Fact] // XC8 (audit): a THROWING route re-surfaces as a positioned CUR2401 — never a raw
    // TargetInvocationException from reflection
    public void XC8_ThrowingRoute_IsPositionedConversionError()
    {
        var ex = ThrowsLoad("CUR2401", () => Load("<BridgeHost Throwing=\"nope\"/>"));
        Assert.Contains("bad payload 'nope'", ex.Message); // the route's own error, positioned
    }

    [Fact] // XC9 (audit): a Nullable<T>-typed member bridges through T's route (the ladder's unwrap rule)
    public void XC9_NullableMember_Bridges()
    {
        var host = Load<BridgeHost>("<BridgeHost NullableCtor=\"4.5\"/>");
        Assert.Equal(4.5, host.NullableCtor!.Value.Value);
    }

    [Fact] // XC10 (W2e — the G4 close): a text value on a member with NO route of any kind is a
    // positioned PARSE error naming the fix — the document previously built silently and died at load
    public void XC10_NoRoute_IsPositionedParseError()
    {
        var ex = ThrowsLoad("CUR2402", () => Load("<BridgeHost Dead=\"whatever\"/>"));

        Assert.Contains("No conversion route", ex.Message);
        Assert.Contains("Unconvertible", ex.Message);
        Assert.Contains("markup extension", ex.Message); // the guidance names the escape hatches
    }

    private sealed class FixedCtorConverter : ITypeConverter
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext context) => new CtorWrapped(99.0);
    }

    private sealed class BridgePassthrough : ITypeConverter
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext context)
            => new CtorWrapped(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }
}
