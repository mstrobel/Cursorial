using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Diagnostics;

// The GetSetProperties enumeration (design doc §3.9) — the store-contribution list the style-inspector
// overlay (P9.8) walks: every property with a live local / style / animation contribution on the element,
// excluding inherited-only values (not set on this element) and never-set properties.
public sealed class GetSetPropertiesTests
{
    [Fact] // includes a local value AND a themed (ControlTheme-layer) contribution; excludes never-set properties
    public void GetSetProperties_IncludesLocalAndStyledContributions()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 6) });
        var button = new Button { Width = 10 }; // Width is a local value; the control theme contributes Background/Template
        host.ShowRoot(button);
        host.RunUntilIdle();

        var set = button.GetSetProperties();

        Assert.Contains(UIElement.WidthProperty, set);          // a local value
        Assert.Contains(Control.BackgroundProperty, set);       // a themed (ControlTheme-layer) contribution
        Assert.DoesNotContain(UIElement.MaxWidthProperty, set); // never set → no store entry
    }

    [Fact] // a fresh element with no contributions reports nothing
    public void GetSetProperties_Empty_WithoutContributions()
    {
        using var host = UIHeadlessHost.Create();
        var border = new Border(); // never attached, never written
        host.ShowRoot(new StackPanel());
        host.RunUntilIdle();

        Assert.Empty(border.GetSetProperties());
    }

    [Fact] // a cleared property leaves a retained (EffectivePriority == Unset) store entry that is EXCLUDED (M115)
    public void GetSetProperties_ExcludesClearedEntry()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 6) });
        var button = new Button { Width = 10 };
        host.ShowRoot(button);
        host.RunUntilIdle();
        Assert.Contains(UIElement.WidthProperty, button.GetSetProperties()); // present while set

        button.ClearValue(UIElement.WidthProperty); // the entry is retained but goes Unset (M115)
        host.RunUntilIdle();
        Assert.DoesNotContain(UIElement.WidthProperty, button.GetSetProperties()); // the != Unset filter excludes it
    }
}
