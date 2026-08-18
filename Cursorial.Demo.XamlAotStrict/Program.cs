using Cursorial.Demo.XamlAotStrict;
using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Input;

// Construct the code-behind view: its generated InitializeComponent loads the embedded StrictView.xaml through
// the assembly's GENERATED metadata provider (reflection-free). Under PublishAot + CursorialXamlStrictAot the
// reflection provider is trimmed out, so this is the whole XAML loading path — no IL2026/IL3050.
// var view = new StrictView();
//
// var ok = view.Ok is not null && view.Label is not null && view.Children.Count == 2;
// System.Console.WriteLine(ok
//     ? $"Strict AOT OK: loaded {view.Children.Count} children via full lowering (no runtime loader); Ok.Content='{view.Ok!.Content}'."
//     : "Strict AOT FAILED: the view did not load as expected.");

var app = UIApplication.DefaultBuilder().Build();

var ok = true;

try
{
    app.InputDispatcher.PreProcessInput += (_, args) =>
                                           {
                                               if (args is KeyEventArgs { Key: Key.Escape } or 
                                                           KeyEventArgs { Key: Key.Character, Text.Span: "Q" or "q" })
                                               {
                                                   app.Shutdown();
                                                   args.Handled = true;
                                               }
                                           };
    await app.RunAsync(() => new StrictView { DataContext = new StrictViewModel() });
}
catch (Exception e)
{
    ok = false;

    await Console.Error.WriteLineAsync($"Error: {e.Message}");
    await Console.Error.WriteLineAsync(e.StackTrace);
    
    for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
    {
        await Console.Error.WriteLineAsync($"Inner Error: {inner.Message}");
        await Console.Error.WriteLineAsync(inner.StackTrace);
    }
}
finally
{
    await app.DisposeAsync(); // clears the thread-local Current for the next demo run
}

return ok ? 0 : 1;
