using Cursorial.UI;

namespace Cursorial.CLI.Views;

public partial class ChooseView
{
    public ChooseView() => InitializeComponent();

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        List.Focus(); // arrows/Enter route from the list without a click-first
    }
}
