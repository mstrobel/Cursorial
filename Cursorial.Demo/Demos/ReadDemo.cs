using System.Text;

using Cursorial.Terminal;

// Streaming reference demo. Opens a session and streams decoded input events to stdout one per line
// until Ctrl+C, then prints a count. Implements IDemo directly (no render loop, so no InteractiveDemo
// harness). Behavior is migrated verbatim from the original Program.cs ReadEventsAsync method.
internal sealed class ReadDemo : IDemo
{
    public string Name => "read";
    public IReadOnlyList<string> Aliases => ["events"];
    public string Description =>
        "Open a session and stream decoded input events to stdout (Ctrl+C to stop).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Reading input events. Press Ctrl+C to return to the prompt.");
        Console.WriteLine();

        int eventCount = 0;
        await using (var session = await TerminalSession.OpenAsync())
        {
            using var stopCts = new CancellationTokenSource();

            try
            {
                var message = $"Your input device is {session.Input}.\n\n";

                await session.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes(message), stopCts.Token);
                await session.Output.Writer.FlushAsync(stopCts.Token);

                await foreach (var inputEvent in session.Input.ReadAllAsync(stopCts.Token))
                {
                    eventCount++;

                    // Raw mode (OPOST off) — write \r\n explicitly so each line wraps back to
                    // column 0 instead of stair-stepping right.
                    await session.Output.Writer.WriteAsync(
                        Encoding.UTF8.GetBytes($"  [{eventCount,4}] {DemoSupport.FormatEvent(inputEvent)}\r\n"));
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
