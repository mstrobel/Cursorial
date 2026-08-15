using Cursorial.Markup;

// The CLR namespaces this assembly exposes under the default Cursorial UI xmlns (https://cursorial.dev/ui).
// Discovered by both XAML metadata providers (the reflection loader's XamlSchemaContext and the build-time symbol
// resolver), so the xmlns→CLR map is declared here on the assembly rather than hand-maintained in a central list.
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Controls")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Data")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Input")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Media")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Themes")]
