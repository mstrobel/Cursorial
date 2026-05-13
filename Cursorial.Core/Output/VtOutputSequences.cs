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
}