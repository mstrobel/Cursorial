namespace Cursorial.UI.Controls;

/// <summary>
/// A live expansion of a <see cref="ControlTemplate"/> for one control (design doc §12.1): the built
/// <see cref="Root"/>, the template <see cref="NameScope"/> (parts only), and the originating
/// <see cref="Template"/>. <see cref="Detach"/> is store-owned retraction — never set-back (CD20).
/// </summary>
public sealed class TemplateInstance
{
    private readonly Control _owner;
    private bool _detached;

    internal TemplateInstance(ControlTemplate template, Control owner, UIElement root, INameScope nameScope)
    {
        Template = template;
        _owner = owner;
        Root = root;
        NameScope = nameScope;
    }

    /// <summary>The template this is an instance of.</summary>
    public ControlTemplate Template { get; }

    /// <summary>The built subtree root (the control's single visual child while applied).</summary>
    public UIElement Root { get; }

    /// <summary>The template name scope — part names live here only (the barrier, doc §12.2 / CD18).</summary>
    public INameScope NameScope { get; }

    /// <summary>Whether <see cref="Detach"/> has run.</summary>
    public bool IsDetached => _detached;

    /// <summary>
    /// Tears down the instance (doc §12.2 / CD20): clears the templated parent's template name scope.
    /// The store-owned style-frame / DynamicResource / auto-alias-observer retractions ride the
    /// <c>Root</c> subtree's ordinary visual detach (which the control runs immediately after this
    /// call) — that bottom-up walk is the retraction trigger for every Template-layer frame, resource
    /// subscription, and <c>When</c> watcher (CD20, doc §11.6/Fork B). Idempotent.
    /// </summary>
    public void Detach()
    {
        if (_detached)
            return;

        _detached = true;
        Cursorial.UI.NameScope.SetTemplateNameScope(_owner, null);
    }
}
