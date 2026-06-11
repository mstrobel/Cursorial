// ReSharper disable CheckNamespace

// A single demo command. Each demo declares its primary verb, optional aliases, a one-line
// description for 'help', and a RunAsync entry point that receives the rest of the command line.
internal interface IDemo
{
    string Name { get; }                       // primary verb, e.g. "render"
    IReadOnlyList<string> Aliases { get; }     // e.g. ["showcase"]
    string Description { get; }                // one line, for 'help'
    Task RunAsync(string argument);            // argument = the rest of the command line ("" if none)
}
