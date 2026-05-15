namespace Cursorial.Output;

/// <summary>
/// An OSC 8 hyperlink anchor — a URI plus an optional identifier that lets the terminal group
/// adjacent runs that point at the same target into one logical link. Used as a field of
/// <see cref="Style"/>; the renderer emits the OSC 8 open/close brackets around runs that
/// share the same hyperlink value.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Uri"/> being <see langword="null"/> (the <c>default</c>) is the "no hyperlink"
/// sentinel; <see cref="IsEmpty"/> reflects that. <see cref="Id"/> is optional — when supplied,
/// terminals that support hyperlinks treat adjacent runs with the same id as a single visual
/// link target (useful when the displayed text wraps or is interrupted by intermediate styling
/// changes). Per the OSC 8 spec, ids beyond 250 bytes are not guaranteed to be honored.
/// </para>
/// </remarks>
/// <param name="Uri">The target URI. Must be UTF-8 encodable; passing <see langword="null"/> disables the hyperlink for the bearing <see cref="Style"/>.</param>
/// <param name="Id">Optional group identifier. When two adjacent runs carry the same id, terminals collapse them into a single hyperlink target.</param>
public readonly record struct Hyperlink(string? Uri, string? Id = null)
{
    /// <summary>The "no hyperlink" value — the default of <see cref="Hyperlink"/> as a struct.</summary>
    public static Hyperlink None => default;

    /// <summary>True when this hyperlink carries no target — equivalent to <c>default(Hyperlink)</c> or <see cref="None"/>.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Uri);
}
