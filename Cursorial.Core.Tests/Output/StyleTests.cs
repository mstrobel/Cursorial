using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Tests.Output;

public class StyleTests
{
    [Fact]
    public void Transparent_IsTheCompositingIdentity()
    {
        var s = CellStyle.Transparent;

        Assert.True(s.Foreground.IsTransparent);
        Assert.True(s.Background.IsTransparent);
        Assert.True(s.UnderlineColor.IsDefault); // Default UnderlineColor means "use Foreground", so no need to make
                                                 // transparent. It creates problems during compositing.

        // Distinct from Default (which paints terminal-default colors opaquely).
        Assert.NotEqual(CellStyle.Default, s);

        // Hyperlink left at the default (None) so a transparent cell carries no link.
        Assert.Equal(default, s.Hyperlink);
    }

    [Fact]
    public void Transparent_ContributesNothingWhenCompositedAsSource()
    {
        // The whole point: a Transparent-styled cell's colors composite to the backdrop verbatim.
        var backdrop = Color.FromRgb(10, 20, 30);

        Assert.Equal(backdrop, Color.Composite(CellStyle.Transparent.Background, backdrop, BlendingModes.Default));
        Assert.Equal(backdrop, Color.Composite(CellStyle.Transparent.Foreground, backdrop, BlendingModes.Default));
    }

    [Fact]
    public void Default_IsTrulyDefault()
    {
        CellStyle s = CellStyle.Default;
        Assert.True(s.IsDefault);
        Assert.True(s.Foreground.IsDefault);
        Assert.True(s.Background.IsDefault);
        Assert.Equal(TextAttributes.None, s.Attributes);
        Assert.Equal(default, s.UnderlineStyle);
        Assert.True(s.UnderlineColor.IsDefault);
    }

    [Fact]
    public void DefaultConstructed_EqualsDefault()
    {
        CellStyle s = default;
        Assert.Equal(CellStyle.Default, s);
    }

    [Fact]
    public void WithForeground_PreservesEverythingElse()
    {
        CellStyle s = CellStyle.Default
                       .WithBackground(Color.FromPalette(2))
                       .WithAttributes(TextAttributes.Bold);

        CellStyle s2 = s.WithForeground(Color.FromRgb(255, 0, 0));

        Assert.Equal(Color.FromRgb(255, 0, 0), s2.Foreground);
        Assert.Equal(Color.FromPalette(2), s2.Background);
        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void AddAttributes_OrsIntoExisting()
    {
        CellStyle s = CellStyle.Default.WithAttributes(TextAttributes.Bold);
        CellStyle s2 = s.AddAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold | TextAttributes.Italic, s2.Attributes);
    }

    [Fact]
    public void RemoveAttributes_ClearsBits()
    {
        CellStyle s = CellStyle.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Italic);
        CellStyle s2 = s.RemoveAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void Equality_IsComponentWise()
    {
        CellStyle a = CellStyle.Default
                       .WithForeground(Color.FromRgb(1, 2, 3))
                       .WithAttributes(TextAttributes.Bold);

        CellStyle b = CellStyle.Default
                       .WithForeground(Color.FromRgb(1, 2, 3))
                       .WithAttributes(TextAttributes.Bold);

        Assert.Equal(a, b);
        Assert.NotEqual(a, b.WithAttributes(TextAttributes.Italic));
    }
}