namespace Cursorial.UI.Input;

/// <summary>
/// An element activatable through an access key (design doc §7.8) — Button, MenuItem, TabItem,
/// Label (which forwards focus to its target). Registered with
/// <see cref="AccessKeyManager.Register"/>; eligibility and the activation reaction are the
/// target's to define.
/// </summary>
public interface IAccessKeyTarget
{
    /// <summary>Whether the target may activate right now (conventionally: effectively visible and enabled).</summary>
    bool IsAccessKeyEligible { get; }

    /// <summary>
    /// The activation callback. With <see cref="AccessKeyEventArgs.IsMultiMatch"/> false the target
    /// should invoke (a button clicks); with it true the manager has already focused the target —
    /// <b>multi-match focuses, never invokes</b> (ND18).
    /// </summary>
    void OnAccessKey(AccessKeyEventArgs e);
}

/// <summary>Args for <see cref="IAccessKeyTarget.OnAccessKey"/>.</summary>
public sealed class AccessKeyEventArgs : EventArgs
{
    /// <summary>
    /// Creates activation args. Public so <see cref="IAccessKeyTarget"/> implementations can be
    /// unit-tested without driving the full <see cref="AccessKeyManager"/> activation path.
    /// </summary>
    /// <param name="key">The activated access key (conventionally registry-folded, lower-invariant).</param>
    /// <param name="isMultiMatch">Whether this is a focus-only cycling step (see <see cref="IsMultiMatch"/>).</param>
    /// <param name="target">The matched element.</param>
    public AccessKeyEventArgs(char key, bool isMultiMatch, UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Key = key;
        IsMultiMatch = isMultiMatch;
        Target = target;
    }

    /// <summary>The activated access key (registry-folded, lower-invariant).</summary>
    public char Key { get; }

    /// <summary>
    /// Whether multiple targets matched — a focus-only cycling step (the manager moved focus here;
    /// nothing should invoke). False means this target is the unique match and should invoke.
    /// </summary>
    public bool IsMultiMatch { get; }

    /// <summary>The matched element.</summary>
    public UIElement Target { get; }
}

/// <summary>
/// The main-menu registration point (design doc §7.8): S8's menu bar installs itself via
/// <see cref="AccessKeyManager.MainMenu"/> and is notified on menu-mode entry (an Alt tap or
/// unhandled F10 — ND19), alongside the <see cref="AccessKeyManager.EnterMenuMode"/> event.
/// </summary>
public interface IMainMenu
{
    /// <summary>Called on menu-mode entry — the menu conventionally takes focus.</summary>
    void OnEnterMenuMode();
}
