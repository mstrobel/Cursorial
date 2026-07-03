using Cursorial.Markup;

// The CLR namespace this assembly exposes under the default Cursorial UI xmlns (https://cursorial.dev/ui): only the
// dedicated markup-extension namespace (e.g. {Icon …}), so the loader's own internals (XamlLoader, the schema
// context, …) stay un-addressable from XAML. Seeded into XamlSchemaContext's default map.
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Xaml.Markup")]
