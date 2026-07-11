using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Input;

using CursorialApp.Views;

var app = UIApplication.DefaultBuilder().Build();

try
{
    // Escape quits from anywhere; everything else routes to the focused element.
    app.InputDispatcher.PreProcessInput += (_, args) =>
    {
        if (args is KeyEventArgs { Key: Key.Escape })
        {
            app.Shutdown();
            args.Handled = true;
        }
    };

    await app.RunAsync(() => new MainView());
    return 0;
}
finally
{
    await app.DisposeAsync();
}
