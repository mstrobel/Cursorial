using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C23 — Image hosting: ImagePresenter (primitive) + Image (control). The presenter draws a
// graphics-protocol image (Cursorial.Rendering.Content.Image — the IBufferFragment path) when a Source is set AND a
// graphics protocol is negotiated; otherwise a placeholder (content/template) shows. ClipToBounds clips the image.
public sealed class Section35_Image
{
    // Fake PNG bytes — the Kitty fragment base64s the bytes without decoding, so the signature head + an explicit
    // RequestedSize is enough (mirrors Cursorial.Rendering.Tests.ImageContentTests).
    private static ImageData Img(int cols = 10, int rows = 5) =>
        new([137, 80, 78, 71], ImageFormat.Png, new Size(cols, rows));

    private static readonly byte[] KittyApcIntroducer = [0x1b, 0x5f, 0x47]; // ESC _ G

    // Step frames (the diff renderer emits a fragment only on the frame it first paints it — a later clean frame
    // would not), returning true if any frame's bytes carry the Kitty graphics APC introducer. Requires CaptureFrameBytes.
    private static bool RenderEmitsKittyApc(UIHeadlessHost host, int maxFrames = 8)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            host.RunFrame();
            if (host.LastFrameBytes.Span.IndexOf(KittyApcIntroducer) >= 0)
                return true;
        }

        return false;
    }

    private static UIHeadlessHost KittyHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12), Capabilities = HeadlessCapabilities.KittyGraphics, CaptureFrameBytes = true });

    [Fact] // C23.1: an ImagePresenter with a Source under Kitty graphics measures to the image's natural cell size
    public void C23_1_MeasuresToNaturalSize()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter
        {
            Source = Img(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.True(presenter.IsImageVisible);
        Assert.Equal(10, presenter.DesiredSize.Columns);
        Assert.Equal(5, presenter.DesiredSize.Rows);
        Assert.Equal(Visibility.Collapsed, presenter.PlaceholderPresenter.Visibility); // placeholder hidden
    }

    [Fact] // C23.2: no Source ⇒ the placeholder renders (its ContentPresenter child is Visible)
    public void C23_2_NoSourceShowsPlaceholder()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter { PlaceholderContent = "no image" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.False(presenter.IsImageVisible);
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
        Assert.NotNull(presenter.PlaceholderPresenter.Child); // the realized "no image" TextBlock
    }

    [Fact] // C23.3: a Source but NO graphics protocol (Ansi16Legacy) ⇒ the placeholder shows (image can't render)
    public void C23_3_NoGraphicsShowsPlaceholder()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12), Capabilities = HeadlessCapabilities.Ansi16Legacy, CaptureFrameBytes = true });
        var presenter = new ImagePresenter { Source = Img(), PlaceholderContent = "no image" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.False(presenter.IsImageVisible);
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
        Assert.False(RenderEmitsKittyApc(host)); // no graphics protocol — nothing emitted
    }

    [Fact] // C23.4: under Kitty graphics, rendering the presenter emits the Kitty graphics APC
    public void C23_4_EmitsKittyApc()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter
        {
            Source = Img(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);

        Assert.True(RenderEmitsKittyApc(host)); // the ESC _ G Kitty graphics introducer reached the wire
    }

    [Fact] // C23.5: a placeholder DataTemplate keyed by content type realizes the placeholder visual
    public void C23_5_PlaceholderByTypeTemplate()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter { PlaceholderContent = new PlaceholderVm() };
        presenter.Resources[new DataTemplateKey(typeof(PlaceholderVm))] =
            new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("BROKEN")) };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.IsType<TextBlock>(presenter.PlaceholderPresenter.Child);
        Assert.Contains("BROKEN", host.GetRowText(0));
    }

    [Fact] // C23.6: the presenter is ClipToBounds (the image is clipped to its layout rect)
    public void C23_6_ClipToBounds()
    {
        var presenter = new ImagePresenter();
        Assert.True(presenter.ClipToBounds);
    }

    [Fact] // C23.7: the :placeholder pseudo-class + placeholder visibility flip with the image-visible state
    public void C23_7_PlaceholderStateFlips()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter { PlaceholderContent = "no image" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.True(presenter.HasCustomPseudoClass(":placeholder"));
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);

        presenter.Source = Img(); // now an image renders
        host.RunUntilIdle();
        Assert.False(presenter.HasCustomPseudoClass(":placeholder"));
        Assert.Equal(Visibility.Collapsed, presenter.PlaceholderPresenter.Visibility);
        Assert.True(presenter.IsImageVisible);
    }

    [Fact] // C23.8: the Image control's template hosts a PART_ImagePresenter the Source flows to; the APC emits
    public void C23_8_ImageControlHostsPresenter()
    {
        using var host = KittyHost();
        var image = new Image
        {
            Source = Img(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(image);
        var emitted = RenderEmitsKittyApc(host); // steps frames: applies the template + paints the image

        Assert.NotNull(image.PresenterPart);
        Assert.Same(image.Source, image.PresenterPart!.Source); // TemplateBound control → presenter
        Assert.True(emitted);
    }

    [Fact] // C23.9: the Image control with no Source shows the placeholder through its presenter
    public void C23_9_ImageControlPlaceholder()
    {
        using var host = KittyHost();
        var image = new Image { PlaceholderContent = "none" };
        host.ShowRoot(image);
        host.RunUntilIdle();

        Assert.NotNull(image.PresenterPart);
        Assert.False(image.PresenterPart!.IsImageVisible);
        Assert.Equal(Visibility.Visible, image.PresenterPart.PlaceholderPresenter.Visibility);
    }

    [Fact] // C23.10: setting Source from null flips placeholder → image live (a re-render emits the APC)
    public void C23_10_SourceSetLiveRenders()
    {
        using var host = KittyHost();
        var image = new Image
        {
            PlaceholderContent = "none",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(image);
        host.RunUntilIdle();

        image.Source = Img(8, 4); // set the source after show
        Assert.True(RenderEmitsKittyApc(host)); // the next painting frame emits the APC
        Assert.True(image.PresenterPart!.IsImageVisible);
    }

    // ── audit regressions (CD-P2K-1 audit) ──────────────────────────────────────────────────────────────

    // A minimal real PNG: the 8-byte signature + an IHDR chunk (width/height). DecodeSize reads only the IHDR (no CRC
    // check), so this is enough to size the image (the earlier fake 4-byte head has no IHDR ⇒ unknown size).
    private static byte[] MinimalPng(int w, int h)
    {
        var bytes = new byte[8 + 8 + 13 + 4];
        ReadOnlySpan<byte> sig = [137, 80, 78, 71, 13, 10, 26, 10];
        sig.CopyTo(bytes);
        var o = 8;
        bytes[o++] = 0; bytes[o++] = 0; bytes[o++] = 0; bytes[o++] = 13;             // IHDR length = 13
        bytes[o++] = (byte)'I'; bytes[o++] = (byte)'H'; bytes[o++] = (byte)'D'; bytes[o++] = (byte)'R';
        bytes[o++] = (byte)(w >> 24); bytes[o++] = (byte)(w >> 16); bytes[o++] = (byte)(w >> 8); bytes[o++] = (byte)w;
        bytes[o++] = (byte)(h >> 24); bytes[o++] = (byte)(h >> 16); bytes[o++] = (byte)(h >> 8); bytes[o++] = (byte)h;
        bytes[o++] = 8; bytes[o++] = 6; bytes[o] = 0; // bit depth 8, color type 6 (RGBA), rest 0; CRC bytes stay 0
        return bytes;
    }

    [Fact] // C23.11 (audit): a Source with a NULL RequestedSize still renders (sized from the decoded pixels), not 0×0
    public void C23_11_NullRequestedSizeStillRenders()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter
        {
            Source = new ImageData(MinimalPng(80, 80), ImageFormat.Png), // no RequestedSize — sized from 80×80 px ÷ 8×16 cell
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        var emitted = RenderEmitsKittyApc(host); // step frames: lays out + paints the painting frame

        Assert.True(presenter.IsImageVisible);
        Assert.True(presenter.DesiredSize.Columns > 0); // never collapses to a 0 extent (the audit bug)
        Assert.True(presenter.DesiredSize.Rows > 0);
        Assert.True(emitted);
    }

    [Fact] // C23.12 (audit): a single-axis RequestedSize (the other axis 0) renders, not 0 on the unconstrained axis
    public void C23_12_SingleAxisRequestedSizeRenders()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter
        {
            Source = new ImageData(MinimalPng(80, 80), ImageFormat.Png, new Size(8, 0)), // columns only; rows from aspect
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.True(presenter.IsImageVisible);
        Assert.True(presenter.DesiredSize.Columns > 0);
        Assert.True(presenter.DesiredSize.Rows > 0); // the rows axis is derived/floored, never 0
    }

    [Fact] // C23.13 (audit): IsImageVisible is FORMAT-aware — a Kitty-only terminal + a JPEG source shows the placeholder
    public void C23_13_FormatIncompatibleShowsPlaceholder()
    {
        using var host = KittyHost(); // Kitty graphics only (no iTerm2) — can't carry JPEG
        var presenter = new ImagePresenter
        {
            Source = new ImageData([1, 2, 3, 4], ImageFormat.Jpeg, new Size(10, 5)),
            PlaceholderContent = "no image",
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.False(presenter.IsImageVisible); // Kitty can't carry JPEG ⇒ the author's placeholder, not the [image] text
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
        Assert.False(RenderEmitsKittyApc(host));
    }

    [Fact] // C23.14 (audit): a capability flip (renegotiation) re-evaluates — placeholder → image without stale state
    public async Task C23_14_CapabilityFlipReevaluates()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(24, 12),
            Capabilities = HeadlessCapabilities.Ansi16Legacy, // start with NO graphics
            CaptureFrameBytes = true,
        });
        var presenter = new ImagePresenter
        {
            Source = Img(8, 4),
            PlaceholderContent = "no image",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();
        Assert.False(presenter.IsImageVisible);
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);

        host.Terminal.ScriptRenegotiatedCapabilities(HeadlessCapabilities.KittyGraphics); // graphics arrive
        await host.Application.RenegotiateAsync();

        // The caps flip re-evaluates on the next layout pass (CapabilitiesChanged → InvalidateMeasure); step frames.
        var emitted = RenderEmitsKittyApc(host);
        Assert.True(presenter.IsImageVisible);
        Assert.Equal(Visibility.Collapsed, presenter.PlaceholderPresenter.Visibility); // not stale (no double content)
        Assert.True(emitted);
    }

    // ── SourceUri (XAML-friendly declarative source, CD-P2K-2) ──────────────────────────────────────────

    [Fact] // C23.15: a SourceUri loads the image via the resource loader (file://) and renders
    public void C23_15_SourceUriLoadsAndRenders()
    {
        var pngPath = Path.Combine(Path.GetTempPath(), $"cursorial-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(pngPath, MinimalPng(80, 80));
        try
        {
            using var host = KittyHost();
            var presenter = new ImagePresenter
            {
                SourceUri = new Uri(pngPath), // an absolute path ⇒ a file:// URI
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            host.ShowRoot(presenter);
            var emitted = RenderEmitsKittyApc(host);

            Assert.True(presenter.IsImageVisible);
            Assert.NotNull(presenter.EffectiveSource); // loaded from the URI
            Assert.True(emitted);
        }
        finally
        {
            File.Delete(pngPath);
        }
    }

    [Fact] // C23.16: an explicit Source (bytes) wins over a SourceUri
    public void C23_16_ExplicitSourceWinsOverUri()
    {
        var presenter = new ImagePresenter
        {
            Source = Img(10, 5),
            SourceUri = new Uri("embedded://Nonexistent.Assembly/none.png"),
        };
        Assert.Same(presenter.Source, presenter.EffectiveSource);
    }

    [Fact] // C23.17: a failed URI load shows the placeholder (no crash, no effective source)
    public void C23_17_BadUriShowsPlaceholder()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter
        {
            SourceUri = new Uri("embedded://Nonexistent.Assembly/none.png"),
            PlaceholderContent = "no image",
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.Null(presenter.EffectiveSource);
        Assert.False(presenter.IsImageVisible);
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
    }

    [Fact] // C23.18: the Image control's SourceUri flows through the template to the presenter
    public void C23_18_ImageControlSourceUriFlows()
    {
        var pngPath = Path.Combine(Path.GetTempPath(), $"cursorial-image-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(pngPath, MinimalPng(80, 80));
        try
        {
            using var host = KittyHost();
            var image = new Image
            {
                SourceUri = new Uri(pngPath),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            host.ShowRoot(image);
            var emitted = RenderEmitsKittyApc(host);

            Assert.NotNull(image.PresenterPart);
            Assert.Equal(image.SourceUri, image.PresenterPart!.SourceUri); // TemplateBound control → presenter
            Assert.True(emitted);
        }
        finally
        {
            File.Delete(pngPath);
        }
    }

    [Fact] // C23.19 (CD-P2K-2 audit): a MALFORMED SourceUri (NUL-char relative path) degrades to the placeholder, never throws
    public void C23_19_MalformedUriDegradesNoThrow()
    {
        using var host = KittyHost();
        var presenter = new ImagePresenter { PlaceholderContent = "no image" };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        // A NUL char makes Path.GetFullPath/File.OpenRead throw (the loader only caught FileNotFound/etc. before); the
        // ImagePresenter call-site guard + the hardened loader must turn it into a clean placeholder, not a crashed setter.
        var ex = Record.Exception(() => presenter.SourceUri = new Uri("bad\0name.png", UriKind.RelativeOrAbsolute));
        Assert.Null(ex);
        host.RunUntilIdle();

        Assert.Null(presenter.EffectiveSource);
        Assert.False(presenter.IsImageVisible);
        Assert.Equal(Visibility.Visible, presenter.PlaceholderPresenter.Visibility);
    }

    private sealed class PlaceholderVm; // a content type for the by-type placeholder template (C23.5)
}
