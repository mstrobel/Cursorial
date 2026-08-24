using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The designer's d:DataContext materialization path, end-to-end: the element form's detached
/// fragment loads through the ordinary <see cref="XamlLoader"/> with EVERY authored member intact.
/// Pins the multi-attribute truncation regression: a <c>ReadSubtree</c> reader's
/// MoveToFirstAttribute/MoveToNextAttribute enumeration silently truncates after ONE attribute on
/// its second pass, so only the first member of a design view-model survived; ParseAttributes now
/// iterates by index (MoveToAttribute(i)), which is immune.
/// </summary>
public sealed class DesignDataContextMaterializationTests : LoaderTestBase
{
    private const string Doc =
        "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
        "xmlns:d=\"https://cursorial.dev/xaml/design\">" +
        "<d:DataContext>" +
        "<DesignProbeViewModel Lines=\"8\" Columns=\"41\" Placeholder=\"Write here...\" Prompt=\"Describe your changes:\" />" +
        "</d:DataContext>" +
        "<TextBlock/>" +
        "</StackPanel>";

    [Fact] // every attribute on the fragment's root object survives capture (was: only the first)
    public void FragmentRootAttributes_AllMembersSurviveCapture()
    {
        var doc = Loader.Parse(Doc, TestSource);

        var fragment = doc.DesignInfo?.DataContextContent;
        Assert.NotNull(fragment);
        Assert.Empty(fragment!.Diagnostics);
        Assert.Equal(4, fragment.Root.MemberCount);
    }

    [Fact] // the designer path: the fragment materializes with every member assigned
    public void Fragment_MaterializesEveryMember()
    {
        var doc = Loader.Parse(Doc, TestSource);
        var fragment = doc.DesignInfo!.DataContextContent!;

        var vm = Assert.IsType<DesignProbeViewModel>(Loader.Load(fragment));
        Assert.Equal(8, vm.Lines);
        Assert.Equal(41, vm.Columns);
        Assert.Equal("Write here...", vm.Placeholder);
        Assert.Equal("Describe your changes:", vm.Prompt);
    }
}

/// <summary>Fixture view-model for the design-data materialization rows (namespace scope — nested
/// classes do not resolve as XAML elements).</summary>
public sealed class DesignProbeViewModel
{
    public int Lines { get; set; }
    public int Columns { get; set; }
    public string? Placeholder { get; set; }
    public string? Prompt { get; set; }
}
