using System.IO;

using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// Spec for the file-type icon table (the file-dialog design page's two icon passes). Three contracts:
/// <list type="number">
/// <item><b>Lookup.</b> Extensions (case-insensitive, with or without a dot), multi-dot names — where the
/// two-segment compound wins ("archive.tar.gz" is a tarball, "archive.gz" is not) — well-known file names and
/// stems (.gitignore, Makefile, Dockerfile, LICENSE, LICENSE.md, README.md), directories/drives/places, and the
/// unknown-extension default.</item>
/// <item><b>Tier completeness.</b> Every row carries all four glyph tiers <i>at that tier's width</i>: a
/// one-cell Nerd Font glyph, a genuinely two-cell emoji (the continuation cell an emoji-tier
/// <see cref="Icon"/> budgets — FB-15), a one-cell Unicode floor, and a printable 7-bit ASCII floor. This is
/// the test that stops the table rotting: a row added with a two-cell "Unicode" glyph would shove a listing's
/// Name column one cell right the moment the user turned Nerd Font off, and it fails here instead.</item>
/// <item><b>It feeds the real control.</b> A descriptor's <see cref="FileTypeDescriptor.CreateIcon"/> resolves
/// through the framework's existing ladder against real rendered frames — Nerd Font glyph → emoji → Unicode
/// floor — rather than through any parallel mechanism of its own.</item>
/// </list>
/// </summary>
public sealed class FileTypeIconsTests
{
    private static UIHeadlessHost Host(TerminalCapabilities? caps = null) => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(20, 4),
        Capabilities = caps ?? HeadlessCapabilities.KittyTruecolor // base Kitty: truecolor, NO graphics protocol
    });

    private static Icon Show(UIHeadlessHost host, Icon icon, bool nerdFont = false, bool? emoji = null)
    {
        host.Application.NerdFontAvailable = nerdFont; // set before attach so the first resolve sees it
        if (emoji is { } value)
            host.Application.EmojiAvailable = value;

        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Top;
        host.ShowRoot(icon);
        host.RunUntilIdle();
        return icon;
    }

    // ───────────────────────────── extension lookup ─────────────────────────────

    [Theory] // the same row whether the caller has a dot, a case, or neither
    [InlineData("png")]
    [InlineData(".png")]
    [InlineData("PNG")]
    [InlineData(".PNG")]
    public void ForExtension_IsDotAndCaseInsensitive(string extension)
    {
        var descriptor = FileTypeIcons.ForExtension(extension);

        Assert.Same(FileTypeIcons.ForExtension("png"), descriptor);
        Assert.Equal("PNG image", descriptor.KindLabel);
        Assert.Equal("\ue60d", descriptor.Glyph); // nf-seti-image, the design page's seed
    }

    [Theory] // a file name resolves by its last extension, casing and surrounding path notwithstanding
    [InlineData("hero-banner.png", "PNG image")]
    [InlineData("THUMBNAIL.JPG", "JPEG image")]
    [InlineData("logo.svg", "SVG image")]
    [InlineData("atlas.lua", "Lua source")]
    [InlineData("build.sh", "Shell script")]
    [InlineData("gradients.json", "JSON file")]
    [InlineData("credits.pdf", "PDF")]
    [InlineData("palette.aco", "Swatch file")]
    [InlineData("/home/mike/assets/hero-banner.PNG", "PNG image")]
    [InlineData(@"C:\Users\mike\assets\hero-banner.png", "PNG image")]
    public void ForFileName_ResolvesByExtension(string fileName, string expectedLabel)
        => Assert.Equal(expectedLabel, FileTypeIcons.ForFileName(fileName).KindLabel);

    [Theory] // multi-dot names: the two-segment compound is probed BEFORE the last extension
    [InlineData("archive.tar.gz", "Gzip tarball")]
    [InlineData("Backup.TAR.GZ", "Gzip tarball")]
    [InlineData("archive.tgz", "Gzip tarball")]
    [InlineData("archive.tar.bz2", "Bzip2 tarball")]
    [InlineData("archive.tar.xz", "XZ tarball")]
    [InlineData("archive.gz", "Gzip archive")] // …but a lone .gz is NOT a tarball
    [InlineData("archive.tar", "TAR archive")]
    [InlineData("api.d.ts", "TS declarations")]
    [InlineData("api.ts", "TS source")]
    [InlineData("notes.v2.txt", "Text")] // unknown compound falls through to the last extension
    public void ForFileName_HonorsCompoundExtensions(string fileName, string expectedLabel)
        => Assert.Equal(expectedLabel, FileTypeIcons.ForFileName(fileName).KindLabel);

    // ───────────────────────────── well-known names ─────────────────────────────

    [Theory] // exact names and stems beat the extension table
    [InlineData(".gitignore", "Git ignore rules")]
    [InlineData(".GITIGNORE", "Git ignore rules")]
    [InlineData(".gitattributes", "Git attributes")]
    [InlineData("Makefile", "Makefile")]
    [InlineData("makefile", "Makefile")]
    [InlineData("GNUmakefile", "Makefile")]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData(".dockerignore", "Docker ignore rules")]
    [InlineData("docker-compose.yml", "Docker Compose file")] // not "YAML file"
    [InlineData(".editorconfig", "EditorConfig")]
    [InlineData("package.json", "npm manifest")]             // not "JSON file"
    [InlineData("LICENSE", "License")]
    [InlineData("license", "License")]
    [InlineData("LICENSE.md", "License")]                    // not "Markdown document"
    [InlineData("licence.txt", "License")]
    [InlineData("COPYING", "License")]
    [InlineData("README", "Readme")]
    [InlineData("README.md", "Readme")]                      // not "Markdown document"
    [InlineData("readme.TXT", "Readme")]
    [InlineData("CHANGELOG.md", "Changelog")]
    public void ForFileName_ResolvesWellKnownNames(string fileName, string expectedLabel)
        => Assert.Equal(expectedLabel, FileTypeIcons.ForFileName(fileName).KindLabel);

    [Fact] // the stem rule is gated on prose extensions — a screenshot of a license is still an image
    public void ForFileName_StemRule_DoesNotSwallowRealExtensions()
    {
        Assert.Equal("PNG image", FileTypeIcons.ForFileName("license.png").KindLabel);
        Assert.Equal("Word", FileTypeIcons.ForFileName("readme.docx").KindLabel);
        Assert.Equal("JSON file", FileTypeIcons.ForFileName("changelog.json").KindLabel);
    }

    [Fact] // the design page's git and license glyphs, on the rows that actually carry them
    public void WellKnownNames_CarryTheDesignPageGlyphs()
    {
        Assert.Equal("\ue702", FileTypeIcons.ForFileName(".gitignore").Glyph); // nf-dev-git
        Assert.Equal("\uf495", FileTypeIcons.ForFileName("LICENSE").Glyph);    // nf-oct-law (v3 home of the page's U+F718)
        Assert.Equal("📜", FileTypeIcons.ForFileName("LICENSE").Emoji);         // the page's license emoji
    }

    // ───────────────────────────── the design page's glyph map ─────────────────────────────

    [Theory]
    // The seed set, pinned codepoint by codepoint against the file-dialog design page's
    // "glyph map (codepoint · name)" line. Two rows deliberately differ from the page and are called out
    // below: it was written against Nerd Fonts v2 and those two codepoints were VACATED by the v3
    // renumbering — U+FC1F and U+F718 render as tofu in a v3-patched font, which is what every current
    // Nerd Font install is.
    [InlineData("folder", "\ue5ff")]          // nf-custom-folder U+E5FF
    [InlineData("png", "\ue60d")]             // nf-seti-image U+E60D
    [InlineData("jpeg", "\ue60d")]            // nf-seti-image U+E60D
    [InlineData("svg", "\ue698")]             // page: nf-mdi-svg U+FC1F (v2, vacated) → U+E698 (Nf.MdSvg)
    [InlineData("pdf", "\uf1c1")]             // nf-fa-file_pdf U+F1C1
    [InlineData("json", "\ue60b")]            // nf-seti-json U+E60B
    [InlineData("lua", "\ue620")]             // nf-seti-lua U+E620
    [InlineData("sh", "\uf489")]              // nf-oct-terminal U+F489
    [InlineData("git", "\ue702")]             // nf-dev-git U+E702
    [InlineData("license", "\uf495")]         // page: nf-oct-law U+F718 (v2, vacated) → nf-oct-law U+F495
    [InlineData("star", "\uf005")]            // U+F005
    [InlineData("drive", "\uf0a0")]           // U+F0A0
    [InlineData("cloud", "\uf0c2")]           // U+F0C2
    [InlineData("home", "\uf015")]            // U+F015
    public void DesignPageSeedGlyphs_ArePresentVerbatim(string seed, string expectedGlyph)
    {
        var descriptor = seed switch
                         {
                             "folder"  => FileTypeIcons.Folder,
                             "git"     => FileTypeIcons.ForFileName(".gitignore"),
                             "license" => FileTypeIcons.ForFileName("LICENSE"),
                             "star"    => FileTypeIcons.ForPlace(FileSystemPlace.Favorites),
                             "drive"   => FileTypeIcons.ForPlace(FileSystemPlace.Drive),
                             "cloud"   => FileTypeIcons.ForPlace(FileSystemPlace.CloudDrive),
                             "home"    => FileTypeIcons.ForPlace(FileSystemPlace.Home),
                             _         => FileTypeIcons.ForExtension(seed)
                         };

        Assert.Equal(expectedGlyph, descriptor.Glyph);
    }

    [Theory] // the Type column the page's Details view shows, verbatim
    [InlineData("hero-banner.png", "PNG image")]
    [InlineData("logo.svg", "SVG image")]
    [InlineData("thumbnail.jpg", "JPEG image")]
    [InlineData("palette.aco", "Swatch file")]
    [InlineData("credits.pdf", "PDF")]
    [InlineData("atlas.lua", "Lua source")]
    [InlineData("build.sh", "Shell script")]
    [InlineData("gradients.json", "JSON file")]
    [InlineData("mystery.qqq", "File")]
    public void DesignPageKindLabels_AreVerbatim(string fileName, string expectedLabel)
        => Assert.Equal(expectedLabel, FileTypeIcons.ForFileName(fileName).KindLabel);

    // ───────────────────────────── directories, drives, places ─────────────────────────────

    [Fact] // a directory row: the design page's folder glyph, "Folder" in the Type column
    public void Directories_ResolveToTheFolderRow()
    {
        var folder = FileTypeIcons.ForEntry("textures", isDirectory: true);

        Assert.Same(FileTypeIcons.Folder, folder);
        Assert.Same(FileTypeIcons.ForPlace(FileSystemPlace.Folder), folder);
        Assert.Equal("Folder", folder.KindLabel);
        Assert.Equal(FileTypeCategory.Folder, folder.Category);
        Assert.Equal("\ue5ff", folder.Glyph); // nf-custom-folder
        Assert.Equal("📁", folder.Emoji);
        Assert.Equal("▸", folder.Text);
    }

    [Fact] // a directory is a directory whatever its name looks like — the extension is never consulted
    public void Directories_IgnoreTheirExtension()
        => Assert.Same(FileTypeIcons.Folder, FileTypeIcons.ForEntry("my.assets.png", isDirectory: true));

    [Fact] // the FileSystemInfo overload: DirectoryInfo → folder, FileInfo → its name's row
    public void ForEntry_FileSystemInfo()
    {
        Assert.Same(FileTypeIcons.Folder, FileTypeIcons.ForEntry(new DirectoryInfo(Path.GetTempPath())));
        Assert.Equal("PNG image", FileTypeIcons.ForEntry(new FileInfo("hero-banner.png")).KindLabel);
        Assert.Throws<ArgumentNullException>(() => FileTypeIcons.ForEntry(null!));
    }

    [Theory] // the places rail's glyph map, pinned to the design page's codepoints
    [InlineData(FileSystemPlace.Home, "\uf015", "Home folder")]           // U+F015
    [InlineData(FileSystemPlace.Favorites, "\uf005", "Favorite")]         // U+F005
    [InlineData(FileSystemPlace.Drive, "\uf0a0", "Local drive")]          // U+F0A0
    [InlineData(FileSystemPlace.CloudDrive, "\uf0c2", "Cloud drive")]     // U+F0C2
    public void ForPlace_CarriesTheDesignPageGlyphs(FileSystemPlace place, string glyph, string label)
    {
        var descriptor = FileTypeIcons.ForPlace(place);

        Assert.Equal(glyph, descriptor.Glyph);
        Assert.Equal(label, descriptor.KindLabel);
    }

    [Fact] // the base (no-Nerd-Font) pass's rail marks: ▣ home · ★ pinned · ▤ drive · ☁ cloud
    public void ForPlace_UnicodeFloor_MatchesTheBasePass()
    {
        Assert.Equal("▣", FileTypeIcons.ForPlace(FileSystemPlace.Home).Text);
        Assert.Equal("★", FileTypeIcons.ForPlace(FileSystemPlace.Favorites).Text);
        Assert.Equal("▤", FileTypeIcons.ForPlace(FileSystemPlace.Drive).Text);
        Assert.Equal("☁", FileTypeIcons.ForPlace(FileSystemPlace.CloudDrive).Text);
    }

    [Fact] // volumes: removable/optical and network shares peel off the fixed-drive row
    public void ForDriveType_MapsVolumeKinds()
    {
        Assert.Same(FileTypeIcons.Drive, FileTypeIcons.ForDriveType(DriveType.Fixed));
        Assert.Same(FileTypeIcons.ForPlace(FileSystemPlace.RemovableDrive), FileTypeIcons.ForDriveType(DriveType.Removable));
        Assert.Same(FileTypeIcons.ForPlace(FileSystemPlace.RemovableDrive), FileTypeIcons.ForDriveType(DriveType.CDRom));
        Assert.Same(FileTypeIcons.ForPlace(FileSystemPlace.NetworkDrive), FileTypeIcons.ForDriveType(DriveType.Network));
    }

    [Fact] // an undefined enum value is a caller bug, not a silent generic icon
    public void ForPlace_RejectsUndefinedPlaces()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FileTypeIcons.ForPlace((FileSystemPlace) 9999));

    // ───────────────────────────── the unknown default ─────────────────────────────

    [Theory] // anything unrecognized lands on the generic "File" row rather than throwing or blanking
    [InlineData("mystery.qqq")]
    [InlineData("noextension")]
    [InlineData("trailing.")]
    [InlineData(".hiddenbutunknown")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForFileName_UnknownFallsToGenericFile(string? fileName)
    {
        var descriptor = FileTypeIcons.ForFileName(fileName);

        Assert.Same(FileTypeIcons.GenericFile, descriptor);
        Assert.Equal("File", descriptor.KindLabel);
        Assert.Equal(FileTypeCategory.Unknown, descriptor.Category);
        Assert.Equal("📄", descriptor.Emoji);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("qqq")]
    public void ForExtension_UnknownFallsToGenericFile(string? extension)
        => Assert.Same(FileTypeIcons.GenericFile, FileTypeIcons.ForExtension(extension));

    // ───────────────────────────── the table-completeness contract ─────────────────────────────

    [Fact]
    // THE anti-rot test: every row of the table must carry every tier, each at its tier's width. Icon measures
    // at its RESOLVED tier's width (FB-15) — emoji budget 2 cells including the reserved continuation cell,
    // every other tier budgets 1 — so a row whose "Unicode floor" is secretly an Emoji_Presentation glyph
    // (⭐ ⛔ ⚡ …, all two cells) silently shifts a listing's Name column the moment Nerd Font is turned off.
    public void EveryEntry_HasEveryTier_AtItsTierWidth()
    {
        Assert.NotEmpty(FileTypeIcons.All);

        foreach (var entry in FileTypeIcons.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id), $"{entry.Id}: missing id");
            Assert.False(string.IsNullOrWhiteSpace(entry.KindLabel), $"{entry.Id}: missing kind label");

            Assert.False(string.IsNullOrEmpty(entry.Glyph), $"{entry.Id}: missing Nerd Font glyph");
            Assert.False(string.IsNullOrEmpty(entry.Emoji), $"{entry.Id}: missing emoji");
            Assert.False(string.IsNullOrEmpty(entry.Text), $"{entry.Id}: missing Unicode floor");
            Assert.False(string.IsNullOrEmpty(entry.Ascii), $"{entry.Id}: missing ASCII floor");

            Assert.Equal(1, GraphemeWidth.ClusterCount(entry.Glyph));
            Assert.Equal(1, GraphemeWidth.ClusterCount(entry.Emoji));
            Assert.Equal(1, GraphemeWidth.ClusterCount(entry.Text));

            Assert.True(GraphemeWidth.StringWidth(entry.Glyph) == 1,
                        $"{entry.Id}: the Nerd Font tier must be single-cell (got {GraphemeWidth.StringWidth(entry.Glyph)})");
            Assert.True(GraphemeWidth.StringWidth(entry.Emoji) == 2,
                        $"{entry.Id}: the emoji tier must be double-cell (got {GraphemeWidth.StringWidth(entry.Emoji)})");
            Assert.True(GraphemeWidth.StringWidth(entry.Text) == 1,
                        $"{entry.Id}: the Unicode floor must be single-cell (got {GraphemeWidth.StringWidth(entry.Text)})");

            Assert.True(entry.Ascii.Length == 1 && entry.Ascii[0] is >= '!' and <= '~',
                        $"{entry.Id}: the ASCII floor must be one printable 7-bit character (got '{entry.Ascii}')");
        }
    }

    [Fact] // ids are the dedupe key behind All — a duplicate would hide a row from the contract above
    public void EveryEntry_HasAUniqueId()
    {
        var ids = FileTypeIcons.All.Select(e => e.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact] // every registered extension really resolves — through the bare-extension AND the file-name doors
    public void EveryRegisteredExtension_Resolves()
    {
        Assert.NotEmpty(FileTypeIcons.ByExtension);

        foreach (var (extension, expected) in FileTypeIcons.ByExtension)
        {
            Assert.Same(expected, FileTypeIcons.ForExtension(extension));
            Assert.Same(expected, FileTypeIcons.ForExtension("." + extension.ToUpperInvariant()));
            Assert.Same(expected, FileTypeIcons.ForFileName($"sample.{extension}"));
            Assert.Contains(expected, FileTypeIcons.All);
        }
    }

    [Fact] // …and every registered well-known name, in either casing
    public void EveryRegisteredFileName_Resolves()
    {
        Assert.NotEmpty(FileTypeIcons.ByFileName);

        foreach (var (fileName, expected) in FileTypeIcons.ByFileName)
        {
            Assert.Same(expected, FileTypeIcons.ForFileName(fileName));
            Assert.Same(expected, FileTypeIcons.ForFileName(fileName.ToUpperInvariant()));
            Assert.Contains(expected, FileTypeIcons.All);
        }
    }

    [Fact] // every place and every category has a row (no enum member can be added without a row)
    public void EveryPlaceAndCategory_HasARow()
    {
        foreach (var place in Enum.GetValues<FileSystemPlace>())
            Assert.Contains(FileTypeIcons.ForPlace(place), FileTypeIcons.All);

        foreach (var category in Enum.GetValues<FileTypeCategory>())
        {
            var descriptor = FileTypeIcons.ForCategory(category);

            Assert.Equal(category, descriptor.Category);
            Assert.Contains(descriptor, FileTypeIcons.All);
        }
    }

    // ───────────────────────────── it feeds the real Icon ladder ─────────────────────────────

    [Fact] // the carrier projection: the four authored tiers land in the Icon slots, glyph width 1
    public void ToIconCarrier_FillsTheIconSlots()
    {
        var png = FileTypeIcons.ForExtension("png");
        var carrier = png.ToIconCarrier();

        Assert.Equal(png.Glyph, carrier.Glyph);
        Assert.Equal(2, carrier.GlyphWidth);
        Assert.Equal(png.Emoji, carrier.Emoji);
        Assert.Equal(png.Text, carrier.Text);

        // asciiFloor swaps the 7-bit floor into the (only) floor slot the framework has.
        Assert.Equal(png.Ascii, png.ToIconCarrier(asciiFloor: true).Text);
    }

    [Fact] // Nerd Font on → the glyph tier: the per-extension mark, one cell (design page's Nerd Font pass)
    public void CreateIcon_ResolvesToTheGlyphTier_WhenNerdFontAvailable()
    {
        using var host = Host();
        var descriptor = FileTypeIcons.ForExtension("lua");
        var icon = Show(host, descriptor.CreateIcon(), nerdFont: true);

        Assert.Equal(IconTier.Glyph, icon.Tier);
        Assert.Contains(descriptor.Glyph, host.GetRowText(0));
        Assert.Equal(2, icon.Bounds.Columns);
    }

    [Fact] // no Nerd Font, emoji present (the framework default) → the emoji tier, measured at TWO cells
    public void CreateIcon_ResolvesToTheEmojiTier_AndReservesItsContinuationCell()
    {
        using var host = Host();
        var descriptor = FileTypeIcons.ForExtension("lua");
        var icon = Show(host, descriptor.CreateIcon());

        Assert.Equal(IconTier.Emoji, icon.Tier);
        Assert.Equal("🌙", descriptor.Emoji); // the design page names this one explicitly
        Assert.Contains(descriptor.Emoji, host.GetRowText(0));
        Assert.Equal(2, icon.Bounds.Columns); // the reserved continuation cell
    }

    [Fact] // emoji opted out → the single-width Unicode floor, and the Name column does not shift
    public void CreateIcon_FallsToTheUnicodeFloor_WhenEmojiDisabled()
    {
        using var host = Host();
        var descriptor = FileTypeIcons.ForExtension("lua");
        var icon = Show(host, descriptor.CreateIcon(), emoji: false);

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Contains(descriptor.Text, host.GetRowText(0));
        Assert.Equal(1, icon.Bounds.Columns);
    }

    [Fact] // the ASCII substitute floor rides in the Text slot and still measures one cell
    public void CreateIcon_AsciiFloor_RendersSevenBit()
    {
        using var host = Host();
        var descriptor = FileTypeIcons.Folder;
        var icon = Show(host, descriptor.CreateIcon(asciiFloor: true), emoji: false);

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Equal("/", descriptor.Ascii);
        Assert.Contains("/", host.GetRowText(0));
        Assert.Equal(1, icon.Bounds.Columns);
    }
}
