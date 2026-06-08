using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// Renders a scene draw delegate and composites it onto an opaque base so tests can read back the
/// resolved cells (the scene buffer itself is internal; compositing is the public read path).
/// </summary>
internal static class DrawHarness
{
    public static CellBuffer Render(int columns, int rows, Action<DrawingContext> draw, Color? baseBackground = null)
    {
        using var scene = Scene.Create(columns, rows);
        scene.Draw(draw);

        var buffer = new CellBuffer(columns, rows);
        var baseStyle = Style.Default.WithBackground(baseBackground ?? Color.FromRgb(0, 0, 0));
        new SceneCompositor(baseStyle).Composite([new SceneLayer(scene)], buffer.AsView());
        return buffer;
    }
}
