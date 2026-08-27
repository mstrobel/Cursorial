using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

using Cursorial.Terminal.Stdio;

// ReSharper disable CheckNamespace

// Times the OSC 11 (background colour) + DA1 sentinel round-trip — the wall-clock cost of a
// standalone light/dark theme probe at startup. Writes `OSC 11 ? · DA1` and measures until the DA1
// reply lands: DA1 is the sentinel every terminal answers, so the wait ends there whether or not
// OSC 11 itself was supported (no per-query timeout in the common case). Reports the cold (first)
// sample plus a warm min/median/mean/max, and decodes the background colour + a light/dark verdict
// from the OSC 11 reply. Run it in each terminal to get real per-terminal figures.
//
// All console output happens BEFORE opening / AFTER closing the raw transports — printing while
// raw mode is on would skip the ONLCR carriage-return and stair-step the lines.
internal sealed class BackgroundProbeDemo : IDemo
{
    public string Name => "bgprobe";
    public IReadOnlyList<string> Aliases => ["osc11"];
    public string Description =>
        "Time the OSC 11 (background) + DA1 round-trip — the startup cost of a light/dark probe.";

    private const int Iterations = 12;
    private static readonly TimeSpan PerQueryTimeout = TimeSpan.FromMilliseconds(500);

    public async Task RunAsync(string argument)
    {
        Console.WriteLine($"Timing OSC 11 (background) + DA1 sentinel round-trip × {Iterations} (+1 cold warm-up).");
        Console.WriteLine("The DA1 reply is the sentinel — the wait ends there, supported or not.");
        Console.WriteLine();

        var query = BuildQuery();
        var samples = new List<double>();
        double? cold = null;
        int timeouts = 0;
        byte[]? firstResponse = null;

        await using (var transports = StdioTransports.Open())
        {
            var writer = transports.Sink.Writer;
            var reader = transports.Source.Reader;

            for (int i = -1; i < Iterations; i++) // i == -1 is the cold warm-up (reported separately)
            {
                var buffer = new List<byte>();
                var sw = Stopwatch.StartNew();

                await writer.WriteAsync(query);
                await writer.FlushAsync();

                bool gotDa1 = await ReadUntilDa1Async(reader, buffer, PerQueryTimeout);
                sw.Stop();

                firstResponse ??= gotDa1 ? buffer.ToArray() : null;

                if (i < 0)
                {
                    if (gotDa1) cold = sw.Elapsed.TotalMilliseconds;
                    continue;
                }

                if (gotDa1) samples.Add(sw.Elapsed.TotalMilliseconds);
                else timeouts++;
            }
        }

        // Raw mode is restored now — safe to print.
        ReportBackground(firstResponse);
        Console.WriteLine();
        ReportTiming(cold, samples, timeouts);
    }

    private static byte[] BuildQuery()
    {
        // OSC 11 ? (background-colour query, ST-terminated) + DA1 (CSI c) sentinel.
        ReadOnlySpan<byte> osc11 = "\x1b]11;?\x1b\\"u8;
        ReadOnlySpan<byte> da1 = "\x1b[c"u8;
        var buf = new byte[osc11.Length + da1.Length];
        osc11.CopyTo(buf);
        da1.CopyTo(buf.AsSpan(osc11.Length));
        return buf;
    }

    private static async Task<bool> ReadUntilDa1Async(PipeReader reader, List<byte> buffer, TimeSpan timeout)
    {
        var timeoutTask = Task.Delay(timeout);
        while (true)
        {
            var readTask = reader.ReadAsync().AsTask();
            if (await Task.WhenAny(readTask, timeoutTask) != readTask)
                return false; // sentinel never arrived

            var result = await readTask;
            buffer.AddRange(result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);

            if (ContainsDa1(buffer)) return true;
            if (result.IsCompleted) return false;
        }
    }

    // A DA1 reply is a CSI sequence terminated by 'c': ESC [ (params/intermediates 0x20-0x3F)* c.
    // OSC replies begin ESC ] and end BEL/ST, so an ESC [ … c is unambiguously the DA response.
    private static bool ContainsDa1(List<byte> buffer)
    {
        for (int i = 0; i + 1 < buffer.Count; i++)
        {
            if (buffer[i] != 0x1B || buffer[i + 1] != (byte) '[') continue;
            for (int j = i + 2; j < buffer.Count; j++)
            {
                byte b = buffer[j];
                if (b == (byte) 'c') return true;      // CSI … c → DA1
                if (b is < 0x20 or > 0x3F) break;      // a different CSI final byte → not this one
            }
        }
        return false;
    }

    private static void ReportBackground(byte[]? response)
    {
        if (response is null || !TryParseOsc11(response, out int r, out int g, out int b))
        {
            Console.WriteLine("Background (OSC 11): not answered — the terminal does not support the query.");
            Console.WriteLine("  Light/dark can't be detected here; lean on Color.Default style overrides.");
            return;
        }

        // Rec. 601 luma; the conventional light/dark split sits at mid-grey.
        double luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        string verdict = luma >= 0.5 ? "LIGHT" : "DARK";
        Console.WriteLine($"Background (OSC 11): rgb({r},{g},{b})  luma={luma:0.00}  →  {verdict}");
    }

    private static bool TryParseOsc11(byte[] response, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = Encoding.ASCII.GetString(response);
        int idx = s.IndexOf("rgb:", StringComparison.Ordinal);
        if (idx < 0) return false;

        var parts = s[(idx + 4)..].Split('/');
        if (parts.Length < 3) return false;

        r = TopByte(parts[0]);
        g = TopByte(parts[1]);
        b = TopByte(parts[2]);
        return true;

        // A channel is 1-4 hex digits (e.g. "ffff" or "ff"); scale the most-significant byte to 0-255.
        static int TopByte(string part)
        {
            var hex = new string(part.TakeWhile(Uri.IsHexDigit).ToArray());
            if (hex.Length == 0) return 0;
            var top = hex.Length >= 2 ? hex[..2] : new string(hex[0], 2);
            return Convert.ToInt32(top, 16);
        }
    }

    private static void ReportTiming(double? cold, List<double> samples, int timeouts)
    {
        if (cold is { } c)
            Console.WriteLine($"Cold (first query, real startup): {c:0.000} ms");
        else
            Console.WriteLine("Cold (first query): TIMEOUT — the terminal never answered DA1 (unexpected).");

        if (samples.Count == 0)
        {
            Console.WriteLine("Warm: no successful samples.");
        }
        else
        {
            samples.Sort();
            Console.WriteLine($"Warm over {samples.Count} samples (ms):  " +
                              $"min {samples[0]:0.000}   median {samples[samples.Count / 2]:0.000}   " +
                              $"mean {samples.Average():0.000}   max {samples[^1]:0.000}");
            Console.WriteLine("  " + string.Join("  ", samples.Select(x => x.ToString("0.00"))));
        }

        if (timeouts > 0)
            Console.WriteLine($"Timeouts: {timeouts}/{Iterations} (DA1 unanswered within {PerQueryTimeout.TotalMilliseconds:0} ms).");
    }
}
