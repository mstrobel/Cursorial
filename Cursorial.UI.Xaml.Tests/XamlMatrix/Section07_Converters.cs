using System.Globalization;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §7 (X83–X100): the terminal converter ladder. Unit-level over <see cref="XamlConverters.For"/>.
/// </summary>
public sealed class Section07_Converters : LoaderTestBase
{
    private static XamlValueContext Ctx(Type targetType)
        => new(CultureInfo.InvariantCulture, null, targetType, null, 1, 1);

    private static object? Convert(Type targetType, string text)
        => XamlConverters.For(targetType)!.ConvertFromString(text, Ctx(targetType));

    [Theory]
    [InlineData("2", 2, 2, 2, 2)]
    [InlineData("2,1", 2, 1, 2, 1)]
    [InlineData("1,0,1,0", 1, 0, 1, 0)]
    public void X083_Margins_Arities(string text, int l, int t, int r, int b)
        => Assert.Equal(new Margins(l, t, r, b), Convert(typeof(Margins), text));

    [Fact] // X086a — [Flags] combinations parse through the generic enum ladder (converter-level pins for
           // the RequiresCapabilities boundaries): comma form accepted when every bit is covered; an
           // UNCOVERED numeric rejects; a non-flags enum keeps rejecting combined values (X90 unchanged).
    public void X086a_FlagsEnum_CombinationBoundaries()
    {
        Assert.Equal(StyleCapabilities.NoColor | StyleCapabilities.Motion,
                     Convert(typeof(StyleCapabilities), "NoColor, Motion"));
        Assert.Equal(InteractionState.Focused | InteractionState.PointerOver,
                     Convert(typeof(InteractionState), "Focused, PointerOver")); // a second [Flags] enum

        Assert.Throws<XamlParseException>(() => Convert(typeof(StyleCapabilities), "8192"));  // uncovered bit
        Assert.Throws<XamlParseException>(() => Convert(typeof(StyleCapabilities), "Bogus")); // unknown member
        // Non-flags boundary (pre-existing Enum.TryParse behavior, unchanged): a comma list ORs — one that
        // collapses to a DEFINED member is accepted; one that ORs to an undefined value rejects.
        Assert.Equal(Visibility.Hidden, Convert(typeof(Visibility), "Visible, Hidden"));           // 0|1 → Hidden
        Assert.Throws<XamlParseException>(() => Convert(typeof(Visibility), "Hidden, Collapsed")); // 1|2 = 3 → undefined
    }

    [Fact]
    public void X084_Margins_NegativeComponent()
        => Assert.Equal(new Margins(0, -1, 0, 0), Convert(typeof(Margins), "0,-1,0,0"));

    [Fact]
    public void X085_FractionalCell_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => Convert(typeof(int), "0.5"));
        Assert.Equal(XamlDiagnosticCodes.ConversionFailed, ex.Code);
    }

    [Theory]
    [InlineData("#66d9ef")]
    [InlineData("#fff")]
    [InlineData("#ff000080")]
    public void X086_Color_Hex(string text)
    {
        // Re-pinned 2026-07-13 (proposal-textattributes-decomposition §10): 8-digit hex is
        // #RRGGBBAA — alpha LAST, one convention with Color.TryParseHex/FromRgba and
        // StyleDiagnostics.FormatValue (a deliberate DEV from WPF's #AARRGGBB).
        var color = (Color)Convert(typeof(Color), text)!;
        Assert.Equal(ColorKind.Rgb, color.Kind);
        if (text == "#ff000080")
        {
            Assert.Equal(0x80, color.Alpha);
            Assert.Equal(255, color.Red);
            Assert.Equal(0, color.Green);
            Assert.Equal(0, color.Blue);
        }
    }

    [Theory]
    [InlineData("Red", ColorKind.Palette)]
    [InlineData("Default", ColorKind.Default)]
    [InlineData("Transparent", ColorKind.Rgb)]
    [InlineData("Palette(123)", ColorKind.Palette)]
    public void X087_Color_NamedAndPalette(string text, ColorKind kind)
    {
        var color = (Color)Convert(typeof(Color), text)!;
        Assert.Equal(kind, color.Kind);
        if (text == "Red")
            Assert.Equal(Colors.Red, color);
        if (text == "Palette(123)")
            Assert.Equal((byte)123, color.PaletteIndex);
    }

    [Fact]
    public void X088_Brush_ColorAndSingleton()
    {
        var brush = (IBrush)Convert(typeof(IBrush), "#66d9ef")!;
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(Color.FromHex("#66d9ef"), solid.Color);

        // A named color yields the cached Brushes.* singleton.
        Assert.Same(Brushes.Red, Convert(typeof(IBrush), "Red"));
    }

    [Theory]
    [InlineData("linear:#f92672,#66d9ef", typeof(LinearGradientBrush))]
    [InlineData("radial:#f92672,#66d9ef", typeof(RadialGradientBrush))]
    [InlineData("conic:#f92672,#66d9ef", typeof(ConicGradientBrush))]
    public void X089_Brush_Gradients(string text, Type expected)
        => Assert.IsType(expected, Convert(typeof(IBrush), text));

    [Fact]
    public void X088b_SolidColorBrush_ElementDeclaration_ParameterlessCtorPlusInitColor()
    {
        // The DemoBrush workaround is gone: SolidColorBrush is now directly authorable as a resource
        // ELEMENT (parameterless ctor + init-only Color the loader sets reflectively after activation).
        var brush = Load<SolidColorBrush>("<SolidColorBrush Color=\"#1e212c\"/>");
        Assert.Equal(Color.FromHex("#1e212c"), brush.Color);
        Assert.Equal(1.0, brush.Opacity);
        Assert.Equal(Color.FromHex("#1e212c"), brush.ColorAt(0, 0, new Rect(0, 0, 4, 4)));
    }

    [Fact]
    public void X088c_SolidColorBrush_ElementDeclaration_OpacityInit_FoldsAtSample()
    {
        var brush = Load<SolidColorBrush>("<SolidColorBrush Color=\"#ff0000\" Opacity=\"0.5\"/>");
        Assert.Equal(0.5, brush.Opacity);
        Assert.Equal(Color.FromHex("#ff0000"), brush.Color);     // declared color is raw…
        Assert.Equal(128, brush.ColorAt(0, 0, new Rect(0, 0, 4, 4)).Alpha); // …opacity folds into the sample
    }

    [Fact]
    public void X090_Enum_ByNameAndOrdinal()
    {
        Assert.Equal(Visibility.Collapsed, Convert(typeof(Visibility), "Collapsed"));
        Assert.Throws<XamlParseException>(() => Convert(typeof(Visibility), "Nope"));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void X091_Bool(string text, bool expected)
        => Assert.Equal(expected, Convert(typeof(bool), text));

    [Fact]
    public void X092_IntAndDouble()
    {
        Assert.Equal(10, Convert(typeof(int), "10"));
        Assert.Equal(0.5, Convert(typeof(double), "0.5"));
    }

    [Theory]
    [InlineData("*", UIControls.GridUnitType.Star, 1)]
    [InlineData("2*", UIControls.GridUnitType.Star, 2)]
    [InlineData("Auto", UIControls.GridUnitType.Auto, 0)]
    [InlineData("12", UIControls.GridUnitType.Cell, 12)]
    public void X093_GridLength(string text, UIControls.GridUnitType unit, int magnitude)
    {
        var gl = (UIControls.GridLength)Convert(typeof(UIControls.GridLength), text)!;
        Assert.Equal(unit, gl.UnitType);
        if (unit == UIControls.GridUnitType.Star)
            Assert.Equal(magnitude, gl.StarWeight);
        else if (unit == UIControls.GridUnitType.Cell)
            Assert.Equal(magnitude, gl.Cells);
    }

    [Fact]
    public void X093_GridLength_FractionalThrows()
        => Assert.Throws<XamlParseException>(() => Convert(typeof(UIControls.GridLength), "1.5"));

    [Theory]
    [InlineData("Heavy")]
    [InlineData("Double Rounded")]
    [InlineData("Dashed #888")]
    public void X094_Pen_Presets(string text)
        => Assert.IsType<Pen>(Convert(typeof(Pen), text));

    [Fact] // PRE-PEN: a #hex token folds onto the pen's stroke (previously parsed and discarded)
    public void X094a_Pen_ColorTokenFolds_PreservingPresets()
    {
        var pen = (Pen)Convert(typeof(Pen), "Heavy #66d9ef")!;
        Assert.Equal(StrokeWeight.Heavy, pen.Weight);                           // preset preserved
        var brush = Assert.IsType<SolidColorBrush>(pen.Brush);
        Assert.Equal(Color.FromHex("#66d9ef"), brush.Color);   // …and the color folded in
    }

    [Fact]
    public void X095_TextAttributes_Flags()
        => Assert.Equal(TextAttributes.Bold | TextAttributes.Italic, Convert(typeof(TextAttributes), "Bold,Italic"));

    [Theory]
    [InlineData("Ctrl+S")]
    [InlineData("F5")]
    public void X096_KeyGesture(string text)
    {
        var gesture = (KeyGesture)Convert(typeof(KeyGesture), text)!;
        Assert.Equal(KeyGesture.Parse(text), gesture);
    }

    [Fact]
    public void X100_Registry_RegisterOverride()
    {
        Assert.NotNull(XamlConverters.For(typeof(Margins)));

        var custom = new ConstConverter();
        XamlConverters.Register(typeof(SentinelTarget), custom);
        Assert.Same(custom, XamlConverters.For(typeof(SentinelTarget)));
    }

    // ── Uri converter (Image.SourceUri — the XAML-friendly declarative image source, CD-P2K-2) ──

    [Theory]
    [InlineData("embedded://App/logo.png")]
    [InlineData("file:///tmp/pic.png")]
    [InlineData("Icons/logo.png")] // a bare relative path is valid (RelativeOrAbsolute)
    public void X101_Uri_Parses(string text)
        => Assert.Equal(new Uri(text, UriKind.RelativeOrAbsolute), Convert(typeof(Uri), text));

    [Fact] // end-to-end: an Image's SourceUri attribute converts to a Uri through the loader
    public void X102_Image_SourceUriAttribute_Converts()
    {
        var image = Loader.Load<UIControls.Image>(Parse("<Image SourceUri=\"embedded://App/logo.png\"/>"));
        Assert.Equal(new Uri("embedded://App/logo.png"), image.SourceUri);
    }

    private sealed class SentinelTarget { }

    private sealed class ConstConverter : ITypeConverter
    {
        public bool IsContextFree => true;
        public object ConvertFromString(string text, in XamlValueContext context) => text;
    }
}
