using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.CLI.Views;

/// <summary>
/// A <see cref="CompletionPopup"/> that starts its session as soon as its template parts exist: for
/// `filter`, the full candidate list IS the prompt, so waiting for a first keystroke would greet the
/// user with an empty band. <see cref="CompletionPopup.Refresh"/> is the sanctioned host seam for
/// starting a text-mode session; it is posted rather than called inside the template pass because
/// opening a window surface belongs after the layout that built the popup's parts.
/// </summary>
public sealed class AutoOpenCompletionPopup : CompletionPopup
{
    private bool _openRequested;

    // Opt into the base control theme: control themes resolve exact-key, so without this the subclass
    // would render untemplated (WPF DefaultStyleKey parity).
    protected override object ControlThemeKey => typeof(CompletionPopup);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_openRequested)
            return;

        _openRequested = true;
        UIApplication.Current?.Dispatcher.Post(Refresh);
    }
}
