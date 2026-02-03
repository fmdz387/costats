using System.Runtime.InteropServices;
using costats.Application.Shell;

namespace costats.Infrastructure.Windows;

public sealed class GlassBackdropService : IGlassBackdropService
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public void ApplyBackdrop(IntPtr hwnd)
    {
        if (!IsSupported || hwnd == IntPtr.Zero)
            return;

        // Apply rounded corners on Windows 11
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int cornerPreference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }

        // Apply backdrop effect
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            ApplyWindows11Backdrop(hwnd);
        }
        else
        {
            ApplyWindows10Blur(hwnd);
        }
    }

    private static void ApplyWindows11Backdrop(IntPtr hwnd)
    {
        // Use Acrylic for popup-style windows
        int backdropType = DWMSBT_TRANSIENTWINDOW; // Acrylic
        int result = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

        if (result != 0)
        {
            // Fallback to Mica
            backdropType = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }
    }

    private static void ApplyWindows10Blur(IntPtr hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_BLURBEHIND,
            AccentFlags = 2,
            GradientColor = 0x01FFFFFF, // Very transparent
            AnimationId = 0
        };

        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    // Windows 11 constants
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    // Windows 10 constants
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
