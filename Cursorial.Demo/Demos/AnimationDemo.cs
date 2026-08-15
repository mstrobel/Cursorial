using Cursorial.Animation;
using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace

// Phase-5 animation showcase. The library is time-free; THIS demo owns the only clock (frame counter ×
// FrameInterval) and asks each animation for its value at the elapsed offset. Shows: an animated gradient
// fill (BrushAnimation sweeping a linear gradient's endpoints, AutoReverse), an easing progress bar with a
// live plot of the current curve (cycle the easing with ←/→ or any key), a slide+fade of a *cached*
// scene via CompositeParametersAnimation (composite-param animation — no re-raster of the slid content),
// and a RectAnimation focus box that glides + resizes each frame (the geometry interpolator).
internal sealed class AnimationDemo : InteractiveDemo
{
    public override string Name => "animate";
    public override IReadOnlyList<string> Aliases => ["animation", "anim"];
    public override string Description => "Easings, animated gradients (BrushInterpolator), slide/fade composite-param animation, and a RectAnimation focus box.";

    protected override string IntroMessage =>
        "Animation demo. Opening alt screen — ←/→ (or any key) cycles the easing; q or Ctrl+C exits.";

    protected override bool Animated => true;
    protected override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(40);

    // The easing catalog to cycle through with the keyboard.
    private static readonly (string Name, Easing Easing)[] Catalog =
    [
        ("Linear", Easings.Linear),
        ("QuadIn", Easings.QuadIn), ("QuadOut", Easings.QuadOut), ("QuadInOut", Easings.QuadInOut),
        ("CubicInOut", Easings.CubicInOut), ("SineInOut", Easings.SineInOut),
        ("ExpoOut", Easings.ExpoOut), ("BackOut", Easings.BackOut), ("BackInOut", Easings.BackInOut),
    ];
    private int _easing;

    private static readonly Color Cyan = Color.FromRgb(120, 220, 232);
    private static readonly Color Label = Color.FromRgb(150, 160, 200);

    private SceneCompositor? _compositor;
    private Scene? _dynamic;
    private Scene? _slide;

    private IAnimation<IBrush> _gradient = null!;
    private IAnimation<CompositeParameters> _slideMove = null!;
    private IAnimation<double> _progress = null!;
    private IAnimation<Rect>? _focusBox;   // null on terminals too narrow for the side panel
    private Rect _focusPanel;

    protected override void Initialize() => Build();

    protected override void OnResize(int columns, int rows)
    {
        base.OnResize(columns, rows);
        Build();
    }

    protected override bool OnEvent(InputEvent evt)
    {
        // Exit keys (q / Esc / Ctrl+C) are handled by the base before this; any other key-down cycles.
        if (evt is KeyEvent { Kind: KeyEventKind.Down } k)
        {
            int delta = k.Key is Key.LeftArrow or Key.UpArrow ? -1 : 1;
            _easing = ((_easing + delta) % Catalog.Length + Catalog.Length) % Catalog.Length;
            RebuildProgress();
            return true;
        }
        return false;
    }

    private void RebuildProgress() =>
        _progress = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(2.4), Catalog[_easing].Easing).Loop();

    private void Build()
    {
        _compositor = new SceneCompositor(Style);

        // Animated gradient: sweep a 3-color ramp's endpoints across the bar and back (AutoReverse → smooth,
        // no seam). The per-frame brush comes straight from BrushInterpolator.
        GradientStop[] ramp =
            [new(0.0, Color.FromRgb(40, 90, 200)), new(0.5, Color.FromRgb(110, 210, 150)), new(1.0, Color.FromRgb(235, 200, 90))];
        var gradA = new LinearGradientBrush(ramp, new RelativePoint(-0.5, 0), new RelativePoint(0.5, 0));
        var gradB = new LinearGradientBrush(ramp, new RelativePoint(0.5, 0), new RelativePoint(1.5, 0));
        _gradient = new BrushAnimation(gradA, gradB, TimeSpan.FromSeconds(2.5)).AutoReverse();

        RebuildProgress();

        // Slide + fade: a cached gradient tile that slides in from offscreen-left and fades up, bouncing
        // forever. Only its CompositeParameters animate — the tile is rasterized once.
        int boxW = Math.Clamp(Buffer.Columns - 4, 8, 30);
        int boxRow = Math.Max(0, Buffer.Rows - 4);
        _slide?.Dispose();
        _slide = Scene.Create(Buffer.Columns, Buffer.Rows);
        _slide.Draw(ctx =>
        {
            ctx.FillRectangle(new Rect(0, boxRow, boxW, 3),
                new BrushedStyle { Background = new LinearGradientBrush(Color.FromRgb(185, 70, 160), Color.FromRgb(90, 120, 230)) });
            ctx.DrawText(2, boxRow + 1, "slide + fade — cached scene", DemoSupport.Ink(Color.FromRgb(18, 18, 28)));
        });
        _slideMove = new CompositeParametersAnimation(
            new CompositeParameters(-boxW, 0, 0),     // offscreen-left, transparent
            new CompositeParameters(2, 0, 255),       // in place, opaque
            TimeSpan.FromSeconds(2.5), Easings.CubicInOut).AutoReverse();

        // Focus box: a Rect that glides AND resizes between two corners of a side panel, bouncing forever
        // (RectAnimation — the geometry interpolator rounds + clamps each component per frame). Drawn only
        // when the terminal is wide/tall enough for a panel beside the easing curve.
        int panelLeft = 1 + Math.Clamp(Buffer.Columns - 2, 10, 40) + 3;
        int panelW = Buffer.Columns - panelLeft - 1;
        const int panelTop = 10, panelH = 6;
        if (panelW >= 16 && Buffer.Rows > panelTop + panelH)
        {
            _focusPanel = new Rect(panelLeft, panelTop, panelW, panelH);
            var small = new Rect(panelLeft + 1, panelTop + 1, 4, 2);
            var large = new Rect(panelLeft + panelW - 11, panelTop + panelH - 4, 10, 3);
            _focusBox = new RectAnimation(small, large, TimeSpan.FromSeconds(2.0), Easings.CubicInOut).AutoReverse();
        }
        else
        {
            _focusBox = null;
        }

        _dynamic?.Dispose();
        _dynamic = Scene.Create(Buffer.Columns, Buffer.Rows);
    }

    protected override void RenderFrame(long frame)
    {
        TimeSpan t = FrameInterval * frame;

        _dynamic?.Invalidate();
        _dynamic?.Draw(ctx => Paint(ctx, t));

        if (_dynamic is {} dynamic && _slide is {} slide)
        {
            _compositor?.Composite(
                [new SceneLayer(dynamic), new SceneLayer(slide, _slideMove.ValueAt(t))],
                Buffer.AsView());
        }
    }

    private void Paint(DrawingContext ctx, TimeSpan t)
    {
        int w = ctx.Bounds.Columns;
        var heading = Color.FromRgb(200, 210, 255);

        ctx.DrawText(1, 0, "Cursorial Animation — easings · animated gradient · slide/fade (Phase 5)", DemoSupport.Ink(heading));

        // Animated gradient bar — the brush is the per-frame value of a BrushAnimation.
        ctx.DrawText(1, 2, "Animated gradient (BrushInterpolator):", DemoSupport.Ink(Label));
        ctx.FillRectangle(new Rect(1, 3, Math.Clamp(w - 2, 1, 60), 2), new BrushedStyle { Background = _gradient.ValueAt(t) });

        // Easing progress bar — fill clamps to the track, so overshoot (Back) parks at the ends; the live
        // curve plot beside it reveals the shape (it dips below 0 / past 1).
        var (name, easing) = Catalog[_easing];
        double p = _progress.ValueAt(t);
        ctx.DrawText(1, 6, $"Easing: {name}    (←/→ to change)", DemoSupport.Ink(Label));
        int barW = Math.Clamp(w - 2, 1, 60);
        ctx.FillRectangle(new Rect(1, 7, barW, 1), new BrushedStyle { Background = new SolidColorBrush(Color.FromRgb(40, 44, 60)) });     // track
        int fill = (int) Math.Round(Math.Clamp(p, 0.0, 1.0) * barW);
        if (fill > 0) ctx.FillRectangle(new Rect(1, 7, fill, 1), new BrushedStyle { Background = new SolidColorBrush(Cyan) });            // fill

        ctx.DrawText(1, 9, "Easing curve:", DemoSupport.Ink(Label));
        var pts = new PointD[33];
        for (int i = 0; i < pts.Length; i++)
        {
            double x = i / (double) (pts.Length - 1);
            pts[i] = new PointD(x, easing(x));
        }
        new LineChart(pts, Cyan)
        {
            Interpolation = CurveInterpolation.Linear,
            XRange = new AxisRange(0, 1),
            YRange = new AxisRange(-0.4, 1.4),   // widened so Back-easing overshoot is visible
        }.Render(ctx, new Rect(1, 10, Math.Clamp(w - 2, 10, 40), 6));

        // RectAnimation: a focus box gliding + resizing within its panel (the geometry interpolator).
        if (_focusBox is not null)
        {
            ctx.DrawText(_focusPanel.Column, 9, "RectAnimation:", DemoSupport.Ink(Label));
            ctx.FillRectangle(_focusPanel, new BrushedStyle { Background = new SolidColorBrush(Color.FromRgb(28, 30, 42)) });   // panel backdrop
            ctx.FillRectangle(_focusBox.ValueAt(t), new BrushedStyle { Background = new SolidColorBrush(Cyan) });              // the gliding/resizing box
        }
    }
}
