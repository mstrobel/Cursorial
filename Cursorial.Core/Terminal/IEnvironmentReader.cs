namespace Cursorial.Terminal;

/// <summary>
/// Reads process environment variables. Abstracted so the terminal negotiator can be exercised
/// against a deterministic stub in tests, and so future implementations can layer (e.g. read
/// from a config file overlay before falling through to the real environment).
/// </summary>
public interface IEnvironmentReader
{
    /// <summary>Returns the value of <paramref name="name"/>, or <c>null</c> if not set.</summary>
    string? GetVariable(string name);
}

/// <summary>Default implementation backed by <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class EnvironmentReader : IEnvironmentReader
{
    /// <summary>A shared singleton instance.</summary>
    public static EnvironmentReader Instance { get; } = new();

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}
