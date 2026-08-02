using System.ComponentModel;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Stage-2 (loader) coverage for a binding-valued <c>Setter.Value</c> (ledger B15). <c>{Binding …}</c>
/// in a setter loads as an unattached <see cref="Binding"/> DESCRIPTOR — the same shape a
/// <c>Binding</c>-typed member such as <c>DataCondition.Binding</c> receives — which the styling engine
/// then installs once per styled element, frame-hosted. The XAML twin of code-first
/// <c>Style.SetBinding</c>.
/// </summary>
public sealed class Section17_BindingSetters : LoaderTestBase
{
    [Fact] // the setter loads as a descriptor, NOT an installed binding and NOT a converted constant
    public void Binding_setter_value_loads_as_a_descriptor()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Setter Property="MinWidth" Value="{Binding Width}"/>
            </Style>
            """);

        var setter = Assert.Single(style.Setters);
        var binding = Assert.IsType<Binding>(setter.Value);
        Assert.Equal("Width", binding.Path.ToString());
    }

    [Fact] // end-to-end: the loaded descriptor installs per element and tracks the source
    public void Binding_setter_applies_and_tracks_the_source()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Setter Property="MinWidth" Value="{Binding Width}"/>
            </Style>
            """);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);

        var vm = new SizeVm { Width = 20 };
        var button = new Button { DataContext = vm };
        host.ShowRoot(button);
        host.RunUntilIdle();

        Assert.Equal(20d, button.MinWidth);

        vm.Width = 35;
        host.RunUntilIdle();
        Assert.Equal(35d, button.MinWidth);
    }

    [Fact] // one authored descriptor, one expression per matched element — the values do not bleed
    public void Binding_setter_installs_independently_per_element()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Setter Property="MinWidth" Value="{Binding Width}"/>
            </Style>
            """);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);

        var left = new Button { DataContext = new SizeVm { Width = 20 } };
        var right = new Button { DataContext = new SizeVm { Width = 35 } };
        var panel = new StackPanel();
        panel.Children.Add(left);
        panel.Children.Add(right);

        host.ShowRoot(panel);
        host.RunUntilIdle();

        Assert.Equal(20d, left.MinWidth);
        Assert.Equal(35d, right.MinWidth);
    }

    private sealed class SizeVm : INotifyPropertyChanged
    {
        private double _width;

        public double Width
        {
            get => _width;
            set
            {
                _width = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
