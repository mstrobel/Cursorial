namespace Cursorial.UI.Testing;

public class TestWindow : Window
{
    public TestWindow(UITestHost host)
    {
        if (host.Options.DisableInactiveWindowTransitions)
            Transition.SetTransitions(this, null);
    }

    protected override object ControlThemeKey => typeof(Window);
}