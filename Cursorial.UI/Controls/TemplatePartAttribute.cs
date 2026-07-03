namespace Cursorial.UI.Controls;

/// <summary>
/// Declares a named template part a <see cref="Control"/> expects from its
/// <see cref="ControlTemplate"/> (design doc §12.1). Names are <c>"PART_*"</c> by convention. Parts
/// are validated immediately after instantiation, before visual attach (doc §12.2 step 4, CD19):
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A declared part present but of the wrong type ⇒ <see cref="InvalidOperationException"/> (always).</item>
/// <item>An <see cref="IsRequired"/> part missing ⇒ <see cref="InvalidOperationException"/> (always).</item>
/// <item>An optional (default) part missing ⇒ <c>GetTemplatePart</c> returns null; the control degrades.</item>
/// </list>
/// Seal-time validation is impossible — <see cref="ITemplateContent"/> is opaque until <c>Build</c>.
/// The attribute is inherited and may be applied multiple times.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class TemplatePartAttribute(string name, Type type) : Attribute
{
    /// <summary>The part's name in the template scope.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The element type the part must be assignable to.</summary>
    public Type Type { get; } = type ?? throw new ArgumentNullException(nameof(type));

    /// <summary>Whether the part must be present (default <see langword="false"/> — optional, degrade gracefully).</summary>
    public bool IsRequired { get; init; }
}
