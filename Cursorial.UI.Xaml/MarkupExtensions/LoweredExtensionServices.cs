namespace Cursorial.UI.Xaml;

/// <summary>
/// The <see cref="IServiceProvider"/> full-lowering hands a custom <see cref="MarkupExtension"/> at its
/// <see cref="MarkupExtension.ProvideValue"/> call — the AOT-clean twin of the loader's
/// <c>XamlServiceProvider</c>. It carries the four services a lowered document can supply from static
/// knowledge: the provide-value target (<see cref="IProvideValueTarget"/>), the document root
/// (<see cref="IRootObjectProvider"/>), and the enclosing name scope (<see cref="INameScopeProvider"/>).
///
/// <para><see cref="IAmbientResources"/> is deliberately absent: there is no lowered ambient resource stack,
/// so an extension that probes it (via <c>GetService(typeof(IAmbientResources))</c>) gets <see langword="null"/>
/// — the same as an unrecognized service. <see cref="IXamlLineInfo"/> is absent for the same reason (no
/// author-position tracking at runtime). An extension that needs either stays on the loader path.</para>
///
/// <para><see cref="TargetProperty"/> mirrors the loader: a registered <c>UIProperty</c> for a styled target,
/// or the target member's runtime <see cref="Type"/> for a CLR member (where the loader passes a
/// <c>XamlMember</c> carrying that type). A standalone entry (a dictionary/collection item) passes
/// <see langword="null"/> for both target fields, matching the loader's standalone provide-value call.</para>
/// </summary>
public sealed class LoweredExtensionServices :
    IServiceProvider,
    IProvideValueTarget,
    IRootObjectProvider,
    INameScopeProvider
{
    /// <summary>Creates the service bundle for a lowered <see cref="MarkupExtension.ProvideValue"/> call.</summary>
    public LoweredExtensionServices(object? targetObject, object? targetProperty, object? rootObject, INameScope? nameScope)
    {
        TargetObject = targetObject;
        TargetProperty = targetProperty;
        RootObject = rootObject;
        NameScope = nameScope;
    }

    /// <inheritdoc/>
    public object? TargetObject { get; }

    /// <inheritdoc/>
    public object? TargetProperty { get; }

    /// <inheritdoc/>
    public object? RootObject { get; }

    /// <inheritdoc/>
    public INameScope? NameScope { get; }

    /// <inheritdoc/>
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IProvideValueTarget)) return this;
        if (serviceType == typeof(IRootObjectProvider)) return this;
        if (serviceType == typeof(INameScopeProvider)) return this;
        return null;
    }
}
