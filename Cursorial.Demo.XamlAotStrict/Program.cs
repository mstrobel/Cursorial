using Cursorial.Demo.XamlAotStrict;

// Construct the code-behind view: its generated InitializeComponent loads the embedded StrictView.xaml through
// the assembly's GENERATED metadata provider (reflection-free). Under PublishAot + CursorialXamlStrictAot the
// reflection provider is trimmed out, so this is the whole XAML loading path — no IL2026/IL3050.
var view = new StrictView();

var ok = view.Ok is not null && view.Label is not null && view.Children.Count == 2;
System.Console.WriteLine(ok
    ? $"Strict AOT OK: loaded {view.Children.Count} children via full lowering (no runtime loader); Ok.Content='{view.Ok!.Content}'."
    : "Strict AOT FAILED: the view did not load as expected.");

return ok ? 0 : 1;
