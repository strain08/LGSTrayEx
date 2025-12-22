using LGSTrayCore;
using LGSTrayUI.Properties;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using System.Runtime.InteropServices;
using LGSTrayPrimitives;
using Microsoft.Win32;

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

    private static int ImageSize;

    static BatteryIconDrawing()
    {
        int dpi;

        try
        {
            var reg = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ThemeManager", false);
            var _dpi = (string?)reg?.GetValue("LastLoadedDPI");
            if (!int.TryParse(_dpi, out dpi))
            {
                dpi = 96;
            }
        }
        catch { dpi = 96; }
        var scale = dpi / 96f;

        ImageSize = (int)(32 * scale);
    }

    private static Bitmap GetDeviceIcon(LogiDevice device) => device.DeviceType switch
    {
        DeviceType.Keyboard => Keyboard,
        DeviceType.Headset => Headset,
        _ => Mouse,
    };

    private static Color GetDeviceColor(LogiDevice device) => device.DeviceType switch
    {
        _ => CheckTheme.LightTheme ? Color.FromArgb(0x11, 0x11, 0x11) : Color.FromArgb(0xEE, 0xEE, 0xEE)

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
            int overlaySize = (int)(ImageSize* 1.50 );

            // Position at bottom-center corner with small margin                
            int marginLeft = - (int)(ImageSize * 0.30);
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
        using Bitmap b = new(ImageSize, ImageSize);
        using Graphics g = Graphics.FromImage(b);

        // Fill with green background if charging
        if (device.PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING)
        {
            using (SolidBrush greenBrush = new SolidBrush(Color.FromArgb(255, 76, 175, 80))) // Material Green 500
            {
                g.FillRectangle(greenBrush, 0, 0, ImageSize, ImageSize);
            }
        }

        string displayString = (device.BatteryPercentage < 0) ? "?" : $"{device.BatteryPercentage:f0}";
        g.DrawString(
            displayString,
            new Font("Segoe UI", (int)(0.8 * ImageSize), GraphicsUnit.Pixel),
            new SolidBrush(GetDeviceColor(device)),
            ImageSize / 2, ImageSize / 2,
            new(StringFormatFlags.FitBlackBox, 0)
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Center,
            }
        );
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        IntPtr iconHandle = b.GetHicon();
        Icon tempManagedRes = Icon.FromHandle(iconHandle);
        taskbarIcon.Icon = (Icon)tempManagedRes.Clone();
        tempManagedRes.Dispose();
        DestroyIcon(iconHandle);
    }
}
