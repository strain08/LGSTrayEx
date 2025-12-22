using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;

namespace LGSTrayUI;
/// <summary>
/// Represents the application's main system tray icon, providing access to context menu actions and status display.
/// </summary>
/// <remarks>This class initializes the system tray icon with a predefined context menu and sets its initial
/// visual state. It is intended to be used as the primary taskbar icon for the application, enabling user interaction
/// through the system tray.</remarks>
public class MainTaskBarIcon : TaskbarIcon
{
    public MainTaskBarIcon() : base()
    {
        ContextMenu = (System.Windows.Controls.ContextMenu)Application.Current.FindResource("SysTrayMenu");
        BatteryIconDrawing.DrawUnknown(this);
    }
}
