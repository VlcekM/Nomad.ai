using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Antigravity;
using Nomad.Core.Codex;
using Nomad.Core.OpenCode;
using Nomad.Core.Process;

namespace Nomad.App.ViewModels;

/// <summary>One subscription OAuth provider offered by OpenCode in its inline picker.</summary>
public sealed partial class OpenCodeProviderOption(OpenCodeOAuthProvider provider) : ObservableObject
{
    public OpenCodeOAuthProvider Provider { get; } = provider;

    public string Id => Provider.Id;

    public string Name => Provider.Name;

    public string MethodLabel => Provider.MethodLabel;

    public bool IsConnected => Provider.IsConnected;
}

/// <summary>One input field an OpenCode OAuth provider asks for before opening the browser (e.g. GitLab URL).</summary>
public sealed partial class OpenCodePromptField(OpenCodeAuthPrompt prompt) : ObservableObject
{
    public OpenCodeAuthPrompt Prompt { get; } = prompt;

    public string Key => Prompt.Key;

    public string Label => Prompt.Label;

    public string Placeholder => Prompt.Placeholder ?? "";

    [ObservableProperty]
    public partial string Value { get; set; } = prompt.DefaultValue ?? "";
}

/// <summary>
/// One coding agent on the Settings → Agents page: its CLI path, live sign-in status, and one-click browser
/// subscription sign-in (with progress, reopen link, device code copy, and cancel).
/// </summary>
public sealed partial class AgentAccountViewModel : ObservableObject
{
    private readonly Action<string> _savePath;
    private readonly Action _onAuthChanged;
    private readonly bool _initialized;

    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _statusCts;
    private OpenCodeOAuthProvider? _pendingCodeProvider;
    private OpenCodeOAuthProvider? _promptingProvider;
    private DispatcherTimer? _successTimer;
    private DispatcherTimer? _copiedTimer;

    public AgentAccountViewModel(
        string backendId,
        string name,
        string description,
        string pathWatermark,
        string installHint,
        string initialPath,
        Action<string> savePath,
        Action onAuthChanged)
    {
        BackendId = backendId;
        Name = name;
        Description = description;
        PathWatermark = pathWatermark;
        InstallHint = installHint;
        _savePath = savePath;
        _onAuthChanged = onAuthChanged;

        CliPath = initialPath;
        UpdateInstalledState();
        _initialized = true;
    }

    public string BackendId { get; }

    public string Name { get; }

    public string Description { get; }

    public string PathWatermark { get; }

    public string InstallHint { get; }

    public bool IsOpenCode => BackendId == AgentBackends.OpenCode;

    /// <summary>Tooltip explaining what clicking Sign in does for this agent.</summary>
    public string SignInToolTip => BackendId switch
    {
        AgentBackends.ClaudeCode => "Opens claude.ai in your browser to sign in with your Claude subscription",
        AgentBackends.Codex => "Opens ChatGPT in your browser to sign in with your ChatGPT subscription",
        AgentBackends.Antigravity => "Opens Google in your browser to sign in with your Antigravity account",
        AgentBackends.OpenCode => "Choose a subscription provider to sign in to in your browser",
        _ => "Sign in in your browser",
    };

    // ------------------------------------------------------------------ CLI path

    [ObservableProperty]
    public partial string CliPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimarySignIn), nameof(ShowSecondarySignIn), nameof(ShowSignOut), nameof(ShowStatusBadge))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial string PathStatus { get; set; } = "";

    partial void OnCliPathChanged(string value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? PathWatermark : value.Trim();
        if (_initialized)
        {
            _savePath(path);
        }

        var wasInstalled = IsInstalled;
        UpdateInstalledState();
        if (_initialized && IsInstalled && !wasInstalled)
        {
            _ = RefreshStatusAsync();
        }
    }

    private void UpdateInstalledState()
    {
        var path = string.IsNullOrWhiteSpace(CliPath) ? PathWatermark : CliPath.Trim();
        var resolved = BackendId switch
        {
            AgentBackends.ClaudeCode => CliLocator.Resolve(path),
            AgentBackends.Codex => CodexServer.ResolveExecutable(path),
            AgentBackends.Antigravity => AntigravityBackend.ResolveExecutable(path),
            AgentBackends.OpenCode => OpenCodeServer.ResolveExecutable(path),
            _ => null,
        };

        IsInstalled = resolved is not null;
        PathStatus = resolved is not null ? "Found: " + resolved : "Not found. " + InstallHint;
        if (!IsInstalled)
        {
            IsSignedIn = false;
            IsSubscription = false;
            IsChecking = false;
            AuthSummary = "Not installed";
            AuthDetail = null;
        }
    }

    // ------------------------------------------------------------------ auth status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimarySignIn), nameof(ShowSecondarySignIn), nameof(ShowSignOut), nameof(ShowStatusBadge))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimarySignIn), nameof(ShowSecondarySignIn), nameof(ShowSignOut), nameof(SignInButtonText), nameof(IsSignedOutBadge))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial bool IsSubscription { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusBadge))]
    public partial string AuthSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthDetail))]
    public partial string? AuthDetail { get; set; }

    public bool HasAuthDetail => !string.IsNullOrWhiteSpace(AuthDetail);

    public bool ShowStatusBadge => IsInstalled && (IsChecking || !string.IsNullOrEmpty(AuthSummary));

    public bool IsSignedOutBadge => IsInstalled && !IsChecking && !IsSignedIn;

    public bool ShowPrimarySignIn => IsInstalled && !IsChecking && !IsSignedIn && !IsSigningIn && !IsSigningOut;

    public bool ShowSecondarySignIn => IsInstalled && !IsChecking && IsSignedIn && !IsSigningIn && !IsSigningOut;

    public bool ShowSignOut => IsInstalled && !IsChecking && IsSignedIn && !IsSigningIn && !IsSigningOut && !IsOpenCode;

    public string SignInButtonText => IsOpenCode
        ? (IsProviderPickerOpen ? "Close" : IsSignedIn ? "Add subscription" : "Sign in")
        : (IsSignedIn ? "Switch account" : "Sign in");

    // ------------------------------------------------------------------ in-flight login UX

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimarySignIn), nameof(ShowSecondarySignIn), nameof(ShowSignOut), nameof(ShowLoginPanel))]
    public partial bool IsSigningIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrimarySignIn), nameof(ShowSecondarySignIn), nameof(ShowSignOut))]
    public partial bool IsSigningOut { get; set; }

    [ObservableProperty]
    public partial string LoginMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginUrl))]
    public partial string? LoginUrl { get; set; }

    public bool HasLoginUrl => !string.IsNullOrEmpty(LoginUrl);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    public partial string? DeviceCode { get; set; }

    public bool HasDeviceCode => !string.IsNullOrEmpty(DeviceCode);

    [ObservableProperty]
    public partial string CopyCodeText { get; set; } = "Copy code";

    [ObservableProperty]
    public partial bool NeedsCodeInput { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    public partial string AuthCodeInput { get; set; } = "";

    public bool ShowLoginPanel => IsSigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess))]
    public partial string? SuccessMessage { get; set; }

    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);

    // ------------------------------------------------------------------ OpenCode providers

    public ObservableCollection<OpenCodeConnectedProvider> ConnectedProviders { get; } = [];

    public bool HasConnectedProviders => ConnectedProviders.Count > 0;

    public ObservableCollection<OpenCodeProviderOption> OpenCodeProviders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignInButtonText))]
    public partial bool IsProviderPickerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingProviders { get; set; }

    public ObservableCollection<OpenCodePromptField> PromptFields { get; } = [];

    [ObservableProperty]
    public partial bool IsPromptingInputs { get; set; }

    [ObservableProperty]
    public partial string PromptingTitle { get; set; } = "";

    // ------------------------------------------------------------------ commands

    /// <summary>Checks this agent's current sign-in status in the background.</summary>
    public async Task RefreshStatusAsync(bool allowStartOpenCode = false)
    {
        UpdateInstalledState();
        if (!IsInstalled)
        {
            return;
        }

        _statusCts?.Cancel();
        var cts = new CancellationTokenSource();
        _statusCts = cts;

        IsChecking = true;
        if (string.IsNullOrEmpty(AuthSummary))
        {
            AuthSummary = "Checking…";
        }

        try
        {
            var status = BackendId switch
            {
                AgentBackends.ClaudeCode => await AgentAuth.GetClaudeStatusAsync(CliPath, cts.Token),
                AgentBackends.Codex => await AgentAuth.GetCodexStatusAsync(cts.Token),
                AgentBackends.Antigravity => await AgentAuth.GetAntigravityStatusAsync(cts.Token),
                AgentBackends.OpenCode => await AgentAuth.GetOpenCodeStatusAsync(allowStartOpenCode, cts.Token),
                _ => new AgentAuthStatus(false, false, "Not signed in"),
            };

            if (cts.IsCancellationRequested)
            {
                return;
            }

            ApplyStatus(status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                IsSignedIn = false;
                IsSubscription = false;
                AuthSummary = "Status unavailable";
                AuthDetail = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_statusCts, cts))
            {
                IsChecking = false;
            }
        }
    }

    private void ApplyStatus(AgentAuthStatus status)
    {
        IsSignedIn = status.IsSignedIn;
        IsSubscription = status.IsSubscription;
        AuthSummary = status.Summary;
        AuthDetail = status.Detail;

        if (IsOpenCode)
        {
            ConnectedProviders.Clear();
            foreach (var p in status.ConnectedProviders ?? [])
            {
                ConnectedProviders.Add(p);
            }

            OnPropertyChanged(nameof(HasConnectedProviders));
        }
    }

    /// <summary>Starts the browser subscription sign-in flow (or toggles OpenCode's provider picker).</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!IsInstalled || IsSigningIn || IsSigningOut)
        {
            return;
        }

        ErrorMessage = null;
        ClearSuccess();

        if (IsOpenCode)
        {
            if (IsProviderPickerOpen)
            {
                IsProviderPickerOpen = false;
                IsPromptingInputs = false;
                return;
            }

            await OpenOpenCodeProviderPickerAsync();
            return;
        }

        await RunDirectSignInAsync();
    }

    private async Task RunDirectSignInAsync()
    {
        _authCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _authCts = cts;

        IsSigningIn = true;
        LoginUrl = null;
        DeviceCode = null;
        NeedsCodeInput = false;
        AuthCodeInput = "";
        LoginMessage = "Opening sign-in page in your browser…";

        void OnProgress(AgentLoginProgress p) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_authCts, cts))
            {
                return;
            }

            LoginMessage = p.Message;
            if (!string.IsNullOrEmpty(p.Url))
            {
                LoginUrl = p.Url;
            }

            if (!string.IsNullOrEmpty(p.DeviceCode))
            {
                DeviceCode = p.DeviceCode;
            }

            NeedsCodeInput = p.NeedsCodeInput;
        });

        try
        {
            switch (BackendId)
            {
                case AgentBackends.ClaudeCode:
                    await AgentAuth.LoginClaudeAsync(CliPath, OnProgress, cts.Token);
                    break;
                case AgentBackends.Codex:
                    await AgentAuth.LoginCodexAsync(url => PlatformShell.OpenUrl(url), OnProgress, cts.Token);
                    break;
                case AgentBackends.Antigravity:
                    await AgentAuth.LoginAntigravityAsync(OnProgress, cts.Token);
                    break;
            }

            FinishSignInSuccess();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user or dialog closed.
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
        }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                IsSigningIn = false;
                LoginUrl = null;
                DeviceCode = null;
                NeedsCodeInput = false;
            }
        }
    }

    private async Task OpenOpenCodeProviderPickerAsync()
    {
        IsProviderPickerOpen = true;
        IsPromptingInputs = false;
        IsLoadingProviders = true;
        ErrorMessage = null;

        try
        {
            var (oauthProviders, connected) = await AgentAuth.LoadOpenCodeProvidersAsync();
            OpenCodeProviders.Clear();
            foreach (var p in oauthProviders)
            {
                OpenCodeProviders.Add(new OpenCodeProviderOption(p));
            }

            ConnectedProviders.Clear();
            foreach (var p in connected)
            {
                ConnectedProviders.Add(p);
            }

            OnPropertyChanged(nameof(HasConnectedProviders));
            IsSignedIn = connected.Count > 0;
            AuthSummary = connected.Count == 0
                ? "No account connected"
                : connected.Count == 1 ? "1 provider connected" : $"{connected.Count} providers connected";
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
            IsProviderPickerOpen = false;
        }
        finally
        {
            IsLoadingProviders = false;
        }
    }

    /// <summary>Starts OAuth sign-in for one chosen OpenCode subscription provider.</summary>
    [RelayCommand]
    private async Task ChooseOpenCodeProviderAsync(OpenCodeProviderOption option)
    {
        if (option is null || IsSigningIn)
        {
            return;
        }

        ErrorMessage = null;
        var provider = option.Provider;

        // Collect required inputs; select prompts with a default (like GitHub Copilot's GitHub.com) auto-fill for one-click login.
        var autoInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var textPrompts = new List<OpenCodeAuthPrompt>();
        foreach (var prompt in provider.Prompts)
        {
            if (prompt.ConditionKey is not null
                && (!autoInputs.TryGetValue(prompt.ConditionKey, out var actual)
                    || !string.Equals(actual, prompt.ConditionValue, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (prompt.IsSelect && prompt.DefaultValue is not null)
            {
                autoInputs[prompt.Key] = prompt.DefaultValue;
            }
            else if (!prompt.IsSelect)
            {
                textPrompts.Add(prompt);
            }
        }

        if (textPrompts.Count > 0)
        {
            _promptingProvider = provider;
            PromptingTitle = $"Sign in to {provider.Name}";
            PromptFields.Clear();
            foreach (var p in textPrompts)
            {
                PromptFields.Add(new OpenCodePromptField(p));
            }

            IsPromptingInputs = true;
            return;
        }

        IsProviderPickerOpen = false;
        IsPromptingInputs = false;
        await RunOpenCodeOAuthAsync(provider, autoInputs);
    }

    /// <summary>Continues an OpenCode OAuth sign-in after filling in required provider fields (e.g. GitLab instance URL).</summary>
    [RelayCommand]
    private async Task ContinueOpenCodePromptAsync()
    {
        if (_promptingProvider is not { } provider)
        {
            return;
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prompt in provider.Prompts.Where(p => p.IsSelect && p.DefaultValue is not null))
        {
            inputs[prompt.Key] = prompt.DefaultValue!;
        }

        foreach (var field in PromptFields)
        {
            var val = field.Value.Trim();
            if (val.Length > 0)
            {
                inputs[field.Key] = val;
            }
        }

        _promptingProvider = null;
        IsPromptingInputs = false;
        IsProviderPickerOpen = false;
        await RunOpenCodeOAuthAsync(provider, inputs);
    }

    [RelayCommand]
    private void CancelOpenCodePrompt()
    {
        _promptingProvider = null;
        IsPromptingInputs = false;
    }

    private async Task RunOpenCodeOAuthAsync(OpenCodeOAuthProvider provider, IReadOnlyDictionary<string, string>? inputs)
    {
        _authCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _authCts = cts;

        IsSigningIn = true;
        LoginUrl = null;
        DeviceCode = null;
        NeedsCodeInput = false;
        AuthCodeInput = "";
        _pendingCodeProvider = null;
        LoginMessage = $"Opening {provider.Name} sign-in in your browser…";

        void OnProgress(AgentLoginProgress p) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_authCts, cts))
            {
                return;
            }

            LoginMessage = p.Message;
            LoginUrl = p.Url;
            DeviceCode = p.DeviceCode;
            NeedsCodeInput = p.NeedsCodeInput;
        });

        try
        {
            var auth = await AgentAuth.StartOpenCodeOAuthAsync(
                provider,
                inputs,
                url => PlatformShell.OpenUrl(url),
                OnProgress,
                cts.Token);

            if (string.Equals(auth.Method, "code", StringComparison.OrdinalIgnoreCase))
            {
                // Keep the panel open so the user can paste the authorization code from the webpage.
                _pendingCodeProvider = provider;
                return;
            }

            await AgentAuth.CompleteOpenCodeOAuthAsync(provider, code: null, cts.Token);
            FinishSignInSuccess($"{provider.Name} connected.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
            IsSigningIn = false;
            LoginUrl = null;
            DeviceCode = null;
            NeedsCodeInput = false;
        }
        finally
        {
            if (_pendingCodeProvider is null && ReferenceEquals(_authCts, cts))
            {
                IsSigningIn = false;
                LoginUrl = null;
                DeviceCode = null;
                NeedsCodeInput = false;
            }
        }
    }

    private bool CanSubmitCode() => !string.IsNullOrWhiteSpace(AuthCodeInput);

    /// <summary>Submits the authorization code pasted from the browser for an OpenCode "code" OAuth flow.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitCode))]
    private async Task SubmitCodeAsync()
    {
        if (_pendingCodeProvider is not { } provider || string.IsNullOrWhiteSpace(AuthCodeInput))
        {
            return;
        }

        var code = AuthCodeInput.Trim();
        var cts = _authCts;
        LoginMessage = $"Completing {provider.Name} sign-in…";
        NeedsCodeInput = false;
        try
        {
            await AgentAuth.CompleteOpenCodeOAuthAsync(provider, code, cts?.Token ?? CancellationToken.None);
            _pendingCodeProvider = null;
            FinishSignInSuccess($"{provider.Name} connected.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
            NeedsCodeInput = true;
            return;
        }

        IsSigningIn = false;
        LoginUrl = null;
        DeviceCode = null;
        AuthCodeInput = "";
    }

    /// <summary>Disconnects one provider from OpenCode.</summary>
    [RelayCommand]
    private async Task DisconnectOpenCodeProviderAsync(OpenCodeConnectedProvider provider)
    {
        if (provider is null || IsSigningIn || IsSigningOut)
        {
            return;
        }

        ErrorMessage = null;
        IsSigningOut = true;
        try
        {
            await AgentAuth.RemoveOpenCodeProviderAsync(provider.Id);
            await RefreshStatusAsync(allowStartOpenCode: true);
            _onAuthChanged();
            if (IsProviderPickerOpen)
            {
                await OpenOpenCodeProviderPickerAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
        }
        finally
        {
            IsSigningOut = false;
        }
    }

    /// <summary>Signs out of Claude Code, Codex, or Antigravity.</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (!IsInstalled || !IsSignedIn || IsSigningIn || IsSigningOut || IsOpenCode)
        {
            return;
        }

        ErrorMessage = null;
        ClearSuccess();
        IsSigningOut = true;
        try
        {
            switch (BackendId)
            {
                case AgentBackends.ClaudeCode:
                    await AgentAuth.LogoutClaudeAsync(CliPath);
                    break;
                case AgentBackends.Codex:
                    await AgentAuth.LogoutCodexAsync();
                    break;
                case AgentBackends.Antigravity:
                    await AgentAuth.LogoutAntigravityAsync();
                    break;
            }

            await RefreshStatusAsync();
            _onAuthChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = CleanError(ex);
        }
        finally
        {
            IsSigningOut = false;
        }
    }

    /// <summary>Reopens the active OAuth sign-in webpage in the browser.</summary>
    [RelayCommand]
    private void OpenLoginPage()
    {
        if (!string.IsNullOrEmpty(LoginUrl))
        {
            PlatformShell.OpenUrl(LoginUrl);
        }
    }

    /// <summary>Marks the device code as copied for a moment (called after copying to the clipboard).</summary>
    public void NotifyCodeCopied()
    {
        CopyCodeText = "Copied";
        _copiedTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _copiedTimer!.Stop();
            CopyCodeText = "Copy code";
        });
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    /// <summary>Cancels an in-flight sign-in flow and cleans up its subprocess / OAuth session.</summary>
    [RelayCommand]
    public void CancelLogin()
    {
        _authCts?.Cancel();
        _authCts = null;
        _pendingCodeProvider = null;
        IsSigningIn = false;
        LoginUrl = null;
        DeviceCode = null;
        NeedsCodeInput = false;
        AuthCodeInput = "";
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    /// <summary>Stops any background checks or active login when Settings closes.</summary>
    public void Detach()
    {
        _statusCts?.Cancel();
        _statusCts = null;
        CancelLogin();
        _successTimer?.Stop();
        _copiedTimer?.Stop();
    }

    private void FinishSignInSuccess(string message = "Signed in.")
    {
        ErrorMessage = null;
        SuccessMessage = message;
        _successTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _successTimer!.Stop();
            SuccessMessage = null;
        });
        _successTimer.Stop();
        _successTimer.Start();

        _ = RefreshStatusAsync(allowStartOpenCode: IsOpenCode);
        _onAuthChanged();
    }

    private void ClearSuccess()
    {
        _successTimer?.Stop();
        SuccessMessage = null;
    }

    private static string CleanError(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(msg) ? "Sign-in did not complete." : msg.Trim();
    }
}
