using System.Windows;
using System.Windows.Controls;

namespace LGSTrayUI.Helpers;

/// <summary>
/// Helper class for creating ContextMenu instances for TaskbarIcons.
/// This ensures each TaskbarIcon has its own ContextMenu instance to prevent
/// stuck menu states when icons are dynamically created/disposed.
/// </summary>
public static class ContextMenuHelper
{
    /// <summary>
    /// Creates a new ContextMenu instance from the SysTrayMenu resource
    /// and sets the specified DataContext.
    /// </summary>
    /// <param name="dataContext">The DataContext to set on the ContextMenu (typically NotifyIconViewModel)</param>
    /// <returns>A new ContextMenu instance with proper DataContext binding</returns>
    public static ContextMenu CreateSysTrayMenu(object? dataContext = null)
    {
        // Load the ContextMenu template from resources
        // Note: Since we removed x:Shared="True", each call creates a new instance
        var menu = (ContextMenu)Application.Current.FindResource("SysTrayMenu");

        // Set the DataContext if provided
        if (dataContext != null)
        {
            menu.DataContext = dataContext;
        }

        return menu;
    }
}
