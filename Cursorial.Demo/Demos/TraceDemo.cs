using System.Buffers;
using System.Text;

using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Terminal;
using Cursorial.Terminal.Stdio;

// ReSharper disable CheckNamespace

// Protocol-debugging demo: streams raw stdin bytes alongside the decoded InputEvents they produce,
// side-by-side, so you can see exactly which wire bytes a keypress / mouse action / paste emits and
// how the interpreter frames them. Runs its own raw-mode handshake (with a maximally verbose Kitty
// keyboard flag set so every key — including printable ones — arrives as a fully-annotated escape
// sequence) rather than the standard demo harness. One-shot; Ctrl+C to stop. Implements IDemo
// directly (no render loop, so no InteractiveDemo harness).
internal sealed class TraceDemo : IDemo
{
    public string Name => "trace";
    public IReadOnlyList<string> Aliases => [];
    public string Description =>
        "Live raw bytes + decoded events side-by-side for protocol debugging.";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Tracing raw bytes + decoded events. Press Ctrl+C to stop.");
        Console.WriteLine();

        await using var transports = StdioTransports.Open();
        var mode = new VtInputMode();
        var negotiator = new VtTerminalNegotiator(transports.Source, transports.Sink, mode);

        try
        {
            // For tracing we want every key event to arrive as an escape sequence carrying full
            // modifier state — including for plain text keys. Otherwise, Kitty's text-shortcut
            // optimization elides the modifier annotation on presses of printable keys, leaving
            // an asymmetric press-vs.-release picture in the trace.
            var traceOptions = new NegotiationOptions
            {
                KittyKeyboardFlags = KittyKeyboardFlags.DisambiguateEscapeCodes
                                     | KittyKeyboardFlags.ReportEventTypes
                                     | KittyKeyboardFlags.ReportAlternateKeys
                                     | KittyKeyboardFlags.ReportAssociatedText
                                     | KittyKeyboardFlags.ReportAllKeysAsEscapeCodes
            };
            await negotiator.NegotiateAsync(traceOptions);

            var classifier = new VtSequenceClassifier();
            var events = new List<InputEvent>();
            var interpreter = new VtInputInterpreter(mode, new TraceEventSink(events));

            using var stopCts = new CancellationTokenSource();
            var reader = transports.Source.Reader;
            var writer = transports.Sink.Writer;
            var ambiguityTimeout = TimeSpan.FromMilliseconds(50);

            Task<System.IO.Pipelines.ReadResult>? pendingRead = null;
            try
            {
                while (!stopCts.IsCancellationRequested)
                {
                    pendingRead ??= reader.ReadAsync(stopCts.Token).AsTask();
                    var completed = await Task.WhenAny(pendingRead, Task.Delay(ambiguityTimeout, stopCts.Token));

                    if (completed != pendingRead)
                    {
                        // Idle window — flush any pending bare-ESC so an Escape keypress doesn't
                        // sit invisibly inside the classifier.
                        classifier.Flush(interpreter);
                        await DemoSupport.DrainEventsAsync(events, writer, stopCts);
                        await writer.FlushAsync();
                        continue;
                    }

                    var result = await pendingRead;
                    pendingRead = null;

                    var buffer = result.Buffer;
                    if (buffer.Length > 0)
                    {
                        var bytes = buffer.ToArray();
                        await writer.WriteAsync(Encoding.UTF8.GetBytes(
                            $"RX  {DemoSupport.BytesToHex(bytes)}  |{BytesToPrintable(bytes)}|\r\n"));

                        foreach (var segment in buffer)
                            classifier.Process(segment.Span, interpreter);
                    }
                    reader.AdvanceTo(buffer.End);

                    await DemoSupport.DrainEventsAsync(events, writer, stopCts);
                    await writer.FlushAsync();

                    if (result.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
        }
        finally
        {
            await negotiator.DisposeAsync();
        }
    }

    private static string BytesToPrintable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '·');
        return sb.ToString();
    }
}

file sealed class TraceEventSink(List<InputEvent> events) : IInputEventSink
{
    public void OnInputEvent(InputEvent inputEvent) => events.Add(inputEvent);
}
