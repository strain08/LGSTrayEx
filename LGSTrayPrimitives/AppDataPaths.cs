namespace LGSTrayPrimitives;

/// <summary>
/// Resolves per-user application data paths. Prefers <c>%LOCALAPPDATA%\LGSTray</c>
/// falling back to the application directory if that location cannot be resolved or created.
/// </summary>
public static class AppDataPaths
{
    /// <summary>
    /// Directory for per-user data (diagnostic log, user settings). Created on first access.
    /// </summary>
    public static string LocalAppDataDir { get; } = ResolveLocalAppDataDir();

    private static string ResolveLocalAppDataDir()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string dir = Path.Combine(localAppData, "LGSTray");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        catch (Exception)
        {
            // Fall back to the application directory below if %LOCALAPPDATA% is unavailable
            // or cannot be created.
        }

        return AppContext.BaseDirectory;
    }
}
