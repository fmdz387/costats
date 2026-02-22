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
    private ProviderPulseViewModel copilot = new();

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

    // Aggregate cost/token totals across all multicc profiles
    [ObservableProperty]
    private string multiccTotalTodayCost = "--";

    [ObservableProperty]
    private string multiccTotalTodayTokens = "--";

    [ObservableProperty]
    private string multiccTotalWeekCost = "--";

    [ObservableProperty]
    private string multiccTotalWeekTokens = "--";

    [ObservableProperty]
    private bool hasMulticcTotals;

    public ObservableCollection<ProviderPulseViewModel> ClaudeProfiles { get; } = new();

    /// <summary>
    /// Returns the currently selected provider based on tab index.
    /// </summary>
    public ProviderPulseViewModel SelectedProvider => SelectedTabIndex switch
    {
        0 => Codex,
        1 => Claude,
        _ => Copilot
    };

    /// <summary>
    /// Returns the provider ID for the currently selected tab.
    /// </summary>
    public string SelectedProviderId
    {
        get
        {
            if (SelectedTabIndex == 0)
                return "codex";

            if (SelectedTabIndex == 1)
            {
                // For multicc, return the first (worst-case) profile's ID for targeted refresh
                if (IsMulticcActive && ClaudeProfiles.Count > 0)
                    return ClaudeProfiles[0].ProviderId;

                return "claude";
            }

            return "copilot";
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderId));
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
                // ── Build all data in local variables first (no UI mutations yet) ──
                var newProviders = new List<ProviderPulseViewModel>();
                var claudeProfileList = new List<ProviderPulseViewModel>();
                ProviderPulseViewModel? newCodex = null;
                ProviderPulseViewModel? newClaude = null;
                ProviderPulseViewModel? newCopilot = null;

                // Aggregate cost/token totals across multicc profiles
                decimal totalTodayCost = 0;
                long totalTodayTokens = 0;
                decimal totalWeekCost = 0;
                long totalWeekTokens = 0;
                var today = DateOnly.FromDateTime(DateTime.Now);
                var weekStart = today.AddDays(-((int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1)); // Monday

                foreach (var (providerId, reading) in value.Providers)
                {
                    var displayName = _displayNames.TryGetValue(providerId, out var name) ? name : providerId;
                    var vm = ProviderPulseViewModel.FromReading(reading, displayName);
                    newProviders.Add(vm);

                    if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                    {
                        newCodex = vm;
                    }
                    else if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
                    {
                        newClaude = vm;
                    }
                    else if (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase))
                    {
                        newCopilot = vm;
                    }
                    else if (providerId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
                    {
                        claudeProfileList.Add(vm);

                        // Accumulate totals from raw reading data
                        if (reading.Usage?.Consumption is { } c)
                        {
                            totalTodayCost += c.TodayCostUsd;
                            totalTodayTokens += c.TodayTokens.TotalConsumed;

                            // Compute this week from daily breakdown (Mon-Sun)
                            foreach (var slice in c.DailyBreakdown)
                            {
                                if (slice.Period >= weekStart && slice.Period <= today)
                                {
                                    totalWeekCost += slice.ComputedCostUsd;
                                    totalWeekTokens += slice.Tokens.TotalConsumed;
                                }
                            }
                        }
                    }
                }

                // Sort claude profiles by session utilization descending (worst-first)
                claudeProfileList.Sort((a, b) => b.SessionProgress.CompareTo(a.SessionProgress));

                var isMulticc = claudeProfileList.Count > 0;

                // Build summary text
                var summaryText = string.Empty;
                if (isMulticc)
                {
                    newClaude = claudeProfileList[0]; // worst-case for backward compat

                    var total = claudeProfileList.Count;
                    var critical = claudeProfileList.Count(p => p.SessionProgress >= 0.95);
                    var warning = claudeProfileList.Count(p => p.SessionProgress >= 0.80 && p.SessionProgress < 0.95);

                    if (critical > 0)
                        summaryText = $"{total} profiles  ·  {critical} at limit, {warning} warning";
                    else if (warning > 0)
                        summaryText = $"{total} profiles  ·  {warning} near limit";
                    else
                        summaryText = $"{total} profiles  ·  All healthy";
                }

                // ── Apply to observable state (batched, single render frame) ──

                // Set scalar properties before collection changes to prevent layout thrash.
                // IsMulticcActive controls panel visibility — setting it first ensures the
                // correct panel stays visible while collections are swapped.
                if (newCodex is not null) Codex = newCodex;
                if (newClaude is not null) Claude = newClaude;
                if (newCopilot is not null) Copilot = newCopilot;
                IsMulticcActive = isMulticc;
                MulticcSummary = summaryText;

                // Multicc aggregate totals
                if (isMulticc && (totalTodayTokens > 0 || totalWeekTokens > 0))
                {
                    MulticcTotalTodayCost = UsageFormatter.FormatCurrency(totalTodayCost);
                    MulticcTotalTodayTokens = UsageFormatter.FormatTokenCount(totalTodayTokens);
                    MulticcTotalWeekCost = UsageFormatter.FormatCurrency(totalWeekCost);
                    MulticcTotalWeekTokens = UsageFormatter.FormatTokenCount(totalWeekTokens);
                    HasMulticcTotals = true;
                }
                else
                {
                    HasMulticcTotals = false;
                }

                // Swap collection contents (single clear + add, no double-clear)
                Providers.Clear();
                foreach (var p in newProviders) Providers.Add(p);

                ClaudeProfiles.Clear();
                foreach (var p in claudeProfileList) ClaudeProfiles.Add(p);
            }

            // Only notify SelectedProvider if the reference actually changed
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
