using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;
using Binding = Cursorial.UI.Data.Binding;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// <c>{x:Self}</c> (Level-0 slice) — the construction-time, read-only self-reference value intrinsic: resolves to
/// the object the value is being assigned onto (always the assignment TARGET, seeing through enclosing markup
/// extensions), at the moment of assignment. Supported positions: a member value, and a Binding
/// <c>Source={x:Self}</c>. Target-less positions (dictionary entries, <c>Setter.Value</c>, descriptor bindings)
/// and <c>Level &gt; 0</c> are rejected loudly — never silently misresolved.
/// </summary>
public sealed class Section20_XSelf : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // the foundation: a member value {x:Self} IS the object being assigned onto (UIProperty target)
    public void MemberValue_ResolvesToAssignmentTarget()
    {
        var host = Load<SelfHost>("<SelfHost Payload=\"{x:Self}\"/>");
        Assert.Same(host, host.Payload);
    }

    [Fact] // …and a plain CLR property target resolves identically (the non-UIProperty assign path)
    public void MemberValue_ClrProperty_ResolvesToAssignmentTarget()
    {
        var host = Load<SelfHost>("<SelfHost Bag=\"{x:Self}\"/>");
        Assert.Same(host, host.Bag);
    }

    [Fact] // element form <x:Self/> ≡ curly (both route through BuildExtensionValue)
    public void ElementForm_EqualsCurly()
    {
        var host = Load<SelfHost>("<SelfHost><SelfHost.Payload><x:Self/></SelfHost.Payload></SelfHost>");
        Assert.Same(host, host.Payload);
    }

    [Fact] // {Binding …, Source={x:Self}} — the self-anchor: Source is the binding's TARGET object (see-through)
    public void BindingSource_ResolvesToBindingTarget()
    {
        var border = Load<UIControls.Border>("<Border Height=\"{Binding Width, Source={x:Self}}\"/>");
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        Assert.Same(border, binding.Source); // the Border, never the Binding
    }

    [Fact] // a nested {x:Self} on a CHILD element resolves to the CHILD (its own assignment target), not the root
    public void MemberValue_OnChild_ResolvesToChildNotRoot()
    {
        var root = Load<UIControls.StackPanel>("<StackPanel><SelfHost Payload=\"{x:Self}\"/></StackPanel>");
        var host = (SelfHost)root.Children[0];
        Assert.Same(host, host.Payload);
    }

    [Fact] // a dictionary entry has no assignment target — a loud error at REALIZATION (entries are deferred)
    public void DictionaryEntry_IsRejected()
    {
        var dict = (Cursorial.UI.ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><x:Self x:Key=\"me\"/></ResourceDictionary>");
        var ex = Assert.ThrowsAny<System.Exception>(() => _ = dict["me"]); // realizing the entry surfaces the error
        Assert.Contains("assignment target", ex.Message);
    }

    [Fact] // Setter.Value applies to every matched element — no single target, rejected in both lanes
    public void SetterValue_IsRejected()
    {
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(
            $"<Style{Pre} TargetType=\"SelfHost\"><Setter Property=\"Payload\" Value=\"{{x:Self}}\"/></Style>"));
        Assert.Contains("assignment target", ex.Message);
    }

    [Fact] // a descriptor-position Source={x:Self} (DataCondition.Binding — armed per matched element later) is rejected
    public void DescriptorBindingSource_IsRejected()
    {
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(
            $"<Style{Pre} TargetType=\"Button\">" +
            "<Style.When><DataCondition Binding=\"{Binding Width, Source={x:Self}}\" Value=\"3\"/></Style.When>" +
            "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
            "</Style>"));
        Assert.Contains("descriptor", ex.Message);
    }

    [Fact] // Level > 0 (the construction-stack walk) is reserved — reported at parse, never misresolved to Level 0
    public void LevelAboveZero_IsReported()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<SelfHost>(
            "<SelfHost Payload=\"{x:Self Level=1}\"/>"));
        Assert.Equal(XamlDiagnosticCodes.UnsupportedIntrinsic, ex.Code);
    }

    // ── Adversarial-review regressions ────────────────────────────────────────────────────────────

    [Fact] // review: DataCondition.Value={x:Self} silently resolved to the DataCondition ITSELF (a nonsense,
    // unmatchable comparison operand) — now the style-machinery guard rejects it, matching the emitter's error
    public void DataConditionValue_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => LoadRaw(
            $"<Style{Pre} TargetType=\"Button\">" +
            "<Style.When><DataCondition Binding=\"{Binding Width}\" Value=\"{x:Self}\"/></Style.When>" +
            "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
            "</Style>"));
        Assert.Contains("style-machinery", ex.Message);
    }

    [Fact] // review: ConverterParameter={x:Self} was SILENTLY null (the literal-only Named read) — now rejected
    public void NestedShapingArg_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => Load<UIControls.Border>(
            "<Border Height=\"{Binding Width, ConverterParameter={x:Self}}\"/>"));
        Assert.Contains("ConverterParameter", ex.Message);
        Assert.Contains("literal", ex.Message);
    }

    [Fact] // review: a LEGITIMATE custom extension named Self ({p:Self}, non-intrinsics namespace) was silently
    // HIJACKED by the raw-name self-anchor match — the namespace gate now lets it provide its own value
    public void CustomSelfExtension_IsNotHijacked()
    {
        var border = (UIControls.Border)LoadRaw(
            "<Border xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:p=\"clr-namespace:Cursorial.Tests.UI.Xaml.XamlMatrix.Prefixed;assembly=Cursorial.UI.Xaml.Tests\" " +
            "Height=\"{Binding Width, Source={p:Self}}\"/>");
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        Assert.Same(Prefixed.SelfExtension.Sentinel, binding.Source); // the extension's value, NOT the Border
    }

    [Fact] // review: any prefix BOUND to the intrinsics xmlns is the intrinsic ({z:Self} ≡ {x:Self}) — ns, not spelling
    public void IntrinsicsBoundPrefixAlias_ResolvesAsSelf()
    {
        var border = (UIControls.Border)LoadRaw(
            "<Border xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:z=\"https://cursorial.dev/xaml\" Height=\"{Binding Width, Source={z:Self}}\"/>");
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        Assert.Same(border, binding.Source);
    }

    [Fact] // review: {x:Self} routed at a read-only COLLECTION member self-added (an unpositioned adoption crash
    // downstream, or a silent no-op on dictionary-shaped members) — now a positioned rejection
    public void ReadOnlyCollectionMember_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => Load<UIControls.StackPanel>(
            "<StackPanel Children=\"{x:Self}\"/>"));
        Assert.Contains("own collection", ex.Message);
    }

    [Fact] // review: RD Source={x:Self} stringified the token into a phantom URI ("cursorial://…/{x:Self}") — now
    // a positioned rejection at the member, never a misleading missing-resource error
    public void ResourceDictionarySource_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => Load<UIControls.Border>(
            "<Border><Border.Resources><ResourceDictionary Source=\"{x:Self}\"/></Border.Resources></Border>"));
        Assert.Contains("not a valid ResourceDictionary Source", ex.Message);
    }

    [Fact] // review: Style members ({x:Self} on BasedOn/RequiresCapabilities) hit unpositioned reflection errors
    // or deferred seal-time cycles — the style-machinery guard now rejects them at the member, positioned
    public void StyleMember_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => LoadRaw(
            $"<Style{Pre} TargetType=\"Button\" BasedOn=\"{{x:Self}}\">" +
            "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/></Style>"));
        Assert.Contains("style-machinery", ex.Message);
    }

    [Fact] // review: an INIT-ONLY member resolves in the loader (reflection invokes the init accessor) — the
    // documented lane asymmetry: the lowered build fails with an accurate error instead (XSelfLoweringTests)
    public void InitOnlyMember_ResolvesInLoader()
    {
        var host = Load<InitOnlySelfHost>("<InitOnlySelfHost Frozen=\"{x:Self}\"/>");
        Assert.Same(host, host.Frozen);
    }
}
