using Cursorial.Core.Output;

namespace Cursorial.Core.Tests.Output;

public class StyleTests
{
    [Fact]
    public void Default_IsTrulyDefault()
    {
        var s = Style.Default;
        Assert.True(s.IsDefault);
        Assert.True(s.Foreground.IsDefault);
        Assert.True(s.Background.IsDefault);
        Assert.Equal(TextAttributes.None, s.Attributes);
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
        var s = Style.Default
            .WithBackground(Color.FromPalette(2))
            .WithAttributes(TextAttributes.Bold);

        var s2 = s.WithForeground(Color.FromRgb(255, 0, 0));

        Assert.Equal(Color.FromRgb(255, 0, 0), s2.Foreground);
        Assert.Equal(Color.FromPalette(2), s2.Background);
        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void AddAttributes_OrsIntoExisting()
    {
        var s = Style.Default.WithAttributes(TextAttributes.Bold);
        var s2 = s.AddAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold | TextAttributes.Italic, s2.Attributes);
    }

    [Fact]
    public void RemoveAttributes_ClearsBits()
    {
        var s = Style.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Italic);
        var s2 = s.RemoveAttributes(TextAttributes.Italic);

        Assert.Equal(TextAttributes.Bold, s2.Attributes);
    }

    [Fact]
    public void Equality_IsComponentWise()
    {
        var a = Style.Default
            .WithForeground(Color.FromRgb(1, 2, 3))
            .WithAttributes(TextAttributes.Bold);
        var b = Style.Default
            .WithForeground(Color.FromRgb(1, 2, 3))
            .WithAttributes(TextAttributes.Bold);

        Assert.Equal(a, b);
        Assert.NotEqual(a, b.WithAttributes(TextAttributes.Italic));
    }
}
