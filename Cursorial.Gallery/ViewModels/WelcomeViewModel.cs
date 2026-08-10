using System.Windows.Input;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Themes;

namespace Cursorial.Gallery.ViewModels;

public class WelcomeViewModel : PageViewModel
{
    public override string Title => "Welcome";
    public override string Summary => "This gallery is a showcase of Cursorial.UI, a rich TUI framework inspired by WPF/Avalonia.";

    public RichText WelcomeTextPreamble
    {
        get { return field ??= MakeWelcomeText(FigletFonts.SmallSlant, "welcome to", new(Scale: 2)); }
    }

    public RichText WelcomeText
    {
        get { return field ??= MakeWelcomeText(FigletFonts.Slant, "cursorial", new(Scale: 6)); }
    }

    public ICommand? ShowUserOptionsCommand => UIApplication.Current?.ShowUserOptionsCommand;

    public string EpilogueText
        => $"[p trim=char wrap=character align=center]Based on the detected capabilities of your terminal, you may see a combination of " +
           $"[fg={ThemeKeys.CoolBrush}]Figlet fonts[/fg] (big ascii art glyphs), " +
           $"[fg={ThemeKeys.RedBrush}]scaled text[/fg], and/or an actual " +
           $"[fg={ThemeKeys.SuccessBrush}]raster image[/fg] above.[br/][br/]" +
           $"Tiered, capability-based presentation is a core design pillar of Cursorial.[/p]";

    private static RichText MakeWelcomeText(IGlyphFont font, string text, TextSizing? sizing = null)
    {
        var rtb = new RichTextBuilder(CellStyle.Transparent.WithForeground(Color.Default), TextTrimming.CharacterEllipsis, WrapMode.NoWrap);

        foreach (var line in text.Split('\n'))
            rtb.Run(line, new GlyphSource(font, sizing ?? TextSizing.Normal));
        
        return rtb.Build();
    }
}