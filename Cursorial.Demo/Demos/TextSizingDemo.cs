using Cursorial.Input;
using Cursorial.Input.Events;

// Kitty OSC 66 text-sizing demonstration. One-shot (no render loop): opens a session, emits a
// fixed sequence of sized-text samples, then waits for Enter (or Ctrl+C) before returning.
// Implements IDemo directly, mirroring NegotiateDemo. Body migrated verbatim from the former
// Program.cs DemoTextSizingAsync.
internal sealed class TextSizingDemo : IDemo
{
    public string Name => "sizing";
    public IReadOnlyList<string> Aliases => ["text-sizing"];
    public string Description =>
        "Kitty OSC 66 text-sizing samples (s=/n=/d=/w=/h=).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Text-sizing demo. Press Enter to return; Ctrl+C also works.");
        Console.WriteLine();

        var (session, _, _, _, palette, capabilities) = await DemoSupport.PrepareDemo();

        await using var ds = session;
        using var dp = palette;

        var writer = session.Output.Writer;
        var caps = capabilities.Output.TextSizing;

        if (caps is { Width: false, Scale: false })
        {
            await DemoSupport.WriteLineAsync(writer,
                "  Terminal does not advertise text-sizing support. Sending the sequences anyway —");
            await DemoSupport.WriteLineAsync(writer,
                "  non-supporting terminals render OSC 66 payloads at normal size (the metadata is ignored).");
            await DemoSupport.WriteLineAsync(writer, "");
        }
        else
        {
            var support = (caps.Width, caps.Scale) switch
                          {
                              (true, true)  => "Width + Scale",
                              (true, false) => "Width only",
                              (false, true) => "Scale only",
                              _             => "none"
                          };

            await DemoSupport.WriteLineAsync(writer, $"  Negotiated text-sizing support: {support}.");
            await DemoSupport.WriteLineAsync(writer, "");
        }

        await DemoSupport.WriteLineAsync(writer, "  Reference (no OSC 66): Hello, world!");
        await DemoSupport.WriteLineAsync(writer, "");

        await DemoSupport.WriteLineAsync(writer, "  s=2 (double-sized):");
        await DemoSupport.WriteSizedAsync(writer, "s=2", "Hello, world!");
        await DemoSupport.WriteLineAsync(writer, "");
        await DemoSupport.WriteLineAsync(writer, "");

        await DemoSupport.WriteLineAsync(writer, "  n=1:d=2 (half-sized):");
        await DemoSupport.WriteSizedAsync(writer, "n=1:d=2", "Hello, world!");
        await DemoSupport.WriteLineAsync(writer, "");
        await DemoSupport.WriteLineAsync(writer, "");

        await DemoSupport.WriteLineAsync(writer, "  w=2 (forced two-cell width on emoji):");
        await DemoSupport.WriteSizedAsync(writer, "w=2", "🐈");
        await DemoSupport.WriteSizedAsync(writer, "w=2", "🌶");
        await DemoSupport.WriteSizedAsync(writer, "w=2", "🚀");
        await DemoSupport.WriteLineAsync(writer, "");
        await DemoSupport.WriteLineAsync(writer, "");

        await DemoSupport.WriteLineAsync(writer, "  s=2:h=2 (double-sized, horizontally centered in the 2-cell block):");
        await DemoSupport.WriteSizedAsync(writer, "s=2:h=2", "Cursorial");
        await DemoSupport.WriteLineAsync(writer, "");
        await DemoSupport.WriteLineAsync(writer, "");

        await DemoSupport.WriteLineAsync(writer, "  (press Enter to return)");
        await writer.FlushAsync();

        using var stopCts = new CancellationTokenSource();
        try
        {
            await foreach (var evt in session.Input.ReadAllAsync(stopCts.Token))
            {
                if (evt is KeyEvent { Key: Key.Enter }) break;
                if (DemoSupport.IsStopSignal(evt)) break;
            }
        }
        catch (OperationCanceledException) { }
    }
}
