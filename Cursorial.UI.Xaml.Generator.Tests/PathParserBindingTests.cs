using Cursorial.UI;
using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The shared path parser's binding-lane features (WS-PP): compiled indexer hops with the
/// int/enum/string key ladder, STATIC-rooted chains (<c>(t:Type.StaticMember).Child</c> — compiled
/// with a RelativeSource-Self anchor, no DataContext consulted), and member steps that CARRY their
/// owner's UIProperty registration field so the runtime observes the property system directly.
/// </summary>
public class PathParserBindingTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string Lower(string xaml, CSharpCompilation compilation) => GeneratorHarness.LowerView(compilation, xaml);

    [Fact] // string- and enum-keyed indexer hops compile through the shared ladder and resolve live
    public void CompiledBinding_StringAndEnumIndexerHops_ResolveLive()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.KeyView\" x:DataType=\"t:KeyVm\">" +
            "<TextBlock x:Name=\"ByName\" Text=\"{Binding Map[apple]}\"/>" +      // unquoted non-int ⇒ Item[string]
            "<TextBlock x:Name=\"ByKind\" Text=\"{Binding Kinds[second]}\"/>" +   // constant-name match ⇒ Item[Kind]
            "</StackPanel>";

        const string codeBehind = @"
using System.Collections.Generic;
using Cursorial.UI.Controls;
namespace TestApp
{
    public enum Kind { First, Second }
    public sealed class KindMap { public string this[Kind kind] => $""kind:{kind}""; }
    public sealed class KeyVm
    {
        public Dictionary<string, string> Map { get; } = new() { [""apple""] = ""red""  };
        public KindMap Kinds { get; } = new();
    }
    public partial class KeyView : StackPanel { public KeyView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("?[\"apple\"]", lowered);                          // string key, quoted in the chain
        Assert.Contains("?[global::TestApp.Kind.Second]", lowered);        // enum key, case-corrected member reference
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered); // neither fell back reflective

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.KeyView")!)!;
        view.DataContext = Activator.CreateInstance(assembly.GetType("TestApp.KeyVm")!)!;

        Assert.Equal("red", ((TextBlock)view.Children[0]).Text);
        Assert.Equal("kind:Second", ((TextBlock)view.Children[1]).Text);
    }

    [Fact] // a `(t:Type.StaticMember)` first segment whose member is STATIC roots the chain at the static
    // value: compiled with a RelativeSource-Self anchor, working with NO x:DataType and NO DataContext.
    public void CompiledBinding_StaticRoot_CompilesWithSelfAnchor_NoDataContextNeeded()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.StaticRootView\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding (t:Defaults.Current).Name, Mode=OneWay}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class Profile { public string Name { get; set; } = ""fallback""; }
    public static class Defaults { public static Profile Current { get; } = new() { Name = ""from-static"" }; }
    public partial class StaticRootView : StackPanel { public StaticRootView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // Compiled (TSource = object — the Self-anchored root is ignored by the getter), rooted at the static.
        Assert.Contains("CompiledBinding<object,", lowered);
        Assert.Contains("global::TestApp.Defaults.Current", lowered);
        Assert.Contains("RelativeSource = global::Cursorial.UI.Data.RelativeSource.Self", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.StaticRootView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        // NO DataContext assigned at all — the static root supplies the chain.
        Assert.Equal("from-static", label.Text);
    }

    [Fact] // a member hop whose owner registers a <Name>Property carries the registration FIELD on its
    // step, so the runtime observes the property system directly (no name-based registry probe).
    public void CompiledBinding_UIObjectHop_CarriesTheRegistrationField()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:Cursorial.UI.Controls\" x:Class=\"TestApp.CarryView\" x:DataType=\"t:TextBox\">" +
            "<TextBlock x:Name=\"Echo\" Text=\"{Binding Text, Mode=OneWay}\"/>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace TestApp { public partial class CarryView : Cursorial.UI.Controls.StackPanel { public CarryView() => InitializeComponent(); } }"));
        var lowered = Lower(xaml, compilation);

        // The step carries the property identity — the just-landed CompiledPathStep.UIProperty channel.
        Assert.Contains("{ UIProperty = global::Cursorial.UI.Controls.TextBox.TextProperty }", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.CarryView")!)!;
        var echo = Assert.IsType<TextBlock>(view.Children[0]);

        var source = new TextBox { Text = "before" };
        view.DataContext = source;
        Assert.Equal("before", echo.Text);

        source.SetValue(TextBox.TextProperty, "after"); // a pure property-system write — the carried field observes it
        Assert.Equal("after", echo.Text);
    }

    [Fact] // WS-PP2 — ElementName roots the walk at the named element's DOCUMENT type: the element
    // property binds compiled (with its registration field carried), and a `DataContext.` prefix
    // drills through the source element's declared x:DataType as a cast-qualified hop.
    public void CompiledBinding_ElementName_TypesTheWalk_AndDrillsDataContext()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.ElementView\">" +
            "<TextBox x:Name=\"Box\" x:DataType=\"t:HostVm\" Text=\"typed\"/>" +
            "<TextBlock x:Name=\"Echo\" Text=\"{Binding Text, ElementName=Box}\"/>" +
            "<TextBlock x:Name=\"Drill\" Text=\"{Binding DataContext.Label, ElementName=Box}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class HostVm { public string Label { get; set; } = ""from-vm""; }
    public partial class ElementView : StackPanel { public ElementView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("CompiledBinding<global::Cursorial.UI.Controls.TextBox, string>", lowered);
        Assert.Contains("ElementName = \"Box\"", lowered);
        Assert.Contains("{ UIProperty = global::Cursorial.UI.Controls.TextBox.TextProperty }", lowered);
        Assert.Contains(".DataContextProperty) as global::TestApp.HostVm)?.Label", lowered); // the drill casts the GetValue-read DataContext (the hop is itself registered)
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);  // nothing fell back

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.ElementView")!)!;
        var box = (TextBox)view.Children[0];
        var echo = (TextBlock)view.Children[1];
        var drill = (TextBlock)view.Children[2];

        Assert.Equal("typed", echo.Text); // the element hop resolved

        box.DataContext = Activator.CreateInstance(assembly.GetType("TestApp.HostVm")!)!;
        Assert.Equal("from-vm", drill.Text); // the DataContext drill resolved through the declared x:DataType

        box.SetValue(TextBox.TextProperty, "changed"); // pure property-system write — the carried field observes
        Assert.Equal("changed", echo.Text);
    }

    [Fact] // WS-PP2 — FindAncestor with a resolvable AncestorType roots the walk at the ancestor type
    public void CompiledBinding_FindAncestor_WithAncestorType_Compiles()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.AncestorView\" Spacing=\"7\">" +
            "<Border><TextBlock x:Name=\"Probe\" Width=\"{Binding Spacing, RelativeSource={RelativeSource FindAncestor, AncestorType=StackPanel}}\"/></Border>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace TestApp { public partial class AncestorView : Cursorial.UI.Controls.StackPanel { public AncestorView() => InitializeComponent(); } }"));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("CompiledBinding<global::Cursorial.UI.Controls.StackPanel, int>", lowered);
        Assert.Contains("RelativeSourceMode.FindAncestor", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.AncestorView")!)!;
        var probe = (TextBlock)((Border)view.Children[0]).Child!;
        Assert.Equal(7, probe.Width); // resolved against the ancestor panel's Spacing
    }

    [Fact] // WS-PP2 — a backward-visible same-document {StaticResource} Source compiles: the emitter
    // constructed the resource, so its TYPE is known and the built local var itself anchors the binding.
    public void CompiledBinding_StaticResourceSource_RootsAtTheConstructedType()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.ResSrcView\">" +
            "<StackPanel.Resources><t:SettingsVm x:Key=\"settings\"/></StackPanel.Resources>" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Title, Source={StaticResource settings}}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class SettingsVm { public string Title { get; set; } = ""from-resource""; }
    public partial class ResSrcView : StackPanel { public ResSrcView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("CompiledBinding<global::TestApp.SettingsVm, string>", lowered);
        Assert.Contains("Source = __", lowered); // anchored on the BUILT resource var, not a runtime key lookup
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.ResSrcView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        Assert.Equal("from-resource", label.Text); // resolved with no DataContext — the resource IS the source
        Assert.Same(view.Resources["settings"],
                    assembly.GetType("TestApp.ResSrcView")!.Assembly == assembly ? view.Resources["settings"] : null); // same instance, sanity
    }

    [Fact] // WS-PP2 — {Binding X, RelativeSource TemplatedParent} INSIDE a ControlTemplate compiles
    // against the statically-known TargetType (the same fact {TemplateBinding} lowers on); the anchor
    // resolves at template application, so the install stays inline in the factory body.
    public void CompiledBinding_TemplatedParent_InsideTemplate_CompilesAgainstTargetType()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.TplView\">" +
            "<StackPanel.Resources>" +
              "<ControlTemplate x:Key=\"Tpl\" TargetType=\"HeaderedContentControl\">" +
                "<TextBlock Text=\"{Binding Header, RelativeSource={RelativeSource TemplatedParent}}\"/>" +
              "</ControlTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace TestApp { public partial class TplView : Cursorial.UI.Controls.StackPanel { public TplView() => InitializeComponent(); } }"));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("CompiledBinding<global::Cursorial.UI.Controls.HeaderedContentControl,", lowered);
        Assert.Contains("RelativeSource = global::Cursorial.UI.Data.RelativeSource.TemplatedParent", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered); // did not fall back reflective

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(Activator.CreateInstance(assembly.GetType("TestApp.TplView")!)); // the factory body compiles + runs
    }

    [Fact] // WS-PP2 — an UNTYPEABLE anchor still carries a compiled binding when the path is
    // UIProperty-rooted: the anchor init is expressible (same as the reflective lane's), and
    // `(Owner.Property)` needs no root type — any UIObject answers GetValue.
    public void CompiledBinding_UntypeableAnchor_StillCompilesUIPropertyPath()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.UntypedAnchorView\">" +
            "<TextBlock x:Name=\"A\" Width=\"{Binding (ui:Grid.Row), RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}\"/>" + // doc-level TemplatedParent: type unknown
            "<TextBlock x:Name=\"B\" Width=\"{Binding (ui:Grid.Row), ElementName=Ghost, Mode=OneWay}\"/>" +                              // unresolvable name: type unknown
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace TestApp { public partial class UntypedAnchorView : Cursorial.UI.Controls.StackPanel { public UntypedAnchorView() => InitializeComponent(); } }"));
        var lowered = Lower(xaml, compilation);

        // Both compiled rooted at UIObject, each with its own (runtime-resolving) anchor init.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(lowered, @"CompiledBinding<global::Cursorial\.UI\.UIObject, int>").Count);
        Assert.Contains("RelativeSource = global::Cursorial.UI.Data.RelativeSource.TemplatedParent", lowered);
        Assert.Contains("ElementName = \"Ghost\"", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(Activator.CreateInstance(assembly.GetType("TestApp.UntypedAnchorView")!)); // inert anchors, no crash
    }

    [Fact] // WS-PP3 — a Style.When DataCondition in the control styles' shape (Self anchor +
    // `(Owner.Property)`) carries a COMPILED descriptor through the shared builder: the last
    // reflective Binding instantiations in the theme XAML are gone.
    public void DataCondition_SelfQualifiedUIProperty_CarriesCompiledDescriptor()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.WhenView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"S\" Selector=\":is(TextBox)\">" +
                "<Style.When>" +
                  "<DataCondition Binding=\"{Binding RelativeSource={RelativeSource Self}, Path=(ui:Grid.Row)}\" Value=\"{x:Null}\"/>" +
                "</Style.When>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace TestApp { public partial class WhenView : Cursorial.UI.Controls.StackPanel { public WhenView() => InitializeComponent(); } }"));
        var lowered = Lower(xaml, compilation);

        Assert.Contains("new global::Cursorial.UI.DataCondition { Binding = new global::Cursorial.UI.Data.CompiledBinding<global::Cursorial.UI.UIObject, int>", lowered);
        Assert.Contains("RelativeSource = global::Cursorial.UI.Data.RelativeSource.Self", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(Activator.CreateInstance(assembly.GetType("TestApp.WhenView")!)); // the condition constructs
    }
}
