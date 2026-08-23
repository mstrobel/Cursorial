using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The generator's ShapingInits emits the SAME binding shaping members the loader's BuildBinding reads, in lockstep
/// (ConverterParameter / ConverterCulture / UpdateSourceTrigger / TargetNullValue / Trace — previously dropped in
/// BOTH lanes). Loader parity is proven in Section17_BindingShaping; this asserts the emitted initializer.
/// </summary>
public class BindingShapingLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string Lower(string inner)
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.BsView\">{inner}</StackPanel>";
        var view = "namespace GenApp { public partial class BsView : Cursorial.UI.Controls.StackPanel { public BsView() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("BsHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        return GeneratorHarness.LowerView(compilation, xaml);
    }

    [Fact] // a reflective {Binding} lowers every shaping member (no x:DataType → reflective lane)
    public void Lowered_ReflectiveBinding_EmitsAllShapingMembers()
    {
        var lowered = Lower(
            "<Border Width=\"{Binding Value, ConverterParameter=42, ConverterCulture=en-US, " +
            "UpdateSourceTrigger=LostFocus, TargetNullValue=none, Trace=True}\"/>");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("ConverterParameter = \"42\"", lowered);
        Assert.Contains("ConverterCulture = global::System.Globalization.CultureInfo.GetCultureInfo(\"en-US\")", lowered);
        Assert.Contains("UpdateSourceTrigger = global::Cursorial.UI.Data.UpdateSourceTrigger.LostFocus", lowered);
        Assert.Contains("TargetNullValue = \"none\"", lowered);
        Assert.Contains("Trace = true", lowered);
    }

    [Fact] // an unrecognized UpdateSourceTrigger is a HARD error (loader Fatals — the lowered build must too, not drop)
    public void Lowered_UnknownUpdateSourceTrigger_IsError()
    {
        var lowered = Lower("<Border Width=\"{Binding Value, UpdateSourceTrigger=Whenever}\"/>");
        Assert.Contains("ERROR X5", lowered);
        Assert.Contains("UpdateSourceTrigger", lowered);
    }
}
