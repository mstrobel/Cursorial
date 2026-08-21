using System.Diagnostics.CodeAnalysis;

using Cursorial.Gallery.Infrastructure;

namespace Cursorial.Gallery.ViewModels;

/// <summary>A gallery page: a view-model whose concrete type selects its view through an implicit
/// <c>DataTemplate</c> (keyed on the type) in the shell's resources. <see cref="Title"/> is the nav-list label.</summary>
/// <remarks>The nav list's type-ahead (TextSearch.TextPath="Title") resolves Title by REFLECTION on
/// the runtime type; the DAM root keeps the base declaration reflection-visible under trim/AOT (the
/// override dispatches virtually through it) — without it, type-ahead silently dies in the published
/// Gallery.</remarks>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public abstract class PageViewModel : ViewModelBase
{
    // Same rationale as DataGridViewModel.OrderRow: TextSearch resolves Title through a dataflow-
    // opaque GetType().GetProperty — the type-level DAM never engages, so force preservation here
    // (the base declaration's PropertyInfo virtual-dispatches to every page's override).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(PageViewModel))]
    protected PageViewModel() {}

    /// <summary>The nav-list label (also the page heading).</summary>
    public abstract string Title { get; }

    /// <summary>A one-line description shown under the page heading.</summary>
    public abstract string Summary { get; }

    public virtual bool IsContentScrollable => true;

    public virtual string? Status
    {
        get => null;
        // ReSharper disable once ValueParameterNotUsed
        protected set {}
    }
}
