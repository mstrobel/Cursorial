using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text; // Margins
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

using CS = Cursorial.Output.Style;

// P9.8 — the XAML style-inspector (design doc §3.9 / proposal §2.8 "style inspector overlay"): load a .xaml
// file, render it on the real frame loop, then inspect the element under the cursor — for it, the overlay
// lists every property with a live, non-default contribution (UIObject.GetSetProperties) and its full
// provenance (StyleDiagnostics.Explain: value ← layer/lane + selector + the packed sort key, winning vs
// shadowed). "Debuggability made concrete": the report is exact and cheap because the styling slots are
// objects, not a re-run of a matching algorithm.
//   'o'      open a XAML source (a bundled sample from the list, or a file path you type),
//   hover    inspect the element under the pointer (motion terminals),
//   Tab      move focus through the loaded tree — the focused element is inspected (keyboard fallback),
//   't'/'d'  cycle the color tier / flip dark-light (the loaded tree re-skins; the inspector re-reads),
//   q / Esc  exit.
internal sealed class InspectorDemo : IDemo
{
    public string Name => "inspect";
    public IReadOnlyList<string> Aliases => ["inspector", "xaml"];

    public string Description =>
        "XAML style-inspector (P9.8): open a .xaml (bundled sample or a file path), render it, then hover/Tab an " +
        "element to see every non-default property and its provenance — value ← slot + selector + sort key (StyleDiagnostics.Explain).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("XAML inspector. Opening alt screen — press 'o' to open a XAML source (a bundled sample " +
                          "or a file path), then HOVER or Tab to an element to inspect its non-default properties and " +
                          "their provenance (value ← slot + selector). 't' cycles the color tier, 'd' flips dark/light; " +
                          "q / Esc exits.");

        var app = UIApplication.DefaultBuilder().Build();
        // app.Theme = Cursorial.UI.Themes.IndigoDusk.IndigoDuskTheme.LoadTheme();
        app.NerdFontAvailable = true;
        var controller = new Controller(app);

        app.Started += (_, _) => controller.OpenDialog();
        
        try
        {
            await app.RunAsync(controller.BuildDesktop);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // Bundled samples — declarative trees with a mix of LOCAL values (Width, explicit Background/BorderPen),
    // THEMED contributions (the control themes' SurfaceBrush/well/etc.), and INHERITED foreground, so the
    // inspector has rich provenance to show. The default xmlns maps the UI/Controls/Drawing.Media types.
    private static readonly (string Label, string Xaml)[] Samples =
    [
        ("Buttons Demo", """
                      <DockPanel xmlns="https://cursorial.dev/ui"
                                 xmlns:x="https://cursorial.dev/xaml">
                        <Expander DockPanel.Dock="Bottom">
                          <Border Height="5" />
                        </Expander>
                        <Grid Margin="2,1">
                          <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="2" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="4" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="2" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                          </Grid.ColumnDefinitions>
                          <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="1" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                          </Grid.RowDefinitions>
                          
                          <Button        Grid.Column="0" Grid.Row="0" Content="_Cancel" MinWidth="10"
                                         Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock     Grid.Column="2" Grid.Row="0" Text="normal" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button        Grid.Column="0" Grid.Row="2" Content="_Accept" MinWidth="10" IsDefault="true"
                                         Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock     Grid.Column="2" Grid.Row="2" Text=":default" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button        Grid.Column="0" Grid.Row="4" Content="Disabled" MinWidth="10" IsEnabled="false"
                                         Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock     Grid.Column="2" Grid.Row="4" Text=":disabled" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <ToggleButton Grid.Column="0" Grid.Row="6" Content="To_ggle" MinWidth="10" Classes="toggle-colors"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="2" Grid.Row="6" Text="toggle" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <ToggleButton Grid.Column="0" Grid.Row="8" Content="To_ggle 2" MinWidth="10" IsThreeState="True" Classes="toggle-colors"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="2" Grid.Row="8" Text="3-state toggle" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <RepeatButton Grid.Column="0" Grid.Row="10" Content="_Repeat" MinWidth="10"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="2" Grid.Row="10" Text="repeat (hold)" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="0" Content="_Engage" MinWidth="10" Classes="accent"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="0" Text=".accent" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="2" Content="_Sync" MinWidth="10" Classes="cool"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="2" Text=".cool" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="4" Content="_Delete" MinWidth="10" Classes="danger"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="4" Text=".danger" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="6" Content="Den_y" MinWidth="10" Classes="warning"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="6" Text=".warning" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="8" Content="In_fo" MinWidth="10" Classes="info"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="8" Text=".info" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                          
                          <Button       Grid.Column="4" Grid.Row="10" Content="Fi_nish" MinWidth="10" Classes="success"
                                        Command="{Binding ClickCommand}" CommandParameter="{Binding Content, RelativeSource={RelativeSource Self}}" />
                          <TextBlock    Grid.Column="6" Grid.Row="10" Text=".success" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                        </Grid>
                      </DockPanel>
                      """),
        ("Tabs Demo", """
                      <TabControl xmlns="https://cursorial.dev/ui"
                                  xmlns:x="https://cursorial.dev/xaml"
                                  Margin="2,1">
                        <TabItem Header="F_irst Tab">
                          <TabItem.Content>
                            <Grid>
                              <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="1" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                              </Grid.ColumnDefinitions>
                              <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="*" />
                              </Grid.RowDefinitions>
                      
                              <Label Grid.Column="0" Grid.Row="0" Content="Te_xt Input" Target="{x:Reference TextInput}" />
                              <TextBox x:Name="TextInput" Grid.Column="1" Grid.Row="0" HorizontalAlignment="Left" Width="28" />
                              <CheckBox Grid.Column="2" Grid.Row="1" HorizontalAlignment="Left" Content="_Reveal password" Margin="0,1,0,0" />
                              <CheckBox Grid.Column="2" Grid.Row="2" HorizontalAlignment="Left" Content="Subscribe to _updates" />
                            </Grid>
                          </TabItem.Content>
                        </TabItem>
                        <TabItem Header="_Second Tab">
                          <Border/>
                        </TabItem>
                        <TabItem Header="T_hird Tab">
                          <Border/>
                        </TabItem>
                      </TabControl>
                      """),
        ("Inputs demo", """
                           <DockPanel xmlns="https://cursorial.dev/ui"
                                      xmlns:x="https://cursorial.dev/xaml"
                                      LastChildFill="True">
                             <Border DockPanel.Dock="Top" Background="{DynamicResource {x:Static ThemeKeys.ElevationWindow}}" Padding="1,0">
                               <StackPanel Orientation="Vertical">
                                 <TextBlock Text="{Binding Title}"
                                            Foreground="{DynamicResource {x:Static ThemeKeys.TextDimBrush}}" />
                                 <TextBlock Text="{Binding Summary}"
                                            Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                               </StackPanel>
                             </Border>
                             
                             <StatusBar DockPanel.Dock="Bottom">
                               <StatusBarItem Classes="alternate" Content="status" />
                               <StatusBarItem Content="This is a status message" />
                             </StatusBar>
                             
                           <Grid Margin="2,1">
                             <Grid.ColumnDefinitions>
                               <ColumnDefinition Width="Auto" />
                               <ColumnDefinition Width="1" />
                               <ColumnDefinition Width="Auto" />
                               <ColumnDefinition Width="*" />
                             </Grid.ColumnDefinitions>
                             <Grid.RowDefinitions>
                               <RowDefinition Height="Auto" />
                               <RowDefinition Height="Auto" />
                               <RowDefinition Height="Auto" />
                               <RowDefinition Height="Auto" />
                               <RowDefinition Height="Auto" />
                               <RowDefinition Height="*" />
                             </Grid.RowDefinitions>
                           
                             <Label Grid.Column="0" Grid.Row="0"
                                    Content="_Name" Target="{x:Reference NameEditor}" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                             <TextBox Grid.Column="2" Grid.Row="0" HorizontalAlignment="Left"
                                      x:Name="NameEditor" Width="28" Text="{Binding Name, Mode=TwoWay}" />
                           
                             <Label Grid.Column="0" Grid.Row="1"
                                    Target="{x:Reference PasswordEditor}" Content="_Password" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                             <PasswordBox Grid.Column="2" Grid.Row="1" HorizontalAlignment="Left"
                                          x:Name="PasswordEditor" Width="28" Text="{Binding Password, Mode=TwoWay}" RevealPassword="{Binding ShowPassword}" />
                           
                             <CheckBox Grid.Column="2" Grid.Row="2" HorizontalAlignment="Left"
                                       Content="_Reveal password" Margin="0,1,0,0" IsChecked="{Binding ShowPassword, Mode=TwoWay}" />
                             <CheckBox Grid.Column="2" Grid.Row="3" HorizontalAlignment="Left"
                                       Content="Subscribe to _updates" IsChecked="{Binding Subscribed, Mode=TwoWay}" />
                           
                             <Label Grid.Column="0" Grid.Row="4"
                                    Target="{x:Reference VolumeSlider}" Margin="0,1,0,0" Content="_Volume" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                             <StackPanel Grid.Column="2" Grid.Row="4" HorizontalAlignment="Left"
                                         Orientation="Horizontal" Margin="0,1,0,0">
                               <Slider x:Name="VolumeSlider" Width="28" Minimum="0" Maximum="100" Value="{Binding Volume, Mode=TwoWay}" />
                               <TextBlock Margin="1,0,0,0" Text="{Binding ElementName=VolumeSlider, Path=Value, StringFormat='{0:N0}\%'}"
                                          TextAlignment="Left" Foreground="{DynamicResource {x:Static ThemeKeys.FaintBrush}}" />
                             </StackPanel>
                           </Grid>
                           
                           </DockPanel>
                           """),
        ("Ribbon demo", """
                           <DockPanel xmlns="https://cursorial.dev/ui"
                                      xmlns:x="https://cursorial.dev/xaml"
                                      xmlns:bars="clr-namespace:Cursorial.UI.Bars;assembly=Cursorial.UI.Bars"
                                      LastChildFill="True">
                             <Border DockPanel.Dock="Top" Background="{DynamicResource {x:Static ThemeKeys.ElevationHighest}}" Padding="1,0">
                               <StackPanel Orientation="Vertical">
                                 <TextBlock Text="{Binding Title}" Foreground="{DynamicResource {x:Static ThemeKeys.TextDimBrush}}" />
                                 <TextBlock Text="{Binding Summary}" Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                               </StackPanel>
                             </Border>
                           
                             <bars:Ribbon DockPanel.Dock="Top" Margin="2,1">
                              <bars:RibbonTab IsFileTab="True">
                                 <bars:RibbonTab.Header>
                                   <StackPanel x:Name="FileTabContent" Orientation="Horizontal"
                                               TextElement.Inverse="{Binding (TextElement.Inverse), RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type bars:RibbonTab}}}">
                                     <Icon Glyph="&#xf035c;" Text="≡" Margin="0,0,1,0"
                                           TextElement.Inverse="{Binding (TextElement.Inverse), ElementName=FileTabContent}" />
                                     <ContentPresenter Content="_File" RecognizesAccessKey="True"
                                                       TextElement.Inverse="{Binding (TextElement.Inverse), ElementName=FileTabContent}" />
                                   </StackPanel>
                                 </bars:RibbonTab.Header>
                               </bars:RibbonTab>
                               <bars:RibbonTab Header="_Home">
                                 <bars:RibbonGroup Header="Clipboard" HasDialogLauncher="True">
                                   <bars:BarButton Content="Paste" bars:Ribbon.ButtonSize="Large" Icon="{Icon Glyph='&#x000F0192;', Emoji='📋', Text='▤'}" />
                                   <bars:RibbonControlGroup >
                                     <bars:BarButton Content="Cut"  Icon="{Icon Glyph='&#x000F0190;', Emoji='🗒', Text='✁'}"/>
                                     <bars:BarButton Content="Copy" Icon="{Icon Glyph='&#x000F018F;', Emoji='📋', Text='⧉'}" />
                                   </bars:RibbonControlGroup>
                                 </bars:RibbonGroup>
                                 <bars:RibbonGroup Header="Format">
                                   <bars:RibbonControlGroup>
                                     <bars:BarToggleButton Content="Bold"   Icon="{Icon Glyph='&#x000F0264;', Emoji='🅱', Text='✱'}"  />
                                     <bars:BarToggleButton Content="Italic" Icon="{Icon Glyph='&#x000F0277;', Emoji='✍️', Text='⟋'}" />
                                     <bars:BarToggleButton Content="Code"   Icon="{Icon Glyph='&#x000F0174;', Emoji='💻', Text='{'}" />
                                     <bars:BarToggleButton Content="Left"   Icon="{Icon Glyph='&#x000F0262;', Emoji='⬅️', Text='⇤'}" bars:RibbonControlGroup.RowBreak="True" />
                                     <bars:BarToggleButton Content="Center" Icon="{Icon Glyph='&#x000F0260;', Emoji='↔️', Text='↹'}" />
                                     <bars:BarToggleButton Content="Right"  Icon="{Icon Glyph='&#x000F0263;', Emoji='➡️', Text='⇥'}" />
                                   </bars:RibbonControlGroup>
                                 </bars:RibbonGroup>
                                 <bars:RibbonGroup Header="Editing">
                                   <bars:BarButton Content="Find" bars:Ribbon.ButtonSize="Large" />
                                 </bars:RibbonGroup>
                               </bars:RibbonTab>
                               <bars:RibbonTab Header="Inse_rt">
                                 <bars:RibbonGroup Header="History">
                                   <bars:BarButton Content="Undo" />
                                   <bars:BarButton Content="Redo" />
                                   <bars:BarButton Content="Settings" />
                                 </bars:RibbonGroup>
                               </bars:RibbonTab>
                               
                               <!-- P3a: a CONTEXTUAL tab — purple-tinted, shown only when a "table" is selected. Its Visibility binds to
                                    the VM; when it's the active tab and hides, the Ribbon falls back to the first content tab (no blank band). -->
                               <bars:RibbonTab Header="Table" IsContextual="True"
                                               Visibility="{Binding TableToolsVisibility}">
                                 <bars:RibbonGroup Header="Cells">
                                   <bars:BarButton Content="Merge Cells" bars:Ribbon.ButtonSize="Large" />
                                   <bars:BarButton Content="Split Cells" />
                                 </bars:RibbonGroup>
                                 <bars:RibbonGroup Header="Table">
                                   <bars:BarButton Content="Delete Table" />
                                 </bars:RibbonGroup>
                               </bars:RibbonTab>
                               
                             </bars:Ribbon>
                           
                             <Border Padding="2,1" Background="{DynamicResource {x:Static ThemeKeys.ElevationDesktop}}">
                               <StackPanel Orientation="Vertical" VerticalAlignment="Center" HorizontalAlignment="Center">
                                 <TextBlock Text="{Binding Status}" HorizontalAlignment="Center"
                                            Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush}}" />
                                 <!-- Toggle the contextual tab: check to select a "table" (the purple Table tab appears), uncheck to hide it. -->
                                 <CheckBox Content="_Table selected (show the contextual Table tab)" Margin="0,1,0,0"
                                           IsChecked="{Binding TableSelected, Mode=TwoWay}" HorizontalAlignment="Center" />
                               </StackPanel>
                             </Border>
                           </DockPanel>
                           """),
        ("Settings panel", """
                           <DockPanel xmlns="https://cursorial.dev/ui"
                                      xmlns:x="https://cursorial.dev/xaml"
                                      Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                      TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}">
                               <TextBlock DockPanel.Dock="Top" Text=" Settings " Margin="1,0"/>
                               <StackPanel Margin="2,1" Spacing="1">
                                   <CheckBox Content="_Airplane mode"/>
                                   <CheckBox Content="_Wi-Fi" IsChecked="True"/>
                                   <CheckBox Content="_Bluetooth"/>
                                   <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                                       <Button Content="_OK" IsDefault="True" Width="10"/>
                                       <Button Content="_Cancel"/>
                                   </StackPanel>
                               </StackPanel>
                           </DockPanel>
                           """),
        ("Login form", """
                       <StackPanel xmlns="https://cursorial.dev/ui"
                                   xmlns:x="https://cursorial.dev/xaml"
                                   Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                   TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                   Margin="2,1" Spacing="1">
                           <Menu Margin="2,1" DockPanel.Dock="Top">
                             <MenuItem Header="_File">
                               <MenuItem Header="_New"  InputGestureText="Ctrl+N" Icon="📄" />
                               <MenuItem Header="_Open" InputGestureText="Ctrl+O" Icon="📂" />
                               <MenuItem Header="_Save" InputGestureText="Ctrl+S" Icon="💾" />
                               <Separator/>
                               <MenuItem Header="E_xit" InputGestureText="Alt+Q" Icon="❌" />
                             </MenuItem>
                             <MenuItem Header="_Edit">
                               <MenuItem Header="Cu_t"   InputGestureText="Ctrl+X" Icon="{Icon Emoji='✂️'}" />
                               <MenuItem Header="_Copy"  InputGestureText="Ctrl+C" Icon="{Icon Emoji='📑'}" />
                               <MenuItem Header="_Paste" InputGestureText="Ctrl+V" Icon="{Icon Emoji='📋'}" />
                               <Separator/>
                               <MenuItem Header="F_ind">
                                 <MenuItem Header="Find _Next"     InputGestureText="F3" />
                                 <MenuItem Header="Find _Previous" InputGestureText="Shift+F3" />
                               </MenuItem>
                             </MenuItem>
                             <MenuItem Header="_View">
                               <MenuItem Header="F_ull Screen"      InputGestureText="Alt+Enter" Icon="🖥️" />
                               <MenuItem Header="_Hide Sidebar"     InputGestureText="Shift+F3" />
                               <MenuItem Header="Hide _Diagnostics" InputGestureText="Shift+F3" />
                             </MenuItem>
                           </Menu>
                           <TextBlock Text="Sign in"/>
                           <Label Content="User _name:"/>
                           <TextBox Placeholder="文文文文文文文文" Width="24"/>
                           <Label Content="_Password:"/>
                           <TextBox Placeholder="••••••••" Width="24"/>
                           <CheckBox Content="_Remember me"/>
                           <ComboBox HorizontalAlignment="Left">
                               <ComboBox.ItemsSource>
                                 <x:Array Type="x:String">
                                     <x:String>Item 1</x:String>
                                     <x:String>Item 2</x:String>
                                     <x:String>Item 3</x:String>
                                 </x:Array>
                               </ComboBox.ItemsSource>
                           </ComboBox>
                           <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                               <Button Content="_Sign in" IsDefault="True"/>
                               <Button Content="_Cancel"/>
                           </StackPanel>
                       </StackPanel>
                       """),
        ("Tree view", """
                      <StackPanel xmlns="https://cursorial.dev/ui"
                                  xmlns:x="https://cursorial.dev/xaml"
                                  Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                  TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                  Margin="2,1" Spacing="1">
                          <TreeView>
                              <TreeViewItem Header="Item 1">
                                  <TreeViewItem Header="Item 1.1" />
                                  <TreeViewItem Header="Item 1.2" />
                              </TreeViewItem>
                              <TreeViewItem Header="Item 2" />
                              <TreeViewItem Header="Item 3" />
                              <TreeViewItem Header="Item 4">
                                  <TreeViewItem Header="Item 4.1">
                                      <TreeViewItem Header="Item 4.1.1" />
                                      <TreeViewItem Header="Item 4.1.2" />
                                  </TreeViewItem>
                                  <TreeViewItem Header="Item 4.2">
                                      <TreeViewItem Header="Item 4.2.1" />
                                      <TreeViewItem Header="Item 4.2.2" />
                                  </TreeViewItem>
                              </TreeViewItem>
                          </TreeView>
                      </StackPanel>
                      """),
        ("List view", """
                      <StackPanel xmlns="https://cursorial.dev/ui"
                                  xmlns:x="https://cursorial.dev/xaml"
                                  Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                                  TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                                  Margin="2,1" Spacing="1">
                          <ListBox SelectionMode="Single" VirtualizingPanel.IsVirtualizing="True" MaxHeight="6">
                              <ListBoxItem Content="alpha" />
                              <ListBoxItem Content="bravo" />
                              <ListBoxItem Content="charley" />
                              <ListBoxItem Content="delta" />
                              <ListBoxItem Content="echo" />
                              <ListBoxItem Content="foxtrot" />
                              <ListBoxItem Content="golf" />
                              <ListBoxItem Content="hotel" />
                              <ListBoxItem Content="india" />
                              <ListBoxItem Content="juliett" />
                              <ListBoxItem Content="kilo" />
                              <ListBoxItem Content="lima" />
                              <ListBoxItem Content="mike" />
                              <ListBoxItem Content="november" />
                              <ListBoxItem Content="oscar" />
                              <ListBoxItem Content="papa" />
                              <ListBoxItem Content="quebec" />
                              <ListBoxItem Content="romeo" />
                              <ListBoxItem Content="sierra" />
                              <ListBoxItem Content="tango" />
                              <ListBoxItem Content="uniform" />
                              <ListBoxItem Content="victor" />
                              <ListBoxItem Content="whiskey" />
                              <ListBoxItem Content="x-ray" />
                              <ListBoxItem Content="yankee" />
                              <ListBoxItem Content="zulu" />
                          </ListBox>
                      </StackPanel>
                      """),
    ];

    private sealed class Controller(UIApplication app)
    {
        private const string NullDisplay = "(null)";

        private TextBlock _status = null!;
        private Border _canvas = null!; // hosts the loaded tree (or the placeholder / error)
        private Border _inspectorContent = null!;
        private UIElement? _lastInspected;
        private UIElement? _lastInspectedParent;
        private UIElement? _lastInspectedRelative;
        private string _loaded = "(nothing)";
        private bool _isInspecting;

        private void ToggleInspection()
        {
            _isInspecting = !_isInspecting;
            _canvas.Cursor = _isInspecting ? MouseCursorShape.Crosshair : MouseCursorShape.Default;
            _canvas.ForceCursor = _isInspecting;
            Refresh();
        }

        private void Refresh(bool reevaluateTarget = true)
        {
            var target = reevaluateTarget 
                             ? app.InputDispatcher.LastHoverTarget 
                             : _lastInspected ?? app.InputDispatcher.LastHoverTarget;

            if (target is not null/* && _canvas.IsAncestorOf(target)*/)
                Inspect(target, forceRefresh: true);
        }

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
            root.AddHandler(UIElement.KeyDownEvent, OnHotkey);

            var header = new Border
                         {
                             Child = new TextBlock(
                                 " Cursorial.UI — P9.8 XAML inspector   ·   o open   ·   hover / Tab to inspect   ·   t tier · d dark/light · q quit")
                         };

            header.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var statusBar = new Border();
            statusBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            _status = new TextBlock();
            statusBar.Child = _status;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // The inspector panel (docked right): a scrollable provenance report for the element under the cursor.
            _inspectorContent = new Border
                                {
                                    Width = 50,
                                    BorderPen = Pens.Light,
                                    Title = " inspector "
                                };

            _inspectorContent.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            DockPanel.SetDock(_inspectorContent, Dock.Right);
            root.Children.Add(_inspectorContent);

            // The canvas (fills the rest): the loaded tree. Hover / focus over it drives the inspector — the
            // handlers are scoped to the canvas, so hovering the inspector panel never inspects itself.
            _canvas = new Border { Child = Placeholder("Press '⌥+o' to open a XAML source.") };

            HookHandlers(root);

            root.Children.Add(_canvas);

            Inspect(null);
            UpdateStatus();
            return root;
        }

        private static TextBlock Placeholder(string text) => new($"\n   {text}");

        // ───────────────────────────── keys ─────────────────────────────

        private void OnHotkey(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return;

            if (e.Key == Key.Escape)
            {
                app.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Character || e.Text.Length == 0 || e.Modifiers is not KeyModifiers.Alt)
                return;

            switch (char.ToLowerInvariant(e.Text.Span[0]))
            {
                case 'q':
                    app.Shutdown();
                    e.Handled = true;
                    break;

                case 'o':
                    OpenDialog();
                    e.Handled = true;
                    break;

                case 'r':
                    Refresh(reevaluateTarget: false);
                    e.Handled = true;
                    break;

                case 't' :
                    var tier = app.ActualThemeVariant.Tier switch
                               {
                                   ColorDepth.Truecolor => ColorDepth.Ansi256,
                                   ColorDepth.Ansi256   => ColorDepth.Ansi16,
                                   ColorDepth.Ansi16    => ColorDepth.NoColor,
                                   _                    => ColorDepth.Truecolor,
                               };
                    app.OnCapabilitiesChanged(app.Capabilities with
                                              {
                                                  Output = app.Capabilities.Output with
                                                           {
                                                               Color = app.Capabilities.Output.Color with
                                                                       {
                                                                           Depth = tier
                                                                       }
                                                           }
                                              });
                    UpdateStatus();
                    ReinspectLast();
                    e.Handled = true;
                    break;

                case 'd':
                    app.RequestedThemeBase = app.ActualThemeVariant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
                    UpdateStatus();
                    ReinspectLast();
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── open + load ─────────────────────────────

        internal async void OpenDialog()
        {
            try
            {
                var list = new ListBox
                           {
                               ItemsSource = Samples.Select(s => s.Label).ToArray(), Height = 4,
                               SelectedIndex = 0
                           };

                var path = new TextBox { Placeholder = "…or a path to a .xaml file" };

                var open = new Button { Content = "_Open", IsDefault = true };
                var cancel = new Button { Content = "_Cancel", IsCancel = true, Margin = new Margins(1, 0, 0, 0) };

                var buttons = new StackPanel
                              {
                                  Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                  Margin = new Margins(0, 1, 0, 0)
                              };

                buttons.Children.Add(open);
                buttons.Children.Add(cancel);

                var body = new StackPanel();
                body.Children.Add(new Label { Content = "_Sample:" });
                body.Children.Add(list);
                body.Children.Add(new TextBlock("\nOr load a file:"));
                body.Children.Add(path);
                body.Children.Add(buttons);

                var dialog = new Window
                             {
                                 Title = "Open XAML",
                                 Content = body,
                                 WindowStartupLocation = WindowStartupLocation.CenterScreen,
                                 Width = 50,
                                 SizeToContent = SizeToContent.Height,
                                 CanResize = false
                             };

                dialog.SetResourceReference(Control.BackgroundProperty, ThemeKeys.PanelBrush);

                open.Click += (_, _) =>
                              {
                                  var typed = path.Text.Trim();

                                  dialog.Close(typed.Length > 0 ? new OpenChoice(IsFile: true, typed)
                                                   : new OpenChoice(IsFile: false, list.SelectedItem as string ?? Samples[0].Label));
                              };

                cancel.Click += (_, _) => dialog.Close(null);

                HookHandlers(dialog);

                if (await dialog.ShowDialogAsync() is OpenChoice choice)
                    LoadAndShow(choice);
            }
            catch (Exception e)
            {
                UIApplication.Current?.Dispatcher.Post(() => throw e);
            }
        }

        private void HookHandlers(UIElement element)
        {
            element.AddHandler(UIElement.MouseMoveEvent,
                              (_, e) =>
                              {
                                  if (_isInspecting)
                                      Inspect(e.Source);
                              },
                              handledEventsToo: true);

            element.AddHandler(UIElement.PreviewKeyDownEvent,
                              (_, e) =>
                              {
                                  if (e is { Key: Key.F12, Modifiers: KeyModifiers.None } || e is { Text.Span: "s" or "S", Modifiers: KeyModifiers.Alt })
                                  {
                                      ToggleInspection();
                                      e.Handled = true;
                                  }
                                  else if (e is { Key: Key.Character, Modifiers: KeyModifiers.Alt, Text.Span: "[" })
                                  {
                                      Inspect(_lastInspected, -1);
                                      e.Handled = true;
                                  }
                                  else if (e is { Key: Key.Character, Modifiers: KeyModifiers.Alt, Text.Span: "]" })
                                  {
                                      Inspect(_lastInspected, 1);
                                      e.Handled = true;
                                  }
                              });
        }

        private void LoadAndShow(OpenChoice choice)
        {
            string label, xaml;

            try
            {
                if (choice.IsFile)
                {
                    label = Path.GetFileName(choice.Value);

                    if (Directory.Exists(choice.Value))
                    {
                        ShowError($"\"{choice.Value}\" is a directory, not a .xaml file.");
                        return;
                    }

                    xaml = File.ReadAllText(choice.Value);
                }
                else
                {
                    label = choice.Value;
                    xaml = Samples.First(s => s.Label == choice.Value).Xaml;
                }
            }
            catch (Exception ex) // a bad path / unreadable file
            {
                ShowError($"Could not read \"{choice.Value}\":\n   {ex.Message}");
                return;
            }

            try
            {
                var tree = (UIElement) XamlLoader.Shared.Load(xaml);
                _loaded = label;
                _lastInspected = null;
                _lastInspectedParent = null;
                _lastInspectedRelative = null;
                _canvas.Child = tree; // render the loaded tree

                var opening = false;

                tree.AddHandler(Ribbon.BackstageRequestedEvent,
                                async (_, e) =>
                                {
                                    if (opening || e.Source is not {} anchor)
                                        return;

                                    opening = true;

                                    try
                                    {
                                        var backstage = BuildBackstage();
                                        _status.Text = "File → Backstage opened (◂ or Esc to return).";
                                        await BackstageHost.ShowAsync(backstage, anchor);
                                        _status.Text = "Backstage closed — back to the document.";
                                    }
                                    finally
                                    {
                                        opening = false;
                                    }
                                });

                Inspect(null);
            }
            catch (Exception ex) // XamlParseException (line+col in the message) / type-resolution / cast
            {
                ShowError($"Failed to load \"{label}\":\n\n   {ex.Message}");
            }

            UpdateStatus();
        }

        private Backstage BuildBackstage()
        {
            var backstage = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
            backstage.Items.Add(Destination("_New", "Start a new, empty document."));
            backstage.Items.Add(Destination("_Open", "Open an existing document from disk."));
            backstage.Items.Add(Destination("_Save", "Save the current document."));
            backstage.Items.Add(Destination("Save _As", "Save the document under a new name."));
            backstage.Items.Add(Destination("_Export", "Export the document to another format."));
            backstage.Items.Add(Destination("_Print", "Print the document."));
            backstage.Items.Add(new BackstageItem { Header = "──────", IsSelectable = false }); // a non-selectable rule
            backstage.Items.Add(Destination("_Account", "Your account, sign-in, and connected services."));
            backstage.Items.Add(Destination("_Options", "Application options and preferences."));

            backstage.SelectionChanged += (_, _) =>
                                          {
                                              if (backstage.SelectedItem is BackstageItem { Header: string header })
                                                  _status.Text = $"Backstage → {header.Replace("_", string.Empty)}";
                                          };
            return backstage;
        }

        // One rail destination: the Header is the rail label (access-key folded); the Content is the detail pane shown when
        // it is selected (a title over a description over an action button). Invoking the action button runs its command AND
        // closes the Backstage (the Backstage's detail-pane close-on-invoke — the Office "act and return" model).
        private BackstageItem Destination(string header, string detail)
        {
            var name = header.Replace("_", string.Empty);

            var title = new TextBlock { Text = name, Margin = new Margins(0, 0, 0, 1), TextWrapping = WrapMode.WordWrap };
            var body = new TextBlock { Text = detail, TextWrapping = WrapMode.WordWrap };
            var action = new BarButton
                         {
                             Content = $"◆ {name}",
                             Margin = new Margins(0, 1, 0, 0),
                             Command = new BarCommand(() => _status.Text = $"{name} invoked — returned to the document.")
                         };
            var pane = new StackPanel { Orientation = Orientation.Vertical, Margin = new Margins(1, 0) };
            pane.Children.Add(title);
            pane.Children.Add(body);
            pane.Children.Add(action);
            return new BackstageItem { Header = header, Content = pane };
        }

        private void ShowError(string message)
        {
            _loaded = "(load failed)";
            _lastInspected = null;
            _lastInspectedParent = null;
            _lastInspectedRelative = null;
            _canvas.Child = new StackPanel { Margin = new Margins(2, 1) };
            ((StackPanel) _canvas.Child!).Children.Add(new TextBlock($"\n⚠ {message}"));
            Inspect(null);
            UpdateStatus();
        }

        // ───────────────────────────── inspect ─────────────────────────────

        private void ReinspectLast()
        {
            var target = _lastInspected;
            _lastInspected = null; // force a rebuild (provenance changed after a theme flip)
            _lastInspectedParent = null;
            _lastInspectedRelative = null;
            Inspect(target);
        }

        private UIElement? AscendTree(UIElement? anchor, UIElement? current)
        {
            var e = current ?? anchor;
            return e?.VisualParent ?? e?.UIParent;
        }

        private UIElement? DescendTree(UIElement? anchor, UIElement? current)
        {
            if (current is null) return anchor;

            UIElement? e;
            UIElement? prev = anchor;

            for (e = anchor; e is not null; e = e.VisualParent ?? e.UIParent)
            {
                if (e == current) return prev;
                prev = e;
            }

            return anchor;
        }

        private UIElement? TemplatedParent(UIElement? anchor)
        {
            UIElement? e = anchor;

            while (e is not null)
            {
                if (e.TemplatedParent is not null) return e.TemplatedParent;

                if (e.LogicalParent is null) e = e.VisualParent;
                else break;
            }

            return e ?? anchor;
        }

        private void Inspect(UIElement? element, int direction = 0, bool forceRefresh = false)
        {
            var parent = TemplatedParent(element);

            if (ReferenceEquals(parent, _lastInspectedParent) && direction == 0 && forceRefresh is false)
                return;

            _lastInspected = element;
            _lastInspectedParent = parent;

            _inspectorContent.Child = null;

            _lastInspectedRelative = direction switch
                                     {
                                         0   => _lastInspectedParent,
                                         > 0 => DescendTree(_lastInspected, _lastInspectedRelative),
                                         < 0 => AscendTree(_lastInspected, _lastInspectedRelative)
                                     };

            var current = _lastInspectedRelative;

            if (current is null)
            {
                _inspectorContent.Child = new TextBlock("\n  Hover or Tab to an element\n  in the loaded tree.\n  " +
                                                        "Use [ and ] to transcend\n  template elements.");

                UpdateStatus();
                return;
            }

            var seen = new HashSet<UIElement>();
            var tree = new TreeView();

            ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Visible); // avoid items shifting with (dis)appearance of scrollbar 
            
            tree.Items.Add(InspectNode(current, seen: seen));

            // if (current is Control c)
            // {
            //     var attributes = c.GetType().GetCustomAttributes(typeof(TemplatePartAttribute), true);
            //
            //     foreach (var attribute in attributes.OfType<TemplatePartAttribute>())
            //     {
            //         if (c.GetTemplatePart<UIElement>(attribute.Name) is {} part)
            //             tree.Items.Add(InspectNode(part, attribute.Name, seen: seen));
            //     }
            // }

            _inspectorContent.Child = tree;
            UpdateStatus();
        }

        private TreeViewItem InspectNode(UIElement current, string? name = null, HashSet<UIElement>? seen = null,
                                         bool expanded = true)
        {
            seen ??= new HashSet<UIElement>();

            if (seen.Add(current) is false)
                return Node(FormatValue(current), NoValue, inlineValue: $"{RuntimeHelpers.GetHashCode(current):x8}");

            if (string.IsNullOrWhiteSpace(name))
                name = BuildElementPath(current);
            else
                name = current.GetType().Name is { Length: > 0 } tName ? $"{tName}#{name}" : name;

            var root = Node($"{name} ({RuntimeHelpers.GetHashCode(current):x8})",
                            NoValue, 
                            ThemeKeys.GreenBrush,
                            expanded: expanded);

            var pseudoClasses = string.Join(
                ", ",
                Enum.GetValues<InteractionState>()
                    .Where(o => current.InteractionStateInternal.HasFlag(o))
                    .Select(o => InteractionPseudoClasses.TryGetPseudoClass(o, out var c) ? c : null)
                    .Where(c => c is not null)
                    .Concat(current.PseudoClasses.CustomClasses)
                    .Concat(current.Classes.Select(c => $".{c}")));

            root.Items.Add(Node("Classes", pseudoClasses));

            root.Items.Add(Node(nameof(UIElement.DesiredSize), current.DesiredSize));
            root.Items.Add(Node(nameof(UIElement.Bounds), current.Bounds));

            root.Items.Add(Node("TextElement.Attributes",
                                NoValue,
                                inlineValue: TextElement.ComposeAttributes(current)));

            if (app.InputDispatcher.LastPointerPosition is {} position)
            {
                IReadOnlyList<LayerCellSample>? samples = app.WindowManager?.SampleCell(position.Column, position.Row);

                if (samples is not null)
                {
                    var cellNode = Node("Layers",
                                        NoValue,
                                        inlineValue: $"{samples.Count} at ({position.Column}, {position.Row})",
                                        expanded: false);

                    for (var i = samples.Count - 1; i >= 0; i--)
                    {
                        var cs = samples[i];
                        cellNode.Items.Add(Node($"[{i}]",
                                                FormatCellSample(cs),
                                                inlineValue: FormatCellSampleDescription(cs),
                                                expanded: false));
                    }

                    root.Items.Add(cellNode);
                }
            }

            var properties = UIProperties.ForType(current.GetType())
                                         .Concat(UIProperties.AttachedBy(current.GetType()))
                                         .Concat(current.GetType() != typeof(AccessTextPresenter)
                                                     ? UIProperties.ForType(typeof(AccessTextPresenter))
                                                     : [])
                                         .Concat(current.GetType() != typeof(TextElement)
                                                     ? UIProperties.AttachedBy(typeof(TextElement))
                                                     : [])
                                         .Concat(UIProperties.Inheriting)
                                         .Distinct()
                                         .OrderBy(p => p.Name)
                                         .ToList();

            foreach (var property in properties)
            {
                // The winning derivation line (StyleDiagnostics.Explain is one line per contributor, strongest
                // first): "<prop> = <value> <- <Layer>(n) \"<selector>\" … -- winning" (or "<- LocalValue").
                // Guarded: a diagnostic must never crash the thing it inspects — a pathological value ToString()
                // in an arbitrarily loaded tree degrades to an error line, not an unhandled hover-handler throw.
                StyleExplanation e;
                object? resourceKey;

                try
                {
                    e = StyleDiagnostics.ExplainDetails(current, property);
                    resourceKey = ResourceDiagnostics.GetResourceKey(current, property); // W3 resource-inspector hook
                }
                catch (Exception ex)
                {
                    return new TreeViewItem
                           {
                               Header = new TextBlock
                                        {
                                            Markup = $"[brush {ThemeKeys.RedBrush}][b]{property.Name} " +
                                                     $"Error:[/b]{ex.GetType().Name}[/brush]"
                                        },
                           };
                }

                var item = Node($"{property.OwnerType.Name}.{property.Name}", NoValue,
                                inlineValue: current.GetValue(property), expanded: false);

                item.Items.Add(Node(nameof(e.TargetDescription), e.TargetDescription));

                item.Items.Add(current.GetValue(property) switch
                               {
                                   UIElement ev => InspectNode(ev, name, seen: seen, expanded: false),
                                   var o        => Node("Value", o, expanded: false)
                               });

                if (resourceKey is not null)
                    item.Items.Add(Node("Resource Key", resourceKey));

                AttachStyleExplanation(item, e);

                if (BindingDiagnostics.Explain(current, property) is { HasBindings: true } bd)
                    AttachBindingExplanation(bd, item);

                //
                // // Append the within-lane provenance (PD25) and, for a resource-backed value, the resource KEY
                // // it resolved through (W3 — the resource-inspector companion to the style inspector).
                // var line = $"{winning} · {kind}";
                // if (resourceKey is not null)
                //     line += $" ← resource '{resourceKey}'";
                // _inspectorContent.Children.Add(new TextBlock(line));

                root.Items.Add(item);
            }

            /*
            if (current.VisualChildrenCount is var vcc and > 0)
            {
                var vc = Node("VisualChildren", NoValue, expanded: false);

                vc.IsExpanded = false;

                for (int i = 0; i < vcc; i++)
                {
                    if (current.GetVisualChild(i) is {} child)
                    {
                        var childNode = Node($"VisualChildren[{i}]", InspectNode(child, seen: seen, expanded: false));
                        childNode.IsExpanded = false;
                        vc.Items.Add(childNode);
                    }
                }

                root.Items.Add(vc);
            }
            */

            /*
            if (current.LogicalChildrenList is { Count: var lcc and >0 } logicalChildren)
            {
                var lc = Node("LogicalChildren", NoValue, expanded: false);

                for (int i = 0; i < lcc; i++)
                {
                    if (logicalChildren[i] is {} child)
                    {
                        var childNode = Node($"LogicalChildren[{i}]", InspectNode(child, seen: seen, expanded: false));
                        lc.Items.Add(childNode);
                    }
                }

                root.Items.Add(lc);
            }
            */

            if (current.VisualParent is {} vp)
                root.Items.Add(Node("VisualParent", InspectNode(vp, seen: seen, expanded: true), expanded: false));

            if (current.LogicalParent is {} lp)
                root.Items.Add(Node("LogicalParent", InspectNode(lp, seen: seen, expanded: true), expanded: false));

            if (current.UIParent is {} up && !ReferenceEquals(current.LogicalParent, up))
                root.Items.Add(Node("UIParent", InspectNode(up, seen: seen, expanded: true), expanded: false));

            if (current.GetInheritanceParent() is {} ip &&
                !ReferenceEquals(current.LogicalParent, ip) &&
                !ReferenceEquals(current.VisualParent, ip))
            {
                root.Items.Add(Node("InheritanceParent",
                                    ip is UIElement ipe ? InspectNode(ipe, seen: seen, expanded: true) : ip, expanded: false));
            }

            return root;
        }

        private static string? FormatCellSampleDescription(LayerCellSample cs)
        {
            if (cs.Cell is { Grapheme: var g } c)
                return $"{QuoteValue(EscapeGraphemes(g))} [{FormatValue(c.Kind)}] {cs.ElementDescription}";
            return cs.ElementDescription;
        }

        private static void AttachStyleExplanation(TreeViewItem item, StyleExplanation e)
        {
            item.Items.Add(Node(nameof(e.Kind), e.Kind));
            item.Items.Add(Node(nameof(e.Priority), e.Priority));
            item.Items.Add(Node(nameof(e.BasePriority), e.BasePriority));
            item.Items.Add(Node(nameof(e.IsAnimated), e.IsAnimated));

            if (e.Frames is { Length: > 0 })
            {
                for (var i = 0; i < e.Frames.Length; i++)
                {
                    var frame = e.Frames[i];

                    var frameRoot = Node($"Frames[{i}]", NoValue);

                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.Layer), frame.Layer));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.SelectorDescription), frame.SelectorDescription));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.IsActive), frame.IsActive));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.IsConditional), frame.IsConditional));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.HasValue), frame.HasValue));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.LastProducedValue), frame.LastProducedValue));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.ResourceKey), frame.ResourceKey));
                    frameRoot.Items.Add(Node(nameof(StyleFrameExplanation.Status), frame.Status));

                    var k = frame.SortKey;
                    var keyRoot = Node(nameof(StyleFrameExplanation.SortKey), NoValue);

                    keyRoot.Items.Add(Node(nameof(k.Layer), k.Layer));
                    keyRoot.Items.Add(Node(nameof(k.Names), k.Names));
                    keyRoot.Items.Add(Node(nameof(k.ClassLike), k.ClassLike));
                    keyRoot.Items.Add(Node(nameof(k.Types), k.Types));
                    keyRoot.Items.Add(Node(nameof(k.ScopeDepth), k.ScopeDepth));
                    keyRoot.Items.Add(Node(nameof(k.Order), k.Order));
                    keyRoot.Items.Add(Node(nameof(k.Packed), k.Packed.ToString("X16")));

                    frameRoot.Items.Add(keyRoot);

                    item.Items.Add(frameRoot);
                }
            }
        }

        private static readonly object NoValue = new();

        private static TreeViewItem Node(object? name, object? value, string? brush = ThemeKeys.MutedBrush,
                                         object? inlineValue = null, bool expanded = true)
        {
            var hasName = name is not null;
            var type = value?.GetType() ?? typeof(object);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                type = Nullable.GetUnderlyingType(type);

            var isSimple = inlineValue is null && 
                           type is { IsPrimitive: true } or { IsEnum: true } or { FullName: "System.String" };

            var header = hasName
                             ? isSimple
                                   ? $"[b][brush {brush}]{Sanitize(name)}:[/brush][/b] {FormatValue(value)}"
                                   : $"[b][brush {brush}]{Sanitize(name)}" +
                                     (inlineValue is not null 
                                          ? $":[/brush][/b] [brush {ThemeKeys.TextBrush}]{FormatValue(inlineValue)}[/brush]"
                                          : "[/brush][/b]")
                             : FormatValue(value);

            var item = new TreeViewItem
                       {
                           IsExpanded = expanded,
                           Header = new TextBlock { Markup = header, TextWrapping = WrapMode.WordWrap }
                       };

            // @formatter:off
            if (hasName && !isSimple && value != NoValue)
            {
                if (value is TreeViewItem tv)
                    item.Items.Add(tv);
                else if (value is IEnumerable<TreeViewItem> tvs)
                    foreach (var tvi in tvs) item.Items.Add(tvi);
                else
                    item.Items.Add(Node(null, value));
            }
            // @formatter:on

            return item;
        }

        private static string QuoteValue(string? value)
        {
            if (value == NullDisplay) return NullDisplay;

            return $"\"{value}\"" +
                   (value?.EnumerateRunes().Any(r => GraphemeWidth.CodepointWidth(r) > 1) is true 
                        ? $" (w={GraphemeWidth.StringWidth(value)})"
                        : "");
        }

        private static string FormatValue(object? value)
        {
            var f = value switch
                    {
                        null                       => NullDisplay,
                        Cell c                     => $"{{Cell({c.Kind}) Grapheme=\"{EscapeGraphemes(c.Grapheme)}\", " +
                                                      $"Style=\"{FormatValue(c.Style)}\"}}",
                        CS cs                      => $"{{CellStyle fg={FormatValue(cs.Foreground)}, " +
                                                      $"bg={FormatValue(cs.Background)}, " +
                                                      $"ul={FormatValue(cs.UnderlineStyle)}" +
                                                      $"({FormatValue(cs.UnderlineColor)})" +
                                                      (cs.Hyperlink is { Uri: {} l }
                                                           ? $", link=\"{FormatValue(l)}\"" 
                                                           : "") +
                                                      $", attr={cs.Attributes}}}",
                        // string s                   => QuoteValue(s),
                        UIElement e                => $"{{{value.GetType().Name}}} ({RuntimeHelpers.GetHashCode(e):x8})",
                        Array a                    => $"[{string.Join(", ", a.Cast<object>().Select(FormatValue))}]" + (a.Length > 0 ? " " : ""),
                        System.Collections.IList l => $"[{string.Join(", ", l.Cast<object>().Select(FormatValue))}]",
                        UIProperty p               => $"{p.OwnerType.Name}.{p.Name}",
                        TimeSpan ts => ts.Hours > 0
                                           ? $"{ts.Hours:0.##}h"
                                           : ts.Minutes > 0
                                               ? $"{ts.Minutes:0.##}m"
                                               : $"{ts.Seconds:0.##}s",
                        Transition t => $"Transition {FormatValue(t.Property)} " + $"({FormatValue(t.Duration)}s" +
                                        $"{(t.Delay > TimeSpan.Zero ? $" after {FormatValue(t.Delay)}s" : "")})",
                        Color { Kind: ColorKind.Rgb } c => c.ToString("x"),
                        Color c                         => c.ToString("c"),
                        Pen p => $"Pen {{ Brush={FormatValue(p.Brush)}, Weight={p.Weight}, Corners={FormatValue(p.Corners)}, " +
                                 $"Dash={FormatValue(p.Dash)}, EndCap={FormatValue(p.EndCap)}, Junction={FormatValue(p.Junction)}, " +
                                 $"GlyphSet={FormatValue(p.GlyphSet)}, Attributes={FormatValue(p.Attributes)} }}",
                        SolidColorBrush sc => $"{FormatValue(sc.Color)} Opacity={sc.Opacity:0.##}",
                        LinearGradientBrush lg => $"linear:({lg.StartPoint.X},{lg.StartPoint.Y}) -> ({lg.EndPoint.X},{lg.EndPoint.Y}, " +
                                                  $"{string.Join(", ", lg.Stops.Select(s => FormatValue(s.Color)))})",
                        RadialGradientBrush rg => $"radial:({rg.Center.X},{rg.Center.Y}) -> ({rg.RadiusX},{rg.RadiusY}, " +
                                                  $"{string.Join(", ", rg.Stops.Select(s => FormatValue(s.Color)))})",
                        ConicGradientBrush cb => $"conic:({cb.Center.X},{cb.Center.Y}) -> ({cb.AngleDegrees}º, Center={cb.Center}, " +
                                                 $"{string.Join(", ", cb.Stops.Select(s => FormatValue(s.Color)))})",
                        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                        GlyphSetCarrier gc       => gc.ToString(),
                        _                        => HasToStringOverride(value) ? (value.ToString() ?? NullDisplay) : $"{{{value.GetType().Name}}}"
                    };

            return Sanitize(f);
        }

        private static TreeViewItem FormatCellSample(LayerCellSample sample)
        {
            var cell = sample.Cell;
            var style = cell?.Style;

            var children = new List<TreeViewItem>
                           {
                               Node("Element", sample.ElementDescription),
                               Node(nameof(sample.SurfaceZ), FormatValue(sample.SurfaceZ)),
                               Node(nameof(sample.Parameters),
                                   new List<TreeViewItem>
                                   {
                                       Node(nameof(sample.Parameters.Clip), NoValue, inlineValue: FormatValue(sample.Parameters.Clip)),
                                       Node(nameof(sample.Parameters.Mode), NoValue, inlineValue: FormatValue(sample.Parameters.Mode)),
                                       Node(nameof(sample.Parameters.OffsetColumn), NoValue, inlineValue: FormatValue(sample.Parameters.OffsetColumn)),
                                       Node(nameof(sample.Parameters.OffsetRow), NoValue, inlineValue: FormatValue(sample.Parameters.OffsetRow)),
                                       Node(nameof(sample.Parameters.Opacity), NoValue, inlineValue: FormatValue(sample.Parameters.Opacity))
                                   },
                                   expanded: false)
                           };

            if (style is {} s)
            {
                children.Add(
                    Node("Style",
                         new[]
                         {
                             Node(nameof(CS.Foreground), NoValue, inlineValue: FormatValue(s.Foreground)),
                             Node(nameof(CS.Background), NoValue, inlineValue: FormatValue(s.Background)),
                             Node(nameof(CS.Attributes), NoValue, inlineValue: FormatValue(s.Attributes)),
                             Node(nameof(CS.UnderlineStyle), NoValue, inlineValue: FormatValue(s.UnderlineStyle)),
                             Node(nameof(CS.UnderlineColor), NoValue, inlineValue: FormatValue(s.UnderlineColor)),
                             Node(nameof(CS.Hyperlink), NoValue, inlineValue: FormatValue(s.Hyperlink.Uri)),
                             Node(nameof(CS.Background), NoValue, inlineValue: FormatValue(s.Background))
                         },
                         expanded: false));
            }

            return Node("Cell",
                        children,
                        inlineValue: cell is {} c ? $"{EscapeGraphemes(c.Grapheme)} [{FormatValue(c.Kind)}]" : null);
        }

        private static string EscapeGraphemes(string? grapheme)
        {
            if (grapheme is null) return NullDisplay;

            var sb = new StringBuilder();
            var enumerator = grapheme.GetGraphemeEnumerator();

            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;

                while (Rune.DecodeFromUtf16(current, out Rune r, out var consumed) is OperationStatus.Done)
                {
                    sb.Append(IsPrintableAscii(r) ? r.ToString() : $"\\u{r.Value:X}");
                    current = current.Slice(consumed);
                }
            }
            
            return sb.ToString();
        }

        private static bool IsPrintableAscii(Rune rune) => rune.Value is >= 0x20 and <= 0x7E;
        
        private static void AttachBindingExplanation(BindingExplanation bd, TreeViewItem item)
        {
            var rootBindingItem = Node("Binding", NoValue);

            rootBindingItem.Items.Add(Node("Target", bd.TargetDescription));

            for (var i = 0; i < bd.Expressions.Length; i++)
            {
                var be = bd.Expressions[i];
                var bindingItem = Node($"Bindings[{i}]", NoValue);

                if (be is BindingExpressionExplanation explanation)
                {
                    bindingItem.Items.Add(Node(nameof(explanation.Lane), explanation.Lane));
                    bindingItem.Items.Add(Node(nameof(explanation.Path), explanation.Path));
                    bindingItem.Items.Add(Node(nameof(explanation.Status), explanation.Status));
                    bindingItem.Items.Add(Node(nameof(explanation.EffectiveMode), explanation.EffectiveMode));
                    bindingItem.Items.Add(Node(nameof(explanation.ResolvedSourceChain), explanation.ResolvedSourceChain));
                    bindingItem.Items.Add(Node(nameof(explanation.LastProducedValue), explanation.LastProducedValue));
                    bindingItem.Items.Add(Node(nameof(explanation.LastFailure), explanation.LastFailure));
                }

                rootBindingItem.Items.Add(bindingItem);
            }

            item.Items.Add(rootBindingItem);
        }

        private static bool HasToStringOverride(object? obj)
        {
            // if (obj is null)
            //     return false;
            //
            // var type = obj.GetType();
            //
            // var toStringMethod = type.GetMethod(nameof(ToString),
            //                                     BindingFlags.Public | BindingFlags.Instance, null
            //                                     , Type.EmptyTypes,
            //                                     null);
            //
            // return toStringMethod is not null &&
            //        toStringMethod.DeclaringType != typeof(object);
            return obj is not null && obj.ToString() != obj.GetType().FullName;
        }

        private static string Sanitize(object? value)
        {
            if (value?.ToString() is not {} s) return NullDisplay;
            return Regex.Replace(s, @"(?<!\\)\[", "\\[");
        }

        private static string BuildElementPath(UIElement current)
        {
            var tree = new List<UIElement>();
            var sb = new StringBuilder();

            for (UIElement? e = current; e is not null; e = e.VisualParent ?? e.UIParent)
                tree.Add(e);

            for (var index = tree.Count - 1; index >= 0; index--)
            {
                var e = tree[index];

                if (sb.Length > 0)
                    sb.Append('.');

                sb.Append(e.GetType().Name);

                if (e.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name))
                    sb.Append($"#{e.Name}");
            }

            return sb.ToString();
        }

        private void UpdateStatus()
        {
            _status.Text = $" loaded: {_loaded} · theme: {app.ActualThemeVariant} — " +
                           "⌥+o open · F12 inspect · ⌥+[, ⌥+] traverse · ⌥+r refresh · ⌥+t tier · ⌥+d dark/light · ⌥+q quit".Replace("[", "\\[");
        }
    }

    private sealed record OpenChoice(bool IsFile, string Value);
}