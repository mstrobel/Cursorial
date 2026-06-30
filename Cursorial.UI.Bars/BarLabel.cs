using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A non-interactive caption on a bar surface (the bars guide's text/label element): static text (or an
/// <see cref="Icon"/>) used to title a cluster of bar controls. Derives from <see cref="ContentControl"/> so it shows
/// either, is not focusable, and renders in the muted bar foreground. It packs and overflows like any
/// <see cref="ToolbarOverflowMode.AsNeeded"/> item on a <see cref="Toolbar"/>.
/// </summary>
public class BarLabel : ContentControl, IAccessKeyTarget
{
    static BarLabel()
    {
        // The ParsesAccessKeyLiterals flag is set on BarLabel.Content (resolved against the runtime type,
        // doc §12.5 producer ②) — a string Content like "_Name" folds to an AccessText mnemonic.
        ContentProperty.OverrideMetadata<BarLabel>(new PropertyMetadata<object?> { ParsesAccessKeyLiterals = true });

        FocusableProperty.OverrideDefaultValue<BarLabel>(false);
        IsTabStopProperty.OverrideDefaultValue<BarLabel>(false); // never a tab stop (metadata parity with Label)
        ThemeProperty.OverrideDefaultValue<BarLabel>(CursorialBarsTheme.BarLabelStyle());
    }

    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyEnabled && IsEffectivelyVisible;

    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e)
    {
        // Multi-match cycling is the manager's job (it focuses among the matches); a forwarder never invokes —
        // ND18, mirroring Label.OnAccessKey. Without this a caption sharing a mnemonic would forward on every step.
        if (e.IsMultiMatch)
            return;

        if (UIApplication.Current?.FocusManager.FindNext(this, FocusNavigationDirection.Right) is not { } target)
            return;

        // A caption forwards INTO the cluster to its RIGHT. On a DirectionalNavigation=Cycle bar a trailing label's
        // rightward query wraps back to the leftmost control — reject that: forward only to a control genuinely to
        // the right, so a trailing label's mnemonic is a no-op rather than a jump to the bar's start.
        if (target.TranslateToWindow(0, 0).Column <= TranslateToWindow(0, 0).Column)
            return;

        // AccessKey (not Directional): a one-shot mnemonic. AccessKey entry arms the non-retaining Toolbar's
        // auto-return (FocusManager.RetainedReturnAuto), so a subsequent invoke returns focus to the pre-bar element
        // — the WPF mnemonic UX (matches Label/Menu). Directional would strand focus in the bar until Escape.
        target.Focus(FocusNavigationMethod.AccessKey);
    }
}
