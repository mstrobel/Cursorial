using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// GAP-B — a compiled deferred-install binding (a <c>RelativeSource</c>/<c>FindAncestor</c> anchor, resolved at
/// runtime against the ancestor chain — no name target needed) placed directly on an INLINE entry of a standalone
/// <c>&lt;ResourceDictionary&gt;</c> was recorded into <c>DeferredScopeLines</c> but NEVER flushed by
/// <c>EmitResourceDictionaryBuilder</c> (which, unlike <c>EmitCodeBehind</c>, omitted the end-of-tree flush tail).
/// The generator silently dropped the binding while the reflective loader installs it — a parity break + silent
/// miscompile. The fix flushes the deferred installs at the end of the builder.
/// </summary>
public class ResourceDictionaryDeferredBindingTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static ResourceDictionary InvokeBuilder(Assembly assembly, string method)
        => (ResourceDictionary)assembly
            .GetType("Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders")!
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, null)!;

    [Fact] // A RelativeSource AncestorType binding on an inline standalone-RD entry must EMIT its install (was dropped).
    public void Lowered_ResourceDictionary_RelativeSourceBinding_OnInlineEntry_IsInstalled()
    {
        var xaml =
            $"<ResourceDictionary {Ns}>" +
            "<Button x:Key=\"Shared\" Width=\"{Binding Spacing, RelativeSource={RelativeSource FindAncestor, AncestorType=StackPanel}}\"/>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("RdDeferHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // The deferred install must be flushed into the builder body — previously recorded then dropped.
        Assert.Contains("BindingOperations.Install(", lowered);
        Assert.Contains("WidthProperty", lowered);

        // And it compiles + the built Button actually carries the binding (parity with the reflective loader).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = InvokeBuilder(assembly, "BuildMyView");
        var button = Assert.IsType<Button>(dict["Shared"]);
        Assert.NotNull(BindingOperations.GetBindingExpression(button, UIElement.WidthProperty)); // the binding stuck
    }
}
