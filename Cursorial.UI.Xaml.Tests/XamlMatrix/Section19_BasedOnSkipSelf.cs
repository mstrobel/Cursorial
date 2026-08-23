using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Universal self-skip (design: a dictionary entry isn't in the dictionary until fully constructed). A
/// <c>{StaticResource K}</c> evaluated DURING K's own realization skips the still-realizing self slot and
/// resolves against the OUTER scope — so <c>Style.BasedOn="{StaticResource {x:Type X}}"</c> on an X-keyed style
/// extends an OUTER (enclosing/theme) same-keyed style instead of the old CD6 self-cycle throw. An outer that
/// still doesn't exist surfaces as a normal runtime ResourceNotFound (not a cycle error).
/// </summary>
public sealed class Section19_BasedOnSkipSelf : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // A same-keyed BasedOn resolves to the OUTER (enclosing-scope) style, not itself.
    public void SelfKeyBasedOn_ResolvesOuterAmbientStyle()
    {
        var root = Load<UIControls.StackPanel>(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<Style x:Key=\"K\" TargetType=\"Button\"><Setter Property=\"Width\" Value=\"3\"/></Style>" +
              "</StackPanel.Resources>" +
              "<Button>" +
                "<Button.Resources>" +
                  "<Style x:Key=\"K\" TargetType=\"Button\" BasedOn=\"{StaticResource K}\">" +
                    "<Setter Property=\"Height\" Value=\"9\"/></Style>" +
                "</Button.Resources>" +
              "</Button>" +
            "</StackPanel>");

        var outer = Assert.IsType<Style>(root.Resources["K"]);
        var button = Assert.IsType<UIControls.Button>(root.Children[0]);
        var derived = Assert.IsType<Style>(button.Resources["K"]);

        Assert.NotSame(outer, derived);        // the inner K is its OWN style...
        Assert.Same(outer, derived.BasedOn);   // ...whose BasedOn skipped self and resolved the OUTER K
    }

    [Fact] // With NO outer same-keyed style, a self-BasedOn is a runtime miss — NOT the old CD6 self-cycle throw.
    public void SelfKeyBasedOn_NoOuter_IsRuntimeMiss_NotCycle()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<Style x:Key=\"{x:Type Button}\" TargetType=\"Button\" BasedOn=\"{StaticResource {x:Type Button}}\">" +
            "<Setter Property=\"Width\" Value=\"7\"/></Style></ResourceDictionary>");

        // Realizing the entry resolves BasedOn against the OUTER scope (skipping the self slot). With no outer the
        // base is simply absent — the point is it is NO LONGER the CD6 self-cycle throw it used to be.
        var style = Assert.IsType<Style>(dict[typeof(UIControls.Button)]); // realizes WITHOUT a cycle error
        Assert.NotSame(style, style.BasedOn);                              // not itself (no outer → null, not self)
    }
}
