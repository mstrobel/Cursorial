namespace Cursorial.Core.Output;

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
/// <param name="AnyEventMouseEnable">DECSET 1003 — report mouse motion regardless of button state.</param>
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
public sealed record class OutputProtocolCapabilities(
    bool BracketedPasteEnable,
    bool FocusReportingEnable,
    bool SgrMouseEnable,
    bool AnyEventMouseEnable,
    bool KittyKeyboardPush,
    bool Win32InputModeEnable,
    bool ClipboardWrite,
    bool ClipboardRead,
    bool SynchronizedOutput)
{
    public static OutputProtocolCapabilities None { get; } = new(
        BracketedPasteEnable: false,
        FocusReportingEnable: false,
        SgrMouseEnable: false,
        AnyEventMouseEnable: false,
        KittyKeyboardPush: false,
        Win32InputModeEnable: false,
        ClipboardWrite: false,
        ClipboardRead: false,
        SynchronizedOutput: false);
}
