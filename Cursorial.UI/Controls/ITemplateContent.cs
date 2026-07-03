namespace Cursorial.UI.Controls;

/// <summary>
/// The runtime template-content seam (design doc §13 "ITemplateContent deferral", matrix CD-pin ⑤).
/// A <see cref="ControlTemplate"/>/<see cref="DataTemplate"/> holds one of these; expansion calls
/// <see cref="Build"/> to produce a fresh element subtree, registering any <c>x:Name</c>-equivalent
/// parts into the supplied <see cref="TemplateBuildContext.NameScope"/>.
/// </summary>
/// <remarks>
/// At P5 the only producer is the code-first <see cref="FuncTemplateContent"/>. Fork C's
/// typed-deferred XAML node lands at P6 and slots in here unchanged — XAML defers automatically when
/// a property is typed <see cref="ITemplateContent"/> (doc §12.1).
/// </remarks>
public interface ITemplateContent
{
    /// <summary>
    /// Builds a fresh element subtree under <paramref name="context"/>, returning its root. Called
    /// once per template instantiation; must produce a distinct subtree each call (no shared
    /// elements across instantiations — the template barrier's foreign-parent guard catches misuse).
    /// </summary>
    UIElement Build(TemplateBuildContext context);
}

/// <summary>
/// The build-time context handed to <see cref="ITemplateContent.Build"/> (design doc §12.1): the
/// owning control and the template's <see cref="NameScope"/> for <c>x:Name</c>-equivalent part
/// registration (<see cref="RegisterName"/>).
/// </summary>
public sealed class TemplateBuildContext
{
    private readonly INameScope _nameScope;

    internal TemplateBuildContext(UIElement? templatedParent, INameScope nameScope)
    {
        TemplatedParent = templatedParent;
        _nameScope = nameScope;
    }

    /// <summary>
    /// The control the template is being expanded for (the future <c>TemplatedParent</c> of every
    /// built element), or <see langword="null"/> for a <see cref="DataTemplate"/> (data content is
    /// app-styleable, never stamped — CD18).
    /// </summary>
    public UIElement? TemplatedParent { get; }

    /// <summary>The template's name scope — part names register here, sealed from the document scope (doc §12.2).</summary>
    public INameScope NameScope => _nameScope;

    /// <summary>
    /// Registers <paramref name="element"/> under <paramref name="name"/> in the template name scope —
    /// the runtime counterpart of an <c>x:Name</c> on a template part. Resolvable via
    /// <c>Control.GetTemplatePart&lt;T&gt;(name)</c> / <c>NameScopeExtensions.RequireControl&lt;T&gt;</c>.
    /// </summary>
    public void RegisterName(string name, UIElement element)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(element);
        element.Name = name;
        _nameScope.Register(name, element);
    }
}

/// <summary>
/// The code-first <see cref="ITemplateContent"/> (matrix CD-pin ⑤): a delegate
/// <c>Build(TemplateBuildContext) → UIElement</c>. The sole P5 template-content producer; Fork C's
/// XAML node joins it at P6.
/// </summary>
public sealed class FuncTemplateContent(Func<TemplateBuildContext, UIElement> build) : ITemplateContent
{
    private readonly Func<TemplateBuildContext, UIElement> _build =
        build ?? throw new ArgumentNullException(nameof(build));

    /// <inheritdoc/>
    public UIElement Build(TemplateBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _build(context) ?? throw new InvalidOperationException(
            "FuncTemplateContent.Build returned null; a template must produce a non-null root element.");
    }
}
