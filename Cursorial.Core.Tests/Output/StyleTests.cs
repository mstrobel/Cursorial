using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class StyleTests
{
    [Fact]
    public void Transparent_IsTheCompositingIdentity()
    {
        var s = Style.Transparent;

        Assert.True(s.Foreground.IsTransparent);
        Assert.True(s.Background.IsTransparent);
        Assert.True(s.UnderlineColor.IsTransparent);

        // Distinct from Default (which paints terminal-default colors opaquely).
        Assert.NotEqual(Style.Default, s);

        // Hyperlink left at the default (None) so a transparent cell carries no link.
        Assert.Equal(default(Hyperlink), s.Hyperlink);
    }

    [Fact]
    public void Transparent_ContributesNothingWhenCompositedAsSource()
    {
        // The whole point: a Transparent-styled cell's colors composite to the backdrop verbatim.
        var backdrop = Color.FromRgb(10, 20, 30);

        Assert.Equal(backdrop, Color.Composite(Style.Transparent.Background, backdrop, BlendingModes.Default));
        Assert.Equal(backdrop, Color.Composite(Style.Transparent.Foreground, backdrop, BlendingModes.Default));
    }

    [Fact]
    public void Default_IsTrulyDefault()
    {
        Style s = Style.Default;
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
        Style s = default;
        Assert.Equal(Style.Default, s);
    }

    [Fact]
    public void WithForeground_PreservesEverythingElse()
    {
        Style s = Style.Default
                       .WithBackground(Color.FromPalette(2))
                       .WithAttributes(TextAttributes.Bold);

        Style s2 = s.WithForeground(Color.FromRgb(255, 0, 0));

        Assert.Equal(Color.FromRgb(255, 0, 0), s2.Foreground);
        Assert.Equal(Color.FromPalette(2), s2.Background);
        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void AddAttributes_OrsIntoExisting()
    {
        Style s = Style.Default.WithAttributes(TextAttributes.Bold);
        Style s2 = s.AddAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold | TextAttributes.Italic, s2.Attributes);
    }

    [Fact]
    public void RemoveAttributes_ClearsBits()
    {
        Style s = Style.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Italic);
        Style s2 = s.RemoveAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void Equality_IsComponentWise()
    {
        Style a = Style.Default
                       .WithForeground(Color.FromRgb(1, 2, 3))
                       .WithAttributes(TextAttributes.Bold);

        Style b = Style.Default
                       .WithForeground(Color.FromRgb(1, 2, 3))
                       .WithAttributes(TextAttributes.Bold);

        Assert.Equal(a, b);
        Assert.NotEqual(a, b.WithAttributes(TextAttributes.Italic));
    }
}