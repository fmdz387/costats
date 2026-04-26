using System.Windows;
using System.Windows.Input;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel viewModel, IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
        }


        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private async void OnSaveCopilotTokenClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                var token = CopilotTokenBox.Password;
                await viewModel.SaveCopilotTokenAsync(token);
            }
        }
    }
}
