using System.Globalization;

using Cursorial.UI;
using Cursorial.UI.Data;

using UIControls = Cursorial.UI.Controls;
using Binding = Cursorial.UI.Data.Binding;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The binding SHAPING members the loader's hand-written BuildBinding / AttachTemplateBinding previously DROPPED
/// (a member is settable on the Binding hierarchy but was never read from the markup): ConverterParameter,
/// ConverterCulture, UpdateSourceTrigger, TargetNullValue, Trace. The generator's ShapingInits and the loader now
/// read the same set in lockstep. These prove the loader sets each; the generator lowers the identical set
/// (BindingShapingLoweringTests).
/// </summary>
public sealed class Section17_BindingShaping : LoaderTestBase
{
    private static Binding WidthBinding(UIControls.Border border)
        => (Binding)BindingOperations.GetBindingExpression(border, UIElement.WidthProperty)!.ParentBinding!;

    [Fact] // every shaping member set from the markup is carried onto the Binding descriptor (was silently dropped)
    public void Binding_ShapingMembers_AllCarried()
    {
        var border = Load<UIControls.Border>(
            "<Border Width=\"{Binding Value, ConverterParameter=42, ConverterCulture=en-US, " +
            "UpdateSourceTrigger=LostFocus, TargetNullValue=none, Trace=True}\"/>");

        var b = WidthBinding(border);
        Assert.Equal("42", b.ConverterParameter);                            // object? — the raw string the converter reads
        Assert.Equal(CultureInfo.GetCultureInfo("en-US"), b.ConverterCulture);
        Assert.Equal(UpdateSourceTrigger.LostFocus, b.UpdateSourceTrigger);
        Assert.Equal("none", b.TargetNullValue);
        Assert.True(b.Trace);
    }

    [Fact] // TargetNullValue specifically — the Shell.xaml case (TargetNullValue='No Selection') the loader dropped
    public void Binding_TargetNullValue_IsCarried()
    {
        var border = Load<UIControls.Border>("<Border Width=\"{Binding Value, TargetNullValue=No Selection}\"/>");
        Assert.Equal("No Selection", WidthBinding(border).TargetNullValue);
    }

    [Fact] // an absent shaping member leaves the Binding default (not a spurious value)
    public void Binding_AbsentShaping_KeepsDefaults()
    {
        var border = Load<UIControls.Border>("<Border Width=\"{Binding Value}\"/>");
        var b = WidthBinding(border);
        Assert.Null(b.ConverterParameter);
        Assert.Null(b.ConverterCulture);
        Assert.Equal(UpdateSourceTrigger.Default, b.UpdateSourceTrigger);
        Assert.Same(UIProperty.UnsetValue, b.TargetNullValue);
        Assert.False(b.Trace);
    }
}
