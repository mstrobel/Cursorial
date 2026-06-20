namespace Cursorial.UI.Controls;

/// <summary>
/// The panel template for an <see cref="ItemsControl"/> (WPF parity): a thin wrapper over an
/// <see cref="ITemplateContent"/> that builds the control's items <see cref="Panel"/>.
/// <see cref="ItemsControl.ItemsPanel"/> is typed to this so it can be authored in XAML, mirroring how
/// <see cref="ControlTemplate"/> wraps its deferred <see cref="ControlTemplate.Content"/>:
/// <code>
/// &lt;ListBox.ItemsPanel&gt;
///   &lt;ItemsPanelTemplate&gt;
///     &lt;VirtualizingStackPanel/&gt;
///   &lt;/ItemsPanelTemplate&gt;
/// &lt;/ListBox.ItemsPanel&gt;
/// </code>
/// The loader instantiates the <see cref="ItemsPanelTemplate"/> eagerly (its property type is the concrete
/// template, not <see cref="ITemplateContent"/>) and defers the inner panel into <see cref="Content"/> (that
/// member <em>is</em> typed <see cref="ITemplateContent"/>) — no special loader handling beyond the
/// <c>ContentProperty</c> registration.
/// </summary>
public sealed class ItemsPanelTemplate
{
    /// <summary>Creates an empty template; assign <see cref="Content"/> before building.</summary>
    public ItemsPanelTemplate()
    {
    }

    /// <summary>Creates a template over <paramref name="content"/> (the panel-producing deferred content).</summary>
    public ItemsPanelTemplate(ITemplateContent content)
        => Content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>Creates a code-first template over a build delegate (the panel factory) — the
    /// <c>new ItemsPanelTemplate(_ =&gt; new StackPanel())</c> form.</summary>
    public ItemsPanelTemplate(Func<TemplateBuildContext, UIElement> build)
        : this(new FuncTemplateContent(build))
    {
    }

    /// <summary>The deferred content producing the items panel. XAML defers here because the member is typed
    /// <see cref="ITemplateContent"/> (the same mechanism as <see cref="ControlTemplate.Content"/>).</summary>
    public ITemplateContent? Content { get; set; }

    /// <summary>Builds a fresh items <see cref="Panel"/> for the control; the built root must be a
    /// <see cref="Panel"/> (the items host requires one).</summary>
    public Panel Build(TemplateBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Content is null)
            throw new InvalidOperationException("This ItemsPanelTemplate has no Content to build.");

        UIElement? root;
            
        using (TemplateInstantiationScope.Enter())
            root = Content.Build(context);

        root.SetTemplatedParent(context.TemplatedParent);

        return root as Panel
               ?? throw new InvalidOperationException(
                   $"ItemsControl.ItemsPanel must build a Panel; got {root?.GetType().Name ?? "null"}.");
    }
}
