using Cursorial.Gallery.Infrastructure;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The Inputs page — a showcase of the editable controls bound two-way to view-model state: a <c>TextBox</c>
/// (<see cref="Name"/>), a <c>PasswordBox</c> (<see cref="Password"/>, with a "reveal" <c>CheckBox</c> driving
/// <see cref="ShowPassword"/>), a <c>CheckBox</c> (<see cref="Subscribed"/>), and a <c>Slider</c>
/// (<see cref="Volume"/>). The live <see cref="Status"/> reflects every bound value (the password as a length,
/// never the plaintext) — proving the two-way binding round-trips.
/// </summary>
public sealed class InputsPageViewModel : PageViewModel
{
    private string _name = "";
    private string _password = "";
    private bool _showPassword;
    private bool _subscribed = true;
    private double _volume = 40;

    public override string Title => "Inputs";
    public override string Summary => "TextBox / PasswordBox / CheckBox / Slider bound two-way to view-model state.";

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) Raise(nameof(Status)); }
    }

    public string Password
    {
        get => _password;
        set { if (Set(ref _password, value ?? "")) Raise(nameof(Status)); }
    }

    /// <summary>Drives the <c>PasswordBox.RevealPassword</c> (two-way with the "Reveal" check box).</summary>
    public bool ShowPassword
    {
        get => _showPassword;
        set => Set(ref _showPassword, value);
    }

    public bool Subscribed
    {
        get => _subscribed;
        set { if (Set(ref _subscribed, value)) Raise(nameof(Status)); }
    }

    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, value)) Raise(nameof(Status)); }
    }

    /// <summary>The live readout of every bound value — the password as a length, never the plaintext.</summary>
    public string Status =>
        $"Name=\"{_name}\"   Password.Length={_password.Length}   Subscribed={_subscribed}   Volume={_volume:0}";
}
