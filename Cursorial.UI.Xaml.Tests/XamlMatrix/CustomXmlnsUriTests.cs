using Cursorial.Markup;
using Cursorial.UI.Xaml;

// #2 — a library or app can declare its OWN xmlns URI via [assembly: XmlnsDefinition("uri", "clrNs")] and use it as
// a prefix, not only the default https://cursorial.dev/ui or the clr-namespace:/using: forms. This exercises the
// URI-keyed namespace map in XamlSchemaContext (the loader-side generalization; the generator's XamlSymbolResolver
// carries the symbol-backed twin).
[assembly: XmlnsDefinition(Cursorial.Tests.UI.Xaml.XamlMatrix.CustomXmlnsUriTests.CustomUri, "Cursorial.Tests.UI.Xaml.CustomWidgets")]

namespace Cursorial.Tests.UI.Xaml.CustomWidgets
{
    /// <summary>A fixture control reachable only under the custom xmlns declared above.</summary>
    public sealed class CustomWidget;
}

namespace Cursorial.Tests.UI.Xaml.XamlMatrix
{
    public sealed class CustomXmlnsUriTests
    {
        public const string CustomUri = "https://cursorial.test/widgets";

        [Fact] // A non-default URI declared via [XmlnsDefinition] resolves once its declaring assembly is registered.
        public void CustomUri_ResolvesTypesAfterAssemblyRegistered()
        {
            var context = new XamlSchemaContext();

            // The custom URI isn't seeded (only UI/Drawing/UI.Xaml are) — a fresh context can't resolve it yet.
            Assert.Null(context.Resolve(CustomUri, "CustomWidget", out _));
            Assert.Empty(context.GetClrNamespaces(CustomUri));

            // Registering the declaring assembly discovers ALL its [XmlnsDefinition] URIs (not just the default one).
            context.RegisterAssembly(typeof(CustomWidgets.CustomWidget));

            var type = context.Resolve(CustomUri, "CustomWidget", out var ambiguous);
            Assert.Empty(ambiguous);
            Assert.NotNull(type);
            Assert.Equal("Cursorial.Tests.UI.Xaml.CustomWidgets.CustomWidget", type!.FullName);

            // …and the URI's namespaces enumerate for the did-you-mean path.
            Assert.Contains("Cursorial.Tests.UI.Xaml.CustomWidgets", context.GetClrNamespaces(CustomUri));
        }

        [Fact] // The default URI is unaffected by the custom one — both are just keys in the same map.
        public void DefaultUri_StillResolves_AlongsideCustom()
        {
            var context = new XamlSchemaContext();
            context.RegisterAssembly(typeof(CustomWidgets.CustomWidget));

            Assert.Equal("Cursorial.UI.Controls.Button",
                context.Resolve("https://cursorial.dev/ui", "Button", out _)?.FullName);
            Assert.Equal("Cursorial.Tests.UI.Xaml.CustomWidgets.CustomWidget",
                context.Resolve(CustomUri, "CustomWidget", out _)?.FullName);
        }
    }
}
