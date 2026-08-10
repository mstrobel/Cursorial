using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Tests.Rendering;

public class IconTests
{
    // Real embedded resource we can rely on existing — Cursorial.Rendering ships the standard
    // FIGlet font as an embedded resource and we can reuse it as opaque bytes for tests that
    // only care about load success / failure paths.
    private static Uri KnownEmbeddedUri =>
        ResourceLoader.Embedded("Cursorial.Rendering",
                                "Fonts/Embedded/standard.flf");

    private static Uri UnknownEmbeddedUri =>
        ResourceLoader.Embedded("Cursorial.Rendering", "does.not.exist.png");

    // ---- Loading -----------------------------------------------------------------

    [Fact]
    public void Construction_LoadsImageWhenUriResolves()
    {
        var icon = new Icon(KnownEmbeddedUri, fallbackGlyph: "⚙️");
        Assert.True(icon.ImageLoaded);
    }

    [Fact]
    public void Construction_FallsBackWhenUriDoesntResolve()
    {
        var icon = new Icon(UnknownEmbeddedUri, fallbackGlyph: "⚙️");
        Assert.False(icon.ImageLoaded);
    }

    [Fact]
    public void Construction_DefaultsRenderSizeTo2x1()
    {
        var icon = new Icon(UnknownEmbeddedUri, fallbackGlyph: "⚙️");
        Assert.Equal(new Size(2, 1), icon.RenderSize);
    }

    [Fact]
    public void Construction_RespectsExplicitRenderSize()
    {
        var icon = new Icon(UnknownEmbeddedUri, fallbackGlyph: "⚙️", renderSize: new Size(4, 2));
        Assert.Equal(new Size(4, 2), icon.RenderSize);
    }

    [Fact]
    public void Construction_NullUri_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Icon(null!, fallbackGlyph: "x"));
    }

    [Fact]
    public void Construction_NullGlyph_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Icon(UnknownEmbeddedUri, fallbackGlyph: null!));
    }

    // ---- Format inference --------------------------------------------------------

    [Theory]
    [InlineData("img/foo.png", ImageFormat.Png)]
    [InlineData("img/foo.PNG", ImageFormat.Png)]
    [InlineData("img/foo.jpg", ImageFormat.Jpeg)]
    [InlineData("img/foo.jpeg", ImageFormat.Jpeg)]
    [InlineData("img/foo.gif", ImageFormat.Gif)]
    [InlineData("img/no-extension", ImageFormat.Png)] // unknown defaults to PNG
    public void Format_InferredFromUriExtension(string path, ImageFormat expected)
    {
        var icon = new Icon(new Uri(path, UriKind.Relative), fallbackGlyph: "x");
        Assert.Equal(expected, icon.Format);
    }

    [Fact]
    public void Format_ExplicitOverrideWins()
    {
        var icon = new Icon(new Uri("img/foo.png", UriKind.Relative),
                            fallbackGlyph: "x",
                            format: ImageFormat.Gif);
        Assert.Equal(ImageFormat.Gif, icon.Format);
    }

    // ---- Convenience factories ---------------------------------------------------

    [Fact]
    public void FromEmbedded_ResolvesViaEmbeddedScheme()
    {
        var icon = Icon.FromEmbedded(
            assemblyName: "Cursorial.Rendering",
            resourceName: "Fonts/Embedded/standard.flf",
            fallbackGlyph: "⚙️");

        Assert.True(icon.ImageLoaded);
        Assert.Equal("embedded", icon.ResourceUri!.Scheme);
    }

    [Fact]
    public void FromFile_AbsolutePath_ProducesFileUri()
    {
        // Use a temp file we know exists.
        var temp = Path.Combine(Path.GetTempPath(),
                                $"cursorial-icon-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(temp, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG signature
        try
        {
            var icon = Icon.FromFile(temp, fallbackGlyph: "x");
            Assert.True(icon.ImageLoaded);
            Assert.Equal("file", icon.ResourceUri!.Scheme);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // ---- Paint --------------------------------------------------------------------

    [Fact]
    public void Paint_WithKittyCapability_AttachesKittyFragment()
    {
        var icon = new Icon(KnownEmbeddedUri, fallbackGlyph: "⚙️");
        var buffer = new CellBuffer(10, 3);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false),
                   };
        icon.Paint(buffer, 0, 0, default, caps);

        Assert.Single(buffer.Fragments);
        Assert.IsType<KittyImageFragment>(buffer.Fragments[(0, 0)].Fragment);
    }

    [Fact]
    public void Paint_WithoutGraphicsCaps_RendersGlyphPlaceholder()
    {
        var renderSize = new Size(3, 1);

        var icon = new Icon(KnownEmbeddedUri,
                            fallbackGlyph: "X",
                            renderSize: renderSize);
        var buffer = new CellBuffer(10, 3);

        icon.Paint(buffer, new Rect(0, 0, renderSize), default, OutputCapabilities.None);

        Assert.Empty(buffer.Fragments);
        // The fallback "X" should appear somewhere in the painted region.
        bool found = false;
        for (int c = 0; c < 3 && !found; c++)
            if (buffer[c, 0].Grapheme == "X") found = true;
        Assert.True(found);
    }

    [Fact]
    public void Paint_LoadFailed_StillRendersGlyph()
    {
        var renderSize = new Size(3, 1);

        var icon = new Icon(UnknownEmbeddedUri,
                            fallbackGlyph: "X",
                            renderSize: renderSize);
        var buffer = new CellBuffer(10, 3);

        icon.Paint(buffer, new Rect(0, 0, renderSize),
                   default, OutputCapabilities.None with
                                  {
                                      Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true,
                                                                          ITerm2InlineImages: false),
                                  });

        // No image loaded → no fragment, even though Kitty is reported supported.
        Assert.Empty(buffer.Fragments);
        bool found = false;
        for (int c = 0; c < 3 && !found; c++)
            if (buffer[c, 0].Grapheme == "X") found = true;
        Assert.True(found);
    }

    // ---- Custom loader -----------------------------------------------------------

    [Fact]
    public void CustomLoader_IsHonored()
    {
        // A loader that always returns a small byte buffer regardless of URI.
        var loader = new StubLoader();
        var icon = new Icon(new Uri("custom://anywhere"),
                            fallbackGlyph: "x",
                            loader: loader);

        Assert.Same(loader, icon.Loader);
        Assert.True(icon.ImageLoaded);
        Assert.Equal(1, loader.OpenCount);
    }

    private sealed class StubLoader : IResourceLoader
    {
        public int OpenCount { get; private set; }

        public Stream TryOpen(Uri uri, out Exception? error)
        {
            OpenCount++;
            error = null;
            return new MemoryStream([0x89, 0x50, 0x4E, 0x47]); // four bytes — content irrelevant
        }
    }
}
