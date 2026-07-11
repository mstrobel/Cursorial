using Cursorial.Markup;

// The dialog controls and theme keys live under the default Cursorial UI xmlns (https://cursorial.dev/ui) —
// unprefixed like every other Cursorial control, consistent with Cursorial.UI.Bars. The suite isn't seeded by
// default; a consuming app makes the dialogs XAML-addressable with
// XamlSchemaContext.Default.RegisterAssembly(typeof(Cursorial.UI.Dialogs.MessageBox)).
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Dialogs")]
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Dialogs.Themes")]
