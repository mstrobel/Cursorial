using Cursorial.Markup;

// Cursorial.Drawing.Media (brushes / Colors / Brushes / pens) is exposed under the default Cursorial UI xmlns so
// {x:Static Colors.Red}, the color mini-language, and <SolidColorBrush/> resolve unprefixed in a UI document.
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.Rendering.Media")]