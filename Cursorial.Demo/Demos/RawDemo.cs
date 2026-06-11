using System.Buffers;
using System.Text;

using Cursorial.Terminal.Stdio;

// ReSharper disable CheckNamespace

// One-shot raw byte dump. Opens platform stdio transports directly (no negotiation, no parsing) and
// echoes every byte read from stdin as a hex/char line until Ctrl+C. Implements IDemo directly
// (no render loop, so no InteractiveDemo harness). Migrated verbatim from Program.DumpRawAsync.
internal sealed class RawDemo : IDemo
{
    public string Name => "raw";
    public IReadOnlyList<string> Aliases => [];
    public string Description =>
        "Dump raw bytes from stdin verbatim — no parsing.";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Dumping raw stdin bytes. Press Ctrl+C to stop.");
        Console.WriteLine();

        await using (var transports = StdioTransports.Open())
        {
            using var stopCts = new CancellationTokenSource();
            var reader = transports.Source.Reader;

            try
            {
                while (!stopCts.IsCancellationRequested)
                {
                    var result = await reader.ReadAsync(stopCts.Token);
                    var bytes = result.Buffer.ToArray();
                    reader.AdvanceTo(result.Buffer.End);

                    foreach (byte b in bytes)
                    {
                        var msg = $"  byte 0x{b:X2}{(b is >= 0x20 and < 0x7F ? $" '{(char)b}'" : "")}\r\n";
                        await transports.Sink.Writer.WriteAsync(Encoding.UTF8.GetBytes(msg));

                        if (b == 0x03) stopCts.Cancel(); // Ctrl+C
                    }
                    await transports.Sink.Writer.FlushAsync();

                    if (result.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) { }
        }

        Console.WriteLine();
        Console.WriteLine("Raw dump stopped.");
    }
}
