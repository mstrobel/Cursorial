using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The layout pass executor — one per visual root, and the <b>sole convergence owner</b> (design
/// doc §5.3): depth-keyed min-heap queues drained shallowest-first to a same-tick fixpoint (16-pass
/// cap + cycle diagnostic), with exactly one bounded re-run when <see cref="LayoutUpdated"/>
/// handlers dirty layout. Invalidations raised during the pass loop within the same frame
/// (invariant 1).
/// </summary>
/// <remarks>
/// <b>P1 single-root stand-in.</b> Constructing a <see cref="LayoutManager"/> over a root element
/// attaches that element as the visual root of a single full-screen tree — the P1 contract
/// (matrix L94): <see cref="RunLayoutPass"/> measures the root with the root constraint and
/// arranges it to <c>(0, 0, constraint)</c>. At P7, S4's <c>Window</c> owns the manager (one per
/// window) and this root-attachment path is replaced by window plumbing; the public pass surface
/// (<see cref="RunLayoutPass"/>, <see cref="HasPendingWork"/>, <see cref="AbandonPendingLayout"/>,
/// <see cref="LayoutUpdated"/>) is the part S6's frame loop consumes unchanged.
/// </remarks>
public sealed class LayoutManager
{
    private const int PassCap = 16;

    private readonly UIElement _root;
    private readonly DepthHeap _measureQueue = new();
    private readonly DepthHeap _arrangeQueue = new();
    private List<UIElement>? _abandonedMeasure;
    private List<UIElement>? _abandonedArrange;
    private bool _hasRootConstraint;
    private Size _rootConstraint;

    /// <summary>
    /// Creates the manager and attaches <paramref name="root"/> as the visual root of its tree
    /// (the P1 single-root stand-in — see the class remarks). The root is queued for its first
    /// measure and arrange.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> has a visual parent.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="root"/> is already attached to a tree.</exception>
    public LayoutManager(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.VerifyAccess();

        _root = root;
        root.AttachAsRoot(this);
        EnqueueMeasure(root);
        EnqueueArrange(root);
    }

    /// <summary>The root element this manager lays out.</summary>
    public UIElement Root => _root;

    /// <summary>Whether any measure or arrange work is queued — S6's idle guard.</summary>
    public bool HasPendingWork
        => _measureQueue.Count > 0 || _arrangeQueue.Count > 0
           || _abandonedMeasure is { Count: > 0 } || _abandonedArrange is { Count: > 0 };

    /// <summary>
    /// Whether the <b>live</b> queues hold work — excludes entries parked by
    /// <see cref="AbandonPendingLayout"/>. The S6 layout facade keys its frame-loop give-up on
    /// this (doc §10.5): after an abandon, parked work does not re-arm the idle guard; the next
    /// explicit invalidation enqueues normally, turns this true, and the resulting pass restores
    /// the parked entries alongside it.
    /// </summary>
    internal bool HasQueuedWork => _measureQueue.Count > 0 || _arrangeQueue.Count > 0;

    /// <summary>
    /// Raised after each pass completes, with layout valid (unless a handler re-dirties it —
    /// handlers get exactly one bounded same-tick re-run; a handler dirtying every raise degrades
    /// to one-frame lag with a DEBUG diagnostic). Caret/overlay reposition hooks live here.
    /// </summary>
    public event Action? LayoutUpdated;

    /// <summary>
    /// Raised (DEBUG builds only) when this root's tree/layout engine emits a diagnostic — the
    /// <b>per-root</b> emission channel: subscribe here rather than the process-global
    /// <see cref="LayoutDiagnostics.DiagnosticRaised"/> when isolation matters (parallel test
    /// collections cross-wire on the static). Diagnostics whose element is detached (no manager)
    /// reach only the static fallback.
    /// </summary>
    public event Action<LayoutDiagnosticEvent>? DiagnosticRaised;

    /// <summary>Raises <see cref="DiagnosticRaised"/> (DEBUG emission path — see <see cref="LayoutDiagnostics.Emit"/>).</summary>
    internal void RaiseDiagnostic(in LayoutDiagnosticEvent diagnostic) => DiagnosticRaised?.Invoke(diagnostic);

    /// <summary>
    /// Runs measure + arrange to fixpoint (the internal convergence loop, doc §5.3): the root is
    /// measured with <paramref name="rootConstraint"/> and arranged to
    /// <c>(0, 0, rootConstraint)</c> (matrix L94); queued elements drain shallowest-first; passes
    /// cap at 16 with a layout-cycle diagnostic; <see cref="LayoutUpdated"/> handlers that dirty
    /// layout get one bounded re-run. Residual work slips to the next tick with a DEBUG diagnostic.
    /// </summary>
    public void RunLayoutPass(Size rootConstraint)
    {
        _root.VerifyAccess();

        if (!_hasRootConstraint || rootConstraint != _rootConstraint)
        {
            // First pass or resize: the root re-measures and re-arranges under the new constraint.
            _hasRootConstraint = true;
            _rootConstraint = rootConstraint;
            _root.InvalidateMeasure();
            _root.InvalidateArrange();
            if (_root.MeasureQueuedIn != this)
                EnqueueMeasure(_root);
            if (_root.ArrangeQueuedIn != this)
                EnqueueArrange(_root);
        }

        RestoreAbandoned();

        var converged = false;
        for (var retry = 0; retry < 2; retry++)
        {
            var passesExhausted = true;
            for (var pass = 0; pass < PassCap; pass++)
            {
                if (_measureQueue.Count == 0 && _arrangeQueue.Count == 0)
                {
                    passesExhausted = false;
                    break;
                }

                // Snapshot-bounded drains: entries enqueued during a drain are processed in the
                // NEXT pass, so a self-invalidating element cannot spin a drain forever.
                DrainMeasure(rootConstraint);
                DrainArrange(rootConstraint);
            }

            if (passesExhausted && (_measureQueue.Count > 0 || _arrangeQueue.Count > 0))
            {
                var offender = _measureQueue.Count > 0 ? _measureQueue.PeekElement() : _arrangeQueue.PeekElement();
                LayoutDiagnostics.Emit(
                    LayoutDiagnosticKind.LayoutCycle, offender,
                    $"Layout did not converge within {PassCap} passes; '{offender?.GetType().Name}' is still " +
                    "invalid — a measure/arrange callback is re-invalidating layout every pass (doc §5.3).");
            }

            LayoutUpdated?.Invoke();

            if (!HasPendingWork)
            {
                converged = true;
                break;
            }
        }

        if (!converged && HasPendingWork)
        {
            LayoutDiagnostics.Emit(
                LayoutDiagnosticKind.ResidualLayoutWork, _measureQueue.PeekElement() ?? _arrangeQueue.PeekElement(),
                "Layout work is still pending after the bounded LayoutUpdated re-run; it slips to the next tick.");
        }
    }

    /// <summary>
    /// Drops the queued work for this tick — the S6 give-up path. Dirty elements stay invalid and
    /// the dropped entries re-queue at the start of the next <see cref="RunLayoutPass"/>.
    /// </summary>
    /// <remarks>
    /// Parked work does <b>not</b> re-arm the frame loop's idle guard by itself (that is the
    /// point — a non-converging layout must not spin the loop), and a parked element's own
    /// <c>InvalidateMeasure</c> early-outs while it is already invalid. Driven through the S6
    /// facade this turns matrix L89's "degrades to one-frame lag" into "parked until the next
    /// <em>distinct</em> invalidation anywhere in the tree" — the accepted trade for pathological
    /// (post-32-pass non-converging) input; any ordinary invalidation restores the parked entries
    /// alongside its own pass.
    /// </remarks>
    public void AbandonPendingLayout()
    {
        _root.VerifyAccess();

        while (_measureQueue.Count > 0)
        {
            var element = _measureQueue.Pop();
            if (element.MeasureQueuedIn == this)
                (_abandonedMeasure ??= []).Add(element);
        }

        while (_arrangeQueue.Count > 0)
        {
            var element = _arrangeQueue.Pop();
            if (element.ArrangeQueuedIn == this)
                (_abandonedArrange ??= []).Add(element);
        }
    }

    private void RestoreAbandoned()
    {
        if (_abandonedMeasure is { Count: > 0 } measures)
        {
            for (var i = 0; i < measures.Count; i++)
            {
                if (measures[i].MeasureQueuedIn == this)
                    _measureQueue.Push(measures[i].Depth, measures[i]);
            }

            measures.Clear();
        }

        if (_abandonedArrange is { Count: > 0 } arranges)
        {
            for (var i = 0; i < arranges.Count; i++)
            {
                if (arranges[i].ArrangeQueuedIn == this)
                    _arrangeQueue.Push(arranges[i].Depth, arranges[i]);
            }

            arranges.Clear();
        }
    }

    private void DrainMeasure(Size rootConstraint)
    {
        var bound = _measureQueue.Count;
        for (var i = 0; i < bound && _measureQueue.Count > 0; i++)
        {
            var element = _measureQueue.Pop();
            if (element.MeasureQueuedIn != this)
                continue; // stale: re-rooted under another manager (lazy deletion)

            element.MeasureQueuedIn = null;
            if (element.IsMeasureValid || element.VisualRoot != _root)
                continue;

            element.Measure(element.HasMeasureConstraint ? element.LastMeasureConstraint : rootConstraint);
        }
    }

    private void DrainArrange(Size rootConstraint)
    {
        var bound = _arrangeQueue.Count;
        for (var i = 0; i < bound && _arrangeQueue.Count > 0; i++)
        {
            var element = _arrangeQueue.Pop();
            if (element.ArrangeQueuedIn != this)
                continue;

            element.ArrangeQueuedIn = null;
            if (element.IsArrangeValid || element.VisualRoot != _root)
                continue;

            if (ReferenceEquals(element, _root))
                element.Arrange(new Rect(0, 0, rootConstraint.Columns, rootConstraint.Rows)); // never the cached rect — it goes stale on resize
            else if (element.HasArrangeRect)
                element.Arrange(element.LastArrangeRect);
            // A never-arranged non-root stays for its parent's ArrangeOverride to place.
        }
    }

    /// <summary>Enqueues a measure-invalid element (idempotent per manager — the enqueued marker dedupes).</summary>
    internal void EnqueueMeasure(UIElement element)
    {
        if (element.MeasureQueuedIn == this)
            return;

        element.MeasureQueuedIn = this;
        _measureQueue.Push(element.Depth, element);
    }

    /// <summary>Enqueues an arrange-invalid element (idempotent per manager).</summary>
    internal void EnqueueArrange(UIElement element)
    {
        if (element.ArrangeQueuedIn == this)
            return;

        element.ArrangeQueuedIn = this;
        _arrangeQueue.Push(element.Depth, element);
    }

    /// <summary>
    /// An array-backed binary min-heap of <c>(depth, element)</c>: shallowest-first so parents
    /// re-run before stale children. Stale entries (elements re-rooted elsewhere) are skipped at
    /// pop via the per-element queued-in marker (lazy deletion).
    /// </summary>
    private sealed class DepthHeap
    {
        private (int Depth, UIElement Element)[] _items = new (int, UIElement)[16];

        /// <summary>The number of entries (including any stale entries awaiting lazy deletion).</summary>
        public int Count { get; private set; }

        /// <summary>Adds an entry.</summary>
        public void Push(int depth, UIElement element)
        {
            if (Count == _items.Length)
                Array.Resize(ref _items, _items.Length * 2);

            var index = Count++;
            _items[index] = (depth, element);

            while (index > 0)
            {
                var parent = (index - 1) >> 1;
                if (_items[parent].Depth <= _items[index].Depth)
                    break;

                (_items[parent], _items[index]) = (_items[index], _items[parent]);
                index = parent;
            }
        }

        /// <summary>Removes and returns the shallowest entry.</summary>
        public UIElement Pop()
        {
            var result = _items[0].Element;
            var last = --Count;
            _items[0] = _items[last];
            _items[last] = default;

            var index = 0;
            while (true)
            {
                var left = 2 * index + 1;
                if (left >= Count)
                    break;

                var smallest = left;
                var right = left + 1;
                if (right < Count && _items[right].Depth < _items[left].Depth)
                    smallest = right;

                if (_items[index].Depth <= _items[smallest].Depth)
                    break;

                (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
                index = smallest;
            }

            return result;
        }

        /// <summary>The shallowest entry's element without removal, or null when empty.</summary>
        public UIElement? PeekElement() => Count > 0 ? _items[0].Element : null;
    }
}
