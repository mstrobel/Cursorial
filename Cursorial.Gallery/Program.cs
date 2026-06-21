using Cursorial.Gallery;
using Cursorial.Gallery.Pages;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Input;

// The standalone control gallery (#107). A real UIApplication over the live terminal (alt screen), NOT a demo
// command. The shell hosts one page per control/group; the ScrollViewer page is first (scrolling is the framework's
// biggest bug surface). q / Esc / Ctrl+C exit.

var pages = new IGalleryPage[]
{
    new ScrollViewerPage(),
    new ChessboardPage(),
    new VirtualizedListPage(),
};

var shell = new GalleryShell(pages);

var app = UIApplication.CreateBuilder()
    .WithFrameRate(60)
    .Build();

try
{
    await app.RunAsync(() =>
    {
        var root = shell.Build();

        // Global exit: q / Esc leave; Ctrl+C falls through to the S6 default gesture (unhandled).
        root.AddHandler(UIElement.KeyDownEvent, (object _, KeyEventArgs e) =>
        {
            if (e.Modifiers != KeyModifiers.None)
                return; // leave Ctrl/Alt chords for bindings + the Ctrl+C exit gesture
            if (e.Key == Key.Escape || (e.Key == Key.Character && e.Text.Length == 1 && e.Text.Span[0] == 'q'))
            {
                app.Shutdown(0);
                e.Handled = true;
            }
        });

        return root;
    });
}
finally
{
    await app.DisposeAsync();
}
