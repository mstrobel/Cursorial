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

    [Fact] // X5.4h — an explicit Selector="…" bakes to a reflection-free Selectors chain; Style.Children nest
    public void Lowered_Style_ExplicitSelectorAndChildren_Bake()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<ResourceDictionary.Styles>" +
              "<Style Selector=\".caps-nocolor Button:focus, .caps-nocolor Button:pressed\" TargetType=\"Button\">" +
                "<Setter Property=\"Foreground\" Value=\"{DynamicResource Accent}\"/>" +
              "</Style>" +
              "<Style TargetType=\"Button\">" +
                "<Style.Children>" +
                  "<Style Selector=\"^:pointerover\" TargetType=\"Button\">" +
                    "<Setter Property=\"Background\" Value=\"{DynamicResource Hover}\"/>" +
                  "</Style>" +
                "</Style.Children>" +
              "</Style>" +
            "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The selector baked to a reflection-free Selectors chain: class, descendant, exact type, pseudo, the
        // comma list → Or, and the ^-nesting child → Selectors.Nesting().
        Assert.Contains("global::Cursorial.UI.Selectors.Class(null, \"caps-nocolor\")", lowered);
        Assert.Contains(".Descendant()", lowered);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", lowered);
        Assert.Contains(".PseudoClass(\"focus\")", lowered);
        Assert.Contains(".Or(", lowered);
        Assert.Contains("global::Cursorial.UI.Selectors.Nesting().PseudoClass(\"pointerover\")", lowered);
        Assert.Contains(".Children.Add(", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        // The baked chains are valid C# that construct Styles; the tree builds + seals (nesting composes).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");
        Assert.Equal(2, dict.Styles!.Count);
        Assert.Single(dict.Styles[1].Children); // the ^:pointerover nested rule
    }

    [Fact] // X5.4i — a same-dictionary {StaticResource} Setter value resolves to the built entry (its load-time snapshot)
    public void Lowered_StaticResource_SameDict_Setter_ReferencesBuiltEntry()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<ControlTemplate x:Key=\"T\" TargetType=\"Button\"><Border/></ControlTemplate>" +
            "<Style x:Key=\"{x:Type Button}\" TargetType=\"Button\">" +
              "<Setter Property=\"Template\" Value=\"{StaticResource T}\"/>" +
            "</Style>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");

        var template = dict["T"];
        var style = Assert.IsType<Cursorial.UI.Style>(dict[typeof(Button)]);
        var setter = Assert.Single(style.Setters);
        Assert.Same(template, setter.Value); // the {StaticResource} is the SAME object as the dictionary entry
    }

    [Fact] // X5.4i — a {StaticResource} INSIDE a template captures the entry var (the factory drops `static`)
    public void Lowered_StaticResource_InsideTemplate_CapturesEntryVar()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<ControlTemplate x:Key=\"Inner\" TargetType=\"Button\"><Border/></ControlTemplate>" +
            "<ControlTemplate x:Key=\"Outer\" TargetType=\"Button\">" +
              "<Button Template=\"{StaticResource Inner}\"/>" +
            "</ControlTemplate>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // The non-capturing Inner factory stays `static`; the capturing Outer factory drops it (it references the
        // enclosing Inner-template local). A 12-space indent directly before `global::` ⇒ a non-static factory.
        Assert.Contains("            static global::Cursorial.UI.UIElement __Factory", lowered);
        Assert.Contains("            global::Cursorial.UI.UIElement __Factory", lowered);

        // Compiles + builds — a `static` local function referencing the enclosing Inner local would be a CS error,
        // so a successful build is the proof that the capturing factory was correctly emitted non-static.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(InvokeBuilder(assembly, "BuildMyView")["Outer"]);
    }

    [Fact] // X5.4i — a keyed ControlTemplate's TargetType resolves a type TOKEN; a {TemplateBinding} resolves against it
    public void Lowered_ControlTemplate_TargetType_And_TemplateBinding()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<ControlTemplate x:Key=\"T\" TargetType=\"Button\">" +
              "<Border Background=\"{TemplateBinding Background}\"/>" +
            "</ControlTemplate>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", lowered);  // TargetType = a typeof token, not a converter call
        Assert.Contains("new global::Cursorial.UI.Data.TemplateBinding(", lowered);
        Assert.Contains(".BackgroundProperty)", lowered);                          // resolved against the template target
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var template = Assert.IsType<ControlTemplate>(InvokeBuilder(assembly, "BuildMyView")["T"]);
        Assert.Equal(typeof(Button), template.TargetType);
    }

    [Fact] // X5.4i — element-level {DynamicResource {x:Static}} → SetResourceReference with the resolved static key (object)
    public void Lowered_DynamicResource_XStaticKey_OnElement()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<Border x:Key=\"B\" Background=\"{DynamicResource {x:Static ThemeKeys.TextBrush}}\"/>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("global::Cursorial.UI.ResourceExtensions.SetResourceReference(", lowered);
        Assert.Contains("global::Cursorial.UI.Themes.ThemeKeys.TextBrush", lowered); // the resolved static member, not a string literal
        Assert.DoesNotContain("TODO X5", lowered);
    }

    [Fact] // X5.4i — a Text Setter value is converted to the property's value type (matching the reflection fold)
    public void Lowered_SetterValue_ConvertsToPropertyType()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<Style x:Key=\"{x:Type Button}\" TargetType=\"Button\">" +
              "<Setter Property=\"Padding\" Value=\"2,1\"/>" +
            "</Style>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The value runs the converter ladder (not passed as the raw "2,1" string the StyleSetterConverter can't take).
        Assert.Contains("__ConvertXamlValue(typeof(", lowered);
        Assert.Contains("\"2,1\"", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var style = Assert.IsType<Cursorial.UI.Style>(InvokeBuilder(assembly, "BuildMyView")[typeof(Button)]);
        Assert.IsNotType<string>(Assert.Single(style.Setters).Value); // converted off the raw "2,1" string to the Padding type
    }
}
