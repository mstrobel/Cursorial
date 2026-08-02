using Cursorial.Gallery.Infrastructure;

namespace Cursorial.Gallery.ViewModels;

/// <summary>A gallery page: a view-model whose concrete type selects its view through an implicit
/// <c>DataTemplate</c> (keyed on the type) in the shell's resources. <see cref="Title"/> is the nav-list label.</summary>
public abstract class PageViewModel : ViewModelBase
{
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
