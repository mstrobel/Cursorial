using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A non-focusable caption with an access-key mnemonic that forwards focus (design doc §12.5/§12.7):
/// <see cref="ContentControl.Content"/> folds access-key literals (the <c>ParsesAccessKeyLiterals</c>
/// flag on <c>Label.Content</c>), and <see cref="IAccessKeyTarget.OnAccessKey"/> focuses
/// <c>Target ?? FocusManager.FindNext(this)</c>. A <see cref="Label"/> is never focusable / never a
/// tab stop.
/// </summary>
public class Label : ContentControl, IAccessKeyTarget
{
    /// <summary>The element focused on access-key activation; <see langword="null"/> ⇒ <c>FocusManager.FindNext(this)</c> (doc §12.7).</summary>
    public static readonly StyledProperty<UIElement?> TargetProperty =
        UIProperty.Register<Label, UIElement?>(nameof(Target));

    static Label()
    {
        // The ParsesAccessKeyLiterals flag is set on Label.Content (resolved against the runtime type,
        // doc §12.5 producer ②) — a string Content like "_Name" folds to an AccessText mnemonic.
        ContentProperty.OverrideMetadata<Label>(new PropertyMetadata<object?> { ParsesAccessKeyLiterals = true });

        // Never focusable / never a tab stop (doc §12.7).
        FocusableProperty.OverrideDefaultValue<Label>(false);
        IsTabStopProperty.OverrideDefaultValue<Label>(false);
    }

    /// <inheritdoc cref="TargetProperty"/>
    public UIElement? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <inheritdoc/>
    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyEnabled && IsEffectivelyVisible;

    /// <inheritdoc/>
    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e) => OnAccessKey(e);

    /// <summary>The access-key reaction (doc §12.7): focus <c>Target ?? FindNext(this)</c>. Multi-match focuses only (ND18).</summary>
    protected virtual void OnAccessKey(AccessKeyEventArgs e)
    {
        if (e.IsMultiMatch)
            return; // the manager already focused us; multi-match never invokes (ND18)

        var target = Target ?? UIApplication.Current?.FocusManager.FindNext(this);
        target?.Focus(FocusNavigationMethod.AccessKey);
    }
}
