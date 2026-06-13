using System.Globalization;
using Cursorial.UI;
using Cursorial.UI.Data;
using Xunit;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §6 — the forward value pipeline (B55–B70).</summary>
public class Section06_ForwardPipeline
{
    public Section06_ForwardPipeline()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B055_ExactAssignable_NoConversion()
    {
        var vm = new Vm { Age = 7 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Age"));
        Assert.Equal(7, w.Num);
    }

    [Fact]
    public void B056_StringToInt_ConversionLadder()
    {
        var vm = new Vm { Name = "42" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Name"));
        Assert.Equal(42, w.Num);
    }

    [Fact]
    public void B057_ConversionFails_TypeMismatch_Unset()
    {
        var vm = new Vm { Name = "notanint" };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.NumProperty, new Binding("Name"));

        Assert.Equal(0, w.Num); // SetUnset → default
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.TypeMismatch);
    }

    [Fact]
    public void B058_Converter()
    {
        var vm = new Vm { Age = 3 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Age") { Converter = new HashConverter() });
        Assert.Equal("#3", w.Text);
    }

    [Fact]
    public void B059_ConverterParameter()
    {
        var vm = new Vm { Age = 3 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Age") { Converter = new ParamConverter(), ConverterParameter = "x" });
        Assert.Equal("3/x", w.Text);
    }

    [Fact]
    public void B060_ConverterThrows_FallbackOrUnset()
    {
        var vm = new Vm { Age = 3 };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.TextProperty, new Binding("Age") { Converter = new ThrowingConverter(), FallbackValue = "fb" });
        Assert.Equal("fb", w.Text);
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.ConversionFailed);
    }

    [Fact]
    public void B061_ConverterReturnsUnset_FallbackOrUnset()
    {
        var vm = new Vm { Age = 3 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Age") { Converter = new UnsetConverter(), FallbackValue = "fb" });
        Assert.Equal("fb", w.Text);
    }

    [Fact]
    public void B062_StringFormat()
    {
        var vm = new Vm { Name = "Bob" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { StringFormat = "Editing: {0}" });
        Assert.Equal("Editing: Bob", w.Text);
    }

    [Fact]
    public void B063_StringFormat_SkippedForNonStringTarget()
    {
        var vm = new Vm { Name = "5" };
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.NumProperty, new Binding("Name") { StringFormat = "x{0}" });
        Assert.Equal(5, w.Num); // StringFormat skipped; value flows through type conversion

        // …and a Warning trace explains the silent skip (finding 9).
        Assert.Contains(BindingDiagnostics.RecentEvents,
            e => e.Level == BindingTraceLevel.Warning && e.Message.Contains("StringFormat") && e.Message.Contains("ignored"));
    }

    [Fact]
    public void B064_TargetNullValue()
    {
        var vm = new Vm { Name = null };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { TargetNullValue = "<none>" });
        Assert.Equal("<none>", w.Text);
    }

    [Fact]
    public void B065_TargetNullValue_ThenStringFormat()
    {
        var vm = new Vm { Name = null };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { TargetNullValue = "<none>", StringFormat = "[{0}]" });
        Assert.Equal("[<none>]", w.Text);
    }

    [Fact]
    public void B066_BrokenPath_NoFallback_Unset()
    {
        var vm = new Vm { Sub = null };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name"));
        Assert.Null(w.Text); // SetUnset → default (null)
    }

    [Fact]
    public void B067_BrokenPath_Fallback_ThenRebinds()
    {
        var vm = new Vm { Sub = null };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name") { FallbackValue = "fb" });
        Assert.Equal("fb", w.Text);

        vm.Sub = new Vm { Name = "now" };
        Assert.Equal("now", w.Text); // fallback withdrawn
    }

    [Fact]
    public void B068_FallbackTypedDifferently_ConvertsToTargetType()
    {
        var vm = new Vm { Sub = null };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name") { FallbackValue = 5 });
        Assert.Equal("5", w.Text); // fallback runs through type conversion
    }

    [Fact]
    public void B069_EnumTarget_FromStringName()
    {
        var vm = new Vm { Name = "Italic" };
        var ew = new EnumWidget { DataContext = vm };
        ew.SetBinding(EnumWidget.FontStyleProperty, new Binding("Name"));
        Assert.Equal(FontStyleish.Italic, ew.FontStyle);
    }

    [Fact]
    public void B070_AffectsComposite_RepeatedWrites_StoreShortCircuit()
    {
        var vm = new Vm { Age = 1 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.OffProperty, new Binding("Age"));
        Assert.Equal(1, w.Off);

        for (var i = 2; i <= 5; i++)
        {
            vm.Age = i;
            Assert.Equal(i, w.Off);
        }
    }

    private sealed class HashConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => $"#{value}";
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
    }

    private sealed class ParamConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => $"{value}/{parameter}";
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
    }

    private sealed class ThrowingConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new InvalidOperationException("boom");
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
    }

    private sealed class UnsetConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => UIProperty.UnsetValue;
    }
}

/// <summary>An enum used by B69's enum-target conversion row.</summary>
public enum FontStyleish { Normal, Italic, Oblique }

/// <summary>A target with an enum-typed property (B69).</summary>
public sealed class EnumWidget : UIElement
{
    public static readonly StyledProperty<FontStyleish> FontStyleProperty =
        UIProperty.Register<EnumWidget, FontStyleish>("FontStyle");

    public FontStyleish FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }
}
