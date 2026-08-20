using System.Collections.Generic;

using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Runtime-lane x:Static indexers (the X174 parity half of the shared path parser): the reflective
/// <c>ReflectionXamlMetadata.TryResolveStatic</c> walks the same bracket-aware segmentation and the
/// same int/enum/string key ladder the generator bakes, so a document that resolves under the
/// generated provider resolves identically under reflection — and vice versa.
/// </summary>
public enum FixtureKind { First, Second }

public sealed class FixtureKindMap
{
    public string this[FixtureKind kind] => $"kind:{kind}";
}

public static class StaticIndexHost
{
    public static List<string> Items { get; } = ["zero", "one"];

    public static Dictionary<string, string> Map { get; } = new() { ["apple"] = "red" };

    public static FixtureKindMap Kinds { get; } = new();
}

public class StaticIndexerLoadTests : LoaderTestBase
{
    [Fact] // int key → Item[int], walked on the running value after the static member
    public void XStatic_IntIndexer_ResolvesThroughReflection()
    {
        var button = Load<Button>("<Button Content=\"{x:Static StaticIndexHost.Items[1]}\"/>");
        Assert.Equal("one", button.Content);
    }

    [Fact] // unquoted non-int key → Item[string]
    public void XStatic_StringIndexer_ResolvesThroughReflection()
    {
        var button = Load<Button>("<Button Content=\"{x:Static StaticIndexHost.Map[apple]}\"/>");
        Assert.Equal("red", button.Content);
    }

    [Fact] // enum key → Item[TEnum], last dot-segment, case-insensitive — the shared ladder's rule (2)
    public void XStatic_EnumIndexer_ResolvesThroughReflection()
    {
        var button = Load<Button>("<Button Content=\"{x:Static StaticIndexHost.Kinds[second]}\"/>");
        Assert.Equal("kind:Second", button.Content);
    }

    [Fact] // an indexer segment chains into instance members exactly like a member hop
    public void XStatic_IndexerThenMember_Chains()
    {
        var button = Load<Button>("<Button Width=\"{x:Static StaticIndexHost.Items[0].Length}\"/>");
        Assert.Equal(4, button.Width); // "zero".Length
    }

    [Fact] // a key the collection does not hold degrades to member-not-found — CUR2102, not an unwind
    public void XStatic_MissingKey_IsMemberNotFound()
    {
        var ex = Assert.Throws<XamlParseException>(
            () => Load<Button>("<Button Content=\"{x:Static StaticIndexHost.Map[pear]}\"/>"));
        Assert.Contains("2102", ex.Message);
    }
}
