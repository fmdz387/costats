using CommunityToolkit.Mvvm.ComponentModel;
using costats.Core.Pulse;

namespace costats.App.ViewModels;

public sealed partial class ProviderPulseViewModel : ObservableObject
{
    [ObservableProperty]
    private string providerId = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string statusSummary = "No data";

    [ObservableProperty]
    private string planText = string.Empty;

    // Session metrics
    [ObservableProperty]
    private double sessionProgress;

    [ObservableProperty]
    private string sessionUsageLabel = "--";

    [ObservableProperty]
    private string sessionResetText = string.Empty;

    [ObservableProperty]
    private string sessionPaceText = string.Empty;

    [ObservableProperty]
    private double sessionPaceProgress;

    [ObservableProperty]
    private bool sessionPaceOnTop;

    // Weekly metrics
    [ObservableProperty]
    private double weekProgress;

    [ObservableProperty]
    private string weekUsageLabel = "--";

    [ObservableProperty]
    private string weekResetText = string.Empty;

    [ObservableProperty]
    private string weekPaceText = string.Empty;

    [ObservableProperty]
    private double weekPaceProgress;

    [ObservableProperty]
    private bool weekPaceOnTop;

    // Extra usage / Credits
    [ObservableProperty]
    private string extraUsageLabel = "--";

    [ObservableProperty]
    private double extraUsageProgress;

    [ObservableProperty]
    private bool hasExtraUsage;

    // Cost tracking
    [ObservableProperty]
    private string todayCostText = "--";

    [ObservableProperty]
    private string monthCostText = "--";

    [ObservableProperty]
    private bool hasCostData;

    // Utilization status for traffic-light indicators (multicc stacked view)
    [ObservableProperty]
    private string sessionStatusColor = "#10B981"; // Green default

    [ObservableProperty]
    private string weekStatusColor = "#10B981";

    [ObservableProperty]
    private string overallStatusText = "OK";

    [ObservableProperty]
    private string overallStatusColor = "#10B981";

    // Readable percentage text for multi-panel hero numbers (WCAG AA contrast on lavender)
    [ObservableProperty]
    private string sessionPercentText = "0%";

    [ObservableProperty]
    private string weekPercentText = "0%";

    [ObservableProperty]
    private string sessionPercentColor = "#047857";

    [ObservableProperty]
    private string weekPercentColor = "#047857";

    // Compact cost line for multicc stacked cards (e.g. "$4.20 today · $82.50 / 30d")
    [ObservableProperty]
    private string compactCostText = string.Empty;

    [ObservableProperty]
    private bool hasCompactCost;

    // Token tracking
    [ObservableProperty]
    private string todayTokensText = "--";

    [ObservableProperty]
    private string monthTokensText = "--";

    // Legacy properties for compatibility
    [ObservableProperty]
    private string sessionText = "--";

    [ObservableProperty]
    private string weekText = "--";

    [ObservableProperty]
    private string creditsText = "--";

    public static ProviderPulseViewModel FromReading(ProviderReading reading, string displayNameFallback)
    {
        var vm = new ProviderPulseViewModel
        {
            ProviderId = reading.Usage?.ProviderId ?? displayNameFallback,
            DisplayName = displayNameFallback,
            StatusSummary = FormatStatusSummary(reading),
            PlanText = reading.Identity?.Plan ?? "Max"
        };

        PopulateSessionMetrics(vm, reading);
        PopulateWeekMetrics(vm, reading);
        PopulateExtraUsage(vm, reading);
        PopulateCostData(vm, reading);

        // Set overall status based on the higher of session or week utilization
        var sessionPercent = vm.SessionProgress * 100.0;
        var weekPercent = vm.WeekProgress * 100.0;
        var worstPercent = Math.Max(sessionPercent, weekPercent);
        vm.OverallStatusColor = GetUtilizationColor(worstPercent);
        vm.OverallStatusText = GetStatusText(worstPercent);

        // Legacy fields
        vm.SessionText = FormatUsageRatio(reading.Usage?.SessionUsed, reading.Usage?.SessionLimit);
        vm.WeekText = FormatUsageRatio(reading.Usage?.WeekUsed, reading.Usage?.WeekLimit);
        vm.CreditsText = reading.Usage?.SpendingBucket?.Available.ToString("0.##") ?? "--";

        return vm;
    }

    private static void PopulateSessionMetrics(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var usage = reading.Usage;
        if (usage is null)
        {
            return;
        }

        var usedPercent = CalculateUsedPercent(usage.SessionUsed, usage.SessionLimit);
        vm.SessionProgress = usedPercent / 100.0;
        vm.SessionUsageLabel = FormatUsageLabel(usedPercent, usage.SessionUsed);

        // Reset text
        if (usage.SessionWindow?.ResetsAt is { } sessionResets)
        {
            vm.SessionResetText = $"Resets {UsageFormatter.ResetCountdown(sessionResets)}";

            // Pace calculation
            var pace = UsagePace.Calculate(
                usedPercent,
                sessionResets,
                usage.SessionWindow.Duration);

            if (pace is not null)
            {
                vm.SessionPaceText = UsageFormatter.FormatPace(pace) ?? string.Empty;
                vm.SessionPaceProgress = pace.ExpectedUsedPercent / 100.0;
                vm.SessionPaceOnTop = pace.DeltaPercent < 0; // Behind = pace marker above actual
            }
        }

        vm.SessionStatusColor = GetUtilizationColor(usedPercent);
        vm.SessionPercentText = $"{(int)Math.Round(usedPercent)}%";
        vm.SessionPercentColor = GetPercentTextColor(usedPercent);
    }

    private static void PopulateWeekMetrics(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var usage = reading.Usage;
        if (usage is null)
        {
            return;
        }

        var usedPercent = CalculateUsedPercent(usage.WeekUsed, usage.WeekLimit);
        vm.WeekProgress = usedPercent / 100.0;
        vm.WeekUsageLabel = FormatUsageLabel(usedPercent, usage.WeekUsed);

        // Reset text
        if (usage.WeekWindow?.ResetsAt is { } weekResets)
        {
            vm.WeekResetText = $"Resets {UsageFormatter.ResetCountdown(weekResets)}";

            // Pace calculation
            var pace = UsagePace.Calculate(
                usedPercent,
                weekResets,
                usage.WeekWindow.Duration);

            if (pace is not null)
            {
                vm.WeekPaceText = UsageFormatter.FormatPace(pace) ?? string.Empty;
                vm.WeekPaceProgress = pace.ExpectedUsedPercent / 100.0;
                vm.WeekPaceOnTop = pace.DeltaPercent < 0;
            }
        }

        vm.WeekStatusColor = GetUtilizationColor(usedPercent);
        vm.WeekPercentText = $"{(int)Math.Round(usedPercent)}%";
        vm.WeekPercentColor = GetPercentTextColor(usedPercent);
    }

    private static void PopulateExtraUsage(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var bucket = reading.Usage?.SpendingBucket;
        if (bucket is null)
        {
            vm.HasExtraUsage = false;
            vm.ExtraUsageLabel = "--";
            return;
        }

        vm.HasExtraUsage = true;

        switch (bucket.Kind)
        {
            case BucketKind.OverageSpend:
                // Claude-style: show spent / ceiling
                vm.ExtraUsageLabel = $"Overage: {bucket.CurrencySymbol}{bucket.Consumed:F2} / {bucket.CurrencySymbol}{bucket.Ceiling:F2}";
                vm.ExtraUsageProgress = bucket.FillRatio;
                break;

            case BucketKind.PrepaidBalance:
                // Codex-style: show remaining balance
                vm.ExtraUsageLabel = $"Balance: {bucket.CurrencySymbol}{bucket.Available:F2} remaining";
                vm.ExtraUsageProgress = 0; // No progress bar for prepaid
                break;
        }
    }

    private static void PopulateCostData(ProviderPulseViewModel vm, ProviderReading reading)
    {
        var consumption = reading.Usage?.Consumption;
        if (consumption is null || (consumption.TodayTokens.TotalConsumed == 0 && consumption.RollingWindowTokens.TotalConsumed == 0))
        {
            vm.HasCostData = false;
            return;
        }

        vm.HasCostData = true;

        // Today's consumption
        var todayTokens = consumption.TodayTokens.TotalConsumed;
        var todayCost = consumption.TodayCostUsd;
        vm.TodayCostText = UsageFormatter.FormatCurrency(todayCost);
        vm.TodayTokensText = UsageFormatter.FormatTokenCount(todayTokens);

        // Rolling window consumption
        var windowTokens = consumption.RollingWindowTokens.TotalConsumed;
        var windowCost = consumption.RollingWindowCostUsd;
        vm.MonthCostText = UsageFormatter.FormatCurrency(windowCost);
        vm.MonthTokensText = UsageFormatter.FormatTokenCount(windowTokens);

        // Compact single-line cost for stacked multicc cards
        var todayFormatted = UsageFormatter.FormatCurrency(todayCost);
        var monthFormatted = UsageFormatter.FormatCurrency(windowCost);
        vm.CompactCostText = $"{todayFormatted} today  ·  {monthFormatted} / 30d";
        vm.HasCompactCost = true;
    }

    private static double CalculateUsedPercent(long? used, long? limit)
    {
        if (used is null)
        {
            return 0;
        }

        // If limit is 100, the "used" value IS the percentage directly
        // This happens when we get percentage data from CLI probe
        if (limit == 100)
        {
            return Math.Clamp(used.Value, 0, 100);
        }

        if (limit is null || limit <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)used.Value / limit.Value * 100, 0, 100);
    }

    private static string FormatUsageLabel(double usedPercent, long? used)
    {
        if (used is null || used == 0)
        {
            return "0% used";
        }

        return $"{(int)Math.Round(usedPercent)}% used";
    }

    private static string FormatUsageRatio(long? used, long? limit)
    {
        if (used is null && limit is null)
        {
            return "--";
        }

        if (limit is null)
        {
            return used?.ToString() ?? "--";
        }

        return $"{used ?? 0}/{limit.Value}";
    }

    private static string FormatStatusSummary(ProviderReading reading)
    {
        if (reading.StatusSummary is not null)
        {
            return reading.StatusSummary;
        }

        return reading.Source switch
        {
            ReadingSource.LocalLog => $"Updated {UsageFormatter.FormatRelativeTime(reading.CapturedAt)}",
            ReadingSource.Api => "API",
            ReadingSource.Cli => "CLI",
            _ => "No data"
        };
    }

    private static string GetUtilizationColor(double percent)
    {
        return percent switch
        {
            >= 95 => "#EF4444",  // Red - at/over limit
            >= 80 => "#F97316",  // Orange - near limit
            >= 50 => "#F59E0B",  // Amber - moderate
            _     => "#10B981",  // Green - healthy
        };
    }

    private static string GetStatusText(double percent)
    {
        return percent switch
        {
            >= 95 => "At limit",
            >= 80 => "Near limit",
            >= 50 => "Moderate",
            _     => "OK",
        };
    }

    /// <summary>
    /// Returns WCAG AA-compliant text colors for percentage hero numbers on lavender background.
    /// Darker variants of the bar colors ensure 4.5:1+ contrast ratio.
    /// </summary>
    private static string GetPercentTextColor(double percent)
    {
        return percent switch
        {
            >= 95 => "#DC2626",  // Red-600 (~6.5:1 on lavender)
            >= 80 => "#C2410C",  // Orange-700 (~6.0:1)
            >= 50 => "#B45309",  // Amber-700 (~5.4:1)
            _     => "#047857",  // Emerald-700 (~4.6:1)
        };
    }
}
