using Cursorial.Rendering;

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
    private ContentControl? _aliasSource;        // the templated parent we read through to (CD21)

    /// <summary>The presenter's content (any object); mirrors <see cref="ContentControl.Content"/>.</summary>
    public static readonly StyledProperty<object?> ContentProperty =
        UIProperty.Register<ContentPresenter, object?>(nameof(Content), changed: OnContentChanged);

    /// <summary>The explicit template for the content.</summary>
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty =
        UIProperty.Register<ContentPresenter, DataTemplate?>(nameof(ContentTemplate), changed: OnContentTemplateChanged);

    /// <summary>Whether a plain-string content is parsed for an access-key mnemonic (default false — doc §12.3/§12.5).</summary>
    public static readonly StyledProperty<bool> RecognizesAccessKeyProperty =
        UIProperty.Register<ContentPresenter, bool>(nameof(RecognizesAccessKey), changed: OnRecognizesAccessKeyChanged);

    static ContentPresenter()
    {
        AffectsMeasure<ContentPresenter>(ContentProperty, ContentTemplateProperty, RecognizesAccessKeyProperty);
    }

    /// <inheritdoc cref="ContentProperty"/>
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <inheritdoc cref="ContentTemplateProperty"/>
    public DataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    /// <inheritdoc cref="RecognizesAccessKeyProperty"/>
    public bool RecognizesAccessKey { get => GetValue(RecognizesAccessKeyProperty); set => SetValue(RecognizesAccessKeyProperty, value); }

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
        _child?.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }

    // ───────────────────────────── auto-alias lifecycle (CD21) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        UpdateAliasSubscription();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // Lifetime = template instance: the auto-alias observers tear down with the presenter's
        // detach (which the templated parent's Detach() triggers via the Root subtree walk — CD20/CD21).
        TearDownAlias();
        base.OnDetachedFromTree(in e);
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
        _aliasContentObserver = null;
        _aliasTemplateObserver = null;
        _aliasSource = null;
    }

    // The effective content/template after the read-through fallback (CD21).
    private object? EffectiveContent
        => IsSet(ContentProperty) ? Content
           : AliasActive && TemplatedParent is ContentControl parent ? parent.Content
           : Content;

    private DataTemplate? EffectiveContentTemplate
        => IsSet(ContentTemplateProperty) ? ContentTemplate
           : AliasActive && TemplatedParent is ContentControl parent ? parent.ContentTemplate
           : ContentTemplate;

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

        // Same resolved template identity on a content change reuses the subtree (CD22/C157): only the
        // DataContext updates. A template-identity change rebuilds (C158). With no template (string /
        // element / AccessText content), reuse keys on content identity instead.
        if (_child is not null && ReferenceEquals(_realizedTemplate, resolvedTemplate))
        {
            if (resolvedTemplate is not null)
            {
                if (!ReferenceEquals(_realizedContent, content))
                {
                    _realizedContent = content;
                    _child.DataContext = content; // data-context update only — subtree reused
                }

                return;
            }

            // No template: reuse only when the content identity is unchanged too.
            if (ReferenceEquals(_realizedContent, content))
                return;
        }

        _realizing = true;
        try
        {
            RebuildChild(content, resolvedTemplate);
        }
        finally
        {
            _realizing = false;
        }
    }

    private bool _childLogicallyOwned; // the presenter adopted the element content logically (free-standing case, chain ③)

    private void RebuildChild(object? content, DataTemplate? template)
    {
        if (_child is { } old)
        {
            if (_childLogicallyOwned && ReferenceEquals(old.LogicalParent, this))
                RemoveLogicalChild(old);
            RemoveVisualChild(old);
            _child = null;
            _childLogicallyOwned = false;
        }

        var built = ContentRealization.Realize(this, content, template, RecognizesAccessKey);

        _realizedContent = content;
        _realizedTemplate = template;
        _child = built;

        if (built is not null)
        {
            _childLogicallyOwned = ReferenceEquals(built.LogicalParent, this);
            AddVisualChildOnly(built);
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

    private static void OnRecognizesAccessKeyChanged(UIObject sender, bool oldValue, bool newValue)
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
}
