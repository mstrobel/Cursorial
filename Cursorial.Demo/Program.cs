using System.Runtime.InteropServices;

// REPL for exercising Cursorial against a real terminal.
//
// Two-phase model: the prompt itself runs in cooked mode (Console.ReadLine for command entry).
// Each command that needs raw input opens a TerminalSession (which puts the terminal into raw
// mode), does its work, and disposes the session — restoring cooked mode before the next prompt.
//
// Each command is an IDemo (see Demos/). This file owns only the prompt loop: it parses the verb +
// argument, dispatches to the matching demo by Name or alias, and handles the inline help/quit
// pseudo-commands. The demo registry is the single source of truth for both dispatch and 'help'.

// The demo registry — one instance of every demo class. Dispatch matches the typed verb against a
// demo's Name or any of its Aliases (case-insensitive); 'help' is generated from this list.
IReadOnlyList<IDemo> demos =
[
    new NegotiateDemo(),
    new ReadDemo(),
    new ReadSynthDemo(),
    new RawDemo(),
    new TraceDemo(),
    new TextSizingDemo(),
    new RenderDemo(),
    new DrawingDemo(),
    new PensDemo(),
    new ChartsDemo(),
    new AnimationDemo(),
    new BrushedTextDemo(),
    new UIPrimitivesDemo(),
    new UIPanelsDemo(),
    new UIControlsDemo(),
    new UIXamlDemo(),
    new WindowingDemo(),
    new ImageSceneDemo(),
    new ImageClipDemo(),
    new ImageDemo(),
    new FormatDemo(),
    new PaletteDemo(),
    new ProbeDemo(),
    new AccessKeysDemo(),
    new RasterBenchDemo(),
];

PrintBanner();

while (true)
{
    Console.Write("cursorial> ");
    string? line = Console.ReadLine();
    if (line is null) break;

    string trimmed = line.Trim();
    if (trimmed.Length == 0) continue;

    // Split the verb (lowercased for dispatch) from its argument (original case preserved —
    // file paths and similar args need it). A trailing-args-friendly split keeps the existing
    // arg-less commands working unchanged.
    int spaceIdx = trimmed.IndexOf(' ');
    string verb = (spaceIdx < 0 ? trimmed : trimmed[..spaceIdx]).ToLowerInvariant();
    string argument = spaceIdx < 0 ? "" : trimmed[(spaceIdx + 1)..].Trim();

    if (verb is "quit" or "exit" or "q") break;

    try
    {
        if (verb is "help" or "?")
        {
            PrintHelp(demos);
            continue;
        }

        var demo = demos.FirstOrDefault(d =>
            string.Equals(d.Name, verb, StringComparison.OrdinalIgnoreCase) ||
            d.Aliases.Any(a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase)));

        if (demo is null)
        {
            Console.WriteLine($"Unknown command: '{verb}'. Type 'help' for the list.");
            continue;
        }

        await demo.RunAsync(argument);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine($"{ex.StackTrace}");
    }
}

Console.WriteLine("Bye.");
return 0;

static void PrintBanner()
{
    Console.WriteLine("Cursorial demo — interactive runner");
    #if DEBUG
    Console.WriteLine($"Runtime: {RuntimeInformation.RuntimeIdentifier}; OS: {RuntimeInformation.OSDescription}; " +
                      $"Framework: {RuntimeInformation.FrameworkDescription}");
    #endif
    Console.WriteLine("Type 'help' for commands, 'quit' to exit.");
    Console.WriteLine();
}

static void PrintHelp(IReadOnlyList<IDemo> demos)
{
    Console.WriteLine("Commands:");

    // Align descriptions in a single column. The verb column width tracks the longest verb list but
    // is capped, so one unusually long alias list wraps to its own line instead of pushing every
    // description far to the right.
    const int indent = 2;
    const int gap = 2;
    const int maxVerbWidth = 20;
    int verbWidth = Math.Min(maxVerbWidth, demos.Max(d => Verbs(d).Length));
    int descColumn = indent + verbWidth + gap;

    foreach (var demo in demos)
        WriteEntry(Verbs(demo), demo.Description);

    WriteEntry("help, ?", "Show this help");
    WriteEntry("quit, exit, q", "Exit the demo");

    static string Verbs(IDemo demo) =>
        demo.Aliases.Count > 0 ? $"{demo.Name}, {string.Join(", ", demo.Aliases)}" : demo.Name;

    void WriteEntry(string verbs, string description)
    {
        if (indent + verbs.Length + 1 <= descColumn)   // verb list fits — description on the same line
        {
            Console.WriteLine($"{new string(' ', indent)}{verbs}{new string(' ', descColumn - indent - verbs.Length)}{description}");
        }
        else                                            // long alias list — wrap the description below
        {
            Console.WriteLine($"{new string(' ', indent)}{verbs}");
            Console.WriteLine($"{new string(' ', descColumn)}{description}");
        }
    }
}
