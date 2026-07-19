using Cursorial.Markup;

// The data-views controls live under the default Cursorial UI xmlns (https://cursorial.dev/ui) — unprefixed like
// every other Cursorial control (the Bars precedent). The suite isn't seeded into the loader by default (that
// would invert the Cursorial.UI.Xaml → Cursorial.UI.DataViews dependency); a consuming app makes it
// XAML-addressable with XamlSchemaContext.Default.RegisterAssembly(typeof(Cursorial.UI.DataViews.DataGrid)).
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.DataViews")]
