using Cursorial.Drawing;
using Cursorial.Media;
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
        var baseStyle = CellStyle.Default.WithBackground(baseBackground ?? Color.FromRgb(0, 0, 0));
        new SceneCompositor(baseStyle).Composite([new SceneLayer(scene)], buffer.AsView());
        return buffer;
    }

    /// <summary>
    /// Render an ordered z-stack of scenes (first = bottom) and composite them onto an opaque base — for
    /// cross-layer behaviour like occlusion, where a top scene must hide a lower scene's glyph.
    /// </summary>
    public static CellBuffer RenderLayers(int columns, int rows, Color? baseBackground, params Action<DrawingContext>[] draws)
    {
        var scenes = new Scene[draws.Length];
        var layers = new SceneLayer[draws.Length];
        for (int i = 0; i < draws.Length; i++)
        {
            scenes[i] = Scene.Create(columns, rows);
            scenes[i].Draw(draws[i]);
            layers[i] = new SceneLayer(scenes[i]);
        }

        var buffer = new CellBuffer(columns, rows);
        var baseStyle = CellStyle.Default.WithBackground(baseBackground ?? Color.FromRgb(0, 0, 0));
        new SceneCompositor(baseStyle).Composite(layers, buffer.AsView());

        foreach (var s in scenes) s.Dispose();
        return buffer;
    }
}
