// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The write surface for a structurally read-only styled property (matrix PD14, WPF
/// <c>DependencyPropertyKey</c> spirit): <c>RegisterReadOnly</c> returns the key, which the declaring
/// type keeps private; the companion <see cref="Property"/> is exposed as the public
/// <c>static readonly</c> field and only reads. Key writes (<c>SetValue(key, value)</c>) land in the
/// <see cref="BindingPriority.LocalValue"/> lane; keyless writes, bindings, frames, and animations
/// targeting the property throw (enforcement lives in the store).
/// </summary>
public sealed class UIPropertyKey<T>
{
    internal UIPropertyKey(StyledProperty<T> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property;
    }

    /// <summary>The read-only styled property this key writes.</summary>
    public StyledProperty<T> Property { get; }
}
