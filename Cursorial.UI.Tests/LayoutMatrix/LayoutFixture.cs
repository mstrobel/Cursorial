using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

/// <summary>
/// The §0.1 fixture: a leaf element whose <c>MeasureOverride</c> returns <see cref="Natural"/>
/// <b>unconditionally</b> (rigid content — ignores the constraint) and whose default
/// <c>ArrangeOverride</c> returns <c>finalSize</c>. Records every measure constraint and
/// <c>MeasureOverride</c>/<c>ArrangeOverride</c>/<c>ApplyTemplate</c> invocation.
/// </summary>
public class Probe : UIElement
{
    public Probe()
    {
    }

    public Probe(int columns, int rows) => Natural = new Size(columns, rows);

    /// <summary>The natural content size — the test knob for desired-size changes (L33/L92).</summary>
    public Size Natural { get; set; }

    /// <summary>Optional override of the measure result (L12-style pathological returns).</summary>
    public Func<Size, Size>? MeasureResult { get; set; }

    /// <summary>Optional override of the arrange result (L57-style pathological returns).</summary>
    public Func<Size, Size>? ArrangeResult { get; set; }

    /// <summary>Optional hook run at the start of <c>MeasureOverride</c> (oscillators, sibling writes).</summary>
    public Action<Probe>? OnMeasuring { get; set; }

    /// <summary>Optional paint hook run by <c>Render</c> (composite-result rows paint distinguishable glyphs here).</summary>
    public Action<Probe, RenderContext>? OnRender { get; set; }

    /// <summary>Optional fill glyph: when set, <c>Render</c> fills the element-local bounds with it (composite rows).</summary>
    public string? FillGlyph { get; set; }

    /// <summary>Optional <c>HitTestCore</c> override (L199 ③).</summary>
    public Func<int, int, bool>? HitTestCoreOverride { get; set; }

    /// <summary>Every constraint handed to <c>MeasureOverride</c> (post margin-deflate, post min/max coercion).</summary>
    public List<Size> MeasureConstraints { get; } = [];

    /// <summary>Every size handed to <c>ArrangeOverride</c> (the aligned size <c>size_a</c>, not the slot).</summary>
    public List<Size> ArrangeSizes { get; } = [];

    public int MeasureOverrideCount { get; private set; }

    public int ArrangeOverrideCount { get; private set; }

    public int ApplyTemplateCount { get; private set; }

    /// <summary>Cumulative <c>Render</c>-call count — the matrix's <c>RC(e)</c> observability (LD12).</summary>
    public int RenderCount { get; private set; }

    /// <summary>An optional shared event log (order-of-operations rows).</summary>
    public List<string>? Log { get; set; }

    /// <summary>The log name used by <see cref="Log"/> entries.</summary>
    public string LogName { get; set; } = "probe";

    public override bool ApplyTemplate()
    {
        ApplyTemplateCount++;
        return base.ApplyTemplate();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureOverrideCount++;
        MeasureConstraints.Add(availableSize);
        Log?.Add($"measure:{LogName}");
        OnMeasuring?.Invoke(this);
        return MeasureResult is { } result ? result(availableSize) : MeasureCore(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeOverrideCount++;
        ArrangeSizes.Add(finalSize);
        Log?.Add($"arrange:{LogName}");
        return ArrangeResult is { } result ? result(finalSize) : finalSize;
    }

    protected override void Render(RenderContext context)
    {
        RenderCount++;
        Log?.Add($"render:{LogName}");
        if (FillGlyph is { } glyph)
        {
            for (var row = 0; row < context.Size.Rows; row++)
            for (var column = 0; column < context.Size.Columns; column++)
                context.Set(column, row, glyph, default(CellStyle));
        }

        OnRender?.Invoke(this, context);
    }

    protected override bool HitTestCore(int column, int row)
        => HitTestCoreOverride?.Invoke(column, row) ?? base.HitTestCore(column, row);

    /// <summary>The content policy: rigid (returns <see cref="Natural"/>). <see cref="Fit"/> overrides.</summary>
    protected virtual Size MeasureCore(Size availableSize) => Natural;
}

/// <summary>
/// The L196 fixture: a <see cref="Probe"/> with an <b>inheriting</b> styled property registered
/// <c>AffectsRender</c> on its own per-type lane (a dedicated subtype so <c>Probe</c>'s effects
/// table, frozen by earlier sections' <c>GetEffects</c> queries, is never touched post-freeze).
/// </summary>
public sealed class InkProbe : Probe
{
    /// <summary>An inherited brush-shaped property: a root write fans out to inheriting descendants.</summary>
    public static readonly StyledProperty<int> InkProperty =
        UIProperty.Register<InkProbe, int>("Ink", inherits: true);

    static InkProbe()
    {
        AffectsRender<InkProbe>(InkProperty);
    }

    public InkProbe(int columns, int rows) : base(columns, rows)
    {
    }
}

/// <summary>The constraint-respecting <see cref="Probe"/> variant: returns <c>min(natural, constraint)</c> per axis (§0.1).</summary>
public class Fit : Probe
{
    public Fit(int columns, int rows) : base(columns, rows)
    {
    }

    protected override Size MeasureCore(Size availableSize)
        => new(Math.Min(Natural.Columns, availableSize.Columns), Math.Min(Natural.Rows, availableSize.Rows));
}

/// <summary>
/// An instrumented container: default container behavior (measures children with the incoming
/// constraint, desired = max of children; arranges children at <c>(0, 0, finalSize)</c>), with
/// counts and <c>OnChildDesiredSizeChanged</c> recording. Children adopt visually + logically.
/// </summary>
public class Host : UIElement
{
    public int MeasureOverrideCount { get; private set; }

    public int ArrangeOverrideCount { get; private set; }

    /// <summary>Cumulative <c>Render</c>-call count — the matrix's <c>RC(e)</c> observability (LD12).</summary>
    public int RenderCount { get; private set; }

    public int ChildDesiredSizeChangedCount { get; private set; }

    public UIElement? LastChangedChild { get; private set; }

    /// <summary>An optional shared event log (order-of-operations rows).</summary>
    public List<string>? Log { get; set; }

    /// <summary>The log name used by <see cref="Log"/> entries.</summary>
    public string LogName { get; set; } = "host";

    public void Add(UIElement child) => AdoptChild(child, -1);

    public void Remove(UIElement child) => DisownChild(child);

    /// <summary>Logical-only adoption (no visual reparent) — the inverse of <c>AddVisualChildOnly</c>.</summary>
    public void AdoptLogical(UIElement child) => AddLogicalChild(child);

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureOverrideCount++;
        Log?.Add($"measure:{LogName}");
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeOverrideCount++;
        Log?.Add($"arrange:{LogName}");
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnChildDesiredSizeChanged(UIElement child)
    {
        ChildDesiredSizeChangedCount++;
        LastChangedChild = child;
        base.OnChildDesiredSizeChanged(child);
    }

    protected override void Render(RenderContext context)
    {
        RenderCount++;
        Log?.Add($"render:{LogName}");
    }
}

/// <summary>
/// The §0.1 <c>slot(rect)</c> fixture: measures its child with the container's own constraint and
/// arranges the child into exactly <see cref="SlotRect"/> — isolates the child-side arrange contract.
/// </summary>
public class SlotHost : UIElement
{
    private readonly UIElement _child;

    public SlotHost(UIElement child)
    {
        _child = child;
        AdoptChild(child, -1);
    }

    public Rect SlotRect { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _child.Measure(availableSize);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _child.Arrange(SlotRect);
        return finalSize;
    }
}

/// <summary>Shared harness helpers for the layout-matrix sections.</summary>
public static class LayoutFixture
{
    /// <summary>Attaches <paramref name="root"/> as the P1 single root and returns its manager (§0.1 <c>Root</c>).</summary>
    public static LayoutManager CreateRoot(UIElement root) => new(root);

    /// <summary>§0.1 <c>layout(W×H)</c>: one <c>RunLayoutPass</c> at the given root constraint.</summary>
    public static void Layout(this LayoutManager manager, int columns, int rows)
        => manager.RunLayoutPass(new Size(columns, rows));

    /// <summary>
    /// The §0.1 render harness: <c>Root</c> laid out at the given size plus a <see cref="RenderTree"/>
    /// over a fresh <see cref="ScenePool"/>; <c>render()</c> = <see cref="Render"/>.
    /// </summary>
    public static (LayoutManager Manager, RenderTree Tree) CreateRenderRoot(UIElement root, int columns = 40, int rows = 12)
    {
        var manager = CreateRoot(root);
        manager.Layout(columns, rows);
        var tree = new RenderTree(root, new ScenePool(), OutputCapabilities.None);
        return (manager, tree);
    }

    /// <summary>§0.1 <c>render()</c>.</summary>
    public static void Render(this RenderTree tree) => tree.RunRenderPass();

    /// <summary>Collects the tree's layers bottom-up into a fresh list (§0.1 <c>layers</c>).</summary>
    public static List<SceneLayer> Layers(this RenderTree tree)
    {
        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers);
        return layers;
    }

    /// <summary>§0.1 <c>P(b)</c>: boundary <paramref name="boundary"/>'s published <see cref="CompositeParameters"/>.</summary>
    public static CompositeParameters Parameters(this RenderTree tree, UIElement boundary)
        => tree.GetPublishedParameters(boundary);

    /// <summary>Composites the tree's layers onto a fresh target buffer and returns it (composite-result rows).</summary>
    public static CellBuffer Composite(this RenderTree tree, int columns, int rows)
    {
        var layers = tree.Layers();
        var buffer = new CellBuffer(columns, rows);
        new SceneCompositor().Composite(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(layers), new CellBufferView(buffer));
        return buffer;
    }

    /// <summary>
    /// Runs <paramref name="action"/> while capturing layout diagnostics (DEBUG raises; empty in
    /// release) from the <b>process-global</b> channel — use only for detached-element scenarios
    /// (no manager); prefer the manager-scoped overload for isolation under parallel collections.
    /// </summary>
    public static List<LayoutDiagnosticEvent> CaptureDiagnostics(Action action)
    {
        var events = new List<LayoutDiagnosticEvent>();
        Action<LayoutDiagnosticEvent> handler = events.Add;
        LayoutDiagnostics.DiagnosticRaised += handler;
        try
        {
            action();
        }
        finally
        {
            LayoutDiagnostics.DiagnosticRaised -= handler;
        }

        return events;
    }

    /// <summary>
    /// The per-root capture: subscribes <paramref name="manager"/>'s
    /// <see cref="LayoutManager.DiagnosticRaised"/> — immune to parallel-collection cross-wiring.
    /// </summary>
    public static List<LayoutDiagnosticEvent> CaptureDiagnostics(LayoutManager manager, Action action)
    {
        var events = new List<LayoutDiagnosticEvent>();
        Action<LayoutDiagnosticEvent> handler = events.Add;
        manager.DiagnosticRaised += handler;
        try
        {
            action();
        }
        finally
        {
            manager.DiagnosticRaised -= handler;
        }

        return events;
    }

    /// <summary>An untyped observer recording (old, new) pairs — the DirectProperty notification rows (L35/L58).</summary>
    public sealed class RecordingObserver : IUntypedValueObserver
    {
        public List<(object? OldValue, object? NewValue)> Changes { get; } = [];

        public void OnPropertyChanged(UIObject sender, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => Changes.Add((oldValue, newValue));
    }
}
