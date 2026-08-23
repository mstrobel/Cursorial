using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// A curly-form intrinsic / primitive <c>Setter.Value</c> — <c>{x:Type T}</c>, <c>{x:Boolean True}</c> — used to
/// FAIL CLOSED in the frontend's bespoke setter classifier ("not supported in v1"). It now rides the shared
/// <c>BuildExtensionValue</c> funnel, so the generator lowers it (no <c>// TODO X5</c> drop) exactly as the loader
/// resolves it (Invariant #3, emitter ≡ loader): a Folded token → <c>typeof(...)</c>, a primitive object → the value.
/// </summary>
public class SetterCurlyIntrinsicLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private const string KindHost = @"
namespace GenApp
{
    public sealed class KindHost : Cursorial.UI.Controls.Control
    {
        public static readonly Cursorial.UI.StyledProperty<System.Type?> KindProperty =
            Cursorial.UI.UIProperty.Register<KindHost, System.Type?>(nameof(Kind));
        public System.Type? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    }
}";

    [Fact] // <Setter Property="Occludes" Value="{x:Boolean True}"/> lowers with no drop AND the loaded setter's
    // value is boxed true — the same result the reflective loader produces (proven in Section17 runtime tests).
    public void Lowered_PrimitiveCurlySetterValue_NoDrop_LoadsToBool()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetView1\">" +
            "<StackPanel.Resources>" +
              "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" Value=\"{x:Boolean True}\"/></Style>" +
            "</StackPanel.Resources>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class SetView1 : Cursorial.UI.Controls.StackPanel { public SetView1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("SetHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);                     // not the fail-closed setter drop
        Assert.Contains("OccludesProperty", lowered);                  // the setter targets Occludes

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var loaded = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.SetView1")!)!;
        var style = (Cursorial.UI.Style)loaded.Resources["Style:Border"];
        Assert.Equal(true, Assert.Single(style.Setters).Value);        // the primitive lowered to boxed true
    }

    [Fact] // <Setter Property="Kind" Value="{x:Type Button}"/> lowers to typeof(...) with no drop (was fail-closed).
    public void Lowered_CurlyXTypeSetterValue_NoDrop_EmitsTypeof()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetView2\">" +
            "<StackPanel.Resources>" +
              "<Style TargetType=\"vm:KindHost\"><Setter Property=\"Kind\" Value=\"{x:Type Button}\"/></Style>" +
            "</StackPanel.Resources>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class SetView2 : Cursorial.UI.Controls.StackPanel { public SetView2() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("SetHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(KindHost), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);                                  // not the fail-closed setter drop
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", lowered);   // the curly {x:Type} folded → typeof
        Assert.Contains("KindProperty", lowered);                                   // the setter targets Kind
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }
}
