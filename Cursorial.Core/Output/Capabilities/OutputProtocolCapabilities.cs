namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes opt-in protocol features a terminal accepts on its output (write) side.
/// </summary>
/// <remarks>
/// These are the writer's mirror of <c>InputCapabilities.Protocol</c>: the input side reports
/// "the device emits a paste event"; this side reports "we can ask the terminal to emit one."
/// Both are needed to wire up an end-to-end feature like bracketed paste — the application
/// must first enable it on the writer, after which the input device begins reporting it.
/// </remarks>
/// <param name="BracketedPasteEnable">DECSET 2004 — wrap pasted input in start/end markers.</param>
/// <param name="FocusReportingEnable">DECSET 1004 — emit focus-in / focus-out reports.</param>
/// <param name="SgrMouseEnable">
/// DECSET 1006 — SGR mouse encoding (lossless coordinates, supports columns &gt; 223).
/// </param>
/// <param name="MouseMotionEnable">DECSET 1003 — report mouse motion regardless of button state.</param>
/// <param name="KittyKeyboardPush">
/// CSI &gt; … u — push the application's preferred Kitty keyboard protocol flags onto the
/// terminal's protocol stack. Must be popped on session end.
/// </param>
/// <param name="Win32InputModeEnable">
/// DECSET 9001 — enable Microsoft's Win32 Input Mode for ConPTY, allowing lossless Win32
/// console key records to be carried over a VT channel.
/// </param>
/// <param name="ClipboardWrite">OSC 52 set — write to the system clipboard.</param>
/// <param name="ClipboardRead">OSC 52 get — read from the system clipboard (often gated by user prompt).</param>
/// <param name="SynchronizedOutput">
/// DECSET 2026 — defer screen updates until end-sync, eliminating mid-frame tearing during
/// large redraws.
/// </param>
/// <param name="MultiplexerPassthrough">
/// True when the application is running inside a terminal multiplexer (tmux today; screen may
/// follow) and protocol payloads that the multiplexer doesn't natively understand must be
/// wrapped in the multiplexer's passthrough envelope to reach the outer terminal. Today this
/// affects Kitty graphics (APC G), iTerm2 inline images (OSC 1337), and any future protocol
/// the multiplexer doesn't pass through unchanged. <see cref="Output.TmuxPassthrough"/> is the
/// utility that wraps a payload in <c>DCS tmux ; … ST</c> with the required ESC-doubling.
/// </param>
/// <param name="MouseCursorShape">
/// OSC 22 — Kitty's pointer-shape protocol. When true the terminal honors application requests
/// for the GUI mouse cursor (I-beam over text inputs, hand over clickable elements, the family
/// of resize cursors over draggable borders, …). Gated to terminals known to implement the
/// protocol — Kitty, Ghostty, and Foot today. <see cref="Output.MouseCursorWriter"/> emits the
/// sequences; the enum of supported shapes is <see cref="Output.MouseCursorShape"/>.
/// </param>
public sealed record OutputProtocolCapabilities(bool BracketedPasteEnable,
                                                bool FocusReportingEnable,
                                                bool SgrMouseEnable,
                                                bool MouseButtonsEnable,
                                                bool MouseDragEnable,
                                                bool MouseMotionEnable,
                                                bool KittyKeyboardPush,
                                                bool Win32InputModeEnable,
                                                bool ClipboardWrite,
                                                bool ClipboardRead,
                                                bool SynchronizedOutput,
                                                bool MultiplexerPassthrough = false,
                                                bool MouseCursorShape = false)
{
    public static OutputProtocolCapabilities None { get; } = new(BracketedPasteEnable: false,
                                                                 FocusReportingEnable: false,
                                                                 SgrMouseEnable: false,
                                                                 MouseButtonsEnable: false,
                                                                 MouseDragEnable: false,
                                                                 MouseMotionEnable: false,
                                                                 KittyKeyboardPush: false,
                                                                 Win32InputModeEnable: false,
                                                                 ClipboardWrite: false,
                                                                 ClipboardRead: false,
                                                                 SynchronizedOutput: false);
}