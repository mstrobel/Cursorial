namespace Cursorial.Tests.UI.Xaml.UnmappedExtensions;

/// <summary>
/// Lives in a namespace the default UI xmlns does NOT map — X53b proves load-time extension
/// resolution honors the prefix's OWN xmlns (the parser-stamped binding) instead of the
/// default-UI fallback the builder used to hardcode.
/// </summary>
public sealed class QuirkExtension : Cursorial.UI.Xaml.MarkupExtension
{
    public string? Text { get; set; }

    public override object ProvideValue(System.IServiceProvider serviceProvider) => $"quirk:{Text}";
}
