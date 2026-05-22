using Cursorial.Rendering.Content;

namespace Cursorial.Tests.Rendering;

public class ResourceLoaderTests
{
    // ---- embedded:// scheme ----------------------------------------------------------------

    [Fact]
    public void TryOpen_EmbeddedScheme_FindsKnownResource()
    {
        // Cursorial.Rendering ships standard.flf as an embedded resource. The default loader
        // should resolve embedded://Cursorial.Rendering/<resource-name>.
        var uri = new Uri("embedded://Cursorial.Rendering/Fonts/Embedded/standard.flf");

        using var stream = ResourceLoader.Default.TryOpen(uri);

        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void TryLoadBytes_EmbeddedScheme_ReadsContent()
    {
        var uri = new Uri("embedded://Cursorial.Rendering/Fonts/Embedded/standard.flf");

        // TryLoadBytes is a default-interface method, callable via the IResourceLoader handle.
        IResourceLoader loader = ResourceLoader.Default;
        var bytes = loader.TryLoadBytes(uri);

        Assert.NotNull(bytes);
        // Every FLF file starts with the "flf2a" signature.
        Assert.Equal((byte) 'f', bytes![0]);
        Assert.Equal((byte) 'l', bytes[1]);
        Assert.Equal((byte) 'f', bytes[2]);
    }

    [Fact]
    public void TryOpen_EmbeddedScheme_UnknownAssembly_ReturnsNull()
    {
        var uri = new Uri("embedded://NotARealAssembly/some.resource");
        Assert.Null(ResourceLoader.Default.TryOpen(uri));
    }

    [Fact]
    public void TryOpen_EmbeddedScheme_KnownAssembly_UnknownResource_ReturnsNull()
    {
        var uri = new Uri("embedded://Cursorial.Rendering/does.not.exist.png");
        Assert.Null(ResourceLoader.Default.TryOpen(uri));
    }

    [Fact]
    public void Embedded_BuildsValidUri()
    {
        var uri = ResourceLoader.Embedded("MyAssembly", "My.Resources.foo.png");
        Assert.Equal(ResourceLoader.EmbeddedScheme, uri.Scheme);
        Assert.Equal("myassembly", uri.Host);
        Assert.Equal("/My.Resources.foo.png", Uri.UnescapeDataString(uri.AbsolutePath));
    }

    // ---- file:// scheme --------------------------------------------------------------------

    [Fact]
    public void TryOpen_FileScheme_ReadsTempFile()
    {
        var temp = Path.Combine(Path.GetTempPath(),
                                $"cursorial-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(temp, [1, 2, 3, 4]);
        try
        {
            var uri = new Uri(temp);
            Assert.Equal("file", uri.Scheme);

            using var stream = ResourceLoader.Default.TryOpen(uri);
            Assert.NotNull(stream);

            var buffer = new byte[4];
            stream.ReadExactly(buffer, 0, 4);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void TryOpen_FileScheme_MissingFile_ReturnsNull()
    {
        var uri = new Uri($"file:///does-not-exist/{Guid.NewGuid():N}");
        Assert.Null(ResourceLoader.Default.TryOpen(uri));
    }

    [Fact]
    public void File_BuildsAbsoluteFileUri()
    {
        var tempDir = Path.GetTempPath();
        var path = Path.Combine(tempDir, "test.png");
        var uri = ResourceLoader.File(path);
        Assert.Equal("file", uri.Scheme);
        Assert.True(uri.IsAbsoluteUri);
    }

    // ---- relative URI ----------------------------------------------------------------------

    [Fact]
    public void TryOpen_RelativeUri_ResolvesAgainstBaseDirectory()
    {
        // Drop a temp file inside AppContext.BaseDirectory so the relative resolution finds it.
        var relPath = $"cursorial-rel-{Guid.NewGuid():N}.bin";
        var fullPath = Path.Combine(AppContext.BaseDirectory, relPath);
        File.WriteAllBytes(fullPath, [9, 9, 9]);
        try
        {
            var uri = new Uri(relPath, UriKind.Relative);
            using var stream = ResourceLoader.Default.TryOpen(uri);
            Assert.NotNull(stream);
            Assert.Equal(3, stream.Length);
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Fact]
    public void TryOpen_RelativeUri_MissingFile_ReturnsNull()
    {
        var uri = new Uri($"does-not-exist-{Guid.NewGuid():N}.bin", UriKind.Relative);
        Assert.Null(ResourceLoader.Default.TryOpen(uri));
    }

    // ---- Unknown scheme --------------------------------------------------------------------

    [Fact]
    public void TryOpen_UnknownScheme_ReturnsNull()
    {
        var uri = new Uri("ftp://example.com/foo.png");
        Assert.Null(ResourceLoader.Default.TryOpen(uri));
    }

    [Fact]
    public void TryOpen_NullUri_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ResourceLoader.Default.TryOpen(null!));
    }
}
