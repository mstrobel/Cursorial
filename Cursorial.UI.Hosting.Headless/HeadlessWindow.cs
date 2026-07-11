namespace Cursorial.UI.Hosting.Headless;

public class HeadlessWindow : Window
{
    public HeadlessWindow(UIHeadlessHost host)
    {
        if (host.Options.DisableInactiveWindowTransitions)
            Transition.SetTransitions(this, null);
    }

    protected override object ControlThemeKey => typeof(Window);
}