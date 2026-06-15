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
    // Cell-faithful default look (design doc §11.8a; default-theme-adoption-spec.md): controls are
    // FILL-BOUNDED, not line-bounded. A control's resting extent is a SurfaceBrush fill (not a stroked
    // border); the interactive states are brush-pair flips into the palette spine — :pointerover = a
    // HoverBrush fill, :focus = reverse-video (TextBrush fill + WindowBackground text), :pressed/:default =
    // accent reverse-video (AccentBrush fill + OnAccentBrush text), :disabled = DisabledBackgroundBrush
    // + DisabledForegroundBrush. No FocusPen ring, no Pens.Double :default weight bump. The NoColor
    // interactive-state distinction (where the brush pairs resolve to Default) rides the caps-nocolor
    // theme-styles rules in CursorialThemeStyles (inherited TextElement.TextAttributes — Inverse / Faint —
    // honored by the Border fill + content text). BorderPen/FocusPen survive only as opt-in chrome, unread here.

    internal static void Populate(ResourceDictionary dict)
    {
        dict[typeof(Button)] = ButtonTheme();
        dict[typeof(RepeatButton)] = RepeatButtonTheme();
        dict[typeof(ToggleButton)] = ToggleButtonTheme();
        dict[typeof(CheckBox)] = ToggleGlyphTheme("Theme.CheckBox", ThemeKeys.CheckBoxGlyphs, ThemeKeys.GreenBrush, ThemeKeys.AmberBrush);
        dict[typeof(RadioButton)] = ToggleGlyphTheme("Theme.RadioButton", ThemeKeys.RadioGlyphs, ThemeKeys.AccentBrush, ThemeKeys.AmberBrush);
        dict[typeof(ScrollBar)] = ScrollBarTheme();
        dict[typeof(ScrollViewer)] = ScrollViewerTheme();
    }

    // ───────────────────────────── Button / RepeatButton / ToggleButton ─────────────────────────────

    // A fill-bounded single-content button (design doc §11.8a): a Border with Padding (1,0) wrapping a
    // RecognizesAccessKey ContentPresenter that auto-aliases the button's Content. The fill IS the button
    // (no resting border) — Background carries the SurfaceBrush spine and flips per interactive state
    // (hover/focus/pressed/disabled); the content text inherits Foreground, which the reverse-video focus
    // and accent pressed/default rules swap. The BorderPen binding stays so an app can opt a frame back in.
    private static ControlTemplate ButtonContentTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        // The face fill follows Button.Background (the WPF default-template wiring): a TemplateBinding
        // makes the resting SurfaceBrush + the per-state brush-pair flips paint the face, quantized per
        // the negotiated tier. No resting pen ⇒ no frame ⇒ a 1-row button (content at row 0).
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        // Opt-in border: the BorderPen binding is retained (unset by the default theme), so a consumer
        // that sets Control.BorderPen gets a framed button without a custom template.
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style ButtonTheme()
    {
        var theme = AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.Button" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));
        // :default (the Enter-default cue) — a resting accent reverse-video fill so the primary action
        // stands out; :focus/:pressed override it when the user interacts. The ▸ OK ◂ gutter brackets are
        // a deferred content nicety (spec §3).
        theme.Children.Add(new Style("^:default")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        return theme;
    }

    private static Style RepeatButtonTheme()
        => AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.RepeatButton" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    private static Style ToggleButtonTheme()
        => AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.ToggleButton" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    // The cell-faithful interactive states shared by the button family (design doc §11.8a): hover = a
    // fill swap; focus = reverse-video (TextBrush fill + WindowBackground text); pressed = accent reverse-video;
    // disabled = disabled fill + muted text. All brush-pair setters are ResourceReferences into the
    // palette spine (color tiers); the NoColor interactive-state distinction rides the caps-nocolor
    // theme-styles rules (inherited TextElement.TextAttributes — Inverse / Faint; see CursorialThemeStyles).
    // Ordered hover → focus → pressed → disabled so the higher-intent state wins on a pseudo-class tie.
    private static Style AddButtonStates(Style theme)
    {
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        theme.Children.Add(new Style("^:disabled")
            .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // The R2 palette spine (design doc §11.5 / §11.8a): a default-look control (no explicit Foreground /
    // Background) resolves its resting fill + ink from the ThemeKeys palette as DynamicResources, so it
    // re-skins per-variant on a dark/light flip or a single ThemeKeys override with zero template work.
    // The setters arm at the ControlTheme layer (below LocalValue), so an explicit value the consumer
    // sets always wins. Cell-faithful reversal of the WPF transparent-Background default: the resting
    // SurfaceBrush fill IS the control's extent (no border). The toggle-glyph controls (CheckBox/Radio)
    // do NOT take this spine — they stay transparent at rest (gallery: their normal fill is the page bg).
    private static Style ApplyPaletteSpine(Style theme)
        => theme
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush);

    // ───────────────────────────── CheckBox / RadioButton ─────────────────────────────

    // glyph cell + 1 space + ContentPresenter (spec line 660), wrapped in a fill Border. The glyph element
    // reads the owning toggle's IsChecked + the glyph-set resource (ASCII default, overridable).
    private static ControlTemplate ToggleGlyphTemplate(string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
        => new(ctx =>
               {
                   var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };

                   // The glyph cell overlays a zero-size Caret on the glyph's INNER cell (column 1 — the space inside
                   // [ ] / ( )). A single-cell Grid stacks both; the Caret is Left/Top-pinned with a 1-column left
                   // margin so it lands on the box's middle cell. It shows only under :focus-visible (driven in code by
                   // ToggleButton). Check/radio focus is this IN-BOX CARET, not reverse-video: a check/radio stretches
                   // to its StackPanel's width, so a reverse fill would span the whole row like a selection bar.
                   var glyphCell = new Grid();
                   var glyph = new ToggleGlyph(glyphKey, checkedMarkKey, indeterminateMarkKey);
                   ctx.RegisterName("PART_Glyph", glyph);

                   var caret = new Caret
                               {
                                   HorizontalAlignment = HorizontalAlignment.Left,
                                   VerticalAlignment = VerticalAlignment.Top,
                                   Margin = new Margins(1, 0, 0, 0),
                               };

                   ctx.RegisterName("PART_Caret", caret);
                   glyphCell.Children.Add(glyph);
                   glyphCell.Children.Add(caret);

                   var presenter = new ContentPresenter { RecognizesAccessKey = true };
                   ctx.RegisterName("PART_ContentPresenter", presenter);
                   row.Children.Add(glyphCell);
                   row.Children.Add(presenter);

                   // The face follows Control.Background — unset (transparent) at rest and there is no fill setter:
                   // check/radio never fill (a stretched row would fill full-width like a selection bar). The binding
                   // stays so a consumer that sets Background still paints. Focus is the in-box caret, not a fill.
                   var face = new Border { Child = row };
                   face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                   return face;
               });

    // themeKey is the diagnostic Style.Key (e.g. "Theme.CheckBox"); glyphKey is the ThemeKeys glyph-set
    // resource the ToggleGlyph leaf reads (already a fully-qualified "Theme.*" constant — never re-prefix it).
    // No fill at all: transparent at rest AND on hover (a stretched check/radio would fill full-width); the
    // glyph + content paint in the inherited Foreground, disabled = muted ink, focus = the in-box caret
    // (driven by ToggleButton), never reverse-video.
    private static Style ToggleGlyphTheme(string themeKey, string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
    {
        var theme = new Style { Key = themeKey }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ToggleGlyphTemplate(glyphKey, checkedMarkKey, indeterminateMarkKey));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

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

        // The line buttons drop out of Tab navigation (Focusable = false, IsTabStop = false): a
        // ScrollBar and its parts are driven by the scrolled content's keyboard + the mouse, never by
        // tabbing onto the arrows (WPF/Avalonia parity — ButtonBase opts Focusable in by default, so
        // these scrollbar parts must opt back out). The Track is already a non-focusable UIElement.
        var lineUp = new RepeatButton { Content = horizontal ? "◀" : "▲", Template = bareTemplate, Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_LineUpButton", lineUp);
        DockPanel.SetDock(lineUp, horizontal ? Dock.Left : Dock.Top);

        var lineDown = new RepeatButton { Content = horizontal ? "▶" : "▼", Template = bareTemplate, Focusable = false, IsTabStop = false };
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
            .SetResource(ScrollBar.BorderPenProperty, ThemeKeys.BorderPen);
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
/// (design doc §12.7, spec line 660): a leaf that reads the owning toggle's
/// <see cref="ToggleButton.IsChecked"/> and the glyph-set resource (<see cref="ThemeKeys.CheckBoxGlyphs"/>
/// / <see cref="ThemeKeys.RadioGlyphs"/>; ASCII <c>[ ] [x] [-]</c> / <c>( ) (*)</c> by default,
/// overridable at any chain scope) and draws the matching glyph. The glyph is foreground text in the
/// toggle's <see cref="Control.Foreground"/> (inherited). Public + XAML-authorable so the control themes
/// can be authored declaratively (Cursorial.UI.Themes.Xaml): set <see cref="GlyphKey"/> /
/// <see cref="CheckedMarkKey"/> / <see cref="IndeterminateMarkKey"/> in the template.
/// </summary>
public sealed class ToggleGlyph : UIElement, IValueObserver<bool?>
{
    private IDisposable? _checkedObserver;

    /// <summary>The <see cref="ThemeKeys"/> glyph-set resource key (the <c>(Unchecked, Checked, Indeterminate)</c> <see cref="GlyphSetCarrier"/> base).</summary>
    public string? GlyphKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the CHECKED inner mark (e.g. GreenBrush ✓ / AccentBrush ●); <c>null</c> leaves it in the foreground.</summary>
    public string? CheckedMarkKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the INDETERMINATE inner mark (e.g. AmberBrush ▪); <c>null</c> leaves it in the foreground.</summary>
    public string? IndeterminateMarkKey { get; set; }

    // The caps-unicode glyph-set override (design doc §12.7 / SD14): CursorialThemeStyles' `.caps-unicode`
    // rules set this per control type to opt the marks UP from the ASCII resource base to Unicode (✓/▪/●);
    // ToggleGlyph reads it off its Owner, falling back to the glyph-set resource when unset (a caps-ascii
    // terminal, or no caps-unicode source). Hosted on ToggleButton so a `.caps-unicode CheckBox`/
    // `RadioButton` theme-styles rule can set it. AffectsRender — the ASCII↔Unicode marks are equal-width.
    public static readonly AttachedProperty<GlyphSetCarrier?> GlyphsProperty =
        UIProperty.RegisterAttached<ToggleGlyph, ToggleButton, GlyphSetCarrier?>("Glyphs");

    static ToggleGlyph() => UIObject.AddGlobalEffects(PropertyEffects.AffectsRender, GlyphsProperty);

    /// <summary>Parameterless constructor for XAML; set <see cref="GlyphKey"/> + the mark keys via properties.</summary>
    public ToggleGlyph() { }

    /// <summary>
    /// The code-first constructor. <paramref name="checkedMarkKey"/> / <paramref name="indeterminateMarkKey"/>
    /// are ThemeKeys brush resources that color the INNER mark for the checked / indeterminate states
    /// (CheckBox ✓ = GreenBrush, ▪ = AmberBrush; RadioButton ● = AccentBrush); null leaves the mark in the
    /// inherited foreground. The brackets always paint in the foreground.
    /// </summary>
    public ToggleGlyph(string glyphKey, string? checkedMarkKey = null, string? indeterminateMarkKey = null)
    {
        GlyphKey = glyphKey;
        CheckedMarkKey = checkedMarkKey;
        IndeterminateMarkKey = indeterminateMarkKey;
    }

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

    private GlyphSetCarrier Glyphs
    {
        get
        {
            // The .caps-unicode override (set on the Owner by a theme-styles rule) wins; else the glyph-set
            // resource (the ASCII base, themeable at any scope); else the hard ASCII fallback.
            if (Owner?.GetValue(GlyphsProperty) is { } unicode)
                return unicode;
            return GlyphKey is { } key && this.TryFindResource(key, out var value) && value is GlyphSetCarrier glyphs
                ? glyphs
                : new GlyphSetCarrier("[ ]", "[x]", "[-]");
        }
    }

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
        var foreground = Owner?.Foreground;
        // The glyph honors the inherited TextElement.TextAttributes (None for an ordinary control), so a
        // NoColor disabled check/radio dims with Faint to match its (Faint) content text — the whole control
        // reads as disabled, not just its label (review #1 follow-up).
        var attrs = TextElement.GetTextAttributes(this);

        // Bracket-neutral colored mark (gallery idiom): the box "[ ]" / "( )" paints in the inherited
        // foreground while the inner mark (✓ / ▪ / ●) takes its state color — but only when a mark color
        // resolves AND the glyph is the canonical open+mark+close triple. Otherwise the whole glyph paints
        // in the foreground (e.g. unchecked, a custom non-triple glyph, or no mark color configured).
        if (MarkBrush(CheckedState) is { } mark && glyph.Length >= 3)
        {
            var open = glyph[..1];
            var inner = glyph[1..^1];
            var openWidth = Cursorial.Text.GraphemeWidth.StringWidth(open);
            DrawAt(context, 0, open, foreground, attrs);
            DrawAt(context, openWidth, inner, mark, attrs);
            DrawAt(context, openWidth + Cursorial.Text.GraphemeWidth.StringWidth(inner), glyph[^1..], foreground, attrs);
            return;
        }

        DrawAt(context, 0, glyph, foreground, attrs);
    }

    private static void DrawAt(RenderContext context, int column, string text, IBrush? brush, TextAttributes attributes)
    {
        var style = new CellStyle().WithAttributes(attributes);
        if (brush is { })
            context.DrawText(column, 0, text, brush, baseStyle: style);
        else
            context.DrawText(column, 0, text, Colors.Default, baseStyle: style);
    }

    // The mark color for the checked / indeterminate states (a ThemeKeys brush resource resolved through
    // the chain); null for the unchecked state, when no key was configured, or when it does not resolve.
    private IBrush? MarkBrush(bool? state)
    {
        var key = state switch { true => CheckedMarkKey, null => IndeterminateMarkKey, _ => null };
        return key is not null && this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
    }
}
