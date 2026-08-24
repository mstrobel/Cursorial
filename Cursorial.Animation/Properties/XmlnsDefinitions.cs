using Cursorial.Markup;

// The CLR namespace this assembly exposes under the default Cursorial UI xmlns (https://cursorial.dev/ui),
// discovered by both XAML metadata providers (the reflection loader's XamlSchemaContext and the build-time
// symbol resolver). Lets markup reach the animation primitives unprefixed — the easing catalog above all
// ({x:Static Easings.QuadInOut} as the escape hatch beside the Easing="QuadInOut" converter form).
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.Animation")]
