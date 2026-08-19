using Cursorial.CLI.Wire;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// `curio filter` — fuzzy-pick one item through a <see cref="CompletionPopup"/> attached to the
/// query box: the popup opens over the FULL list before any typing (the list is the prompt), each
/// keystroke narrows and re-ranks it (the framework matcher owns both, plus the match-cell
/// highlighting), Up/Down move the popup highlight, Enter accepts it. The result carries the item's
/// ORIGINAL position in <see cref="Items"/> — it rides each candidate's
/// <see cref="CompletionItem.Data"/>, so it survives any amount of narrowing and re-ranking.
/// </summary>
public sealed class FilterViewModel : CommandletViewModel
{
    private readonly IReadOnlyList<CompletionItem> _candidates = [];

    public FilterViewModel(UIApplication app, string? prompt, IReadOnlyList<string> items, string? placeholder = null)
        : base(app)
    {
        Prompt = prompt;
        Placeholder = placeholder;
        Items = items;
    }

    public string? Prompt { get; init => SetProperty(ref field, value); }

    public string? Placeholder { get; init => SetProperty(ref field, value); }

    /// <summary>All items (argv positionals or the stdin feed); re-seeds the candidate list when set.</summary>
    public IReadOnlyList<string> Items
    {
        get;
        init
        {
            SetProperty(ref field, value);

            var candidates = new CompletionItem[value.Count];
            for (var i = 0; i < candidates.Length; i++)
                candidates[i] = new CompletionItem(value[i]) { Data = i };
            _candidates = candidates;
        }
    }

    /// <summary>The query box text; the accept splice writes the chosen item back through it — the receipt.</summary>
    public string Query { get; set => SetProperty(ref field, value); } = "";

    /// <summary>
    /// Whole-text completion over <see cref="Items"/>: the entire field is both the pattern and the
    /// replace span, so the popup narrows as the user types and an accept swaps the query for the
    /// chosen item. Lazy rather than ctor-built so a designer-materialized instance (which skips
    /// the ctor) still provides.
    /// </summary>
    public ICompletionProvider Provider =>
        field ??= new DelegateCompletionProvider(query =>
            new CompletionContext(0, query.Text.Length, query.Text, _candidates));

    public string? Selected { get; private set => SetProperty(ref field, value); }

    /// <summary>The accepted item's original index into <see cref="Items"/>; -1 until accepted.</summary>
    public int SelectedIndex { get; private set => SetProperty(ref field, value); } = -1;

    /// <summary>The view's <see cref="CompletionPopup.Committed"/> hook: record the pick, complete accepted.</summary>
    public void AcceptItem(CompletionItem item)
    {
        Selected = item.Display;
        SelectedIndex = item.Data is int index ? index : -1;
        Accept();
    }

    public override Variable BuildResult(string name)
        => new(name, VariableKind.Selection, [Selected ?? ""], [Math.Max(0, SelectedIndex)]);
}
