using Cursorial.Gallery;
using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Input;

// The standalone control gallery (#107) — XAML-first MVVM. A real UIApplication over the live terminal (alt
// screen), NOT a demo command. The shell + every page is loaded from embedded XAML (GalleryApp.BuildRoot) and
// bound to view-models; implicit DataTemplates resolve each page. q / Esc / Ctrl+C exit.

var app = UIApplication.CreateBuilder()
    .WithFrameRate(60)
    .Build();

try
{
    await app.RunAsync(() =>
    {
        var root = GalleryApp.BuildRoot();

        // Global exit: q / Esc leave; Ctrl+C falls through to the S6 default gesture (unhandled).
        root.AddHandler(UIElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Modifiers != KeyModifiers.None)
                return; // leave Ctrl/Alt chords for bindings + the Ctrl+C exit gesture

            if (e is { Key: Key.Escape, IsRepeat: true })
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
