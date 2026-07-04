using Cursorial.Markup;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Text;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// A capability-tiered icon (design doc §12 / the command-bars guide). An author declares up to four
/// representations and the icon renders the highest-preference one that is both <b>provided</b> and
/// <b>supported</b> by the terminal:
/// <list type="number">
/// <item><see cref="Glyph"/> — a Nerd Font codepoint, shown only when <see cref="UIApplication.NerdFontAvailable"/>
/// is set (there is no probe for Nerd Font coverage, so it is an explicit app opt-in — CD-P2J-1).</item>
/// <item><see cref="Image"/>/<see cref="ImageUri"/> — a graphics-protocol image, shown when the effective protocols
/// can carry it (Kitty/iTerm2/Sixel — the <see cref="ImagePresenter"/> gate, honoring
/// <see cref="UIApplication.CapabilityOverrides"/>); otherwise it falls through to the
/// <see cref="Text"/> tier (the image hosts <see cref="Text"/> as its placeholder).</item>
/// <item><see cref="Emoji"/> — a color-emoji glyph, shown only when <see cref="UIApplication.EmojiAvailable"/>
/// is set (FB-15: a user opt-in, default absent — same no-tofu posture as Nerd Font). Emoji are
/// <b>double-width</b> in the cell grid; the icon measures at its resolved tier's width, so an emoji-tier
/// icon budgets 2 cells where the other glyph tiers typically budget 1.</item>
/// <item><see cref="Text"/> — a single-width Unicode glyph, always renderable (the guaranteed floor).</item>
/// </list>
/// Usable standalone (<c>&lt;Icon Glyph="…" Emoji="📁" Text="F"/&gt;</c>), as a control's content
/// (<c>&lt;Button&gt;&lt;Icon …/&gt;&lt;/Button&gt;</c>), or — concisely — via the <c>{Icon …}</c> markup extension.
/// It re-resolves its tier live when the terminal renegotiates graphics, capability overrides change, or the
/// <see cref="UIApplication.NerdFontAvailable"/>/<see cref="UIApplication.EmojiAvailable"/> opt-ins
/// flip. <see cref="Control.Foreground"/> (inherited) tints the glyph/emoji/text tiers.
/// </summary>
[ContentProperty(nameof(Text))]
public class Icon : Control
{
    /// <summary>The Nerd Font codepoint(s) — the preferred tier when <see cref="UIApplication.NerdFontAvailable"/>.</summary>
    public static readonly StyledProperty<string?> GlyphProperty =
        UIProperty.Register<Icon, string?>(nameof(Glyph), changed: OnTierInputChanged);

    /// <summary>The total display width of the Nerd Font codepoint(s) — useful for variable-width glyphs.</summary>
    public static readonly StyledProperty<int> GlyphWidthProperty =
        UIProperty.Register<Icon, int>(nameof(GlyphWidth),
                                       defaultValue: 1,
                                       coerce: (_, baseValue) => Math.Max(1, baseValue));

    /// <inheritdoc cref="IconTierProperty"/>
    protected static readonly UIPropertyKey<IconTier> IconTierPropertyKey =
        UIProperty.RegisterReadOnly<Icon, IconTier>(nameof(IconTier));

    /// <summary>The image as explicit bytes — the middle tier when a graphics protocol can carry it.</summary>
    protected static readonly StyledProperty<IconTier> IconTierProperty = IconTierPropertyKey.Property;

    /// <summary>The image as explicit bytes — the middle tier when a graphics protocol can carry it.</summary>
    public static readonly StyledProperty<ImageData?> ImageProperty =
        UIProperty.Register<Icon, ImageData?>(nameof(Image), changed: OnTierInputChanged);

    /// <summary>A URI the image loads from (the XAML-friendly source; <see cref="Image"/> takes precedence).</summary>
    public static readonly StyledProperty<Uri?> ImageUriProperty =
        UIProperty.Register<Icon, Uri?>(nameof(ImageUri), changed: OnTierInputChanged);

    /// <summary>The color-emoji glyph — the tier between Image and Text, shown only when
    /// <see cref="UIApplication.EmojiAvailable"/> is set (FB-15; measures double-width).</summary>
    public static readonly StyledProperty<string?> EmojiProperty =
        UIProperty.Register<Icon, string?>(nameof(Emoji), changed: OnTierInputChanged);

    /// <summary>The single-width Unicode fallback glyph — always renderable (the guaranteed floor).</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<Icon, string?>(nameof(Text), changed: OnTierInputChanged);

    /// <summary>The resolved visual the template renders — a <see cref="string"/> (glyph/text tier) or an
    /// <see cref="ImagePresenter"/> (image tier). Internal: driven by <see cref="ResolveTier"/>, bound by the theme.</summary>
    internal static readonly StyledProperty<object?> ResolvedContentProperty =
        UIProperty.Register<Icon, object?>(nameof(ResolvedContent));

    private UIApplication? _subscribedApp; // the app whose capability/nerd-font events we're subscribed to

    /// <inheritdoc cref="GlyphProperty"/>
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <inheritdoc cref="GlyphWidthProperty"/>
    public int GlyphWidth { get => GetValue(GlyphWidthProperty); set => SetValue(GlyphWidthProperty, value); }

    /// <inheritdoc cref="ImageProperty"/>
    public ImageData? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    /// <inheritdoc cref="ImageUriProperty"/>
    public Uri? ImageUri { get => GetValue(ImageUriProperty); set => SetValue(ImageUriProperty, value); }

    /// <inheritdoc cref="EmojiProperty"/>
    public string? Emoji { get => GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <inheritdoc cref="ResolvedContentProperty"/>
    internal object? ResolvedContent { get => GetValue(ResolvedContentProperty); private set => SetValue(ResolvedContentProperty, value); }

    /// <summary>The tier currently rendered (test observability).</summary>
    public IconTier Tier { get => GetValue(IconTierProperty); protected set => SetValue(IconTierPropertyKey, value); }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        if (UIApplication.Current is { } app)
        {
            app.CapabilitiesChanged += OnCapabilitiesChanged; // graphics (image tier) renegotiation
            app.CapabilityOverridesChanged += OnCapabilityOverridesChanged; // FB-5 override flip (image tier gate)
            app.NerdFontAvailableChanged += OnNerdFontChanged; // nerd-font (glyph tier) opt-in flip
            app.EmojiAvailableChanged += OnEmojiChanged; // emoji tier opt-in flip (FB-15)
            _subscribedApp = app;
        }

        ResolveTier();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_subscribedApp is { } app)
        {
            app.CapabilitiesChanged -= OnCapabilitiesChanged;
            app.CapabilityOverridesChanged -= OnCapabilityOverridesChanged;
            app.NerdFontAvailableChanged -= OnNerdFontChanged;
            app.EmojiAvailableChanged -= OnEmojiChanged;
            _subscribedApp = null;
        }

        base.OnDetachedFromTree(in e);
    }

    private void OnCapabilitiesChanged(object? sender, CapabilitiesChangedEventArgs e) => ResolveTier();
    private void OnCapabilityOverridesChanged(object? sender, EventArgs e) => ResolveTier();
    private void OnNerdFontChanged(object? sender, EventArgs e) => ResolveTier();
    private void OnEmojiChanged(object? sender, EventArgs e) => ResolveTier();

    private static void OnTierInputChanged(UIObject sender, object? oldValue, object? newValue)
        => (sender as Icon)?.ResolveTier();

    // Picks the highest-preference tier that is both provided and supported; the unicode Text tier is the floor.
    private void ResolveTier()
    {
        var nerdFont = UIApplication.Current?.NerdFontAvailable ?? false;

        if (nerdFont && !string.IsNullOrEmpty(Glyph))
        {
            Tier = IconTier.Glyph;
            var text = new TextBlock { TextAlignment = TextAlignment.Center };
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Glyph)) { Source = this });
            text.SetBinding(MinWidthProperty, new Binding(nameof(GlyphWidth)) { Source = this });
            ResolvedContent = text;
        }
        else if ((Image is not null || ImageUri is not null) && GraphicsSupported)
        {
            // The image tier; the ImagePresenter shows the Text tier as its placeholder when a graphics protocol is
            // present but cannot carry this image's specific format (e.g. a JPEG on a Kitty-only terminal).
            Tier = IconTier.Image;
            ResolvedContent = new ImagePresenter { Source = Image, SourceUri = ImageUri, PlaceholderContent = Text };
        }
        else if (!string.IsNullOrEmpty(Emoji) && (UIApplication.Current?.EmojiAvailable ?? false))
        {
            // The emoji tier (FB-15) — between Image and Text: richer than single-width Unicode, wins on
            // emoji-capable image-less terminals. Measures at its natural (double-cell) grapheme width.
            Tier = IconTier.Emoji;
            ResolvedContent = Emoji;
        }
        else
        {
            // The unicode floor — also the resting tier on a terminal with no Nerd Font and no graphics protocol.
            Tier = IconTier.Text;
            ResolvedContent = Text;
        }
    }

    // Whether the EFFECTIVE capabilities (negotiated ∘ user overrides — FB-5) include a graphics protocol that can
    // carry an inline image (mirrors the protocol gate in ImagePresenter.IsImageVisible; the per-image format check
    // lives there, behind the placeholder).
    private static bool GraphicsSupported
        => UIApplication.Current?.EffectiveCapabilities.Output.Graphics is { } g
           && (g.ITerm2InlineImages || g.KittyGraphics || g.Sixel);
}

/// <summary>The representation an <see cref="Icon"/> resolved to (test observability).</summary>
public enum IconTier
{
    /// <summary>The single-width Unicode <see cref="Icon.Text"/> floor.</summary>
    Text,

    /// <summary>The Nerd Font <see cref="Icon.Glyph"/> tier.</summary>
    Glyph,

    /// <summary>The graphics-protocol <see cref="Icon.Image"/> tier (falls back to Text when unsupported).</summary>
    Image,

    /// <summary>The double-width color-emoji <see cref="Icon.Emoji"/> tier (FB-15) — shown only when
    /// <see cref="UIApplication.EmojiAvailable"/> is opted in; sits between Image and Text in preference.</summary>
    Emoji,
}
