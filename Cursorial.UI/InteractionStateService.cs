using Cursorial.UI.Input;

namespace Cursorial.UI;

/// <summary>
/// The application-wide interaction-state coordinator behind <see cref="IInteractionStateSink"/>
/// (matrix ND11): batches flips into one observer notification per element per batch, delivers
/// unbatched flips immediately (always after commit), and fans
/// <see cref="InteractionState.Pressed"/> transitions into the dispatcher's pressed-holder set
/// (ND12 — membership is live, never deferred to the batch flush, so the C8 terminal-focus-out
/// clear sees an accurate set mid-batch).
/// </summary>
/// <remarks>
/// UI-thread only; zero steady-state allocation (the pending list is retained, per-element batch
/// membership is a flag on the element). Exception safety: the <see langword="using"/>-scoped
/// <see cref="InteractionUpdateScope"/> flushes on every unwind path, so a throwing handler cannot
/// leave a batch open.
/// </remarks>
internal sealed class InteractionStateService
{
    private List<(UIElement Element, InteractionState EntryState)> _pending = [];
    private List<(UIElement Element, InteractionState EntryState)> _flushScratch = [];
    private int _batchDepth;
    private bool _flushing;

    /// <summary>The installed per-application observer (P3's styling engine; test sinks) — null until installed.</summary>
    internal IInteractionStateObserver? Observer;

    /// <summary>The pressed-holder fan-in target (assigned by the application after dispatcher construction).</summary>
    internal InputDispatcher? PressedSink;

    /// <summary>Opens (or nests) a batch; flips flush as one notification per element at the outermost dispose.</summary>
    internal InteractionUpdateScope BeginUpdate()
    {
        _batchDepth++;
        return new InteractionUpdateScope(this);
    }

    /// <summary>
    /// Called by <see cref="UIElement"/> after a state commit (the flip already happened —
    /// equality-gated at the element). Routes the Pressed fan-in immediately, then either notifies
    /// the observer (unbatched) or records the element's first-flip entry state for the flush.
    /// </summary>
    internal void OnStateCommitted(UIElement element, InteractionState oldState, InteractionState newState)
    {
        if (((oldState ^ newState) & InteractionState.Pressed) != 0)
            PressedSink?.OnPressedChanged(element, (newState & InteractionState.Pressed) != 0);

        if (_batchDepth == 0)
        {
            Observer?.OnInteractionStateChanged(element, oldState, newState);
            return;
        }

        if (element.EnterInteractionBatch())
            _pending.Add((element, oldState)); // first flip in this batch — entry state captured
    }

    /// <summary>Closes one nesting level; the outermost close flushes (first-flip order, net-zero flips silent).</summary>
    internal void EndUpdate()
    {
        if (_batchDepth == 0)
            return; // double-dispose of a copied scope — tolerated, never underflows

        if (--_batchDepth > 0)
            return;

        FlushPending();
    }

    /// <summary>
    /// The outermost-dispose flush, re-entrancy-hardened (ND29 — the P3 styling engine reacts to
    /// notifications by opening its own batches): the pending list is swapped out before delivery,
    /// so flips made by observer callbacks land in a fresh list and deliver exactly once — after
    /// the original entries — and a nested <see cref="EndUpdate"/> reaching depth 0 mid-flush never
    /// replays the in-flight list (the <c>_flushing</c> guard; the outer loop drains it instead).
    /// Both lists are retained (zero steady-state allocation).
    /// </summary>
    private void FlushPending()
    {
        if (_flushing)
            return; // nested EndUpdate during a flush — the outer drain loop picks up the new entries

        _flushing = true;
        try
        {
            while (_pending.Count > 0)
            {
                var batch = _pending;
                _pending = _flushScratch; // callback flips accumulate here
                _flushScratch = batch;

                for (var i = 0; i < batch.Count; i++)
                {
                    var (element, entryState) = batch[i];
                    element.ExitInteractionBatch();

                    var current = element.InteractionStateInternal;
                    if (current != entryState)
                        Observer?.OnInteractionStateChanged(element, entryState, current);
                }

                batch.Clear();
            }
        }
        finally
        {
            _flushing = false;
        }
    }
}

/// <summary>
/// A <see langword="using"/>-scoped interaction-state batch (design doc §3.2 /
/// <see cref="IInteractionStateSink.BeginInteractionUpdate"/>). Disposal closes one nesting level;
/// the outermost dispose delivers the coalesced observer notifications. <c>default</c> is a
/// <b>no-op scope</b> — no batch is opened and disposal is harmless; it is what
/// <see cref="IInteractionStateSink.BeginInteractionUpdate"/> returns outside a running
/// application, so control authors never need a null/running check around the
/// <see langword="using"/>.
/// </summary>
public struct InteractionUpdateScope : IDisposable
{
    private InteractionStateService? _service;

    internal InteractionUpdateScope(InteractionStateService service) => _service = service;

    /// <summary>Closes the scope (idempotent on this instance).</summary>
    public void Dispose()
    {
        _service?.EndUpdate();
        _service = null;
    }
}
