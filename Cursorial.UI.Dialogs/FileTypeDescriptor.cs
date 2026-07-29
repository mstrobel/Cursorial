using Cursorial.Drawing.Media;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// One row of the <see cref="FileTypeIcons"/> table: everything a file listing needs to present a single kind
/// of file-system entry — the four <b>text</b> tiers of the framework's capability-tiered
/// <see cref="Icon"/> (design doc §12 / the command-bars guide) plus the human-readable
/// <see cref="KindLabel"/> the Open/Save dialog's Details view shows in its <i>Type</i> column
/// ("PNG image", "Folder", "Lua source", "Shell script" — the labels are lifted verbatim from the file-dialog
/// design page).
/// <para>
/// <b>Why the label lives here.</b> The icon and the type label are two renderings of the same fact ("this is a
/// Lua source file"). Authored apart they drift — a new extension gets a glyph and no label, or a renamed label
/// stops matching its icon. Keeping them on one record makes that impossible to express, and lets the
/// table-completeness test (<c>FileTypeIconsTests</c>) assert both in a single pass.
/// </para>
/// <para>
/// <b>The tiers.</b> This record does not invent a tier system — it feeds the framework's. <see cref="Glyph"/>
/// is the Nerd Font codepoint the <see cref="Icon"/> shows when <see cref="UIApplication.NerdFontAvailable"/>
/// is set (one cell, per-extension specificity); <see cref="Emoji"/> is the double-width color glyph shown
/// unless <see cref="UIApplication.EmojiAvailable"/> is disabled (broad category, <b>two</b> cells — see the
/// width note below); <see cref="Text"/> is the single-width Unicode floor that is always renderable. The
/// framework has no ASCII tier, so <see cref="Ascii"/> is offered as a 7-bit <i>substitute floor</i>: callers
/// that must not emit non-ASCII (a terminal with a broken font, a plain-text export of the listing) pass
/// <c>asciiFloor: true</c> to <see cref="ToIconCarrier"/>/<see cref="CreateIcon"/> and it takes the
/// <see cref="Icon.Text"/> slot. The graphics-protocol <see cref="Icon.Image"/> tier is deliberately left
/// unset — this is a glyph table, and per-file-type raster art is an app's business.
/// </para>
/// <para>
/// <b>Width discipline (the reason the completeness test exists).</b> <see cref="Icon"/> measures at its
/// <i>resolved</i> tier's width — an emoji-tier icon budgets 2 cells (its right-hand continuation cell) where
/// the glyph/Unicode/ASCII tiers budget 1, and grid safety lives in that measurement rather than in hiding the
/// tier (FB-15). So the table is only safe if every emoji really is double-width and every other tier really is
/// single-width; a two-cell "Unicode" floor would silently shove a listing's Name column one cell right on
/// terminals without a Nerd Font. <c>FileTypeIconsTests.EveryEntry_HasEveryTier_AtItsTierWidth</c> pins that
/// with <see cref="Cursorial.Text.GraphemeWidth"/>, so an entry added with the wrong glyph fails the build
/// rather than the layout.
/// </para>
/// </summary>
/// <param name="Id">
/// A stable, lowercase identifier for the row ("png", "targz", "folder", "license"). Unique across the table;
/// used for diagnostics and as the dedupe key behind <see cref="FileTypeIcons.All"/> (one row may be registered
/// under many extensions — "jpg", "jpeg" and "jpe" all resolve to the single "jpeg" row).
/// </param>
/// <param name="Category">The broad family — the unit at which the emoji/Unicode/ASCII tiers are authored.</param>
/// <param name="KindLabel">The Details view's <i>Type</i> column text, e.g. "PNG image" or "Shell script".</param>
/// <param name="Glyph">The Nerd Font codepoint (single cell) — the <see cref="Icon.Glyph"/> tier.</param>
/// <param name="Emoji">The color-emoji glyph (double width) — the <see cref="Icon.Emoji"/> tier.</param>
/// <param name="Text">The single-width Unicode floor — the <see cref="Icon.Text"/> tier.</param>
/// <param name="Ascii">The 7-bit substitute floor, one printable ASCII character.</param>
public sealed record FileTypeDescriptor(string Id,
                                        FileTypeCategory Category,
                                        string KindLabel,
                                        string Glyph,
                                        string Emoji,
                                        string Text,
                                        string Ascii)
{
    /// <summary>
    /// Projects the row onto an <see cref="IconCarrier"/> — the immutable, theme-resource-safe carrier the
    /// <c>{Icon …}</c> markup extension produces, so a file listing's item template can bind an
    /// <see cref="Icon"/> straight from a view model without touching the UI thread.
    /// </summary>
    /// <param name="asciiFloor">
    /// When <see langword="true"/>, <see cref="Ascii"/> takes the <see cref="Icon.Text"/> slot instead of
    /// <see cref="Text"/> — a 7-bit floor for terminals (or transcripts) that cannot carry the Unicode one.
    /// </param>
    public IconCarrier ToIconCarrier(bool asciiFloor = false)
        => new()
           {
               Glyph = Glyph,
               GlyphWidth = 2, // some Nerd Font codepoints are variably-sized or double-cell
               Emoji = Emoji,
               Text = asciiFloor ? Ascii : Text,
               IconBrush = CategoryBrush(Category)
           };

    private IBrush? CategoryBrush(FileTypeCategory category)
    {
        string? key = category switch
                      {
                          FileTypeCategory.Unknown    => null,
                          FileTypeCategory.Folder     => ThemeKeys.AnsiCyan,
                          FileTypeCategory.Drive      => null,
                          FileTypeCategory.Place      => null,
                          FileTypeCategory.Text       => ThemeKeys.AnsiLightBlack,
                          FileTypeCategory.Document   => ThemeKeys.AnsiLightBlack,
                          FileTypeCategory.Source     => ThemeKeys.AnsiGreen,
                          FileTypeCategory.Markup     => ThemeKeys.AnsiGreen,
                          FileTypeCategory.Data       => ThemeKeys.AnsiYellow,
                          FileTypeCategory.Image      => ThemeKeys.AnsiMagenta,
                          FileTypeCategory.Vector     => ThemeKeys.AnsiMagenta,
                          FileTypeCategory.Audio      => ThemeKeys.AnsiBlue,
                          FileTypeCategory.Video      => ThemeKeys.AnsiBlue,
                          FileTypeCategory.Archive    => ThemeKeys.AnsiRed,
                          FileTypeCategory.Executable => ThemeKeys.AnsiLightCyan,
                          FileTypeCategory.Library    => ThemeKeys.AmberBrush,
                          FileTypeCategory.Font       => ThemeKeys.RedBrush,
                          _                           => ThemeKeys.AnsiLightBlack
                      };

        if (key is null) return null;

        var themeVariant = UIApplication.Current?.ActualThemeVariant;
        if (themeVariant is null) return null;

        return ResourceExtensions.WalkApplicationTail(key,
                                                      themeVariant.GetValueOrDefault(),
                                                      null,
                                                      out object? value) &&
               value is IBrush
                   ? (IBrush) value
                   : null;
    }

    /// <summary>
    /// Builds a fresh <see cref="Icon"/> for the row. A new instance every call — an <see cref="Icon"/> is a
    /// <see cref="UIElement"/> and can only live at one place in one tree (use <see cref="ToIconCarrier"/> when
    /// the value must be shared, cached or parked in a resource dictionary).
    /// </summary>
    /// <param name="asciiFloor">See <see cref="ToIconCarrier"/>.</param>
    public Icon CreateIcon(bool asciiFloor = false) => new()
                                                      {
                                                          Glyph = Glyph,
                                                          GlyphWidth = 2,
                                                          Emoji = Emoji,
                                                          Text = asciiFloor ? Ascii : Text
                                                      };
}
