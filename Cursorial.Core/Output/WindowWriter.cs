using System.Buffers;
using System.Text;

namespace Cursorial.Output;

/// <summary>
/// Window-manipulation byte writers: set the terminal's window title and / or icon name via
/// xterm OSC 0 / 1 / 2 sequences. Trivial pass-throughs for the protocol; the value is having
/// a discoverable, capability-gated API alongside the rest of the byte writers rather than
/// scattering OSC literals across consumer code.
/// </summary>
/// <remarks>
/// <para>
/// Capability gating is the caller's responsibility — check
/// <see cref="Output.Capabilities.WindowCapabilities.TitleSet"/> /
/// <see cref="Output.Capabilities.WindowCapabilities.IconSet"/> on the negotiated
/// <see cref="Output.Capabilities.OutputCapabilities"/>. Emitting OSC 0/1/2 to a
/// non-supporting terminal is benign (most strip unrecognized OSCs), but the title won't change.
/// </para>
/// <para>
/// The supplied <see cref="string"/> is encoded as UTF-8. Most modern terminals decode the OSC
/// payload as UTF-8; legacy terminals that expect Latin-1 will see mojibake for non-ASCII
/// characters, but that's a terminal-side limitation Cursorial can't paper over here.
/// </para>
/// </remarks>
public static class WindowWriter
{
    /// <summary>
    /// Set the terminal window title (OSC 2). Most terminals also pick this up as the tab
    /// label and the OS-level window title.
    /// </summary>
    public static void WriteTitle(IBufferWriter<byte> writer, ReadOnlySpan<char> title)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteOsc(writer, VtOutputSequences.Window.TitlePrefix, title);
    }

    /// <summary>
    /// Set the icon name (OSC 1). On X11-derived terminals this is the iconified-window
    /// label; modern terminals usually ignore it or alias it to the title.
    /// </summary>
    public static void WriteIconName(IBufferWriter<byte> writer, ReadOnlySpan<char> name)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteOsc(writer, VtOutputSequences.Window.IconPrefix, name);
    }

    /// <summary>
    /// Set both the icon name and the window title in a single OSC 0 emission. Cheaper than
    /// two separate OSC sequences when both should match.
    /// </summary>
    public static void WriteTitleAndIconName(IBufferWriter<byte> writer, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteOsc(writer, VtOutputSequences.Window.IconAndTitlePrefix, text);
    }

    private static void WriteOsc(IBufferWriter<byte> writer, ReadOnlySpan<byte> prefix, ReadOnlySpan<char> payload)
    {
        var terminator = VtOutputSequences.Window.StringTerminator;
        int payloadBytes = Encoding.UTF8.GetByteCount(payload);
        var dest = writer.GetSpan(prefix.Length + payloadBytes + terminator.Length);
        int written = 0;

        prefix.CopyTo(dest[written..]);
        written += prefix.Length;

        if (payloadBytes > 0)
        {
            int encoded = Encoding.UTF8.GetBytes(payload, dest[written..]);
            written += encoded;
        }

        terminator.CopyTo(dest[written..]);
        written += terminator.Length;

        writer.Advance(written);
    }
}