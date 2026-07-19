using System.ComponentModel;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Stage-2 (loader) coverage for declaring a <see cref="Style.When"/> conjunction in XAML: a
/// <c>&lt;Style.When&gt;</c> collection of <see cref="DataCondition"/>s, each with a
/// <c>Binding="{Binding …}"</c> descriptor and a typed <c>Value</c> (attribute or
/// <c>&lt;DataCondition.Value&gt;</c> element). The XAML twin of the code-first
/// <c>Style { When = { new DataCondition(...) } }</c>.
/// </summary>
public sealed class Section15_StyleWhen : LoaderTestBase
{
    [Fact]
    public void When_element_value_via_typed_element()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Style.When>
                <DataCondition Binding="{Binding IsSpecial}">
                  <DataCondition.Value><x:Boolean>true</x:Boolean></DataCondition.Value>
                </DataCondition>
              </Style.When>
              <Setter Property="TextElement.Foreground" Value="Red"/>
            </Style>
            """);

        Assert.Single(style.When);
        var condition = style.When[0];
        Assert.IsType<Binding>(condition.Binding);
        Assert.Equal("IsSpecial", ((Binding)condition.Binding).Path.Path);
        Assert.Equal(true, condition.Value);
        Assert.False(condition.Negate);
    }

    [Fact]
    public void When_value_attribute_xnull_and_negate()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Style.When>
                <DataCondition Binding="{Binding Foo}" Value="{x:Null}" Negate="True"/>
              </Style.When>
              <Setter Property="TextElement.Foreground" Value="Red"/>
            </Style>
            """);

        Assert.Single(style.When);
        var condition = style.When[0];
        Assert.Null(condition.Value);
        Assert.True(condition.Negate);
    }

    [Fact]
    public void When_multiple_conditions_conjunction()
    {
        // Mirrors the code-first DatePicker When (ControlThemes.cs) / its Controls.xaml twin.
        var style = Load<Style>("""
            <Style TargetType="DatePicker">
              <Style.When>
                <DataCondition Binding="{Binding RelativeSource={RelativeSource Self}, Path=(DatePicker.IsEditable)}">
                  <DataCondition.Value><x:Boolean>false</x:Boolean></DataCondition.Value>
                </DataCondition>
                <DataCondition Binding="{Binding RelativeSource={RelativeSource Self}, Path=(DatePicker.SelectedDate)}" Value="{x:Null}"/>
              </Style.When>
              <Setter Property="TextElement.Foreground" Value="Red"/>
            </Style>
            """);

        Assert.Equal(2, style.When.Count);
        Assert.Equal(false, style.When[0].Value);
        Assert.Null(style.When[1].Value);
    }

    [Fact] // end-to-end: the LOADED Binding descriptor arms a watch and the When conjunction gates the whole style.
    public void When_gates_style_at_runtime()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Style.When>
                <DataCondition Binding="{Binding IsSpecial}">
                  <DataCondition.Value><x:Boolean>true</x:Boolean></DataCondition.Value>
                </DataCondition>
              </Style.When>
              <Setter Property="MinWidth" Value="20"/>
            </Style>
            """);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);
        var vm = new WhenProbeVm { IsSpecial = false };
        var button = new Button { DataContext = vm };
        host.ShowRoot(button);
        host.RunUntilIdle();

        // Condition unmet ⇒ the guarded setter is inert (MinWidth stays at its default 0).
        Assert.Equal(0d, button.MinWidth);

        // Meeting the condition activates the whole style through the armed watch ⇒ the setter applies.
        vm.IsSpecial = true;
        host.RunUntilIdle();
        Assert.Equal(20d, button.MinWidth);

        // Un-meeting it deactivates and the store promotes the base value back.
        vm.IsSpecial = false;
        host.RunUntilIdle();
        Assert.Equal(0d, button.MinWidth);
    }

    [Fact] // a genuine-null binding delivery satisfies a Value="{x:Null}" condition (the WhenConditionRequirement
           // null-vs-unset fix): a RESOLVED-to-null path is met, distinct from an UNRESOLVED path (which is unmet).
    public void When_null_delivery_matches_value_null()
    {
        var style = Load<Style>("""
            <Style TargetType="Button">
              <Style.When>
                <DataCondition Binding="{Binding Tag}" Value="{x:Null}"/>
              </Style.When>
              <Setter Property="MinWidth" Value="20"/>
            </Style>
            """);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);
        var vm = new WhenProbeVm { Tag = null };       // the bound property resolves to null (delivers null, not unset)
        var button = new Button { DataContext = vm };
        host.ShowRoot(button);
        host.RunUntilIdle();

        // Tag == null ⇒ the Value=null condition is MET ⇒ the guarded setter applies. (Under the pre-fix
        // `_watch?.Value ?? UnsetValue`, a null delivery collapses to UnsetValue and can never match — MinWidth 0.)
        Assert.Equal(20d, button.MinWidth);

        // A non-null delivery no longer equals null ⇒ deactivate.
        vm.Tag = "x";
        host.RunUntilIdle();
        Assert.Equal(0d, button.MinWidth);

        vm.Tag = null;
        host.RunUntilIdle();
        Assert.Equal(20d, button.MinWidth);
    }

    [Fact] // a <DataCondition> with no Binding is rejected at load with a positioned diagnostic — not an opaque
           // ArgumentNullException deep in style arming (the Xaml init lane bypasses the ctor's reflection-lane guard).
    public void When_missing_Binding_is_rejected_at_load()
    {
        var ex = ThrowsLoad(XamlDiagnosticCodes.MemberNotFound, () => Load<Style>("""
            <Style TargetType="Button">
              <Style.When>
                <DataCondition Value="true"/>
              </Style.When>
              <Setter Property="MinWidth" Value="20"/>
            </Style>
            """));
        Assert.Contains("DataCondition", ex.Message);
    }

    private sealed class WhenProbeVm : INotifyPropertyChanged
    {
        private bool _isSpecial;
        private string? _tag;

        public bool IsSpecial
        {
            get => _isSpecial;
            set
            {
                _isSpecial = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpecial)));
            }
        }

        public string? Tag
        {
            get => _tag;
            set
            {
                _tag = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tag)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
