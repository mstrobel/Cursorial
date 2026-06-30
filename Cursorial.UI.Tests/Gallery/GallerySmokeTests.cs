using System.Text;

using Cursorial.Gallery;
using Cursorial.Gallery.Pages;
using Cursorial.Gallery.ViewModels;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Gallery;

// The standalone gallery (#107) is a real TTY app, so it can't be run in CI — this headless canary loads its
// XAML-first MVVM shell through UITestHost and asserts the implicit-DataTemplate page resolution + navigation work
// (the manual harness still gets exercised on every run).
public sealed class GallerySmokeTests(ITestOutputHelper output)
{
    private static string Screen(UITestHost host, int rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                if (FindDescendant<T>(child) is { } found)
                    return found;
        return null;
    }

    private static IEnumerable<T> AllDescendants<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            yield return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                foreach (var found in AllDescendants<T>(child))
                    yield return found;
    }

    [Fact] // the shell loads from embedded XAML, binds to the ShellViewModel, and the first page (ScrollViewer) resolves
    public void Shell_LoadsFromXaml_RendersFirstPage()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });

        UIElement root = null!;
        var ex = Record.Exception(() =>
        {
            root = GalleryApp.BuildRoot();
            host.ShowRoot(root);

            if (root.DataContext is ShellViewModel shell)
                shell.SelectedPage = shell.Pages.OfType<ScrollViewerPageViewModel>().Single();

            host.RunUntilIdle();
        });
        Assert.Null(ex);

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Gallery", screen);        // the title bar
        Assert.Contains("ScrollViewer", screen);   // the nav entry (first page, selected)
        Assert.Contains("Inputs", screen);         // the second nav entry
        Assert.Contains("Cycle V-bar", screen);    // the ScrollViewer page's toggle bar (the implicit template resolved)
        Assert.Contains("row 000", screen);        // the scrollable content
        Assert.NotNull(FindDescendant<ScrollViewer>(root)); // the page view materialized
    }

    [Fact] // selecting a different page VM swaps the ContentControl's view via the implicit DataTemplate (the MVVM nav proof)
    public void Shell_Navigation_SwapsPageViaImplicitDataTemplate()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot();
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        Assert.IsType(shell.Pages[0].GetType(), shell.SelectedPage); // starts on the first page

        // Navigate to the Inputs page by moving the selection (the nav ListBox's SelectedItem is two-way bound to this).
        shell.SelectedPage = shell.Pages.OfType<InputsPageViewModel>().Single();
        host.RunUntilIdle();

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Password", screen);            // the Inputs view rendered
        Assert.Contains("Subscribe to updates", screen);
        Assert.DoesNotContain("Cycle V-bar", screen);   // the ScrollViewer page's chrome is gone
        Assert.NotNull(FindDescendant<PasswordBox>(root)); // the new control is in the showcase
        Assert.NotNull(FindDescendant<Slider>(root));
    }

    [Fact] // two-way bindings on the Inputs page round-trip VM <-> control (typing into the bound TextBox updates the VM)
    public void InputsPage_TwoWayBinding_RoundTrips()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot();
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        var inputs = shell.Pages.OfType<InputsPageViewModel>().Single();
        shell.SelectedPage = inputs;
        host.RunUntilIdle();

        var textBox = FindDescendant<TextBox>(root)!; // the first editable field is the Name TextBox
        textBox.Focus();
        host.RunUntilIdle();
        host.SendText("Ada");
        host.RunUntilIdle();

        Assert.Equal("Ada", inputs.Name);              // control -> VM (two-way)
        Assert.Contains("Name=\"Ada\"", inputs.Status); // and the live status reflects it
    }

    [Fact] // the Journal field is the undo/redo canary: the VM Undo/Redo commands (the button path, editor passed via
           // x:Reference) AND the field's own Ctrl+Z all drive the multi-line TextBox's history
    public void InputsPage_Journal_UndoRedo()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot();
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        var inputs = shell.Pages.OfType<InputsPageViewModel>().Single();
        shell.SelectedPage = inputs;
        host.RunUntilIdle();

        // The Journal is the one multi-line field (AcceptsReturn) — the Name/Permissions TextBoxes and the
        // PasswordBox (a TextBox subclass) are single-line.
        var journal = AllDescendants<TextBox>(root).Single(t => t.AcceptsReturn);
        var initial = inputs.Journal;
        Assert.Contains("\n", initial); // the seeded text is genuinely multi-line

        journal.Focus();
        journal.CaretIndex = journal.Text.Length; // seals coalescing; append after the seed text
        host.RunUntilIdle();
        host.SendText(" Extra.");
        host.RunUntilIdle();
        Assert.EndsWith(" Extra.", inputs.Journal); // typed text round-tripped to the VM (two-way)

        // Undo via the VM command — the editor arrives as the command parameter (the x:Reference button wiring).
        Assert.True(inputs.UndoCommand.CanExecute(journal));
        inputs.UndoCommand.Execute(journal);
        host.RunUntilIdle();
        Assert.Equal(initial, inputs.Journal); // the typed run was reverted in one undo

        inputs.RedoCommand.Execute(journal);
        host.RunUntilIdle();
        Assert.EndsWith(" Extra.", inputs.Journal); // and redone

        // The field's own keyboard chord drives the same history.
        host.SendKey(Key.Character, KeyModifiers.Control, "z");
        host.RunUntilIdle();
        Assert.Equal(initial, inputs.Journal);
    }

    [Fact] // The chessboard primitive (#107, a future page): content-assisted LEADING-EDGE snapping via IScrollContentHost.
           // The viewport height (24) is a whole number of 4-row tiles, so the vertical offsets are exact (4, then 0).
    public void Chessboard_SnapsScrollToWholeTiles()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var board = new Chessboard();
        var sv = new ScrollViewer
        {
            Content = board,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // Hidden scrolls without a bar
            Focusable = true,
        };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(4, sv.VerticalOffset);   // bottom edge snapped onto a 4-row tile boundary (24 → 28 → offset 4)

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.InRange(sv.HorizontalOffset, 1, 8); // leading-edge: scroll right advances 1..8 cells to align the right edge

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.VerticalOffset);

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.HorizontalOffset);
    }

    [Fact] // Leading-edge snap math (the reported case), exercised directly with a controlled non-tile-multiple viewport.
    public void Chessboard_LeadingEdgeSnap_RevealsTheTrailingTile()
    {
        var board = new Chessboard();
        IScrollContentHost host = board;
        host.SetViewport(new Size(77, 24)); // 77 is NOT a multiple of the 8-wide tile → a tile would be cut

        Assert.Equal(3, host.LineStep(0, +1, vertical: false));
        Assert.Equal(8, host.LineStep(3, +1, vertical: false));
        Assert.Equal(3, host.LineStep(11, -1, vertical: false));
        Assert.Equal(8, host.LineStep(8, -1, vertical: false));
    }
}
