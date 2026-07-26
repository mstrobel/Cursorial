using System.Windows.Input;

using Cursorial.Output;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Themes;

using Style = Cursorial.Output.Style;

namespace Cursorial.Gallery.ViewModels;

public class WelcomeViewModel : PageViewModel
{
    public override string Title => "Welcome";
    public override string Summary => "This gallery is a showcase of Cursorial.UI, a rich TUI framework inspired by WPF/Avalonia.";
    
    public RichText WelcomeTextPreamble
    {
        get { return field ??= MakeWelcomeText(FigletFonts.LCDMatrix, "welcome to", new(Scale: 2)); }
    }

    public RichText WelcomeText
    {
        get { return field ??= MakeWelcomeText(FigletFonts.Slant, "cursorial", new(Scale: 6)); }
    }

    public ICommand? ShowUserOptionsCommand => UIApplication.Current?.ShowUserOptionsCommand;

    public string EpilogueText
        => $"Based on the detected capabilities of your terminal, you may see a combination of " +
           $"[brush={ThemeKeys.CoolBrush}]Figlet fonts[/brush] (big ascii art glyphs), " +
           $"[brush={ThemeKeys.RedBrush}]scaled text[/brush], and/or an actual " +
           $"[brush={ThemeKeys.SuccessBrush}]raster image[/brush] above.[br/][br/]" +
           $"Tiered, capability-based presentation is a core design pillar of Cursorial.";

    private static RichText MakeWelcomeText(IGlyphFont font, string text, TextSizing? sizing = null)
    {
        var rtb = new RichTextBuilder(Style.Transparent.WithForeground(Color.Default));

        foreach (var line in text.Split('\n'))
        {
            if (sizing is {} sz)
                rtb.SizedText(line, sz, fallback: font);
            else
                rtb.Figlet(line, font);

        }
        return rtb.Build();
    }
}