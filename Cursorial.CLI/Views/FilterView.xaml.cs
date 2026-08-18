using Cursorial.UI;

namespace Cursorial.CLI.Views;

public partial class FilterView
{
    public FilterView() => InitializeComponent();

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        QueryBox.Focus(); // type-to-narrow immediately; Up/Down drive the list via the root bindings
    }
}
