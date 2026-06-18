using System.Reflection;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5.4f — ResourceDictionary-root lowering. A no-<c>x:Class</c> <c>&lt;ResourceDictionary&gt;</c> document
/// (a theme / resource file) lowers to an internal static <c>Build&lt;File&gt;()</c> in a per-assembly
/// <c>GeneratedXamlLoaders</c> partial, returning the populated dictionary — no runtime parse, no provider.
/// These tests lower a real RD, compile + invoke the builder, and assert the dictionary content.
/// </summary>
public class ResourceDictionaryLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static ResourceDictionary InvokeBuilder(Assembly assembly, string method)
        => (ResourceDictionary)assembly
            .GetType("Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders")!
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, null)!;

    [Fact] // keyed entries + ThemeDictionaries (string variant key) build into the returned dictionary
    public void Lowered_ResourceDictionaryRoot_BuildsKeyedEntriesAndThemeDictionaries()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<SolidColorBrush x:Key=\"Accent\" Color=\"#3050C0\"/>" +
            "<ResourceDictionary.ThemeDictionaries>" +
              "<ResourceDictionary x:Key=\"Dark\"><SolidColorBrush x:Key=\"Ink\" Color=\"#101010\"/></ResourceDictionary>" +
            "</ResourceDictionary.ThemeDictionaries>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("internal static partial class GeneratedXamlLoaders", lowered);
        Assert.Contains("global::Cursorial.UI.ResourceDictionary BuildMyView()", lowered);
        Assert.Contains(".ThemeDictionaries[\"Dark\"]", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");

        // The keyed brush built with its converted (init-only) Color, added under "Accent".
        var accent = Assert.IsType<SolidColorBrush>(dict["Accent"]);
        Assert.Equal(Color.FromRgb(0x30, 0x50, 0xC0), accent.Color);

        // The theme sub-dictionary keyed by the parsed variant key, with its own keyed entry.
        var darkInk = Assert.IsType<SolidColorBrush>(dict.ThemeDictionaries["Dark"]["Ink"]);
        Assert.Equal(Color.FromRgb(0x10, 0x10, 0x10), darkInk.Color);
    }

    [Fact] // an {x:Static} x:Key resolves at codegen to the static member as the dictionary key object
    public void Lowered_ResourceDictionaryRoot_XStaticKey_ResolvesToStaticMember()
    {
        // ThemeKeys exposes well-known resource-key statics in the default UI xmlns (Cursorial.UI.Themes).
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<SolidColorBrush x:Key=\"{x:Static ThemeKeys.WindowBackground}\" Color=\"#0A0A0A\"/>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The key is the resolved static reference (the actual key object), not the literal string.
        Assert.Contains(".Add(global::Cursorial.UI.Themes.ThemeKeys.WindowBackground,", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");

        var brush = Assert.IsType<SolidColorBrush>(dict[ThemeKeys.WindowBackground]);
        Assert.Equal(Color.FromRgb(0x0A, 0x0A, 0x0A), brush.Color);
    }

    [Fact] // X5.4g — a <Style TargetType> with Setters: TargetType→Selectors.OfType, Setter.Property resolved, {DynamicResource}→ResourceReference
    public void Lowered_Style_WithTargetTypeAndSetters()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<ResourceDictionary.Styles>" +
              "<Style TargetType=\"Button\">" +
                "<Setter Property=\"Foreground\" Value=\"{DynamicResource Accent}\"/>" +
              "</Style>" +
            "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("new global::Cursorial.UI.Style(global::Cursorial.UI.Selectors.OfType(null, typeof(global::Cursorial.UI.Controls.Button)))", lowered);
        Assert.Contains("ForegroundProperty", lowered);
        Assert.Contains("new global::Cursorial.UI.ResourceReference(\"Accent\")", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");

        var style = Assert.Single(dict.Styles!);
        var setter = Assert.Single(style.Setters);
        Assert.Same(Control.ForegroundProperty, setter.Property);          // Property resolved to the registered owner
        var reference = Assert.IsType<ResourceReference>(setter.Value);     // {DynamicResource} → a carrier (resolved per-element)
        Assert.Equal("Accent", reference.Key);
    }
}
