using Cursorial.Markup;

// The bar controls live under the default Cursorial UI xmlns (https://cursorial.dev/ui) — unprefixed like every
// other Cursorial control, consistent with Cursorial.Drawing.Media. The suite isn't seeded by default (that would
// invert the Cursorial.UI.Xaml → Cursorial.UI.Bars dependency); a consuming app makes the bars XAML-addressable
// with XamlSchemaContext.Default.RegisterAssembly(typeof(Cursorial.UI.Bars.Toolbar)).
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Bars")]
