using System.Buffers;

using Cursorial.Terminal.Stdio;

// ReSharper disable CheckNamespace

// One-shot XTVERSION + DA1 capture. Writes the probe sequences directly through raw stdio
// transports, then reads back whatever the terminal sends for one second and dumps it as hex +
// ASCII. Implements IDemo directly (no render loop, so no InteractiveDemo harness). Migrated
// verbatim from the original Program.cs ProbeAsync.
internal sealed class ProbeDemo : IDemo
{
    public string Name => "probe";
    public IReadOnlyList<string> Aliases => [];
    public string Description =>
        "Write XTVERSION (CSI > q) + DA1 (CSI c), capture the raw response.";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Probing: writing XTVERSION (CSI > q) + DA1 (CSI c).");
        Console.WriteLine("Reading raw response bytes for 1 second...");
        Console.WriteLine();

        var collected = new List<byte>();

        await using (var transports = StdioTransports.Open())
        {
            await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'>', (byte)'q' });
            await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'c' });
            await transports.Sink.Writer.FlushAsync();

            // Task.WhenAny timeout — the underlying read syscall doesn't honor cancellation
            // mid-call, so we rely on the timeout task to break the wait.
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));
            var reader = transports.Source.Reader;

            while (true)
            {
                var readTask = reader.ReadAsync().AsTask();
                var completed = await Task.WhenAny(readTask, timeoutTask);
                if (completed != readTask) break;

                var result = await readTask;
                collected.AddRange(result.Buffer.ToArray());
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }

        if (collected.Count == 0)
        {
            Console.WriteLine("(no response received within 1 second)");
            return;
        }

        Console.WriteLine($"Received {collected.Count} byte(s):");
        Console.Write("  hex:   ");
        foreach (byte b in collected) Console.Write($"{b:X2} ");
        Console.WriteLine();
        Console.Write("  ascii: ");
        foreach (byte b in collected) Console.Write(b is >= 0x20 and < 0x7F ? (char)b : '·');
        Console.WriteLine();
    }
}
