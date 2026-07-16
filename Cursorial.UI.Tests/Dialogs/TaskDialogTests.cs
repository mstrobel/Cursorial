// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine and
// the awaited dialog tasks finish on pure (non-UI) continuations, so a bounded Wait cannot deadlock.
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// The real FB-12 <see cref="TaskDialog"/>'s first coverage, driven through the canonical static
/// entry point <c>TaskDialog.ShowAsync(UIApplication, TaskDialogRequest, …)</c> (parallel to
/// <c>MessageBox.ShowAsync(UIApplication, …)</c>): the request's main
/// instruction / content / title / buttons render over a stub root, the keyboard grammar drives the
/// save triad (Enter → Save, Esc → Cancel, Tab → Don't Save), command links return the chosen link,
/// the verification checkbox's live (toggled) state rides back on the result, and a canceled token
/// dismisses without throwing (the no-throw contract). The triad/link/dismissal rows run under both
/// §5.1 capability presets; the checkbox row is glyph-sensitive and pins one preset.
///
/// Severity-based visual STYLING is deliberately NOT asserted: it is carried on the request but is
/// not yet functional in <see cref="TaskDialog"/> (FB-12 gap). These tests pin behavior only — the
/// request carrying <see cref="TaskDialogSeverity"/> and the buttons working — never a severity
/// glyph or color.
/// </summary>
public sealed class TaskDialogTests
{
    /// <summary>The §5.1 capability matrix for rendering/input suites.</summary>
    public static TheoryData<string> CapabilityPresets =>
        new() { nameof(HeadlessCapabilities.KittyTruecolor), nameof(HeadlessCapabilities.Ansi16Legacy) };

    private static TerminalCapabilities Resolve(string preset) => preset switch
    {
        nameof(HeadlessCapabilities.KittyTruecolor) => HeadlessCapabilities.KittyTruecolor,
        nameof(HeadlessCapabilities.Ansi16Legacy) => HeadlessCapabilities.Ansi16Legacy,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    private static UIHeadlessHost CreateHostWithRoot(string capabilityPreset, Size? initialSize = null)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            Capabilities = Resolve(capabilityPreset),
            InitialSize = initialSize ?? new Size(80, 24)
        });
        host.ShowRoot(new TextBlock { Text = "root stub" });
        Assert.True(host.RunUntilIdle());
        return host;
    }

    /// <summary>The composited screen as one string, for containment assertions.</summary>
    private static string ScreenText(UIHeadlessHost host)
    {
        var text = new StringBuilder();

        for (var row = 0; row < host.FrameBuffer.Rows; row++)
            text.AppendLine(host.GetRowText(row));

        return text.ToString();
    }

    /// <summary>Pumps the host idle, then waits out the dialog task's pure (non-UI) mapping tail.</summary>
    private static TResult Complete<TResult>(UIHeadlessHost host, Task<TResult> task)
    {
        Assert.True(host.RunUntilIdle());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "the dialog task did not complete");
        return task.Result;
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_RendersInstructionContentAndButtons(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        // Severity carried, NOT asserted visually (FB-12 styling gap).
        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Save changes to notes.md?")
                                        {
                                            Title = "Unsaved Changes",
                                            Content = "Unsaved edits will be lost.",
                                            Severity = TaskDialogSeverity.Warning,
                                            Buttons = TaskDialogButton.SaveTriad
                                        });

        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("Save changes to notes.md?", screen);
        Assert.Contains("Unsaved edits will be lost.", screen);
        Assert.Contains("Save", screen);
        Assert.Contains("Don't Save", screen);
        Assert.Contains("Cancel", screen);

        host.SendKey(Key.Escape); // don't leak the modal
        Complete(host, task);
    }

    // ── Keyboard grammar: the save triad ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_Enter_MapsToSave(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Save changes to notes.md?")
                                        {
                                            Buttons = TaskDialogButton.SaveTriad
                                        });

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter); // Save is the triad's default

        var result = Complete(host, task);
        Assert.False(result.IsDismissed);
        Assert.Equal(TaskDialogButton.Save, result.Button);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_Escape_MapsToCancel(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Save changes to notes.md?")
                                        {
                                            Buttons = TaskDialogButton.SaveTriad
                                        });

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Escape); // Cancel carries IsCancel in the triad

        var result = Complete(host, task);
        Assert.False(result.IsDismissed);
        Assert.Equal(TaskDialogButton.Cancel, result.Button);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_TabToDontSave_MapsToDiscard(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Save changes to notes.md?")
                                        {
                                            Buttons = TaskDialogButton.SaveTriad
                                        });

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Tab); // Save (default, focused) → Don't Save
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(TaskDialogButton.DontSave, Complete(host, task).Button);
    }

    // ── Dismissal + defaults ─────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void CanceledToken_YieldsDismissedResult_WithoutThrowing(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        using var cts = new CancellationTokenSource();

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Recover the journal?")
                                        {
                                            Buttons = TaskDialogButton.SaveTriad
                                        },
                                        cts.Token);

        Assert.True(host.RunUntilIdle());
        cts.Cancel();

        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
        Assert.Null(result.Button);
        Assert.Empty(host.Application.WindowManager!.Windows);
    }

    // ── Verification checkbox (a distinctive TaskDialog affordance) ──────────────────────────────

    // Single-preset: the checkbox glyph ([ ] → [✓]) is capability-sensitive, and this row is about the
    // checkbox's live state riding back on the result, not the §5.1 render matrix.
    [Fact]
    public void VerificationCheckbox_ToggledState_RidesBackOnResult()
    {
        using var host = CreateHostWithRoot(nameof(HeadlessCapabilities.KittyTruecolor));

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Delete the workspace?")
                                        {
                                            Buttons = [TaskDialogButton.Ok, TaskDialogButton.Cancel],
                                            VerificationText = "Don't ask me again",
                                            VerificationChecked = false
                                        });

        Assert.True(host.RunUntilIdle());
        Assert.Contains("Don't ask me again", ScreenText(host));
        Assert.Contains("[ ]", ScreenText(host)); // starts unchecked

        // The checkbox takes the dialog's initial focus; Space toggles it on.
        host.SendKey(Key.Space);
        Assert.True(host.RunUntilIdle());
        Assert.Contains("[✓]", ScreenText(host)); // now checked

        host.SendKey(Key.Escape); // Cancel (IsCancel) closes regardless of which control is focused

        var result = Complete(host, task);
        Assert.Equal(TaskDialogButton.Cancel, result.Button);
        Assert.True(result.VerificationChecked); // the live checkbox state rode back on the result
    }

    // ── Command links (the distinctive TaskDialog surface, no MessageBox analog) ──────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void CommandLink_Selected_RidesBackAsResult(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        // Buttons carrying an Explanation render as command links (title + inline description),
        // unlike the plain-button save triad the MessageBox-backed path is limited to.
        var overwrite = new TaskDialogButton("overwrite", "_Overwrite") { Explanation = "Replace the file." };
        var keepBoth = new TaskDialogButton("keepboth", "_Keep Both") { Explanation = "Keep both files." };

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("A file with that name already exists.")
                                        {
                                            Buttons = [overwrite, keepBoth, TaskDialogButton.Cancel]
                                        });

        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("Overwrite", screen);
        Assert.Contains("Replace the file.", screen); // the command-link description renders inline
        Assert.Contains("Keep Both", screen);

        // Initial focus lands on the first command link; one Tab advances to the second, Enter picks it.
        host.SendKey(Key.Tab);
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        var result = Complete(host, task);
        Assert.False(result.IsDismissed);
        Assert.Equal(keepBoth, result.Button); // the chosen link (Explanation and all) is the result
    }

    // ── Expandable details (SizeToContentMode.Always — the framework re-fit replaced the manual re-layout) ──

    [Fact]
    public void ExpandedInformation_Toggle_GrowsAndShrinksDialog()
    {
        using var host = CreateHostWithRoot(nameof(HeadlessCapabilities.KittyTruecolor));

        var task = TaskDialog.ShowAsync(host.Application,
                                        new TaskDialogRequest("Something happened.")
                                        {
                                            ExpandedInformation = "Recovery journal:\nline two\nline three",
                                            Buttons = [TaskDialogButton.Ok]
                                        });

        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;
        var dialog = Assert.Single(wm.Windows);
        var collapsed = dialog.ActualSize;
        Assert.DoesNotContain("Recovery journal:", ScreenText(host));

        var toggle = FindDescendant<Cursorial.UI.Controls.ToggleButton>(dialog)
                     ?? throw new InvalidOperationException("expander toggle not found");

        // Expand: the dialog grows to include the details — the framework Always-mode re-fit, no dialog-side layout code.
        toggle.IsChecked = true;
        Assert.True(host.RunUntilIdle());

        Assert.True(dialog.ActualSize.Rows > collapsed.Rows);
        Assert.Contains("Recovery journal:", ScreenText(host));
        Assert.Contains("line three", ScreenText(host));
        Assert.False(dialog.IsClippedByViewport); // AutoFitToViewport keeps the grown dialog on-screen

        // Collapse: it shrinks back to the original fit.
        toggle.IsChecked = false;
        Assert.True(host.RunUntilIdle());

        Assert.Equal(collapsed.Rows, dialog.ActualSize.Rows);
        Assert.DoesNotContain("Recovery journal:", ScreenText(host));

        host.SendKey(Key.Enter); // dismiss via Ok so the dialog task completes
        Complete(host, task);
    }

    private static T? FindDescendant<T>(Cursorial.UI.UIElement element) where T : Cursorial.UI.UIElement
    {
        for (var i = 0; i < element.VisualChildrenCount; i++)
        {
            var child = element.GetVisualChild(i);

            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is {} nested)
                return nested;
        }

        return null;
    }
}
