using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Cursorial.Media;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The named-color lookup (matrix XD13): a name resolves to the matching <c>Cursorial.Media.Colors</c>
/// entry — the <b>ANSI palette</b> color, not a web RGB value. Built once by reflecting the public
/// static <c>Color</c> fields/properties of <c>Colors</c>; case-insensitive.
/// </summary>
internal static class NamedColors
{
    [RequiresUnreferencedCode("Reflects the public static Color members of Cursorial.Media.Colors.")]
    private static Dictionary<string, Color> Build()
    {
        var table = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        var type = typeof(Colors);

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(Color) && field.GetValue(null) is Color c)
                table[field.Name] = c;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (prop.PropertyType == typeof(Color) && prop.GetValue(null) is Color c)
                table[prop.Name] = c;
        }

        return table;
    }

    // Lazily built and thread-safe: BrushConverter.IsContextFree == true, so the converter may run on
    // multiple threads during concurrent parses (P6 review P2-12).
    private static readonly Lazy<Dictionary<string, Color>> Table = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    // The field initializer above compiles INTO this cctor — which is where ILC attributes Build's RUC
    // call, so a suppression on the field never covered it. What makes the suppression TRUE is the
    // ILLink.Descriptors.xml embedded in Cursorial.Core (an embedded descriptor roots only its OWN
    // assembly's members): it preserves Colors wholesale, so the table stays complete under trimming.
    // (An attribute-based DynamicDependency would say the same thing, but naming the
    // DynamicallyAccessedMemberTypes enum here collides with the frontend's netstandard2.0 polyfill of
    // it, which InternalsVisibleTo puts in scope — CS0433.)
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The ILLink.Descriptors.xml embedded in Cursorial.Core preserves every Colors member Build reflects — the table is complete under trimming.")]
    static NamedColors() { }

    public static bool TryGet(string name, out Color color)
        => Table.Value.TryGetValue(name, out color);
}

/// <summary>
/// The named-brush lookup (matrix XD13 / allocation discipline): a plain color resolves to the cached
/// <c>Brushes.*</c> singleton when one carries the same color, else a fresh <see cref="SolidColorBrush"/>.
/// </summary>
internal static class NamedBrushes
{
    private static readonly ConcurrentDictionary<Color, IBrush> ByColor = new();

    [RequiresUnreferencedCode("Reflects the public static SolidColorBrush members of Cursorial.Drawing.Media.Brushes.")]
    private static Dictionary<Color, IBrush> Build()
    {
        var table = new Dictionary<Color, IBrush>();
        var type = typeof(Brushes);

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is SolidColorBrush brush)
                table[brush.Color] = brush;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (prop.PropertyType == typeof(SolidColorBrush) && prop.GetValue(null) is SolidColorBrush brush)
                table[brush.Color] = brush;
        }

        return table;
    }

    // Lazily built and thread-safe (the converter is context-free — see NamedColors, P6 review P2-12).
    private static readonly Lazy<Dictionary<Color, IBrush>> Singletons = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    // Same cctor-scope story as NamedColors: the ILLink.Descriptors.xml embedded in Cursorial.Rendering
    // preserves the Brushes statics Build reflects, so the singleton-brush table stays complete under
    // trimming.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The ILLink.Descriptors.xml embedded in Cursorial.Rendering preserves every Brushes member Build reflects — the table is complete under trimming.")]
    static NamedBrushes() { }

    public static IBrush ForOrCreate(Color color)
    {
        if (Singletons.Value.TryGetValue(color, out var singleton))
            return singleton;
        return ByColor.GetOrAdd(color, static c => new SolidColorBrush(c));
    }
}
