using System.Windows.Input;

namespace Cursorial.UI;

/// <summary>
/// An <see cref="ICommand"/> that can additionally <b>dry-run</b> its effect (the Actipro Avalonia
/// <c>IPreviewableCommand</c> shape): <see cref="Preview"/> is a dry-run Execute — it produces the effect so the user
/// can see the outcome, but commits nothing; <see cref="CancelPreview"/> unwinds the dry-run, restoring the
/// pre-preview state byte-exact; and <see cref="ICommand.Execute"/> simply <b>does the thing for real</b> — the
/// command's ordinary execution, written with <em>zero preview awareness</em>, because a previewing control (a
/// <c>BarComboBox</c>/<c>BarGallery</c> drop-down) always cancels any active dry-run <em>before</em> executing, so
/// by the time <c>Execute</c> runs there is nothing pending and no preview ever happened as far as the command can
/// tell. Because the verbs are command-side methods — not a mode smuggled through the parameter — a command that is
/// unaware of previewing can never mis-execute a dry-run as the real thing: the control dispatches the preview verbs
/// only when the bound command implements this interface, and hands the highlighted value through
/// <see cref="IValueCommandParameter.PreviewValue"/> (the chosen value rides
/// <see cref="IValueCommandParameter.Value"/> into <c>Execute</c>).
/// <para>
/// <b>The atomicity contract.</b> A dry-run either becomes the real execution or is entirely unwound — no in-between:
/// <list type="bullet">
/// <item><see cref="Preview"/> is refused while <see cref="ICommand.CanExecute"/> is <see langword="false"/> — a
/// gated command acquires no NEW tentative state. A repeat preview replaces the previous one (never stacks), and the
/// implementation must retain enough state to restore exactly on cancel.</item>
/// <item><see cref="CancelPreview"/> is a <b>cleanup obligation</b>, not a user action: it always executes,
/// regardless of <see cref="ICommand.CanExecute"/> — unwinding an applied dry-run must stay deliverable even after
/// the command gates itself mid-session. It is structurally separate from the (self-gated) <c>Execute</c>, so no
/// gate can swallow it.</item>
/// <item><see cref="ICommand.Execute"/> stays self-gated as usual and runs only after the control has cancelled any
/// active dry-run — so a command gated mid-session nets out to "dry-run unwound, execution refused".</item>
/// </list>
/// </para>
/// See <c>BarCommand</c> for the delegate-based implementation.
/// </summary>
public interface IPreviewableCommand : ICommand
{
    /// <summary>
    /// Whether this command supports dry-runs <em>at all</em> — the <b>structural</b> gate, as opposed to
    /// <see cref="ICommand.CanExecute"/>'s <b>runtime</b> gate ("may I run right now"). A previewing control probes
    /// this before starting a preview session, so a wrapper command (a <c>BarCommand</c> whose bindable
    /// inner command may or may not be previewable) can answer honestly instead of advertising verbs that would
    /// no-op — the probe never lies. (A deliberate departure from Actipro's Avalonia interface, which has no such
    /// member: it never wraps, so implementing the interface IS the capability there.)
    /// </summary>
    bool CanPreview { get; }

    /// <summary>The dry-run <c>Execute</c>: produce the effect for <paramref name="parameter"/> so the user can see
    /// the outcome, committing nothing — and keep enough state to restore exactly on <see cref="CancelPreview"/>.
    /// Must refuse (no-op) while <see cref="ICommand.CanExecute"/> is <see langword="false"/>; a repeat preview
    /// replaces the previous one.</summary>
    /// <param name="parameter">The command parameter (an <see cref="IValueCommandParameter"/> carries the highlighted
    /// value in <see cref="IValueCommandParameter.PreviewValue"/>).</param>
    void Preview(object? parameter);

    /// <summary>Unwinds the active dry-run, restoring the pre-preview state byte-exact. A cleanup obligation:
    /// executes regardless of <see cref="ICommand.CanExecute"/> (see the atomicity contract in the class remarks).
    /// A no-op when no dry-run is active.</summary>
    /// <param name="parameter">The command parameter the preview was requested with.</param>
    void CancelPreview(object? parameter);
}
