using Cursorial.Output.Capabilities;

namespace Cursorial.Output;

/// <summary>
/// Output-side byte-string constants — the wire form of escape sequences the application
/// emits to drive terminal behavior. The input-side mirror (sequences the terminal sends
/// back) lives in <c>VtInputSequences</c> under <c>Cursorial.Input.Parsing</c>.
/// </summary>
/// <remarks>
/// As Cursorial's output surface grows (SGR builders, cursor control, screen control,
/// hyperlinks, …) this is where the magic bytes belong. Today the file is small because the
/// higher-level output writers live in the not-yet-built <c>Cursorial.Rendering</c> library;
/// constants land here as the protocols they encode get capability slots in
/// <see cref="OutputCapabilities"/>.
/// </remarks>
public static class VtOutputSequences
{
    /// <summary>
    /// OSC 8 hyperlink anchors. Format: <c>ESC ] 8 ; params ; uri ST</c> to open, and
    /// <c>ESC ] 8 ; ; ST</c> to close. Params is a colon-separated list of <c>key=value</c>
    /// pairs (typically just <c>id=&lt;id&gt;</c>); empty params is valid.
    /// </summary>
    public static class Hyperlink
    {
        /// <summary><c>ESC ] 8 ;</c> — opening of the OSC 8 envelope. Params (or an empty param block) follow.</summary>
        public static ReadOnlySpan<byte> Prefix => "\x1b]8;"u8;

        /// <summary>
        /// Complete close sequence: <c>ESC ] 8 ; ; ESC \</c>. Emit immediately after the
        /// anchor text; nesting hyperlinks within hyperlinks is undefined per the spec.
        /// </summary>
        public static ReadOnlySpan<byte> Close => "\x1b]8;;\x1b\\"u8;
    }

    /// <summary>
    /// Kitty text-sizing protocol — application-emitted OSC 66 sequences that render text in a
    /// non-default cell footprint. Format: <c>ESC ] 66 ; metadata ; text ST</c>, where the
    /// metadata is a colon-separated list of <c>key=value</c> pairs (<c>s</c>/<c>w</c>/<c>n</c>/
    /// <c>d</c>/<c>v</c>/<c>h</c>) per the spec at
    /// <see href="https://sw.kovidgoyal.net/kitty/text-sizing-protocol/"/>.
    /// </summary>
    /// <remarks>
    /// We expose the prefix and the canonical ST terminator as raw byte spans rather than a
    /// formatter — the parameter-assembly logic belongs in the higher-level renderer in
    /// <c>Cursorial.Rendering</c>. Consumers should gate emission on
    /// <see cref="OutputCapabilities.TextSizing"/>; sending OSC 66 to a non-supporting terminal
    /// is benign (the body is ignored) but leaves the user with unrendered escape bytes if the
    /// terminal also strips OSCs it doesn't recognize.
    /// </remarks>
    /// <summary>
    /// xterm window-manipulation OSC sequences. Format: <c>ESC ] N ; payload ST</c> where N is
    /// 0 (both icon and title), 1 (icon only), or 2 (title only). Universally supported by
    /// terminal emulators that report <see cref="OutputCapabilities.Window"/>.<c>TitleSet</c>
    /// or <c>IconSet</c>.
    /// </summary>
    public static class Window
    {
        /// <summary><c>ESC ] 0 ;</c> — set both icon name and window title.</summary>
        public static ReadOnlySpan<byte> IconAndTitlePrefix => "\x1b]0;"u8;

        /// <summary><c>ESC ] 1 ;</c> — set icon name only.</summary>
        public static ReadOnlySpan<byte> IconPrefix => "\x1b]1;"u8;

        /// <summary><c>ESC ] 2 ;</c> — set window title only.</summary>
        public static ReadOnlySpan<byte> TitlePrefix => "\x1b]2;"u8;

        /// <summary><c>ESC \</c> — String Terminator (ST). BEL (0x07) also works on most terminals.</summary>
        public static ReadOnlySpan<byte> StringTerminator => "\x1b\\"u8;
    }

    public static class KittyTextSizing
    {
        /// <summary><c>ESC ] 66 ;</c> — opening of the OSC 66 envelope. The metadata block follows.</summary>
        public static ReadOnlySpan<byte> Prefix => "\x1b]66;"u8;

        /// <summary><c>ESC \\</c> — String Terminator (ST). Caller may instead emit BEL (0x07).</summary>
        public static ReadOnlySpan<byte> StringTerminator => "\x1b\\"u8;

        /// <summary>
        /// Maximum text-payload byte count per the spec (4096). Longer strings must be split
        /// across multiple OSC 66 sequences to avoid being truncated.
        /// </summary>
        public const int MaxTextBytes = 4096;
    }

    /// <summary>
    /// DECSET 2026 — synchronized output. Wrap a frame's emission between
    /// <see cref="Begin"/> and <see cref="End"/> to ask the terminal to buffer paints and apply
    /// them atomically, eliminating mid-frame tearing on supporting terminals. Caller is
    /// responsible for gating emission on the negotiated
    /// <c>OutputProtocolCapabilities.SynchronizedOutput</c>; sending these to a non-supporting
    /// terminal is benign (they're DEC private modes that get silently ignored).
    /// </summary>
    public static class SynchronizedOutput
    {
        /// <summary><c>CSI ? 2026 h</c> — begin a synchronized-output frame.</summary>
        public static ReadOnlySpan<byte> Begin => "\x1b[?2026h"u8;

        /// <summary><c>CSI ? 2026 l</c> — end a synchronized-output frame; terminal commits the buffered paints.</summary>
        public static ReadOnlySpan<byte> End => "\x1b[?2026l"u8;
    }

    /// <summary>
    /// OSC 52 clipboard protocol. Format: <c>ESC ] 52 ; &lt;target&gt; ; &lt;base64-payload&gt; ST</c>.
    /// Target is <c>c</c> for the system clipboard or <c>p</c> for the X11 primary selection;
    /// other one-character codes select less-common selections (<c>q</c>, <c>s</c>, <c>0</c>..<c>7</c>).
    /// A <c>?</c> payload requests a read (terminal responds with the same envelope holding the
    /// current selection's base64).
    /// </summary>
    public static class Clipboard
    {
        /// <summary><c>ESC ] 52 ;</c> — opening of the OSC 52 envelope. Target and payload follow.</summary>
        public static ReadOnlySpan<byte> Prefix => "\x1b]52;"u8;

        /// <summary><c>ESC \</c> — String Terminator (ST).</summary>
        public static ReadOnlySpan<byte> StringTerminator => "\x1b\\"u8;

        /// <summary>Selection identifier for the system clipboard.</summary>
        public const byte SystemTarget = (byte) 'c';

        /// <summary>Selection identifier for the X11 primary selection.</summary>
        public const byte PrimaryTarget = (byte) 'p';
    }

    /// <summary>
    /// Kitty pointer-shape protocol — application-emitted OSC 22 sequences that ask the
    /// terminal to display a specific GUI mouse cursor while the pointer hovers over the
    /// application. Format: <c>ESC ] 22 ; [stack_op] shape[,shape…] ST</c>. The
    /// <c>stack_op</c> is <c>&gt;</c> (push), <c>&lt;</c> (pop), or absent (replace current).
    /// Per the spec at <see href="https://sw.kovidgoyal.net/kitty/pointer-shapes/"/>.
    /// </summary>
    public static class KittyPointerShape
    {
        /// <summary><c>ESC ] 22 ;</c> — opening of the OSC 22 envelope. Optional stack op + shape names follow.</summary>
        public static ReadOnlySpan<byte> Prefix => "\x1b]22;"u8;

        /// <summary><c>ESC \\</c> — String Terminator (ST).</summary>
        public static ReadOnlySpan<byte> StringTerminator => "\x1b\\"u8;

        /// <summary><c>ESC ] 22 ; ESC \\</c> — reset the current pointer shape to the terminal default.</summary>
        public static ReadOnlySpan<byte> Reset => "\x1b]22;\x1b\\"u8;

        /// <summary><c>ESC ] 22 ; &lt; ESC \\</c> — pop the topmost shape from the pointer-shape stack.</summary>
        public static ReadOnlySpan<byte> Pop => "\x1b]22;<\x1b\\"u8;
    }
}