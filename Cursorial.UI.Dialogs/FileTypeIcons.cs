using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

using Cursorial.UI.Controls;

using Cat = Cursorial.UI.Dialogs.FileTypeCategory;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The file-type icon table: maps a file-system entry — by extension, by well-known file name, or by
/// <see cref="FileSystemPlace"/> — onto a <see cref="FileTypeDescriptor"/> carrying both its capability-tiered
/// glyphs and the <i>Type</i>-column label the Open/Save dialog's Details view shows.
///
/// <para>
/// <b>This is data, not machinery.</b> The framework already ships the tier system: an
/// <see cref="Icon"/> is a templated control that renders the highest-preference representation that is both
/// <i>provided</i> and <i>supported</i> — Nerd Font <see cref="Icon.Glyph"/> (gated on
/// <see cref="UIApplication.NerdFontAvailable"/>, an explicit app opt-in because there is no probe for Nerd
/// Font coverage) → graphics-protocol <see cref="Icon.Image"/> → double-width <see cref="Icon.Emoji"/> (gated
/// on <see cref="UIApplication.EmojiAvailable"/>, a user opt-OUT that defaults present) → the single-width
/// Unicode <see cref="Icon.Text"/> floor, which is always renderable. The icon re-resolves live when those
/// capabilities flip. Nothing here duplicates or shadows that ladder: every row simply <i>fills the four
/// slots</i>, and <see cref="FileTypeDescriptor.ToIconCarrier"/> hands them to the real control.
/// </para>
///
/// <para>
/// <b>Which tier carries which meaning</b> (design doc: the file-dialog page's two icon passes). The Nerd Font
/// tier is authored <i>per entry</i> — it is one cell wide with no width ambiguity and gives per-extension
/// specificity "a generic icon cannot: distinct marks for .png, .svg, .json, .lua, .sh, plus folder, git, and
/// license glyphs — the look of a modern editor file tree". The emoji, Unicode and ASCII tiers are authored
/// <i>per <see cref="FileTypeCategory"/></i>, because "emoji read instantly as broad categories but cannot
/// distinguish, say, .png from .jpg — both are 🖼️"; a handful of rows the design page calls out by name
/// (lua 🌙, shell ⚙️, license 📜) override the category emoji. That split is the whole reason
/// <see cref="FileTypeCategory"/> is a public concept.
/// </para>
///
/// <para>
/// <b>Emoji are two cells.</b> An emoji-tier <see cref="Icon"/> measures 2 columns — its right-hand
/// continuation cell is reserved by the measurement itself (FB-15: grid safety lives in the measurement, not in
/// hiding the tier), which is exactly the design page's "each is a two-cell glyph with its right-hand
/// continuation cell reserved in the layout, so alignment holds whether the terminal renders it wide or
/// narrow". Honoring that here means one thing: every <see cref="FileTypeDescriptor.Emoji"/> in the table must
/// really be double-width and every other tier really single-width, or a listing's Name column shifts when the
/// user toggles Nerd Font. The completeness test measures all four tiers of every row with
/// <see cref="Cursorial.Text.GraphemeWidth"/> so a bad row fails the build instead of the layout.
/// </para>
///
/// <para>
/// <b>Nerd Fonts v3 codepoints.</b> The design page's glyph map predates the Nerd Fonts v3 renumbering, and two
/// of its codepoints — <c>U+FC1F nf-mdi-svg</c> and <c>U+F718 nf-oct-law</c> — no longer exist in v3: those
/// ranges were vacated, so a v3-patched font renders them as tofu. Both are carried here at their v3 homes
/// (<c>U+F0721 nf-md-svg</c>, <c>U+F495 nf-oct-law</c>), matching the sibling <c>nf-md-*</c> codepoints the
/// gallery's icon set already uses. Every other codepoint on the design page is unchanged in v3 and is used
/// verbatim.
/// </para>
/// </summary>
/// <remarks>
/// Lookup order for a file name (see <see cref="ForFileName"/>): exact well-known name → well-known
/// <i>stem</i> (so <c>LICENSE</c>, <c>LICENSE.md</c> and <c>license.txt</c> all land on the license row) →
/// compound extension (<c>archive.tar.gz</c> → "Gzip tarball") → last extension → <see cref="GenericFile"/>.
/// All matching is ordinal-case-insensitive: <c>README.MD</c>, <c>Makefile</c> and <c>.PNG</c> behave like
/// their lowercase spellings.
/// </remarks>
public static class FileTypeIcons
{
    // ───────────────────────────── Nerd Font codepoints (v3) ─────────────────────────────
    // Every constant is named for its nf-* glyph name and verified against the Nerd Fonts v3 glyphnames table.
    // Codepoints above U+FFFF live in Plane 15 (Supplementary PUA-A) and MUST be written as \U000FXXXX — they
    // are single scalars, not surrogate pairs of two glyphs, and measure one cell.
    private static class Nf
    {
        internal const string CustomFolder = "\ue5ff";               // nf-custom-folder — the design page's folder glyph
        internal const string FaLevelUp = "\uf148";                  // nf-fa-level_up
        internal const string FaHome = "\uf015";                     // nf-fa-home
        internal const string FaDesktop = "\uf108";                  // nf-fa-desktop
        internal const string FaDownload = "\uf019";                 // nf-fa-download
        internal const string FaPicture = "\uf03e";                  // nf-fa-picture_o
        internal const string FaMusic = "\uf001";                    // nf-fa-music
        internal const string FaFilm = "\uf008";                     // nf-fa-film
        internal const string FaHistory = "\uf1da";                  // nf-fa-history
        internal const string FaStar = "\uf005";                     // nf-fa-star
        internal const string FaTrash = "\uf1f8";                    // nf-fa-trash
        internal const string FaHdd = "\uf0a0";                      // nf-fa-hdd_o
        internal const string FaUsb = "\uf287";                      // nf-fa-usb
        internal const string FaCloud = "\uf0c2";                    // nf-fa-cloud
        internal const string OctServer = "\uf473";                  // nf-oct-server

        internal const string MdDesktopTower = "\U000F01C5";         // nf-md-desktop_tower
        internal const string MdFileDocument = "\U000F0219";         // nf-md-file_document
        internal const string MdFileDocumentMultiple = "\U000F1517"; // nf-md-file_document_multiple
        internal const string MdSvg = "\ue698";                      // nf-md-svg (v3 home of the design page's nf-mdi-svg)

        internal const string FaFile = "\uf15b";                     // nf-fa-file
        internal const string FaFilePdf = "\uf1c1";                  // nf-fa-file_pdf
        internal const string FaFilePowerPoint = "\uf1c4";           // nf-fa-file_powerpoint
        internal const string FaPaintBrush = "\uf1fc";               // nf-fa-paint_brush
        internal const string OctBook = "\uf405";                    // nf-oct-book
        internal const string OctHistory = "\uf464";                 // nf-oct-history
        internal const string OctLaw = "\uf495";                     // nf-oct-law (v3 home of the design page's U+F718)
        internal const string OctTerminal = "\uf489";                // nf-oct-terminal
        internal const string OctFileCode = "\uf40d";                // nf-oct-file_code
        internal const string OctFileBinary = "\uf471";              // nf-oct-file_binary
        internal const string DevGit = "\ue702";                     // nf-dev-git

        internal const string SetiText = "\ue64e";                   // nf-seti-text (also nf-seti-default)
        internal const string SetiMarkdown = "\ue609";               // nf-seti-markdown
        internal const string SetiCSharp = "\ue648";                 // nf-seti-c_sharp
        internal const string SetiFSharp = "\ue65a";                 // nf-seti-f_sharp
        internal const string SetiTypeScript = "\ue628";             // nf-seti-typescript
        internal const string SetiReact = "\ue625";                  // nf-seti-react
        internal const string SetiJavaScript = "\ue60c";             // nf-seti-javascript
        internal const string SetiPython = "\ue606";                 // nf-seti-python
        internal const string SetiRust = "\ue68b";                   // nf-seti-rust
        internal const string SetiGo = "\ue627";                     // nf-seti-go
        internal const string SetiC = "\ue649";                      // nf-seti-c
        internal const string SetiCpp = "\ue646";                    // nf-seti-cpp
        internal const string SetiJava = "\ue66d";                   // nf-seti-java
        internal const string SetiKotlin = "\ue634";                 // nf-seti-kotlin
        internal const string SetiSwift = "\ue699";                  // nf-seti-swift
        internal const string SetiRuby = "\ue605";                   // nf-seti-ruby
        internal const string SetiPhp = "\ue608";                    // nf-seti-php
        internal const string SetiLua = "\ue620";                    // nf-seti-lua
        internal const string SetiPowerShell = "\ue683";             // nf-seti-powershell
        internal const string SetiDatabase = "\ue64d";               // nf-seti-db
        internal const string SetiHtml = "\ue60e";                   // nf-seti-html
        internal const string SetiCss = "\ue614";                    // nf-seti-css
        internal const string SetiSass = "\ue603";                   // nf-seti-sass
        internal const string SetiXml = "\ue619";                    // nf-seti-xml
        internal const string SetiJson = "\ue60b";                   // nf-seti-json
        internal const string SetiYml = "\ue6a8";                    // nf-seti-yml
        internal const string SetiConfig = "\ue615";                 // nf-seti-config
        internal const string SetiCsv = "\ue64a";                    // nf-seti-csv
        internal const string SetiImage = "\ue60d";                  // nf-seti-image
        internal const string SetiFavicon = "\ue623";                // nf-seti-favicon
        internal const string SetiPhotoshop = "\ue67f";              // nf-seti-photoshop
        internal const string SetiIllustrator = "\ue669";            // nf-seti-illustrator
        internal const string SetiAudio = "\ue638";                  // nf-seti-audio
        internal const string SetiVideo = "\ue69f";                  // nf-seti-video
        internal const string SetiZip = "\ue6aa";                    // nf-seti-zip
        internal const string SetiWord = "\ue6a5";                   // nf-seti-word
        internal const string SetiXls = "\ue6a6";                    // nf-seti-xls
        internal const string SetiFont = "\ue659";                   // nf-seti-font
        internal const string SetiMakefile = "\ue673";               // nf-seti-makefile
        internal const string SetiDocker = "\ue650";                 // nf-seti-docker
        internal const string SetiEditorConfig = "\ue652";           // nf-seti-editorconfig
        internal const string SetiNpm = "\ue616";                    // nf-seti-npm
        internal const string SetiTsconfig = "\ue69d";               // nf-seti-tsconfig
        internal const string SetiProject = "\ue601";                // nf-seti-project
        internal const string CustomToml = "\ue6b2";                 // nf-custom-toml
    }

    private static readonly char[] PathSeparators = ['/', '\\'];

    // The extensions a well-known STEM may wear and still be recognized (see the stem rule in ForFileName).
    // Deliberately narrow: "license.txt" is a license, "license.png" is a screenshot of one.
    private static readonly FrozenSet<string> StemExtensions =
        new[] { "", "md", "markdown", "mdown", "txt", "text", "rst", "adoc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FileTypeDescriptor> ExtensionMap;
    private static readonly FrozenDictionary<string, FileTypeDescriptor> FileNameMap;
    private static readonly FrozenDictionary<string, FileTypeDescriptor> StemMap;
    private static readonly FrozenDictionary<FileSystemPlace, FileTypeDescriptor> PlaceMap;
    private static readonly FrozenDictionary<FileTypeCategory, FileTypeDescriptor> CategoryMap;
    private static readonly FileTypeDescriptor[] AllEntries;

    /// <summary>The row for an unrecognized file — kind label "File", the design page's generic
    /// <c>nf-fa-file</c> glyph over the 📄 emoji and a "▪" floor.</summary>
    public static FileTypeDescriptor GenericFile { get; }

    /// <summary>The row for an ordinary directory (kind label "Folder"). Shorthand for
    /// <c>ForPlace(FileSystemPlace.Folder)</c>.</summary>
    public static FileTypeDescriptor Folder { get; }

    /// <summary>The row for the ".." entry that navigates to the containing directory. Shorthand for
    /// <c>ForPlace(FileSystemPlace.ParentFolder)</c>.</summary>
    public static FileTypeDescriptor ParentFolder { get; }

    /// <summary>The row for a fixed local volume. Shorthand for <c>ForPlace(FileSystemPlace.Drive)</c>.</summary>
    public static FileTypeDescriptor Drive { get; }

    /// <summary>
    /// Every distinct row in the table — extensions, well-known names, well-known stems, places, the
    /// per-category generics and <see cref="GenericFile"/> — deduplicated by
    /// <see cref="FileTypeDescriptor.Id"/> (one row is registered under many keys: "jpg", "jpeg" and "jpe" are
    /// all the single "jpeg" row). Exposed so the table can be enumerated by tests (the completeness contract),
    /// by a "Files of type" filter builder, and by documentation tooling.
    /// </summary>
    public static IReadOnlyList<FileTypeDescriptor> All => AllEntries;

    /// <summary>The registered extensions, keyed without their leading dot and matched case-insensitively.
    /// Compound extensions ("tar.gz", "d.ts") are ordinary keys — see <see cref="ForFileName"/>.</summary>
    public static IReadOnlyDictionary<string, FileTypeDescriptor> ByExtension => ExtensionMap;

    /// <summary>The registered well-known file names (".gitignore", "Makefile", "Dockerfile",
    /// "docker-compose.yml", …), matched case-insensitively and consulted before any extension.</summary>
    public static IReadOnlyDictionary<string, FileTypeDescriptor> ByFileName => FileNameMap;

    // ───────────────────────────── the table ─────────────────────────────
    // Built once, in a static constructor, so the rows read as a table rather than as code: one line per file
    // type, columns id · category · Type-column label · Nerd Font glyph · the extensions (or names) it claims ·
    // an optional emoji override for the rows the design page names individually. Extension lists are
    // space-separated string literals purely so the columns stay aligned and the optional override can be the
    // last parameter — Split() is the cost of a readable table, paid once per process.
    static FileTypeIcons()
    {
        var all = new Dictionary<string, FileTypeDescriptor>(StringComparer.Ordinal);
        var extensions = new Dictionary<string, FileTypeDescriptor>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new Dictionary<string, FileTypeDescriptor>(StringComparer.OrdinalIgnoreCase);
        var stems = new Dictionary<string, FileTypeDescriptor>(StringComparer.OrdinalIgnoreCase);
        var places = new Dictionary<FileSystemPlace, FileTypeDescriptor>();
        var categories = new Dictionary<FileTypeCategory, FileTypeDescriptor>();

        // ── source ──────────────────────────────────────────────────────────────────────────────────────────
        Ext("cs",         Cat.Source,   "C# source",                 Nf.SetiCSharp,     "cs csx");
        Ext("fsharp",     Cat.Source,   "F# source",                 Nf.SetiFSharp,     "fs fsi fsx");
        Ext("vb",         Cat.Source,   "VB source",                 Nf.OctFileCode,    "vb");
        Ext("ts",         Cat.Source,   "TS source",                 Nf.SetiTypeScript, "ts mts cts");
        Ext("dts",        Cat.Source,   "TS declarations",           Nf.SetiTypeScript, "d.ts");
        Ext("tsx",        Cat.Source,   "TS React source",           Nf.SetiReact,      "tsx");
        Ext("js",         Cat.Source,   "JS source",                 Nf.SetiJavaScript, "js mjs cjs");
        Ext("jsx",        Cat.Source,   "JS React source",           Nf.SetiReact,      "jsx");
        Ext("py",         Cat.Source,   "Python source",             Nf.SetiPython,     "py pyw pyi");
        Ext("rs",         Cat.Source,   "Rust source",               Nf.SetiRust,       "rs");
        Ext("go",         Cat.Source,   "Go source",                 Nf.SetiGo,         "go");
        Ext("c",          Cat.Source,   "C source",                  Nf.SetiC,          "c");
        Ext("h",          Cat.Source,   "C header",                  Nf.SetiC,          "h");
        Ext("cpp",        Cat.Source,   "C++ source",                Nf.SetiCpp,        "cpp cc cxx c++");
        Ext("hpp",        Cat.Source,   "C++ header",                Nf.SetiCpp,        "hpp hh hxx h++");
        Ext("java",       Cat.Source,   "Java source",               Nf.SetiJava,       "java");
        Ext("kt",         Cat.Source,   "Kotlin source",             Nf.SetiKotlin,     "kt kts");
        Ext("swift",      Cat.Source,   "Swift source",              Nf.SetiSwift,      "swift");
        Ext("rb",         Cat.Source,   "Ruby source",               Nf.SetiRuby,       "rb erb");
        Ext("php",        Cat.Source,   "PHP source",                Nf.SetiPhp,        "php");
        Ext("lua",        Cat.Source,   "Lua source",                Nf.SetiLua,        "lua",           emoji: "🌙"); // design page
        Ext("sh",         Cat.Source,   "Shell script",              Nf.OctTerminal,    "sh bash zsh ksh fish", emoji: "⚙️"); // design page
        Ext("ps1",        Cat.Source,   "PowerShell script",         Nf.SetiPowerShell, "ps1 psm1 psd1");

        // ── markup & prose ──────────────────────────────────────────────────────────────────────────────────
        Ext("html",       Cat.Markup,   "HTML",                      Nf.SetiHtml,       "html htm xhtml");
        Ext("css",        Cat.Markup,   "Stylesheet",                       Nf.SetiCss,        "css");
        Ext("scss",       Cat.Markup,   "Sass",                      Nf.SetiSass,       "scss sass less");
        Ext("xml",        Cat.Markup,   "XML",                       Nf.SetiXml,        "xml xsd xsl");
        Ext("xaml",       Cat.Markup,   "XAML",                      Nf.SetiXml,        "xaml");
        Ext("md",         Cat.Text,     "Markdown",                  Nf.SetiMarkdown,   "md markdown mdown");
        Ext("txt",        Cat.Text,     "Text",                      Nf.SetiText,       "txt text log");

        // ── data & configuration ────────────────────────────────────────────────────────────────────────────
        Ext("json",       Cat.Data,     "JSON file",                 Nf.SetiJson,       "json jsonc json5"); // design page
        Ext("yaml",       Cat.Data,     "YAML file",                 Nf.SetiYml,        "yaml yml");
        Ext("toml",       Cat.Data,     "TOML file",                 Nf.CustomToml,     "toml");
        Ext("ini",        Cat.Data,     "Configuration",             Nf.SetiConfig,     "ini cfg conf config properties");
        Ext("csv",        Cat.Data,     "CSV file",                  Nf.SetiCsv,        "csv tsv");
        Ext("db",         Cat.Data,     "Database",                  Nf.SetiDatabase,   "db sqlite sqlite3 mdb");
        Ext("sql",        Cat.Data,     "SQL script",                Nf.SetiDatabase,   "sql");
        Ext("csproj",     Cat.Data,     "C# project",                Nf.SetiCSharp,     "csproj");
        Ext("sln",        Cat.Data,     "Solution file",             Nf.SetiProject,    "sln slnx");

        // ── images & design assets ──────────────────────────────────────────────────────────────────────────
        Ext("png",        Cat.Image,    "PNG image",                 Nf.SetiImage,      "png");  // design page
        Ext("jpeg",       Cat.Image,    "JPEG image",                Nf.SetiImage,      "jpg jpeg jpe"); // design page
        Ext("gif",        Cat.Image,    "GIF image",                 Nf.SetiImage,      "gif");
        Ext("bmp",        Cat.Image,    "Bitmap image",              Nf.SetiImage,      "bmp");
        Ext("webp",       Cat.Image,    "WebP image",                Nf.SetiImage,      "webp");
        Ext("tiff",       Cat.Image,    "TIFF image",                Nf.SetiImage,      "tif tiff");
        Ext("ico",        Cat.Image,    "Icon image",                Nf.SetiFavicon,    "ico icns cur");
        Ext("svg",        Cat.Vector,   "SVG image",                 Nf.MdSvg,          "svg"); // design page (v3 codepoint)
        Ext("aco",        Cat.Vector,   "Swatch file",               Nf.FaPaintBrush,   "aco ase swatches gpl"); // design page
        Ext("psd",        Cat.Vector,   "Photoshop",                 Nf.SetiPhotoshop,  "psd psb");
        Ext("ai",         Cat.Vector,   "Illustrator",               Nf.SetiIllustrator, "ai");

        // ── media ───────────────────────────────────────────────────────────────────────────────────────────
        Ext("mp3",        Cat.Audio,    "MP3 audio",                 Nf.SetiAudio,      "mp3");
        Ext("wav",        Cat.Audio,    "WAV audio",                 Nf.SetiAudio,      "wav");
        Ext("flac",       Cat.Audio,    "FLAC audio",                Nf.SetiAudio,      "flac");
        Ext("ogg",        Cat.Audio,    "Ogg audio",                 Nf.SetiAudio,      "ogg opus");
        Ext("aac",        Cat.Audio,    "AAC audio",                 Nf.SetiAudio,      "aac m4a");
        Ext("mp4",        Cat.Video,    "MP4 video",                 Nf.SetiVideo,      "mp4 m4v");
        Ext("mov",        Cat.Video,    "QuickTime",                 Nf.SetiVideo,      "mov qt");
        Ext("avi",        Cat.Video,    "AVI video",                 Nf.SetiVideo,      "avi");
        Ext("mkv",        Cat.Video,    "Matroska video",            Nf.SetiVideo,      "mkv");
        Ext("webm",       Cat.Video,    "WebM",                      Nf.SetiVideo,      "webm");
        Ext("wmv",        Cat.Video,    "Windows Media",             Nf.SetiVideo,      "wmv");

        // ── archives ────────────────────────────────────────────────────────────────────────────────────────
        // The compound rows come first only for readability; ForFileName always probes the two-segment
        // extension before the one-segment one, so "archive.tar.gz" is a tarball and "archive.gz" is not.
        Ext("targz",      Cat.Archive,  "Gzip tarball",              Nf.SetiZip,        "tar.gz tgz");
        Ext("tarbz2",     Cat.Archive,  "Bzip2 tarball",             Nf.SetiZip,        "tar.bz2 tbz2");
        Ext("tarxz",      Cat.Archive,  "XZ tarball",                Nf.SetiZip,        "tar.xz txz");
        Ext("tar",        Cat.Archive,  "TAR archive",               Nf.SetiZip,        "tar");
        Ext("gz",         Cat.Archive,  "Gzip archive",              Nf.SetiZip,        "gz gzip");
        Ext("bz2",        Cat.Archive,  "Bzip2 archive",             Nf.SetiZip,        "bz2");
        Ext("xz",         Cat.Archive,  "XZ archive",                Nf.SetiZip,        "xz zst");
        Ext("zip",        Cat.Archive,  "ZIP archive",               Nf.SetiZip,        "zip");
        Ext("sevenzip",   Cat.Archive,  "7-Zip archive",             Nf.SetiZip,        "7z");
        Ext("rar",        Cat.Archive,  "RAR archive",               Nf.SetiZip,        "rar");

        // ── documents ───────────────────────────────────────────────────────────────────────────────────────
        Ext("pdf",        Cat.Document, "PDF",              Nf.FaFilePdf,      "pdf"); // design page
        Ext("doc",        Cat.Document, "Word",             Nf.SetiWord,       "doc docx");
        Ext("xls",        Cat.Document, "Excel",            Nf.SetiXls,        "xls xlsx xlsm");
        Ext("ppt",        Cat.Document, "PowerPoint",       Nf.FaFilePowerPoint, "ppt pptx");
        Ext("rtf",        Cat.Document, "Rich Text",        Nf.MdFileDocument, "rtf odt");

        // ── binaries & fonts ────────────────────────────────────────────────────────────────────────────────
        Ext("exe",        Cat.Executable, "Application",             Nf.OctFileBinary,  "exe com");
        Ext("msi",        Cat.Executable, "Installer",               Nf.OctFileBinary,  "msi pkg deb rpm");
        Ext("bin",        Cat.Executable, "Binary file",             Nf.OctFileBinary,  "bin dat");
        Ext("dll",        Cat.Library,    "Dynamic Lib",             Nf.OctFileBinary,  "dll");
        Ext("so",         Cat.Library,    "Shared Lib",              Nf.OctFileBinary,  "so dylib a lib");
        Ext("ttf",        Cat.Font,       "TrueType font",           Nf.SetiFont,       "ttf ttc");
        Ext("otf",        Cat.Font,       "OpenType font",           Nf.SetiFont,       "otf");
        Ext("woff",       Cat.Font,       "Web font",                Nf.SetiFont,       "woff woff2 eot");

        // ── well-known file names (consulted BEFORE any extension) ──────────────────────────────────────────
        Named("gitignore",     Cat.Data,   "Git ignore rules",       Nf.DevGit,         ".gitignore"); // design page
        Named("gitattributes", Cat.Data,   "Git attributes",         Nf.DevGit,         ".gitattributes");
        Named("gitmodules",    Cat.Data,   "Git submodules",         Nf.DevGit,         ".gitmodules");
        Named("gitkeep",       Cat.Data,   "Git placeholder",        Nf.DevGit,         ".gitkeep .gitconfig");
        Named("makefile",      Cat.Source, "Makefile",               Nf.SetiMakefile,   "Makefile GNUmakefile Makefile.am Makefile.in");
        Named("cmake",         Cat.Source, "CMake script",           Nf.SetiMakefile,   "CMakeLists.txt");
        Named("dockerfile",    Cat.Source, "Dockerfile",             Nf.SetiDocker,     "Dockerfile Containerfile");
        Named("dockerignore",  Cat.Data,   "Docker ignore rules",    Nf.SetiDocker,     ".dockerignore");
        Named("compose",       Cat.Data,   "Docker Compose file",    Nf.SetiDocker,     "docker-compose.yml docker-compose.yaml compose.yml compose.yaml");
        Named("editorconfig",  Cat.Data,   "EditorConfig",           Nf.SetiEditorConfig, ".editorconfig");
        Named("npmmanifest",   Cat.Data,   "npm manifest",           Nf.SetiNpm,        "package.json");
        Named("npmlock",       Cat.Data,   "npm lockfile",           Nf.SetiNpm,        "package-lock.json");
        Named("tsconfig",      Cat.Data,   "TypeScript config",      Nf.SetiTsconfig,   "tsconfig.json");

        // ── well-known stems (name minus a prose extension: LICENSE, LICENSE.md, license.txt …) ─────────────
        Stem("license",   Cat.Text,     "License",                   Nf.OctLaw,         "license licence copying unlicense notice", emoji: "📜"); // design page
        Stem("readme",    Cat.Text,     "Readme",                    Nf.OctBook,        "readme",                                    emoji: "📖");
        Stem("changelog", Cat.Text,     "Changelog",                 Nf.OctHistory,     "changelog changes history");

        // ── directories, drives and the places rail ─────────────────────────────────────────────────────────
        //          place                          kind label        glyph                emoji  text  ascii
        Place(FileSystemPlace.Folder,         Cat.Folder, "Folder",         Nf.CustomFolder,   "📁", "▸", "/"); // design page
        Place(FileSystemPlace.ParentFolder,   Cat.Folder, "Parent folder",  Nf.FaLevelUp,      "⬆️", "▴", "^");
        Place(FileSystemPlace.Home,           Cat.Place,  "Home folder",    Nf.FaHome,         "🏠", "▣", "~"); // design page
        Place(FileSystemPlace.Desktop,        Cat.Place,  "Desktop",        Nf.FaDesktop,      "🖥️", "▭", "/");
        Place(FileSystemPlace.Documents,      Cat.Place,  "Documents",      Nf.MdFileDocumentMultiple, "📂", "▦", "/");
        Place(FileSystemPlace.Downloads,      Cat.Place,  "Downloads",      Nf.FaDownload,     "⬇️", "↓", "/");
        Place(FileSystemPlace.Pictures,       Cat.Place,  "Pictures",       Nf.FaPicture,      "🖼️", "▨", "/");
        Place(FileSystemPlace.Music,          Cat.Place,  "Music",          Nf.FaMusic,        "🎵", "♪", "/");
        Place(FileSystemPlace.Videos,         Cat.Place,  "Videos",         Nf.FaFilm,         "🎬", "▷", "/");
        Place(FileSystemPlace.Recent,         Cat.Place,  "Recent items",   Nf.FaHistory,      "🕘", "◷", "/");
        Place(FileSystemPlace.Favorites,      Cat.Place,  "Favorite",       Nf.FaStar,         "📌", "★", "*"); // design page
        Place(FileSystemPlace.Trash,          Cat.Place,  "Trash",          Nf.FaTrash,        "🗑️", "⌫", "x");
        Place(FileSystemPlace.Computer,       Cat.Drive,  "This computer",  Nf.MdDesktopTower, "💻", "▥", "=");
        Place(FileSystemPlace.Drive,          Cat.Drive,  "Local drive",    Nf.FaHdd,          "💾", "▤", "="); // design page
        Place(FileSystemPlace.RemovableDrive, Cat.Drive,  "Removable drive", Nf.FaUsb,         "💽", "▧", "=");
        Place(FileSystemPlace.NetworkDrive,   Cat.Drive,  "Network drive",  Nf.OctServer,      "🌐", "⇆", "@");
        Place(FileSystemPlace.CloudDrive,     Cat.Drive,  "Cloud drive",    Nf.FaCloud,        "☁️", "☁", "@"); // design page

        // The per-category generics: the fallback a consumer gets from ForCategory, and the row an unknown file
        // lands on (Cat.Unknown). Built from the same Defaults() switch the rows above draw their lower tiers
        // from, so a category can never present one set of tiers here and another there.
        foreach (var category in Enum.GetValues<FileTypeCategory>())
        {
            var (emoji, text, ascii, label) = Defaults(category);
            var glyph = category switch
                        {
                            Cat.Folder     => Nf.CustomFolder,
                            Cat.Drive      => Nf.FaHdd,
                            Cat.Place      => Nf.CustomFolder,
                            Cat.Text       => Nf.SetiText,
                            Cat.Document   => Nf.MdFileDocument,
                            Cat.Source     => Nf.OctFileCode,
                            Cat.Markup     => Nf.SetiXml,
                            Cat.Data       => Nf.SetiConfig,
                            Cat.Image      => Nf.SetiImage,
                            Cat.Vector     => Nf.MdSvg,
                            Cat.Audio      => Nf.SetiAudio,
                            Cat.Video      => Nf.SetiVideo,
                            Cat.Archive    => Nf.SetiZip,
                            Cat.Executable => Nf.OctFileBinary,
                            Cat.Library    => Nf.OctFileBinary,
                            Cat.Font       => Nf.SetiFont,
                            _              => Nf.FaFile
                        };

            var descriptor = new FileTypeDescriptor($"category:{category.ToString().ToLowerInvariant()}",
                                                    category,
                                                    label,
                                                    glyph,
                                                    emoji,
                                                    text,
                                                    ascii);

            categories.Add(category, descriptor);
            all.Add(descriptor.Id, descriptor); // the "category:" prefix keeps these out of the rows' id space
        }

        ExtensionMap = extensions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        FileNameMap = fileNames.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        StemMap = stems.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        PlaceMap = places.ToFrozenDictionary();
        CategoryMap = categories.ToFrozenDictionary();

        AllEntries = [.. all.Values];

        GenericFile = categories[Cat.Unknown];
        Folder = places[FileSystemPlace.Folder];
        ParentFolder = places[FileSystemPlace.ParentFolder];
        Drive = places[FileSystemPlace.Drive];

        return;

        // Registers one row under a space-separated list of extensions (no leading dots; "tar.gz" is a
        // compound). The lower tiers come from the row's category unless the design page names an override.
        void Ext(string id, FileTypeCategory category, string label, string glyph, string keys, string? emoji = null)
            => Register(id, category, label, glyph, keys, emoji, extensions);

        // Registers one row under a space-separated list of exact file names.
        void Named(string id, FileTypeCategory category, string label, string glyph, string keys, string? emoji = null)
            => Register(id, category, label, glyph, keys, emoji, fileNames);

        // Registers one row under a space-separated list of file-name STEMS (see the stem rule in ForFileName).
        void Stem(string id, FileTypeCategory category, string label, string glyph, string keys, string? emoji = null)
            => Register(id, category, label, glyph, keys, emoji, stems);

        void Place(FileSystemPlace place, FileTypeCategory category, string label, string glyph, string emoji, string text, string ascii)
        {
            var descriptor = new FileTypeDescriptor(place.ToString().ToLowerInvariant(), category, label, glyph, emoji, text, ascii);
            places.Add(place, descriptor);
            all.Add(descriptor.Id, descriptor); // an id shared with an extension row is an authoring bug
        }

        void Register(string id,
                      FileTypeCategory category,
                      string label,
                      string glyph,
                      string keys,
                      string? emoji,
                      Dictionary<string, FileTypeDescriptor> target)
        {
            var (categoryEmoji, text, ascii, _) = Defaults(category);
            var descriptor = new FileTypeDescriptor(id, category, label, glyph, emoji ?? categoryEmoji, text, ascii);

            all.Add(id, descriptor); // duplicate ids are an authoring bug — fail loudly at type-init

            foreach (var key in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                target.Add(key, descriptor);
        }
    }

    // ───────────────────────────── category defaults ─────────────────────────────
    // The emoji / Unicode / ASCII tiers, authored once per category (the design page's "broad categories"
    // point). Rules the completeness test enforces:
    //   · emoji MUST measure 2 cells — that is the tier's contract, and Icon budgets the continuation cell;
    //   · the Unicode floor MUST measure 1 cell and must NOT be an Emoji_Presentation glyph (⭐, ⛔ and friends
    //     measure 2, which would shift a listing's Name column on non-Nerd-Font terminals);
    //   · the ASCII floor is a single printable 7-bit character.
    // Collisions BETWEEN categories are expected and fine at the two floors — there are not 17 legible
    // single-cell marks, let alone 17 ASCII ones, and the floors are the least expressive tiers by design
    // (the design page's base pass leaves every file row's icon blank and distinguishes only folder/drive/
    // cloud/star). Per-type specificity is the Nerd Font tier's job.
    private static (string Emoji, string Text, string Ascii, string Label) Defaults(FileTypeCategory category)
        => category switch
           {
               Cat.Folder     => ("📁", "▸", "/", "Folder"),
               Cat.Drive      => ("💾", "▤", "=", "Drive"),
               Cat.Place      => ("📂", "▸", "~", "Place"),
               Cat.Text       => ("📝", "≡", "t", "Text document"),
               Cat.Document   => ("📕", "≣", "p", "Document"),
               Cat.Source     => ("💻", "⌗", "c", "Source file"),
               Cat.Markup     => ("📃", "⌗", "h", "Markup document"),
               Cat.Data       => ("🔧", "◈", "d", "Data file"),
               Cat.Image      => ("🖼️", "▨", "i", "Image"),
               Cat.Vector     => ("🎨", "◇", "g", "Vector image"),
               Cat.Audio      => ("🎵", "♪", "a", "Audio file"),
               Cat.Video      => ("🎬", "▷", "v", "Video file"),
               Cat.Archive    => ("📦", "▩", "z", "Archive"),
               Cat.Executable => ("🚀", "▰", "*", "Application"),
               Cat.Library    => ("📚", "▱", "b", "Library"),
               Cat.Font       => ("🔤", "ℱ", "f", "Font file"),
               _              => ("📄", "▪", "-", "File") // Cat.Unknown — the design page's generic 📄 row
           };

    // ───────────────────────────── lookup ─────────────────────────────

    /// <summary>
    /// The row for a <see cref="FileSystemPlace"/> — a directory, a volume, or a rail entry that has no file
    /// name to inspect.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="place"/> is not a defined member.</exception>
    public static FileTypeDescriptor ForPlace(FileSystemPlace place)
        => PlaceMap.TryGetValue(place, out var descriptor)
               ? descriptor
               : throw new ArgumentOutOfRangeException(nameof(place), place, "Unknown file-system place.");

    /// <summary>
    /// The generic row for a <see cref="FileTypeCategory"/> — the icons and label a consumer should use when it
    /// knows the family but not the type (a "Files of type: Images" filter row, an entry whose extension is
    /// recognized by the app but not by this table).
    /// </summary>
    public static FileTypeDescriptor ForCategory(FileTypeCategory category)
        => CategoryMap.TryGetValue(category, out var descriptor) ? descriptor : GenericFile;

    /// <summary>
    /// The row for a bare extension, with or without its leading dot and in any casing (".PNG", "png",
    /// "tar.gz" all work). Unknown or empty extensions fall to <see cref="GenericFile"/>.
    /// </summary>
    public static FileTypeDescriptor ForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return GenericFile;

        var key = extension.Trim().TrimStart('.');

        return key.Length > 0 && ExtensionMap.TryGetValue(key, out var descriptor) ? descriptor : GenericFile;
    }

    /// <summary>
    /// The row for a file name. Accepts a bare name or a full path (anything up to the last <c>/</c> or
    /// <c>\</c> is discarded), and resolves in this order:
    /// <list type="number">
    /// <item>an exact well-known name — <c>.gitignore</c>, <c>Makefile</c>, <c>docker-compose.yml</c>;</item>
    /// <item>a well-known <b>stem</b> wearing a prose extension or none at all — <c>LICENSE</c>,
    /// <c>LICENSE.md</c>, <c>readme.txt</c>. Narrowly gated on <see cref="StemExtensions"/> so
    /// <c>license.png</c> stays an image;</item>
    /// <item>the two-segment <b>compound</b> extension — <c>archive.tar.gz</c> is a "Gzip tarball", not a
    /// "Gzip archive", and <c>api.d.ts</c> is a declaration file;</item>
    /// <item>the last extension;</item>
    /// <item><see cref="GenericFile"/>.</item>
    /// </list>
    /// A leading dot never starts an extension (<c>.gitignore</c> is a name, not a "gitignore file"), matching
    /// every shell and file manager.
    /// </summary>
    public static FileTypeDescriptor ForFileName(string? fileName)
    {
        var name = TrimToName(fileName);

        if (name.Length == 0)
            return GenericFile;

        if (FileNameMap.TryGetValue(name, out var byName))
            return byName;

        var (stem, extension, compound) = SplitExtensions(name);

        if (StemExtensions.Contains(extension) && StemMap.TryGetValue(stem, out var byStem))
            return byStem;

        if (compound is not null && ExtensionMap.TryGetValue(compound, out var byCompound))
            return byCompound;

        if (extension.Length > 0 && ExtensionMap.TryGetValue(extension, out var byExtension))
            return byExtension;

        return GenericFile;
    }

    /// <summary>
    /// The row for one listing entry: <see cref="Folder"/> when <paramref name="isDirectory"/>, otherwise
    /// <see cref="ForFileName"/>. The one call a file dialog's item template needs.
    /// </summary>
    public static FileTypeDescriptor ForEntry(string? name, bool isDirectory)
        => isDirectory ? Folder : ForFileName(name);

    /// <summary>
    /// The row for a <see cref="FileSystemInfo"/> — <see cref="Folder"/> for a <see cref="DirectoryInfo"/>,
    /// otherwise the entry's name resolved through <see cref="ForFileName"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public static FileTypeDescriptor ForEntry(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry is DirectoryInfo ? Folder : ForFileName(entry.Name);
    }

    /// <summary>
    /// The row for a volume, by <see cref="DriveType"/>: removable and optical media get the removable-drive
    /// row, network shares the network row, everything else the fixed <see cref="Drive"/> row.
    /// </summary>
    public static FileTypeDescriptor ForDriveType(DriveType driveType)
        => driveType switch
           {
               DriveType.Removable => ForPlace(FileSystemPlace.RemovableDrive),
               DriveType.CDRom     => ForPlace(FileSystemPlace.RemovableDrive),
               DriveType.Network   => ForPlace(FileSystemPlace.NetworkDrive),
               _                   => Drive
           };

    // ───────────────────────────── name parsing ─────────────────────────────

    // Reduces a path to its final segment. Trailing separators are dropped first so "assets/textures/" yields
    // "textures" rather than an empty name — directory listings and breadcrumbs both hand out that shape.
    private static string TrimToName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim().TrimEnd(PathSeparators);

        if (trimmed.Length == 0)
            return string.Empty;

        var separator = trimmed.LastIndexOfAny(PathSeparators);

        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }

    // Splits a bare file name into (stem, last extension, two-segment compound extension). A dot at index 0 is
    // NOT an extension separator — ".gitignore" is a hidden file whose whole name is its stem — and neither is
    // the second dot of ".config.yml", which would otherwise produce the nonsense compound "config.yml".
    private static (string Stem, string Extension, string? Compound) SplitExtensions(string name)
    {
        var lastDot = name.LastIndexOf('.');

        if (lastDot <= 0)
            return (name, string.Empty, null);

        var stem = name[..lastDot];
        var extension = name[(lastDot + 1)..];
        var previousDot = stem.LastIndexOf('.');
        var compound = previousDot > 0 ? name[(previousDot + 1)..] : null;

        return (stem, extension, compound);
    }
}
