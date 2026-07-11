using Cursorial.UI.Controls;

namespace CursorialApp.Views;

/// <summary>
/// The shell view. <c>InitializeComponent</c> and the typed <c>CountButton</c> field are
/// generated from MainView.xaml by the Cursorial XAML source generator.
/// </summary>
public partial class MainView : DockPanel
{
    private int _count;

    public MainView()
    {
        InitializeComponent();
        CountButton.Click += (_, _) => CountButton.Content = $"Clicked {++_count}×";
    }
}
