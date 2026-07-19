using System.Globalization;

using Cursorial.Output;
using Cursorial.Rendering;
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
        for (int i = 0; i < FormatPresets.Length; i++)
        {
            if (FormatPresets[i].Format == format)
                return i;
        }
        return 0;
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
        var panel = new StackPanel(); // vertical
        panel.Children.Add(content);

        var window = new Window
        {
            Title = title,
            CanResize = false,
            Padding = new Margins(1, 0, 1, 0),
            SizeToContent = SizeToContent.WidthAndHeight,
            SizeToContentMode = SizeToContentMode.Always,
            AutoFitToViewport = true, // a rebuilt pane must keep the dialog on-screen (the TaskDialog policy)
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Shadow = WindowShadow.Default,
            Content = panel,
        };
        window.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ElevationDialog);
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);

        if (buttons.Length > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            foreach (var (caption, onClick) in buttons)
            {
                var button = new Button { Content = caption };
                var captured = onClick;
                button.Click += (_, _) => captured();
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }

        return window;
    }

    /// <summary>A muted section-label TextBlock (the dialogs' field captions).</summary>
    internal static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        return block;
    }
}
