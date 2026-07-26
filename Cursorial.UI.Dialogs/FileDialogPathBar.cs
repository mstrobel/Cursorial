using System;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// A <see cref="BreadcrumbBar"/> that surfaces its raw-text edit box, so the dialog can hang a
/// <see cref="CompletionPopup"/> on it.
/// <para>
/// <b>Why a subclass at all.</b> <c>PART_EditBox</c> is a template part, and template parts are private to
/// their control by design — <see cref="BreadcrumbBar"/> exposes it only as an <c>internal</c> test seam.
/// <see cref="Control.GetTemplatePart{T}"/> is <c>protected internal</c>, though, which makes DERIVING the
/// sanctioned way to reach a part from outside the assembly. That is all this type does: the same part, the
/// same lifetime, no behaviour of its own.
/// </para>
/// <para>
/// <b>The theme key is pinned to the base type.</b> Control themes resolve by EXACT key, so a subclass with no
/// theme of its own gets no template at all and measures 0×0 (the trap
/// <see cref="Configuration.UserOptionsDialog"/> documents). Overriding
/// <see cref="Control.ControlThemeKey"/> opts back into the real <see cref="BreadcrumbBar"/> chrome.
/// </para>
/// </summary>
internal sealed class FileDialogPathBar : BreadcrumbBar
{
    /// <summary>The edit box's part name, as declared by <see cref="BreadcrumbBar"/>'s
    /// <see cref="TemplatePartAttribute"/> (a private constant there, so it is restated here).</summary>
    private const string PartEditBox = "PART_EditBox";

    /// <inheritdoc/>
    protected override object ControlThemeKey => typeof(BreadcrumbBar);

    /// <summary>
    /// The raw-text edit box, or <see langword="null"/> before the template is expanded. Realized by the time
    /// <see cref="BreadcrumbBar.EditingStarted"/> is raised (<see cref="BreadcrumbBar.BeginEdit"/> applies the
    /// template first), which is where the dialog attaches its completion popup.
    /// </summary>
    internal TextBox? EditBox => GetTemplatePart<TextBox>(PartEditBox);

    /// <summary>Raised after every template expansion — the point at which <see cref="EditBox"/> becomes a
    /// different instance and anything bound to it must be re-pointed.</summary>
    internal event EventHandler? TemplateApplied;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        TemplateApplied?.Invoke(this, EventArgs.Empty);
    }
}
