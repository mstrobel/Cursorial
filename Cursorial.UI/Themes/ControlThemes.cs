using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.Themes;

/// <summary>
/// The S8 default control themes authored into <see cref="CursorialTheme.BuiltIn"/>'s structure
/// (design doc §11.8/§12.7, CD30): one <see cref="Style"/> per control <see cref="Type"/> key, each a
/// selector-less theme rooted at <c>^</c> carrying a <see cref="ControlTemplate"/> setter plus
/// <c>:focus</c>/<c>:pressed</c>/<c>:checked</c>/<c>:disabled</c> child rules. The styling engine
/// resolves them by <see cref="Control.ControlThemeKey"/> (exact-key) and arms them at
/// <see cref="StyleLayer.ControlTheme"/> — the weakest style layer, so an app style always wins
/// (CD30). Templates are code-first <see cref="ControlTemplate"/>s (Fork C wires XAML at P6).
/// </summary>
internal static class ControlThemes
{
    // The default pens/brushes the templates wire as constants (the ResourceReference palette spine —
    // re-skin-via-one-key-override — is the R2 concern; P5 control themes wire constants so the
    // controls render with a sane default look on every tier through the quantizer).
    private static readonly Pen BorderPen = Pens.Light;
    private static readonly Pen FocusPen = Pens.Heavy;
    private static readonly Pen DefaultPen = Pens.Double;

    internal static void Populate(ResourceDictionary dict)
    {
        dict[typeof(Button)] = ButtonTheme();
        dict[typeof(RepeatButton)] = RepeatButtonTheme();
        dict[typeof(ToggleButton)] = ToggleButtonTheme();
        dict[typeof(CheckBox)] = ToggleGlyphTheme("Theme.CheckBox", ThemeKeys.CheckBoxGlyphs);
        dict[typeof(RadioButton)] = ToggleGlyphTheme("Theme.RadioButton", ThemeKeys.RadioGlyphs);
        dict[typeof(ScrollBar)] = ScrollBarTheme();
        dict[typeof(ScrollViewer)] = ScrollViewerTheme();
    }

    // ───────────────────────────── Button / RepeatButton / ToggleButton ─────────────────────────────

    // A bordered single-content button (spec line 658): a Border with Padding (1,0) wrapping a
    // RecognizesAccessKey ContentPresenter that auto-aliases the button's Content. :focus → Heavy
    // border (render-only, NoColor-safe per the §3.5 escalation), :default → Double, :pressed →
    // Inverse content attribute, :disabled → Faint (handled by the presenter's effective-enabled read).
    private static ControlTemplate ButtonContentTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border
        {
            BorderPen = BorderPen,
            Padding = new Margins(1, 0),
            Child = presenter,
        };
        // The surface fill follows the button's Background (the WPF default-template wiring): a
        // TemplateBinding makes Button.Background paint the face, quantized per the negotiated tier.
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        return border;
    });

    private static Style ButtonTheme()
    {
        var theme = new Style { Key = "Theme.Button" }.Set(Control.TemplateProperty, ButtonContentTemplate());
        theme.Children.Add(new Style("^:focus").Set(Control.BorderPenProperty, FocusPen));
        theme.Children.Add(new Style("^:default").Set(Control.BorderPenProperty, DefaultPen));
        return theme;
    }

    private static Style RepeatButtonTheme()
        => new Style { Key = "Theme.RepeatButton" }.Set(Control.TemplateProperty, ButtonContentTemplate());

    private static Style ToggleButtonTheme()
    {
        var theme = new Style { Key = "Theme.ToggleButton" }.Set(Control.TemplateProperty, ButtonContentTemplate());
        theme.Children.Add(new Style("^:focus").Set(Control.BorderPenProperty, FocusPen));
        return theme;
    }

    // ───────────────────────────── CheckBox / RadioButton ─────────────────────────────

    // glyph cell(s) + 1 space + ContentPresenter (spec line 660). The glyph element reads the owning
    // toggle's IsChecked + the glyph-set resource (ASCII default, overridable). No Border by default.
    private static ControlTemplate ToggleGlyphTemplate(string glyphKey) => new(ctx =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        var glyph = new ToggleGlyph(glyphKey);
        ctx.RegisterName("PART_Glyph", glyph);
        var presenter = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        row.Children.Add(glyph);
        row.Children.Add(presenter);
        return row;
    });

    // themeKey is the diagnostic Style.Key (e.g. "Theme.CheckBox"); glyphKey is the ThemeKeys glyph-set
    // resource the ToggleGlyph leaf reads (already a fully-qualified "Theme.*" constant — never re-prefix it).
    private static Style ToggleGlyphTheme(string themeKey, string glyphKey)
        => new Style { Key = themeKey }.Set(Control.TemplateProperty, ToggleGlyphTemplate(glyphKey));

    // ───────────────────────────── ScrollBar / ScrollViewer ─────────────────────────────

    // A borderless line-step RepeatButton template: a single arrow glyph (no border/padding), so a
    // 1-cell-wide ScrollBar's arrows fit (the bordered ButtonContentTemplate would draw a │ frame).
    private static ControlTemplate BareGlyphButtonTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = false };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        return presenter;
    });

    // PART_Track (required) + optional PART_LineUpButton/PART_LineDownButton arrow RepeatButtons
    // (CD19/C231/C236). The arrows are borderless RepeatButtons with arrow-glyph content; the track is
    // the internal Track primitive. A DockPanel docks the arrows to the ends, the track filling the rest.
    private static ControlTemplate ScrollBarTemplate(bool horizontal) => new(ctx =>
    {
        var dock = new DockPanel();
        var bareTemplate = BareGlyphButtonTemplate();

        var lineUp = new RepeatButton { Content = horizontal ? "<" : "^", Template = bareTemplate };
        ctx.RegisterName("PART_LineUpButton", lineUp);
        DockPanel.SetDock(lineUp, horizontal ? Dock.Left : Dock.Top);

        var lineDown = new RepeatButton { Content = horizontal ? ">" : "v", Template = bareTemplate };
        ctx.RegisterName("PART_LineDownButton", lineDown);
        DockPanel.SetDock(lineDown, horizontal ? Dock.Right : Dock.Bottom);

        var owner = (ScrollBar)(ctx.TemplatedParent
                                ?? throw new InvalidOperationException("The ScrollBar theme template requires a templated parent."));
        var track = new Track(owner);
        ctx.RegisterName("PART_Track", track);

        dock.Children.Add(lineUp);
        dock.Children.Add(lineDown);
        dock.Children.Add(track); // last child fills the remaining space (DockPanel default)
        return dock;
    });

    private static Style ScrollBarTheme()
    {
        var theme = new Style { Key = "Theme.ScrollBar" }
            .Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: false))
            .Set(ScrollBar.BorderPenProperty, BorderPen);
        // A horizontal bar uses the rotated template (arrows on the ends of the long axis).
        theme.Children.Add(new Style("^:horizontal").Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: true)));
        return theme;
    }

    // PART_ScrollContentPresenter (required) + a PART_VerticalScrollBar docked right (CD28/C235).
    private static ControlTemplate ScrollViewerTemplate() => new(ctx =>
    {
        var dock = new DockPanel();

        var bar = new ScrollBar { Orientation = Orientation.Vertical };
        ctx.RegisterName("PART_VerticalScrollBar", bar);
        DockPanel.SetDock(bar, Dock.Right);

        var presenter = new ScrollContentPresenter();
        ctx.RegisterName("PART_ScrollContentPresenter", presenter);

        dock.Children.Add(bar);
        dock.Children.Add(presenter); // fills the remaining space
        return dock;
    });

    private static Style ScrollViewerTheme()
        => new Style { Key = "Theme.ScrollViewer" }.Set(Control.TemplateProperty, ScrollViewerTemplate());
}

/// <summary>
/// The check/radio glyph cell of a <see cref="CheckBox"/>/<see cref="RadioButton"/> default template
/// (design doc §12.7, spec line 660): an internal leaf that reads the owning toggle's
/// <see cref="ToggleButton.IsChecked"/> and the glyph-set resource (<see cref="ThemeKeys.CheckBoxGlyphs"/>
/// / <see cref="ThemeKeys.RadioGlyphs"/>; ASCII <c>[ ] [x] [-]</c> / <c>( ) (*)</c> by default,
/// overridable at any chain scope) and draws the matching glyph. The glyph is foreground text in the
/// toggle's <see cref="Control.Foreground"/> (inherited).
/// </summary>
internal sealed class ToggleGlyph : UIElement, IValueObserver<bool?>
{
    private readonly string _glyphKey;
    private IDisposable? _checkedObserver;

    internal ToggleGlyph(string glyphKey) => _glyphKey = glyphKey;

    private ToggleButton? Owner => TemplatedParent as ToggleButton;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        // Re-render when the owning toggle's checked state flips (the glyph swap is render-only — the
        // ASCII triple is equal-width; a Unicode swap that changed width would also need re-measure).
        if (Owner is { } owner)
            _checkedObserver = owner.AddObserver(ToggleButton.IsCheckedProperty, this);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        _checkedObserver?.Dispose();
        _checkedObserver = null;
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    void IValueObserver<bool?>.OnPropertyChanged(UIObject source, UIProperty property, bool? oldValue, bool? newValue, BindingPriority priority)
        => InvalidateVisual();

    private GlyphSet Glyphs
        => this.TryFindResource(_glyphKey, out var value) && value is GlyphSet glyphs
            ? glyphs
            : new GlyphSet("[ ]", "[x]", "[-]");

    // The owner's checked state, defaulting to unchecked only when there is no owner — a null here is
    // the genuine *indeterminate* value, which the '?? false' coalesce must NOT collapse.
    private bool? CheckedState => Owner is { } owner ? owner.IsChecked : false;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var glyph = Glyphs.ForChecked(CheckedState);
        return new Size(Cursorial.Text.GraphemeWidth.StringWidth(glyph), 1);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEmpty)
            return;

        var glyph = Glyphs.ForChecked(CheckedState);
        var brush = Owner?.Foreground;
        if (brush is { })
            context.DrawText(0, 0, glyph, brush);
        else
            context.DrawText(0, 0, glyph, Colors.Default);
    }
}
