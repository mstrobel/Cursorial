namespace Cursorial.UI.Controls;

/// <summary>
/// A data template (design doc §12.1/§12.3): builds a visual subtree for a data item via
/// <see cref="ITemplateContent"/>. Keyed implicitly by <see cref="DataType"/> in resource
/// dictionaries (the <c>DataTemplateKey(DataType)</c> implicit-template walk, CD22).
/// </summary>
/// <remarks>
/// <para>
/// <b>The barrier (CD18, a deliberate WPF deviation).</b> A <see cref="Build"/>-produced subtree gets
/// <c>TemplatedParent == null</c> — data-template content is <em>app content</em> and must be
/// app-styleable (the hybrid styling model has no <c>DataTemplate.Triggers</c>). A fresh document
/// name scope is attached to the root so <c>ElementName</c> bindings inside resolve template-locally;
/// <c>DataContext</c> is set to the data on the root.
/// </para>
/// </remarks>
public class DataTemplate
{
    /// <summary>The data type this template is keyed for (the implicit-template key, CD22).</summary>
    public Type? DataType { get; set; }

    /// <summary>The deferred content producing the visual subtree.</summary>
    public ITemplateContent? Content { get; set; }

    /// <summary>
    /// Builds a fresh subtree for <paramref name="data"/> (design doc §12.1/CD22): a fresh document
    /// name scope is attached via <see cref="NameScope.SetNameScope"/>; <c>DataContext = data</c> on
    /// the root; <c>TemplatedParent</c> stays null (data content is app-styleable, CD18).
    /// </summary>
    public UIElement Build(object? data)
    {
        if (Content is null)
            throw new InvalidOperationException("This DataTemplate has no Content to build.");

        var scope = new NameScopeDictionary();
        var context = new TemplateBuildContext(templatedParent: null, scope);
        var root = Content.Build(context)
            ?? throw new InvalidOperationException("The DataTemplate's ITemplateContent produced a null root element.");

        Cursorial.UI.NameScope.SetNameScope(root, scope);
        root.DataContext = data;
        return root;
    }
}
