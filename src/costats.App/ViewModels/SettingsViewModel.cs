using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.Settings;
using Microsoft.Win32;

namespace costats.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IPulseOrchestrator _pulseOrchestrator;
    private readonly StartupUpdateCoordinator? _updateCoordinator;
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "costats";

    public SettingsViewModel(ISettingsStore settingsStore, AppSettings settings, IPulseOrchestrator pulseOrchestrator, StartupUpdateCoordinator? updateCoordinator = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _updateCoordinator = updateCoordinator;

        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = GetStartupRegistryValue();
    }

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    public string Version { get; } =
        (Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown")
        .Split('+')[0];

    public static IReadOnlyList<RefreshOption> RefreshOptions { get; } = new[]
    {
        new RefreshOption(1, "1 minute"),
        new RefreshOption(2, "2 minutes"),
        new RefreshOption(3, "3 minutes"),
        new RefreshOption(5, "5 minutes"),
        new RefreshOption(10, "10 minutes"),
        new RefreshOption(15, "15 minutes"),
    };

    public RefreshOption SelectedRefreshOption
    {
        get => RefreshOptions.FirstOrDefault(o => o.Minutes == RefreshMinutes) ?? RefreshOptions[3];
        set
        {
            if (value is not null && RefreshMinutes != value.Minutes)
            {
                RefreshMinutes = value.Minutes;
                OnPropertyChanged();
            }
        }
    }

    partial void OnRefreshMinutesChanged(int value)
    {
        _settings.RefreshMinutes = value;
        _pulseOrchestrator.UpdateRefreshInterval(TimeSpan.FromMinutes(value));
        _ = SaveSettingsAsync();
        OnPropertyChanged(nameof(SelectedRefreshOption));
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        _settings.StartAtLogin = value;
        SetStartupRegistryValue(value);
        _ = SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateCoordinator is null)
        {
            UpdateStatusText = "Updates are not available.";
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";

        try
        {
            var result = await Task.Run(() => _updateCoordinator.CheckAndStageUpdateAsync(CancellationToken.None, forceCheck: true));

            switch (result)
            {
                case UpdateCheckResult.UpdateStaged:
                case UpdateCheckResult.UpdateAlreadyStaged:
                    UpdateStatusText = "Update found. Restarting...";
                    if (await Task.Run(() => _updateCoordinator.TryApplyPendingUpdateAsync(CancellationToken.None)))
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            System.Windows.Application.Current.Shutdown(0));
                    }
                    else
                    {
                        UpdateStatusText = "Update staged. Restart to apply.";
                        IsCheckingForUpdates = false;
                    }
                    break;

                case UpdateCheckResult.UpToDate:
                case UpdateCheckResult.Skipped:
                    UpdateStatusText = "You're up to date.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.Disabled:
                    UpdateStatusText = "Updates are not available.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.AlreadyRunning:
                    UpdateStatusText = "Update check already in progress.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.CheckFailed:
                default:
                    UpdateStatusText = "Could not check for updates.";
                    IsCheckingForUpdates = false;
                    break;
            }
        }
        catch
        {
            UpdateStatusText = "Could not check for updates.";
            IsCheckingForUpdates = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    private static bool GetStartupRegistryValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupRegistryValue(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key is null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Silently ignore registry errors
        }
    }
}

public sealed record RefreshOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}
