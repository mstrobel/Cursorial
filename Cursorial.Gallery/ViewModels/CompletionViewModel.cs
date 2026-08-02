using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Matching;
using Cursorial.UI.Themes;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Completion page: one ordinary <see cref="TextBox"/> with a <see cref="CompletionPopup"/> attached to it over
/// a FIXED in-memory catalog of ~60 realistic terms — half CamelCase type names, half snake/kebab file names — so
/// that <see cref="FuzzyMatcher"/>'s two headline behaviours are visible after four keystrokes: <c>inpc</c> lands
/// <c>INotifyPropertyChanged</c> (camel humps plus the acronym tail) and <c>hban</c> lands <c>hero_banner.png</c>
/// (the <c>_</c> separator boundary). The matched cells are bold in every row, and each row carries the
/// <see cref="FileTypeIcons"/> icon and Type-column label of its own kind.
/// <para>
/// <b>All three seams of the control are on this page.</b> The <see cref="Provider"/> is the token grammar: it
/// decides where the completed span starts and — importantly — where it <em>ends</em>, which is <b>not</b> the
/// caret (put the caret in the middle of <c>hero_ban|ner.png</c> and the accept still replaces the whole token, the
/// bug <see cref="CompletionContext"/> documents). The matcher does the filtering, ranking and highlighting;
/// nothing here re-implements a <c>StartsWith</c>. And <see cref="Commit"/> is the commit hook, which deliberately
/// does something the default <see cref="CompletionCommit.Splice"/> would not: <c>Enter</c> on a <i>group</i> row
/// appends the <c>/</c> separator and keeps the session OPEN so the popup immediately offers that group's members
/// (the path-bar drill-in), while <c>Tab</c> on the same row inserts the bare name and closes. Same candidate, two
/// gestures, two answers — which is exactly why the hook takes a <see cref="CompletionAcceptReason"/>.
/// </para>
/// <para>
/// <b>Keyboard.</b> Focus never leaves the field. While the popup is up: <c>↑</c>/<c>↓</c> move the highlight,
/// <c>PageUp</c>/<c>PageDown</c> page it, <c>Tab</c>/<c>Enter</c> accept, <c>Esc</c> dismisses <em>without</em>
/// reverting what you typed (and, because the popup marks it handled, without reaching the shell's quit binding).
/// Every other key goes straight to the field.
/// </para>
/// </summary>
public sealed class CompletionViewModel : PageViewModel
{
    /// <summary>The one character this page's toy grammar treats as a group boundary. It is the <em>page's</em>
    /// convention, not the control's — <see cref="CompletionPopup"/> has no notion of a separator at all.</summary>
    private const char GroupSeparator = '/';

    // The C# source row's tiered icon, reused for every symbol term (one carrier, shared: an IconCarrier is an
    // immutable value, unlike an Icon element, which can only live at one place in one tree).
    private static readonly IconCarrier SymbolIcon = FileTypeIcons.ForExtension("cs").ToIconCarrier();
    private static readonly IconCarrier GroupIcon = FileTypeIcons.Folder.ToIconCarrier();

    private readonly Dictionary<string, IReadOnlyList<CompletionItem>> _catalog = BuildCatalog();

    public CompletionViewModel()
    {
        Provider = new TermProvider(_catalog);
        ClearCommand = new RelayCommand(() => Text = "");
    }

    public override string Title => "Completion";

    public override string Summary =>
        "A TextBox + CompletionPopup over a fixed 60-term catalog. Type inpc → INotifyPropertyChanged, hban → " +
        "hero_banner.png; ↑/↓ move, Tab/Enter accept, Esc dismisses without reverting the field.";

    /// <summary>The completion seam handed to <see cref="CompletionPopup.Provider"/> (a plain <c>{Binding}</c>).</summary>
    public ICompletionProvider Provider { get; }

    /// <summary>Empties the field (and with it any open session — the popup closes when nothing matches).</summary>
    public ICommand ClearCommand { get; }

    /// <summary>The field's text, two-way bound. The page never parses it; the provider does.</summary>
    public string Text
    {
        get;
        set => Set(ref field, value);
    } = "";

    /// <summary>The live readout of what the last accept did — the seam made legible.</summary>
    public override string? Status
    {
        get;
        protected set => Set(ref field, value);
    } = "Type to complete — nothing accepted yet.";

    // ───────────────────────────── the commit hook ─────────────────────────────

    /// <summary>
    /// The <see cref="CompletionPopup.CommitHandler"/>: turns an accepted candidate plus the gesture that accepted
    /// it into the field's new text, caret and session lifetime.
    /// <para>
    /// Every catalog item leaves <see cref="CompletionItem.InsertText"/> and
    /// <see cref="CompletionItem.ContinuesSession"/> at their defaults on purpose, so the default
    /// <see cref="CompletionCommit.Splice"/> would replace the span with the bare display text and close. All of the
    /// divergence below is therefore unmistakably the <em>hook's</em> doing.
    /// </para>
    /// </summary>
    /// <param name="accept">The candidate, the field's text, the span the session owns, and the gesture.</param>
    public CompletionCommit Commit(CompletionAccept accept)
    {
        // Enter (and a click, which means the same thing) on a GROUP drills in: splice the group's KEY — the host
        // payload the candidate carries, which already ends in the separator — and keep the session alive, so the
        // popup re-queries against the POST-commit text and offers that group's members. Tab means "complete as
        // far as this candidate": the bare display text, session closed.
        var drill = accept.Item.Data is TermGroup
                    && accept.Reason is CompletionAcceptReason.Enter or CompletionAcceptReason.Pointer;

        var insert = drill && accept.Item.Data is TermGroup group ? group.Key : accept.Item.Display;

        // The span is [ReplaceStart, ReplaceEnd) — already clamped into the text by the popup — and ReplaceEnd may
        // sit AFTER the caret, which is the whole reason the splice is written against the span and not the caret.
        var text = accept.Text
                         .Remove(accept.ReplaceStart, accept.ReplaceEnd - accept.ReplaceStart)
                         .Insert(accept.ReplaceStart, insert);

        return new CompletionCommit(text, accept.ReplaceStart + insert.Length, drill);
    }

    /// <summary>Reports a landed commit into <see cref="Status"/>. Raised from
    /// <see cref="CompletionPopup.Committed"/> — i.e. after the text is already written, so the hook itself stays a
    /// pure function of its argument.</summary>
    /// <param name="e">The commit that was applied.</param>
    public void ReportCommit(CompletionCommittedEventArgs e)
    {
        var gesture = e.Reason switch
                      {
                          CompletionAcceptReason.Tab     => "Tab",
                          CompletionAcceptReason.Enter   => "Enter",
                          CompletionAcceptReason.Pointer => "Click",
                          _                              => "Programmatic"
                      };

        Status = e.Commit.KeepOpen
            ? $"{gesture} on group “{e.Item.Display}” → the hook appended “{GroupSeparator}” and KEPT the session open (drill-in)."
            : $"{gesture} accepted “{e.Item.Display}” ({e.Item.KindLabel}) → spliced over the token; session closed.";
    }

    /// <summary>Reports a dismissed session (<c>Esc</c>, or the field losing focus) into <see cref="Status"/>.</summary>
    public void ReportDismissed() => Status = "Session dismissed — the field keeps everything you typed.";

    // ───────────────────────────── the provider ─────────────────────────────

    /// <summary>
    /// The page's token grammar. A token is the run of letters, digits, <c>_ - .</c> and <c>/</c> around the caret;
    /// the part of it BEFORE the caret splits at the last <c>/</c> into a group prefix (which selects the candidate
    /// set) and the pattern (which the matcher filters and bolds).
    /// <para>
    /// The two shapes worth noticing are the ones <see cref="CompletionContext"/> exists to express. First,
    /// <c>ReplaceEnd</c> walks FORWARD past the caret to the token's real end, so completing from the middle of
    /// <c>hero_ban|ner.png</c> replaces the whole token instead of leaving a <c>ner.png</c> tail behind. Second, the
    /// pattern is independent of the span: with <c>assets/he|</c> the span starts after the <c>/</c> while the
    /// pattern is just <c>he</c> — deriving one from the other would filter on <c>assets/he</c> and match nothing.
    /// </para>
    /// </summary>
    /// <param name="catalog">The fixed candidate sets, keyed by group prefix (<c>""</c> is the root).</param>
    private sealed class TermProvider(IReadOnlyDictionary<string, IReadOnlyList<CompletionItem>> catalog) : ICompletionProvider
    {
        /// <inheritdoc/>
        public CompletionContext? GetCompletions(in CompletionQuery query)
        {
            var text = query.Text;
            var caret = query.CaretIndex;

            var start = caret;
            while (start > 0 && IsTokenChar(text[start - 1]))
                start--;

            var head = text.AsSpan(start, caret - start);
            var prefixLength = head.LastIndexOf(GroupSeparator) + 1; // 0 when there is no separator
            var group = head[..prefixLength].ToString();

            // An unknown group ("nope/") has nothing to offer — returning null closes any open session, which is
            // the honest answer and cheaper than offering the root set under a prefix that does not exist.
            if (!catalog.TryGetValue(group, out var items))
                return null;

            var end = caret;
            while (end < text.Length && IsTokenChar(text[end]) && text[end] != GroupSeparator)
                end++;

            return new CompletionContext(start + prefixLength, end, head[prefixLength..].ToString(), items);
        }

        private static bool IsTokenChar(char c)
            => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or GroupSeparator;
    }

    /// <summary>The <see cref="CompletionItem.Data"/> payload marking a catalog group — the host payload the commit
    /// hook reads back off the accepted candidate. The popup never looks at it.</summary>
    /// <param name="Key">The group's catalog key, e.g. <c>"assets/"</c>.</param>
    private sealed record TermGroup(string Key);

    // ───────────────────────────── the catalog ─────────────────────────────
    //
    // Fixed and in memory, which is the shape ICompletionProvider is synchronous FOR: a keystroke must leave the
    // popup holding a consistent (text, caret, span, items) tuple before the next key arrives. Order matters — an
    // empty pattern deliberately shows the PROVIDER's order rather than the matcher's ranking — so the symbols come
    // first, then the files, then the groups.

    private static Dictionary<string, IReadOnlyList<CompletionItem>> BuildCatalog() => new(StringComparer.Ordinal)
    {
        [""] =
        [
            SymbolTerm("INotifyPropertyChanged", "interface"),
            SymbolTerm("INotifyCollectionChanged", "interface"),
            SymbolTerm("IReadOnlyCollection", "interface"),
            SymbolTerm("IReadOnlyDictionary", "interface"),
            SymbolTerm("IEqualityComparer", "interface"),
            SymbolTerm("IServiceProvider", "interface"),
            SymbolTerm("IValueConverter", "interface"),
            SymbolTerm("IAsyncEnumerable", "interface"),
            SymbolTerm("IDisposable", "interface"),
            SymbolTerm("IComparable", "interface"),
            SymbolTerm("ObservableCollection", "class"),
            SymbolTerm("ConcurrentDictionary", "class"),
            SymbolTerm("CancellationTokenSource", "class"),
            SymbolTerm("DependencyPropertyChangedEventArgs", "class"),
            SymbolTerm("HierarchicalDataTemplate", "class"),
            SymbolTerm("RoutedEventArgs", "class"),
            SymbolTerm("TextSearchNavigator", "class"),
            SymbolTerm("PropertyChangedEventHandler", "delegate"),
            SymbolTerm("GraphemeClusterEnumerator", "struct"),
            SymbolTerm("ContentPresenter", "control"),
            SymbolTerm("ScrollContentPresenter", "control"),
            SymbolTerm("VirtualizingStackPanel", "control"),
            SymbolTerm("UniformWrapPanel", "control"),
            SymbolTerm("BreadcrumbBarItem", "control"),
            SymbolTerm("CompletionListItem", "control"),
            SymbolTerm("ListViewColumnHeader", "control"),
            SymbolTerm("KeyboardNavigationMode", "enum"),
            SymbolTerm("HorizontalAlignment", "enum"),
            SymbolTerm("ScrollBarVisibility", "enum"),
            SymbolTerm("RelativeSourceMode", "enum"),

            FileTerm("hero_banner.png"),
            FileTerm("gradient-map.json"),
            FileTerm("icon_sprite.svg"),
            FileTerm("user-avatar.jpg"),
            FileTerm("splash_screen.mp4"),
            FileTerm("release_notes.md"),
            FileTerm("build-matrix.yaml"),
            FileTerm("docker-compose.yml"),
            FileTerm("package-lock.json"),
            FileTerm("tsconfig.base.json"),
            FileTerm("site-manifest.json"),
            FileTerm("deploy-preview.sh"),
            FileTerm("bootstrap_env.ps1"),
            FileTerm("lua_bindings.lua"),
            FileTerm("noise-octaves.glsl"),
            FileTerm("theme-dark.css"),
            FileTerm("color_tokens.scss"),
            FileTerm("keymap_default.toml"),
            FileTerm("telemetry-schema.proto"),
            FileTerm("crash_report.log"),
            FileTerm("archive.tar.gz"),
            FileTerm("README.md"),
            FileTerm("CHANGELOG.md"),
            FileTerm("LICENSE"),
            FileTerm("Makefile"),
            FileTerm(".gitignore"),
            FileTerm(".editorconfig"),

            GroupTerm("assets"),
            GroupTerm("shaders"),
            GroupTerm("docs"),
            GroupTerm("vendor"),
        ],

        ["assets/"] =
        [
            FileTerm("hero_banner.png"),
            FileTerm("sprite_atlas.png"),
            FileTerm("icon_sprite.svg"),
            FileTerm("user-avatar.jpg"),
            FileTerm("splash_screen.mp4"),
            FileTerm("gradient-map.json"),
        ],

        ["shaders/"] =
        [
            FileTerm("noise-octaves.glsl"),
            FileTerm("blur_kernel.glsl"),
            FileTerm("vertex_common.glsl"),
            FileTerm("tone-map.hlsl"),
        ],

        ["docs/"] =
        [
            FileTerm("architecture.md"),
            FileTerm("protocol.md"),
            FileTerm("getting-started.md"),
            FileTerm("release_notes.md"),
        ],

        ["vendor/"] =
        [
            FileTerm("package-lock.json"),
            FileTerm("archive.tar.gz"),
            FileTerm("third_party.txt"),
            FileTerm("LICENSE"),
        ],
    };

    /// <summary>A CamelCase symbol term: the kind label is authored here (<c>interface</c>, <c>enum</c>, …) because
    /// the file-type table has nothing to say about a type name.</summary>
    private static CompletionItem SymbolTerm(string display, string kind)
        => new(display) { KindLabel = kind, Icon = SymbolIcon };

    /// <summary>A file-name term: BOTH its icon and its kind label come out of <see cref="FileTypeIcons"/>, so the
    /// row shows the same per-extension glyph and Type-column text the file dialog's listing will.</summary>
    private static CompletionItem FileTerm(string name)
    {
        var type = FileTypeIcons.ForFileName(name);
        return new CompletionItem(name) { KindLabel = type.KindLabel, Icon = type.ToIconCarrier() };
    }

    /// <summary>A group term — the row the commit hook treats specially (see <see cref="Commit"/>).</summary>
    private static CompletionItem GroupTerm(string name)
        => new(name) { KindLabel = "group", Icon = GroupIcon, Data = new TermGroup(name + GroupSeparator) };
}
