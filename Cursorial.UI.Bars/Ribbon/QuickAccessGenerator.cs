using System.Collections.ObjectModel;
using System.Collections.Specialized;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// Maps a <see cref="Ribbon"/>'s Quick-Access membership (an ordered set of <see cref="BarCommand"/>s) 1:1 onto the
/// active QAT <see cref="Toolbar"/>'s items: a checkable command becomes a <see cref="BarToggleButton"/>, else a
/// <see cref="BarButton"/>, each with <see cref="ButtonBase.Command"/> set so <c>BarCommandSync</c> auto-fills its
/// Content/Icon/InputGestureText and the CanExecute gate flows — define-once, bind-everywhere. The generator OWNS the
/// controls it creates (they are not the app's — unlike a ribbon group's user-authored controls), so re-hosting onto a
/// different toolbar (the above/below placement flip) simply rebuilds them in the new host.
/// </summary>
internal sealed class QuickAccessGenerator
{
    private readonly ObservableCollection<BarCommand> _commands;
    private readonly Dictionary<BarCommand, UIElement> _controls = new();
    private Toolbar? _host;

    public QuickAccessGenerator(ObservableCollection<BarCommand> commands)
    {
        _commands = commands;
        _commands.CollectionChanged += OnCommandsChanged;
    }

    /// <summary>Points the generator at the currently-active QAT toolbar (null while no part is realized, or the old
    /// host on a placement flip). Clears the old host and refills the new from current membership.</summary>
    public void SetHost(Toolbar? host)
    {
        if (ReferenceEquals(_host, host))
            return;

        _host?.Items.Clear();
        _controls.Clear();
        _host = host;
        if (_host is null)
            return;

        foreach (var command in _commands)
            _host.Items.Add(GetOrCreate(command));
    }

    private void OnCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_host is null)
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (var i = 0; i < e.NewItems.Count; i++)
                    _host.Items.Insert(e.NewStartingIndex + i, GetOrCreate((BarCommand) e.NewItems[i]!));
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (BarCommand command in e.OldItems)
                    Remove(command);
                break;

            default:
                Rebuild(); // Replace/Move/Reset — rare; a full rebuild keeps order authoritative
                break;
        }
    }

    private void Remove(BarCommand command)
    {
        if (_controls.Remove(command, out var control))
            _host!.Items.Remove(control);
    }

    private void Rebuild()
    {
        _host!.Items.Clear();
        _controls.Clear();
        foreach (var command in _commands)
            _host.Items.Add(GetOrCreate(command));
    }

    private UIElement GetOrCreate(BarCommand command)
    {
        if (_controls.TryGetValue(command, out var existing))
            return existing;

        UIElement control = command.IsCheckable
            ? new BarToggleButton { Command = command }
            : new BarButton { Command = command };
        _controls[command] = control;
        return control;
    }
}
