using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;
using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis;

using System.Text;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The kitchen-sink drift gate: ONE document exercising every construct the closed-set sweep must
/// cover — styles with TargetType/Selector/BasedOn <c>{StaticResource {x:Type …}}</c>, a merged
/// <c>ResourceDictionary Source=</c> (through the thread-current loader), a ControlTemplate with
/// TemplateBinding + TemplatedParent bindings, <c>{x:Static}</c>, <c>{DynamicResource}</c>,
/// <c>palette()</c> brushes, dotted attached properties, deferred DataTemplates with
/// <c>DataType="{x:Type …}"</c>, and <c>x:Name</c>/<c>x:Reference</c> — rendered in a headless host
/// under the GENERATED provider and under REFLECTION, asserting BYTE-IDENTICAL frames. Catches
/// closed-set collection gaps and emitted-provider parity regressions in one sweep; grow the
/// document when a new construct ships (a real-project drift found by probing Cursorial.Samples is
/// a construct this document is missing).
/// </summary>
public sealed class KitchenSinkDriftTests
{
    private const string InnerUri = "cursorial://KitchenSinkAsm/Themes/Inner.xaml";

    private const string InnerXaml =
        """
        <ResourceDictionary xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
          <SolidColorBrush x:Key="AccentBrush" Color="#3050C0"/>
          <Style x:Key="SinkExpanderTheme" Selector="Expander">
            <Setter Property="Expander.Template">
              <ControlTemplate TargetType="Expander">
                <DockPanel LastChildFill="True">
                  <ToggleButton x:Name="PART_Header" DockPanel.Dock="Top" Content="{TemplateBinding Header}" Padding="0"
                                IsChecked="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=IsExpanded, Mode=TwoWay}"/>
                  <Border x:Name="PART_Content" Background="{DynamicResource {x:Static ThemeKeys.ElevationWindow}}">
                    <ContentPresenter/>
                  </Border>
                </DockPanel>
              </ControlTemplate>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;

    private const string SinkXaml =
        $$$"""
        <Border xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
                xmlns:d="https://cursorial.dev/xaml/design" BorderPen="{x:Null}">
          <Border.Styles>
            <Style TargetType="ListBoxItem" Selector="ListBoxItem" BasedOn="{StaticResource {x:Type ListBoxItem}}">
              <Setter Property="Control.Padding" Value="0"/>
            </Style>
          </Border.Styles>
          <Border.Resources>
            <ResourceDictionary Source="{{{InnerUri}}}"/>
          </Border.Resources>
          <Expander Header="Sink" IsExpanded="True" Theme="{StaticResource SinkExpanderTheme}">
            <Grid>
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
              </Grid.RowDefinitions>
              <WrapPanel Grid.Row="0" Orientation="Horizontal">
                <Border Width="2" Height="1" Background="palette(3)" DataContext="{Icon Text='K'}"/>
                <Border Width="2" Height="1" Background="{StaticResource AccentBrush}"/>
                <Label Content="_Go:" Target="{x:Reference SinkList}"/>
              </WrapPanel>
              <DockPanel Grid.Row="1" LastChildFill="True">
                <TextBlock DockPanel.Dock="Bottom" Text="{Binding Length}"
                           Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"/>
                <ListBox x:Name="SinkList" ScrollViewer.VerticalScrollBarVisibility="Visible">
                  <ListBoxItem Content="alpha"/>
                  <ListBoxItem Content="beta"/>
                </ListBox>
              </DockPanel>
            </Grid>
          </Expander>
        </Border>
        """;

    [Fact]
    public void KitchenSink_RendersIdenticallyUnderGeneratedAndReflectionProviders()
    {
        var generated = BuildGeneratedProvider(SinkXaml, InnerXaml);

        using var _ = XamlModule.UseResourceProvider(new InMemoryResourceProvider((InnerUri, InnerXaml)));

        var byGenerated = RenderFrame(generated);
        var byReflection = RenderFrame(ReflectionXamlMetadata.Instance);

        Assert.False(byGenerated.StartsWith("EXCEPTION", StringComparison.Ordinal), byGenerated);
        Assert.Contains("IsBlank = False", byGenerated); // guards against a trivially-blank pass
        Assert.Equal(byReflection, byGenerated);         // byte-identical frames — zero provider drift
    }

    /// <summary>Builds the generated provider EXACTLY as production does: the union of element names,
    /// load-time type references, and baked statics over ALL documents (outer + merged).</summary>
    private static IXamlTypeMetadataProvider BuildGeneratedProvider(params string[] documents)
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var resolver = new XamlSymbolResolver(compilation);

        var names = new HashSet<(string Namespace, string LocalName)>();
        foreach (var text in documents)
        {
            foreach (var name in ClosedTypeSet.CollectElementNames(text))
                names.Add(name);
            foreach (var name in ClosedTypeSet.CollectTypeReferenceNames(text))
                names.Add(name);
        }

        // Extension names resolve suffix-first, parser parity — exactly as production EmitProvider does.
        var extensionTypes = new List<INamedTypeSymbol>();
        foreach (var text in documents)
        {
            foreach (var (ns, local) in ClosedTypeSet.CollectMarkupExtensionNames(text))
            {
                var symbol = resolver.Resolve(ns, local + "Extension", out _) ?? resolver.Resolve(ns, local, out _);
                if (symbol is not null)
                    extensionTypes.Add(symbol);
            }
        }

        var types = names
            .Select(n => resolver.Resolve(n.Namespace, n.LocalName, out _))
            .Where(static t => t is not null)
            .Cast<INamedTypeSymbol>()
            .Concat(extensionTypes)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToList();

        var statics = ClosedTypeSet.CollectStatics(resolver, documents);
        var source = new MetadataProviderEmitter(compilation).Emit(types, statics)
            ?? throw new InvalidOperationException("no provider emitted");
        return GeneratorHarness.CompileAndLoadProvider(source);
    }

    private static string RenderFrame(IXamlTypeMetadataProvider provider)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        try
        {
            Exception? frameException = null;
            host.Application.DispatcherUnhandledException += (_, args) => { args.Handled = true; frameException = args.Exception; };

            var loader = new XamlLoader(new XamlLoaderOptions { MetadataProvider = provider });

            UIElement root;
            try
            {
                root = (UIElement)loader.Load(SinkXaml, new Uri("cursorial://KitchenSinkAsm/Sink.xaml"));
            }
            catch (Exception ex)
            {
                return $"EXCEPTION (load): {ex.GetBaseException()}";
            }

            root.SetValue(UIElement.DataContextProperty, "sink");
            host.ShowRoot(root);
            if (!host.RunUntilIdle(maxFrames: 20))
            {
                for (var i = 0; i < 150 && !host.RunUntilIdle(maxFrames: 5); i++)
                    host.AdvanceTime(host.Options.FrameInterval);
            }

            if (frameException is not null)
                return $"EXCEPTION (frame): {frameException.GetBaseException()}";

            var buffer = host.FrameBuffer;
            var sb = new StringBuilder();
            for (var row = 0; row < buffer.Rows; row++)
            {
                for (var col = 0; col < buffer.Columns; col++)
                    sb.Append(buffer[col, row].ToString());
                sb.Append('\n');
            }

            return sb.ToString();
        }
        finally
        {
            host.Dispose();
        }
    }

    private sealed class InMemoryResourceProvider(params (string Uri, string Xaml)[] entries) : IXamlResourceProvider
    {
        public bool TryGetXaml(Uri uri, out string? xaml)
        {
            foreach (var (key, text) in entries)
            {
                if (string.Equals(key, uri.OriginalString, StringComparison.OrdinalIgnoreCase))
                {
                    xaml = text;
                    return true;
                }
            }

            xaml = null;
            return false;
        }
    }
}
