using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// The content host (design doc §12.3): realizes a single <see cref="Content"/> into a visual
/// <see cref="Child"/> via the DataTemplate lookup chain (CD22). Inside a template, an unset
/// <see cref="Content"/>/<see cref="ContentTemplate"/> <b>auto-aliases</b> (read-through, never an
/// installed binding) to the <c>TemplatedParent</c>'s <c>Content</c>/<c>ContentTemplate</c> (CD21).
/// </summary>
public sealed class ContentPresenter : UIElement
{
    private UIElement? _child;
    private object? _realizedContent;          // the content identity the current Child was built from
    private DataTemplate? _realizedTemplate;    // the template identity the current Child was built from
    private bool _realizing;                    // recursion guard (C147)
    private IDisposable? _aliasContentObserver;
    private IDisposable? _aliasTemplateObserver;
    private IDisposable? _aliasStringFormatObserver;
    private ContentControl? _aliasSource;
    private string? _realizedStringFormat; // the string format the current Child was built from (no-template reuse key)
    private IDisposable? _hAlignObserver;
    private IDisposable? _vAlignObserver;
    private ContentControl? _alignmentSource;        // the templated parent we read through to (CD21)

    /// <summary>The presenter's content (any object); mirrors <see cref="ContentControl.Content"/>.</summary>
    public static readonly StyledProperty<object?> ContentProperty =
        UIProperty.Register<ContentPresenter, object?>(nameof(Content), changed: OnContentChanged);

    /// <summary>The explicit template for the content.</summary>
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty =
        UIProperty.Register<ContentPresenter, DataTemplate?>(nameof(ContentTemplate), changed: OnContentTemplateChanged);

    /// <summary>A composite format applied to non-templated content rendered as text; mirrors
    /// <see cref="ContentControl.ContentStringFormat"/> and auto-aliases to it (CD21).</summary>
    public static readonly StyledProperty<string?> ContentStringFormatProperty =
        UIProperty.Register<ContentPresenter, string?>(nameof(ContentStringFormat), changed: OnContentStringFormatChanged);

    /// <summary>Whether a plain-string content is parsed for an access-key mnemonic (default false — doc §12.3/§12.5).</summary>
    public static readonly StyledProperty<bool> RecognizesAccessKeyProperty =
        UIProperty.Register<ContentPresenter, bool>(nameof(RecognizesAccessKey), changed: OnRecognizesAccessKeyChanged);

    /// <summary>Whether a plain-string content is parsed as <see cref="TextMarkup">text markup.</see></summary>
    public static readonly StyledProperty<bool> RecognizesMarkupProperty =
        UIProperty.Register<ContentPresenter, bool>(nameof(RecognizesMarkup), changed: OnRecognizesMarkupChanged);

    /// <summary>Whether the <see cref="TextElement.InverseProperty"/> is forwarded to the realized content.</summary>
    public static readonly StyledProperty<bool> ForwardTextInverseProperty =
        UIProperty.Register<ContentPresenter, bool>(nameof(ForwardTextInverse),
                                                    changed: OnForwardingDetailsChanged,
                                                    defaultValue: true);

    /// <summary>
    /// When <c>false</c>, <see cref="TextElement">text formatting</see> and other forwarded properties will
    /// always be forwarded from the host <see cref="ContentPresenter"/> itself, rather than the host's
    /// <see cref="UIElement.TemplatedParent">TemplatedParent</see> when available. Defaults to <c>true</c>.
    /// </summary>
    public static readonly StyledProperty<bool> ForwardsFromTemplatedParentProperty =
        UIProperty.Register<ContentPresenter, bool>(nameof(ForwardsFromTemplatedParent),
                                                    changed: OnForwardingDetailsChanged,
                                                    defaultValue: true);

    static ContentPresenter()
    {
        AffectsMeasure<ContentPresenter>(ContentProperty, ContentTemplateProperty, ContentStringFormatProperty, RecognizesAccessKeyProperty);
        // The realized child swaps on any of these even when its desired size is unchanged, so the zone must
        // re-render too (a same-length string-format change, an access-key re-fold).
        AffectsRender<ContentPresenter>(ContentProperty, ContentTemplateProperty, ContentStringFormatProperty, RecognizesAccessKeyProperty);
    }

    /// <inheritdoc cref="ContentProperty"/>
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <inheritdoc cref="ContentTemplateProperty"/>
    public DataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    /// <inheritdoc cref="ContentStringFormatProperty"/>
    public string? ContentStringFormat { get => GetValue(ContentStringFormatProperty); set => SetValue(ContentStringFormatProperty, value); }

    /// <inheritdoc cref="RecognizesAccessKeyProperty"/>
    public bool RecognizesAccessKey { get => GetValue(RecognizesAccessKeyProperty); set => SetValue(RecognizesAccessKeyProperty, value); }

    /// <inheritdoc cref="RecognizesMarkupProperty"/>
    public bool RecognizesMarkup { get => GetValue(RecognizesMarkupProperty); set => SetValue(RecognizesMarkupProperty, value); }

    /// <inheritdoc cref="ForwardTextInverseProperty"/>
    public bool ForwardTextInverse { get => GetValue(ForwardTextInverseProperty); set => SetValue(ForwardTextInverseProperty, value); }

    /// <inheritdoc cref="ForwardsFromTemplatedParentProperty"/>
    public bool ForwardsFromTemplatedParent { get => GetValue(ForwardsFromTemplatedParentProperty); set => SetValue(ForwardsFromTemplatedParentProperty, value); }

    /// <summary>The realized visual child (diagnostic; null before first measure / empty content).</summary>
    public UIElement? Child => _child;

    /// <summary>The chain-③ logical adoption of element content for a free-standing presenter (no <c>ContentControl</c> host).</summary>
    internal void AdoptElementContentLogically(UIElement element) => AddLogicalChild(element);

    // ───────────────────────────── measure / arrange ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureChild();

        if (_child is null)
            return Size.Empty;

        _child.Measure(availableSize);
        return _child.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_child is null)
            return finalSize;

        // Position the content per the templated ContentControl's Horizontal/VerticalContentAlignment (the WPF
        // feature). Default Stretch ⇒ the child fills the full rect (then its OWN alignment applies) — byte-identical
        // to the prior behavior. Non-Stretch ⇒ the child takes its desired size, placed left/center/right (top/…).
        var (h, v) = EffectiveContentAlignment();
        var width = h == HorizontalAlignment.Stretch ? finalSize.Columns : Math.Min(_child.DesiredSize.Columns, finalSize.Columns);
        var height = v == VerticalAlignment.Stretch ? finalSize.Rows : Math.Min(_child.DesiredSize.Rows, finalSize.Rows);
        var x = h switch
        {
            HorizontalAlignment.Right => Math.Max(0, finalSize.Columns - width),
            HorizontalAlignment.Center => Math.Max(0, (finalSize.Columns - width) / 2),
            _ => 0, // Left, Stretch
        };
        var y = v switch
        {
            VerticalAlignment.Bottom => Math.Max(0, finalSize.Rows - height),
            VerticalAlignment.Center => Math.Max(0, (finalSize.Rows - height) / 2),
            _ => 0, // Top, Stretch
        };

        _child.Arrange(new Rect(x, y, width, height));
        return finalSize;
    }

    // The content alignment from the templated ContentControl (Stretch for a free-standing / non-ContentControl parent).
    private (HorizontalAlignment Horizontal, VerticalAlignment Vertical) EffectiveContentAlignment()
        => TemplatedParent is ContentControl cc
            ? (cc.HorizontalContentAlignment, cc.VerticalContentAlignment)
            : (HorizontalAlignment.Stretch, VerticalAlignment.Stretch);

    // ───────────────────────────── auto-alias lifecycle (CD21) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        UpdateAliasSubscription();
        UpdateAlignmentSubscription();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // A directly-hosted UIElement content (the item IS the child — its visual parent is THIS presenter) must
        // release that parentage on detach, so the same item can be re-hosted by another presenter — e.g. a RECYCLED
        // virtualization container — without an "already has a visual parent" crash. Built children (a TextBlock from
        // string content) are presenter-owned and not shared, so they stay; either way the next measure re-realizes.
        if (_child is not null && ReferenceEquals(_child, _realizedContent))
            RebuildChild(null, null, null);

        // Lifetime = template instance: the auto-alias observers tear down with the presenter's
        // detach (which the templated parent's Detach() triggers via the Root subtree walk — CD20/CD21).
        TearDownAlias();
        TearDownAlignment();
        base.OnDetachedFromTree(in e);
    }

    // Re-arrange when the templated ContentControl's content alignment changes (live), since ArrangeOverride reads it
    // but AffectsArrange on the parent doesn't reach the deep presenter (the parent re-arranges it with the same rect,
    // which short-circuits). A change to the parent's H/VContentAlignment invalidates only this presenter's arrange.
    private void UpdateAlignmentSubscription()
    {
        if (TemplatedParent is ContentControl cc)
        {
            if (ReferenceEquals(_alignmentSource, cc))
                return;
            TearDownAlignment();
            _alignmentSource = cc;
            _hAlignObserver = cc.AddObserver(ContentControl.HorizontalContentAlignmentProperty, new HAlignObserver(this));
            _vAlignObserver = cc.AddObserver(ContentControl.VerticalContentAlignmentProperty, new VAlignObserver(this));
        }
        else
        {
            TearDownAlignment();
        }
    }

    private void TearDownAlignment()
    {
        _hAlignObserver?.Dispose();
        _vAlignObserver?.Dispose();
        _hAlignObserver = null;
        _vAlignObserver = null;
        _alignmentSource = null;
    }

    // The auto-alias is active when neither local property IsSet and the presenter is a template part
    // (TemplatedParent is a ContentControl). It reads through to that parent's Content/ContentTemplate
    // WITHOUT installing a binding (a binding would create a frame, flip IsSet, and break its own
    // condition — CD21). A typed observer on the parent re-realizes on change.
    private bool AliasActive => TemplatedParent is ContentControl && !IsSet(ContentProperty) && !IsSet(ContentTemplateProperty);

    private void UpdateAliasSubscription()
    {
        if (AliasActive && TemplatedParent is ContentControl parent)
        {
            if (ReferenceEquals(_aliasSource, parent))
                return; // already subscribed to this parent

            TearDownAlias();
            _aliasSource = parent;
            _aliasContentObserver = parent.AddObserver(ContentControl.ContentProperty, new AliasObserver(this));
            _aliasTemplateObserver = parent.AddObserver(ContentControl.ContentTemplateProperty, new AliasTemplateObserver(this));
            _aliasStringFormatObserver = parent.AddObserver(ContentControl.ContentStringFormatProperty, new AliasStringFormatObserver(this));
        }
        else
        {
            TearDownAlias();
        }
    }

    private void TearDownAlias()
    {
        _aliasContentObserver?.Dispose();
        _aliasTemplateObserver?.Dispose();
        _aliasStringFormatObserver?.Dispose();
        _aliasContentObserver = null;
        _aliasTemplateObserver = null;
        _aliasStringFormatObserver = null;
        _aliasSource = null;
    }

    // The effective content/template after the read-through fallback (CD21).
    private object? EffectiveContent
        => IsSet(ContentProperty) || ResourceDiagnostics.GetResourceKey(this, ContentProperty) is {}
               ? Content
               : AliasActive && TemplatedParent is ContentControl parent
                   ? parent.Content
                   : Content;

    private DataTemplate? EffectiveContentTemplate
        => IsSet(ContentTemplateProperty) ? ContentTemplate
           : AliasActive && TemplatedParent is ContentControl parent ? parent.ContentTemplate
           : ContentTemplate;

    private string? EffectiveContentStringFormat
        => IsSet(ContentStringFormatProperty) ? ContentStringFormat
           : AliasActive && TemplatedParent is ContentControl parent ? parent.ContentStringFormat
           : ContentStringFormat;

    // ───────────────────────────── realization (the lookup chain, CD22) ─────────────────────────────

    private void EnsureChild()
    {
        if (_realizing)
        {
            ControlDiagnostics.ContentRecursion(this);
            return;
        }

        var content = EffectiveContent;
        var explicitTemplate = EffectiveContentTemplate;
        var resolvedTemplate = explicitTemplate ?? ContentRealization.FindImplicitTemplate(this, content);
        var stringFormat = EffectiveContentStringFormat;

        // Same resolved template identity on a content change reuses the subtree (CD22/C157): only the
        // DataContext updates. A template-identity change rebuilds (C158). With no template (string /
        // element / AccessText content), reuse keys on content identity AND the string format (the latter
        // re-renders the text fallback).
        if (_child is not null && ReferenceEquals(_realizedTemplate, resolvedTemplate))
        {
            if (resolvedTemplate is not null)
            {
                if (!ReferenceEquals(_realizedContent, content))
                {
                    _realizedContent = content;
                    _child.DataContext = content; // data-context update only — subtree reused (format ignored under a template)
                }

                return;
            }

            // No template: reuse only when the content identity AND the string format are unchanged.
            if (ReferenceEquals(_realizedContent, content) && string.Equals(_realizedStringFormat, stringFormat, StringComparison.Ordinal))
                return;
        }

        _realizing = true;
        
        try
        {
            RebuildChild(content, resolvedTemplate, stringFormat);
        }
        finally
        {
            _realizing = false;
        }
    }

    private bool _childLogicallyOwned; // the presenter adopted the element content logically (free-standing case, chain ③)

    // The framework-installed Inverse forward on BORROWED Icon content (§2.1) — the presenter owns its
    // teardown because RebuildChild leaves borrowed content's own bindings alone, so a source-anchored
    // forward would leak the Icon otherwise (audit fix 2026-07-13). Null unless the current child is an Icon.
    private BindingExpressionBase? _borrowedIconForward;

    // The framework-installed Inverse forward on ADOPTED UIElement content (§2.1) — the presenter owns its
    // teardown because RebuildChild leaves borrowed content's own bindings alone, so a source-anchored
    // forward would leak the element otherwise (audit fix 2026-07-13). Null unless the content is an adopted
    // UIElement.
    private BindingExpressionBase? _adoptedContentForward;

    private void RebuildChild(object? content, DataTemplate? template, string? stringFormat)
    {
        if (_child is {} old)
        {
            // The framework's own forward onto borrowed Icon content — dispose it here (it is NOT the
            // author's, and RemoveVisualChild does not tear it down): the source-anchored observer would
            // otherwise pin the unhosted Icon to the live templated parent on every content swap / recycle.
            _borrowedIconForward?.Dispose();
            _borrowedIconForward = null;

            // The framework's own forward onto adopted UIElement content — dispose it here (it is NOT the
            // author's, and RemoveVisualChild does not tear it down): the source-anchored observer would
            // otherwise pin the unhosted element to the live templated parent on every content swap / recycle.
            _adoptedContentForward?.Dispose();
            _adoptedContentForward = null;

            // A presenter-BUILT child (a TextBlock realized from string/object content — NOT borrowed element content,
            // where the child IS the content and the author owns it) is presenter-owned and discarded here. Tear its
            // bindings down: RemoveVisualChild does NOT run TearDown (by design), so a Source-anchored binding it
            // carries — e.g. the fallback TextBlock's live TextWrapping link back to THIS presenter — would otherwise
            // leak its observer onto us on every content rebuild (each discarded child pinned alive). Borrowed content
            // (old == _realizedContent) is left untouched — its bindings belong to the author, not us.
            if (!ReferenceEquals(old, _realizedContent))
                BindingOperations.TearDown(old);

            if (_childLogicallyOwned && ReferenceEquals(old.LogicalParent, this))
                RemoveLogicalChild(old);

            RemoveVisualChild(old);
            _child = null;
            _childLogicallyOwned = false;
        }

        var built = ContentRealization.Realize(this,
                                               content,
                                               template,
                                               RecognizesAccessKey,
                                               RecognizesMarkup,
                                               stringFormat);

        _realizedContent = content;
        _realizedTemplate = template;
        _realizedStringFormat = stringFormat;
        _child = built;

        if (built is not null)
        {
            _childLogicallyOwned = ReferenceEquals(built.LogicalParent, this);
            AddVisualChildOnly(built);
            RedirectBorrowedContentInheritance(built);

            // An Icon carries the Inverse cue (a glyph — Inverse only; §2.1). It is borrowed content, so
            // the presenter installs AND owns the forward (disposed above on the next rebuild).
            if (ForwardTextInverse)
            {
                if (built is Icon)
                    _borrowedIconForward = ContentRealization.ForwardInverseOnly(this, built);
                else if (content is UIElement && built == content)
                    _adoptedContentForward = ContentRealization.ForwardInverseOnly(this, built);
            }
        }
    }

    // WPF parity for BORROWED element content — a UIElement hosted here whose LOGICAL owner is a foreign control.
    // The canonical case is a TabControl: a TabItem's Content is a logical child of the TabItem, but visually it is
    // shown ONLY here (the TabControl's PART_ContentHost). AddVisualChildOnly rewires the content's inheritance
    // parent to its UIParent (= its logical owner, the TabItem), so it would inherit values (Foreground, …) from
    // that item — e.g. the rail item's STATE-DEPENDENT label ink (dark on a light focus fill), which is invisible on
    // the detail surface, and which flips as the item gains/loses :focus. Re-point the inheritance parent at THIS
    // visual host so inherited values flow from the content area instead — matching WPF, where hosted content
    // inherits through the visual tree. RemoveVisualChild restores it to UIParent on unhost (symmetric).
    //
    // Left untouched (default UIParent-driven inheritance) when the content is logically owned by THIS presenter
    // (free-standing chain-③ adoption) or by this presenter's OWN templated parent — an ordinary ContentControl
    // (Button.Content), where UIParent already IS the correct source and no redirect is needed.
    private void RedirectBorrowedContentInheritance(UIElement child)
    {
        if (child.LogicalParent is { } owner &&
            !ReferenceEquals(owner, this) &&
            !ReferenceEquals(owner, TemplatedParent))
        {
            child.SetInheritanceParent(this);
        }
    }

    /// <summary>Forces a re-realization (the auto-alias observer / template change re-entry).</summary>
    private void Refresh()
    {
        // Re-check whether the alias is still active (a later explicit value stops the read-through).
        UpdateAliasSubscription();
        InvalidateMeasure();
    }

    private static void OnContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is ContentPresenter presenter)
            presenter.Refresh(); // a local explicit value flips IsSet → read-through stops (CD21/C144)
    }

    private static void OnContentTemplateChanged(UIObject sender, DataTemplate? oldValue, DataTemplate? newValue)
    {
        if (sender is ContentPresenter presenter)
            presenter.Refresh();
    }

    private static void OnContentStringFormatChanged(UIObject sender, string? oldValue, string? newValue)
    {
        if (sender is ContentPresenter presenter)
            presenter.Refresh(); // a local explicit format stops the read-through and re-renders the text fallback
    }

    private static void OnRecognizesAccessKeyChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is ContentPresenter presenter)
        {
            presenter._realizedContent = NoContentSentinel; // force a rebuild (the realization branch changes)
            presenter.InvalidateMeasure();
        }
    }

    private static void OnForwardingDetailsChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is ContentPresenter presenter)
        {
            presenter._realizedContent = NoContentSentinel; // force a rebuild (the realization branch changes)
            presenter.InvalidateMeasure();
        }
    }

    private static void OnRecognizesMarkupChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is ContentPresenter presenter)
        {
            presenter._realizedContent = NoContentSentinel; // force a rebuild (the realization branch changes)
            presenter.InvalidateMeasure();
        }
    }

    private static readonly object NoContentSentinel = new();

    // The typed read-through observers on the templated parent (no presenter store entry — CD21).
    private sealed class AliasObserver(ContentPresenter presenter) : IValueObserver<object?>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
        {
            if (presenter.AliasActive)
                presenter.InvalidateMeasure(); // re-realize through the read-through (C143)
        }
    }

    private sealed class AliasTemplateObserver(ContentPresenter presenter) : IValueObserver<DataTemplate?>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, DataTemplate? oldValue, DataTemplate? newValue, BindingPriority priority)
        {
            if (presenter.AliasActive)
                presenter.InvalidateMeasure();
        }
    }

    private sealed class AliasStringFormatObserver(ContentPresenter presenter) : IValueObserver<string?>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, string? oldValue, string? newValue, BindingPriority priority)
        {
            if (presenter.AliasActive)
                presenter.InvalidateMeasure();
        }
    }

    // The templated parent's content alignment changed → re-arrange (ArrangeOverride re-reads it).
    private sealed class HAlignObserver(ContentPresenter presenter) : IValueObserver<HorizontalAlignment>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, HorizontalAlignment oldValue, HorizontalAlignment newValue, BindingPriority priority)
            => presenter.InvalidateArrange();
    }

    private sealed class VAlignObserver(ContentPresenter presenter) : IValueObserver<VerticalAlignment>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, VerticalAlignment oldValue, VerticalAlignment newValue, BindingPriority priority)
            => presenter.InvalidateArrange();
    }
}
