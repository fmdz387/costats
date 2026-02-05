using System.Windows;
using costats.App.Services;
using costats.App.Services.Updates;
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
        private SingleInstanceCoordinator? _singleInstance;
        private StartupUpdateCoordinator? _updateCoordinator;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            RegisterExceptionHandlers();

            _singleInstance = new SingleInstanceCoordinator("costats");
            if (!_singleInstance.IsPrimary)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SingleInstanceCoordinator.SignalPrimaryAsync(
                            _singleInstance.PipeName,
                            ActivationMessage.ShowWidget,
                            TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Ignore activation errors on secondary instances.
                    }
                    finally
                    {
                        Dispatcher.Invoke(() => Shutdown(0));
                    }
                });
                return;
            }

            _ = InitializeAsync();
        }

        protected override async void OnExit(System.Windows.ExitEventArgs e)
        {
            try
            {
                if (_host is not null)
                {
                    await _host.StopAsync();
                    _host.Dispose();
                }
            }
            catch
            {
                // Ignore shutdown failures.
            }

            _singleInstance?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private async Task InitializeAsync()
        {
            try
            {
                var startupConfiguration = BuildStartupConfiguration();
                _updateCoordinator = new StartupUpdateCoordinator(UpdateOptions.FromConfiguration(startupConfiguration));
                if (await _updateCoordinator.TryApplyPendingUpdateAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await Dispatcher.InvokeAsync(() => Shutdown(0));
                    return;
                }

                var settingsStore = new JsonSettingsStore();
                var settings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    var tray = InitializeHost(settingsStore, settings);
                    _ = StartListenerAsync(tray);
                    tray.ShowWidget();
                });

                if (_updateCoordinator is not null)
                {
                    _ = Task.Run(() => _updateCoordinator.CheckAndStageUpdateAsync(CancellationToken.None));
                }
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

        private static IConfiguration BuildStartupConfiguration()
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
                .Build();
        }

        private void RegisterExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                // Mark as handled to prevent app crash - log and continue
                args.Handled = true;
                // Only shutdown for truly fatal errors, not routine exceptions
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    Log.Error(ex, "Unhandled domain exception");
                }
            };
        }

        private TrayHost InitializeHost(ISettingsStore settingsStore, AppSettings settings)
        {
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
                    services.AddSingleton<ISettingsStore>(settingsStore);
                    services.AddSingleton(settings);

                    services.AddOptions<PulseOptions>()
                        .Configure<AppSettings>((options, appSettings) =>
                        {
                            var minutes = Math.Max(1, appSettings.RefreshMinutes);
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
                    services.AddSingleton<TaskbarPositionService>();
                    services.AddSingleton<TrayHost>();
                    services.AddSingleton<HotkeyService>();
                })
                .Build();

            _host.Start();

            _ = _host.Services.GetRequiredService<HotkeyService>();
            return _host.Services.GetRequiredService<TrayHost>();
        }

        private async Task StartListenerAsync(TrayHost tray)
        {
            if (_singleInstance is null)
            {
                return;
            }

            await _singleInstance.StartListenerAsync(async message =>
            {
                if (message == ActivationMessage.ShowWidget)
                {
                    await Dispatcher.InvokeAsync(() => tray.ShowWidget());
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
