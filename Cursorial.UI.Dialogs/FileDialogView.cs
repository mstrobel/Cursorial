using System;
using System.ComponentModel;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The chrome both file dialogs wear, built once and shared: the design page's
/// <c>◂ ▸ ▴ ↻ | breadcrumb | search | ▤ ▥ ▦</c> toolbar, the grouped places rail, the sortable listing, and
/// the <c>File name</c> + type-filter footer, wired to a <see cref="FileDialogViewModel"/>. Save As merely
/// re-tasks it — a New Folder button appears, the <i>Type</i> column goes away (the Save mock lists Name /
/// Size / Modified only), and the accept button says Save.
/// <para>
/// <b>Why one builder for two dialogs instead of a base class.</b> The dialogs differ in their public entry
/// point, their request record and their result semantics — none of which is chrome. A shared <c>Window</c>
/// base would put those differences behind virtuals and leave the interesting part (what Accept MEANS) spread
/// over two files; a shared builder keeps every visual decision in one place and leaves each dialog as the
/// twenty lines that are genuinely its own.
/// </para>
/// <para>
/// <b>Every region is one tab stop</b> (<see cref="KeyboardNavigationMode.Once"/> + arrow navigation inside —
/// the toolbar rule, ND16), in the design page's order: nav buttons → breadcrumb → search → view switcher →
/// places rail → file list → File name → type filter → Accept → Cancel. Crossing the dialog costs nine Tabs
/// regardless of how deep the path or how long the listing is.
/// </para>
/// </summary>
internal sealed class FileDialogView
{
    private readonly FileDialogViewModel _model;
    private readonly FileDialogPathBar _pathBar = new() { IsEditable = true };
    private readonly CompletionPopup _completion = new();
    private readonly FileDialogPathCompletionProvider _completionProvider;
    private readonly ListView _listView = new();
    private readonly TextBox _fileNameBox = new();
    private readonly TextBox _searchBox = new();
    private readonly TextBox _newFolderBox = new();
    private readonly Border _newFolderRow = new();
    private readonly StackPanel _placesPanel = new();
    private readonly TextBlock _errorLine = new();
    private readonly TextBlock _summaryLine = new();
    private readonly ListViewColumn _nameColumn = new() { Header = "Name", Width = GridLength.Star(), SortMemberPath = "Name" };
    private readonly ListViewColumn _sizeColumn;
    private readonly ListViewColumn? _typeColumn;
    private readonly ListViewColumn _modifiedColumn;

    private bool _syncingSelection;
    private TextBox? _completionHost; // the edit box the completion popup is currently attached to

    /// <summary>Builds the chrome for <paramref name="model"/>.</summary>
    /// <param name="model">The view-model every control binds to.</param>
    internal FileDialogView(FileDialogViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _completionProvider = new FileDialogPathCompletionProvider(model, RequestCompletionRefresh);

        _sizeColumn = new ListViewColumn
                      {
                          Header = "Size",
                          Width = GridLength.Auto,
                          DisplayMemberPath = nameof(FileDialogEntry.SizeText),
                          HorizontalAlignment = HorizontalAlignment.Right,
                          SortMemberPath = "Size"
                      };

        _typeColumn = model.IsSaveDialog
            ? null
            : new ListViewColumn
              {
                  Header = "Type",
                  Width = GridLength.Auto,
                  DisplayMemberPath = nameof(FileDialogEntry.TypeText),
                  SortMemberPath = "Type"
              };

        _modifiedColumn = new ListViewColumn
                          {
                              Header = "Modified",
                              Width = GridLength.Auto,
                              DisplayMemberPath = nameof(FileDialogEntry.ModifiedText),
                              SortMemberPath = "Modified"
                          };

        Root = Build();
        model.PropertyChanged += OnModelPropertyChanged;
        RebuildPlacesRail();
        SyncSortIndicator();
    }

    /// <summary>The chrome's root element — what the dialog assigns to <see cref="ContentControl.Content"/>.</summary>
    internal UIElement Root { get; }

    /// <summary>Where focus lands when the dialog is shown: the listing, which is where a user starts.</summary>
    internal UIElement InitialFocusTarget => _listView;

    /// <summary>The accept button — the dialog marks it default so Enter reaches it from anywhere.</summary>
    internal Button AcceptButton { get; private set; } = null!;

    /// <summary>The cancel button — the dialog marks it <see cref="Button.IsCancel"/>, which is the LAST rung
    /// of the Escape ladder (see <see cref="HandleKey"/>).</summary>
    internal Button CancelButton { get; private set; } = null!;

    // ───────────────────────────── composition ─────────────────────────────

    private UIElement Build()
    {
        var root = new DockPanel { LastChildFill = true, Margin = new Margins(1, 0) };
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(BuildBody());
        return root;
    }

    private UIElement BuildToolbar()
    {
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Margins(0, 0, 0, 1) };

        // ── nav buttons (+ New folder on Save): one region, arrows inside ──────────────────────────
        var nav = Region(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 }, tabIndex: 0);
        nav.Children.Add(GlyphButton("◂", _model.BackCommand, "Back"));
        nav.Children.Add(GlyphButton("▸", _model.ForwardCommand, "Forward"));
        nav.Children.Add(GlyphButton("▴", _model.UpCommand, "Up"));
        nav.Children.Add(GlyphButton("↻", _model.RefreshCommand, "Refresh"));

        if (_model.CanCreateDirectories)
        {
            var newFolder = new Button { Content = "▤＋ _New folder", Command = _model.NewFolderCommand, Margin = new Margins(1, 0, 0, 0) };
            nav.Children.Add(newFolder);
        }

        DockPanel.SetDock(nav, Dock.Left);
        toolbar.Children.Add(nav);

        // ── view switcher (right-most), then the search box left of it ─────────────────────────────
        var views = Region(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 }, tabIndex: 3);
        views.Children.Add(ViewButton("▤", ListViewViewMode.Details));
        views.Children.Add(ViewButton("▥", ListViewViewMode.SmallIcons));
        views.Children.Add(ViewButton("▦", ListViewViewMode.Tiles));
        DockPanel.SetDock(views, Dock.Right);
        toolbar.Children.Add(views);

        _searchBox.Placeholder = "⌕ Search";
        _searchBox.Width = 16;
        _searchBox.TabIndex = 2;
        _searchBox.Margin = new Margins(1, 0);
        _searchBox.SetBinding(TextBox.TextProperty,
                              new Binding(nameof(FileDialogViewModel.SearchText)) { Source = _model, Mode = BindingMode.TwoWay });
        DockPanel.SetDock(_searchBox, Dock.Right);
        toolbar.Children.Add(_searchBox);

        // ── the breadcrumb + its completion overlay ────────────────────────────────────────────────
        //
        // The popup is a 0×0, non-hit-testable placeholder for a Popup surface, so parking it in the same cell
        // as the bar costs nothing and keeps it alive exactly as long as the field it decorates.
        _pathBar.ItemsSource = _model.Segments;
        _pathBar.TabIndex = 1;
        _pathBar.ItemActivated += (_, e) => _model.NavigateCommand.Execute(e.Item);
        _pathBar.EditingStarted += OnPathEditingStarted;
        _pathBar.EditCommitted += OnPathEditCommitted;
        _pathBar.DropDownOpening += OnPathDropDownOpening;
        _pathBar.ChildActivated += (_, e) => _model.NavigateCommand.Execute(e.SelectedChild);
        _pathBar.EditCanceled += (_, _) => DetachCompletion();
        _pathBar.TemplateApplied += (_, _) => DetachCompletion();

        _completion.CommitHandler = _completionProvider.Commit;
        _completion.Provider = _completionProvider;
        _completion.FooterText = "Tab complete   ^v select   Enter open   Esc cancel";
        _completion.Committed += OnCompletionCommitted;

        var pathCell = new Grid();
        pathCell.Children.Add(_pathBar);
        pathCell.Children.Add(_completion);
        toolbar.Children.Add(pathCell);

        return toolbar;
    }

    private UIElement BuildBody()
    {
        var body = new DockPanel { LastChildFill = true };

        // ── places rail ────────────────────────────────────────────────────────────────────────────
        _placesPanel.Orientation = Orientation.Vertical;
        Region(_placesPanel, tabIndex: 4);

        var rail = new Border
                   {
                       Child = new ScrollViewer { Content = _placesPanel },
                       Width = 18,
                       Padding = new Margins(0, 0, 1, 0)
                   };

        DockPanel.SetDock(rail, Dock.Left);
        body.Children.Add(rail);

        // ── listing (with the New folder editor docked above it) ───────────────────────────────────
        var listArea = new DockPanel { LastChildFill = true };

        _newFolderBox.SetBinding(TextBox.TextProperty,
                                 new Binding(nameof(FileDialogViewModel.NewFolderName)) { Source = _model, Mode = BindingMode.TwoWay });
        _newFolderBox.KeyDown += OnNewFolderKeyDown;

        var newFolderContent = new DockPanel { LastChildFill = true };
        var newFolderGlyph = new TextBlock { Text = "▸ ", Margin = new Margins(0, 0, 1, 0) };
        DockPanel.SetDock(newFolderGlyph, Dock.Left);
        newFolderContent.Children.Add(newFolderGlyph);
        newFolderContent.Children.Add(_newFolderBox);

        _newFolderRow.Child = newFolderContent;
        _newFolderRow.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_newFolderRow, Dock.Top);
        listArea.Children.Add(_newFolderRow);

        _listView.ItemsSource = _model.Entries;
        _listView.SelectionMode = SelectionMode.Single;
        _listView.TabIndex = 5;
        _listView.View = _model.ViewMode;

        // A ListView is a container, not a control — its items host is the tab stop and its ROWS take focus,
        // so it is unfocusable by default. The dialog needs a focus floor for the empty-directory case (see
        // FocusFileList), and IsTabStop stays false, so this adds no stop to the tab order.
        _listView.Focusable = true;

        // The view-model owns type-ahead, because Backspace has to be able to EDIT the buffer (the design
        // page's Backspace-navigates-up and its type-ahead otherwise fight over the same key). The control's
        // own TextSearch keeps its prefix private and has no Backspace rung, so it is switched off outright
        // rather than left to race ours.
        _listView.IsTextSearchEnabled = false;

        _listView.DisplayMemberPath = nameof(FileDialogEntry.Name);
        _listView.IconMemberPath = nameof(FileDialogEntry.Icon);
        _listView.SecondaryMemberPath = nameof(FileDialogEntry.TypeText);

        _nameColumn.CellTemplate = NameCellTemplate();
        _listView.Columns.Add(_nameColumn);
        _listView.Columns.Add(_sizeColumn);

        if (_typeColumn is not null)
            _listView.Columns.Add(_typeColumn);

        _listView.Columns.Add(_modifiedColumn);

        _listView.Sorting += OnListSorting;
        _listView.SelectionChanged += OnListSelectionChanged;
        _listView.ItemInvoked += (_, e) => _model.ActivateCommand.Execute(e.Item);
        _listView.TextInput += OnListTextInput;
        _listView.LostFocus += (_, _) =>
        {
            if (!_listView.IsKeyboardFocusWithin)
                _model.ResetTypeAhead();
        };

        listArea.Children.Add(_listView);
        body.Children.Add(listArea);
        return body;
    }

    private UIElement BuildFooter()
    {
        var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Margins(0, 1, 0, 0) };

        _errorLine.Visibility = Visibility.Collapsed;
        _errorLine.TextWrapping = WrapMode.WordWrap;
        _errorLine.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.DangerBrush);
        footer.Children.Add(_errorLine);

        // ── File name + type filter ────────────────────────────────────────────────────────────────
        var nameRow = new DockPanel { LastChildFill = true };

        var nameLabel = new TextBlock { Text = "File name: ", Margin = new Margins(0, 0, 1, 0) };
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);

        var filterBox = new ComboBox { ItemsSource = _model.Filters, Width = 30, TabIndex = 7 };
        filterBox.SetBinding(SelectingItemsControl.SelectedItemProperty,
                             new Binding(nameof(FileDialogViewModel.SelectedFilter)) { Source = _model, Mode = BindingMode.TwoWay });
        DockPanel.SetDock(filterBox, Dock.Right);
        nameRow.Children.Add(filterBox);

        var filterLabel = new TextBlock
                          {
                              Text = _model.IsSaveDialog ? " Save as type: " : " Files of type: ",
                              Margin = new Margins(1, 0, 1, 0)
                          };

        DockPanel.SetDock(filterLabel, Dock.Right);
        nameRow.Children.Add(filterLabel);

        _fileNameBox.TabIndex = 6;
        _fileNameBox.SetBinding(TextBox.TextProperty,
                                new Binding(nameof(FileDialogViewModel.FileName)) { Source = _model, Mode = BindingMode.TwoWay });
        nameRow.Children.Add(_fileNameBox);
        footer.Children.Add(nameRow);

        // ── selection summary + the two buttons ────────────────────────────────────────────────────
        var buttonRow = new DockPanel { LastChildFill = true, Margin = new Margins(0, 1, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 1 };

        AcceptButton = new Button
                       {
                           Content = _model.IsSaveDialog ? "_Save" : "_Open",
                           Command = _model.AcceptCommand,
                           IsDefault = true,
                           TabIndex = 8
                       };

        CancelButton = new Button
                       {
                           Content = "_Cancel",
                           Command = _model.CancelCommand,
                           IsCancel = true,
                           TabIndex = 9
                       };

        buttons.Children.Add(AcceptButton);
        buttons.Children.Add(CancelButton);
        DockPanel.SetDock(buttons, Dock.Right);
        buttonRow.Children.Add(buttons);

        _summaryLine.Text = _model.SelectionSummary;
        _summaryLine.Opacity = 0.6;
        buttonRow.Children.Add(_summaryLine);

        footer.Children.Add(buttonRow);
        return footer;
    }

    // A cell that is icon + name, because the Name column is the one place the file-type glyph belongs. The
    // icon rides a ContentPresenter over the IconCarrier rather than a hand-built Icon, so it picks up the
    // implicit IconCarrier data template — and with it the framework's whole capability ladder (Nerd Font →
    // emoji → Unicode floor), live-re-resolving when the user toggles a capability.
    private static DataTemplate NameCellTemplate()
        => new()
           {
               DataType = typeof(FileDialogEntry),
               Content = new FuncTemplateContent(_ =>
                         {
                             var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };

                             var icon = new ContentPresenter();
                             icon.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(FileDialogEntry.Icon)));

                             var text = new TextBlock();
                             text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileDialogEntry.Name)));

                             row.Children.Add(icon);
                             row.Children.Add(text);
                             return row;
                         })
           };

    private static TPanel Region<TPanel>(TPanel panel, int tabIndex) where TPanel : Panel
    {
        // ND16: one Tab stop for the whole region, arrows inside. The same rule the toolbar, the breadcrumb
        // and the items host all use — see the design page's "why the breadcrumb is one tab stop".
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(panel, DirectionalNavigationMode.Contained);
        panel.TabIndex = tabIndex;
        return panel;
    }

    private static Button GlyphButton(string glyph, System.Windows.Input.ICommand command, string name)
        => new() { Content = glyph, Command = command, Width = 3, Name = name };

    private Button ViewButton(string glyph, ListViewViewMode mode)
        => new()
           {
               Content = glyph,
               Command = _model.SetViewModeCommand,
               CommandParameter = mode,
               Width = 3
           };

    // ───────────────────────────── the places rail ─────────────────────────────

    private void RebuildPlacesRail()
    {
        _placesPanel.Children.Clear();

        foreach (var group in _model.PlaceGroups)
        {
            var header = new TextBlock { Text = group.Header, Opacity = 0.6, Margin = new Margins(0, 1, 0, 0) };
            _placesPanel.Children.Add(header); // unfocusable, so arrowing the rail skips straight over it

            foreach (var place in group.Places)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
                row.Children.Add(place.Type.CreateIcon());
                row.Children.Add(new TextBlock { Text = place.Name });

                _placesPanel.Children.Add(new Button
                                          {
                                              Content = row,
                                              Command = _model.NavigateCommand,
                                              CommandParameter = place.FullPath,
                                              HorizontalAlignment = HorizontalAlignment.Stretch
                                          });
            }
        }
    }

    // ───────────────────────────── model → view ─────────────────────────────

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileDialogViewModel.PlaceGroups):
                RebuildPlacesRail();
                break;

            case nameof(FileDialogViewModel.SelectedEntry):
                SyncSelectionToList();
                break;

            case nameof(FileDialogViewModel.ViewMode):
                _listView.View = _model.ViewMode;
                break;

            case nameof(FileDialogViewModel.SortKey):
            case nameof(FileDialogViewModel.SortDirection):
                SyncSortIndicator();
                break;

            case nameof(FileDialogViewModel.ErrorMessage):
                _errorLine.Text = _model.ErrorMessage ?? "";
                _errorLine.Visibility = _model.ErrorMessage is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
                break;

            case nameof(FileDialogViewModel.SelectionSummary):
                _summaryLine.Text = _model.SelectionSummary;
                break;

            case nameof(FileDialogViewModel.IsCreatingFolder):
                SyncNewFolderRow();
                break;
        }
    }

    private void SyncSelectionToList()
    {
        if (_syncingSelection)
            return;

        _syncingSelection = true;

        try
        {
            _listView.SelectedItem = _model.SelectedEntry;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;

        _syncingSelection = true;

        try
        {
            _model.SelectedEntry = _listView.SelectedItem as FileDialogEntry;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    // The view-model is the single source of truth for the order, so the header click is CANCELLED at the
    // control (args.Cancel — "abandon the sort entirely, move no indicator") and the indicator is then written
    // back from the model. Letting the ListView cycle its own direction as well would give two state machines
    // one keystroke apart, and they would drift the first time the model's order was restored from a setting.
    private void OnListSorting(object? sender, ListViewSortingEventArgs e)
    {
        e.Cancel = true;
        _model.SortCommand.Execute(e.SortMemberPath);
    }

    private void SyncSortIndicator()
    {
        _listView.SortColumn = _model.SortKey switch
        {
            FileDialogSortKey.Size => _sizeColumn,
            FileDialogSortKey.Type => _typeColumn ?? _nameColumn,
            FileDialogSortKey.Modified => _modifiedColumn,
            _ => _nameColumn
        };

        _listView.SortDirection = _model.SortDirection;
    }

    private void SyncNewFolderRow()
    {
        _newFolderRow.Visibility = _model.IsCreatingFolder ? Visibility.Visible : Visibility.Collapsed;

        if (_model.IsCreatingFolder)
        {
            _newFolderBox.Focus(FocusNavigationMethod.Programmatic);
            _newFolderBox.SelectAll();
            return;
        }

        // Deferred by one dispatcher turn: the editor closes from INSIDE the refresh that follows a successful
        // create, so focusing the listing inline would land on a container the rebuild is about to destroy —
        // and would miss the new folder's row, which is selected only once that rebuild has run.
        if (_newFolderBox.IsKeyboardFocusWithin)
            UIApplication.Current?.Dispatcher.Post(FocusFileList);
    }

    private void OnNewFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                _model.CommitNewFolderCommand.Execute(null);
                break;
            case Key.Escape:
                _model.CancelNewFolderCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ───────────────────────────── the path bar ─────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The refresh is POSTED, never called inline. The provider asks for one from inside GetCompletions, and
    // with a synchronous file system (the in-memory provider, a warm cache) the enumeration it is waiting on
    // completes immediately — so an inline Refresh would re-enter CompletionPopup.Update from within its own
    // provider call. The inner pass would open the popup with the right candidates and then the OUTER pass,
    // still holding its own null answer, would close it again: a completion list that flickers open and shut
    // exactly when the cache was cold. One dispatcher turn separates the two passes for good.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void RequestCompletionRefresh()
    {
        if (UIApplication.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(() => _completion.Refresh());
    }

    private void OnPathEditingStarted(object? sender, BreadcrumbBarEditEventArgs e)
    {
        // Seeded WITH the trailing separator so the very first keystroke is already completing a new segment
        // (the design page's "~/Projects/assets/te" state), and pre-selected by the bar so typing replaces.
        // Home collapses to "~" for the same reason the chips show a place name rather than "/home/ada": the
        // path is there to be read and retyped, and nobody types their home directory out in full. It round-
        // trips for free — both providers' ResolvePath expand a leading "~", so a committed or completed path
        // needs no un-collapsing step.
        var separator = _model.FileSystem.DirectorySeparator;
        var path = _model.ToDisplayPath(_model.CurrentDirectory);
        e.Text = path.Length > 0 && path[^1] != separator ? path + separator : path;

        AttachCompletion();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The completion session's lifetime is EXACTLY the path-edit session's — attached when the bar enters
    // edit mode, released when it leaves. It is tempting to attach once and leave it, but the bar writes its
    // committed Text back into the (now hidden) edit box on the way out of edit mode, and that write raises
    // TextChanged: a still-attached popup would answer it by opening a candidate list over a collapsed field.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void AttachCompletion()
    {
        if (_pathBar.EditBox is not { } editBox || ReferenceEquals(editBox, _completionHost))
            return;

        DetachCompletion(); // a template swap hands out a NEW box; release the old one first

        _completionHost = editBox;
        _completion.Target = editBox;

        // Registered AFTER Target so the popup's own PreviewKeyDown handler runs first: when the popup is up
        // it accepts Tab and marks it handled, and this only ever sees the closed case.
        editBox.PreviewKeyDown += OnPathEditBoxPreviewKeyDown;
    }

    private void DetachCompletion()
    {
        if (_completionHost is not { } editBox)
            return;

        editBox.PreviewKeyDown -= OnPathEditBoxPreviewKeyDown;
        _completionHost = null;
        _completion.Target = null; // closes any live session (CompletionPopup.OnTargetChanged)
    }

    // The Tab contract, in full (design page, "resolving the Tab conflict"):
    //   • popup OPEN                    → the popup accepted it and marked it handled; we never run.
    //   • popup CLOSED, a partial typed → Refresh opens it and we claim the key: Tab completed.
    //   • popup CLOSED, nothing to complete (an empty final segment, or an exact path)
    //                                   → we leave it unhandled and Tab does the ordinary thing: LEAVE the
    //                                     region. The user is never trapped.
    // Shift+Tab is excluded by the modifier test, so it ALWAYS leaves — the unambiguous way backwards.
    //
    // ↓ is the design's ungated "open the popup explicitly" (Ctrl+Space is its [CAP] twin and needs
    // caps-kitty-keyboard to be distinguishable from NUL, so it is deliberately not the primary): it opens on
    // an empty segment too, which is how you browse a folder from inside the path field.
    private void OnPathEditBoxPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Modifiers != KeyModifiers.None || _completion.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.DownArrow:
                break;
            case Key.Tab when HasPartialSegment():
                break;
            default:
                return;
        }

        _completion.Refresh();

        if (_completion.IsOpen)
            e.Handled = true;
    }

    // Whether the caret sits after a non-empty, not-yet-complete final segment — "is there anything to
    // complete?" answered without asking the provider (which would enumerate).
    private bool HasPartialSegment()
    {
        if (_pathBar.EditBox is not { } editBox)
            return false;

        var text = editBox.Text;
        var caret = Math.Clamp(editBox.CaretIndex, 0, text.Length);

        for (var i = caret - 1; i >= 0; i--)
        {
            if (text[i] is '/' or '\\')
                return i < caret - 1;
        }

        return caret > 0;
    }

    private void OnPathEditCommitted(object? sender, BreadcrumbBarEditEventArgs e)
    {
        if (!_model.CommitPathText(e.Text))
        {
            e.KeepEditing = true; // a path that does not resolve keeps the user in the field, text intact
            return;
        }

        DetachCompletion();

        // The bar restores focus to a chip on the way out of edit mode, so the design page's "revert to chips,
        // move focus to the file list" has to land AFTER that — one dispatcher turn later.
        UIApplication.Current?.Dispatcher.Post(FocusFileList);
    }

    private void OnPathDropDownOpening(object? sender, BreadcrumbBarDropDownEventArgs e)
    {
        // Explorer's sibling list: the folders alongside the pressed segment. Cache-only on purpose — this is
        // a pointer affordance on a synchronous event, and enumerating a slow share here would stall the
        // frame the press is being handled in. An un-cached segment simply has no drop-down.
        if (e.Item is not FileDialogPathSegment segment)
            return;

        if (_model.FileSystem.GetParentPath(segment.Path) is not { } parent)
            return;

        if (_model.TryGetCachedListing(parent) is not { } listing)
        {
            _model.PrimeListing(parent);
            return;
        }

        foreach (var entry in listing)
        {
            if (entry.IsDirectory)
                e.Children.Add(new FileDialogPathSegment(entry.Name, entry.FullPath));
        }
    }

    private void OnCompletionCommitted(object? sender, CompletionCommittedEventArgs e)
    {
        // Enter on a FILE ends the completion session (KeepOpen false) — and, because the popup consumed the
        // Enter, the breadcrumb never saw it. Committing the edit here is what turns "accept the candidate"
        // into "commit the path", which is what the user pressed Enter for.
        if (!e.Commit.KeepOpen && e.Reason is CompletionAcceptReason.Enter or CompletionAcceptReason.Pointer)
            _pathBar.CommitEdit();
    }

    // ───────────────────────────── type-ahead + the dialog keymap ─────────────────────────────

    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || e.FromPaste || e.Text.Length == 0 || char.IsControl(e.Text.Span[0]))
            return;

        if (!_model.TypeAhead(e.Text.ToString()))
            return;

        FocusSelectedRow();
        e.Handled = true;
    }

    /// <summary>
    /// The dialog's ACCELERATORS, called from the window's tunnelling <c>OnPreviewKeyDown</c> — i.e. before
    /// any control on the route sees the key. Returns whether the key was handled.
    /// </summary>
    /// <param name="e">The tunnelling key event.</param>
    /// <remarks>
    /// <b>Why these tunnel.</b> <c>Alt+↑</c> is a dialog command, but <c>↑</c> is the file list's own
    /// navigation and <see cref="ListView"/> claims it modifiers and all — so an accelerator handled on the
    /// bubble would never run while the list had focus, which is exactly when the user presses it. Accelerators
    /// outrank control keys by definition, so they belong on the tunnel. Escape and Backspace deliberately do
    /// NOT: they must yield to the control that owns them first (see <see cref="HandleKey"/>).
    /// <para>
    /// <c>F2</c> is absent on purpose — <see cref="BreadcrumbBar"/> already implements it (enter edit mode
    /// when the bar is focused), and claiming it here would only pre-empt the control with the same behaviour.
    /// </para>
    /// </remarks>
    internal bool HandlePreviewKey(KeyEventArgs e)
    {
        var alt = (e.Modifiers & KeyModifiers.Alt) != 0;

        switch (e.Key)
        {
            // F4 is both the classic address-bar key and the combobox-drop key — apt, because a breadcrumb
            // with completion IS a combobox. Alt+D ("focus address") is its twin. Ctrl+L is deliberately NOT
            // bound: in a terminal that is the near-universal clear/repaint binding.
            case Key.F4:
            case Key.Character when alt && IsLetter(e, 'd'):
                return BeginPathEdit();

            // Alt+F / F3, never Ctrl+F — readline binds Ctrl+F to forward-char.
            case Key.F3:
            case Key.Character when alt && IsLetter(e, 'f'):
                _searchBox.Focus(FocusNavigationMethod.Programmatic);
                _searchBox.SelectAll();
                return true;

            // F5, never Ctrl+R — that is readline's reverse-history-search.
            case Key.F5:
                _model.RefreshCommand.Execute(null);
                return true;

            case Key.LeftArrow when alt:
                _model.BackCommand.Execute(null);
                return true;

            case Key.RightArrow when alt:
                _model.ForwardCommand.Execute(null);
                return true;

            case Key.UpArrow when alt:
                _model.UpCommand.Execute(null);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The keys that must yield to the controls first, called from the window's bubbling <c>OnKeyDown</c> —
    /// after every control on the route has had its say, so a key a text field wanted (Backspace inside the
    /// File name box, Escape inside the completion popup) has already been claimed and never reaches here.
    /// Returns whether the key was handled.
    /// </summary>
    /// <param name="e">The bubbling key event.</param>
    /// <remarks>
    /// <b>Escape is a ladder, not a cancel.</b> Each press unwinds exactly one level, and the first two rungs
    /// never reach this method at all because the control that owns them claims the key: the completion popup
    /// closes (KEEPING the typed text), then the path bar's edit mode reverts to chips (DISCARDING it), then —
    /// the rung implemented HERE — focus leaves the breadcrumb for the file list. Only at the dialog's top
    /// level does Escape fall through unhandled, where <see cref="CancelButton"/>'s
    /// <see cref="Button.IsCancel"/> binding finally cancels. A user backing out of the path bar must never
    /// destroy their dialog.
    /// </remarks>
    internal bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when e.Modifiers == KeyModifiers.None:
                return HandleEscape();

            // Backspace navigates up ONLY when no type-ahead is running; while one is, it EDITS that buffer.
            // The design page had the two collide; the view-model owns the resolution so both callers agree.
            case Key.Backspace when e.Modifiers == KeyModifiers.None && _listView.IsKeyboardFocusWithin:
                if (!_model.TypeAheadBackspace())
                {
                    _model.UpCommand.Execute(null);
                    return true;
                }

                FocusSelectedRow();
                return true;

            default:
                return false;
        }
    }

    private bool HandleEscape()
    {
        if (_model.IsCreatingFolder)
        {
            _model.CancelNewFolderCommand.Execute(null);
            return true;
        }

        if (_pathBar.IsKeyboardFocusWithin)
        {
            FocusFileList();
            return true;
        }

        return false; // top level — fall through to the Cancel button's IsCancel binding
    }

    private bool BeginPathEdit()
    {
        // BeginEdit expands the template if it has to, raises EditingStarted (where the text is seeded and the
        // completion popup attaches) and pre-selects — so F4 / Alt+D / F2 are one call, not a sequence.
        _pathBar.Focus(FocusNavigationMethod.Programmatic);
        return _pathBar.BeginEdit();
    }

    /// <summary>Moves focus to the listing — the Escape ladder's third rung and the landing spot after a
    /// committed path edit.</summary>
    internal void FocusFileList()
    {
        if (_model.SelectedEntry is not null && FocusSelectedRow())
            return;

        // "Focus the list" means focus a ROW — the selected one when there is one, else the first. Focus is
        // deliberately NOT allowed to change the selection here: Escape out of the path bar in a Save dialog
        // must not overwrite the name the user has already typed. An EMPTY listing has no row to land on, and
        // focus has to land somewhere or the next Escape would read as "still in the breadcrumb" and refuse to
        // cancel — hence the opt-in Focusable on the ListView itself as the floor.
        if (_listView.ItemContainerGenerator.ContainerFromIndex(0) is { } first)
            first.Focus(FocusNavigationMethod.Programmatic);
        else
            _listView.Focus(FocusNavigationMethod.Programmatic);
    }

    /// <summary>Moves focus to the File name field and selects its text — where a Save dialog opens, because
    /// its user came to type a name rather than to pick one.</summary>
    internal void FocusFileName()
    {
        _fileNameBox.Focus(FocusNavigationMethod.Programmatic);
        _fileNameBox.SelectAll();
    }

    private bool FocusSelectedRow()
    {
        var index = _listView.SelectedIndex;

        if (index < 0)
            return false;

        // Focusing the container is also what scrolls it into view — the ScrollViewer follows keyboard focus,
        // and there is no ScrollIntoView to call instead.
        var container = _listView.ItemContainerGenerator.ContainerFromIndex(index);
        container?.Focus(FocusNavigationMethod.Directional);
        return container is not null;
    }

    private static bool IsLetter(KeyEventArgs e, char letter)
        => e.Text.Length == 1 && char.ToLowerInvariant(e.Text.Span[0]) == letter;
}
