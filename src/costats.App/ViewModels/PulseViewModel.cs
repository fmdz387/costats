using System.Collections.ObjectModel;
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

    /// <summary>
    /// Returns the currently selected provider based on tab index.
    /// </summary>
    public ProviderPulseViewModel SelectedProvider => SelectedTabIndex == 0 ? Codex : Claude;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _orchestrator.RefreshOnceAsync(CancellationToken.None);
    }

    public void OnNext(PulseState value)
    {
        // Use BeginInvoke (async) instead of Invoke to avoid blocking the UI thread
        // This allows window deactivation to work even during data updates
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Providers.Clear();
            foreach (var (providerId, reading) in value.Providers)
            {
                var displayName = _displayNames.TryGetValue(providerId, out var name) ? name : providerId;
                var vm = ProviderPulseViewModel.FromReading(reading, displayName);
                Providers.Add(vm);
                if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
                {
                    Claude = vm;
                }
                else if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    Codex = vm;
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
