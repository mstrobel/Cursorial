using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Imaging;

// ReSharper disable CheckNamespace

// Inline-image showcase. Loads a PNG / JPEG / GIF from a path argument (with ~/ and embedded:
// support) and renders it via the best available terminal graphics protocol — Kitty, iTerm2, or
// Sixel — falling back to a cell-grid placeholder where none is supported. Event-driven: the image
// is painted once on entry and only re-painted on resize, because re-emitting a base64 image payload
// every tick would be wasteful and visibly flickery on slower terminals.
internal sealed class ImageDemo : InteractiveDemo, IDemo
{
    public override string Name => "image";
    public override string Description =>
        "Render a PNG / JPEG / GIF inline via Kitty / iTerm2 / Sixel (or a cell-grid placeholder).";

    // The decoded image, resolved ONCE before the harness runs and held for the lifetime of the
    // demo. PaintImageShowcase rebuilds the ImageData/Image per paint because their cell footprint
    // depends on the (resizable) buffer dimensions — but the bytes/format/path never change.
    private byte[] _bytes = null!;
    private string _path = "";
    private ImageFormat _format;

    // Decode happens before alt-screen so the early-return cases (no argument, missing file, read
    // failure) print their message in cooked mode and never flash the alternate screen. Only on a
    // successful load do we print the "Loading…" banner and hand off to the InteractiveDemo harness.
    //
    // InteractiveDemo.RunAsync is not virtual, so we hide it with `new` (concrete-type dispatch
    // selects this one) and route the IDemo interface slot here too, so the early-return holds no
    // matter how the demo is invoked.
    Task IDemo.RunAsync(string argument) => RunAsync(argument);

    public new async Task RunAsync(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            Console.WriteLine("Usage: image <path-to-png-jpeg-or-gif>");
            return;
        }

        byte[]? bytes = null;

        string path = argument.Trim('"', '\'');
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }
        else if (path.StartsWith("embedded:") &&
                 Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                 ResourceLoader.Default.TryLoadBytes(uri) is {} embeddedBytes)
        {
            bytes = embeddedBytes;
        }

        if (bytes is null && !File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            return;
        }

        if (bytes is null)
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read {path}: {ex.Message}");
                return;
            }
        }

        var format = Path.GetExtension(path).ToLowerInvariant() switch
                     {
                         ".png"            => ImageFormat.Png,
                         ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                         ".gif"            => ImageFormat.Gif,
                         _                 => ImageFormat.Png // best-guess; Kitty will refuse non-PNG, iTerm2 accepts most
                     };

        Console.WriteLine(
            $"Image demo. Loading {path} ({bytes.Length} bytes, {format}). Press q or Ctrl+C to exit.");

        _bytes = bytes;
        _path = path;
        _format = format;

        await base.RunAsync(argument);
    }

    protected override void RenderFrame(long frame) =>
        PaintImageShowcase(Buffer, _path, _bytes, _format, Capabilities.Output, Style);

    private static void PaintImageShowcase(
        CellBufferView buf,
        string path,
        byte[] bytes,
        ImageFormat format,
        OutputCapabilities outputCaps,
        in Style defaultStyle)
    {
        buf.CursorVisible = false;
        buf.Clear();
        buf.Fill(Cell.Blank with { Style = defaultStyle });

        int cols = buf.Columns;
        int rows = buf.Rows;

        // Reserve top rows for the header (path + protocol info) and bottom rows for the footer.
        // The image occupies the rectangle between them, centered horizontally.
        const int headerRows = 3;
        const int footerRows = 2;

        int availableRows = Math.Max(1, rows - headerRows - footerRows);
        int availableCols = Math.Max(1, cols - 2);

        int imageW = Math.Max(10, Math.Min(60, availableCols));
        int imageH = Math.Max(4, Math.Min(availableRows, 20));

        int anchorRow = headerRows;
        int anchorCol = Math.Max(1, (cols - imageW) / 2);

        // Header line 1: full path (truncated from the left if too long, so the file name stays visible).
        string header = $"image: {path}";
        if (header.Length > cols - 2) header = "..." + header[^(cols - 5)..];

        DemoSupport.PaintLine(buf,
                  1,
                  0,
                  header,
                  defaultStyle.WithForeground(Color.FromRgb(220, 220, 255))
                              .WithAttributes(TextAttributes.Bold));

        // Header line 2: chosen protocol + cell footprint.
        string protocol = ChooseProtocolLabel(outputCaps, format);
        string sub = $"  {bytes.Length:N0} bytes, {format} → {imageW}×{imageH} cells via {protocol}";

        DemoSupport.PaintLine(buf, 1, 1, sub, defaultStyle.WithForeground(Color.FromRgb(160, 160, 200)));

        // Image (capability-aware — Image content picks Kitty / iTerm2 / placeholder at paint time).
        var data = new ImageData(bytes, format, new Size(imageW, imageH));

        var placeholderStyle = defaultStyle.WithBackground(Color.FromRgb
                                                               (40, 40, 70))
                                           .WithForeground(Color.FromRgb(200, 200, 220));

        var content = new Image(data, placeholderStyle);

        content.Paint(buf, anchorCol, anchorRow, defaultStyle, outputCaps);

        // Footer.
        int footerRow = Math.Min(anchorRow + imageH + 1, rows - 1);

        DemoSupport.PaintLine(buf, 1, footerRow,
                  "Press q or Ctrl+C to return.",
                  defaultStyle.WithForeground(Color.FromRgb(180, 180, 220)));
    }

    private static string ChooseProtocolLabel(OutputCapabilities caps, ImageFormat format)
    {
        if (caps.Graphics.KittyGraphics && format == ImageFormat.Png)
            return "Kitty graphics protocol";
        if (caps.Graphics.ITerm2InlineImages)
            return "iTerm2 inline images";
        if (caps.Graphics.Sixel)
            return "Sixel graphics protocol";
        return "cell-grid placeholder (no graphics support)";
    }
}
