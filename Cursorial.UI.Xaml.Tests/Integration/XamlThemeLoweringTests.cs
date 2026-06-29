using System;
using System.IO;
using System.Linq;

using Cursorial.Drawing.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.Default;
using Cursorial.UI.Xaml;

using Xunit;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// WS-X5.4 — the theme-lowering dual-run drift gate (plan X174). Each <c>Themes/*.xaml</c> is build-LOWERED to a
/// reflection-free <c>GeneratedXamlLoaders.Build…()</c> that <see cref="CursorialDefaultTheme"/> calls at runtime,
/// with the <c>.xaml</c> retained embedded as the oracle. This loads each theme BOTH ways — the build-lowered
/// builder AND the reflective <see cref="XamlLoader"/> over the embedded source — and asserts the two
/// <see cref="ResourceDictionary"/> trees are structurally equivalent (same keys, value shapes, brush colors,
/// glyph carriers, resource references, styles + their setters/children, and theme-variant sub-dictionaries).
/// Any drift between the lowering and the reflective loader fails here. (Behavioral parity with the code-first
/// <c>CursorialTheme.BuiltIn</c> is the separate render gate — ArchOne/Palette/Styles tests.)
/// </summary>
public class XamlThemeLoweringTests
{
    [Theory]
    [InlineData("Controls", "Cursorial.UI.Themes.Themes.Default.Controls.xaml")]
    [InlineData("Palette", "Cursorial.UI.Themes.Themes.Default.Palette.xaml")]
    [InlineData("Styles", "Cursorial.UI.Themes.Themes.Default.Styles.xaml")]
    public void LoweredBuilder_StructurallyMatches_ReflectiveLoad(string name, string resource)
    {
        var lowered = name switch
        {
            "Controls" => CursorialDefaultTheme.LoadControls(),
            "Palette" => CursorialDefaultTheme.LoadPalette(),
            "Styles" => CursorialDefaultTheme.LoadStyles(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var reflective = (ResourceDictionary)XamlLoader.Shared.Load(ReadEmbed(resource), new Uri($"cursorial-oracle://{name}.xaml"));

        AssertEquivalent(reflective, lowered, name);
    }

    private static string ReadEmbed(string resource)
    {
        var asm = typeof(CursorialDefaultTheme).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded '{resource}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertEquivalent(ResourceDictionary expected, ResourceDictionary actual, string ctx)
    {
        // Same keyed entries (a dropped/extra entry is the loudest drift signal).
        Assert.Equal(
            expected.Keys.Select(KeyString).OrderBy(s => s, StringComparer.Ordinal),
            actual.Keys.Select(KeyString).OrderBy(s => s, StringComparer.Ordinal));

        foreach (var key in expected.Keys)
        {
            Assert.True(actual.TryGetValue(key, out var av), $"{ctx}: lowered is missing key '{KeyString(key)}'");
            expected.TryGetValue(key, out var ev);
            AssertValueEquivalent(ev, av, $"{ctx}[{KeyString(key)}]");
        }

        // The theme-styles slot (Styles.xaml's <ResourceDictionary.Styles>), index-aligned (both build in doc order).
        Assert.Equal(expected.Styles?.Count ?? 0, actual.Styles?.Count ?? 0);

        if (expected.Styles is {} es && actual.Styles is {} acs)
        {
            for (int i = 0; i < es.Count; i++)
                AssertStyleEquivalent(es[i], acs[i], $"{ctx}.Styles[{i}]");
        }

        // Theme-variant sub-dictionaries (Palette.xaml's per-(base × tier) brush sets), keyed by variant.
        var ev2 = expected.ThemeDictionaries.ToDictionary(p => p.Key.ToString()!, p => p.Value);
        var av2 = actual.ThemeDictionaries.ToDictionary(p => p.Key.ToString()!, p => p.Value);
        Assert.Equal(ev2.Keys.OrderBy(s => s, StringComparer.Ordinal), av2.Keys.OrderBy(s => s, StringComparer.Ordinal));

        foreach (var (vk, sub) in ev2)
            AssertEquivalent(sub, av2[vk], $"{ctx}.ThemeDictionaries[{vk}]");

        // Merged dictionaries (none in the leaf theme files today, but recurse for completeness).
        Assert.Equal(expected.MergedDictionaries.Count, actual.MergedDictionaries.Count);

        for (int i = 0; i < expected.MergedDictionaries.Count; i++)
            AssertEquivalent(expected.MergedDictionaries[i], actual.MergedDictionaries[i], $"{ctx}.Merged[{i}]");
    }

    private static void AssertValueEquivalent(object? expected, object? actual, string ctx)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual!.GetType());

        switch (expected)
        {
            case SolidColorBrush brush:
                Assert.Equal(brush.Color, ((SolidColorBrush)actual).Color);
                break;
            case GlyphSetCarrier:          // record — structural equality over the glyph triple
            case ResourceReference:        // record struct — structural equality over the Key
                Assert.Equal(expected, actual);
                break;
            case Style style:
                AssertStyleEquivalent(style, (Style)actual, ctx);
                break;
            case ControlTemplate template:
                // Distinct fresh instances per load — compare the statically-meaningful TargetType (the content
                // factory's behavior is the render gate's job).
                Assert.Equal(template.TargetType, ((ControlTemplate)actual).TargetType);
                break;
        }
    }

    private static void AssertStyleEquivalent(Style expected, Style actual, string ctx)
    {
        Assert.Equal(expected.Key, actual.Key);

        Assert.Equal(expected.Setters.Count, actual.Setters.Count);
        for (int i = 0; i < expected.Setters.Count; i++)
        {
            Assert.Equal(expected.Setters[i].Property.Name, actual.Setters[i].Property.Name);
            AssertValueEquivalent(expected.Setters[i].Value, actual.Setters[i].Value, $"{ctx}.Setter[{i}]({expected.Setters[i].Property.Name})");
        }

        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
            AssertStyleEquivalent(expected.Children[i], actual.Children[i], $"{ctx}.Child[{i}]");
    }

    private static string KeyString(object key) => key is Type t ? t.FullName! : key.ToString()!;
}
