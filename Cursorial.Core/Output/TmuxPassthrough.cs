using System.Buffers;

namespace Cursorial.Output;

/// <summary>
/// Wrap protocol-specific byte payloads in tmux's <c>DCS tmux ; … ST</c> passthrough envelope.
/// When tmux is positioned between an application and the outer terminal, it normally strips
/// unrecognized escape sequences before they reach the terminal — that's how it isolates panes
/// from each other's escape codes. The passthrough envelope tells tmux "forward these bytes
/// verbatim to the outer terminal" so terminal-specific features (Kitty graphics, iTerm2
/// inline images, …) still work through tmux.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> The envelope is <c>ESC P tmux ; &lt;body&gt; ESC \</c>, where
/// <c>&lt;body&gt;</c> is the inner payload with every <c>ESC</c> byte (<c>0x1B</c>) doubled.
/// Doubling is required because tmux's DCS parser would otherwise terminate on the first
/// <c>ESC \</c> within the inner sequence (a DCS body terminates on <c>ESC \</c> per ECMA-48).
/// tmux's parser collapses doubled-ESC back into a single ESC during unwrap, then forwards
/// the result to the outer terminal.
/// </para>
/// <para>
/// <b>When to wrap.</b> Check
/// <see cref="Output.Capabilities.OutputProtocolCapabilities.MultiplexerPassthrough"/>; when
/// true, wrap any byte sequence the multiplexer is likely to strip (today that's Kitty
/// graphics APC, iTerm2 inline-image OSC 1337, and potentially OSC 8 / OSC 66 on older tmux
/// versions). SGR codes and basic cursor / clear escapes pass through unmodified — wrapping
/// them would defeat tmux's own pane-aware processing.
/// </para>
/// <para>
/// <b>Enabling on tmux's side.</b> tmux 3.3+ defaults <c>allow-passthrough</c> to <c>on</c> for
/// terminals whose <c>TERM</c> is in the recognized list (<c>screen-*</c>, <c>tmux-*</c>);
/// older versions require <c>set -g allow-passthrough on</c> in <c>.tmux.conf</c>. This is a
/// user configuration concern — the wrapping is correct either way; tmux just may not forward
/// when passthrough is disabled.
/// </para>
/// </remarks>
public static class TmuxPassthrough
{
    /// <summary>
    /// Wrap <paramref name="innerBytes"/> in a tmux DCS passthrough envelope and write the
    /// result to <paramref name="output"/>. The inner bytes are written verbatim except that
    /// each <c>0x1B</c> (ESC) byte is doubled to satisfy tmux's DCS-body parser.
    /// </summary>
    public static void WriteWrapped(IBufferWriter<byte> output, ReadOnlySpan<byte> innerBytes)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Reserve worst-case space: 9 bytes for the fixed framing (ESC P tmux ; ESC \) plus
        // up to 2× the inner length when every byte is an ESC.
        int needed = 9 + innerBytes.Length * 2;
        var dest = output.GetSpan(needed);

        int written = 0;
        dest[written++] = 0x1B;                 // ESC
        dest[written++] = (byte) 'P';           // DCS introducer
        dest[written++] = (byte) 't';
        dest[written++] = (byte) 'm';
        dest[written++] = (byte) 'u';
        dest[written++] = (byte) 'x';
        dest[written++] = (byte) ';';

        for (int i = 0; i < innerBytes.Length; i++)
        {
            byte b = innerBytes[i];
            dest[written++] = b;
            if (b == 0x1B)
            {
                // Double the ESC so tmux's DCS parser doesn't terminate on inner ESC \.
                dest[written++] = 0x1B;
            }
        }

        dest[written++] = 0x1B;                 // ESC
        dest[written++] = (byte) '\\';          // ST
        output.Advance(written);
    }
}
