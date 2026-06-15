// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A value that may be explicitly set or deliberately unset (design doc §9.3) — distinct from
/// <c>null</c>/<c>default</c> so an animation track can tell "no <c>From</c> given ⇒ snapshot the
/// property at track start" from "<c>From</c> set to the default value".
/// </summary>
/// <typeparam name="T">The wrapped value type.</typeparam>
public readonly struct Optional<T>
{
    /// <summary>Whether a value was explicitly set.</summary>
    public bool HasValue { get; }

    /// <summary>The set value (meaningful only when <see cref="HasValue"/>).</summary>
    public T Value { get; }

    /// <summary>Wraps an explicit value.</summary>
    public Optional(T value)
    {
        Value = value;
        HasValue = true;
    }

    /// <summary>The unset value (<see cref="HasValue"/> false).</summary>
    public static Optional<T> Unset => default;

    /// <summary>Any value implicitly becomes a set <see cref="Optional{T}"/>.</summary>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <summary>Returns <see cref="Value"/> when set, else <paramref name="fallback"/>.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? Value : fallback;

    /// <inheritdoc/>
    public override string ToString() => HasValue ? $"Optional({Value})" : "Optional.Unset";
}
