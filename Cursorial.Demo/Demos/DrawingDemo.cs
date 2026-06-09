using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

// Drawing-layer scene-compositing demo. A static wallpaper (gradient + title bar) with three
// translucent panels that slide and blend over it, demonstrating gradients, opacity, z-order,
// clipping, and the cached-raster payoff. Animated at ~20 fps for a smooth slide; the scene graph
// is rebuilt on resize. Migrated verbatim from Program.cs's DemoDrawingAsync + file-scoped
// DrawingDemoScene.
internal sealed class DrawingDemo : InteractiveDemo
{
    public override string Name => "draw";
    public override IReadOnlyList<string> Aliases => ["scenes", "compose"];
    public override string Description =>
        "Drawing-layer scene compositing — translucent panels slide and blend over a static background.";

    protected override string? IntroMessage =>
        "Drawing demo (scene compositing). Translucent panels slide and blend over a static background — press q or Ctrl+C to exit.";

    protected override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(20); // ~20 fps for a smooth slide
    protected override bool Animated => true;

    // The drawing-layer scene graph: a static wallpaper + translucent panels, all rasterized once.
    // Each frame only re-composites them at moving offsets — the cached-raster payoff. Held as a
    // field, built in Initialize and rebuilt in OnResize.
    private DrawingDemoScene _scene = null!;

    protected override void Initialize()
    {
        _scene = new DrawingDemoScene(Buffer.Columns, Buffer.Rows, CellAspect());
    }

    protected override void OnResize(int columns, int rows)
    {
        // Resize reallocates + clears the buffer; rebuild the scene graph (a fresh compositor)
        // so the whole frame is recomposited at the new size.
        base.OnResize(columns, rows);
        _scene = new DrawingDemoScene(Buffer.Columns, Buffer.Rows, CellAspect());
    }

    // Cell width:height pixel ratio (≈0.5) for aspect-correcting a radial gradient into a true on-screen
    // circle; falls back to a typical 1:2 cell when the terminal didn't report its cell-pixel size.
    private double CellAspect()
    {
        var win = Capabilities.Output.Window;
        return win.CellPixelWidth is { } w && win.CellPixelHeight is { } h && h > 0 ? w / (double) h : 0.5;
    }

    protected override void RenderFrame(long frame) => _scene.Composite(Buffer, frame);

    private sealed class DrawingDemoScene
    {
        private readonly int _cols;
        private readonly int _rows;
        private readonly double _cellAspect;
        private readonly SceneCompositor _compositor;
        private readonly Scene _wallpaper;
        private readonly Panel[] _panels;
        private readonly SceneLayer[] _layers;

        private readonly record struct Panel(
            Scene Scene, int Width, int Height, double SpeedX, double SpeedY, double PhaseX, double PhaseY,
            int BaseRow, Rect? Clip);

        public DrawingDemoScene(int columns, int rows, double cellAspect)
        {
            _cols = Math.Max(1, columns);
            _rows = Math.Max(1, rows);
            _cellAspect = cellAspect;

            // Dark base; the compositor resets each dirty region to it before compositing the z-stack.
            _compositor = new SceneCompositor(Style.Default.WithBackground(Color.FromRgb(16, 18, 24)));

            _wallpaper = BuildWallpaper(_cols, _rows, _cellAspect);

            int w = Math.Min(Math.Clamp(_cols / 4, 6, 22), _cols);
            int h = Math.Min(Math.Clamp(_rows / 3, 3, 8), Math.Max(1, _rows - 1));
            int midRow = Math.Max(1, (_rows - h) / 2);

            var red   = BuildPanel(w, h, Color.FromRgba(235, 70, 70, 150),  'R', Color.FromRgb(70, 16, 16));
            var green = BuildPanel(w, h, Color.FromRgba(70, 200, 95, 150),  'G', Color.FromRgb(16, 56, 26));
            var blue  = BuildPanel(w, h, Color.FromRgba(80, 130, 235, 150), 'B', Color.FromRgb(18, 30, 70));

            // Blue is clipped to a fixed window over the right half, so it is visibly cut as it slides
            // into the clip's left edge — demonstrating composite clipping.
            int clipCol = Math.Clamp(_cols / 2, 0, Math.Max(0, _cols - 1));
            Rect? blueClip = _cols > clipCol && _rows > 1
                                 ? new Rect(clipCol, 1, _cols - clipCol, _rows - 1)
                                 : null;

            _panels =
            [
                new Panel(red,   w, h, 0.9, 0.0, 0.0,             0.0, midRow,                       Clip: null),
                new Panel(green, w, h, 0.9, 0.0, Math.PI,         0.0, midRow,                       Clip: null),
                new Panel(blue,  w, h, 0.6, 0.7, Math.PI / 2.0,   0.5, Math.Max(1, midRow - h / 2),  Clip: blueClip),
            ];

            _layers = new SceneLayer[1 + _panels.Length];
        }

        /// <summary>Composite the wallpaper + panels onto <paramref name="target"/> for the given frame.</summary>
        public void Composite(CellBuffer target, long frame)
        {
            double t = frame * 0.05;

            _layers[0] = new SceneLayer(_wallpaper);   // static background (z = 0)

            for (int i = 0; i < _panels.Length; i++)
            {
                var p = _panels[i];

                int travelX = Math.Max(0, _cols - p.Width);
                int offX = Math.Clamp((int) Math.Round(travelX * 0.5 * (1 + Math.Sin(t * p.SpeedX + p.PhaseX))), 0, travelX);

                int maxRow = Math.Max(1, _rows - p.Height);
                int offY;
                if (p.SpeedY == 0.0)
                {
                    offY = Math.Clamp(p.BaseRow, 1, maxRow);
                }
                else
                {
                    int amp = Math.Max(0, Math.Min(p.Height + 2, (_rows - 1) - p.Height));
                    offY = Math.Clamp(p.BaseRow + (int) Math.Round(amp * 0.5 * Math.Sin(t * p.SpeedY + p.PhaseY)), 1, maxRow);
                }

                _layers[i + 1] = new SceneLayer(p.Scene, new CompositeParameters(offX, offY, clip: p.Clip));
            }

            _compositor.Composite(_layers, target.AsView());
        }

        private static Scene BuildWallpaper(int cols, int rows, double cellAspect)
        {
            var scene = Scene.Create(cols, rows);
            scene.Draw(ctx =>
            {
                // Body: a subtle vertical gradient (top → bottom) — a Phase-2 gradient fill, sampled
                // per cell and cached once.
                if (rows > 1)
                    ctx.FillRectangle(
                        new Rect(0, 1, cols, rows - 1),
                        new LinearGradientBrush([new(0.0, Color.FromRgb(24, 28, 48)), new(1.0, Color.FromRgb(6, 8, 14))],
                                             startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom));   // vertical

                // Opaque title bar across the top row.
                var barColor = Color.FromRgb(40, 52, 87);
                ctx.FillRectangle(new Rect(0, 0, cols, 1), new SolidColorBrush(barColor));

                // Title text with a horizontal gradient foreground (teal → violet) over the bar — each
                // glyph cell samples its own color.
                const string title = " Cursorial.Drawing — gradients - scenes - opacity - z-order - clip - cached raster ";
                var clipped = title.Length < cols ? title : (cols > 1 ? title[..(cols - 1)] : "");
                var titleFg = new LinearGradientBrush([new(0.0, Color.FromRgb(120, 220, 232)), new(1.0, Color.FromRgb(196, 150, 255))]);
                ctx.DrawText(0, 0, clipped, titleFg, new SolidColorBrush(barColor));

                // Aspect-correction showcase: two radial gradients over square-in-CELLS regions (which are tall
                // on screen). The left uses no correction → a vertical ellipse; the right is aspect-corrected →
                // a true on-screen circle (shorter in rows than it is wide in columns).
                const int sw = 9;
                if (rows >= sw + 3 && cols >= 2 * sw + 6)
                {
                    int top = rows - sw - 1;
                    ctx.DrawText(1, top - 1, "radial:  ellipse (raw)    circle (aspect-corrected) →", Color.FromRgb(150, 160, 200));
                    var deep = Color.FromRgb(16, 18, 30);
                    ctx.FillRectangle(new Rect(1, top, sw, sw), new RadialGradientBrush(Color.FromRgb(120, 220, 232), deep));
                    ctx.FillRectangle(new Rect(sw + 4, top, sw, sw),
                        new RadialGradientBrush(Color.FromRgb(196, 150, 255), deep) { CellAspectRatio = cellAspect });
                }
            });
            return scene;
        }

        private static Scene BuildPanel(int width, int height, Color fill, char label, Color labelBackground)
        {
            var scene = Scene.Create(width, height);
            scene.Draw(ctx =>
            {
                // Translucent fill — its alpha is preserved for the compositor to blend.
                ctx.FillRectangle(scene.Bounds, new SolidColorBrush(fill));

                // A small opaque label tab so panels are identifiable while they overlap.
                var labelStyle = Style.Default.WithForeground(Color.FromRgb(245, 245, 250)).WithBackground(labelBackground);
                ctx.Set(1, 0, label.ToString(), labelStyle);
            });
            return scene;
        }
    }
}
