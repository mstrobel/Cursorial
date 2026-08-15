using System.Collections.Immutable;

using Cursorial.Gallery.Infrastructure;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Inputs page — a showcase of the editable controls bound two-way to view-model state: a <c>TextBox</c>
/// (<see cref="Name"/>), a <c>PasswordBox</c> (<see cref="Password"/>, with a "reveal" <c>CheckBox</c> driving
/// <see cref="ShowPassword"/>), a <c>CheckBox</c> (<see cref="Subscribed"/>), a <c>Slider</c>
/// (<see cref="Volume"/>), and a multi-line <c>Journal</c> field (<see cref="Journal"/>) wired to
/// <see cref="UndoCommand"/>/<see cref="RedoCommand"/> — the live canary for multi-line editing + undo/redo
/// (the buttons and the field's own Ctrl+Z/Ctrl+Y both drive it). The live <see cref="Status"/> reflects every
/// bound value (the password as a length, never the plaintext) — proving the two-way binding round-trips.
/// </summary>
public sealed class InputsPageViewModel : PageViewModel
{
    private string _name = "";
    private string _password = "";
    private bool _subscribed = true;
    private double _volume = 40;
    private string _journal = "The quick brown fox\njumps over the lazy dog.";
    private bool _capSubscribed;

    public InputsPageViewModel()
    {
        // The editor supplies itself as the command parameter (x:Reference in the view), so undo/redo stay in the
        // view-model without it holding a control reference. Undo()/Redo() self-gate, so the buttons are safe no-ops
        // when there is nothing to undo.
        UndoCommand = new RelayCommand<TextBox>(editor => editor.Undo());
        RedoCommand = new RelayCommand<TextBox>(editor => editor.Redo());
        SelectedColor = Colors[5];
    }

    private IReadOnlyList<GlyphSource> RebuildGlyphSources()
    {
        IReadOnlyList<GlyphSource> sized = ImmutableList<GlyphSource>.Empty;

        var app = UIApplication.Current;

        if (app is not null && _capSubscribed is false)
        {
            _capSubscribed = true;
            app.EffectiveCapabilitiesChanged += OnEffectiveCapabilitiesChanged;
            app.BeginShutdown += OnAppBeginShutdown;
        }

        if (app?.EffectiveCapabilities.Output.TextSizing is { Scale: true })
        {
            sized =
            [
                new(null, new TextSizing(Scale: 2, Numerator: 1, Denominator: 2, Vertical: TextSizingVerticalAlignment.Center)),
                new(null, new TextSizing(Scale: 2)),
                new(null, new TextSizing(Scale: 3)),
                new(null, new TextSizing(Scale: 1, Numerator: 1, Denominator: 2, Vertical: TextSizingVerticalAlignment.Center)),
                new(null, new TextSizing(Scale: 1, Numerator: 1, Denominator: 2, Vertical: TextSizingVerticalAlignment.Top)),
                new(null, new TextSizing(Scale: 1, Numerator: 1, Denominator: 2, Vertical: TextSizingVerticalAlignment.Bottom))
            ];
        }

        IReadOnlyList<GlyphSource> fonts =
        [
            new(DecoratedFont.QuarterBlockUnderline),
            new(FigletFonts.CGA),
            new(FigletFonts.Mini),
            new(FigletFonts.MiniWi),
            new(FigletFonts.Small),
            new(FigletFonts.SmallSlant),
            new(FigletFonts.LCDMatrix)
        ];

        return [new(MonospaceFont.Default), ..sized, ..fonts];
    }

    private void OnAppBeginShutdown(object? sender, EventArgs e)
    {
        if (sender is not UIApplication app) return;
        app.EffectiveCapabilitiesChanged -= OnEffectiveCapabilitiesChanged;
        app.BeginShutdown -= OnAppBeginShutdown;
        _capSubscribed = false;

    }

    private void OnEffectiveCapabilitiesChanged(object? sender, EventArgs e)
    {
        GlyphSources = RebuildGlyphSources();
    }

    public override string Title => "Inputs";
    public override string Summary => "TextBox / PasswordBox / CheckBox / Slider + a multi-line undo/redo Journal, bound two-way.";

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) Raise(nameof(Status)); }
    }

    public string Password
    {
        get => _password;
        set { if (Set(ref _password, value ?? "")) Raise(nameof(Status)); }
    }

    /// <summary>Drives the <c>PasswordBox.RevealPassword</c> (two-way with the "Reveal" check box).</summary>
    public bool ShowPassword
    {
        get;
        set => Set(ref field, value);
    }

    public bool Subscribed
    {
        get => _subscribed;
        set { if (Set(ref _subscribed, value)) Raise(nameof(Status)); }
    }

    public bool? Mystery
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(Status)); }
    }

    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, value)) Raise(nameof(Status)); }
    }

    /// <summary>The multi-line Journal text (two-way) — the surface for the undo/redo canary.</summary>
    public string Journal
    {
        get => _journal;
        set { if (Set(ref _journal, value ?? "")) Raise(nameof(Status)); }
    }

    public IGlyphFont? SelectedFont { get; private set => Set(ref field, value); }

    public TextSizing? SelectedTextSize { get; private set => Set(ref field, value); }

    public GlyphSource? SelectedGlyphSource
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            SelectedFont = value?.Font;
            SelectedTextSize = value?.Sizing;
        }
    }

    public IReadOnlyList<GlyphSource> GlyphSources
    {
        get
        {
            if (field is null)
                GlyphSources = RebuildGlyphSources();

            return field!;
        }
        private set
        {
            var selectedSource = SelectedGlyphSource;

            Set(ref field, value);

            if (value.Contains(selectedSource))
                SelectedGlyphSource = selectedSource;
            else
                SelectedGlyphSource = value.FirstOrDefault();
        }
    }

    public IList<ColorInfo> Colors { get; } =
        [

            new(Brushes.Black, "Black"),
            new(Brushes.Red, "Red"),
            new(Brushes.Green, "Green"),
            new(Brushes.Yellow, "Yellow"),
            new(Brushes.Blue, "Blue"),
            new(Brushes.Magenta, "Magenta"),
            new(Brushes.Cyan, "Cyan"),
            new(Brushes.White, "White"),
            new(Brushes.LightBlack, "Light Black"),
            new(Brushes.LightRed, "Light Red"),
            new(Brushes.LightGreen, "Light Green"),
            new(Brushes.LightYellow, "Light Yellow"),
            new(Brushes.LightBlue, "Light Blue"),
            new(Brushes.LightMagenta, "Light Magenta"),
            new(Brushes.LightCyan, "Light Cyan"),
            new(Brushes.LightWhite, "Light White"),
        ];

    public ColorInfo SelectedColor
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>Reverts the most recent edit in the Journal field (the editor is the command parameter).</summary>
    public RelayCommand<TextBox> UndoCommand { get; }

    /// <summary>Re-applies the most recently undone Journal edit.</summary>
    public RelayCommand<TextBox> RedoCommand { get; }

    /// <summary>The live readout of every bound value — the password as a length, never the plaintext.</summary>
    public override string Status =>
        $"Name=\"{_name}\"  Password.Length={_password.Length}  Subscribed={_subscribed}  Mystery={Mystery?.ToString() ?? "<unset>"}  Volume={_volume:0}  Journal.Lines={JournalLineCount}";

    private int JournalLineCount => _journal.Length == 0 ? 0 : _journal.AsSpan().Count('\n') + 1;
    
}

public class ColorInfo
{
    public SolidColorBrush Color { get; }
    public string Name { get; }

    public ColorInfo(SolidColorBrush color, string name)
    {
        Color = color;
        Name = name;
    }
}