using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A non-focusable caption with an access-key mnemonic that forwards focus (design doc §12.5/§12.7):
/// <see cref="ContentControl.Content"/> folds access-key literals (the <c>ParsesAccessKeyLiterals</c>
/// flag on <c>Label.Content</c>), and <see cref="IAccessKeyTarget.OnAccessKey"/> focuses
/// <c>Target ?? FocusManager.FindNext(this)</c>. A <see cref="Label"/> is never focusable / never a
/// tab stop.
/// </summary>
public class Label : ContentControl, IAccessKeyTarget, IRichTextCapable
{
    /// <summary>The element focused on access-key activation; <see langword="null"/> ⇒ <c>FocusManager.FindNext(this)</c> (doc §12.7).</summary>
    public static readonly StyledProperty<UIElement?> TargetProperty =
        UIProperty.Register<Label, UIElement?>(nameof(Target), changed: OnTargetChanged);

    private static void OnTargetChanged(UIObject sender, UIElement? oldValue, UIElement? newValue)
    {
        if (sender is Label l)
            l.UpdateAccessKeyProxyRegistration();
    }

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

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        UpdateAccessKeyProxyRegistration(); // (re-)claim the target now that we're live
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        ClearAccessKeyProxyRegistration(); // release the target — never leave it pointing at a detached Label
        base.OnDetachedFromTree(in e);
    }

    // Pairs Target with this Label as its access-key proxy (so the manager forwards the mnemonic to Target),
    // first-wins: a target already claimed by another proxy is left alone (this Label then forwards in OnAccessKey).
    // Re-run on Target change and on (re-)attach; the prior registration is released first.
    private void UpdateAccessKeyProxyRegistration()
    {
        ClearAccessKeyProxyRegistration();

        if (IsAttachedToTree
            && Target is {} target
            && target.GetValueSource(AccessKeyManager.AccessKeyProxyProperty).Kind is ValueSourceKind.Default)
        {
            AccessKeyManager.SetAccessKeyProxy(target, this);
            AccessKeyManager.SetAccessKeyProxyFor(this, target);
        }
    }

    private void ClearAccessKeyProxyRegistration()
    {
        if (AccessKeyManager.GetAccessKeyProxyFor(this) is not {} target)
            return;

        if (AccessKeyManager.GetAccessKeyProxy(target) is {} proxy && ReferenceEquals(proxy, this))
            target.ClearValue(AccessKeyManager.AccessKeyProxyProperty);

        ClearValue(AccessKeyManager.AccessKeyProxyForProperty);
    }
}
