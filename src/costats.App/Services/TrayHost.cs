using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using costats.App.ViewModels;
using costats.Application.Pulse;
using costats.Core.Pulse;
using Microsoft.Win32;
using Serilog;

namespace costats.App.Services
{
    public sealed class TrayHost : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly TaskbarIcon _taskbarIcon;
        private readonly GlassWidgetWindow _widgetWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly IPulseOrchestrator _pulseOrchestrator;
        private readonly PulseViewModel _viewModel;
        private readonly TaskbarPositionService _taskbarPosition;

        public TrayHost(
            PulseViewModel viewModel,
            GlassWidgetWindow widgetWindow,
            SettingsWindow settingsWindow,
            IPulseOrchestrator pulseOrchestrator,
            TaskbarPositionService taskbarPosition)
        {
            _viewModel = viewModel;
            _widgetWindow = widgetWindow;
            _settingsWindow = settingsWindow;
            _pulseOrchestrator = pulseOrchestrator;
            _taskbarPosition = taskbarPosition;

            _taskbarIcon = new TaskbarIcon();
            _taskbarIcon.Icon = CreateIcon();
            _taskbarIcon.ToolTipText = "costats";
            _taskbarIcon.ContextMenu = BuildContextMenu();
            _taskbarIcon.TrayLeftMouseUp += OnTrayLeftClick;
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _widgetWindow.SizeChanged += OnWidgetSizeChanged;
        }

        private void OnTrayLeftClick(object? sender, EventArgs e)
        {
            ToggleWidget();
        }

        private static Icon CreateIcon()
        {
            try
            {
                // Load icon from embedded resource
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "costats.App.Resources.tray-icon.ico";

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    return new Icon(stream);
                }
            }
            catch
            {
                // Fall through to fallback
            }

            // Fallback: create a simple colored icon programmatically
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(99, 102, 241)); // Indigo
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            using var pen = new Pen(Color.White, 3);
            g.DrawLine(pen, 10, 22, 10, 14);
            g.DrawLine(pen, 16, 22, 16, 10);
            g.DrawLine(pen, 22, 22, 22, 16);

            var hIcon = bitmap.GetHicon();
            using var tempIcon = Icon.FromHandle(hIcon);
            var clonedIcon = (Icon)tempIcon.Clone();
            DestroyIcon(hIcon);
            return clonedIcon;
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var showItem = new MenuItem { Header = "Show Widget", FontWeight = FontWeights.SemiBold };
            showItem.Click += (_, _) => ShowWidget();

            var refreshItem = new MenuItem { Header = "Refresh Now" };
            refreshItem.Click += async (_, _) => await _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

            var settingsItem = new MenuItem { Header = "Settings..." };
            settingsItem.Click += (_, _) => ShowSettings();

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);
            return menu;
        }

        public void ShowSettings()
        {
            // Center on screen
            var workArea = SystemParameters.WorkArea;
            _settingsWindow.Left = (workArea.Width - _settingsWindow.Width) / 2 + workArea.Left;
            _settingsWindow.Top = (workArea.Height - _settingsWindow.Height) / 2 + workArea.Top;

            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
        }

        public void ShowWidget()
        {
            PositionWidget();

            var wasVisible = _widgetWindow.IsVisible;

            if (!wasVisible)
            {
                _widgetWindow.Show();
            }

            _widgetWindow.Activate();

            // Silent refresh for the currently selected provider when panel opens
            if (!wasVisible)
            {
                _ = RefreshSelectedProviderAsync().ContinueWith(
                    t => Log.Warning(t.Exception!.GetBaseException(), "Silent provider refresh failed"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        private Task RefreshSelectedProviderAsync()
        {
            return _viewModel.RefreshSelectedProviderSilentlyAsync();
        }

        public void HideWidget()
        {
            _widgetWindow.Hide();
        }

        public void ToggleWidget()
        {
            if (_widgetWindow.IsVisible)
            {
                HideWidget();
            }
            else
            {
                ShowWidget();
            }
        }

        public void Dispose()
        {
            _widgetWindow.SizeChanged -= OnWidgetSizeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _taskbarIcon.Dispose();
            _widgetWindow.Close();
            _settingsWindow.Close();
        }

        private void OnWidgetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
        }

        private void PositionWidget()
        {
            var position = _taskbarPosition.GetWidgetPosition(_widgetWindow.Width, _widgetWindow.Height, 12);
            _widgetWindow.Left = position.X;
            _widgetWindow.Top = position.Y;
        }
    }
}
