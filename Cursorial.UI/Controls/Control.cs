using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.UI.Controls;

/// <summary>
/// The templated control base (design doc §12.1): there is no separate <c>TemplatedControl</c>. A
/// <see cref="Template"/> expands lazily at measure time (<see cref="ApplyTemplate"/>, doc §12.2,
/// CD17) into a visual-only subtree whose parts are reachable via <see cref="GetTemplatePart{T}"/>
/// in the template name scope. The control owns <see cref="Background"/>/<see cref="Foreground"/>/
/// <see cref="BorderPen"/>/<see cref="Padding"/> and the <see cref="ControlThemeKey"/> S7 lookup hook.
/// </summary>
public class Control : UIElement, IControlThemeHost
{
    private TemplateInstance? _templateInstance;
    private bool _templateValid;
    private bool _applyingTemplate;
    private bool _reapplyRequested;
    private ResourceSubscription _controlThemeSubscription;

    /// <summary>The control's visual template (<c>AffectsMeasure</c> — a change re-expands at the next measure, CD17).</summary>
    public static readonly StyledProperty<ControlTemplate?> TemplateProperty =
        UIProperty.Register<Control, ControlTemplate?>(nameof(Template), changed: OnTemplateChanged);

    /// <inheritdoc cref="Controls.ContextMenu.MenuProperty"/>
    public static readonly StyledProperty<ContextMenu?> ContextMenuProperty =
        ContextMenu.MenuProperty.AddOwner<Control>();
    
    /// <summary>The control's surface brush (<c>AffectsRender</c>; <b>not</b> inherited — doc §12.1).</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<Control>();

    /// <summary>The control's text foreground — <see cref="TextElement.ForegroundProperty"/> <c>AddOwner</c> (inherits).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Control>();

    /// <summary>The control's border pen (<c>AffectsRender</c> + nullity escalation — doc §12.4).</summary>
    public static readonly StyledProperty<Pen?> BorderPenProperty =
        Border.BorderPenProperty.AddOwner<Control>();

    /// <summary>The control's inner padding (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<Margins> PaddingProperty =
        Border.PaddingProperty.AddOwner<Control>();

    /// <summary>The per-instance control-theme override (the explicit <c>Control.Theme</c>, doc §11.3/CD13).</summary>
    public static readonly StyledProperty<Style?> ThemeProperty =
        UIProperty.Register<Control, Style?>(nameof(Theme), changed: OnThemeChanged);

    static Control()
    {
        AffectsMeasure<Control>(TemplateProperty, PaddingProperty);
        AffectsRender<Control>(BackgroundProperty, BorderPenProperty);
        // Foreground inherits + AffectsRender via the TextElement owner registration (global lane).
    }

    /// <inheritdoc cref="TemplateProperty"/>
    public ControlTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    /// <inheritdoc cref="BackgroundProperty"/>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc cref="BorderPenProperty"/>
    public Pen? BorderPen
    {
        get => GetValue(BorderPenProperty);
        set => SetValue(BorderPenProperty, value);
    }

    /// <inheritdoc cref="PaddingProperty"/>
    public Margins Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <inheritdoc cref="ThemeProperty"/>
    public Style? Theme
    {
        get => GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    /// <inheritdoc cref="ThemeProperty"/>
    public ContextMenu? ContextMenu
    {
        get => GetValue(ContextMenuProperty);
        set => SetValue(ContextMenuProperty, value);
    }

    /// <summary>
    /// The S7 control-theme lookup key (design doc §12.1 / CD13): the runtime type, exact-key — no
    /// base-class probing. A subclass without its own theme overrides this to a base type's key (e.g.
    /// <c>typeof(Button)</c>) to inherit that theme.
    /// </summary>
    protected virtual object ControlThemeKey => GetType();

    /// <summary>The live template instance, or <see langword="null"/> before first expansion / after a null template.</summary>
    protected internal TemplateInstance? TemplateInstance => _templateInstance;

    /// <summary>
    /// If control has a ScrollViewer in its style and has a custom keyboard scrolling behavior, then
    /// HandlesScrolling should return true. Then ScrollViewer will not handle keyboard input and will
    /// leave it up to the control.
    /// </summary>
    protected internal virtual bool HandlesScrolling => false;

    // ───────────────────────────── IControlThemeHost (S7 seam) ─────────────────────────────

    UIElement IControlThemeHost.Element => this;
    object IControlThemeHost.ControlThemeKey => ControlThemeKey;
    Style? IControlThemeHost.ThemeOverride => Theme;

    // ───────────────────────────── control-theme subscription (CD13/CD15) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        // Watch the control-theme identity (the one-handle ThemeProperty + chain-node watch, CD13): a
        // chain shadowing / variant flip pulse re-resolves so the new theme's rules arm and the old
        // theme's frames retract. The first arm already resolved the theme (GatherMatches calls
        // ResolveControlTheme); this only re-matches on a later identity change.
        _controlThemeSubscription = ResourceServices.SubscribeControlTheme(this, new ControlThemeListener(this), out _);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        _controlThemeSubscription.Dispose();
        _controlThemeSubscription = default;
        base.OnDetachedFromTree(in e);
    }

    private static void OnThemeChanged(UIObject sender, Style? oldValue, Style? newValue)
    {
        // The explicit Control.Theme override changed: re-match so the new override (or, on clear, the
        // chain lookup) arms at ControlTheme(0) — the SD21 identity diff keeps shared survivors.
        if (sender is Control { IsAttachedToTree: true } control)
            UIApplication.Current?.StyleEngineInternal.OnControlThemeChanged(control);
    }

    // The control-theme change listener: a chain pulse (variant flip / shadowing) re-resolves through
    // the styling engine's element re-match (CD15 — same Style instance ⇒ no-op diff, no re-templating).
    private sealed class ControlThemeListener(Control control) : IResourceChangeListener
    {
        public void OnResourceChanged(object key, object? newValue)
        {
            if (control.IsAttachedToTree)
                UIApplication.Current?.StyleEngineInternal.OnControlThemeChanged(control);
        }
    }

    // ───────────────────────────── styling / chain-walk seams ─────────────────────────────

    /// <inheritdoc/>
    internal override Styles? TemplateStylesForArming
        => _templateInstance is { Template.Styles.Count: > 0 } instance ? instance.Template.Styles : null;

    /// <inheritdoc/>
    internal override ResourceDictionary? TemplateResourcesForChainWalk
        => _templateInstance?.Template.Resources;

    // ───────────────────────────── template application (doc §12.2, CD17) ─────────────────────────────

    /// <summary>
    /// Expands the template if stale (design doc §12.2 / CD17): called by <see cref="UIElement.Measure"/>
    /// at the head of measure, before <c>MeasureOverride</c>. Returns whether a (re-)expansion ran.
    /// </summary>
    public override bool ApplyTemplate()
    {
        if (_templateValid)
            return false;

        if (_applyingTemplate)
        {
            // Re-entrant Template set from inside OnApplyTemplate (C134): record dirty, defer to the
            // next measure behind the guard — never recurse.
            _reapplyRequested = true;
            return false;
        }

        _applyingTemplate = true;

        try
        {
            ExpandTemplate();
        }
        finally
        {
            _applyingTemplate = false;
        }

        _templateValid = true;

        if (_reapplyRequested)
        {
            _reapplyRequested = false;
            _templateValid = false;
            InvalidateMeasure();
        }

        return true;
    }

    private void ExpandTemplate()
    {
        // Reuse on same-template re-resolution (the transient detach/reattach case). A control-theme Style
        // setter retracts Template → null on a transient detach (e.g. switching away from a TabControl tab) and
        // re-arms it to the SAME ControlTemplate instance on reattach, which marks the template invalid and would
        // otherwise tear the instance down and rebuild it. Rebuilding discards the live, correctly-tracking
        // template bindings and wires fresh ones during the reattach MEASURE — before the templated parent's own
        // values have re-settled — so a `{TemplateBinding}` latches a stale value (e.g. the dark foreground that
        // survived a theme flip while the tab was detached). When the resolved Template is the very instance the
        // current expansion was built from, nothing changed: keep the retained instance. Its subtree re-attaches
        // with the control, so its bindings re-resolve through the normal attach walk (the same path that
        // correctly refreshes the control's own inherited values), and we also avoid rebuilding every control's
        // template on every tab switch.
        if (_templateInstance is { } reusable && ReferenceEquals(reusable.Template, Template))
            return;

        // ① Detach the old instance (unhook-before-rewire, doc §12.2 step 1).
        if (_templateInstance is {} old)
        {
            OnTemplateDetaching(old);
            old.Detach();
            var oldRoot = old.Root;
            RemoveVisualChild(oldRoot); // subtree detach retracts Template-layer frames / resource subs / When watchers
            // The discarded template subtree's TemplateBindings + auto-alias observers tear down here
            // (CD20: Detach() is store-owned retraction). The subtree is reused by no one — it is gone.
            oldRoot.TearDown();
            _templateInstance = null;
        }

        // ② Resolve the template (null ⇒ no child + one-time diagnostic, C135).
        if (Template is not {} template)
        {
            ControlDiagnostics.NoTemplate(this);
            return;
        }

        // ③ Instantiate: build + stamp TemplatedParent + set the template name scope.
        var instance = template.Instantiate(this);
        NameScope.SetTemplateNameScope(this, instance.NameScope);
        _templateInstance = instance;

        // ④ Validate [TemplatePart] immediately, before visual attach (CD19/C129/C130/C132).
        ValidateTemplateParts(instance);

        // ⑤ Attach Root as a VISUAL child only — parts are never logical children (doc §12.2 step 5).
        AddVisualChildOnly(instance.Root);

        // ⑥ OnApplyTemplate — re-entrant Template sets defer behind the guard.
        OnApplyTemplate();
    }

    /// <summary>Called after a template expands and attaches (doc §12.2 step 6). Wire part handlers here.</summary>
    protected virtual void OnApplyTemplate() {}

    /// <summary>
    /// Called before the old template detaches (doc §12.2 step 1, "unhook before rewire"): unhook any
    /// part event handlers / timers / command subscriptions wired in <see cref="OnApplyTemplate"/>.
    /// Runs <b>before</b> <c>old.Detach()</c> and the old root's removal.
    /// </summary>
    protected virtual void OnTemplateDetaching(TemplateInstance old) {}

    /// <summary>
    /// Resolves a template part by name in the template name scope only (design doc §12.1 / CD17).
    /// Returns null before the first expansion (call <see cref="ApplyTemplate"/> explicitly to force
    /// it earlier) and when the part is absent / of a different type.
    /// </summary>
    protected internal T? GetTemplatePart<T>(string name) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(name);
        return _templateInstance?.NameScope.Find(name) as T;
    }

    private void ValidateTemplateParts(TemplateInstance instance)
    {
        foreach (var part in TemplatePartCache.For(GetType()))
        {
            if (instance.NameScope.Find(part.Name) is not {} found)
            {
                if (part.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"The template for '{GetType().Name}' is missing the required part '{part.Name}' " +
                        $"(expected type '{part.Type.Name}', CD19).");
                }

                continue; // optional missing — degrade
            }

            if (!part.Type.IsInstanceOfType(found))
            {
                throw new InvalidOperationException(
                    $"The template for '{GetType().Name}' provides part '{part.Name}' as a " +
                    $"'{found.GetType().Name}', but type '{part.Type.Name}' was declared ([TemplatePart], CD19).");
            }
        }
    }

    private static void OnTemplateChanged(UIObject sender, ControlTemplate? oldValue, ControlTemplate? newValue)
    {
        if (sender is Control control)
            control._templateValid = false; // AffectsMeasure routes InvalidateMeasure → ApplyTemplate re-expands
    }
}