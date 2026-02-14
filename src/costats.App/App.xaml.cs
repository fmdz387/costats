using System.IO;
using System.Reflection;
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
using Microsoft.Extensions.Logging.Abstractions;
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

            BootstrapEarlyLogger();
            RegisterExceptionHandlers();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Log.Information("costats starting (v{Version}, PID {Pid})", version, Environment.ProcessId);

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
            Log.Information("Application exiting (ExitCode={ExitCode})", e.ApplicationExitCode);
            try
            {
                if (_host is not null)
                {
                    await _host.StopAsync();
                    _host.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error during host shutdown");
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
                    Log.Information("Pending update is being applied, shutting down for update");
                    await Dispatcher.InvokeAsync(() => Shutdown(0));
                    return;
                }

                var settingsStore = new JsonSettingsStore();
                var settings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    var tray = InitializeHost(settingsStore, settings);
                    LogFireAndForget(StartListenerAsync(tray), "SingleInstanceListener");
                    tray.ShowWidget();
                });

                if (_updateCoordinator is not null)
                {
                    // Use a timeout so a stalled download never holds the semaphore forever
                    var backgroundCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    LogFireAndForget(
                        Task.Run(() => _updateCoordinator.CheckAndStageUpdateAsync(backgroundCts.Token)),
                        "UpdateCheck");
                }

            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Startup failed");
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

        /// <summary>
        /// Bootstraps Serilog before the host is built so that startup and
        /// exception-handler logs reach the file sink even if host init fails.
        /// The host builder replaces this logger with the fully-configured one.
        /// </summary>
        private static void BootstrapEarlyLogger()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "costats", "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(logDir, "costats-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .Enrich.FromLogContext()
                .CreateLogger();
        }

        private void RegisterExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                args.Handled = true;
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
                    Log.Fatal(ex, "Unhandled domain exception (IsTerminating={IsTerminating})", args.IsTerminating);
                    Log.CloseAndFlush();
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Log.CloseAndFlush();
            };
        }

        /// <summary>
        /// Observes a fire-and-forget task so exceptions are logged instead
        /// of silently swallowed or deferred to the finalizer.
        /// </summary>
        private static void LogFireAndForget(Task task, string operationName)
        {
            task.ContinueWith(
                t => Log.Error(t.Exception!.GetBaseException(), "Background task {Operation} faulted", operationName),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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

                    if (_updateCoordinator is not null)
                    {
                        services.AddSingleton(_updateCoordinator);
                    }

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
                    // Multicc integration: conditionally register per-profile or default Claude source
                    services.AddSingleton<MulticcConfigReader>();

                    var tempReader = new MulticcConfigReader(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<MulticcConfigReader>.Instance);
                    var discovery = new MulticcDiscoveryService(tempReader, settings.MulticcConfigPath);

                    services.AddSingleton<IMulticcDiscovery>(discovery);

                    if (settings.MulticcEnabled && discovery.IsDetected && discovery.Profiles.Count > 0)
                    {
                        if (settings.MulticcSelectedProfile is not null)
                        {
                            // Single-profile mode: register one source for the selected profile
                            var selected = discovery.Profiles
                                .FirstOrDefault(p => p.Name.Equals(settings.MulticcSelectedProfile, StringComparison.OrdinalIgnoreCase));

                            if (selected is not null)
                            {
                                services.AddSingleton<ISignalSource>(new MulticcClaudeLogSource(selected));
                            }
                            else
                            {
                                // Fallback to default Claude source if selected profile not found
                                services.AddSingleton<ISignalSource, ClaudeLogSource>();
                            }
                        }
                        else
                        {
                            // Stacked mode: register one source per profile
                            foreach (var profile in discovery.Profiles)
                            {
                                services.AddSingleton<ISignalSource>(new MulticcClaudeLogSource(profile));
                            }
                        }
                    }
                    else
                    {
                        // No multicc or disabled: use default Claude source
                        services.AddSingleton<ISignalSource, ClaudeLogSource>();
                    }
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

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Log.Warning("Host is stopping"));

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
