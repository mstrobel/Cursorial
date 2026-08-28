using System.Globalization;
using System.Reflection;
using System.Windows.Input;

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.IndigoDusk;

namespace Cursorial.Gallery.ViewModels;

public enum ThemeName
{
    Default,
    // IndigoDusk,
    // Curio
}

public sealed class ComplementBrush : IBrush
{
    public required IBrush InnerBrush { get; init; }

    public Color ColorAt(int column, int row, Rect bounds)
    {
        var inner = InnerBrush.ColorAt(column, row, bounds);
        return inner.Complement();
    }
}

public sealed class ComplementConverter : IValueConverter
{
    public static readonly ComplementConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        targetType = targetType.UnwrapNullable();

        var brush = value as IBrush ?? (value is Pen p ? p.Brush : null);
        if (brush is null) return UIProperty.UnsetValue;

        brush = new ComplementBrush { InnerBrush = brush };

        if (targetType  == typeof(Pen))
            return new Pen(brush) { Weight = StrokeWeight.Light };

        if (typeof(IBrush).IsAssignableFrom(targetType))
            return brush;

        return UIProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class ThemeEntry : UIObject
{
    public static readonly AttachedProperty<object?> ResourceRootSourceProperty
        = UIProperty.RegisterAttached<ThemeEntry, UIObject, object?>("ResourceRootSource");

    public static readonly AttachedProperty<object?> ResourceRootTargetProperty
        = UIProperty.RegisterAttached<ThemeEntry, UIObject, object?>("ResourceRootTarget");

    public static readonly StyledProperty<object?> ResourceProperty
        = UIProperty.Register<ThemeEntry, object?>(nameof(Resource));

    public static readonly StyledProperty<string> ResourceKeyProperty
        = UIProperty.Register<ThemeEntry, string>(nameof(ResourceKey));

    public static readonly StyledProperty<string> DescriptionProperty
        = UIProperty.Register<ThemeEntry, string>(nameof(Description), defaultValue: "");
    
    public object? Resource { get => GetValue(ResourceProperty); set => SetValue(ResourceProperty, value); }
    public string ResourceKey { get => GetValue(ResourceKeyProperty); set => SetValue(ResourceKeyProperty, value); }
    public string Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

public class ThemesViewModel : PageViewModel
{
    public override string Title => "Themes";

    public override string Summary => "Cursorial.UI is fully themeable. Explore custom palettes, styles, and control templates.";

    public override bool IsContentScrollable => false;

    public IReadOnlyList<ThemeEntry> Entries { get; set => Set(ref field, value); } = [];

    public IReadOnlyList<ThemeName> Themes { get; } = Enum.GetValues<ThemeName>();

    public ThemeName SelectedTheme
    {
        get
        {
            if (Entries is not { Count: > 0 }) UpdateEntries();
            return field;
        }
        set
        {
            if (Set(ref field, value))
                UpdateTheme();
        }
    }

    protected internal override void OnThemeChanged()
    {
        UpdateEntries();
    }

    protected internal override void OnAppStarted()
    {
        UpdateEntries();
    }

    private void UpdateEntries()
    {
        if (UIApplication.Current is not {} app) return;

        if (Entries is not { Count: > 0 } entries)
        {
            List<ThemeEntry> list =
            [
                new() { ResourceKey = "Demo.HeaderStyle" },
                new() { ResourceKey = "Demo.SubheaderStyle" },
                new() { ResourceKey = "Demo.TypoStyle" },
                ..typeof(ThemeKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
                                   .Where(f => f.FieldType == typeof(string))
                                   .Select(f => new ThemeEntry { ResourceKey = (string) f.GetValue(null)! })
            ];

            entries = list;
        }

        app.Resources["Demo.HeaderStyle"] =
            BrushedStyle.Identity
                        .WithForeground(Find(app, ThemeKeys.CoolBrush, Brushes.Blue))
                        .Weighing(TextWeight.Bold);

        app.Resources["Demo.SubheaderStyle"] =
            BrushedStyle.Identity
                        .WithForeground(Find(app, ThemeKeys.AmberBrush, Brushes.Yellow))
                        .Posturing(TextStyle.Italic);
    
        app.Resources["Demo.TypoStyle"] =
            BrushedStyle.Identity
                        .Underlining(UnderlineStyle.Curly,
                                     Find(app, ThemeKeys.DangerBrush, Brushes.Red));

        foreach (var entry in entries)
        {
            entry.Resource = Find<object?>(app, entry.ResourceKey, null);
            entry.Description = entry.Resource is {} r ? r.GetType().Name! : "(not defined)";
        }

        Entries = entries;
    }

    private static T Find<T>(UIApplication app, string key, T fallback)
    {
        if (app.RootElement!.TryFindResource(key, app.ActualThemeVariant, out var resource) &&
            resource is T t)
        {
            return t;
        }
        return fallback;
    }
    
    private void UpdateTheme()
    {
        if (UIApplication.Current is not {} app) return;

        app.Theme = SelectedTheme switch
                    {
                        // ThemeName.IndigoDusk => IndigoDuskTheme.LoadTheme(),
                        // ThemeName.Curio      => CurioTheme.LoadTheme(),
                        _                    => null
                    };

        OnThemeChanged();
    }
}