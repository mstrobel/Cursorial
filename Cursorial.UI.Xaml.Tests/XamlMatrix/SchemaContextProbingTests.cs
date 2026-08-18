using System.Reflection;
using System.Reflection.Emit;

using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml;

/// <summary>
/// The design-time assembly-resolution seams on <see cref="XamlSchemaContext"/>: an
/// <c>assembly=</c> simple name that is neither registered nor in the host's load context consults
/// <see cref="XamlSchemaContext.AssemblyResolver"/> (the Rider-previewer hook — its own load context,
/// its own identity rules), then the <see cref="XamlSchemaContext.AddProbePath"/> directories, before
/// the <c>Assembly.Load</c> fallback. A hit is registered so later lookups take the fast path.
/// </summary>
public sealed class SchemaContextProbingTests
{
    [Fact]
    public void AssemblyResolver_ConsultedOnce_ThenRegistered()
    {
        var context = new XamlSchemaContext();
        var calls = 0;
        context.AssemblyResolver = name =>
        {
            calls++;
            return name == "phantom-assembly" ? typeof(SchemaContextProbingTests).Assembly : null;
        };

        const string ns = "clr-namespace:Cursorial.Tests.UI.Xaml;assembly=phantom-assembly";
        var first = context.Resolve(ns, nameof(SchemaContextProbingTests), out _);
        var second = context.Resolve(ns, nameof(SchemaContextProbingTests), out _);

        Assert.Equal(typeof(SchemaContextProbingTests), first);
        Assert.Equal(typeof(SchemaContextProbingTests), second);
        Assert.Equal(1, calls); // the hit was registered — the second lookup matched by name, no re-resolve
    }

    [Fact]
    public void AssemblyResolver_Faulting_DeclinesGracefully()
    {
        var context = new XamlSchemaContext();
        context.AssemblyResolver = _ => throw new InvalidOperationException("designer host hiccup");

        var resolved = context.Resolve("clr-namespace:No.Such.Space;assembly=no-such-assembly", "Nope", out _);

        Assert.Null(resolved); // fell through the ladder without surfacing the resolver's exception
    }

    [Fact]
    public void AddProbePath_LoadsAssemblyFromDirectory()
    {
        // Emit a real single-type assembly to a temp directory — the probe target. PersistedAssemblyBuilder
        // gives a deterministic on-disk fixture with no dependencies beyond corelib.
        var dir = Directory.CreateTempSubdirectory("curio-probe-").FullName;
        try
        {
            var name = new AssemblyName("curio-probe-fixture");
            var builder = new PersistedAssemblyBuilder(name, typeof(object).Assembly);
            var module = builder.DefineDynamicModule("main");
            module.DefineType("ProbeSpace.Widget", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            var path = Path.Combine(dir, "curio-probe-fixture.dll");
            builder.Save(path);

            var context = new XamlSchemaContext();
            context.AddProbePath(dir);

            var resolved = context.Resolve("clr-namespace:ProbeSpace;assembly=curio-probe-fixture", "Widget", out _);

            Assert.NotNull(resolved);
            Assert.Equal("ProbeSpace.Widget", resolved!.FullName);
            Assert.Equal(path, resolved.Assembly.Location); // loaded from the probe directory, not the app base
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* file may be pinned by the loaded assembly on some OSes */ }
        }
    }

    [Fact]
    public void AddProbePath_RejectsEmpty()
    {
        var context = new XamlSchemaContext();
        Assert.ThrowsAny<ArgumentException>(() => context.AddProbePath(""));
    }
}
