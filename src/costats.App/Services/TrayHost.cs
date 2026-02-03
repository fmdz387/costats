using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using costats.App.ViewModels;
using costats.Application.Pulse;

namespace costats.App.Services
{
    public sealed class TrayHost : IDisposable
    {
        private readonly TaskbarIcon _taskbarIcon;
        private readonly GlassWidgetWindow _widgetWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly IPulseOrchestrator _pulseOrchestrator;

        public TrayHost(
            PulseViewModel viewModel,
            GlassWidgetWindow widgetWindow,
            SettingsWindow settingsWindow,
            IPulseOrchestrator pulseOrchestrator)
        {
            _widgetWindow = widgetWindow;
            _settingsWindow = settingsWindow;
            _pulseOrchestrator = pulseOrchestrator;

            _taskbarIcon = new TaskbarIcon();
            _taskbarIcon.Icon = CreateIcon();
            _taskbarIcon.ToolTipText = "costats";
            _taskbarIcon.ContextMenu = BuildContextMenu();
            _taskbarIcon.TrayLeftMouseUp += OnTrayLeftClick;
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
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

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var showItem = new MenuItem { Header = "Show Widget", FontWeight = FontWeights.SemiBold };
            showItem.Click += (_, _) => ShowWidget();

            var refreshItem = new MenuItem { Header = "Refresh Now" };
            refreshItem.Click += async (_, _) => await _pulseOrchestrator.RefreshOnceAsync(CancellationToken.None);

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
            // Position near the system tray (bottom-right)
            var workArea = SystemParameters.WorkArea;
            _widgetWindow.Left = workArea.Right - _widgetWindow.Width - 12;
            _widgetWindow.Top = workArea.Bottom - _widgetWindow.Height - 12;

            if (!_widgetWindow.IsVisible)
            {
                _widgetWindow.Show();
            }

            _widgetWindow.Activate();
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
            _taskbarIcon.Dispose();
            _widgetWindow.Close();
            _settingsWindow.Close();
        }
    }
}
