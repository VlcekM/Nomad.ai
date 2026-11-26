using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// The connect screen. Scanning the QR code from Settings › Hub › Add phone with the camera app fills it in; the address and token
/// (or the whole link) can also be pasted.
/// </summary>
public sealed partial class ConnectViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string DeviceName { get; set; } = AppServices.DefaultDeviceName;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>A whole <c>nomad://enroll</c> link pasted into the address box fills in both boxes.</summary>
    partial void OnAddressChanged(string value)
    {
        if (EnrollLink.TryParse(value) is { } link)
        {
            Fill(link.Hub, link.Token);
        }
    }

    public void Fill(string hub, string token)
    {
        Address = hub;
        Token = token;
        Error = null;
        OnPropertyChanged(nameof(CanCancel));
    }

    public void Reset()
    {
        Address = "";
        Token = "";
        Error = null;
        IsBusy = false;
        OnPropertyChanged(nameof(CanCancel));
    }

    private bool CanConnect => !IsBusy && Address.Trim().Length > 0 && Token.Trim().Length > 0 && DeviceName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var (url, problem) = EnrollLink.CheckAddress(Address);
        if (url is null)
        {
            Error = problem;
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await main.EnrollAsync(url, Token.Trim(), DeviceName.Trim());
            Token = "";
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest }
                ? "The hub refused the token. Tokens work once and expire; make a new one with Add phone on a PC."
                : "Couldn't connect: " + ChatViewModel.Problem(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Back to the chats without connecting elsewhere (only when this phone is already connected).</summary>
    public bool CanCancel => main.Connection is not null;

    [RelayCommand]
    private void Cancel() => main.GoBack();
}
