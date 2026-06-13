namespace Cursorial.UI.Xaml;

/// <summary>
/// Converts a string attribute/text value to a typed object. The terminal converters (geometry,
/// color, brush, enums, grid length, …) implement this in the loader assembly; the frontend only
/// needs the contract so it can decide whether a literal is foldable at parse time
/// (<see cref="IsContextFree"/>) and, when the loader supplies a provider, fold it.
/// </summary>
public interface ITypeConverter
{
    /// <summary>
    /// Converts <paramref name="text"/> to a value of the converter's target type. Throws
    /// <see cref="XamlParseException"/> (a <c>CUR2401</c>-class diagnostic) on a malformed value.
    /// </summary>
    object? ConvertFromString(string text, in XamlValueContext context);

    /// <summary>
    /// True when the conversion depends only on the input text (and culture) — never on services,
    /// the live tree, or relative-URI resolution. A context-free converter may run once at parse and
    /// its boxed result shared by every <c>Load</c> / template <c>Build</c> (the constant-folding rule,
    /// matrix XD3). A context-dependent converter stays <c>Text</c> and runs in stage 2.
    /// </summary>
    bool IsContextFree { get; }
}
