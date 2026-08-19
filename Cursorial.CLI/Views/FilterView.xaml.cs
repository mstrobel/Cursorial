using Cursorial.CLI.Commandlets;
using Cursorial.UI;

namespace Cursorial.CLI.Views;

public partial class FilterView
{
    // Mirrors the XAML's MaxVisibleItems: the band never reserves more rows than the popup can show.
    private const int MaxCandidateRows = 8;

    // field row + count header + key-hint footer + the popup's two border rows.
    private const int BandChromeRows = 5;

    public FilterView()
    {
        InitializeComponent();

        // The popup is an overlay: the inline region measures ROOT content only, so the band it opens
        // over must be reserved here — sized to the LIVE match count (a three-item list should not dig a
        // thirteen-row hole), and only while the popup is open, or the retained receipt (and any
        // zero-match lull) would drag blank rows along under one line of answer. The TextChanged
        // subscription tracks narrowing; it runs AFTER the popup's own (the popup subscribes when Target
        // is assigned, during InitializeComponent), so MatchCount is already re-queried when we read it.
        Popup.Opened += (_, _) => SyncBandReserve();
        Popup.Closed += (_, _) => MinHeight = 0;
        QueryBox.TextChanged += (_, _) => SyncBandReserve();

        Popup.Committed += (_, e) =>
        {
            if (DataContext is FilterViewModel vm)
                vm.AcceptItem(e.Item);
        };
    }

    private void SyncBandReserve()
    {
        if (Popup.IsOpen)
            MinHeight = Math.Min(Popup.MatchCount, MaxCandidateRows) + BandChromeRows;
    }

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        QueryBox.Focus(); // type-to-narrow immediately; the popup rides the box's key stream
    }
}
