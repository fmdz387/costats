using System.Windows.Input;
using costats.Application.Settings;
using Microsoft.Extensions.Logging;
using NHotkey.Wpf;

namespace costats.App.Services
{
    public sealed class HotkeyService : IDisposable
    {
        private const string HotkeyName = "ToggleWidget";
        private readonly ILogger<HotkeyService> _logger;

        public HotkeyService(TrayHost trayHost, AppSettings settings, ILogger<HotkeyService> logger)
        {
            _logger = logger;

            try
            {
                var (key, modifiers) = ParseHotkey(settings.Hotkey);
                HotkeyManager.Current.AddOrReplace(
                    HotkeyName,
                    key,
                    modifiers,
                    (_, _) => trayHost.ToggleWidget());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register hotkey");
            }
        }

        public void Dispose()
        {
            try
            {
                HotkeyManager.Current.Remove(HotkeyName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to unregister hotkey");
            }
        }

        private static (Key key, ModifierKeys modifiers) ParseHotkey(string? hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                return (Key.U, ModifierKeys.Control | ModifierKeys.Alt);
            }

            var modifiers = ModifierKeys.None;
            Key key = Key.None;

            foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = part.Trim();
                if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Control;
                    continue;
                }

                if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Alt;
                    continue;
                }

                if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Shift;
                    continue;
                }

                if (token.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Windows;
                    continue;
                }

                if (!Enum.TryParse(token, true, out key))
                {
                    key = Key.None;
                }
            }

            if (key == Key.None)
            {
                key = Key.U;
            }

            if (modifiers == ModifierKeys.None)
            {
                modifiers = ModifierKeys.Control | ModifierKeys.Alt;
            }

            return (key, modifiers);
        }
    }
}
