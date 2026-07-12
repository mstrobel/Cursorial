namespace CursorialApp.Views;

/// <summary>
/// The shell view. <c>InitializeComponent</c>, the typed <c>CountButton</c> field, and the
/// base type (the XAML root element) are generated from MainView.xaml by the Cursorial XAML
/// source generator — changing the root element needs no edit here.
/// </summary>
public partial class MainView
{
    private int _count;

    public MainView()
    {
        InitializeComponent();
        CountButton.Click += (_, _) => CountButton.Content = $"Clicked {++_count}×";
    }
}
