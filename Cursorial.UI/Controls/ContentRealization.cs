using System.Globalization;

namespace Cursorial.UI.Controls;

/// <summary>
/// The shared DataTemplate lookup + content realization chain (design doc §12.3 / CD22), reused by
/// <see cref="ContentPresenter"/> (and, at P9, <c>ItemsControl</c>). The chain:
/// ① explicit <c>ContentTemplate</c> → ② implicit <c>DataTemplateKey(t)</c> walk (runtime type then
/// each base up to but excluding <see cref="object"/>) → ③ <see cref="UIElement"/> passthrough →
/// ④ <see cref="AccessText"/> ⇒ <see cref="AccessTextPresenter"/> (extended to plain strings when
/// <c>RecognizesAccessKey</c>) → ⑤ fallback <see cref="TextBlock"/>(<c>Convert.ToString(content)</c>).
/// </summary>
internal static class ContentRealization
{
    /// <summary>
    /// Resolves the implicit <see cref="DataTemplate"/> for <paramref name="content"/> (chain ②): the
    /// runtime type then each base class via <c>DataTemplateKey</c> through the resource chain
    /// (the templated-parent hop is built into the chain walk). Returns null when nothing matches or
    /// the content is a string / <see cref="UIElement"/> / <see cref="AccessText"/> (those take chains
    /// ③/④/⑤). Interfaces are deferred (CD22).
    /// </summary>
    internal static DataTemplate? FindImplicitTemplate(UIElement scope, object? content)
    {
        if (content is null or string or UIElement or AccessText)
            return null;

        for (var type = content.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (scope.TryFindResource(new DataTemplateKey(type), out var resolved) && resolved is DataTemplate template)
                return template;
        }

        return null;
    }

    /// <summary>
    /// Realizes <paramref name="content"/> into a visual element via the chain (CD22). A resolved
    /// <paramref name="template"/> (explicit or implicit) wins (chains ①/②); otherwise the content
    /// type selects chains ③/④/⑤.
    /// </summary>
    /// <param name="host">The presenter (for the templated-parent logical adoption of element content).</param>
    /// <param name="content">The content object.</param>
    /// <param name="template">The resolved template, or null.</param>
    /// <param name="recognizesAccessKey">Whether a plain string is parsed for an access-key mnemonic (chain ④ extension).</param>
    internal static UIElement? Realize(ContentPresenter host, object? content, DataTemplate? template, bool recognizesAccessKey)
    {
        // ①/② a resolved template builds the data subtree (DataContext = content, TemplatedParent null).
        if (template is not null)
            return template.Build(content);

        switch (content)
        {
            case null:
                return null;

            // ③ a UIElement passes through: it is a logical child of the templated parent (so it
            // inherits the host's DataContext) and a visual child of the presenter. When the presenter
            // is a template part, the templated parent owns the logical adoption (the ContentControl);
            // a free-standing presenter adopts it itself.
            case UIElement element:
                AdoptElementContent(host, element);
                return element;

            // ④ an AccessText value always becomes an AccessTextPresenter, regardless of RecognizesAccessKey.
            case AccessText accessText:
                return new AccessTextPresenter(accessText);

            // ④ extension: a plain string under RecognizesAccessKey folds to an AccessText (doc §12.5).
            case string s when recognizesAccessKey:
                return new AccessTextPresenter(AccessText.Parse(s));

            // ⑤ fallback: any other content (incl. a plain string without RecognizesAccessKey) renders
            // as TextBlock(Convert.ToString(content)) with CurrentCulture (CD22).
            case string s:
                return new TextBlock(s);

            default:
                return new TextBlock(Convert.ToString(content, CultureInfo.CurrentCulture));
        }
    }

    private static void AdoptElementContent(ContentPresenter host, UIElement element)
    {
        // If the element already has a logical parent (the ContentControl adopted it on Content change),
        // leave it. Otherwise the presenter adopts it logically so DataContext inheritance flows.
        if (element.LogicalParent is not null)
            return;

        // A presenter inside a template: the templated parent (the ContentControl) is the logical owner.
        // It adopts the element on its own Content change; the presenter only hosts it visually here.
        if (host.TemplatedParent is ContentControl)
            return;

        // A free-standing presenter (no ContentControl host): adopt logically so inheritance works.
        host.AdoptElementContentLogically(element);
    }
}
