using Cursorial.UI;

namespace Cursorial.CLI.Views;

public partial class InputView
{
    public InputView() => InitializeComponent();

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        Editor.Focus(); // type-immediately, no click-first
    }
}
