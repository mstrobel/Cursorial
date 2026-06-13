// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The style-channel layer — the top field of the packed <see cref="StyleSortKey"/> (design doc
/// §3.4/§3.5). <b>Layer beats specificity</b> (a documented deliberate divergence from CSS/WPF/
/// Avalonia): any rule in a higher layer outranks any rule in a lower one, regardless of selector
/// specificity. <see cref="Scoped"/> rules additionally order by scope depth (nearer scope wins).
/// </summary>
public enum StyleLayer : byte
{
    /// <summary>Type-keyed control themes from resource dictionaries (P5).</summary>
    ControlTheme = 0,

    /// <summary>Template-scoped styles armed by <c>ControlTemplate.Instantiate</c> (P5).</summary>
    Template = 1,

    /// <summary>The application theme dictionary's <c>Styles</c> slot (P5).</summary>
    Theme = 2,

    /// <summary>The application-level <c>UIApplication.Styles</c> collection.</summary>
    App = 3,

    /// <summary>Element-scoped <c>UIElement.Styles</c> collections — deeper scopes win via <c>scopeDepth</c>.</summary>
    Scoped = 4,

    /// <summary>The explicit <c>UIElement.Style</c> attachment — element-addressed, strongest layer.</summary>
    Explicit = 5
}
