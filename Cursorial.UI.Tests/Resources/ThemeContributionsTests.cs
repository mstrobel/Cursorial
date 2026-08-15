using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Themes;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Resources;

/// <summary>
/// The assembly theme-contribution tier (design doc §11.3a) resolution rules, exercised through the pure
/// <see cref="ThemeContributions.TryGetResource(ResourceDictionary[], object, ThemeVariant, out object?)"/>
/// overload so no test mutates the process-global registry (the chain-integration + real-app precedence
/// legs live in <c>Cursorial.UI.Bars.Tests</c>, whose Bars module already registers globally by design).
/// </summary>
public sealed class ThemeContributionsTests
{
    private static readonly ThemeVariant Dark = new(ThemeBase.Dark, ColorDepth.Truecolor);

    private static ResourceDictionary Dict(params (object Key, object? Value)[] entries)
    {
        var dict = new ResourceDictionary();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    // Plain marker types — resolution reads only Type.BaseType, so no UIElement machinery is needed.
    private class BaseControl;
    private class DerivedControl : BaseControl;
    private sealed class LeafControl : DerivedControl;

    [Fact] // an exact string key resolves; a missing key returns false with null out.
    public void ExactKey_ResolvesAndMisses()
    {
        var brush = Brush(1, 2, 3);
        ResourceDictionary[] contributions = [Dict(("Lib.Ink", brush))];

        Assert.True(ThemeContributions.TryGetResource(contributions, "Lib.Ink", Dark, out var hit));
        Assert.Same(brush, hit);

        Assert.False(ThemeContributions.TryGetResource(contributions, "Lib.Missing", Dark, out var miss));
        Assert.Null(miss);
    }

    [Fact] // an empty registry resolves nothing.
    public void Empty_ResolvesNothing()
    {
        Assert.False(ThemeContributions.TryGetResource([], "Lib.Ink", Dark, out var value));
        Assert.Null(value);
    }

    [Fact] // last-registered wins on a shared key (a later library shadows an earlier one).
    public void SharedKey_LastRegisteredWins()
    {
        var first = Brush(10, 10, 10);
        var second = Brush(20, 20, 20);
        ResourceDictionary[] contributions = [Dict(("Lib.Ink", first)), Dict(("Lib.Ink", second))];

        Assert.True(ThemeContributions.TryGetResource(contributions, "Lib.Ink", Dark, out var value));
        Assert.Same(second, value); // the second (last-registered) shadows the first
    }

    [Fact] // a control theme resolves by EXACT Type key (the CD13 contract shared by every chain tier).
    public void TypeKey_ExactMatch()
    {
        var themeStyle = new Style { Key = "T" };
        ResourceDictionary[] contributions = [Dict((typeof(BaseControl), themeStyle))];

        Assert.True(ThemeContributions.TryGetResource(contributions, typeof(BaseControl), Dark, out var value));
        Assert.Same(themeStyle, value);
    }

    [Fact] // a SUBCLASS Type key does NOT resolve the base entry — no base probing. The subclass opts into the
           // base theme by overriding Control.ControlThemeKey to return the base type (WPF DefaultStyleKey parity),
           // so its ControlThemeKey IS typeof(base) and hits the exact entry through this tier.
    public void TypeKey_SubclassMissesWithoutOptIn()
    {
        var baseStyle = new Style { Key = "base" };
        ResourceDictionary[] contributions = [Dict((typeof(BaseControl), baseStyle))];

        // typeof(DerivedControl) is NOT the key — exact-key resolution misses (the subclass must override
        // ControlThemeKey => typeof(BaseControl) to resolve, which then requests typeof(BaseControl) here).
        Assert.False(ThemeContributions.TryGetResource(contributions, typeof(DerivedControl), Dark, out var derived));
        Assert.Null(derived);
        Assert.False(ThemeContributions.TryGetResource(contributions, typeof(LeafControl), Dark, out var leaf));
        Assert.Null(leaf);
    }

    [Fact] // a Type key resolves per-variant when the contribution ships ThemeDictionaries (variant descent).
    public void TypeKey_VariantDescent()
    {
        var darkBrush = Brush(0, 0, 0);
        var lightBrush = Brush(255, 255, 255);
        var dict = new ResourceDictionary();
        dict.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Truecolor)] = Dict(("Lib.Ink", darkBrush));
        dict.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Light, ColorDepth.Truecolor)] = Dict(("Lib.Ink", lightBrush));
        ResourceDictionary[] contributions = [dict];

        Assert.True(ThemeContributions.TryGetResource(contributions, "Lib.Ink", Dark, out var dark));
        Assert.Same(darkBrush, dark);
        Assert.True(ThemeContributions.TryGetResource(contributions, "Lib.Ink", new ThemeVariant(ThemeBase.Light, ColorDepth.Truecolor), out var light));
        Assert.Same(lightBrush, light);
    }
}
