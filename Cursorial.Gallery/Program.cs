using Cursorial.Gallery;
using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

// The standalone control gallery (#107) — XAML-first MVVM. A real UIApplication over the live terminal (alt
// screen), NOT a demo command. The shell + every page is loaded from embedded XAML (GalleryApp.BuildRoot) and
// bound to view-models; implicit DataTemplates resolve each page. q / Esc / Ctrl+C exit.

UIApplication app = UIApplication.CreateBuilder()
                                 .WithFrameRate(60)
                                 .UseAlternateScreen()
                                 .Build();

// app.NerdFontAvailable = true;
// app.Theme = Cursorial.UI.Themes.IndigoDusk.IndigoDuskTheme.LoadTheme();

static string FormatElement(UIObject? element)
{
    if (element is null) return "<null>";
    var name = (element as UIElement)?.Name;
    return $"{element.GetType().Name}" + (name is { Length: > 0 } ? $"#{name})" : "");
}

try
{
    var root = GalleryApp.BuildRoot();
    var vm = root.DataContext as ShellViewModel;
    
    void OnStyleDebugDiagnosticsDiagnosticEmitted(string c, string m) => vm?.AddDiagnostic($"[Style    ] {c}: {m}");
    void OnControlDiagnosticsDiagnosticRaised(ControlDiagnosticEvent d) => vm?.AddDiagnostic($"[Control  ] {d.Kind}: {d.Message} " + $"({d.Element?.GetType().Name}" + (d.Element is { Name: { Length: > 0 } n } ? $"#{n})" : ")"));
    void OnBindingDiagnosticsTraceEmitted(BindingTraceEvent d) => vm?.AddDiagnostic($"[Binding  ] {d.Level} - {d.Kind}: {d.Message} " + $"(Target={d.TargetDescription}; Path={d.Path})");
    void OnLayoutDiagnosticsDiagnosticRaised(LayoutDiagnosticEvent d) => vm?.AddDiagnostic($"[Layout   ] {d.Kind}: {d.Message} ({FormatElement(d.Element)})");
    void OnAnimationDiagnosticsTrackError(StoryboardTrackError e) => vm?.AddDiagnostic($"[Animation] {FormatElement(e.Scope)}: {e.Message} " + $"({e.Track.TargetProperty?.Name})");
    void OnUIDiagnosticsRejectedValue(UIObject t, UIProperty p, object? v) => vm?.AddDiagnostic($"[Rejected ] {FormatElement(t)}.{p.Name} = {v}");

    StyleDebugDiagnostics.DiagnosticEmitted += OnStyleDebugDiagnosticsDiagnosticEmitted;
    ControlDiagnostics.DiagnosticRaised += OnControlDiagnosticsDiagnosticRaised;
    BindingDiagnostics.TraceEmitted += OnBindingDiagnosticsTraceEmitted;
    LayoutDiagnostics.DiagnosticRaised += OnLayoutDiagnosticsDiagnosticRaised;
    AnimationDiagnostics.TrackError += OnAnimationDiagnosticsTrackError;
    UIDiagnostics.RejectedValue += OnUIDiagnosticsRejectedValue;

    app.BeginShutdown += (_, _) =>
                         {
                             StyleDebugDiagnostics.DiagnosticEmitted -= OnStyleDebugDiagnosticsDiagnosticEmitted;
                             ControlDiagnostics.DiagnosticRaised -= OnControlDiagnosticsDiagnosticRaised;
                             BindingDiagnostics.TraceEmitted -= OnBindingDiagnosticsTraceEmitted;
                             LayoutDiagnostics.DiagnosticRaised -= OnLayoutDiagnosticsDiagnosticRaised;
                             AnimationDiagnostics.TrackError -= OnAnimationDiagnosticsTrackError;
                             UIDiagnostics.RejectedValue -= OnUIDiagnosticsRejectedValue;
                         };

    app.Started += (_, _) =>
                   {
                       vm?.RefreshTheme();
                       UITimer.Start(TimeSpan.FromSeconds(0.1), () => app.FocusManager.MoveFocus(FocusNavigationDirection.Next));
                   };

    await app.RunAsync(() => root);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"{ex.StackTrace}");
        
    for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
    {
        Console.WriteLine($"Inner: {inner.Message}");
        Console.WriteLine($"{inner.StackTrace}");
    }
}
finally
{
    await app.DisposeAsync();
}