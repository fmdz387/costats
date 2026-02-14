using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App
{
    public partial class GlassWidgetWindow : Window
    {
        private readonly IGlassBackdropService _backdropService;

        public GlassWidgetWindow(PulseViewModel viewModel, IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _backdropService = backdropService;
            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            Deactivated += OnDeactivated;

            // Subscribe to ViewModel property changes for dynamic height
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // Skip backdrop - we use AllowsTransparency with custom Border for rounded corners
            // Applying DWM backdrop creates a conflicting layer with different corner radius
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging the window, but only if clicking on the background (not on buttons/controls)
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Controls.Border or System.Windows.Controls.Grid or Window)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // DragMove can throw if called at wrong time
                }
            }
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            // Hide window when it loses focus (like a popup)
            Hide();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PulseViewModel.IsMulticcActive) or
                nameof(PulseViewModel.SelectedTabIndex))
            {
                UpdateWindowHeight();
            }
        }

        private void UpdateWindowHeight()
        {
            var vm = DataContext as PulseViewModel;
            if (vm is null) return;

            if (vm.IsMulticcActive && vm.SelectedTabIndex == 1)
            {
                var profileCount = vm.ClaudeProfiles.Count;
                const double baseHeight = 280.0;   // Header + tabs + summary bar
                const double summaryBarHeight = 40.0;
                const double perCardHeight = 90.0;
                const double footerHeight = 80.0;
                var totalHeight = baseHeight + summaryBarHeight + (profileCount * perCardHeight) + footerHeight;
                Height = Math.Min(Math.Max(totalHeight, 580.0), 900.0);
            }
            else
            {
                Height = 580.0;
            }
        }

        private void OnQuitClick(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
