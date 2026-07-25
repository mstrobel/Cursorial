namespace Cursorial.UI.Configuration;

/// <summary>The settings surface a <see cref="UserOptionDescriptor"/> is grouped under (one tab / wizard section each).</summary>
public enum UserOptionCategory : byte
{
    /// <summary>Terminal fundamentals a first run should confirm: Nerd Font, emoji, images.</summary>
    General,

    /// <summary>Look and motion: theme base, animations, translucency.</summary>
    Appearance,

    /// <summary>Keyboard and pointer behavior.</summary>
    Input,

    /// <summary>Capability overrides that can corrupt the display when forced wrong — gated behind a warning, staged rather than live-applied, and offered a timed test.</summary>
    Advanced
}

/// <summary>How a <see cref="UserOptionDescriptor"/> edits: an on/off toggle or a closed choice list.</summary>
public enum UserOptionKind : byte
{
    /// <summary>An on/off toggle (<c>"true"</c>/<c>"false"</c> in the store).</summary>
    Boolean,

    /// <summary>One value from <see cref="UserOptionDescriptor.Choices"/> (stored verbatim).</summary>
    Choice
}

/// <summary>One selectable value of a <see cref="UserOptionKind.Choice"/> option.</summary>
/// <param name="Value">The stored string (<c>"dark"</c>, <c>"on"</c>, …).</param>
/// <param name="Label">The display label.</param>
public sealed record UserOptionChoice(string Value, string Label);

/// <summary>
/// The UI-facing description of one well-known option key — the single source both the settings
/// dialog and the first-run wizard build their rows from (<see cref="UserOptionCatalog"/> is the
/// authoritative list). Purely descriptive: the store stays a flat string map and unknown keys
/// keep round-tripping untouched.
/// </summary>
public sealed record UserOptionDescriptor
{
    /// <summary>The <see cref="UserOptionKeys"/> key.</summary>
    public required string Key { get; init; }

    /// <summary>The row label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The one-or-two-sentence explanation shown under / beside the row.</summary>
    public required string Description { get; init; }

    /// <summary>The tab / wizard section.</summary>
    public required UserOptionCategory Category { get; init; }

    /// <summary>Toggle vs choice.</summary>
    public required UserOptionKind Kind { get; init; }

    /// <summary>The closed value list for <see cref="UserOptionKind.Choice"/>; <see langword="null"/> for toggles.</summary>
    public IReadOnlyList<UserOptionChoice>? Choices { get; init; }

    /// <summary>
    /// The value semantically in force when the key is absent from BOTH layers — what an
    /// "inherited (default)" row displays and what clearing every layer live-reverts to.
    /// For toggles: <c>"true"</c>/<c>"false"</c>; for choices: a member of <see cref="Choices"/>.
    /// </summary>
    public required string DefaultValue { get; init; }

    /// <summary>
    /// Whether a wrong value can corrupt the whole display (forcing an unsupported capability
    /// on). Such options do NOT live-apply: they stage, offer the timed test-and-auto-revert,
    /// and commit only on save.
    /// </summary>
    public bool RequiresTest { get; init; }

    /// <summary>
    /// The key is defined and persists but no framework consumer exists yet — the row is shown
    /// with a "takes effect in a future version" annotation so saved values pre-seed cleanly.
    /// </summary>
    public bool ReservedForFuture { get; init; }
}

/// <summary>
/// The authoritative descriptor list for the framework's well-known option keys — the shared
/// source the settings dialog tabs and the first-run wizard pages are generated from.
/// </summary>
public static class UserOptionCatalog
{
    private static readonly UserOptionChoice[] AutoLightDark =
    [
        new("auto", "Auto (from terminal background)"),
        new("light", "Light"),
        new("dark", "Dark")
    ];

    private static readonly UserOptionChoice[] ColorTiers =
    [
        new("auto", "Auto (negotiated)"),
        new("truecolor", "Truecolor (24-bit)"),
        new("ansi256", "256 colors"),
        new("ansi16", "16 colors"),
        new("nocolor", "Monochrome")
    ];

    private static readonly UserOptionChoice[] AutoOnOff =
    [
        new("auto", "Auto (negotiated)"),
        new("on", "Force on"),
        new("off", "Force off")
    ];

    private static readonly UserOptionChoice[] KeyboardProfiles =
    [
        new("platform", "Platform-native (⌘ / Super where applicable)"),
        new("pc", "PC-standard (Ctrl)")
    ];

    /// <summary>Every catalogued option, in display order (grouped by <see cref="UserOptionCategory"/>).</summary>
    public static IReadOnlyList<UserOptionDescriptor> All { get; } =
    [
        // ── General: terminal fundamentals ───────────────────────────────────────────────
        new()
        {
            Key = UserOptionKeys.NerdFont,
            DisplayName = "Nerd Font installed",
            Description = "Enables icon glyphs from the Nerd Font ranges. Use the sample glyphs to check: "
                        + "if they render as boxes or question marks, leave this off.",
            Category = UserOptionCategory.General,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "false"
        },
        new()
        {
            Key = UserOptionKeys.Emoji,
            DisplayName = "Color emoji",
            Description = "Whether the terminal font renders color emoji at the expected double width. "
                        + "Turn off if emoji appear as narrow monochrome glyphs or misalign the layout.",
            Category = UserOptionCategory.General,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "true"
        },
        new()
        {
            Key = UserOptionKeys.Images,
            DisplayName = "Inline images",
            Description = "Allow image protocols (Kitty graphics, Sixel, iTerm2) when the terminal supports them. "
                        + "Turning this off forces text-only rendering everywhere.",
            Category = UserOptionCategory.General,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "true"
        },

        // ── Appearance ───────────────────────────────────────────────────────────────────
        new()
        {
            Key = UserOptionKeys.ThemeBase,
            DisplayName = "Theme",
            Description = "Light or dark theme, or derive automatically from the terminal background.",
            Category = UserOptionCategory.Appearance,
            Kind = UserOptionKind.Choice,
            Choices = AutoLightDark,
            DefaultValue = "auto"
        },
        new()
        {
            Key = UserOptionKeys.Animations,
            DisplayName = "Animated transitions",
            Description = "Storyboards and transitions. Turn off for reduced motion; state changes apply instantly.",
            Category = UserOptionCategory.Appearance,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "true"
        },
        new()
        {
            Key = UserOptionKeys.Translucency,
            DisplayName = "Translucent menus and popups",
            Description = "Fancy translucent chrome for menus and popups.",
            Category = UserOptionCategory.Appearance,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "true",
            ReservedForFuture = true
        },

        // ── Input ────────────────────────────────────────────────────────────────────────
        new()
        {
            Key = UserOptionKeys.AlwaysShowAccessKeyCues,
            DisplayName = "Always show access-key cues",
            Description = "Show underlined access keys permanently instead of only while Alt is held.",
            Category = UserOptionCategory.Input,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "false",
            ReservedForFuture = true
        },
        new()
        {
            Key = UserOptionKeys.KeyboardProfile,
            DisplayName = "Keyboard shortcuts",
            Description = "Platform-native modifiers, or PC-standard chords (Ctrl in place of ⌘/Super).",
            Category = UserOptionCategory.Input,
            Kind = UserOptionKind.Choice,
            Choices = KeyboardProfiles,
            DefaultValue = "platform",
            ReservedForFuture = true
        },
        new()
        {
            Key = UserOptionKeys.HorizontalScrollDeadZone,
            DisplayName = "Horizontal scroll dead zone",
            Description = "Suppresses accidental vertical drift while scrolling horizontally.",
            Category = UserOptionCategory.Input,
            Kind = UserOptionKind.Boolean,
            DefaultValue = "false",
            ReservedForFuture = true
        },

        // ── Advanced: capability overrides (warning-gated; staged + testable) ────────────
        new()
        {
            Key = UserOptionKeys.ColorTier,
            DisplayName = "Color depth override",
            Description = "Overrides the negotiated color tier. Forcing a tier the terminal cannot render "
                        + "produces garbage colors — use the timed test first.",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = ColorTiers,
            DefaultValue = "auto",
            RequiresTest = true
        },
        new()
        {
            Key = UserOptionKeys.KittyKeyboardOverride,
            DisplayName = "Kitty keyboard protocol",
            Description = "Overrides key-event protocol detection. Forcing on where unsupported can make "
                        + "keys arrive as escape-sequence noise.",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = AutoOnOff,
            DefaultValue = "auto",
            RequiresTest = true
        },
        new()
        {
            Key = UserOptionKeys.MouseMotionOverride,
            DisplayName = "Mouse motion tracking",
            Description = "Overrides any-event mouse motion detection (hover effects).",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = AutoOnOff,
            DefaultValue = "auto",
            RequiresTest = true
        },
        new()
        {
            Key = UserOptionKeys.KittyGraphicsOverride,
            DisplayName = "Kitty graphics",
            Description = "Overrides Kitty image-protocol detection. Forcing on where unsupported prints "
                        + "raw protocol bytes into the display.",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = AutoOnOff,
            DefaultValue = "auto",
            RequiresTest = true
        },
        new()
        {
            Key = UserOptionKeys.SixelOverride,
            DisplayName = "Sixel graphics",
            Description = "Overrides Sixel image-protocol detection. Forcing on where unsupported prints "
                        + "raw protocol bytes into the display.",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = AutoOnOff,
            DefaultValue = "auto",
            RequiresTest = true
        },
        new()
        {
            Key = UserOptionKeys.ITerm2ImagesOverride,
            DisplayName = "iTerm2 inline images",
            Description = "Overrides iTerm2 image-protocol detection. Forcing on where unsupported prints "
                        + "raw protocol bytes into the display.",
            Category = UserOptionCategory.Advanced,
            Kind = UserOptionKind.Choice,
            Choices = AutoOnOff,
            DefaultValue = "auto",
            RequiresTest = true
        }
    ];

    /// <summary>
    /// The Nerd Font sample glyphs the General tab / wizard shows so a user can visually confirm
    /// coverage — one glyph from each commonly-used Nerd Font subset (dev icons, Font Awesome,
    /// Octicons, Powerline, Material, Weather). Boxes/tofu ⇒ no Nerd Font.
    /// </summary>
    public static string NerdFontProbeGlyphs => "      ";
}
