using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The one funnel, two Value-delivery slot classes (empirically loader-matched): <c>Setter.Value</c> is a DEDICATED
/// LENIENT slot — a <c>{DynamicResource}</c> lowers to a ResourceReference CARRIER (its <c>BuildSetter</c> twin) —
/// while a GENERIC-member STRICT slot (<c>DataCondition.Value</c>) REJECTS <c>{DynamicResource}</c> exactly as the
/// loader throws CUR2210 at attach. Both route through the same <c>EmitValue</c>; only <c>ResourceLenient</c> differs.
/// (A <c>{Binding}</c> Value on either object-typed slot is rejected earlier still — by the shared frontend, CUR2210
/// at parse — so it never reaches the emitter and is covered by the frontend's bindability tests, not here.)
/// </summary>
public class ValueSlotLenientStrictTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string LowerStyle(string styleBody)
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.VsView\">" +
            "<StackPanel.Resources>" + styleBody + "</StackPanel.Resources>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class VsView : Cursorial.UI.Controls.StackPanel { public VsView() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("VsHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        return GeneratorHarness.LowerView(compilation, xaml);
    }

    [Fact] // LENIENT: a {DynamicResource} Setter.Value lowers to a ResourceReference carrier (no drop).
    public void Lowered_SetterValue_DynamicResource_IsCarrier()
    {
        var lowered = LowerStyle(
            "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" Value=\"{DynamicResource K}\"/></Style>");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.ResourceReference(", lowered); // the carrier — the lenient slot admits it
    }

    [Fact] // STRICT: a {DynamicResource} DataCondition.Value is REJECTED — the When-gated style drops (no carrier),
    // matching the loader's CUR2210 (a generic object member does not admit a dynamic resource).
    public void Lowered_DataConditionValue_DynamicResource_IsRejected()
    {
        var lowered = LowerStyle(
            "<Style TargetType=\"Border\">" +
              "<Style.When><DataCondition Binding=\"{Binding Foo}\" Value=\"{DynamicResource K}\"/></Style.When>" +
              "<Setter Property=\"Occludes\" Value=\"{x:Boolean True}\"/>" +
            "</Style>");

        Assert.Contains("TODO X5", lowered);                                    // the style dropped (visible, never silent)
        Assert.Contains("<Style.When> condition is not lowerable", lowered);    // the drop reason
        Assert.DoesNotContain("ResourceReference", lowered);                    // NOT a carrier — that would be the lenient behavior
    }
}
