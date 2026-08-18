using Microsoft.Win32.SafeHandles;

namespace Cursorial.CLI.Wire;

/// <summary>
/// Reads piped stdin DATA (`git branch | curio choose`) without ever touching <see cref="Console"/>'s
/// stream plumbing — on Unix the Console subsystem silently mutates termios, which would fight the
/// terminal session's raw mode (the framework's documented hazard). The pipe on fd 0 is read raw and
/// FULLY, before the session opens; the session reads keys from the controlling tty regardless.
/// </summary>
public static class StdinFeed
{
    /// <summary>All stdin lines when stdin is redirected (trailing blank lines dropped); null when
    /// stdin is a terminal (nothing to feed — the tty belongs to the UI).</summary>
    public static IReadOnlyList<string>? TryReadLines()
    {
        if (!Console.IsInputRedirected)
            return null; // a metadata-only probe — no stream is opened

        using var stream = new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
