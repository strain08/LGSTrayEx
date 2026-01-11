using LGSTrayPrimitives;
using System.Windows;
using System.Windows.Controls;

namespace LGSTrayUI;

/// <summary>
/// Interaction logic for NotifyIconResources.xaml
/// </summary>
public partial class NotifyIconResources : ResourceDictionary
{
    public NotifyIconResources()
    {
        InitializeComponent();
    }

    private void SysTrayMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            // Force focus to the menu when it opens
            // This helps ensure proper focus tracking for close detection
            menu.Focus();
            DiagnosticLogger.Log($"ContextMenu OPENED - IsOpen: {menu.IsOpen}");
        }
    }

    private void SysTrayMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            // Ensure menu is properly reset when closed
            // This prevents stuck menu states
            menu.IsOpen = false;
            DiagnosticLogger.Log($"ContextMenu CLOSED - IsOpen: {menu.IsOpen}");
        }
    }
}
