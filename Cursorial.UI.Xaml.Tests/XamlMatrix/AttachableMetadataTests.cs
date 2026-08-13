using System;
using System.Linq;

using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

using Xunit;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The optional <see cref="IXamlAttachablePropertyProvider"/> seam (task #28): the runtime reflection provider
/// surfaces the attached properties declarable on a target type, qualified <c>Owner.Property</c>, so a designer
/// host can offer them in completion without touching <c>UIPropertyRegistry</c>. Providers that cannot answer
/// simply do not implement the seam (the netstandard2.0-safe analogue of an empty default) and the consumer's
/// capability probe falls back to no attachable completion.
/// </summary>
public sealed class AttachableMetadataTests
{
    // The nine real attached properties a plain UIElement child (TextBlock) can carry from Grid / Canvas /
    // DockPanel — the ones whose owners this test force-registers. TextElement/ToolTipService/etc. also apply,
    // but these three owners are asserted explicitly (the rest are covered by the faithfulness subset check).
    private static readonly string[] KnownGridCanvasDock =
    [
        "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan",
        "Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom",
        "DockPanel.Dock",
    ];

    [Fact]
    public void ReflectionProvider_ImplementsTheSeam()
        => Assert.IsAssignableFrom<IXamlAttachablePropertyProvider>(ReflectionXamlMetadata.Instance);

    [Fact]
    public void GetAttachableMembers_ReturnsRegistryAttachablesQualified_ForUIElementChild()
    {
        // The registry is populated lazily as owner types initialize; force the owners we assert on.
        _ = UIControls.Grid.RowProperty;
        _ = UIControls.Canvas.LeftProperty;
        _ = UIControls.DockPanel.DockProperty;

        var provider = (IXamlAttachablePropertyProvider) ReflectionXamlMetadata.Instance;
        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(UIControls.TextBlock)).ClrType;

        var members = provider.GetAttachableMembers(target);
        var qualified = members.Select(m => m.QualifiedName).ToHashSet(StringComparer.Ordinal);

        // Completeness (for the forced owners): the real Grid/Canvas/DockPanel attached properties are offered.
        foreach (var name in KnownGridCanvasDock)
            Assert.Contains(name, qualified);

        // Shape: owner simple name + CLR namespace are carried (for xmlns-prefix resolution) alongside the name.
        var gridRow = members.Single(m => m.QualifiedName == "Grid.Row");
        Assert.Equal("Grid", gridRow.OwnerName);
        Assert.Equal("Row", gridRow.PropertyName);
        Assert.Equal("Cursorial.UI.Controls", gridRow.OwnerNamespace);

        // Every entry is a well-formed single-dot Owner.Property with non-empty, dot-free parts.
        Assert.All(members, m =>
        {
            Assert.NotEmpty(m.OwnerName);
            Assert.NotEmpty(m.PropertyName);
            Assert.DoesNotContain('.', m.OwnerName);
            Assert.DoesNotContain('.', m.PropertyName);
            Assert.Equal($"{m.OwnerName}.{m.PropertyName}", m.QualifiedName);
        });

        // Faithfulness: the provider invents nothing — every surfaced member is an attached property the registry
        // actually reports for the target. The registry only grows (append-only), so a snapshot taken AFTER the
        // provider call is a safe superset even under parallel test registration.
        var registryQualified = UIPropertyRegistry.AttachableOnType(typeof(UIControls.TextBlock))
                                                  .Select(p => $"{p.OwnerType.Name}.{p.Name}")
                                                  .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(registryQualified, qualified);
    }

    [Fact]
    public void GetAttachableMembers_TypeThatHostsNothing_ReturnsEmpty()
    {
        // Every attached property in this framework hosts on a UIElement-derived type (THost : UIElement); a
        // non-UIElement target (string) can therefore carry none.
        var provider = (IXamlAttachablePropertyProvider) ReflectionXamlMetadata.Instance;
        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(string)).ClrType;

        Assert.Empty(provider.GetAttachableMembers(target));
    }

    [Fact]
    public void GetAttachableMembers_ExcludesReadOnlyAttached_KeepsReadWriteAttached()
    {
        // TextElement declares two UIElement-wide attached properties: TextTrimming (read-write) and IsTrimmed
        // (read-only — reported via GetValue, never assigned). Completion offers only SETTABLE members, so the
        // seam must drop the read-only one while keeping the read-write one, even though the registry's
        // AttachableOnType (a pure "what may be attached" query) reports BOTH. This is the interaction the
        // IsTrimmed relocation surfaced: it is the first read-only attached property with a UIElement-wide host,
        // so before the settable gate it would have leaked onto every element in attachable completion.
        _ = UIControls.TextElement.TextTrimmingProperty;   // force TextElement's static ctor (registers both)
        _ = UIControls.TextElement.IsTrimmedProperty;

        var provider = (IXamlAttachablePropertyProvider) ReflectionXamlMetadata.Instance;
        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(UIControls.TextBlock)).ClrType;
        var qualified = provider.GetAttachableMembers(target).Select(m => m.QualifiedName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("TextElement.TextTrimming", qualified);    // read-write attached: offered
        Assert.DoesNotContain("TextElement.IsTrimmed", qualified); // read-only attached: excluded

        // The gate lives in the completion seam, not the registry query — AttachableOnType still reports both.
        var registry = UIPropertyRegistry.AttachableOnType(typeof(UIControls.TextBlock))
                                         .Select(p => $"{p.OwnerType.Name}.{p.Name}")
                                         .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("TextElement.IsTrimmed", registry);
    }

    [Fact]
    public void GetAttachableMembers_ReportsTargetsChildElements_ForChildPositioningOwners()
    {
        // The parent-context band lifts ONLY child-positioning attached properties. The seam carries
        // UIProperty.TargetsChildElements so a host can tell Grid.Row (set on a Grid's child) from an attached
        // SERVICE like NameScope (owner UIElement, attachable everywhere but never child-positioning) or a
        // formatting default like TextElement.TextTrimming (self-usable, unmarked).
        _ = UIControls.Grid.RowProperty;
        _ = UIControls.Canvas.LeftProperty;
        _ = UIControls.DockPanel.DockProperty;
        _ = UIControls.TextElement.TextTrimmingProperty;
        _ = Cursorial.UI.NameScope.NameScopeProperty;

        var provider = (IXamlAttachablePropertyProvider) ReflectionXamlMetadata.Instance;
        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(UIControls.TextBlock)).ClrType;
        var byName = provider.GetAttachableMembers(target)
                             .ToDictionary(m => m.QualifiedName, m => m.TargetsChildElements, StringComparer.Ordinal);

        // Child-positioning owners: marked, so a child inside them gets the parent-context lift.
        Assert.True(byName["Grid.Row"]);
        Assert.True(byName["Canvas.Left"]);
        Assert.True(byName["DockPanel.Dock"]);
        // Broadly-hosted service / formatting attached properties: NOT child-positioning.
        Assert.False(byName["UIElement.NameScope"]);
        Assert.False(byName["TextElement.TextTrimming"]);
    }

    [Fact]
    public void ProviderWithoutTheSeam_ProbeIsFalse_AndConsumerFallsBackToEmpty()
    {
        // A metadata provider that does not implement the optional seam — the netstandard2.0-safe stand-in for a
        // default-interface-method that would return empty. The consumer's capability probe fails and it offers
        // no attachable completion.
        IXamlTypeMetadataProvider bare = HandBuiltMetadata.Instance;
        Assert.False(bare is IXamlAttachablePropertyProvider);

        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(UIControls.TextBlock)).ClrType;
        var offered = bare is IXamlAttachablePropertyProvider ap
                          ? ap.GetAttachableMembers(target)
                          : Array.Empty<XamlAttachableMember>();
        Assert.Empty(offered);
    }

    [Fact]
    public void GetAttachableMembers_NullTarget_Throws()
    {
        var provider = (IXamlAttachablePropertyProvider) ReflectionXamlMetadata.Instance;
        Assert.Throws<ArgumentNullException>(() => provider.GetAttachableMembers(null!));
    }
}
