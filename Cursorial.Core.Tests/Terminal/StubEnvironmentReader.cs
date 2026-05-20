using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Test stub for <see cref="IEnvironmentReader"/>. Tests build the desired environment with
/// <see cref="Set"/> calls; everything else returns <c>null</c>.
/// </summary>
internal sealed class StubEnvironmentReader : IEnvironmentReader
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public StubEnvironmentReader Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    /// <summary>
    /// Whether <see cref="IsAttachedToWindowsConsole"/> reports true. Defaults to false so
    /// tests don't accidentally pick up the host's real console state; the negotiator's
    /// Windows-family branch only fires when this is explicitly enabled.
    /// </summary>
    public bool WindowsConsoleAttached { get; set; }

    public StubEnvironmentReader WithWindowsConsoleAttached(bool value = true)
    {
        WindowsConsoleAttached = value;
        return this;
    }

    public string? GetVariable(string name) =>
        _values.GetValueOrDefault(name);

    public bool IsAttachedToWindowsConsole() => WindowsConsoleAttached;
}
