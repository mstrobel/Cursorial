using System.Globalization;

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The shared plumbing of the DataGrid dialog suite (expression editor, Filter Builder, rules
/// manager — design doc §9.1 + the mockup's cfmgr/fbuilder/feditor windows): literal text ⇄ typed
/// key-value conversion (the Builder's value cells and the rule editor's condition values funnel
/// through ONE parser so their semantics can't drift), the named <see cref="CellFormat"/> presets
/// the rule dialogs pick from, and the window scaffold every dialog composes (title strip +
/// content + button row over <see cref="ThemeKeys.ElevationDialog"/> — the TaskDialog idiom).
/// </summary>
internal static class DataGridDialogHelpers
{
    // ── Literal text ⇄ typed value (the Builder/rule-editor value cells) ─────────────────────────

    /// <summary>
    /// Parses a value cell's text against a column's key type. Empty ⇒ null (the (Blanks)
    /// convention — meaningful for =/&lt;&gt; only); <c>'quoted'</c> and <c>#dated#</c> wrappers
    /// are stripped so ToText-seeded literals round-trip; numerics parse invariant-first (§9.1 —
    /// saved filters are portable) with a current-culture fallback for hand-typed text.
    /// </summary>
    internal static bool TryParseLiteral(Type? keyType, string text, out object? value)
    {
        value = null;
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            text = text[1..^1].Replace("''", "'");
        else if (text.Length >= 2 && text[0] == '#' && text[^1] == '#')
            text = text[1..^1];
        if (text.Length == 0)
            return true;

        var target = keyType is null ? typeof(string) : Nullable.GetUnderlyingType(keyType) ?? keyType;
        try
        {
            if (target == typeof(string))
                value = text;
            else if (target.IsEnum)
            {
                if (!Enum.TryParse(target, text, ignoreCase: true, out var parsed))
                    return false;
                value = parsed;
            }
            else if (target == typeof(bool))
            {
                if (!bool.TryParse(text, out bool b))
                    return false;
                value = b;
            }
            else if (target == typeof(DateOnly))
            {
                if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var d) &&
                    !DateOnly.TryParse(text, CultureInfo.CurrentCulture, out d))
                {
                    return false;
                }
                value = d;
            }
            else if (target == typeof(DateTime))
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) &&
                    !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
                {
                    return false;
                }
                value = dt;
            }
            else
            {
                value = Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
            }
            return true;
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            // The current-culture retry covers hand-typed decimal separators; anything still
            // unparseable is a validation error the caller surfaces on its strip.
            try
            {
                value = Convert.ChangeType(text, target, CultureInfo.CurrentCulture);
                return true;
            }
            catch (Exception e2) when (e2 is InvalidCastException or FormatException or OverflowException)
            {
                value = null;
                return false;
            }
        }
    }

    /// <summary>The inverse: a typed literal rendered as editable cell text (unquoted — the cell IS the value).</summary>
    internal static string FormatLiteral(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // ── Format presets (the rule dialogs' "Format" picker — mockup palette, raw data colors) ─────

    /// <summary>
    /// The named cell-format presets (the mockup's "Green fill ▾"/"Bold accent ▾" picks). Raw RGB
    /// (Tokyo-Night anchored) rather than theme brushes deliberately: <see cref="CellFormat"/> is
    /// pure data evaluated in the engine — it cannot carry a resource reference (§2.7).
    /// </summary>
    internal static readonly (string Name, CellFormat Format)[] FormatPresets =
    [
        ("Green text", new CellFormat(Foreground: Color.FromRgb(0x9E, 0xCE, 0x6A))),
        ("Amber text", new CellFormat(Foreground: Color.FromRgb(0xE0, 0xAF, 0x68))),
        ("Red text", new CellFormat(Foreground: Color.FromRgb(0xF7, 0x76, 0x8E))),
        ("Bold accent", new CellFormat(Foreground: Color.FromRgb(0x7A, 0xA2, 0xF7), Bold: true)),
        ("Green fill", new CellFormat(Foreground: Color.FromRgb(0x1A, 0x1B, 0x26), Background: Color.FromRgb(0x9E, 0xCE, 0x6A))),
        ("Red fill", new CellFormat(Foreground: Color.FromRgb(0x1A, 0x1B, 0x26), Background: Color.FromRgb(0xF7, 0x76, 0x8E))),
        ("Red fill + bold", new CellFormat(Foreground: Color.FromRgb(0x1A, 0x1B, 0x26), Background: Color.FromRgb(0xF7, 0x76, 0x8E), Bold: true)),
        ("Dim", new CellFormat(Foreground: Color.FromRgb(0x56, 0x5F, 0x89))),
        ("Inverse", new CellFormat(Inverse: true)),
    ];

    /// <summary>The preset index whose format equals <paramref name="format"/>, or 0 (edit-seed best match).</summary>
    internal static int PresetIndexOf(CellFormat format)
    {
        int exact = ExactPresetIndexOf(format);
        return exact >= 0 ? exact : 0;
    }

    /// <summary>The preset index whose format EXACTLY equals <paramref name="format"/>, or −1 (⇒ a
    /// custom format the "Custom…" pick must carry rather than silently snapping to preset 0).</summary>
    internal static int ExactPresetIndexOf(CellFormat format)
    {
        for (int i = 0; i < FormatPresets.Length; i++)
        {
            if (FormatPresets[i].Format == format)
                return i;
        }
        return -1;
    }

    // ── Color entry (the custom pickers — §10.6) ─────────────────────────────────────────────────

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = Color.FromRgb(0xF7, 0x76, 0x8E), ["green"] = Color.FromRgb(0x9E, 0xCE, 0x6A),
        ["amber"] = Color.FromRgb(0xE0, 0xAF, 0x68), ["blue"] = Color.FromRgb(0x7A, 0xA2, 0xF7),
        ["cyan"] = Color.FromRgb(0x7D, 0xCF, 0xFF), ["magenta"] = Color.FromRgb(0xBB, 0x9A, 0xF7),
        ["white"] = Color.FromRgb(0xC0, 0xCA, 0xF5), ["black"] = Color.FromRgb(0x1A, 0x1B, 0x26),
        ["gray"] = Color.FromRgb(0x56, 0x5F, 0x89), ["grey"] = Color.FromRgb(0x56, 0x5F, 0x89),
    };

    /// <summary>
    /// Parses a custom color: a small named-color set, else a <c>#RGB</c> / <c>#RRGGBB</c> /
    /// <c>#RRGGBBAA</c> hex through the Core <see cref="Color.TryParseHex"/> (the stack's
    /// <c>#RRGGBBAA</c> alpha convention). Blank/whitespace ⇒ no color (<see langword="false"/>, an
    /// unset lane); a malformed value ⇒ <see langword="false"/> too.
    /// </summary>
    internal static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        return NamedColors.TryGetValue(s, out color) || Color.TryParseHex(s, out color, out _);
    }

    /// <summary>The <c>#RRGGBB</c>/<c>#RRGGBBAA</c> text for a color (the custom picker's seed), or "" for none.</summary>
    internal static string ColorToHex(Color? color)
    {
        if (color is not { } c || c.Kind != ColorKind.Rgb)
            return string.Empty;
        return c.Alpha == 255
            ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}"
            : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}{c.Alpha:X2}";
    }

    // ── The dialog window scaffold (title/content/buttons — every suite dialog composes this) ────

    /// <summary>
    /// Builds the suite's standard dialog window: framework chrome (title bar, ✕), the content
    /// panel, and a right-aligned button row, painted on <see cref="ThemeKeys.ElevationDialog"/>.
    /// Button actions are the dialog's own (an Apply that fails validation simply doesn't close —
    /// the veto lives with the dialog, not the scaffold).
    /// </summary>
    internal static Window CreateDialogWindow(string title, UIElement content,
                                              params (string Caption, Action OnClick)[] buttons)
    {
        return CreateDialogWindow(title, content, null, null, null, null, buttons);
    }

    /// <summary>
    /// Builds the suite's standard dialog window: framework chrome (title bar, ✕), the content
    /// panel, and a right-aligned button row, painted on <see cref="ThemeKeys.ElevationDialog"/>.
    /// Button actions are the dialog's own (an Apply that fails validation simply doesn't close —
    /// the veto lives with the dialog, not the scaffold).
    /// </summary>
    internal static Window CreateDialogWindow(string title, UIElement content,
                                              string? defaultButton = null, string? cancelButton = null,
                                              Size? minSize = null,
                                              Size? maxSize = null,
                                              params (string Caption, Action OnClick)[] buttons)
    {
        var root = new DockPanel { LastChildFill = true };
        var panel = new StackPanel { TabIndex = 0 }; // vertical
        panel.Children.Add(content);

        var window = new Window
        {
            Title = title,
            CanResize = false,
            Padding = new Margins(2, 1),
            SizeToContent = SizeToContent.WidthAndHeight,
            SizeToContentMode = SizeToContentMode.Always,
            AutoFitToViewport = true, // a rebuilt pane must keep the dialog on-screen (the TaskDialog policy)
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Shadow = WindowShadow.Default,
            Content = root,
        };

        if (minSize is { Columns: var minCols and > 0 })
            window.MinWidth = minCols;

        if (minSize is { Rows: var minRows and > 0 })
            window.MinHeight = minRows;

        if (maxSize is { Columns: var maxCols and > 0 })
            window.MaxWidth = maxCols;

        if (maxSize is { Rows: var maxRows and > 0 })
            window.MaxHeight = maxRows;

        window.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ElevationDialog);
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);

        if (buttons.Length > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                HorizontalAlignment = HorizontalAlignment.Right,
                TabIndex = 1
            };
            
            DockPanel.SetDock(row, Dock.Bottom);
            root.Children.Add(row);

            for (int i = 0; i < buttons.Length; i++)
            {
                var (caption, onClick) = buttons[i];
                var button = new Button { Content = caption };
                var captured = onClick;

                if (caption == defaultButton || defaultButton is null)
                    button.IsDefault = true;

                if (caption == cancelButton)
                    button.IsCancel = true;

                button.Click += (_, _) => captured();
                row.Children.Add(button);
            }
        }

        root.Children.Add(panel);
        return window;
    }

    /// <summary>A muted section-label TextBlock (the dialogs' field captions).</summary>
    internal static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = WrapMode.WordWrap };
        block.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        return block;
    }

    /// <summary>
    /// A stepped color-scale swatch: <paramref name="cells"/> ▒ cells split evenly across the stops
    /// IN ORDER — 2 cells per stop for a 3-color scale, 3 for a 2-color (the cfmgr mockup; the
    /// live-canary report was every cell inked with the LAST stop). Linear boundaries, so an uneven
    /// split spreads the remainder rather than piling it on one run.
    /// </summary>
    internal static StackPanel ScaleSwatch(IReadOnlyList<Color> stops, int cells = 6)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < stops.Count; i++)
        {
            int start = i * cells / stops.Count;
            int end = (i + 1) * cells / stops.Count;
            panel.Children.Add(new TextBlock
            {
                Text = new string('▒', Math.Max(1, end - start)),
                Foreground = new SolidColorBrush(stops[i]),
            });
        }
        return panel;
    }

    /// <summary>
    /// A small modal confirm over the suite scaffold (the editor→designer hop's representability
    /// warning): stacks <paramref name="messageLines"/> as muted text under the title, offers
    /// yes/no buttons, and completes <see langword="true"/> only on the affirmative (✕ / Esc /
    /// <paramref name="noCaption"/> all decline).
    /// </summary>
    internal static async Task<bool> ConfirmAsync(string title, string[] messageLines,
                                                  string yesCaption, string noCaption)
    {
        var content = new StackPanel(); // vertical
        foreach (string line in messageLines)
            content.Children.Add(Caption(line));

        Window window = null!;
        window = CreateDialogWindow(title, content,
            (yesCaption, () => window.Close(true)),
            (noCaption, () => window.Close(false)));
        return await window.ShowDialogAsync() is true;
    }
}

/// <summary>
/// A value-entry control that adapts to the column's key type (§10 value-entry): an
/// <see cref="Enum"/> (or <see cref="bool"/>) key gets a prepopulated <see cref="ComboBox"/> of its
/// valid names — so a user picks from the options instead of typing an enum member and hoping it
/// parses — while every other type keeps a free-text <see cref="TextBox"/>. Shared by the rule
/// editor's condition rows and the Filter Builder so their value entry can't drift. The exposed
/// <see cref="Value"/> string round-trips through the same <c>TryParseLiteral</c> lane either way
/// (enum names / <c>true|false</c> parse cleanly).
/// </summary>
internal sealed class ValueEditor
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;

    private ValueEditor(Control element, bool isChoice, Func<string> get, Action<string> set)
    {
        Element = element;
        IsChoice = isChoice;
        _get = get;
        _set = set;
    }

    /// <summary>The live control — a <see cref="ComboBox"/> for enum/bool, else a <see cref="TextBox"/>.</summary>
    internal Control Element { get; }

    /// <summary>Whether this editor is the prepopulated dropdown (vs a free-text box).</summary>
    internal bool IsChoice { get; }

    /// <summary>The current text value (the choice name for a dropdown, the typed text for a box).</summary>
    internal string Value { get => _get(); set => _set(value); }

    /// <summary>The backing text box, or null when this is a dropdown (tests / back-compat accessors).</summary>
    internal TextBox? AsTextBox => Element as TextBox;

    /// <summary>
    /// Builds the editor for <paramref name="keyType"/> seeded with <paramref name="seed"/>, calling
    /// <paramref name="onChanged"/> on every edit. Enum (incl. <see cref="Nullable{T}"/> of enum) and
    /// bool key types yield a name dropdown; everything else a text box.
    /// </summary>
    internal static ValueEditor Create([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] Type? keyType, string seed, Action onChanged, int minWidth = 8)
    {
        var underlying = keyType is null ? null : Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (underlying is { IsEnum: true })
        {
            // The dropdown shows each member's display text ([Display(Name)] where declared), but
            // Value round-trips the raw MEMBER name (what TryParseLiteral / the engine parse).
            var members = Enum.GetNames(underlying);
            var labels = members.Select(m => EnumDisplay.NameOf(underlying, m)).ToList();
            var combo = new ComboBox
            {
                ItemsSource = labels,
                MinWidth = Math.Max(minWidth, 8),
                SelectedIndex = Array.IndexOf(members, seed),
            };
            combo.SelectionChanged += (_, _) => onChanged();
            return new ValueEditor(combo, isChoice: true,
                () => combo.SelectedIndex >= 0 && combo.SelectedIndex < members.Length ? members[combo.SelectedIndex] : string.Empty,
                v => combo.SelectedIndex = Array.IndexOf(members, v));
        }
        if (underlying == typeof(bool))
        {
            List<string> items = ["true", "false"];
            var combo = new ComboBox
            {
                ItemsSource = items,
                MinWidth = Math.Max(minWidth, 8),
                SelectedItem = items.Contains(seed) ? seed : null,
            };
            combo.SelectionChanged += (_, _) => onChanged();
            return new ValueEditor(combo, isChoice: true,
                () => combo.SelectedItem as string ?? string.Empty,
                v => combo.SelectedItem = items.Contains(v) ? v : null);
        }

        var box = new TextBox { Text = seed, MinWidth = minWidth };
        box.TextChanged += (_, _) => onChanged();
        return new ValueEditor(box, isChoice: false, () => box.Text, v => box.Text = v);
    }
}
