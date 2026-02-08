namespace costats.App.Services.Updates;

public enum UpdateCheckResult
{
    UpToDate,
    UpdateStaged,
    UpdateAlreadyStaged,
    Skipped,
    Disabled,
    AlreadyRunning,
    CheckFailed
}
