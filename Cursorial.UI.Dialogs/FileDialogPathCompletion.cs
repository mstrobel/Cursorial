using System;
using System.Collections.Generic;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The path-bar's completion seam: turns the raw text of the breadcrumb's edit box into the candidates for its
/// <b>final</b> segment, and turns an accepted candidate into the field's new text.
/// <para>
/// <b>Why this lives in the dialog and not in the controls.</b> <see cref="BreadcrumbBar"/> owns the chips ↔
/// edit state machine and nothing else — it "knows nothing about file systems, paths, or separators in
/// strings" — and <see cref="CompletionPopup"/> owns ranking and the overlay and nothing else. Path grammar is
/// the third thing, and it belongs to whoever knows what a path IS. That is this file.
/// </para>
/// <para>
/// <b>The span is not the pattern.</b> With <c>~/Projects/as|sets</c> the session owns
/// <c>[ReplaceStart, ReplaceEnd)</c> = the whole final segment <c>assets</c> — including the <c>sets</c> tail
/// that sits AFTER the caret, so accepting does not leave it stranded — while the filter pattern is only
/// <c>as</c>, what the user has actually typed. Deriving one from the other is the bug
/// <see cref="CompletionContext"/> documents at length; both are computed independently here.
/// </para>
/// <para>
/// <b>Synchronous over an asynchronous file system.</b> <see cref="ICompletionProvider"/> is synchronous by
/// design (it sits on the keystroke path). Enumeration is not. The reconciliation is the one the control
/// documents: answer from <see cref="FileDialogViewModel.TryGetCachedListing"/>, and on a miss ask
/// <see cref="FileDialogViewModel.PrimeListing"/> to fill it and re-query — the popup re-runs from scratch, so
/// a late listing is always scored against the CURRENT text and there is no stale-answer window to guard.
/// </para>
/// </summary>
internal sealed class FileDialogPathCompletionProvider : ICompletionProvider
{
    private readonly FileDialogViewModel _model;
    private readonly Action _requestRefresh;

    // The last text this provider was queried with, so an empty final segment can tell "the user just typed the
    // separator" from "the field arrived already ending in one". See the empty-segment rule in GetCompletions.
    private string? _lastText;

    /// <summary>Creates the provider.</summary>
    /// <param name="model">The dialog's view-model — the base directory, the provider, and the listing cache.</param>
    /// <param name="requestRefresh">
    /// Invoked once a directory the provider had to enumerate lands in the cache; the host wires this to
    /// <see cref="CompletionPopup.Refresh"/>.
    /// </param>
    internal FileDialogPathCompletionProvider(FileDialogViewModel model, Action requestRefresh)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(requestRefresh);

        _model = model;
        _requestRefresh = requestRefresh;
    }

    /// <inheritdoc/>
    public CompletionContext? GetCompletions(in CompletionQuery query)
    {
        var text = query.Text;
        var caret = Math.Clamp(query.CaretIndex, 0, text.Length);

        // Snapshot-and-advance FIRST, before any early return: several exits below (an unresolvable prefix, a
        // directory not yet enumerated) answer null while the user is still mid-path, and leaving the tracker
        // behind on those turns would make the next keystroke look like an edit-from-nowhere.
        //
        // Advance on TextChanged ONLY. One keystroke produces TWO queries — CaretMoved first, then
        // TextChanged, both carrying the ALREADY-updated text — so advancing on both would let the CaretMoved
        // turn consume the evidence and leave TextChanged comparing the new text against itself. That is
        // precisely how the typed-separator test below silently never fires.
        var previousText = _lastText;
        if (query.Trigger is CompletionTrigger.TextChanged)
            _lastText = text;

        var replaceStart = LastSeparator(text, caret) + 1;
        var replaceEnd = NextSeparator(text, caret);
        var pattern = text[replaceStart..caret];

        // The prefix INCLUDING its trailing separator is the directory being listed; empty means "no directory
        // typed yet", which is the current one.
        var prefix = text[..replaceStart];
        var directory = prefix.Length == 0
            ? _model.CurrentDirectory
            : _model.FileSystem.ResolvePath(prefix, _model.CurrentDirectory);

        if (directory is not { Length: > 0 } || !_model.FileSystem.DirectoryExists(directory))
            return null; // nothing to complete against — the session closes

        // ── an EMPTY segment does not open the popup by itself — unless the user JUST TYPED the separator ──
        // Entering edit mode seeds the field with the current path plus its trailing separator, which is an
        // empty final segment — and without this rule that alone would drop the whole directory listing over
        // the dialog before the user has typed a thing, adding a phantom rung to the Escape ladder (design
        // page S2 → S3: the popup opens on ↓, Ctrl+Space, or Tab, never on arrival). An EXPLICIT request
        // still opens it, and so does CONTINUED — that one IS the folder drill, where showing the level you
        // just stepped into is the entire point.
        //
        // But "arrived ending in a separator" and "the user just typed one" produce an identical query, and
        // suppressing both broke the second: a user who finishes a segment by hand instead of accepting it
        // from the list, then types '/', got the session CLOSED — while accepting that same folder from the
        // list drills in and keeps completing (KeepOpen ⇒ Continued). Two routes to the same state, diverging.
        // Typing the separator IS the drill gesture, so it is treated as one.
        var typedSeparator = query.Trigger is CompletionTrigger.TextChanged
                             && caret > 0
                             && IsSeparator(text[caret - 1])
                             && IsSingleInsertionAt(previousText, text, caret);

        if (pattern.Length == 0 && !typedSeparator &&
            query.Trigger is CompletionTrigger.TextChanged or CompletionTrigger.CaretMoved)
        {
            return null;
        }

        if (_model.TryGetCachedListing(directory) is not { } listing)
        {
            _model.PrimeListing(directory, _requestRefresh);
            return null;
        }

        var separator = SeparatorFor(prefix);
        var items = new List<CompletionItem>(listing.Count);

        foreach (var entry in listing)
        {
            // Hidden entries are offered only once the user asks for them — by turning them on in the
            // listing, or by typing the leading dot, which is the shell convention and the only gesture that
            // unambiguously means "yes, I want the dotfiles".
            if (entry.IsHidden && !_model.ShowHiddenEntries && !pattern.StartsWith('.'))
                continue;

            items.Add(new CompletionItem(entry.Name)
                      {
                          // A folder inserts its separator too, so the very next keystroke is already
                          // completing the NEXT segment — that is what makes Enter-Enter a natural drill.
                          InsertText = entry.IsDirectory ? entry.Name + separator : entry.Name,
                          Icon = entry.Type.ToIconCarrier(),
                          KindLabel = entry.IsDirectory ? "folder" : "file",
                          ContinuesSession = entry.IsDirectory,
                          SortGroup = entry.IsDirectory ? 0 : 1, // folders can never be out-scored by a file
                          Data = entry
                      });
        }

        return new CompletionContext(replaceStart, replaceEnd, pattern, items);
    }

    /// <summary>
    /// The commit hook wired to <see cref="CompletionPopup.CommitHandler"/> — the gesture policy the design
    /// page specifies:
    /// <list type="bullet">
    /// <item><b>Tab</b> always accepts and KEEPS editing, folder or file. It is the "complete as far as this,
    /// keep going" gesture; ending the session on it would make Tab-to-complete-a-filename close the path bar.</item>
    /// <item><b>Enter on a folder</b> inserts it plus a separator and keeps editing — you are mid-path, so
    /// ending the flow would be wrong. (A second Enter on the now-complete folder path commits and navigates:
    /// the natural "drill, then go" is Enter Enter.)</item>
    /// <item><b>Enter on a file</b> commits — there is nothing left to drill into.</item>
    /// </list>
    /// </summary>
    /// <param name="accept">The accepted candidate, the field's text, the owned span and the gesture.</param>
    internal CompletionCommit Commit(CompletionAccept accept)
    {
        var commit = CompletionCommit.Splice(accept);

        return accept.Reason == CompletionAcceptReason.Tab
            ? commit with { KeepOpen = true }
            : commit; // Splice already honours ContinuesSession: true for a folder, false for a file
    }

    // ───────────────────────────── path lexing ─────────────────────────────
    //
    // Both separators are accepted regardless of the provider's own, because a user on Windows types '/' all
    // day and a path bar that stops completing the moment they do is worse than useless.

    private static bool IsSeparator(char c) => c is '/' or '\\';

    /// <summary>
    /// Whether <paramref name="text"/> is <paramref name="previous"/> with exactly ONE character inserted
    /// ending at <paramref name="caret"/> — i.e. the user typed a single key, rather than the field being
    /// seeded, pasted into, or edited elsewhere.
    /// </summary>
    /// <remarks>
    /// A paste of a whole path that happens to end in a separator grows the text by more than one character
    /// and so does NOT open the popup, which matches the arrival rule: a path the user did not type their way
    /// into should not drop a listing over the dialog unasked.
    /// </remarks>
    private static bool IsSingleInsertionAt(string? previous, string text, int caret)
    {
        if (previous is null || text.Length != previous.Length + 1 || caret < 1)
            return false;

        // Everything before the inserted character, and everything after it, must be untouched.
        return text.AsSpan(0, caret - 1).SequenceEqual(previous.AsSpan(0, caret - 1))
            && text.AsSpan(caret).SequenceEqual(previous.AsSpan(caret - 1));
    }

    private static int LastSeparator(string text, int caret)
    {
        for (var i = caret - 1; i >= 0; i--)
        {
            if (IsSeparator(text[i]))
                return i;
        }

        return -1;
    }

    private static int NextSeparator(string text, int caret)
    {
        for (var i = caret; i < text.Length; i++)
        {
            if (IsSeparator(text[i]))
                return i;
        }

        return text.Length;
    }

    // Mirror the separator the user is already typing (the character just before the segment), so completing
    // "C:/Users/" keeps producing '/' rather than silently switching the path to backslashes half way through.
    private char SeparatorFor(string prefix)
        => prefix.Length > 0 && IsSeparator(prefix[^1]) ? prefix[^1] : _model.FileSystem.DirectorySeparator;
}
