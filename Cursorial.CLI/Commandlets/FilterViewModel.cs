using System.Windows.Input;

using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// `curio filter` — fuzzy-pick one item: type to narrow (a <see cref="FuzzyMatcher"/> subsequence
/// match), Up/Down move the match list while typing continues in the query box (root key bindings —
/// focus never leaves the editor), Enter accepts. The result carries the item's ORIGINAL position
/// in <see cref="Items"/>, not its position in the narrowed <see cref="Matches"/>.
/// </summary>
public sealed class FilterViewModel : CommandletViewModel
{
    public FilterViewModel(UIApplication app, string prompt, IReadOnlyList<string> items) : base(app)
    {
        Prompt = prompt;
        Items = items; // the init accessor seeds Matches + Selected
        AcceptCommand = new DelegateCommand(() =>
        {
            if (Selected is null && Matches.Count == 1)
                Selected = Matches[0]; // a sole match accepts without an explicit selection
            if (Selected is not null)
                Accept();
        });
        MoveDownCommand = new DelegateCommand(() => MoveSelection(+1));
        MoveUpCommand = new DelegateCommand(() => MoveSelection(-1));
    }

    public string Prompt { get; init => SetProperty(ref field, value); }

    /// <summary>All items (argv positionals or the stdin feed); re-seeds the match list when set.</summary>
    public IReadOnlyList<string> Items
    {
        get;
        init
        {
            SetProperty(ref field, value);
            RecomputeMatches();
        }
    }

    /// <summary>The fuzzy query; every keystroke recomputes <see cref="Matches"/>.</summary>
    public string Query
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                RecomputeMatches();
        }
    } = "";

    /// <summary>The narrowed, ranked match list (never null).</summary>
    public IReadOnlyList<string> Matches { get; private set => SetProperty(ref field, value); } = [];

    public string? Selected { get; set => SetProperty(ref field, value); }

    public ICommand AcceptCommand { get; init => SetProperty(ref field, value); }

    public ICommand MoveDownCommand { get; init => SetProperty(ref field, value); }

    public ICommand MoveUpCommand { get; init => SetProperty(ref field, value); }

    public override Variable BuildResult(string name)
    {
        var index = 0;
        for (; index < Items.Count && !string.Equals(Items[index], Selected, StringComparison.Ordinal); index++) { }
        return new Variable(name, VariableKind.Selection, [Selected ?? ""], [index]);
    }

    private void RecomputeMatches()
    {
        // Capture BEFORE the Matches push: the list's items-source reset may write Selected through
        // the two-way binding while the property change propagates.
        var previous = Selected;

        Matches = FuzzyMatcher.Filter(Items ?? [], Query);

        // Selection follows the narrowing: keep it while it still matches, else snap to the best match.
        var keep = false;
        foreach (var match in Matches)
        {
            if (string.Equals(match, previous, StringComparison.Ordinal))
            {
                keep = true;
                break;
            }
        }

        Selected = keep ? previous : Matches.Count > 0 ? Matches[0] : null;
    }

    private void MoveSelection(int delta)
    {
        if (Matches.Count == 0)
            return;

        var index = 0;
        for (; index < Matches.Count && !string.Equals(Matches[index], Selected, StringComparison.Ordinal); index++) { }
        Selected = Matches[index >= Matches.Count ? 0 : Math.Clamp(index + delta, 0, Matches.Count - 1)];
    }
}
