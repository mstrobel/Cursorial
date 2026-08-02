using Cursorial.UI.Matching;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Controls;

/// <summary>
/// Why <see cref="CompletionPopup"/> is asking its <see cref="ICompletionProvider"/> for candidates.
/// A provider that behaves the same for every trigger can ignore it; a provider that wants
/// "complete while typing but never pop open on a bare caret move" branches on it.
/// </summary>
public enum CompletionTrigger
{
    /// <summary>The target's text changed (the user typed, pasted, or deleted) — the usual trigger.</summary>
    TextChanged,

    /// <summary>The caret moved with the session already open (an arrow key, a click in the field).
    /// Never opens a session; it only re-derives the span/pattern of one that is already showing.</summary>
    CaretMoved,

    /// <summary>The host called <see cref="CompletionPopup.Refresh"/> — an explicit "complete now"
    /// gesture (a Ctrl+Space binding, or a dialog seeding the field programmatically).</summary>
    Explicit,

    /// <summary>
    /// A commit asked to keep the session alive (<see cref="CompletionCommit.KeepOpen"/> — the path
    /// bar's "Enter on a folder inserts it plus a separator and keeps drilling"). The text the
    /// provider sees is the POST-commit text.
    /// </summary>
    Continued,
}

/// <summary>
/// Which gesture accepted a candidate. The two reference consumers deliberately disagree about what
/// Tab and Enter mean, which is exactly why the commit hook receives this rather than the popup
/// hard-coding one policy: a path bar drills into a folder on Enter and keeps editing, while an
/// expression editor accepts and closes on both.
/// </summary>
public enum CompletionAcceptReason
{
    /// <summary>Tab — conventionally "complete as far as this candidate, keep going".</summary>
    Tab,

    /// <summary>Enter — conventionally "take this one".</summary>
    Enter,

    /// <summary>A pointer press on a row.</summary>
    Pointer,

    /// <summary>A host call to <see cref="CompletionPopup.AcceptSelected"/> / <see cref="CompletionPopup.Accept"/>.</summary>
    Programmatic,
}

/// <summary>
/// What the popup hands an <see cref="ICompletionProvider"/>: the field's whole text and the caret
/// offset inside it. Deliberately <em>not</em> a pre-chopped "word under the caret" — deciding what
/// the token is (and where it ends) is the provider's job, and the interesting cases are exactly the
/// ones a generic tokenizer gets wrong (a <c>[Field Name]</c> with a space in it, a quoted path
/// segment, an unterminated bracket).
/// </summary>
/// <param name="Text">The field's full text. Never <see langword="null"/>.</param>
/// <param name="CaretIndex">The caret's UTF-16 offset, already clamped into <c>[0, Text.Length]</c>.</param>
/// <param name="Trigger">Why the popup is asking.</param>
public readonly record struct CompletionQuery(string Text, int CaretIndex, CompletionTrigger Trigger);

/// <summary>
/// One candidate offered by a completion provider.
/// <para>
/// <b><see cref="Display"/> and <see cref="InsertText"/> are different strings, on purpose.</b> The
/// row shows the human-readable token and the fuzzy highlight lands on it; the edit splices the
/// syntactically complete form. The DataViews criteria editor is the motivating case — the field
/// <c>Price</c> displays as <c>Price</c> but inserts as <c>[Price]</c>, and the function
/// <c>upper</c> displays as <c>upper</c> but inserts as <c>upper(</c>. A file path bar is the same
/// shape: <c>Documents</c> displays, <c>Documents/</c> inserts.
/// </para>
/// </summary>
/// <param name="Display">The text shown in the row — also what the fuzzy matcher filters, ranks and highlights.</param>
public sealed record CompletionItem(string Display)
{
    /// <summary>What to splice into the field. <see langword="null"/> ⇒ <see cref="Display"/> is inserted verbatim.</summary>
    public string? InsertText { get; init; }

    /// <summary>The capability-tiered row icon (a folder / file glyph), or <see langword="null"/> for no icon column.</summary>
    public IconCarrier? Icon { get; init; }

    /// <summary>
    /// The right-aligned category label — the mock's <c>folder</c> / <c>file</c> annotation. The
    /// popup appends its own <see cref="CompletionPopup.FuzzyLabel"/> to this when the row only
    /// matched as a scattered subsequence, producing the design's <c>folder · fuzzy</c>.
    /// </summary>
    public string? KindLabel { get; init; }

    /// <summary>Optional secondary text (a signature, a size, a type) shown right of the display text.</summary>
    public string? Description { get; init; }

    /// <summary>An arbitrary host payload — the <c>FileSystemInfo</c>, the field descriptor, the function record.
    /// The popup never touches it; the commit hook reads it back off <see cref="CompletionAccept.Item"/>.</summary>
    public object? Data { get; init; }

    /// <summary>
    /// The candidate's default answer to "does accepting me end the session?". <see langword="true"/> for a
    /// folder (accepting drills into it and completion continues on the next segment), <see langword="false"/>
    /// for a leaf. It is only the DEFAULT: <see cref="CompletionCommit.KeepOpen"/> from the commit hook wins,
    /// because the same candidate can end a session on one gesture and continue it on another.
    /// </summary>
    public bool ContinuesSession { get; init; }

    /// <summary>
    /// A coarse ordering bucket applied <b>before</b> the fuzzy score — lower sorts first. This is how
    /// "folders before files" survives ranking: the path provider stamps folders 0 and files 1, and no
    /// score a file earns can float it above a folder. Rows inside a bucket order by
    /// <see cref="FuzzyMatcher.CompareRank"/>. Default 0 (one bucket ⇒ pure score order).
    /// </summary>
    public int SortGroup { get; init; }

    /// <summary>The text actually spliced into the field — <see cref="InsertText"/> when set, else <see cref="Display"/>.</summary>
    public string EffectiveInsertText => InsertText ?? Display;
}

/// <summary>
/// A provider's answer: the span of the field's text this completion session owns, the pattern to
/// filter on, and the candidates.
/// <para>
/// <b>The span is <c>[ReplaceStart, ReplaceEnd)</c>, not <c>[start, caret)</c>.</b> This is the single
/// most important shape in the whole model, and getting it wrong is a real bug that was fixed once
/// already in <c>DataGridExpressionEditor</c>: the caret can sit in the MIDDLE of the token being
/// completed. With the text <c>[Pr|ce]</c> (caret after <c>Pr</c>), replacing only up to the caret and
/// inserting <c>[Price]</c> leaves the <c>ce]</c> tail behind and produces <c>[Price]ce]</c>. The
/// provider — which is the only party that knows the token grammar — reports where the token really
/// ends, and the popup replaces exactly that.
/// </para>
/// <para>
/// <b><see cref="Pattern"/> is independent of the span.</b> In the same example the span covers
/// <c>[Price]</c> (six characters, brackets included) while the pattern is just <c>Pr</c> — what the
/// user has typed so far, which is what should be matched and bolded. Deriving the pattern from the
/// span would filter on <c>[Pr</c> and match nothing.
/// </para>
/// </summary>
public sealed class CompletionContext
{
    /// <summary>Creates a completion context.</summary>
    /// <param name="replaceStart">Inclusive start of the text the accepted candidate replaces.</param>
    /// <param name="replaceEnd">Exclusive end of the text the accepted candidate replaces; <c>&gt;= replaceStart</c>.</param>
    /// <param name="pattern">What the user has typed — the fuzzy filter/highlight pattern. Empty ⇒ every candidate passes, in provider order.</param>
    /// <param name="items">The candidates, unfiltered and unranked (the popup does both).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="replaceStart"/> is negative or greater than <paramref name="replaceEnd"/>.</exception>
    public CompletionContext(int replaceStart, int replaceEnd, string pattern, IReadOnlyList<CompletionItem> items)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replaceStart);
        ArgumentOutOfRangeException.ThrowIfLessThan(replaceEnd, replaceStart);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(items);

        ReplaceStart = replaceStart;
        ReplaceEnd = replaceEnd;
        Pattern = pattern;
        Items = items;
    }

    /// <summary>Inclusive start of the replaced span, a UTF-16 offset into the query's text.</summary>
    public int ReplaceStart { get; }

    /// <summary>Exclusive end of the replaced span — the token's real end, which may sit AFTER the caret.</summary>
    public int ReplaceEnd { get; }

    /// <summary>The typed prefix/pattern to filter and highlight on. Empty ⇒ show everything in provider order.</summary>
    public string Pattern { get; }

    /// <summary>The candidate set. The popup filters, ranks and highlights it; the provider need not.</summary>
    public IReadOnlyList<CompletionItem> Items { get; }

    /// <summary>
    /// Match constraints for the fuzzy pass. The default (<see cref="FuzzyMatchOptions.None"/>) is the
    /// full fuzzy behaviour the design calls for; <see cref="FuzzyMatchOptions.RequirePrefix"/> is the
    /// "Tab completes a literal prefix" mode a path bar wants for its inline completion.
    /// </summary>
    public FuzzyMatchOptions MatchOptions { get; init; }
}

/// <summary>
/// Everything the commit hook needs to decide what accepting a candidate should do to the text.
/// </summary>
/// <param name="Item">The accepted candidate (its <see cref="CompletionItem.Data"/> is the host's payload).</param>
/// <param name="Text">The field's text at the moment of the accept.</param>
/// <param name="ReplaceStart">Inclusive start of the span the session owns.</param>
/// <param name="ReplaceEnd">Exclusive end of the span the session owns — may sit after the caret.</param>
/// <param name="Reason">Which gesture accepted (Tab vs Enter vs a click) — the two consumers differ on this.</param>
public readonly record struct CompletionAccept(
    CompletionItem Item,
    string Text,
    int ReplaceStart,
    int ReplaceEnd,
    CompletionAcceptReason Reason);

/// <summary>
/// The commit hook's answer: the field's new text, where the caret lands, and whether the session
/// survives the edit.
/// </summary>
/// <param name="NewText">The field's replacement text. Never <see langword="null"/>.</param>
/// <param name="NewCaretIndex">Where the caret lands, clamped into the new text by the popup.</param>
/// <param name="KeepOpen">
/// <see langword="true"/> ⇒ re-query the provider against the post-commit text with
/// <see cref="CompletionTrigger.Continued"/> and keep showing the popup (the path bar's folder drill);
/// <see langword="false"/> ⇒ close.
/// </param>
public readonly record struct CompletionCommit(string NewText, int NewCaretIndex, bool KeepOpen)
{
    /// <summary>
    /// The default commit: splice <see cref="CompletionItem.EffectiveInsertText"/> over
    /// <c>[ReplaceStart, ReplaceEnd)</c>, park the caret after the inserted text, and let
    /// <see cref="CompletionItem.ContinuesSession"/> decide whether the session survives. This is
    /// what <see cref="CompletionPopup"/> uses when no <see cref="CompletionPopup.CommitHandler"/>
    /// is supplied, and it is also the useful base case for a handler that only wants to special-case
    /// one <see cref="CompletionAcceptReason"/>.
    /// </summary>
    /// <param name="accept">The accept being committed.</param>
    public static CompletionCommit Splice(in CompletionAccept accept)
    {
        var insert = accept.Item.EffectiveInsertText;
        var start = Math.Clamp(accept.ReplaceStart, 0, accept.Text.Length);
        var end = Math.Clamp(accept.ReplaceEnd, start, accept.Text.Length);

        return new CompletionCommit(
            accept.Text.Remove(start, end - start).Insert(start, insert),
            start + insert.Length,
            accept.Item.ContinuesSession);
    }
}

/// <summary>
/// The completion seam: given the field's text and caret, decide whether there is anything to
/// complete and, if so, which span the completion owns, what pattern to filter on, and which
/// candidates to offer.
/// <para>
/// <b>Why this is synchronous.</b> Cursorial is a single-UI-thread framework — <c>UIHeadlessHost.Create</c>
/// makes the calling thread the UI thread and every element API is affine to it — and this call sits
/// on the keystroke path, where the popup must already hold a consistent
/// <c>(text, caret, span, items)</c> tuple before the next key arrives. A <c>Task</c>-returning seam
/// would buy nothing here and cost three real hazards: a dispatcher hop back onto the UI thread, a
/// generation guard so a slow keystroke's results cannot land after a fast one's, and a visible
/// "popup opens a beat after you stopped typing" flicker. Every candidate source the design actually
/// has is already in memory — a field list, a function table, a directory enumeration the host
/// caches per folder.
/// </para>
/// <para>
/// <b>How to back it with slow work anyway.</b> Return what you have <em>now</em> (an empty item list
/// is fine — the popup simply stays closed), kick the slow work off yourself, and call
/// <see cref="CompletionPopup.Refresh"/> from its UI-thread continuation. The popup re-queries from
/// scratch, so the late answer is scored against the CURRENT text and caret; there is no stale-result
/// window to guard, which is precisely the property an async seam would have taken away.
/// </para>
/// </summary>
public interface ICompletionProvider
{
    /// <summary>
    /// Returns the completion context for <paramref name="query"/>, or <see langword="null"/> when
    /// there is nothing to complete at the caret (which closes any open session).
    /// </summary>
    /// <param name="query">The field's text, the caret offset, and why the popup is asking.</param>
    CompletionContext? GetCompletions(in CompletionQuery query);
}

/// <summary>
/// An <see cref="ICompletionProvider"/> over a delegate — the lambda form for hosts that do not want
/// a named type (<c>popup.Provider = new DelegateCompletionProvider(q =&gt; …)</c>).
/// </summary>
public sealed class DelegateCompletionProvider : ICompletionProvider
{
    private readonly Func<CompletionQuery, CompletionContext?> _callback;

    /// <summary>Creates a provider that forwards every query to <paramref name="callback"/>.</summary>
    /// <param name="callback">The completion callback; returns <see langword="null"/> for "nothing to complete".</param>
    public DelegateCompletionProvider(Func<CompletionQuery, CompletionContext?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    /// <inheritdoc/>
    public CompletionContext? GetCompletions(in CompletionQuery query) => _callback(query);
}

/// <summary>
/// The argument to <see cref="CompletionPopup.Picked"/> — the picker-mode twin of
/// <see cref="CompletionCommittedEventArgs"/>. There is no <c>Commit</c> here because a picker splices no
/// text: it has no field, so the whole answer is "this candidate, by this gesture", and the host does
/// whatever that means to it (navigate, filter, insert a row).
/// <para>
/// <see cref="KeepOpen"/> is the picker's replacement for <see cref="CompletionCommit.KeepOpen"/>. Leave it
/// <see langword="false"/> and the pick closes the popup, which is what a one-shot chooser wants; set it and
/// the popup clears its filter pattern and re-queries the provider, which is how a drill-down picker (pick a
/// folder, keep picking inside it) is built without ever closing and re-opening the surface.
/// </para>
/// </summary>
public sealed class CompletionPickedEventArgs : EventArgs
{
    /// <summary>Creates the event argument.</summary>
    /// <param name="item">The picked candidate.</param>
    /// <param name="reason">The gesture that picked it.</param>
    public CompletionPickedEventArgs(CompletionItem item, CompletionAcceptReason reason)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Reason = reason;
    }

    /// <summary>The picked candidate (its <see cref="CompletionItem.Data"/> is the host's payload).</summary>
    public CompletionItem Item { get; }

    /// <summary>The gesture that picked it — <see cref="CompletionAcceptReason.Enter"/> or
    /// <see cref="CompletionAcceptReason.Pointer"/> for the two user gestures a picker offers.</summary>
    public CompletionAcceptReason Reason { get; }

    /// <summary>
    /// Set by a handler to keep the picker up: the filter pattern is cleared and the provider is re-queried
    /// with <see cref="CompletionTrigger.Continued"/>. Default <see langword="false"/> ⇒ the pick closes it.
    /// </summary>
    public bool KeepOpen { get; set; }
}

/// <summary>The argument to <see cref="CompletionPopup.Committed"/> — what was accepted, how, and what the field became.</summary>
public sealed class CompletionCommittedEventArgs : EventArgs
{
    /// <summary>Creates the event argument.</summary>
    /// <param name="item">The accepted candidate.</param>
    /// <param name="reason">The accepting gesture.</param>
    /// <param name="commit">The commit that was applied to the field.</param>
    public CompletionCommittedEventArgs(CompletionItem item, CompletionAcceptReason reason, CompletionCommit commit)
    {
        Item = item;
        Reason = reason;
        Commit = commit;
    }

    /// <summary>The accepted candidate.</summary>
    public CompletionItem Item { get; }

    /// <summary>The gesture that accepted it.</summary>
    public CompletionAcceptReason Reason { get; }

    /// <summary>The commit that was applied (already written to the target).</summary>
    public CompletionCommit Commit { get; }
}
