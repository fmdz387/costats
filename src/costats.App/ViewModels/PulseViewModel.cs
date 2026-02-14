using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.App.ViewModels;

public sealed partial class PulseViewModel : ObservableObject, IObserver<PulseState>, IDisposable
{
    private readonly IPulseOrchestrator _orchestrator;
    private readonly IDisposable _subscription;
    private readonly Dictionary<string, string> _displayNames;

    public PulseViewModel(IPulseOrchestrator orchestrator, IEnumerable<ISignalSource> sources)
    {
        _orchestrator = orchestrator;
        _displayNames = sources
            .Select(source => source.Profile)
            .GroupBy(profile => profile.ProviderId)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        Providers = new ObservableCollection<ProviderPulseViewModel>();
        _subscription = orchestrator.PulseStream.Subscribe(this);
    }

    public ObservableCollection<ProviderPulseViewModel> Providers { get; }

    [ObservableProperty]
    private string lastUpdated = "Never";

    [ObservableProperty]
    private ProviderPulseViewModel claude = new();

    [ObservableProperty]
    private ProviderPulseViewModel codex = new();

    [ObservableProperty]
    private string updatedLabel = "Updated never";

    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    private bool isRefreshing = true; // Start true to show spinner on initial load

    [ObservableProperty]
    private bool isMulticcActive;

    [ObservableProperty]
    private string multiccSummary = string.Empty;

    public ObservableCollection<ProviderPulseViewModel> ClaudeProfiles { get; } = new();

    /// <summary>
    /// Returns the currently selected provider based on tab index.
    /// </summary>
    public ProviderPulseViewModel SelectedProvider => SelectedTabIndex == 0 ? Codex : Claude;

    /// <summary>
    /// Returns the provider ID for the currently selected tab.
    /// </summary>
    public string SelectedProviderId
    {
        get
        {
            if (SelectedTabIndex == 0)
                return "codex";

            // For multicc, return the first (worst-case) profile's ID for targeted refresh
            if (IsMulticcActive && ClaudeProfiles.Count > 0)
                return ClaudeProfiles[0].ProviderId;

            return "claude";
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderId));

        // Silent refresh when switching tabs
        _ = RefreshSelectedProviderSilentlyAsync();
    }

    /// <summary>
    /// Silently refresh the currently selected provider (no loading indicator).
    /// </summary>
    public async Task RefreshSelectedProviderSilentlyAsync()
    {
        try
        {
            await _orchestrator.RefreshProviderAsync(SelectedProviderId, CancellationToken.None);
        }
        catch
        {
            // Silent refresh failures are non-blocking
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Show loading indicator immediately for responsive UX
        IsRefreshing = true;
        try
        {
            await _orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log but don't crash - refresh failures should not take down the app
            System.Diagnostics.Debug.WriteLine($"Refresh failed: {ex.Message}");
        }
        finally
        {
            // Ensure loading indicator is hidden even if orchestrator doesn't publish
            IsRefreshing = false;
        }
    }

    public void OnNext(PulseState value)
    {
        // Use BeginInvoke (async) instead of Invoke to avoid blocking the UI thread
        // This allows window deactivation to work even during data updates
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsRefreshing = value.IsRefreshing;

            // Only update provider data if we have providers (keep last state during refresh)
            if (value.Providers.Count > 0)
            {
                Providers.Clear();
                ClaudeProfiles.Clear();

                ProviderPulseViewModel? firstClaude = null;

                foreach (var (providerId, reading) in value.Providers)
                {
                    var displayName = _displayNames.TryGetValue(providerId, out var name) ? name : providerId;
                    var vm = ProviderPulseViewModel.FromReading(reading, displayName);
                    Providers.Add(vm);

                    if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                    {
                        Codex = vm;
                    }
                    else if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
                    {
                        // Non-multicc single Claude provider
                        Claude = vm;
                    }
                    else if (providerId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Multicc profile
                        ClaudeProfiles.Add(vm);
                        firstClaude ??= vm;
                    }
                }

                // Sort ClaudeProfiles by session utilization descending (worst-first for glanceability)
                if (ClaudeProfiles.Count > 0)
                {
                    var sorted = ClaudeProfiles.OrderByDescending(p => p.SessionProgress).ToList();
                    ClaudeProfiles.Clear();
                    foreach (var p in sorted)
                    {
                        ClaudeProfiles.Add(p);
                    }

                    // Set Claude property to the worst-case profile for backward compatibility
                    Claude = sorted[0];
                }

                IsMulticcActive = ClaudeProfiles.Count > 0;

                // Build summary text for multicc header
                if (IsMulticcActive)
                {
                    var nearLimit = ClaudeProfiles.Count(p => p.SessionProgress >= 0.80);
                    MulticcSummary = nearLimit > 0
                        ? $"{ClaudeProfiles.Count} profiles | {nearLimit} near limit"
                        : $"{ClaudeProfiles.Count} profiles | All healthy";
                }
                else
                {
                    MulticcSummary = string.Empty;
                }
            }

            // Notify that SelectedProvider may have changed
            OnPropertyChanged(nameof(SelectedProvider));

            LastUpdated = value.LastRefresh.ToLocalTime().ToString("g");
            UpdatedLabel = $"Updated {value.LastRefresh.ToLocalTime():t}";
        });
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
