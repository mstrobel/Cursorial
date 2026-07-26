using Cursorial.UI.Dialogs;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// <see cref="FileDialogFilter"/>'s two jobs: the glob matcher that narrows a listing, and the value-equality
/// that lets a caller compare a returned filter against a well-known static.
/// </summary>
public sealed class FileDialogFilterTests
{
    [Theory]
    [InlineData("*.png", "hero-banner.png", true)]
    [InlineData("*.png", "HERO-BANNER.PNG", true)]  // case-insensitive: hiding PHOTO.PNG from *.png is a bug
    [InlineData("*.png", "logo.svg", false)]
    [InlineData("*.jpg;*.jpeg", "thumbnail.jpeg", true)]
    [InlineData("*.jpg;*.jpeg", "thumbnail.jpg", true)]
    [InlineData("*.jpg;*.jpeg", "thumbnail.png", false)]
    [InlineData("*.*", "LICENSE", true)]           // '*' spans a dot-less name too
    [InlineData("*.*", "archive.tar.gz", true)]
    [InlineData("report?.md", "report1.md", true)]
    [InlineData("report?.md", "report10.md", false)]
    public void Matches_HonoursGlobsAndCase(string pattern, string fileName, bool expected)
        => Assert.Equal(expected, new FileDialogFilter("Test", pattern).Matches(fileName));

    [Theory]
    [InlineData("*.png", ".png")]
    [InlineData("*.jpg;*.jpeg", ".jpg")] // the first glob is the preferred spelling
    [InlineData("*.*", null)]
    [InlineData("*", null)]
    [InlineData("*.?g", null)]           // a family, not an extension — never appended to a name
    public void DefaultExtension_OnlyResolvesASingleLiteralExtension(string pattern, string? expected)
        => Assert.Equal(expected, new FileDialogFilter("Test", pattern).DefaultExtension);

    [Fact]
    public void DisplayText_IsTheSelectorRow()
        => Assert.Equal("Images (*.png;*.jpg;*.svg)", new FileDialogFilter("Images", "*.png;*.jpg;*.svg").DisplayText);

    [Fact]
    public void AllFiles_ComparesByValue_SoCallersCanTestTheWellKnownStatic()
    {
        Assert.Equal(FileDialogFilter.AllFiles, new FileDialogFilter("All Files", "*.*"));
        Assert.True(FileDialogFilter.AllFiles == new FileDialogFilter("All Files", "*.*"));
        Assert.NotEqual(FileDialogFilter.AllFiles, new FileDialogFilter("All Files", "*"));
    }
}
