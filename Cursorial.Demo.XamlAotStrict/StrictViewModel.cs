using Cursorial.UI.Data;

namespace Cursorial.Demo.XamlAotStrict;

public class StrictViewModel : ObservableObject
{
    public StrictViewModel()
    {
        Text = "Loaded via the generated provider.";
    }

    public string Text
    {
        get;
        set => SetProperty(ref field, value);
    }
}