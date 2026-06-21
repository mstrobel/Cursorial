namespace Cursorial.UI.Controls;

/// <summary>
/// A control's visual template (design doc §12.1): an <see cref="ITemplateContent"/> that
/// <see cref="Instantiate"/> expands into a fresh subtree stamped with the owning control as
/// <c>TemplatedParent</c>. Carries Template-layer <see cref="Styles"/> (armed at
/// <see cref="StyleLayer.Template"/>) and a sealed <see cref="Resources"/> dictionary (the
/// chain-walk template-root hop, doc §11.4 / CD11).
/// </summary>
/// <remarks>
/// A template must be <see cref="Seal"/>ed before instantiation/arming (CD17/C125): seal deep-freezes
/// <see cref="Resources"/> (legalizing the sealed-exempt shared-slot rule, CD5) and the
/// <see cref="Styles"/> collection. A sealed template is freely shared across controls.
/// </remarks>
[Cursorial.Markup.ContentProperty("Content")]
public sealed class ControlTemplate
{
    private ITemplateContent? _content;
    private bool _sealed;

    /// <summary>Creates an empty template; assign <see cref="Content"/> before instantiating.</summary>
    public ControlTemplate()
    {
    }

    /// <summary>Creates a template over <paramref name="content"/>.</summary>
    public ControlTemplate(ITemplateContent content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    /// <summary>Creates a code-first template over a <see cref="FuncTemplateContent"/> build delegate.</summary>
    public ControlTemplate(Func<TemplateBuildContext, UIElement> build)
        : this(new FuncTemplateContent(build))
    {
    }

    /// <summary>
    /// The control type this template targets. When set, <see cref="Instantiate"/> throws if the
    /// owning control is not assignable to it (CD19/C124). <see langword="null"/> ⇒ no check.
    /// </summary>
    public Type? TargetType { get; set; }

    /// <summary>The deferred content producing the visual subtree (the one expansion source — CD-pin ⑤).</summary>
    public ITemplateContent? Content
    {
        get => _content;
        set
        {
            ThrowIfSealed();
            _content = value;
        }
    }

    /// <summary>The Template-layer styles armed on every instantiated part (CD30, layer <see cref="StyleLayer.Template"/>).</summary>
    public Styles Styles { get; } = new();

    /// <summary>The template's keyed resources — the chain-walk template-root hop (doc §11.4, CD11); sealed in <see cref="Seal"/>.</summary>
    public ResourceDictionary Resources { get; } = new();

    /// <summary>Whether the template has been sealed (deep-frozen and instantiable).</summary>
    public bool IsSealed => _sealed;

    /// <summary>
    /// Deep-freezes the template: seals <see cref="Resources"/> (CD4) and the <see cref="Styles"/>
    /// members. Idempotent. A sealed template is shareable across controls (CD5).
    /// </summary>
    public void Seal()
    {
        if (_sealed)
            return;

        if (_content is null)
            throw new InvalidOperationException("A ControlTemplate cannot be sealed without Content.");

        Resources.Seal();
        Styles.Freeze(); // seals every member AND blocks post-seal mutation (a shared sealed template is immutable)

        _sealed = true;
    }

    /// <summary>
    /// Builds a fresh <see cref="TemplateInstance"/> for <paramref name="owner"/> (the ONLY entry
    /// point, doc §12.1): seals if needed → builds the subtree under a fresh template name scope →
    /// stamps <c>TemplatedParent = owner</c> on every null-stamped element (a foreign non-null
    /// <c>TemplatedParent</c> throws — shared-subtree misuse, CD17/C123). <see cref="TargetType"/> is
    /// validated first (C124).
    /// </summary>
    public TemplateInstance Instantiate(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (_content is null)
            throw new InvalidOperationException("This ControlTemplate has no Content to instantiate.");

        if (TargetType is { } target && !target.IsInstanceOfType(owner))
        {
            throw new InvalidOperationException(
                $"ControlTemplate.TargetType is '{target.Name}' but it is being applied to a " +
                $"'{owner.GetType().Name}', which is not assignable to it (CD19).");
        }

        // Sealing on first instantiate keeps Resources frozen + shareable (CD5) and arms the styles.
        Seal();

        var scope = new NameScopeDictionary();
        var context = new TemplateBuildContext(owner, scope);

        // §20/PD24: while the scope is open, every value the template authors on its parts — a literal
        // SetValue, a {TemplateBinding}/{Binding}, a SetResourceReference — routes to the Template lane
        // (one rung below Style), so a page/theme Style overrides a template default. Covers the nested
        // FuncTemplateContent / XAML ITemplateContent builds. TemplatedParent stamping stays outside.
        UIElement? root;
        using (TemplateInstantiationScope.Enter())
            root = _content.Build(context);

        if (root is null)
        {
            throw new InvalidOperationException(
                $"The ITemplateContent for '{owner.GetType().Name}' produced a null root element.");
        }

        StampTemplatedParent(root, owner);
        return new TemplateInstance(this, owner, root, scope);
    }

    // The TemplatedParent stamp (doc §12.2 step 3): pre-order over the built subtree; a null stamp
    // becomes the owner, a stamp already equal to the owner is a no-op (nested controls' parts stamp
    // themselves at their own expansion and are exempt), and any other non-null stamp throws.
    private static void StampTemplatedParent(UIElement element, Control owner)
    {
        if (element.TemplatedParent is { } existing)
        {
            if (ReferenceEquals(existing, owner))
                return; // already this owner's stamp — re-instantiation / idempotent

            // A foreign stamp: a nested control's OWN parts are exempt — they hang under that nested
            // control (their TemplatedParent), never under us, and the build never re-roots them here.
            // Reaching one means the build aliased a foreign subtree into ours (shared-subtree misuse).
            throw new InvalidOperationException(
                $"A template element ('{element.GetType().Name}') already has a foreign TemplatedParent " +
                $"('{existing.GetType().Name}') — a ControlTemplate must build a fresh subtree per instantiation, " +
                "never alias elements across instances or controls (CD17).");
        }

        element.SetTemplatedParent(owner);

        foreach (var child in element.VisualChildrenForTemplateStamp)
            StampTemplatedParent(child, owner);
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
            throw new InvalidOperationException("This ControlTemplate is sealed and can no longer be mutated.");
    }
}
