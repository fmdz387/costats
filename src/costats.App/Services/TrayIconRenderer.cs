using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using WpfColor = System.Windows.Media.Color;
using WpfGeometry = System.Windows.Media.Geometry;

namespace costats.App.Services;

/// <summary>
/// Renders a provider logo + two usage-bar rows into a GDI <see cref="System.Drawing.Icon"/>.
/// Must be called on the WPF UI thread (uses DrawingVisual).
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Renders a 32×32 tray icon with the provider geometry in the upper portion
    /// and two horizontal usage bars (session + weekly) at the bottom.
    /// </summary>
    /// <param name="providerGeometry">WPF Geometry for the provider logo.</param>
    /// <param name="accentColor">Fill color for the logo and bar fills.</param>
    /// <param name="sessionPct">Session usage 0–100.</param>
    /// <param name="weekPct">Weekly usage 0–100.</param>
    /// <param name="size">Icon size in pixels (default 32).</param>
    public static Icon? Render(
        WpfGeometry providerGeometry,
        WpfColor accentColor,
        double sessionPct,
        double weekPct,
        int size = 32)
    {
        try
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // --- Logo layer: fills size × (size-8) area ---
                var logoBounds = new Rect(0, 0, size, size - 8);
                var transform = GetFitTransform(providerGeometry.Bounds, logoBounds);
                dc.PushTransform(transform);
                dc.DrawGeometry(new SolidColorBrush(accentColor), null, providerGeometry);
                dc.Pop();

                // --- Bar layer: bottom 8 pixels ---
                byte trackAlpha = 77; // ~30% of 255
                var trackBrush = new SolidColorBrush(
                    WpfColor.FromArgb(trackAlpha, accentColor.R, accentColor.G, accentColor.B));
                var fillBrush = new SolidColorBrush(accentColor);

                // Session bar: y=size-7, height=3
                int barY1 = size - 7;
                dc.DrawRectangle(trackBrush, null, new Rect(0, barY1, size, 3));
                double sessionW = Math.Max(0, size * Math.Clamp(sessionPct, 0, 100) / 100.0);
                if (sessionW > 0)
                    dc.DrawRectangle(fillBrush, null, new Rect(0, barY1, sessionW, 3));

                // Weekly bar: y=size-3, height=3
                int barY2 = size - 3;
                dc.DrawRectangle(trackBrush, null, new Rect(0, barY2, size, 3));
                double weekW = Math.Max(0, size * Math.Clamp(weekPct, 0, 100) / 100.0);
                if (weekW > 0)
                    dc.DrawRectangle(fillBrush, null, new Rect(0, barY2, weekW, 3));
            }

            // Render to bitmap
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            // Convert WPF BitmapSource → System.Drawing.Icon
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Seek(0, SeekOrigin.Begin);

            using var bmp = new System.Drawing.Bitmap(ms);
            var hIcon = bmp.GetHicon();
            var temp = Icon.FromHandle(hIcon);
            var icon = (Icon)temp.Clone();
            temp.Dispose(); // disposes wrapper object only; HICON owned by us
            DestroyIcon(hIcon);
            return icon;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TrayIconRenderer failed to render icon");
            return null;
        }
    }

    /// <summary>
    /// Computes a scale+translate transform that fits <paramref name="source"/> bounds
    /// into <paramref name="dest"/> while preserving aspect ratio and centering.
    /// </summary>
    private static System.Windows.Media.Transform GetFitTransform(Rect source, Rect dest)
    {
        if (source.IsEmpty || source.Width == 0 || source.Height == 0)
            return System.Windows.Media.Transform.Identity;

        double scaleX = dest.Width / source.Width;
        double scaleY = dest.Height / source.Height;
        double scale = Math.Min(scaleX, scaleY);

        double offsetX = dest.X + (dest.Width - source.Width * scale) / 2 - source.X * scale;
        double offsetY = dest.Y + (dest.Height - source.Height * scale) / 2 - source.Y * scale;

        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(scale, scale));
        tg.Children.Add(new TranslateTransform(offsetX, offsetY));
        return tg;
    }
}
