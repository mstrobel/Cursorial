using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A control with a single piece of arbitrary <see cref="Content"/> (design doc §12.3): the content
/// is realized through a <see cref="ContentPresenter"/> in the control's template (the presenter
/// auto-aliases to <see cref="Content"/>/<see cref="ContentTemplate"/>, CD21). <c>Content</c> that is
/// a <see cref="UIElement"/> becomes a logical child so it inherits the control's DataContext.
/// </summary>
/// <remarks>
/// This type owns the access-key registration lifecycle (doc §12.5 producer ③): when a derived control
/// folds access-key literals on <see cref="ContentProperty"/> and is itself an <see cref="IAccessKeyTarget"/>
/// (<see cref="ButtonBase"/>, <see cref="Label"/>), the folded mnemonic is registered with
/// <see cref="AccessKeyManager"/> on attach and re-registered on a <see cref="Content"/> change. A
/// <see cref="ContentControl"/> that is not an <see cref="IAccessKeyTarget"/> registers nothing.
/// </remarks>
[Cursorial.Markup.ContentProperty("Content")]
public class ContentControl : Control
{
    /// <summary>The control's content (<c>AffectsMeasure</c>; any object).</summary>
    public static readonly StyledProperty<object?> ContentProperty =
        UIProperty.Register<ContentControl, object?>(nameof(Content), changed: OnContentChanged);

    /// <summary>The explicit template for the content (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty =
        UIProperty.Register<ContentControl, DataTemplate?>(nameof(ContentTemplate));

    /// <summary>How the content is positioned horizontally within the control (the WPF analog; default
    /// <see cref="HorizontalAlignment.Stretch"/> ⇒ the content fills, then its own alignment applies — the prior
    /// behavior). The <see cref="ContentPresenter"/> in the control's template reads this and aligns the realized
    /// content, so a control theme or consumer opts into Left/Center/Right without touching the content element.</summary>
    public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
        UIProperty.Register<ContentControl, HorizontalAlignment>(nameof(HorizontalContentAlignment), defaultValue: HorizontalAlignment.Stretch);

    /// <summary>How the content is positioned vertically within the control (the WPF analog; default
    /// <see cref="VerticalAlignment.Stretch"/>).</summary>
    public static readonly StyledProperty<VerticalAlignment> VerticalContentAlignmentProperty =
        UIProperty.Register<ContentControl, VerticalAlignment>(nameof(VerticalContentAlignment), defaultValue: VerticalAlignment.Stretch);

    static ContentControl()
    {
        AffectsMeasure<ContentControl>(ContentProperty, ContentTemplateProperty);
    }

    /// <inheritdoc cref="ContentProperty"/>
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <inheritdoc cref="ContentTemplateProperty"/>
    public DataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    /// <inheritdoc cref="HorizontalContentAlignmentProperty"/>
    public HorizontalAlignment HorizontalContentAlignment { get => GetValue(HorizontalContentAlignmentProperty); set => SetValue(HorizontalContentAlignmentProperty, value); }

    /// <inheritdoc cref="VerticalContentAlignmentProperty"/>
    public VerticalAlignment VerticalContentAlignment { get => GetValue(VerticalContentAlignmentProperty); set => SetValue(VerticalContentAlignmentProperty, value); }

    /// <summary>
    /// Called after <see cref="Content"/> changed — the access-key re-registration hook (doc §12.5):
    /// the base re-folds and re-registers its mnemonic with <see cref="AccessKeyManager"/> when this
    /// control is an <see cref="IAccessKeyTarget"/> and attached. Overriders should call
    /// <c>base.OnContentRefreshedForAccessKey()</c>.
    /// </summary>
    protected virtual void OnContentRefreshedForAccessKey()
    {
        if (!IsAttachedToTree)
            return;

        // Content change re-registers (doc §12.5): unhook the old key, re-resolve the new.
        UnregisterAccessKey();
        RegisterAccessKey();
    }

    private static void OnContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is not ContentControl control)
            return;

        // A UIElement content is a logical child of the host (chain ③ / doc §12.3): adopt the new,
        // disown the old, so it inherits the host's DataContext / participates in the logical tree.
        if (oldValue is UIElement oldElement && ReferenceEquals(oldElement.LogicalParent, control))
            control.RemoveLogicalChild(oldElement);

        if (newValue is UIElement newElement && newElement.LogicalParent is null)
            control.AddLogicalChild(newElement);

        control.OnContentRefreshedForAccessKey();
    }

    // ───────────────────────────── access-key registration (doc §12.5 producer ③) ─────────────────────────────

    private char _registeredAccessKey;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        RegisterAccessKey();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        UnregisterAccessKey();
        base.OnDetachedFromTree(in e);
    }

    /// <summary>
    /// The folded access-key label of the content (doc §12.5 producer ③); <see cref="AccessText.HasKey"/>
    /// is <see langword="false"/> when the content carries no mnemonic or this control does not fold
    /// access-key literals. Overridden by controls whose mnemonic source is not <see cref="Content"/>.
    /// </summary>
    protected virtual AccessText GetAccessText()
        => Content is string s && ContentParsesAccessKeyLiterals() ? AccessText.Parse(s) : default;

    /// <summary>Whether <see cref="ContentProperty"/> folds access-key literals for this control's runtime type.</summary>
    protected bool ContentParsesAccessKeyLiterals()
        => ContentProperty.GetMetadata(GetType()).ParsesAccessKeyLiterals == true;

    /// <summary>
    /// Registers this control's folded mnemonic with <see cref="AccessKeyManager"/> (doc §12.5). A no-op
    /// when this control is not an <see cref="IAccessKeyTarget"/>, there is no application/manager, or the
    /// content carries no mnemonic.
    /// </summary>
    private protected void RegisterAccessKey()
    {
        if (this is not IAccessKeyTarget)
            return;
        if (UIApplication.Current?.AccessKeys is not { } manager)
            return;

        var access = GetAccessText();
        if (!access.HasKey)
            return;

        _registeredAccessKey = access.Key;
        manager.Register(access.Key, this);
    }

    /// <summary>Unregisters this control's mnemonic (no-op when none was registered).</summary>
    private protected void UnregisterAccessKey()
    {
        if (_registeredAccessKey != '\0' && UIApplication.Current?.AccessKeys is { } manager)
            manager.Unregister(_registeredAccessKey, this);
        _registeredAccessKey = '\0';
    }
}

/// <summary>
/// A <see cref="ContentControl"/> with an additional <see cref="Header"/> (design doc §12.3) — the
/// shape <c>MenuItem</c> derives from (the items half lands at P9).
/// </summary>
public class HeaderedContentControl : ContentControl
{
    /// <summary>The header content (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        UIProperty.Register<HeaderedContentControl, object?>(nameof(Header));

    /// <summary>The explicit template for the header (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<DataTemplate?> HeaderTemplateProperty =
        UIProperty.Register<HeaderedContentControl, DataTemplate?>(nameof(HeaderTemplate));

    static HeaderedContentControl()
    {
        AffectsMeasure<HeaderedContentControl>(HeaderProperty, HeaderTemplateProperty);
    }

    /// <inheritdoc cref="HeaderProperty"/>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <inheritdoc cref="HeaderTemplateProperty"/>
    public DataTemplate? HeaderTemplate { get => GetValue(HeaderTemplateProperty); set => SetValue(HeaderTemplateProperty, value); }
}
