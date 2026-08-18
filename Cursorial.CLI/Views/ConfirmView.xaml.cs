using Cursorial.UI;

namespace Cursorial.CLI.Views;

public partial class ConfirmView
{
    public ConfirmView() => InitializeComponent();

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        Focus(); // the panel itself anchors the y/n/Enter key bindings
    }
}
