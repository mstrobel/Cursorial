using System.Globalization;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The <see cref="IServiceProvider"/> full-lowering hands a custom <see cref="MarkupExtension"/> at its
/// <see cref="MarkupExtension.ProvideValue"/> call — the AOT-clean twin of the loader's
/// <c>XamlServiceProvider</c>. It carries the four services a lowered document can supply from static
/// knowledge: the provide-value target (<see cref="IProvideValueTarget"/>), the document root
/// (<see cref="IRootObjectProvider"/>), and the enclosing name scope (<see cref="INameScopeProvider"/>).
///
/// <para><see cref="IAmbientResources"/> and <see cref="IXamlLineInfo"/> are absent: there is no lowered
/// ambient resource stack and no runtime author-position, so an extension that probes either gets
/// <see langword="null"/>. Under full-lowering there is NO loader to fall back to — the document is emitted as
/// straight-line C# — so an extension whose <see cref="MarkupExtension.ProvideValue"/> depends on ambient
/// resource resolution would diverge from the loader here. The emitter fences such an extension only when it
/// can prove the dependency; a custom extension that consumes <see cref="IAmbientResources"/> is a known
/// lowering gap (tracked separately) and should stay on the loader path (a non-lowered document).</para>
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

    /// <summary>
    /// Coerces a <see cref="MarkupExtension.ProvideValue"/> result to a target member type — the runtime
    /// counterpart of the loader's <c>AssignResolvedValue</c> (matrix X121): a <see langword="string"/> result
    /// destined for a typed (non-string/object) slot runs the converter ladder, everything else passes
    /// through. Full-lowering wraps a custom extension's result with this before assigning it to a typed target,
    /// so a string-returning extension behaves identically to the loader instead of an unchecked cast.
    /// </summary>
    public static object? Coerce(object? value, Type targetType)
    {
        if (value is string text && targetType != typeof(string) && targetType != typeof(object))
        {
            var converter = XamlConverters.For(targetType) ?? XamlConverters.BclConverterForType(targetType);
            if (converter is not null)
                return converter.ConvertFromString(text, new XamlValueContext(CultureInfo.InvariantCulture, null, targetType, null, 0, 0));
        }
        return value;
    }
}
