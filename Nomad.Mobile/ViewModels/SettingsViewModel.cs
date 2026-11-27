using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Nomad.Mobile.ViewModels;

/// <summary>Settings: the hub, the PCs on the board, the app's version and updates, and Sign out, which also revokes this phone on the hub.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IClosable
{
    private readonly MainViewModel main;

    /// <summary>Follows the connection and the board while it shows.</summary>
    public SettingsViewModel(MainViewModel main)
    {
        this.main = main;
        main.PropertyChanged += OnMainChanged;
        main.Chats.BoardChanged += OnBoardChanged;
        _ = main.Update.CheckAsync(force: true);
    }

    /// <summary>The App section: this version, and an update when there is one (looked for again as Settings opens).</summary>
    public AppUpdateViewModel Update => main.Update;

    public void Close()
    {
        main.PropertyChanged -= OnMainChanged;
        main.Chats.BoardChanged -= OnBoardChanged;
    }

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            OnPropertyChanged(nameof(ConnectionText));
        }
    }

    private void OnBoardChanged()
    {
        OnPropertyChanged(nameof(Pcs));
        OnPropertyChanged(nameof(HasPcs));
    }

    public string HubAddress => main.Enrollment?.Hub.ToString().TrimEnd('/') ?? "";

    public string DeviceName => main.Enrollment?.DeviceName ?? "";

    public string ConnectionText => main.IsConnected ? "Connected" : "Not connected";

    public IReadOnlyList<PcLine> Pcs => [.. main.Devices.Select(d => new PcLine(d.DeviceName, d.Chats.Count == 1 ? "1 open chat" : $"{d.Chats.Count} open chats"))];

    public bool HasPcs => main.Devices.Count > 0;

    /// <summary>Sign out asks once more before it acts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignOutText))]
    public partial bool ConfirmingSignOut { get; set; }

    public string SignOutText => ConfirmingSignOut ? "Tap again to sign out and revoke this phone" : "Sign out";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (!ConfirmingSignOut)
        {
            ConfirmingSignOut = true;
            return;
        }

        IsBusy = true;
        await main.SignOutAsync();
        IsBusy = false;
    }

    /// <summary>Connect to another hub (or the same one again, with a new token).</summary>
    [RelayCommand]
    private void ConnectElsewhere() => main.ShowConnect();

    [RelayCommand]
    private void Back() => main.GoBack();
}

public sealed record PcLine(string Name, string Detail);
