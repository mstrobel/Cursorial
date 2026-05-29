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
    /// Determines whether the current session is running over an SSH connection.
    /// Returns true if either the <c>SSH_CONNECTION</c> or <c>SSH_TTY</c> environment
    /// variables are set and contain a non-empty value; otherwise, returns false.
    /// </summary>
    /// <returns>
    /// A boolean value indicating whether the session is an SSH session.
    /// </returns>
    bool IsSSH() => DefaultIsSSH(this);

    /// <summary>
    /// Indicates whether the current environment is a Windows Subsystem for Linux (WSL).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the process is running in a Windows Subsystem for Linux (WSL) environment;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool IsWSL() => DefaultIsWSL(this);

    /// <summary>
    /// Determines whether the current process is running under a Cygwin environment.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the environment
    /// variable "CYGWIN" is present; otherwise, <c>false</c>.
    /// </returns>
    bool IsCygwin() => DefaultIsCygwin(this);

    /// <summary>
    /// Determines whether the current process is running under a MinGW environment.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the environment
    /// variable "MINGW" is present; otherwise, <c>false</c>.
    /// </returns>
    bool IsMinGW() => DefaultIsMinGW(this);

    /// <summary>
    /// Shared default for <see cref="IsAttachedToWindowsConsole"/> — exposed as 'static' so
    /// custom <see cref="IEnvironmentReader"/> implementations can delegate to it instead of
    /// re-implementing the P/Invoke.
    /// </summary>
    protected static bool DefaultIsAttachedToWindowsConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // GetConsoleMode succeeds only when the handle refers to a real console. Stdin
            // redirected from a pipe or file returns false; an unattached process returns false.
            var stdin = WindowsConsoleProbe.GetStdHandle(WindowsConsoleProbe.StdInputHandle);
            if (stdin == IntPtr.Zero || stdin == new IntPtr(-1)) return false;

            return WindowsConsoleProbe.GetConsoleMode(stdin, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Indicates whether the current environment is a Windows Subsystem for Linux (WSL).
    /// </summary>
    /// <param name="env">
    /// An instance of <see cref="IEnvironmentReader"/> used to evaluate the environment.
    /// </param>
    /// <returns>
    /// <c>true</c> if the process is running in a Windows Subsystem for Linux (WSL) environment;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This default implementation is UNRELIABLE. 
    /// </remarks>
    protected static bool DefaultIsWSL(IEnvironmentReader env)
    {
        return env.IsPlatform(OSPlatform.Linux) &&
               File.ReadAllText("/proc/version") is {} v && v.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the current session is running over an SSH connection
    /// by evaluating specific environment variables.
    /// </summary>
    /// <param name="env">
    /// An instance of <see cref="IEnvironmentReader"/> used to evaluate the environment.
    /// </param>
    /// <returns>
    /// A boolean value indicating whether the session is an SSH session.
    /// Returns true if either the <c>SSH_CONNECTION</c> or <c>SSH_TTY</c> environment
    /// variables are set and contain a non-empty value; otherwise, returns false.
    /// </returns>
    protected static bool DefaultIsSSH(IEnvironmentReader env)
    {
        return env.GetVariable("SSH_CONNECTION") is { Length: > 0 } ||
               env.GetVariable("SSH_TTY") is { Length: > 0 };
    }

    /// <summary>
    /// Determines whether the current process is running under a Cygwin environment.
    /// </summary>
    /// <param name="env">
    /// An instance of <see cref="IEnvironmentReader"/> used to evaluate the environment.
    /// </param>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the environment
    /// variable "CYGWIN" is present; otherwise, <c>false</c>.
    /// </returns>
    protected static bool DefaultIsCygwin(IEnvironmentReader env)
    {
        return env.IsPlatform(OSPlatform.Windows) && 
               env.GetVariable("CYGWIN") is { Length: >= 0 };
    }

    /// <summary>
    /// Determines whether the current process is running under a MinGW environment.
    /// </summary>
    /// <param name="env">
    /// An instance of <see cref="IEnvironmentReader"/> used to evaluate the environment.
    /// </param>
    /// <returns>
    /// <c>true</c> if the operating system platform is Windows and the "MSYSTEM" environment
    /// variable is present and has a non-empty value; otherwise, <c>false</c>.
    /// </returns>
    protected static bool DefaultIsMinGW(IEnvironmentReader env)
    {
        return env.IsPlatform(OSPlatform.Windows) && 
               env.GetVariable("MSYSTEM") is { Length: >= 0 };
    }
}

/// <summary>Default implementation backed by <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class EnvironmentReader : IEnvironmentReader
{
    [Flags]
    private enum CachedFlags
    {
        None = 0,
        Probed = 1,
        IsSSH = 2,
        IsWSL = 4,
        IsCygwin = 8,
        IsMinGW = 16,
        IsAttachedToWindowsConsole = 32
    }

    private CachedFlags EvaluateFlags()
    {
        var flags = CachedFlags.Probed;

        if (IEnvironmentReader.DefaultIsSSH(this)) flags |= CachedFlags.IsSSH;
        if (IEnvironmentReader.DefaultIsAttachedToWindowsConsole()) flags |= CachedFlags.IsAttachedToWindowsConsole;
        if (IEnvironmentReader.DefaultIsWSL(this)) flags |= CachedFlags.IsWSL;
        if (IEnvironmentReader.DefaultIsCygwin(this)) flags |= CachedFlags.IsCygwin;
        if (IEnvironmentReader.DefaultIsMinGW(this)) flags |= CachedFlags.IsMinGW;

        return flags;
    }

    private CachedFlags Flags
    {
        get
        {
            if (field is var cached and not CachedFlags.None)
                return cached;

            Interlocked.Exchange(ref field, EvaluateFlags());
            return field;
        }
    }

    /// <summary>A shared singleton instance.</summary>
    public static EnvironmentReader Instance { get; } = new();

    /// <inheritdoc/>
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc/>
    public bool IsAttachedToWindowsConsole() => Flags.HasFlag(CachedFlags.IsAttachedToWindowsConsole);

    /// <inheritdoc/>
    public bool IsPlatform(in OSPlatform platform) => RuntimeInformation.IsOSPlatform(platform);

    /// <inheritdoc/>
    public bool IsWSL() => Flags.HasFlag(CachedFlags.IsWSL);

    /// <inheritdoc/>
    public bool IsSSH() => Flags.HasFlag(CachedFlags.IsSSH);

    /// <inheritdoc/>
    public bool IsCygwin() => Flags.HasFlag(CachedFlags.IsCygwin);

    /// <inheritdoc/>
    public bool IsMinGW() => Flags.HasFlag(CachedFlags.IsMinGW);
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
