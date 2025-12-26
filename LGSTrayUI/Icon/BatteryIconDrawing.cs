using Hardcodet.Wpf.TaskbarNotification;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayUI.Properties;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.IO;

namespace LGSTrayUI;
/// <summary>
/// Provides static methods for drawing and updating battery and device icons for taskbar notifications.
/// </summary>
/// <remarks>This class offers utility methods to render battery status and device icons, including numeric and
/// unknown states, for use with taskbar notification icons. It adapts icon appearance based on the current Windows
/// theme and device charging status.
/// <br> All members are static and thread safety is not guaranteed; callers should ensure
/// thread safety if accessing from multiple threads.</br></remarks>
public static partial class BatteryIconDrawing
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    private static Bitmap Mouse => CheckTheme.LightTheme ? Resources.Mouse : Resources.Mouse_dark;
    private static Bitmap Keyboard => CheckTheme.LightTheme ? Resources.Keyboard : Resources.Keyboard_dark;
    private static Bitmap Headset => CheckTheme.LightTheme ? Resources.Headset : Resources.Headset_dark;
    private static Bitmap Battery => CheckTheme.LightTheme ? Resources.Battery : Resources.Battery_dark;
    private static Bitmap Missing => CheckTheme.LightTheme ? Resources.Missing : Resources.Missing_dark;
    private static Bitmap Charging => CheckTheme.LightTheme ? Resources.Charging : Resources.Charging_dark;

   // private static int ImageSize;
    private readonly static float Scale;

    static BatteryIconDrawing()
    {
        Scale = DetectDpiScale();
    }

    private static float DetectDpiScale()
    {
        int dpi = 96; // Default DPI

        try
        {
            // Method 1: Try registry (may not exist on all Windows 10 configs)
            try
            {
                using var reg = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ThemeManager", false);
                var dpiString = reg?.GetValue("LastLoadedDPI") as string;

                if (!string.IsNullOrEmpty(dpiString) && int.TryParse(dpiString, out int regDpi) && regDpi > 0)
                {
                    DiagnosticLogger.Log($"DPI from registry: {regDpi}");
                    return regDpi / 96f;
                }
            }
            catch (Exception regEx)
            {
                DiagnosticLogger.LogWarning($"Registry DPI detection failed: {regEx.Message}");
            }

            // Method 2: Use GDI GetDeviceCaps (works on all Windows versions)
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            dpi = (int)graphics.DpiX;
            DiagnosticLogger.Log($"DPI from GDI: {dpi}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogWarning($"DPI detection failed, using default: {ex.Message}");
            dpi = 96;
        }

        DiagnosticLogger.Log($"Icon scale factor: {dpi / 96f}");
        return dpi / 96f;
    }

    private static Bitmap GetDeviceIcon(LogiDevice device) => device.DeviceType switch
    {
        DeviceType.Keyboard => Keyboard,
        DeviceType.Headset => Headset,
        _ => Mouse,
    };

    private static System.Drawing.Color GetDeviceColor(LogiDevice device) => device.DeviceType switch
    {
        _ => CheckTheme.LightTheme ? System.Drawing.Color.FromArgb(0x11, 0x11, 0x11) : System.Drawing.Color.White

    //return device.DeviceType switch
    //{
    //    DeviceType.Keyboard => Color.FromArgb(0xA1, 0xE4, 0x4D),
    //    DeviceType.Headset => Color.FromArgb(0xFA, 0x79, 0x21),
    //    _ => Color.FromArgb(0xBB, 0x86, 0xFC),
    //};
    };

    private static Bitmap GetBatteryValue(LogiDevice device) => device.BatteryPercentage switch
    {
        < 0 => Missing,
        < 10 => Resources.Indicator_10,
        < 50 => Resources.Indicator_30,
        < 85 => Resources.Indicator_50,
        _ => Resources.Indicator_100
    };

    public static void DrawUnknown(TaskbarIcon taskbarIcon)
    {
        DrawIcon(taskbarIcon, new()
        {
            BatteryPercentage = -1,
        });
    }

    public static void DrawIcon(TaskbarIcon taskbarIcon, LogiDevice device)
    {
        var ImageSize = (int)(96 * Scale);
        var destRect = new Rectangle(0, 0, ImageSize, ImageSize);
        using var b = new Bitmap(ImageSize, ImageSize);
        using var g = Graphics.FromImage(b);
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality; 

        using var wrapMode = new ImageAttributes();
        wrapMode.SetWrapMode(WrapMode.TileFlipXY);

        Bitmap[] layers = [
            GetBatteryValue(device),
            Battery,
            GetDeviceIcon(device),
        ];

        foreach (var image in layers)
        {
            g.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
            image.Dispose();
        }

        // Overlay charging indicator if device is charging
        if (device.PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING)
        {
            // Use theme-appropriate charging icon
            Bitmap chargingOverlay = Charging;

            // Scale overlay
            int overlaySize = (int)(ImageSize * 1.50);

            // Position at bottom-center corner with small margin                
            int marginLeft = -(int)(ImageSize * 0.30);
            int marginTop = (int)(ImageSize * 0.50);
            int x = ImageSize - overlaySize + marginLeft;
            int y = ImageSize - overlaySize + marginTop;


            Rectangle overlayRect = new(x, y, overlaySize, overlaySize);

            //Create ColorMatrix to render charging icon
            //using var blackAttributes = new ImageAttributes();
            //ColorMatrix colorMatrix = new ColorMatrix(new float[][]
            //{
            //    new float[] {0, 0, 0, 0, 0},        // Red channel 
            //    new float[] {0, 0, 0, 0, 0},        // Green channel 
            //    new float[] {0, 0, 255, 0, 0},        // Blue channel 
            //    new float[] {0, 0, 0, 1, 0},        // Alpha channel (preserve)
            //    new float[] {0, 0, 0, 0, 1}         // Translation
            //});
            //blackAttributes.SetColorMatrix(colorMatrix);

            g.DrawImage(chargingOverlay, overlayRect, 0, 0,
                        chargingOverlay.Width, chargingOverlay.Height,
                        GraphicsUnit.Pixel);

            chargingOverlay.Dispose();
        }

        g.Save();

        IntPtr iconHandle = b.GetHicon();
        Icon tempManagedRes = Icon.FromHandle(iconHandle);
        taskbarIcon.Icon = (Icon)tempManagedRes.Clone();
        tempManagedRes.Dispose();
        DestroyIcon(iconHandle);
    }

    public static void DrawNumeric(TaskbarIcon taskbarIcon, LogiDevice device)
    {
        var ImageSize = (int)(92 * Scale);
        
        // Create WPF visual for high-quality text rendering
        DrawingVisual drawingVisual = new();
        // Better text rendering:       
        TextOptions.SetTextRenderingMode(drawingVisual, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(drawingVisual, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(drawingVisual, TextHintingMode.Fixed);

        using (DrawingContext drawingContext = drawingVisual.RenderOpen())
        {
            // Fill with green background if charging
            if (device.PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING)
            {
                System.Windows.Media.Color bgColor = CheckTheme.LightTheme
                    ? System.Windows.Media.Color.FromRgb(50, 205, 50)  // LimeGreen
                    : System.Windows.Media.Color.FromRgb(0, 100, 0);    // DarkGreen
                drawingContext.DrawRectangle(
                    new SolidColorBrush(bgColor),
                    null,
                    new Rect(0, 0, ImageSize, ImageSize));
            }

            // Prepare text
            string displayString = (device.BatteryPercentage < 0) ? "?" : $"{device.BatteryPercentage:f0}";
            //displayString = "100";

            // Get device color
            var deviceColor = GetDeviceColor(device);
            var wpfColor = System.Windows.Media.Color.FromRgb(deviceColor.R, deviceColor.G, deviceColor.B);
            
            // Create formatted text with WPF's superior text rendering
            FormattedText formattedText = new (
                displayString,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    new System.Windows.Media.FontFamily("Segoe UI Variable Display"),
                    FontStyles.Normal,
                    FontWeights.SemiBold,
                    FontStretches.Normal, 
                    fallbackFontFamily: new System.Windows.Media.FontFamily("Segoe UI")),
                .7 * ImageSize,
                new SolidColorBrush(wpfColor),
                VisualTreeHelper.GetDpi(drawingVisual).PixelsPerDip);

            RenderOptions.SetClearTypeHint(drawingVisual, ClearTypeHint.Auto);
            TextOptions.SetTextFormattingMode(drawingVisual, TextFormattingMode.Display);
            // Center the text
            double x = (ImageSize - formattedText.Width) / 2.0;
            double y = (ImageSize - formattedText.Height) / 2.0;
            
            drawingContext.DrawText(formattedText, new System.Windows.Point(x, y));
        }

        // Render to bitmap using WPF pipeline
        RenderTargetBitmap renderBitmap = new (ImageSize, ImageSize, 96, 96, PixelFormats.Pbgra32);
        renderBitmap.Render(drawingVisual);

        // Convert WPF bitmap to GDI+ bitmap
        using Bitmap b = ConvertToBitmap(renderBitmap);

        IntPtr iconHandle = b.GetHicon();
        Icon tempManagedRes = Icon.FromHandle(iconHandle);
        taskbarIcon.Icon = (Icon)tempManagedRes.Clone();
        tempManagedRes.Dispose();
        DestroyIcon(iconHandle);
    }

    private static Bitmap ConvertToBitmap(BitmapSource bitmapSource)
    {
        using MemoryStream memoryStream = new MemoryStream();
        BitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        encoder.Save(memoryStream);
        memoryStream.Position = 0;
        return new Bitmap(memoryStream);
    }
}
