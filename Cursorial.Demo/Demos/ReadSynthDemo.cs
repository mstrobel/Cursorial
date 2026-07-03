using System.Text;

using Cursorial.Input;
using Cursorial.Terminal;

// ReSharper disable CheckNamespace

// One-shot / streaming demo. Mirrors the plain `read` demo (ReadEventsAsync) but wraps the
// session's input device in key-up/repeat synthesis (a device decorator) plus mouse-click
// synthesis (an IInputTransformer), so terminals that don't natively report releases or click
// gestures still surface them as synthesized events. No render loop, so it implements IDemo
// directly rather than extending InteractiveDemo.
internal sealed class ReadSynthDemo : IDemo
{
    public string Name => "read-synth";
    public IReadOnlyList<string> Aliases => [];
    public string Description =>
        "Stream input events with key-up + mouse-click synthesis.";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Reading input events with key-up + mouse-click synthesis. Press Ctrl+C to return.");
        Console.WriteLine(
            $"Synthesized releases appear after the up timeout ({KeyReleaseSynthesizer.DefaultUpTimeout.TotalMilliseconds:0} ms); " +
            $"subsequent Downs within the repeat timeout ({KeyReleaseSynthesizer.DefaultRepeatTimeout.TotalMilliseconds:0} ms) " +
            $"are marked IsRepeat.");
        Console.WriteLine("Click the same cell rapidly to see ClickCount climb and synthesized Click events appear.");
        Console.WriteLine();

        int eventCount = 0;
        await using (var session = await TerminalSession.OpenAsync())
        {
            // Compose two transforms over the session's input: key-up/repeat synthesis (a device
            // decorator) and mouse-click synthesis (an IInputTransformer applied via WithClickSynthesis).
            // Disposing the outer device cascades through the chain to the session's input device; the
            // outer `await using` on the session handles transport restoration.
            await using var device = new KeyReleaseSynthesizer(session.Input)
                .WithClickSynthesis(new MouseClickOptions
                                    {
                                        SynthesizeClickEvents = true,
                                        ClickCount = ClickCountTarget.Click,
                                    });
            using var stopCts = new CancellationTokenSource();

            try
            {
                await foreach (var inputEvent in device.ReadAllAsync(stopCts.Token))
                {
                    eventCount++;

                    var label = inputEvent.Synthesized ? " (synth)" : "";
                    await session.Output.Writer.WriteAsync(
                        Encoding.UTF8.GetBytes($"  [{eventCount,4}]{label} {DemoSupport.FormatEvent(inputEvent)}\r\n"));
                    await session.Output.Writer.FlushAsync();

                    if (DemoSupport.IsStopSignal(inputEvent))
                    {
                        stopCts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
        }

        Console.WriteLine();
        Console.WriteLine($"Stopped after {eventCount} event(s).");
    }
}
