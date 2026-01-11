using Hardcodet.Wpf.TaskbarNotification;
using LGSTrayCore;
using LGSTrayPrimitives;
using LGSTrayUI.Properties;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LGSTrayUI.IconDrawing;
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

    private static float GetDpiScale()
    {
        float dpiScale = 1.0f;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
        {
            dpiScale = g.DpiX / 96.0f;
        }

        return dpiScale;
    }

    private static Bitmap GetDeviceIcon(LogiDevice device) => device.DeviceType switch
    {
        DeviceType.Keyboard => Keyboard,
        DeviceType.Headset => Headset,
        _ => Mouse,
    };

    private static Bitmap GetBatteryValue(LogiDevice device)
    {
        if (!device.IsVisuallyOnline || device.BatteryPercentage < 0)
        {
            return Missing;
        }

        return device.BatteryPercentage switch
        {
            < 10 => Resources.Indicator_10,
            < 50 => Resources.Indicator_30,
            < 85 => Resources.Indicator_50,
            _ => Resources.Indicator_100
        };
    }

    public static void DrawUnknown(TaskbarIcon taskbarIcon)
    {
        DrawIcon(taskbarIcon, new()
        {
            BatteryPercentage = -1,
            IsOnline = false,
            IsVisuallyOnline = false
        });
    }

    public static void DrawIcon(TaskbarIcon taskbarIcon, LogiDevice device)
    {
        var ImageSize = (int)(96 * GetDpiScale());
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
        if (device.PowerSupplyStatus == PowerSupplyStatus.CHARGING)
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
        // 1. Get the exact system DPI scaling
        // We use a dummy Graphics object to fetch the true system DPI
        float dpiScale = GetDpiScale();

        // 16x16 is standard, but we scale it up by DPI (e.g., 24x24 at 150%)
        int width = (int)(16 * dpiScale);
        int height = (int)(16 * dpiScale);

        using Bitmap bitmap = new(width, height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            // High quality compositing avoids dark halos on transparent backgrounds
            g.CompositingQuality = CompositingQuality.HighQuality;

            // "AntiAliasGridFit" is the magic setting. 
            // It aligns text to the pixel grid (sharpness) but smooths edges.
            // Note: Do NOT use ClearType on transparent backgrounds; it creates ugly black fringes.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;


            bool isCharging = device.PowerSupplyStatus == PowerSupplyStatus.CHARGING;

            // Background Logic
            //if (isCharging)
            //{
            //    // If charging, use Green background
            //    // We use ClearType here because we have a solid background!
            //    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            //    System.Drawing.Color bgColor = CheckTheme.LightTheme
            //    ? System.Drawing.Color.LimeGreen
            //    : System.Drawing.Color.DarkGreen;

            //    using System.Drawing.Brush bgBrush = new SolidBrush(bgColor);
            //    g.FillRectangle(bgBrush, 0, 0, width, height);
            //}


            //bool device100Percent = device.BatteryPercentage == 100;

            // Font style and color
            FontStyle fontStyle = isCharging ? FontStyle.Bold : FontStyle.Regular;
            Color textColor;

            if (CheckTheme.LightTheme)
            {
                textColor = isCharging ? Color.DarkGreen : Color.Black;
            }
            else
            {
                textColor = isCharging ? Color.LightGreen : Color.White;
            }

            // Font size 
            //float emSize = height * (device.BatteryPercentage == 100 ? 0.7f : 0.8f);
            float emSize = height * 0.7f;
            //emSize = height * 0.7f;
            using Font font = new("Segoe UI Variable", emSize, fontStyle, GraphicsUnit.Pixel);
            string text = (!device.IsVisuallyOnline || device.BatteryPercentage < 0) ? "?" : $"{device.BatteryPercentage:f0}";

            // Text Centering
            SizeF textSize = g.MeasureString(text, font);
            float x = (width - textSize.Width) / 2;
            float y = (height - textSize.Height) / 2;

            // Fine-tune adjustment            
            y += 1 * dpiScale;

            using Brush textBrush = new SolidBrush(textColor);
            g.DrawString(text, font, textBrush, x, y);
        }

        // Icon
        IntPtr iconHandle = bitmap.GetHicon();
        var oldIcon = taskbarIcon.Icon;

        using (Icon tempIcon = Icon.FromHandle(iconHandle))
        {
            taskbarIcon.Icon = (Icon)tempIcon.Clone();
        }

        // Cleanup
        DestroyIcon(iconHandle);
        oldIcon?.Dispose();
    }

}
