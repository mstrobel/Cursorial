namespace Cursorial.Demo.XamlAotStrict;

/// <summary>
/// The code-behind half of the strict-AOT view. <c>InitializeComponent</c> is generated (X4.6) and binds to this
/// assembly's own generated metadata provider — no reflection. The typed <c>Ok</c>/<c>Label</c> x:Name fields are
/// generated too. The BASE TYPE comes from the generated half (the XAML root element), so changing
/// the root element is a one-place edit.
/// </summary>
public partial class StrictView
{
    public StrictView() => InitializeComponent();
}
