namespace Cursorial.UI.Input;

/// <summary>
/// The nullable seam <see cref="AccessKeyManager"/> holds so the Cursorial.UI.Bars KeyTip overlay (the Alt-badge
/// accelerator, design guide §"KeyTips") can ride the Alt cue machine without <c>Cursorial.UI</c> referencing Bars.
/// KeyTips is a MODE layered on the access-key cue: Alt arms the badge overlay (in <see cref="AccessKeyMode.AltHeld"/>),
/// letters walk a multi-level prefix drill, Esc backs out. The manager fires <c>CueActivated</c>/<c>CueDeactivated</c>
/// at the controller; the controller owns its own key interception (via <c>InputDispatcher.PreProcessInput</c>), badge
/// surface, and focus snapshot/restore. All manager methods that touch this slot no-op when it is <see langword="null"/>
/// (no controller installed ⇒ classic inline underline mnemonics only).
/// </summary>
public interface IKeyTipController
{
    /// <summary>Whether the KeyTip overlay is currently showing (a level stack is active).</summary>
    bool IsActive { get; }

    /// <summary>Arm the overlay: snapshot focus, suppress the inline cue, build the top level, show the badges. Called
    /// by the controller's own trigger when the access-key cue turns on and the capability gate permits.</summary>
    void Enter();

    /// <summary>Tear the overlay down: hide badges, clear the level stack, restore the inline cue, restore focus.
    /// Idempotent — fires from the cue-off path, a leaf activation, or a window-activation change.</summary>
    void Exit();
}
