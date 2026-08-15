// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The <c>caps-*</c> style-class catalog — the names <c>StyleEngine.StampCapabilityClasses</c>
/// stamps on surface roots from the effective capability record (color tier, input, graphics,
/// glyph coverage). Public so tooling (the designer's selector completion) enumerates the same
/// vocabulary the engine writes; <c>caps-ascii</c> is reserved and never stamped (SD14), so it
/// is deliberately absent from <see cref="Names"/>.
/// </summary>
public static class CapabilityClasses
{
    public const string Truecolor = "caps-truecolor";
    public const string Ansi256 = "caps-ansi256";
    public const string Ansi16 = "caps-ansi16";
    public const string NoColor = "caps-nocolor";
    public const string Motion = "caps-motion";
    public const string KittyKeyboard = "caps-kitty-keyboard";
    public const string Images = "caps-images";
    public const string ImageClipping = "caps-image-clipping";
    public const string ImageOcclusion = "caps-image-occlusion";
    public const string NerdFont = "caps-nerdfont";
    public const string Emoji = "caps-emoji";
    public const string Unicode = "caps-unicode";
    public const string TextSizing = "caps-textsizing";
    public const string Local = "caps-local";

    /// <summary>Every stampable capability class, stamping order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        Truecolor, Ansi256, Ansi16, NoColor,
        Motion, KittyKeyboard,
        Images, ImageClipping, ImageOcclusion,
        NerdFont, Emoji, Unicode, TextSizing,
        Local
    ];
}
