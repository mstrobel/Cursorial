using System.Reflection;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;

// Phase-6b showcase: images composited inside a Scene. Each icon is drawn with DrawContent, which registers
// an out-of-band fragment (Kitty / iTerm2 / Sixel) on the scene buffer; SceneCompositor carries those
// fragments onto the target so the frame renderer emits them — alongside the scene's ordinary cell content
// (the title, the box). On a terminal without a graphics protocol, each icon falls back to its glyph.
// Event-driven (static): re-paints only on resize.
internal sealed class ImageSceneDemo : InteractiveDemo
{
    public override string Name => "imagescene";
    public override IReadOnlyList<string> Aliases => ["iscene"];
    public override string Description => "Images composited inside a Scene via DrawContent (Phase 6b).";

    protected override string? IntroMessage =>
        "Image-in-scene demo. Opening alt screen — press q or Ctrl+C to exit.";

    private static readonly (string Resource, string Fallback, string Label)[] Icons =
    [
        ("Icons/settings.png", "\\[⚙️]", "settings"),
        ("Icons/download.png", "\\[⬇️]", "download"),
        ("Icons/calendar.png", "\\[📆]", "calendar"),
    ];

    private IContent[] _content = null!;
    private Scene _scene = null!;
    private SceneCompositor _compositor = null!;

    protected override void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();
        _content = [.. Icons.Select(i =>
            (IContent) Icon.FromEmbedded(assembly, i.Resource, i.Fallback, renderSize: new Size(10, 5)))];
        Build();
    }

    protected override void OnResize(int columns, int rows)
    {
        base.OnResize(columns, rows);
        Build();
    }

    private void Build()
    {
        _compositor = new SceneCompositor(Style);
        _scene?.Dispose();
        _scene = Scene.Create(Buffer.Columns, Buffer.Rows);
        _scene.Draw(Paint);
    }

    private void Paint(DrawingContext ctx)
    {
        var heading = Color.FromRgb(200, 210, 255);
        var label = Color.FromRgb(150, 160, 200);

        ctx.DrawText(2, 0, "Images composited inside a Scene — DrawContent → fragment passthrough (Phase 6b):", heading);
        ctx.DrawText(2, 1, "Real images on a Kitty / iTerm2 / Sixel terminal; the fallback glyph otherwise.", label);

        int boxWidth = Math.Min(ctx.Bounds.Columns - 4, 44);
        if (boxWidth < 14) return;   // too narrow to lay out the gallery
        ctx.DrawBox(new Rect(2, 3, boxWidth, 9), label);

        int x = 4;
        foreach (var (content, meta) in _content.Zip(Icons))
        {
            if (x + 11 > 2 + boxWidth) break;
            ctx.DrawContent(new Rect(x, 4, 10, 5), content, Capabilities.Output);
            ctx.DrawText(x, 10, meta.Label, label);
            x += 13;
        }
    }

    protected override void RenderFrame(long frame) =>
        _compositor.Composite([new SceneLayer(_scene)], Buffer.AsView());
}
