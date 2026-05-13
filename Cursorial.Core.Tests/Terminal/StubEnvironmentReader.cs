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

    public string? GetVariable(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;
}
