using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// Element-form <c>&lt;x:Static Member="Type.Member"/&gt;</c> as a stand-in for an element in the
/// resource-dictionary positions the emitter builds with a bespoke loop (MergedDictionaries /
/// ThemeDictionaries) rather than through <c>EmitObject</c>. The bespoke loop must funnel a
/// markup-extension child to its standalone value — a <c>new ResourceDictionary()</c> that dropped the
/// <c>Member=</c> was the regression this guards.
/// </summary>
public class XStaticElementFormLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:t=\"using:TestApp\"";

    // Static fixtures the x:Static element-form children point at (a merged dict, a shared Setter, a shared condition).
    private const string Stub = @"
namespace TestApp
{
    public static class ThemeStub
    {
        public static readonly Cursorial.UI.ResourceDictionary Shared = new();
    }

    public static class SetterStub
    {
        public static readonly Cursorial.UI.Setter Shared =
            new(Cursorial.UI.Controls.Control.ForegroundProperty, null);
    }

    public static class ConditionStub
    {
        public static readonly Cursorial.UI.DataCondition Shared =
            new() { Binding = new Cursorial.UI.Data.Binding() };
    }
}";

    [Fact] // <x:Static Member="t:ThemeStub.Shared"/> as a MergedDictionaries child lowers to the member ref
    public void Lowered_XStatic_MergedDictionariesChild_ResolvesMember_AndMerges()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.XStaticView1\">" +
            "<StackPanel.Resources>" +
              "<ResourceDictionary>" +
                "<ResourceDictionary.MergedDictionaries>" +
                  "<x:Static Member=\"t:ThemeStub.Shared\"/>" +
                "</ResourceDictionary.MergedDictionaries>" +
              "</ResourceDictionary>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        var view = "namespace TestApp { public partial class XStaticView1 : Cursorial.UI.Controls.StackPanel { public XStaticView1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("XStaticHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The MergedDictionaries child is the RESOLVED static member, NOT a fresh empty dictionary.
        Assert.Contains("MergedDictionaries.Add(global::TestApp.ThemeStub.Shared)", lowered);

        // EmitAndLoad compiles the lowered source WITH the stub + view — a clean build is the compile check
        // (CompileErrors would compile the lowered source alone, where TestApp.ThemeStub is undefined).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var instance = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.XStaticView1")!)!;
        var shared = assembly.GetType("TestApp.ThemeStub")!.GetField("Shared")!.GetValue(null);

        // The merged dictionary IS the shared static instance (the same reference), not a detached empty dict.
        Assert.Same(shared, Assert.Single(instance.Resources.MergedDictionaries));
    }

    [Fact] // <x:Static/> as a <Style.Setters> item adds the RESOLVED shared Setter (was a CURG3001 drop —
    // the bespoke EmitSetter now funnels a markup-extension item through its standalone value).
    public void Lowered_XStatic_StyleSettersItem_AddsResolvedSetter()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.XStaticView2\">" +
            "<StackPanel.Resources><ResourceDictionary>" +
              "<Style x:Key=\"S\" TargetType=\"Button\">" +
                "<Style.Setters><x:Static Member=\"t:SetterStub.Shared\"/></Style.Setters>" +
              "</Style>" +
            "</ResourceDictionary></StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/></StackPanel>";
        var view = "namespace TestApp { public partial class XStaticView2 : Cursorial.UI.Controls.StackPanel { public XStaticView2() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("XStaticHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("Setters.Add((global::Cursorial.UI.Setter)(global::TestApp.SetterStub.Shared))", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var instance = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.XStaticView2")!)!;
        var shared = assembly.GetType("TestApp.SetterStub")!.GetField("Shared")!.GetValue(null);

        var style = Assert.IsType<Cursorial.UI.Style>(instance.Resources["S"]);
        Assert.Same(shared, Assert.Single(style.Setters));
    }

    [Fact] // <x:Static/> as a <Style.When> item adds the RESOLVED shared DataCondition (was a DropStyle —
    // the bespoke EmitDataCondition now funnels a markup-extension item, keeping the style).
    public void Lowered_XStatic_StyleWhenItem_AddsResolvedCondition()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.XStaticView3\">" +
            "<StackPanel.Resources><ResourceDictionary>" +
              "<Style x:Key=\"S\" TargetType=\"Button\">" +
                "<Style.When><x:Static Member=\"t:ConditionStub.Shared\"/></Style.When>" +
              "</Style>" +
            "</ResourceDictionary></StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/></StackPanel>";
        var view = "namespace TestApp { public partial class XStaticView3 : Cursorial.UI.Controls.StackPanel { public XStaticView3() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("XStaticHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("When.Add((global::Cursorial.UI.DataCondition)(global::TestApp.ConditionStub.Shared))", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var instance = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.XStaticView3")!)!;
        var shared = assembly.GetType("TestApp.ConditionStub")!.GetField("Shared")!.GetValue(null);

        var style = Assert.IsType<Cursorial.UI.Style>(instance.Resources["S"]);
        Assert.Same(shared, Assert.Single(style.When));
    }
}
