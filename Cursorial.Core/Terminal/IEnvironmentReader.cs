using System.Runtime.InteropServices;

namespace Cursorial.Terminal;

/// <summary>
/// Reads process environment variables and a small set of platform signals the negotiator
/// consults during family identification. Abstracted so the terminal negotiator can be
/// exercised against a deterministic stub in tests, and so future implementations can layer
/// (e.g., read from a config file overlay before falling through to the real environment).
/// </summary>
public interface IEnvironmentReader
{
    static IEnvironmentReader Default => EnvironmentReader.Instance;

    /// <summary>Returns the value of <paramref name="name"/>, or <c>null</c> if not set.</summary>
    string? GetVariable(string name);

    /// <summary>
    /// On Windows, returns true when the process is attached to a real console host
    /// (conhost.exe or the legacy console wrapper used by older Windows Terminal builds).
    /// Used by the negotiator to distinguish the legacy console host family from Windows
    /// Terminal (which sets <c>WT_SESSION</c>) and from non-console scenarios like piped
    /// output. Always returns false on non-Windows platforms. Default implementation calls
    /// <c>GetConsoleMode</c> on the standard input handle.
    /// </summary>
    bool IsAttachedToWindowsConsole() => DefaultIsAttachedToWindowsConsole();

    /// <inheritdoc cref="RuntimeInformation.IsOSPlatform(OSPlatform)"/>
    bool IsPlatform(in OSPlatform platform) => RuntimeInformation.IsOSPlatform(platform);

    /// <summary>
    /// Indicates whether the current environment is a Windows Subsystem for Linux (WSL).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the process is running in a Windows Subsystem for Linux (WSL) environment;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool IsWSL() => IsPlatform(OSPlatform.Windows) && GetVariable("WSL_DISTRO_NAME") is { Length: > 0 };

    /// <summary>
    /// Determines whether the current process is running under a Cygwin environment.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the environment
    /// variable "CYGWIN" is present; otherwise, <c>false</c>.
    /// </returns>
    bool IsCygwin() => IsPlatform(OSPlatform.Windows) && GetVariable("CYGWIN") is { Length: >= 0 };

    /// <summary>
    /// Determines whether the current process is running under a MinGW environment.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the environment
    /// variable "MINGW" is present; otherwise, <c>false</c>.
    /// </returns>
    bool IsMinGW() => IsPlatform(OSPlatform.Windows) && GetVariable("MSYSTEM") is { Length: >= 0 };

    /// <summary>
    /// Shared default for <see cref="IsAttachedToWindowsConsole"/> — exposed as 'static' so
    /// custom <see cref="IEnvironmentReader"/> implementations can delegate to it instead of
    /// re-implementing the P/Invoke.
    /// </summary>
    protected static bool DefaultIsAttachedToWindowsConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;

        // GetConsoleMode succeeds only when the handle refers to a real console. Stdin
        // redirected from a pipe or file returns false; an unattached process returns false.
        var stdin = WindowsConsoleProbe.GetStdHandle(WindowsConsoleProbe.StdInputHandle);
        if (stdin == IntPtr.Zero || stdin == new IntPtr(-1)) return false;

        return WindowsConsoleProbe.GetConsoleMode(stdin, out _);
    }

    /// <summary>
    /// Indicates whether the current environment is a Windows Subsystem for Linux (WSL).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the process is running in a Windows Subsystem for Linux (WSL) environment;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This default implementation is UNRELIABLE. 
    /// </remarks>
    protected static bool DefaultIsWSL()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
               File.ReadAllText("/proc/version") is {} v && v.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Default implementation backed by <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class EnvironmentReader : IEnvironmentReader
{
    private bool? _cachedIsWSL;

    /// <summary>A shared singleton instance.</summary>
    public static EnvironmentReader Instance { get; } = new();

    /// <inheritdoc/>
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc/>
    public bool IsAttachedToWindowsConsole() => IEnvironmentReader.DefaultIsAttachedToWindowsConsole();

    /// <inheritdoc/>
    public bool IsPlatform(in OSPlatform platform) => RuntimeInformation.IsOSPlatform(platform);

    /// <inheritdoc/>
    public bool IsWSL()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) is false) return false;
        if (_cachedIsWSL is {} isWSL) return isWSL;
        isWSL = File.ReadAllText("/proc/version") is {} v && v.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        Thread.MemoryBarrier();
        _cachedIsWSL = isWSL;
        Volatile.WriteBarrier();
        return isWSL;
    }
}

/// <summary>
/// Minimal Win32 probe used by <see cref="IEnvironmentReader.IsAttachedToWindowsConsole"/>.
/// Kept internal, so the broader codebase isn't tempted to reach for the Win32 console handle
/// directly — transports and the resize monitor own their own P/Invoke declarations.
/// </summary>
internal static partial class WindowsConsoleProbe
{
    internal const int StdInputHandle = -10;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
}
