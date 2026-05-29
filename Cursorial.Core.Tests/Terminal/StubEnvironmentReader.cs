using System.Runtime.InteropServices;

using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Test stub for <see cref="IEnvironmentReader"/>. Tests build the desired environment with
/// <see cref="Set"/> calls; everything else returns <c>null</c>.
/// </summary>
internal sealed class StubEnvironmentReader : IEnvironmentReader
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private HashSet<OSPlatform>? _platforms;

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

    /// <summary>
    /// Forces <see cref="IsPlatform"/> to report <paramref name="platform"/> as the active
    /// platform regardless of the host OS — necessary for tests of
    /// <see cref="IEnvironmentReader.IsCygwin"/> / <c>IsMinGW</c> / <c>IsWSL</c> which short-
    /// circuit on platform check. Call once per platform you want to claim; once any platform
    /// has been added, <see cref="IsPlatform"/> stops deferring to the real OS and answers
    /// purely from the configured set.
    /// </summary>
    public StubEnvironmentReader WithPlatform(OSPlatform platform)
    {
        _platforms ??= [];
        _platforms.Add(platform);
        return this;
    }

    public string? GetVariable(string name) =>
        _values.GetValueOrDefault(name);

    public bool IsAttachedToWindowsConsole() => WindowsConsoleAttached;

    public bool IsPlatform(in OSPlatform platform) =>
        _platforms is null ? RuntimeInformation.IsOSPlatform(platform) : _platforms.Contains(platform);
}
