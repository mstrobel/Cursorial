namespace Cursorial.UI.Data;

/// <summary>The severity of a <see cref="BindingTraceEvent"/> (design doc §6.10).</summary>
public enum BindingTraceLevel : byte
{
    /// <summary>Tracing disabled.</summary>
    Off,

    /// <summary>Errors only (the default).</summary>
    Error,

    /// <summary>Errors and warnings.</summary>
    Warning,

    /// <summary>Everything, including informational and per-binding verbose events.</summary>
    Verbose
}

/// <summary>The kind of a binding failure (design doc §6.10; the diagnostics taxonomy).</summary>
public enum BindingFailureKind : byte
{
    /// <summary>Not a failure (informational/verbose).</summary>
    None,

    /// <summary>A path step could not be resolved against the source.</summary>
    PathError,

    /// <summary>The anchor/source could not be resolved (parked, retry on attach).</summary>
    SourceMissing,

    /// <summary>An <c>ElementName</c> was not found in scope after attach.</summary>
    NameNotFound,

    /// <summary>A <c>FindAncestor</c> walk found no matching ancestor.</summary>
    AncestorNotFound,

    /// <summary>A converter's <c>Convert</c> failed (threw or returned <c>UnsetValue</c>).</summary>
    ConversionFailed,

    /// <summary>A converter's <c>ConvertBack</c> failed; no write to the source.</summary>
    ConvertBackFailed,

    /// <summary>A target → source write failed (type gap, composite format); no write.</summary>
    SourceUpdateFailed,

    /// <summary>Source → target type conversion failed.</summary>
    TypeMismatch,

    /// <summary>A compiled binding's anchor resolved to a non-<c>TSource</c> object.</summary>
    SourceTypeMismatch
}

/// <summary>
/// One recorded binding-diagnostics event (design doc §6.10). A readonly record struct so the ring
/// buffer holds them by value.
/// </summary>
/// <param name="Level">The severity.</param>
/// <param name="Kind">The failure kind (or <see cref="BindingFailureKind.None"/>).</param>
/// <param name="Path">The binding path text.</param>
/// <param name="TargetDescription">The target descriptor (e.g. <c>Widget#a.Text</c>).</param>
/// <param name="Message">The human-readable message.</param>
/// <param name="Timestamp">Monotonic milliseconds (<see cref="Environment.TickCount64"/>).</param>
public readonly record struct BindingTraceEvent(
    BindingTraceLevel Level,
    BindingFailureKind Kind,
    string Path,
    string TargetDescription,
    string Message,
    long Timestamp);

/// <summary>A consumer of binding-diagnostics events (status bars, test asserts, dev-tools).</summary>
public interface IBindingTraceSink
{
    /// <summary>Receives an event whose severity is ≥ <see cref="BindingDiagnostics.Level"/>.</summary>
    void Write(in BindingTraceEvent e);
}

/// <summary>
/// The terminal-appropriate binding diagnostics façade (design doc §6.10). <b>Never writes to the
/// terminal</b> — stdout is the application's screen, and a stray write desyncs the
/// <c>FrameRenderer</c>. A 256-entry always-on ring (<see cref="RecentEvents"/>) makes post-mortem
/// answerable; an env-gated file sink (<c>CURSORIAL_BINDING_TRACE</c>) and app sinks/handlers fan
/// out. Ring policy (pinned): Warning/Error events are always constructed and ring-recorded
/// (failure paths only — the happy path allocates nothing for diagnostics); Verbose events are
/// constructed only when <see cref="Level"/> is <see cref="BindingTraceLevel.Verbose"/> or the
/// binding's <c>Trace</c> flag is set.
/// </summary>
public static class BindingDiagnostics
{
    private const int RingCapacity = 256;

    private static readonly Lock Gate = new();
    private static readonly BindingTraceEvent[] Ring = new BindingTraceEvent[RingCapacity];
    private static readonly List<IBindingTraceSink> Sinks = [];
    private static int _ringCount;
    private static int _ringNext;
    private static int _errorCount;
    private static IBindingTraceSink? _fileSink;
    private static bool _fileSinkResolved;

    /// <summary>The minimum severity delivered to sinks and <see cref="TraceEmitted"/>. Default <see cref="BindingTraceLevel.Error"/>.</summary>
    public static BindingTraceLevel Level { get; set; } = BindingTraceLevel.Error;

    /// <summary>The number of Error-severity events recorded over the lifetime of the process.</summary>
    public static int ErrorCount
    {
        get
        {
            lock (Gate)
                return _errorCount;
        }
    }

    /// <summary>The most-recent (≤256) recorded events, oldest first.</summary>
    public static IReadOnlyList<BindingTraceEvent> RecentEvents
    {
        get
        {
            lock (Gate)
            {
                var result = new BindingTraceEvent[_ringCount];
                var start = _ringCount < RingCapacity ? 0 : _ringNext;
                for (var i = 0; i < _ringCount; i++)
                    result[i] = Ring[(start + i) % RingCapacity];
                return result;
            }
        }
    }

    /// <summary>An app hook (status bars, test asserts) receiving events with severity ≥ <see cref="Level"/>.</summary>
    public static event Action<BindingTraceEvent>? TraceEmitted;

    /// <summary>Adds a sink receiving events with severity ≥ <see cref="Level"/>.</summary>
    public static void AddSink(IBindingTraceSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
            Sinks.Add(sink);
    }

    /// <summary>Removes a previously added sink.</summary>
    public static void RemoveSink(IBindingTraceSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
            Sinks.Remove(sink);
    }

    /// <summary>
    /// "Why does this property have this value?" — every expression on
    /// <paramref name="property"/> of <paramref name="target"/> across all lanes (LocalValue,
    /// frame-hosted, watch-only, DirectProperty): status, resolved source chain, last produced
    /// value, last failure. Registry-backed (release build).
    /// </summary>
    public static BindingExplanation Explain(UIObject target, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        return BindingRegistry.Explain(target, property);
    }

    /// <summary>
    /// A post-session dump of the ring (call <b>after</b> session disposal in the canonical teardown
    /// order — design doc §6.10/§10.7).
    /// </summary>
    public static void DumpTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (var e in RecentEvents)
            writer.WriteLine($"[{e.Level}] {e.Kind} {e.TargetDescription} ({e.Path}): {e.Message}");
    }

    /// <summary>
    /// Whether an event of <paramref name="level"/> should be constructed at all. Warning/Error are
    /// always constructed (failure paths only); Verbose/Info only when the level admits it or the
    /// binding opted in via <paramref name="bindingTrace"/>.
    /// </summary>
    internal static bool ShouldConstruct(BindingTraceLevel level, bool bindingTrace)
        => level switch
        {
            BindingTraceLevel.Error or BindingTraceLevel.Warning => true,
            _ => bindingTrace || Level == BindingTraceLevel.Verbose
        };

    /// <summary>
    /// Records an event: always ring-recorded (the ring is on regardless of <see cref="Level"/>),
    /// then fanned to the file sink, <see cref="TraceEmitted"/>, and app sinks for severity ≥
    /// <see cref="Level"/>. Callers gate construction via <see cref="ShouldConstruct"/>.
    /// </summary>
    internal static void Record(in BindingTraceEvent e)
    {
        lock (Gate)
        {
            Ring[_ringNext] = e;
            _ringNext = (_ringNext + 1) % RingCapacity;
            if (_ringCount < RingCapacity)
                _ringCount++;
            if (e.Level == BindingTraceLevel.Error)
                _errorCount++;
        }

        if (e.Level == BindingTraceLevel.Off || e.Level < Level)
            return;

        ResolveFileSink()?.Write(in e);
        TraceEmitted?.Invoke(e);

        // Snapshot the sink list and release the lock before delivery: a sink that re-enters the
        // façade (RecentEvents, AddSink, DumpTo, …) must not deadlock on Gate.
        IBindingTraceSink[]? snapshot;
        lock (Gate)
            snapshot = Sinks.Count > 0 ? [.. Sinks] : null;

        if (snapshot is not null)
            foreach (var sink in snapshot)
                sink.Write(in e);
    }

    /// <summary>Test-only ring reset (registrations are process-global).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Array.Clear(Ring);
            _ringCount = 0;
            _ringNext = 0;
            _errorCount = 0;
            Sinks.Clear();
        }

        TraceEmitted = null;
        Level = BindingTraceLevel.Error;
    }

    private static IBindingTraceSink? ResolveFileSink()
    {
        if (_fileSinkResolved)
            return _fileSink;

        lock (Gate)
        {
            if (_fileSinkResolved)
                return _fileSink;

            var path = Environment.GetEnvironmentVariable("CURSORIAL_BINDING_TRACE");
            if (!string.IsNullOrEmpty(path))
                _fileSink = new FileTraceSink(path);
            _fileSinkResolved = true;
            return _fileSink;
        }
    }

    private sealed class FileTraceSink(string path) : IBindingTraceSink
    {
        private readonly Lock _writeGate = new();

        public void Write(in BindingTraceEvent e)
        {
            lock (_writeGate)
            {
                try
                {
                    File.AppendAllText(path, $"[{e.Level}] {e.Kind} {e.TargetDescription} ({e.Path}): {e.Message}{Environment.NewLine}");
                }
                catch (IOException)
                {
                    // A trace sink must never crash the app or desync the terminal.
                }
            }
        }
    }
}
