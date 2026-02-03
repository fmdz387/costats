using System.Windows;
using costats.App.Services;
using costats.App.ViewModels;
using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Application.Shell;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Pulse;
using costats.Infrastructure.Security;
using costats.Infrastructure.Settings;
using costats.Infrastructure.Time;
using costats.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace costats.App
{
    public partial class App : System.Windows.Application
    {
        private IHost? _host;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(config =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog((context, services, loggerConfig) =>
                {
                    loggerConfig
                        .ReadFrom.Configuration(context.Configuration)
                        .Enrich.FromLogContext();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                    services.AddSingleton(sp =>
                        sp.GetRequiredService<ISettingsStore>()
                            .LoadAsync(CancellationToken.None)
                            .GetAwaiter()
                            .GetResult());

                    services.AddOptions<PulseOptions>()
                        .Configure<AppSettings>((options, settings) =>
                        {
                            var minutes = Math.Max(1, settings.RefreshMinutes);
                            options.RefreshInterval = TimeSpan.FromMinutes(minutes);
                        });

                    services.AddSingleton<IClock, SystemClock>();

                    services.AddSingleton<PulseBroadcaster>();
                    services.AddSingleton<ISourceSelector, SourceSelector>();
                    services.AddSingleton<ISignalSource, CodexLogSource>();
                    services.AddSingleton<ISignalSource, ClaudeLogSource>();
                    services.AddSingleton<IPulseSnapshotWriter, JsonPulseSnapshotWriter>();
                    services.AddSingleton<IPulseOrchestrator, PulseOrchestrator>();
                    services.AddHostedService(sp => (PulseOrchestrator)sp.GetRequiredService<IPulseOrchestrator>());

                    services.AddSingleton<ICredentialVault, CredentialVault>();
                    services.AddSingleton<IGlassBackdropService, GlassBackdropService>();

                    services.AddSingleton<PulseViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<GlassWidgetWindow>();
                    services.AddSingleton<SettingsWindow>();
                    services.AddSingleton<TrayHost>();
                    services.AddSingleton<HotkeyService>();
                })
                .Build();

            try
            {
                _host.Start();

                var tray = _host.Services.GetRequiredService<TrayHost>();
                _ = _host.Services.GetRequiredService<HotkeyService>();

                // Show widget on first launch, then use tray/hotkey
                tray.ShowWidget();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Startup error: {ex.Message}\n\n{ex.StackTrace}",
                    "costats Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override async void OnExit(System.Windows.ExitEventArgs e)
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
