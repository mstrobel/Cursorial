using Cursorial.Gallery;
using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Configuration;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

// The standalone control gallery (#107) — XAML-first MVVM. A real UIApplication over the live terminal (alt
// screen), NOT a demo command. The shell + every page is loaded from embedded XAML (GalleryApp.BuildRoot) and
// bound to view-models; implicit DataTemplates resolve each page. q / Esc / Ctrl+C exit.

UIApplication app = UIApplication.DefaultBuilder()
                                 .WithUserConfiguration(new UserConfigurationOptions { ShowFirstRunWizard = true })
                                 .Build();

// app.NerdFontAvailable = true;
// app.Theme = Cursorial.UI.Themes.CurioTheme.Snapshot;

// KeyTips (#145): arm the Bars Alt-overlay accelerator over the ribbon. On a terminal that passes the ND23 gate
// (a Kitty-keyboard / Win32 terminal with color), holding Alt shows amber badges over the tabs; letters drill
// tab → group → control, Esc backs out. On a terminal that fails the gate it's a no-op (inline access-key
// underlines remain the affordance). Idempotent, so it's safe to call once here.
app.EnableKeyTips();

static string FormatElement(UIObject? element)
{
    if (element is null) return "<null>";
    var name = (element as UIElement)?.Name;
    return $"{element.GetType().Name}" + (name is { Length: > 0 } ? $"#{name})" : "");
}

if (args.Contains("--debug") is true)
{
    while (System.Diagnostics.Debugger.IsAttached is false)
        Thread.Sleep(33);
}

try
{
    var root = GalleryApp.BuildRoot(app);
    var vm = root.DataContext as ShellViewModel;
    
    void OnStyleDebugDiagnosticsDiagnosticEmitted(string c, string m) => vm?.AddDiagnostic($"[Style    ] {c}: {m}");
    void OnControlDiagnosticsDiagnosticRaised(ControlDiagnosticEvent d) => vm?.AddDiagnostic($"[Control  ] {d.Kind}: {d.Message} " + $"({d.Element?.GetType().Name}" + (d.Element is { Name: { Length: > 0 } n } ? $"#{n})" : ")"));
    void OnBindingDiagnosticsTraceEmitted(BindingTraceEvent d) => vm?.AddDiagnostic($"[Binding  ] {d.Level} - {d.Kind}: {d.Message} " + $"(Target={d.TargetDescription}; Path={d.Path})");
    void OnLayoutDiagnosticsDiagnosticRaised(LayoutDiagnosticEvent d) => vm?.AddDiagnostic($"[Layout   ] {d.Kind}: {d.Message} ({FormatElement(d.Element)})");
    void OnAnimationDiagnosticsTrackError(StoryboardTrackError e) => vm?.AddDiagnostic($"[Animation] {FormatElement(e.Scope)}: {e.Message} " + $"({e.Track.TargetProperty?.Name})");
    void OnUIDiagnosticsRejectedValue(UIObject t, UIProperty p, object? v) => vm?.AddDiagnostic($"[Rejected ] {FormatElement(t)}.{p.Name} = {v}");

    // TODO: RE-ENABLE THESE BEFORE COMMITTING!
    // StyleDebugDiagnostics.DiagnosticEmitted += OnStyleDebugDiagnosticsDiagnosticEmitted;
    // ControlDiagnostics.DiagnosticRaised += OnControlDiagnosticsDiagnosticRaised;
    BindingDiagnostics.TraceEmitted += OnBindingDiagnosticsTraceEmitted;
    // LayoutDiagnostics.DiagnosticRaised += OnLayoutDiagnosticsDiagnosticRaised;
    // AnimationDiagnostics.TrackError += OnAnimationDiagnosticsTrackError;
    // UIDiagnostics.RejectedValue += OnUIDiagnosticsRejectedValue;

    void OnBeginShutdown(object? o, EventArgs eventArgs)
    {
        if (o is UIApplication a)
        {
            a.Started -= OnStarted;
            a.BeginShutdown -= OnBeginShutdown;
        }

        StyleDebugDiagnostics.DiagnosticEmitted -= OnStyleDebugDiagnosticsDiagnosticEmitted;
        ControlDiagnostics.DiagnosticRaised -= OnControlDiagnosticsDiagnosticRaised;
        BindingDiagnostics.TraceEmitted -= OnBindingDiagnosticsTraceEmitted;
        LayoutDiagnostics.DiagnosticRaised -= OnLayoutDiagnosticsDiagnosticRaised;
        AnimationDiagnostics.TrackError -= OnAnimationDiagnosticsTrackError;
        UIDiagnostics.RejectedValue -= OnUIDiagnosticsRejectedValue;
    }

    app.BeginShutdown += OnBeginShutdown;

    void OnStarted(object? o, EventArgs eventArgs)
    {
        if (o is UIApplication a) a.Started -= OnStarted;
        vm?.RefreshTheme();
        UITimer.Start(TimeSpan.FromSeconds(0.1), () => app.FocusManager.MoveFocus(FocusNavigationDirection.Next));
    }

    app.Started += OnStarted;

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