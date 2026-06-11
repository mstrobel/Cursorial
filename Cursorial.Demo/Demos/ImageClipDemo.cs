using System.Reflection;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;

// ReSharper disable CheckNamespace

// Phase-6b.2 showcase: an image clipped at a scene boundary. Two composited layers — an unclipped label layer
// (title + a line marking the clip edge) and an image layer clipped to the left of that edge. The image is
// positioned to straddle the edge, so its right half is past the clip. Sixel re-crops the pixels at the edge
// and Kitty re-places with a source rectangle (x,y,w,h in image pixels); iTerm2 can't crop a partial image
// and is suppressed (all-or-nothing). Event-driven (static).
internal sealed class ImageClipDemo : InteractiveDemo
{
    public override string Name => "imageclip";
    public override IReadOnlyList<string> Aliases => ["iclip"];
    public override string Description =>
        "An image clipped at a scene boundary — Sixel + Kitty crop; iTerm2 suppresses (Phase 6b.2).";

    protected override string IntroMessage =>
        "Image-clip demo. Opening alt screen — press q or Ctrl+C to exit.";

    private IContent _image = null!;
    private Scene? _labels;
    private Scene? _imageScene;
    private SceneCompositor? _compositor;
    private int _clipColumns;
    private bool _tooSmall;

    protected override void Initialize()
    {
        _image = Icon.FromEmbedded(Assembly.GetExecutingAssembly(), "Icons/cursorial_icon.png", "\\[img]",
                                   renderSize: new Size(24, 12));
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
        _tooSmall = Buffer.Columns < 36 || Buffer.Rows < 9;
        // Only meaningful (and only safe to clamp — min ≤ max needs Columns ≥ 20) when not too small.
        _clipColumns = _tooSmall ? 0 : Math.Clamp(Buffer.Columns / 2, 16, Buffer.Columns - 4);

        _labels?.Dispose();
        _labels = Scene.Create(Buffer.Columns, Buffer.Rows);
        _labels.Draw(PaintLabels);

        _imageScene?.Dispose();
        _imageScene = Scene.Create(Buffer.Columns, Buffer.Rows);
        
        if (!_tooSmall)
            _imageScene.Draw(ctx => ctx.DrawContent(new Rect(_clipColumns - 12, 3, 24, 12), _image, Capabilities.Output));
    }

    private void PaintLabels(DrawingContext ctx)
    {
        var heading = Color.FromRgb(200, 210, 255);
        var label = Color.FromRgb(150, 160, 200);

        ctx.DrawText(2, 0, "An image straddling a scene clip — its right half is past the clip edge:", heading);
        ctx.DrawText(2, 1, "Sixel re-crops pixels; Kitty re-places with a source rectangle; iTerm2 can't, so it suppresses.", label);

        if (_tooSmall)
        {
            ctx.DrawText(2, 3, "(Terminal too small — widen it.)", label);
            return;
        }

        // The clip edge, marked so the crop reads as a clip, not a glitch.
        ctx.DrawLine(_clipColumns, 3, _clipColumns, Math.Min(Buffer.Rows - 1, 15), Color.FromRgb(110, 120, 150));
        ctx.DrawText(Math.Max(0, _clipColumns - 9), 16, "← clip edge", label);
    }

    protected override void RenderFrame(long frame)
    {
        if (_labels is {} labels && _imageScene is {} imageScene)
        {
            _compositor?.Composite(
                [
                    new SceneLayer(labels),
                    new SceneLayer(imageScene, new CompositeParameters(clip: new Rect(0, 0, _clipColumns, Buffer.Rows))),
                ], Buffer.AsView());
        }
    }
}
