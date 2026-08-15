using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// Value-type (record struct) element authoring (loader capability behind the XAML palette's <c>&lt;Pen&gt;</c>):
/// <see cref="ReflectionXamlMetadata"/> activates a value type via <c>Activator.CreateInstance</c> (its implicit
/// parameterless ctor is not surfaced by <c>GetConstructor</c>), and the builder sets each <c>init</c> member via
/// reflection on the SAME boxed instance — so multiple non-default members all persist (boxed-struct-safe).
/// </summary>
public sealed class XamlValueTypeElementTests
{
    private static readonly XamlLoader Loader = new();

    // Pen lives in Cursorial.Drawing.Media, part of the default UI uri namespace — authored prefix-free as <Pen>.
    private const string Ns =
        " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // Multiple non-default init members on one boxed struct all persist (the mutation isn't lost to a copy).
    public void PenElement_SetsAllInitMembers_OnTheBoxedStruct()
    {
        var pen = Loader.Load<Pen>(
            "<Pen" + Ns + " Brush=\"#7aa2f7\" Weight=\"Heavy\" GlyphSet=\"Ascii\" Corners=\"Rounded\"/>");

        Assert.Equal(StrokeWeight.Heavy, pen.Weight);
        Assert.Equal(GlyphSet.Ascii, pen.GlyphSet);
        Assert.Equal(CornerStyle.Rounded, pen.Corners);
        Assert.Equal(Color.FromHex("#7aa2f7"), Assert.IsType<SolidColorBrush>(pen.Brush).Color);
    }

    [Fact] // A value-type element with no members set default-constructs (no spurious failure) — equals default(Pen).
    public void PenElement_NoMembers_IsDefault()
    {
        var pen = Loader.Load<Pen>("<Pen" + Ns + "/>");
        Assert.Equal(default, pen);
    }
}
