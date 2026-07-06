using Cursorial.UI.Bars.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// Enables the Bars KeyTip overlay on a <see cref="UIApplication"/> (keytips-design §1). A bars app calls this once
/// during setup; it installs the <see cref="KeyTipController"/> into the no-op-by-default engine seams (the
/// <c>AccessKeyManager</c> arm/disarm slot and the frame-loop layout hook) and returns it. Idempotent — a second
/// call returns the already-installed controller. Only a bars app opts in, and <c>Cursorial.UI</c> never references
/// Bars (the controller flows in through the internal seams, visible via <c>InternalsVisibleTo</c>).
/// </summary>
public static class KeyTipExtensions
{
    /// <summary>Installs (or returns the already-installed) KeyTip overlay controller for <paramref name="app"/>.</summary>
    public static KeyTipController EnableKeyTips(this UIApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.AccessKeys.KeyTipController is KeyTipController existing)
            return existing;

        var controller = new KeyTipController(app);
        app.AccessKeys.KeyTipController = controller; // arm/disarm off CueActivated/CueDeactivated
        app.KeyTipController = controller;           // the post-layout re-anchor/park hook (IKeyTipLayoutHook)
        return controller;
    }
}
